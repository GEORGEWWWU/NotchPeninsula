using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Timer = System.Timers.Timer;

namespace NotchPeninsula
{
    public class NotchWindow
    {
        private readonly IntPtr _hwnd;
        private readonly MediaController _media;
        private bool _isHovered = false;
        private bool _isTrackingMouse = false;
        private readonly Timer _renderTimer;

        // 核心修复3：用一个类级别变量死死拿住委托引用，防止被GC干掉！
        private readonly Win32.WndProc _wndProcDelegate;

        public NotchWindow()
        {
            _media = new MediaController();
            _wndProcDelegate = WndProc; // 只要窗口在，委托就在

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                hInstance = Process.GetCurrentProcess().Handle,
                lpszClassName = "NotchPeninsulaClass",

                // 强行指定为标准箭头指针
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };

            // 加入错误检测
            if (Win32.RegisterClass(ref wc) == 0)
            {
                throw new Exception($"注册窗口类失败！错误码: {Marshal.GetLastWin32Error()}");
            }

            // 修复空引用警告，加上 ? 和默认宽度兜底
            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            // 变量名更新为 WINDOW_WIDTH
            int x = (screenWidth - Renderer.WINDOW_WIDTH) / 2;
            int y = 0; // 紧贴顶部

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED,
                "NotchPeninsulaClass", "Notch",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                x, y, Renderer.WINDOW_WIDTH, Renderer.HEIGHT, // 变量名更新
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero
            );

            // 加入错误检测
            if (_hwnd == IntPtr.Zero)
            {
                throw new Exception($"创建窗口失败！错误码: {Marshal.GetLastWin32Error()}");
            }

            _renderTimer = new Timer(33);
            _renderTimer.Elapsed += (s, e) => RenderLoop();
            _renderTimer.Start();
        }

        public void Run()
        {
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }

        private unsafe void RenderLoop()
        {
            // 变量名更新为 WINDOW_WIDTH
            var info = new SKImageInfo(Renderer.WINDOW_WIDTH, Renderer.HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            Renderer.Draw(canvas, _media, _isHovered);
            UpdateWindow(surface.PeekPixels());
        }

        private unsafe void UpdateWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);

            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = Renderer.WINDOW_WIDTH, // 变量名更新
                    biHeight = -Renderer.HEIGHT,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);

            // 变量名更新为 WINDOW_WIDTH
            long bytes = Renderer.WINDOW_WIDTH * Renderer.HEIGHT * 4;
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT { x = 0, y = 0 };

            // 这里现在能完美取到窗口位置了
            Win32.GetWindowRect(_hwnd, out var rect);
            ptDst.x = rect.Left;
            ptDst.y = rect.Top;

            // 变量名更新为 WINDOW_WIDTH
            var size = new Win32.SIZE(Renderer.WINDOW_WIDTH, Renderer.HEIGHT);
            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = Win32.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32.AC_SRC_ALPHA
            };

            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);

            Win32.SelectObject(memDc, hOldBitmap);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    if (!_isTrackingMouse)
                    {
                        var tme = new Win32.TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf(typeof(Win32.TRACKMOUSEEVENT)),
                            dwFlags = 2,
                            hwndTrack = hwnd,
                            dwHoverTime = 0
                        };
                        Win32.TrackMouseEvent(ref tme);
                        _isTrackingMouse = true;
                        _isHovered = true;
                        RenderLoop();
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    _isTrackingMouse = false;
                    _isHovered = false;
                    RenderLoop();
                    break;

                case Win32.WM_LBUTTONDOWN:
                    if (_isHovered && _media.IsActive)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);

                        if (x >= Renderer.BTN_PREV_X && x < Renderer.BTN_PLAY_X)
                            _media.Previous();
                        else if (x >= Renderer.BTN_PLAY_X && x < Renderer.BTN_NEXT_X)
                            _media.TogglePlayPause();
                        // 变量名更新为 WINDOW_WIDTH
                        else if (x >= Renderer.BTN_NEXT_X && x <= Renderer.WINDOW_WIDTH)
                            _media.Next();
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}