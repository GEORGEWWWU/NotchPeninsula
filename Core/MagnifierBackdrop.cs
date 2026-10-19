using System;
using System.Runtime.InteropServices;
using System.Threading;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // 放大镜（Magnification API）抓屏：给液态玻璃提供**全分辨率**的真实桌面像素。
    //
    // 线程模型（这里最容易踩坑，写死在下面）：
    //   · 宿主窗口 + 放大镜窗口必须建在 **UI 线程**（消息泵那条），Magnification API 只认这条线。
    //   · MagSetWindowSource 会**同步触发**回调，所以谁调用、回调就在谁那条线上跑 ——
    //     渲染线程绝不直接调，只 PostMessage 给宿主窗口，由 UI 线程的消息里代劳。
    //   · 渲染线程只从 Take() 拿走最新的那块像素（靠版本号判断有没有新图），两者之间一把锁。
    //
    // 可用性自检：放大镜窗口通常要求「可见」，但我们不能真的在屏幕上开一块窗口。
    // 所以分两步探：先放屏幕外 → 回调一次都没来就改放屏幕内 + 整窗 alpha=0（视觉上不存在）
    // → 还不行就彻底停用，IslandBackdrop 自动回退 GDI 抓屏。
    internal static unsafe class MagnifierBackdrop
    {
        private const string HostClassName = "NPS_MagHostClass";

        // 回调委托必须被静态字段持有：否则 GC 一回收，原生侧回调进来直接崩
        private static readonly MagNative.MagImageScalingCallback _callback = OnScaling;

        private static readonly Win32.WndProc _hostProc = HostWndProc;

        private static IntPtr _host, _mag;

        private static bool _inited;

        private static bool _available;      // 自检通过
        private static bool _disabled;       // 明确不可用（或出过异常），别再反复试

        private static int _magW, _magH;

        // ---- 抓取结果（UI 线程写 / 渲染线程读）----
        private static readonly object _lock = new();

        // 双缓冲：回调写 _back，写完整帧后与 _front 交换；渲染线程只读 _front，不会读到"半张新图"
        private static byte[] _front = Array.Empty<byte>();

        private static byte[] _back = Array.Empty<byte>();

        private static int _pixW, _pixH;

        private static int _originX, _originY;

        private static int _version;

        private static int _callbackCount;

        // ---- 渲染线程的请求 ----
        private static int _reqX, _reqY, _reqW, _reqH;

        private static IntPtr[] _exclude = Array.Empty<IntPtr>();

        public static bool IsAvailable => _available;

        public static bool IsDisabled => _disabled;

        /// <summary>UI 线程：建宿主窗口与放大镜窗口，并做一次可用性自检。</summary>
        public static void Initialize(IntPtr island, IntPtr owner)
        {
            if (_inited) return;
            _inited = true;

            try
            {
                if (!MagNative.MagInitialize())
                {
                    Warn("[放大镜] MagInitialize 失败，抓屏回退 GDI");
                    _disabled = true;
                    return;
                }

                IntPtr hInst = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;
                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _hostProc,
                    hInstance = hInst,
                    lpszClassName = HostClassName
                };
                Win32.RegisterClass(ref wc);   // 已注册过返回 0，无所谓

                int vx = MagNative.GetSystemMetrics(MagNative.SM_XVIRTUALSCREEN);
                int vy = MagNative.GetSystemMetrics(MagNative.SM_YVIRTUALSCREEN);

                // 第一步：整窗丢到虚拟屏幕外侧（最干净 —— 屏幕上什么都不多）
                _host = Win32.CreateWindowEx(
                    Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | MagNative.WS_EX_TRANSPARENT,
                    HostClassName, "NPS-MagHost",
                    Win32.WS_POPUP | Win32.WS_VISIBLE,
                    vx - 300, vy - 300, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

                if (_host == IntPtr.Zero)
                {
                    Warn($"[放大镜] 宿主窗口创建失败，抓屏回退 GDI（{Marshal.GetLastWin32Error()}）");
                    MagNative.MagUninitialize();
                    _disabled = true;
                    return;
                }

                _mag = Win32.CreateWindowEx(
                    0, MagNative.MagnifierClass, null,
                    MagNative.WS_CHILD | Win32.WS_VISIBLE | MagNative.MS_SHOWMAGNIFIEDCURSOR,
                    0, 0, 8, 8, _host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_mag == IntPtr.Zero)
                {
                    Warn($"[放大镜] 放大镜窗口创建失败，抓屏回退 GDI（{Marshal.GetLastWin32Error()}）");
                    DestroyWindows();
                    MagNative.MagUninitialize();
                    _disabled = true;
                    return;
                }

                if (!MagNative.MagSetImageScalingCallback(_mag, _callback))
                {
                    Warn($"[放大镜] 注册缩放回调失败，抓屏回退 GDI（{Marshal.GetLastWin32Error()}）");
                    DestroyWindows();
                    MagNative.MagUninitialize();
                    _disabled = true;
                    return;
                }

                SetExclude(island, owner);

                // 同步自检：这里就是 UI 线程，MagSetWindowSource 会当场回调一次
                _available = Probe();

                if (!_available)
                {
                    // 第二步：屏幕外拿不到画面（部分环境的放大镜必须真的在屏幕内），
                    // 挪到虚拟屏左上角并把整窗做成 alpha=0 —— 视觉上等于不存在。
                    _inScreen = true;
                    Win32.SetWindowPos(_host, IntPtr.Zero, vx, vy, 1, 1, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                    MagNative.SetLayeredWindowAttributes(_host, 0, 0, MagNative.LWA_ALPHA);
                    _available = Probe();
                }

                if (_available)
                {
                    Info($"[放大镜] 抓屏就绪：宿主={_host}  放大镜={_mag}"
                        + $"  位置={(_inScreen ? "屏幕内(全透明)" : "屏幕外")}");
                }
                else
                {
                    Warn("[放大镜] 两次自检都没有回调，抓屏回退 GDI");
                    DestroyWindows();
                    MagNative.MagUninitialize();
                    _disabled = true;
                }
            }
            catch (Exception ex)
            {
                Error("[放大镜] 初始化异常，抓屏回退 GDI", ex);
                try { DestroyWindows(); MagNative.MagUninitialize(); } catch { }
                _disabled = true;
            }
        }

        private static bool _inScreen;

        private static bool Probe()
        {
            _callbackCount = 0;
            int vx = MagNative.GetSystemMetrics(MagNative.SM_XVIRTUALSCREEN);
            int vy = MagNative.GetSystemMetrics(MagNative.SM_YVIRTUALSCREEN);
            try
            {
                // 抓虚拟屏左上角一小块：能不能回调、给的数据对不对，一次就能看出来
                CaptureNow(vx, vy, 32, 32);
            }
            catch (Exception ex)
            {
                Error("[放大镜] 自检异常", ex);
                return false;
            }
            return _callbackCount > 0 && _pixW > 0 && _pixH > 0;
        }

        // UI 线程专用
        private static void CaptureNow(int x, int y, int w, int h)
        {
            if (_mag == IntPtr.Zero || w < 1 || h < 1) return;

            // 放大镜窗口尺寸 = 源矩形尺寸 → 缩放比 1:1，回调给的 srcdata 就是全分辨率原始像素
            if (w != _magW || h != _magH)
            {
                Win32.SetWindowPos(_mag, IntPtr.Zero, 0, 0, w, h,
                    Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                _magW = w;
                _magH = h;
            }

            var rect = new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            MagNative.MagSetWindowSource(_mag, rect);   // 同步触发 OnScaling
        }

        private static void SetExclude(IntPtr island, IntPtr owner)
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            if (island != IntPtr.Zero) list.Add(island);
            if (owner != IntPtr.Zero) list.Add(owner);
            if (_host != IntPtr.Zero) list.Add(_host);
            if (_mag != IntPtr.Zero) list.Add(_mag);

            _exclude = list.ToArray();
            if (_exclude.Length == 0 || _mag == IntPtr.Zero) return;

            int bytes = _exclude.Length * IntPtr.Size;
            IntPtr p = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.Copy(_exclude, 0, p, _exclude.Length);
                // 排除自身 → 抓到的就是「岛体背后」的桌面，玻璃不需要再抹掉自己
                MagNative.MagSetWindowFilterList(_mag, MagNative.MW_FILTERMODE.Exclude, _exclude.Length, p);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>渲染线程：请求抓这一块（真正抓取发生在 UI 线程的下一条消息）。</summary>
        public static void Request(int x, int y, int w, int h)
        {
            if (!_available || _host == IntPtr.Zero) return;
            _reqX = x;
            _reqY = y;
            _reqW = w;
            _reqH = h;
            Win32.PostMessage(_host, MagNative.WM_APP_MAG_CAPTURE, IntPtr.Zero, IntPtr.Zero);
        }

        private static IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == MagNative.WM_APP_MAG_CAPTURE && _available)
            {
                try
                {
                    int x = _reqX, y = _reqY, w = _reqW, h = _reqH;
                    if (w >= 16 && h >= 16) CaptureNow(x, y, w, h);
                }
                catch (Exception ex)
                {
                    Error("[放大镜] 抓屏异常，停用并回退 GDI", ex);
                    _available = false;
                    _disabled = true;
                }
                return IntPtr.Zero;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // ---- 回调（UI 线程）：把 srcdata 拷进自己的缓冲区 ----
        private static bool OnScaling(IntPtr hwnd, IntPtr srcdata, MagNative.MAGIMAGEHEADER srcheader,
            IntPtr destdata, MagNative.MAGIMAGEHEADER destheader,
            Win32.RECT unclipped, Win32.RECT clipped, IntPtr dirty)
        {
            try
            {
                if (srcdata == IntPtr.Zero) return false;
                int w = (int)srcheader.width, h = (int)srcheader.height;
                int stride = (int)srcheader.stride;
                if (w <= 0 || h <= 0 || stride <= 0) return false;
                if (w > 8192 || h > 8192) return false;

                int dstStride = w * 4;
                long need = (long)h * dstStride;
                if (need > 64L * 1024 * 1024) return false;

                byte* src = (byte*)srcdata + srcheader.offset;

                lock (_lock)
                {
                    if (_back.Length < need) _back = new byte[(int)need];

                    fixed (byte* dst = _back)
                    {
                        // 逐行拷：源的 stride 可能带对齐填充，不能当成一行连续内存
                        for (int j = 0; j < h; j++)
                        {
                            Buffer.MemoryCopy(src + (long)j * stride, dst + (long)j * dstStride, dstStride, dstStride);
                        }

                        // ⚠️ 与 GDI 那条线同款坑：放大镜只写 BGR，alpha 位是 0，
                        //    SKImage.FromPixels 会把 a=0 当全透明 → 贴上去等于没贴。
                        int n = h * dstStride;
                        for (int i = 3; i < n; i += 4) dst[i] = 255;
                    }

                    // 整帧写完了才交换：渲染线程拿到的永远是完整的一帧
                    (_front, _back) = (_back, _front);
                    _pixW = w;
                    _pixH = h;
                    // 源矩形原点 = 我们请求的那块（unclipped 是放大镜实际取的矩形，与请求一致）
                    _originX = unclipped.Left;
                    _originY = unclipped.Top;
                    _version++;
                    _callbackCount++;
                }
                return true;   // 已自行处理，系统不必再做默认缩放拷贝
            }
            catch (Exception ex)
            {
                Error("[放大镜] 缩放回调异常", ex);
                _available = false;
                _disabled = true;
                return false;
            }
        }

        /// <summary>
        /// 渲染线程：取最新一帧。返回 false 表示还没有（继续用旧的）。
        /// 数据指针只在本次调用期间有效，调用方需要立刻快照。
        /// </summary>
        public static bool Take(out byte[] pixels, out int width, out int height, out int originX, out int originY, out int version)
        {
            pixels = Array.Empty<byte>();
            width = height = originX = originY = version = 0;
            if (!_available) return false;

            lock (_lock)
            {
                if (_pixW <= 0 || _pixH <= 0 || _front.Length == 0) return false;
                pixels = _front;
                width = _pixW;
                height = _pixH;
                originX = _originX;
                originY = _originY;
                version = _version;
                return true;
            }
        }

        public static void Stop()
        {
            _available = false;
            try { DestroyWindows(); MagNative.MagUninitialize(); } catch { }
        }

        private static void DestroyWindows()
        {
            if (_mag != IntPtr.Zero) { Win32.DestroyWindow(_mag); _mag = IntPtr.Zero; }
            if (_host != IntPtr.Zero) { Win32.DestroyWindow(_host); _host = IntPtr.Zero; }
        }
    }
}
