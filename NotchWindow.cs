using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Forms= System.Windows.Forms;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Timer = System.Timers.Timer;

namespace NotchPeninsula
{
    public class NotchWindow
    {
        private readonly IntPtr _hwnd;
        private const string RunKey = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "NotchPeninsula";
        private readonly MediaController _media;
        private bool _isHovered = false;
        private bool _isTrackingMouse = false;
        private readonly Timer _renderTimer;
        private readonly Win32.WndProc _wndProcDelegate;

        // 动画引擎核心状态
        private bool _lastActiveState = false;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private bool _isAnimating = false;
        private float _currentWidth = Renderer.STANDBY_WIDTH;
        private float _startWidth = Renderer.STANDBY_WIDTH;
        private float _targetWidth = Renderer.STANDBY_WIDTH;
        private DateTime _animStartTime;
        private readonly IntPtr _hCursorArrow;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private readonly DateTime _appStartTime = DateTime.Now;

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

            // 在注册窗口类 (Win32.RegisterClass) 之前加载好指针
            _hCursorArrow = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            _hCursorHand = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_HAND);

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
             InitializeNotifyIcon();
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
         #region 托盘
        private void InitializeNotifyIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            try { _notifyIcon!.Icon = new System.Drawing.Icon(".\\ico.ico"); }
            catch { _notifyIcon!.Icon = System.Drawing.SystemIcons.Application; }
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "NotchPeninsula";

            var menu = new Forms.ContextMenuStrip();
            var auto = new Forms.ToolStripMenuItem("开机自启") { Checked = IsAutoStartEnabled() };
            auto.Click += (_, __) => { ToggleAutoStart(); auto.Checked = IsAutoStartEnabled(); };
            menu.Items.Add(auto);
            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += (_, __) => Environment.Exit(917813);
            menu.Items.Add(exit);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        }

        private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
        {
            
            return;

        }


        #endregion
        #region 自启

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);


private bool IsAutoStartEnabled()
{
    try
    {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) != null;
    }
    catch { return false; }
}

private void ToggleAutoStart()
{
    try
    {
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
if (k.GetValue(AppName) != null)
{
    k.DeleteValue(AppName, false);
    Debug.WriteLine("新信息:🔘 已关闭开机自启");
}
else
{
    k.SetValue(AppName, Environment.ProcessPath ?? "");
    Debug.WriteLine("新信息:🔘 已开启开机自启");
}
        }
    }
    catch (Exception ex) { Debug.WriteLine($"新信息:❌ 自启失败：{ex.Message}"); }
}
#endregion
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

            // 计算启动待机文本的进入动画进度 (0到1)
            double uptime = (DateTime.Now - _appStartTime).TotalSeconds;
            float startupProgress = 1f; // 默认 1，代表动画结束
            if (uptime < 0.6) // 动画总时长 0.6 秒
            {
                double t = uptime / 0.6;
                double invT = 1.0 - t;
                // 使用 Cubic Ease-Out (快进缓停) 公式: 1 - (1-t)^3，全乘法计算性能最好
                startupProgress = (float)(1.0 - (invT * invT * invT));
            }

            // 3. 渲染
            var info = new SKImageInfo(Renderer.WINDOW_WIDTH, Renderer.HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // 将 startupProgress 传递给 Draw 方法
            Renderer.Draw(canvas, _media, _isHovered, _currentWidth, startupProgress);

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
                case Win32.WM_SETCURSOR:
                    // 如果在图标热区上，设置小手并返回 1 拦截默认消息，彻底防止指针闪烁
                    if (_isCursorOverIcon)
                    {
                        Win32.SetCursor(_hCursorHand);
                        return (IntPtr)1;
                    }
                    break; // 不在图标上则交给默认处理，系统会自动恢复为箭头

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

                    // ★ 精准热区判定：提取 X 和 Y 坐标，将命中区收缩至 SVG 周围
                    if (_isHovered && _media.IsActive)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);
                        int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                        int btnPrevX = (int)right - 90;
                        int btnPlayX = (int)right - 60;
                        int btnNextX = (int)right - 30;

                        // 设置一个大约 18x18 的舒适命中区（紧凑包围 SVG 图标）
                        bool overPrev = x >= btnPrevX + 6 && x <= btnPrevX + 24 && y >= 8 && y <= 26;
                        bool overPlay = x >= btnPlayX + 6 && x <= btnPlayX + 24 && y >= 8 && y <= 26;
                        bool overNext = x >= btnNextX + 6 && x <= btnNextX + 24 && y >= 8 && y <= 26;

                        _isCursorOverIcon = overPrev || overPlay || overNext;
                    }
                    else
                    {
                        _isCursorOverIcon = false;
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    _isTrackingMouse = false;
                    _isHovered = false;
                    _isCursorOverIcon = false; // 离开窗口时务必重置状态
                    break;

                case Win32.WM_LBUTTONDOWN:
                    // ★ 统一修改点击逻辑：只有在指针变为小手的热区内才允许触发点击
                    if (_isHovered && _media.IsActive && _isCursorOverIcon)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);
                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;

                        // 使用和上面完全一致的热区坐标计算方式触发事件
                        if (x >= right - 84 && x <= right - 66)
                            _media.Previous();
                        else if (x >= right - 54 && x <= right - 36)
                            _media.TogglePlayPause();
                        else if (x >= right - 24 && x <= right - 6)
                            _media.Next();
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}