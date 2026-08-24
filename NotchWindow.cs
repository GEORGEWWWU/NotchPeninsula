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
        private readonly Win32.WndProc _wndProcDelegate;

        // 动画引擎核心状态
        private bool _lastActiveState = false;
        private bool _isAnimating = false;
        private float _currentWidth = Renderer.STANDBY_WIDTH;
        private float _startWidth = Renderer.STANDBY_WIDTH;
        private float _targetWidth = Renderer.STANDBY_WIDTH;
        private DateTime _animStartTime;

        public NotchWindow()
        {
            _media = new MediaController();
            _wndProcDelegate = WndProc;

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                hInstance = Process.GetCurrentProcess().Handle,
                lpszClassName = "NotchPeninsulaClass",
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };

            if (Win32.RegisterClass(ref wc) == 0)
                throw new Exception($"注册窗口类失败！错误码: {Marshal.GetLastWin32Error()}");

            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int x = (screenWidth - Renderer.WINDOW_WIDTH) / 2;
            int y = 0;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED,
                "NotchPeninsulaClass", "Notch",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                x, y, Renderer.WINDOW_WIDTH, Renderer.HEIGHT,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
                throw new Exception($"创建窗口失败！错误码: {Marshal.GetLastWin32Error()}");

            // 将定时器提速至 16ms (~60FPS)，保障 Q弹 动画的丝滑度
            _renderTimer = new Timer(16);
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
            // 1. 监测状态变更，触发动画
            bool currentActive = _media.IsActive;
            if (currentActive != _lastActiveState)
            {
                _lastActiveState = currentActive;
                _startWidth = _currentWidth;
                _targetWidth = currentActive ? Renderer.MEDIA_WIDTH : Renderer.STANDBY_WIDTH;
                _animStartTime = DateTime.Now;
                _isAnimating = true;
            }

            // 2. 弹簧物理引擎计算每一帧
            if (_isAnimating)
            {
                double elapsed = (DateTime.Now - _animStartTime).TotalSeconds;
                double duration = 0.400; // 动画总时长 400ms

                if (elapsed >= duration)
                {
                    _isAnimating = false;
                    _currentWidth = _targetWidth;
                }
                else
                {
                    // 复刻你提供的 Bouncy 物理常数
                    double freq = 2.4;
                    double decay = 12.0;
                    double spring = 1.0 - Math.Cos(freq * elapsed * 2.0 * Math.PI) * Math.Exp(-decay * elapsed);
                    _currentWidth = (float)(_startWidth + (_targetWidth - _startWidth) * spring);
                }
            }

            // 3. 渲染
            var info = new SKImageInfo(Renderer.WINDOW_WIDTH, Renderer.HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // 将当前计算的宽度传入渲染器
            Renderer.Draw(canvas, _media, _isHovered, _currentWidth);
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
                    biWidth = Renderer.WINDOW_WIDTH,
                    biHeight = -Renderer.HEIGHT,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);

            long bytes = Renderer.WINDOW_WIDTH * Renderer.HEIGHT * 4;
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT { x = 0, y = 0 };

            Win32.GetWindowRect(_hwnd, out var rect);
            ptDst.x = rect.Left;
            ptDst.y = rect.Top;

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
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    _isTrackingMouse = false;
                    _isHovered = false;
                    break;

                case Win32.WM_LBUTTONDOWN:
                    if (_isHovered && _media.IsActive)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);

                        // 基于当前宽度的实时动态命中测试
                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                        int btnPrevX = (int)right - 90;
                        int btnPlayX = (int)right - 60;
                        int btnNextX = (int)right - 30;

                        if (x >= btnPrevX && x < btnPlayX)
                            _media.Previous();
                        else if (x >= btnPlayX && x < btnNextX)
                            _media.TogglePlayPause();
                        else if (x >= btnNextX && x <= right)
                            _media.Next();
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}