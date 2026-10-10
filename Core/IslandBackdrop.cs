using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // 灵动岛的「亚克力」背板。
    //
    // 为什么不用系统原生亚克力（本机实测结论，别再走回头路）：
    //   · DWM 的 accent / SystemBackdrop 背板是按【整块窗口矩形】铺的，SetWindowRgn 对它**完全无效**
    //     （实测：加了胶囊 region 与不加 region，截屏逐字节一致，模糊永远是带直角的整块矩形）。
    //   · 而胶囊形状只有逐像素 alpha / 路径裁切才给得出完美抗锯齿 —— 两者在 Win32 里互斥。
    //     想要「原生模糊 + 任意形状」只能上 DirectComposition（Win2D 高斯模糊 + 几何裁切，重依赖）。
    //
    // 所以这里自己抓：把胶囊正后方的真实桌面像素 BitBlt 下来 → 高斯模糊 → 缓存成 SKImage，
    // 交给 Skia 按胶囊路径裁切。视觉上等价于原生亚克力，且形状完全可控 —— 无边框、无锯齿。
    // 关键点：抓的瞬间把岛体设为 WDA_EXCLUDEFROMCAPTURE，让自己对捕获隐身，
    //         这样拿到的是岛体**背后**的桌面（实测：开之前截到白窗，开之后截到背后的棋盘格）。
    internal static class IslandBackdrop
    {
        private static IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
        private static int _capW, _capH;
        private static SKImage? _blurred;

        private static int _curX, _curY, _curW, _curH;
        private static long _lastCaptureMs;
        private static bool _disabled;
        private static volatile bool _expire;

        private const int RefreshMs = 110;

        // 模糊口径：抓到的原图缩到 1/12 → 小高斯去块感 → 绘制时再线性放大回胶囊大小。
        // 倍数越大越糊。实测（36px 棋盘格，胶囊内 R 通道标准差）：k=2→59 / k=4→52 / k=8→31 / k=16→6.6
        // （原图不缩 = 62）。k=12 大致与系统原生亚克力的"糊成一坨"口径相当。
        private const float Downscale = 12f;

        private static readonly SKPaint _smoothPaint = new()
        { IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(1.5f, 1.5f) };

        public static SKImage? Image => _blurred;

        // 设置面板切材质 / 换主题时调用：只置标记，真正释放留给渲染线程（避免跨线程析构）
        public static void Expire() => _expire = true;

        public static void Reset()
        {
            _blurred?.Dispose();
            _blurred = null;
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
            _memDc = _hBitmap = _oldBitmap = _pBits = IntPtr.Zero;
            _capW = _capH = 0;
            _curW = _curH = 0;
            _lastCaptureMs = 0;
        }

        // 渲染线程每帧调用。几何没变、也没到期 → 一个系统调用都不发。
        public static void Sync(int x, int y, int w, int h, IntPtr hwnd)
        {
            if (_expire) { _expire = false; Reset(); }
            if (_disabled || hwnd == IntPtr.Zero) return;
            if (w < 8 || h < 8) return;

            bool geomChanged = x != _curX || y != _curY || w != _curW || h != _curH;
            long now = Environment.TickCount64;
            if (!geomChanged && now - _lastCaptureMs < RefreshMs) return;

            try
            {
                if (!Capture(x, y, w, h, hwnd)) return;
                _curX = x; _curY = y; _curW = w; _curH = h;
                _lastCaptureMs = now;
            }
            catch (Exception ex)
            {
                Error("[IslandBackdrop] 抓取背板失败，亚克力已停用", ex);
                _disabled = true;
                Reset();
            }
        }

        private static bool Capture(int x, int y, int w, int h, IntPtr hwnd)
        {
            EnsureBuffer(w, h);
            if (_memDc == IntPtr.Zero) return false;

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            if (sdc == IntPtr.Zero) return false;

            bool excluded = false;
            try
            {
                excluded = Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
                if (!excluded)
                {
                    // 不支持就宁可没有亚克力 —— 硬抓会把岛体自己糊进去，画面会变成回声
                    Warn("[IslandBackdrop] 系统不支持 WDA_EXCLUDEFROMCAPTURE，亚克力不可用");
                    _disabled = true;
                    return false;
                }
                if (!Win32.BitBlt(_memDc, 0, 0, w, h, sdc, x, y, Win32.SRCCOPY)) return false;
            }
            finally
            {
                if (excluded) Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_NONE);
                Win32.ReleaseDC(IntPtr.Zero, sdc);
            }

            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var src = SKImage.FromPixels(info, _pBits, w * 4);
            if (src == null) return false;

            int sw = Math.Max(1, (int)(w / Downscale));
            int sh = Math.Max(1, (int)(h / Downscale));

            // 缩到 1/k：小图本身就是"重模糊"的载体，绘制时放大回去即得大范围模糊
            using var small = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (small == null) return false;
            small.Canvas.Clear(SKColors.Black);
            small.Canvas.DrawImage(src, new SKRect(0, 0, sw, sh));
            small.Canvas.Flush();
            using var smallImg = small.Snapshot();
            if (smallImg == null) return false;

            // 再做一次小高斯，抹掉缩略采样留下的硬块感
            using var smooth = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (smooth == null) return false;
            smooth.Canvas.Clear(SKColors.Black);
            smooth.Canvas.DrawImage(smallImg, 0, 0, _smoothPaint);
            smooth.Canvas.Flush();
            var blurred = smooth.Snapshot();
            if (blurred == null) return false;

            _blurred?.Dispose();
            _blurred = blurred;
            return true;
        }

        private static void EnsureBuffer(int w, int h)
        {
            if (_memDc != IntPtr.Zero && _capW == w && _capH == h) return;

            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            _memDc = Win32.CreateCompatibleDC(sdc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = w,
                    biHeight = -h,      // 负数 = 从上到下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            _hBitmap = Win32.CreateDIBSection(sdc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
            Win32.ReleaseDC(IntPtr.Zero, sdc);

            _capW = w; _capH = h;
        }
    }
}
