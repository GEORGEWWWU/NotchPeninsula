using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // 灵动岛的「亚克力」背板。
    //
    // 为什么不用系统原生亚克力（本机实测结论，别再走回头路）：
    //   · DWM 的 accent / SystemBackdrop 背板按【整块窗口矩形】铺，SetWindowRgn 对它完全无效
    //     （实测：加不加胶囊 region 截屏逐字节一致），而任意形状 + 抗锯齿只有路径裁切给得出。
    //
    // 为什么必须跑在后台线程（实测数据）：
    //   · 从屏幕读一次像素本身就要 **18~23ms**（BitBlt 1:1 19ms / StretchBlt 缩到 1/12 23ms /
    //     COLORONCOLOR 18ms —— 跟抓多大、怎么缩无关，是 DWM 回读的固定开销）。
    //     放在渲染线程上 = 每秒数次 20ms 级别的硬停顿，帧率必崩。
    //   · 所以：渲染线程只写「要抓哪块」，抓取/缩放/模糊全在 NPS-Backdrop 线程做，
    //     产出的小图通过 Interlocked 交换交给渲染线程，**由渲染线程负责释放**（唯一的消费者）。
    //
    // 内存口径：GDI 直接 StretchBlt 成 1/12 小图（约 200x37），绝不把 4MB 的整屏原图搬进托管堆；
    //          每次只新产一张 ~30KB 的 SKImage，旧的那张在渲染线程手里、由它释放。
    internal static class IslandBackdrop
    {
        private const int RefreshMs = 250;      // 背板刷新节拍（屏幕读取很贵，别太频繁）
        public const float Downscale = 12f;     // 缩略倍数：越大越糊（实测 k=8→std31 / k=16→std6.6）

        // ⚠️ 这里的平滑**绝不能用 SKImageFilter.CreateBlur**：Skia 会把「每次过滤的产物」按源图的 uniqueID
        // 缓存起来，而背板每 250ms 就产出一张**新图** → 缓存只增不减。实测（纯管道 200s）：
        // 带滤镜 +13.9MB/分（私有内存 50.7→92.3MB），去掉滤镜后 0 增长。这就是「内存暴增」的真凶。
        // 改用「降到 1/2 再升回」的双线性等效模糊：无滤镜、无缓存、开销极小（GDI 那一步 1/12 已经做过盒式平均）。
        private static readonly SKPaint _resample = new()
        { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

        // ---- 渲染线程写入 ----
        private static volatile int _tx, _ty, _tw, _th;
        private static IntPtr _hwnd;
        private static volatile bool _dirty;
        private static volatile bool _expire;

        // ---- 后台线程独占 ----
        private static Thread? _thread;
        private static volatile bool _run;
        private static readonly AutoResetEvent _work = new(false);
        private static IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
        private static int _capW, _capH;
        private static long _lastMs;
        private static bool _disabled;
        private static int _loggedX = int.MinValue, _loggedY = int.MinValue;

        // ---- 交给渲染线程的成品（交换后由渲染线程释放）----
        private static SKImage? _pending;

        public static void Expire() { _expire = true; _dirty = true; _work.Set(); }

        // 渲染线程：告诉后台线程「要抓这块屏幕矩形」。矩形没变就不会重新抓。
        public static void SetTarget(int x, int y, int w, int h, IntPtr hwnd)
        {
            if (x == _tx && y == _ty && w == _tw && h == _th) return;
            _tx = x; _ty = y; _tw = w; _th = h; _hwnd = hwnd;
            _dirty = true;
            _work.Set();
        }

        // 渲染线程：取走最新背板；返回 null 表示没有新图（继续用旧的）
        public static SKImage? TakePending()
        {
            EnsureThread();
            return Interlocked.Exchange(ref _pending, null);
        }

        public static void Stop()
        {
            _run = false;
            _work.Set();
            _thread = null;
            var p = Interlocked.Exchange(ref _pending, null);
            p?.Dispose();
        }

        private static void EnsureThread()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(ThreadLoop)
            {
                IsBackground = true,
                Name = "NPS-Backdrop",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
        }

        private static void ThreadLoop()
        {
            while (_run)
            {
                _work.WaitOne(RefreshMs);
                if (!_run) break;

                if (_expire)
                {
                    _expire = false;
                    FreeBuffer();
                    var old = Interlocked.Exchange(ref _pending, null);
                    old?.Dispose();
                    _lastMs = 0;
                }

                if (_disabled || _tw < 16 || _th < 16) continue;

                long now = Environment.TickCount64;
                if (!_dirty && now - _lastMs < RefreshMs) continue;
                _dirty = false;

                try
                {
                    if (Capture()) _lastMs = now;
                    else _lastMs = now;    // 失败也别每 250ms 重试同一帧
                }
                catch (Exception ex)
                {
                    Error("[IslandBackdrop] 抓取背板失败，亚克力已停用", ex);
                    _disabled = true;
                    FreeBuffer();
                }
            }
        }

        private static bool Capture()
        {
            int w = _tw, h = _th, x = _tx, y = _ty;
            IntPtr hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return false;

            int sw = Math.Max(1, (int)(w / Downscale));
            int sh = Math.Max(1, (int)(h / Downscale));
            EnsureBuffer(sw, sh);
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
                // GDI 一步缩到 1/12，不经过全尺寸原图
                Win32.SetStretchBltMode(_memDc, Win32.HALFTONE);
                if (!Win32.StretchBlt(_memDc, 0, 0, sw, sh, sdc, x, y, w, h, Win32.SRCCOPY)) return false;
            }
            finally
            {
                if (excluded) Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_NONE);
                Win32.ReleaseDC(IntPtr.Zero, sdc);
            }

            // ⚠️ 必须把 DIB 的 alpha 字节全部置 255。
            // StretchBlt/BitBlt 只写 BGR、**不写 alpha**（留下 0），而 SKImage.FromPixels 会把 a=0 当「全透明」——
            // 贴上去等于什么都没贴，亚克力整体消失（实测：不置 alpha → 画进画布后整块变透明）。
            for (int i = 3, n = sw * sh * 4; i < n; i += 4) Marshal.WriteByte(_pBits, i, 255);

            var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var src = SKImage.FromPixels(info, _pBits, sw * 4);
            if (src == null) return false;

            // 无滤镜等效模糊：先降到 1/2（双线性 = 盒式平均），再升回原尺寸
            int hw = Math.Max(1, sw / 2), hh = Math.Max(1, sh / 2);
            using var half = SKSurface.Create(new SKImageInfo(hw, hh, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (half == null) return false;
            half.Canvas.DrawImage(src, new SKRect(0, 0, hw, hh), _resample);
            half.Canvas.Flush();
            using var halfImg = half.Snapshot();
            if (halfImg == null) return false;

            using var smooth = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (smooth == null) return false;
            smooth.Canvas.DrawImage(halfImg, new SKRect(0, 0, sw, sh), _resample);
            smooth.Canvas.Flush();
            var img = smooth.Snapshot();
            if (img == null) return false;

            // 旧的那张还没被渲染线程取走 → 直接丢掉它（我们自己产的，安全）
            var stale = Interlocked.Exchange(ref _pending, img);
            stale?.Dispose();

            if (x != _loggedX || y != _loggedY)
            {
                _loggedX = x; _loggedY = y;
                // 抓取矩形一变就报一行：把它和「背板中央平均 RGB」一起看 —— 矩形跑到屏幕外会得到全黑。
                // 直接从 DIB 取样（BGRA），不再建 SKPixmap。
                int cw = Math.Max(1, sw / 3), chh = Math.Max(1, sh / 3);
                long ar = 0, ag = 0, ab = 0; int cnt = 0;
                for (int j = sh / 3; j < sh / 3 + chh && j < sh; j++)
                    for (int i = sw / 3; i < sw / 3 + cw && i < sw; i++)
                    {
                        int p = (j * sw + i) * 4;
                        ab += Marshal.ReadByte(_pBits, p);
                        ag += Marshal.ReadByte(_pBits, p + 1);
                        ar += Marshal.ReadByte(_pBits, p + 2);
                        cnt++;
                    }
                Info($"[亚克力] 抓屏 ok：屏幕矩形=({x},{y},{w},{h})  缩略图={sw}x{sh}"
                    + $"  背板中央平均 RGB=({(cnt == 0 ? -1 : ar / cnt)},{(cnt == 0 ? -1 : ag / cnt)},{(cnt == 0 ? -1 : ab / cnt)})");
            }
            return true;
        }

        private static void EnsureBuffer(int sw, int sh)
        {
            if (_memDc != IntPtr.Zero && _capW == sw && _capH == sh) return;
            FreeBuffer();

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            _memDc = Win32.CreateCompatibleDC(sdc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = sw,
                    biHeight = -sh,      // 负数 = 从上到下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            _hBitmap = Win32.CreateDIBSection(sdc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
            Win32.ReleaseDC(IntPtr.Zero, sdc);

            _capW = sw; _capH = sh;
        }

        private static void FreeBuffer()
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
            _memDc = _hBitmap = _oldBitmap = _pBits = IntPtr.Zero;
            _capW = _capH = 0;
        }
    }
}
