using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Microsoft.Win32;
using Timer = System.Timers.Timer;
using static NotchPeninsula.Logger;

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
        private readonly IntPtr _hCursorArrow;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private readonly DateTime _appStartTime = DateTime.Now;
        private readonly AudioAnalyzer _audioAnalyzer;
        private float[] _currentBars = new float[5]; // 用于渲染线程的平滑过渡
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon; // 托盘与自启常量
        private const string AppName = "NotchPeninsula";

        public NotchWindow()
        {
            _media = new MediaController();
            _audioAnalyzer = new AudioAnalyzer();
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
            else Info($"窗口创建成功，句柄: {_hwnd}");
            // 将定时器提速至 16ms (~60FPS)，保障 Q弹 动画的丝滑度
            _renderTimer = new Timer(16);
            _renderTimer.Elapsed += (s, e) => RenderLoop();
            _renderTimer.Start();

            // 🛠️ 托盘图标与右键菜单
            // 1. 先实例化托盘对象，防止闭包捕获到未初始化的变量
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            // 打开设置选项
            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("打开设置");
            settingsItem.Click += (s, e) => ConsoleWindow.Toggle();
            contextMenu.Items.Add(settingsItem);

            // 开机自启选项
            var autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.CheckedChanged += (s, e) => ToggleAutoStart(autoStartItem.Checked);

            // 退出选项
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => {
                // 增加判空，彻底消除警告并保证绝对安全
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                Info("程序退出");
                Environment.Exit(0);
            };

            contextMenu.Items.Add(autoStartItem);
            contextMenu.Items.Add(exitItem);

            // 2. 最后再给托盘对象的各项属性赋值
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName);
            _notifyIcon.Text = "NotchPeninsula";
            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.Visible = true;
        }

        public void Run()
        {
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }

        private static string GetCurrentExePath()
        {
            return Process.GetCurrentProcess().MainModule?.FileName
                ?? Environment.ProcessPath
                ?? string.Empty;
        }

        private static string NormalizeRunValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();

            // 兼容 "C:\...\App.exe" 这种带引号的写法
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Trim();
        }

        // 🛠️ 开机自启注册表逻辑 (CurrentUser 级别，无需管理员权限)
        private void ToggleAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                if (enable)
                {
                    string exePath = GetCurrentExePath();
                    if (string.IsNullOrEmpty(exePath))
                    {
                        Warn("当前 exe 路径为空，无法设置开机自启");
                        return;
                    }

                    // 必须和程序当前运行位置一致
                    string registryValue = $"\"{exePath}\"";
                    key?.SetValue(AppName, registryValue);
                    Info($"已设置开机自启，路径: {exePath}");
                }
                else
                {
                    key?.DeleteValue(AppName, false);
                    Info("已取消开机自启");
                }
            }
            catch (Exception ex)
            {
                Error("修改开机自启失败", ex);
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);

                string? rawValue = key?.GetValue(AppName) as string;
                string exePath = GetCurrentExePath();

                bool enabled =
                    !string.IsNullOrEmpty(exePath) &&
                    string.Equals(
                        NormalizeRunValue(rawValue),
                        exePath,
                        StringComparison.OrdinalIgnoreCase);

                Info($"开机自启状态: {enabled} | 当前路径: {exePath} | 注册表值: {rawValue}");

                // 若存在残留值但不是当前 exe，则清理掉
                if (!enabled && !string.IsNullOrWhiteSpace(rawValue))
                {
                    try
                    {
                        using var writeKey = Registry.CurrentUser.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                        writeKey?.DeleteValue(AppName, false);
                        Info("已清理残留的开机自启注册表值");
                    }
                    catch (Exception ex)
                    {
                        Error("清理残留开机自启值失败", ex);
                    }
                }

                return enabled;
            }
            catch
            {
                Warn("检查开机自启状态失败，默认返回 false");
                return false;
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

            // 渲染前，加入平滑插值逻辑
            var targetBars = _audioAnalyzer.GetBars();
            for (int i = 0; i < 5; i++)
            {
                float target = targetBars[i];
                if (target > _currentBars[i])
                {
                    // 瞬间跃升 (Attack)：响应极速，体现打击感
                    _currentBars[i] += (target - _currentBars[i]) * 0.75f;
                }
                else
                {
                    // 缓慢回落 (Release)：带一点点滞留和残影
                    _currentBars[i] += (target - _currentBars[i]) * 0.12f;
                }
            }

            // 3. 渲染
            var info = new SKImageInfo(Renderer.WINDOW_WIDTH, Renderer.HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            // 将平滑后的柱子数据 _currentBars 传给 Draw 方法
            Renderer.Draw(canvas, _media, _isHovered, _currentWidth, startupProgress, _currentBars);

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
                    if (_isCursorOverIcon)
                    {
                        Win32.SetCursor(_hCursorHand);
                        return (IntPtr)1;
                    }
                    break;

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

                    if (_isHovered && _media.IsActive)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);
                        int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                        int btnPrevX = (int)right - 90;
                        int btnPlayX = (int)right - 60;
                        int btnNextX = (int)right - 30;

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
                    _isCursorOverIcon = false;
                    break;

                case Win32.WM_LBUTTONDOWN:
                    if (_isHovered && _media.IsActive && _isCursorOverIcon)
                    {
                        int x = (short)(lParam.ToInt32() & 0xFFFF);
                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;

                        if (x >= right - 84 && x <= right - 66)
                            _media.Previous();
                        else if (x >= right - 54 && x <= right - 36)
                            _media.TogglePlayPause();
                        else if (x >= right - 24 && x <= right - 6)
                            _media.Next();
                    }
                    break;

                case Win32.WM_RBUTTONDOWN:
                    if (_isHovered)
                    {
                        ConsoleWindow.Toggle();
                    }
                    break;
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}