using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Microsoft.Win32;
using Timer = System.Timers.Timer;
using static NotchPeninsula.Logger;
using System.Windows.Threading;

namespace NotchPeninsula
{
    public class NotchWindow
    {
        public static bool IsToastEnabled = true;
        float _currentVolume = 0f;
        private readonly IntPtr _hwnd;
        private readonly MediaController _media;
        private bool _isHovered = false;
        private bool _isTrackingMouse = false;
        private readonly Timer _renderTimer;
        private readonly Win32.WndProc _wndProcDelegate;

        // 动画引擎核心状态
        private bool _isAnimating = false;
        private float _currentWidth = Renderer.STANDBY_WIDTH;
        private float _startWidth = Renderer.STANDBY_WIDTH;
        private float _targetWidth = Renderer.STANDBY_WIDTH;
        private float _currentHeight = Renderer.BASE_HEIGHT;
        private float _startHeight = Renderer.BASE_HEIGHT;
        private float _targetHeight = Renderer.BASE_HEIGHT;
        // 形态弹簧动画状态
        private float _currentStyleProgress = Renderer.NotchStyle;
        private float _startStyleProgress = Renderer.NotchStyle;
        private float _targetStyleProgress = Renderer.NotchStyle;
        private bool _isStyleAnimating = false;
        private DateTime _styleAnimStartTime;

        // Toast 状态控制
        private ToastData? _currentToast = null;
        private DateTime _toastEndTime;
        private DateTime _animStartTime;
        private readonly IntPtr _hCursorArrow;
        private readonly SystemSettingsManager? audio;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private ToastNotificationListener? _listener;
        private DispatcherTimer? _pollingTimer;
        private readonly Dispatcher _dispatcher;
        private readonly DateTime _appStartTime = DateTime.Now;
        private readonly AudioAnalyzer _audioAnalyzer;
        private float[] _currentBars = new float[5]; // 用于渲染线程的平滑过渡
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon; // 托盘与自启常量
        private const string AppName = "NotchPeninsula";
        private static System.Windows.Forms.ToolStripMenuItem? _autoStartItem; // 提权为静态，方便全局同步
        private static bool _isSyncingState = false; // 防重入锁，性能消耗几乎为 0
        public static bool IsAutoHideEnabled = false; // 全局自动隐藏开关
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // 新增的 Toast 监听器
        // Y轴动画引擎状态
        private float _currentY = 0f;
        private float _targetY = 0f;
        private float _startY = 0f;
        private bool _isYAnimating = false;
        private DateTime _yAnimStartTime;
        private bool _isManuallyExpanded = false; // 用户是否点击了尾巴展开
        // DPI 缩放相关
        private float _dpiScale = 1f;
        private int _scaledWidth;
        private int _scaledHeight;
        // 持久化零拷贝渲染缓冲
        private IntPtr _memDc;
        private IntPtr _hBitmap;
        private IntPtr _oldBitmap;
        private IntPtr _pBits;
        private SKSurface? _renderSurface;
        // 极速无锁防重入标记
        private int _isRendering = 0;
        private volatile bool _needsBufferResize = false; // 显存重建标记

        public NotchWindow()
        {
            audio = new SystemSettingsManager();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _media = new MediaController();
            _audioAnalyzer = new AudioAnalyzer();
            _wndProcDelegate = WndProc;

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                hInstance = Marshal.GetHINSTANCE(typeof(NotchWindow).Module),
                lpszClassName = "NotchPeninsulaClass",
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };

            // 在注册窗口类 (Win32.RegisterClass) 之前加载好指针
            _hCursorArrow = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);
            _hCursorHand = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_HAND);

            if (Win32.RegisterClass(ref wc) == 0)
                throw new Exception($"注册窗口类失败！错误码: {Marshal.GetLastWin32Error()}");

            _dpiScale = Win32.GetDpiForSystem() / 96f;
            _scaledWidth = (int)(Renderer.WINDOW_WIDTH * _dpiScale);
            _scaledHeight = (int)(Renderer.MAX_WINDOW_HEIGHT * _dpiScale);

            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            // 使用 _scaledWidth 进行真正的物理居中
            int x = (screenWidth - _scaledWidth) / 2;
            int y = 0;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED,
                "NotchPeninsulaClass", "Notch",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                x, y, _scaledWidth, _scaledHeight, // 传入缩放后的尺寸
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero
            );

            InitRenderBuffer();

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
            _autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启");
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = IsAutoStartEnabled();
            // 触发时，告诉核心逻辑“这来自托盘(true)”
            _autoStartItem.CheckedChanged += (s, e) => ToggleAutoStart(_autoStartItem.Checked, true);

            // 添加到菜单时使用 _autoStartItem
            contextMenu.Items.Add(_autoStartItem);

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

            contextMenu.Items.Add(exitItem);

            // 2. 最后再给托盘对象的各项属性赋值
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName);
            _notifyIcon.Text = "NotchPeninsula";
            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.Visible = true;
            _currentVolume = audio.GetSystemVolume();
            Debug($"初始音量读取完成，当前音量：{_currentVolume:F2}");
            _ = InitializeListenerAsync();
            Timer aud = new Timer(500);
            aud.Elapsed += (s, e) => { if(_currentVolume != audio.GetSystemVolume()) {_currentVolume = audio.GetSystemVolume();audioVolumeChanged();} };
            aud.Start();
        }
        private void audioVolumeChanged() => Debug($"音量改变{_currentVolume:F2}");
        #region 监听
        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            _listener = new ToastNotificationListener();
            var (ok, msg) = await _listener.InitializeAsync();
            if (!ok) { Error($"监听失败：{msg}"); return; }
            _listener.OnToastDetected += OnToastDetected;
            Info("通知监听已启动");

            // Start polling only after listener initialization to reduce CPU usage during startup.
            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _pollingTimer.Tick += (_, __) => _ = _listener?.FetchLatestNotificationAsync();
            _pollingTimer.Start();
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(() => OnToastDetected(toast)); return; }

            _currentToast = toast;
            _toastEndTime = DateTime.Now.AddSeconds(4); // 消息展示4秒自动消失
        }

        #endregion

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

        // 🛠️ 开机自启注册表逻辑
        public static void ToggleAutoStart(bool enable, bool sourceIsTray = false)
        {
            // 防重入锁
            if (_isSyncingState) return;
            _isSyncingState = true;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                if (enable)
                {
                    string exePath = GetCurrentExePath();
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key?.SetValue(AppName, $"\"{exePath}\"");
                        Info($"已设置开机自启，路径: {exePath}");
                    }
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

            // 极速双向同步逻辑
            if (!sourceIsTray && _autoStartItem != null)
            {
                _autoStartItem.Checked = enable;
            }
            else if (sourceIsTray)
            {
                ConsoleWindow.UpdateAutoStartState(enable);
            }

            _isSyncingState = false; // 解锁
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                string? rawValue = key?.GetValue(AppName) as string;
                string exePath = GetCurrentExePath();
                bool enabled = !string.IsNullOrEmpty(exePath) && string.Equals(NormalizeRunValue(rawValue), exePath, StringComparison.OrdinalIgnoreCase);

                if (!enabled && !string.IsNullOrWhiteSpace(rawValue))
                {
                    try
                    {
                        using var writeKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                        writeKey?.DeleteValue(AppName, false);
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
                return false;
            }
        }

        private unsafe void RenderLoop()
        {
            if (System.Threading.Interlocked.Exchange(ref _isRendering, 1) == 1) return;

            try
            {
                // 实时追踪目标尺寸，动态安全重建底层显存画布
                float currentTargetDpi = (Win32.GetDpiForSystem() / 96f) * Renderer.GLOBAL_DPI;
                int targetScaledWidth = (int)(Renderer.WINDOW_WIDTH * currentTargetDpi);
                int targetScaledHeight = (int)(Renderer.MAX_WINDOW_HEIGHT * currentTargetDpi);

                // 不但要判断 DPI 变化，还要检测目标物理宽高是否发生改变
                if (Math.Abs(_dpiScale - currentTargetDpi) > 0.01f || _scaledWidth != targetScaledWidth || _scaledHeight != targetScaledHeight || _needsBufferResize)
                {
                    _dpiScale = currentTargetDpi;
                    _scaledWidth = targetScaledWidth;
                    _scaledHeight = targetScaledHeight;

                    _renderSurface?.Dispose();
                    Win32.DeleteObject(_hBitmap);
                    Win32.DeleteDC(_memDc);
                    InitRenderBuffer(); // 重新向系统申请足够大尺寸的内存
                    _needsBufferResize = false;
                }

                // 判断当前 Toast 是否处于激活期
                bool isToastActive = _currentToast != null && DateTime.Now < _toastEndTime;
                if (!isToastActive && _currentToast != null) _currentToast = null; // 超时清理

                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出时绝对不允许隐藏
                bool shouldHide = IsAutoHideEnabled && !_media.IsActive && !_isManuallyExpanded && !isToastActive;
                if (_isHovered) shouldHide = false;

                // Y 轴的位移量基于 MAX_WINDOW_HEIGHT 计算
                // Y 轴的隐藏位移量必须加上灵动岛专属的下沉高度，否则藏不进屏幕
                float currentTopY = 12f * _currentStyleProgress;
                float expectedTargetY = shouldHide ? -((Renderer.BASE_HEIGHT + currentTopY - 4) * _dpiScale) : 0f;

                if (Math.Abs(expectedTargetY - _targetY) > 0.1f)
            {
                _startY = _currentY;
                _targetY = expectedTargetY;
                _yAnimStartTime = DateTime.Now;
                _isYAnimating = true;
            }

            if (_isYAnimating)
            {
                double elapsedY = (DateTime.Now - _yAnimStartTime).TotalSeconds;
                double durationY = 0.35; // 350ms 缓入缓出
                if (elapsedY >= durationY)
                {
                    _isYAnimating = false;
                    _currentY = _targetY;
                }
                else
                {
                    double t = elapsedY / durationY;
                    double ease;
                    if (t < 0.5)
                    {
                        ease = 4.0 * t * t * t;
                    }
                    else
                    {
                        double f = -2.0 * t + 2.0;
                        ease = 1.0 - (f * f * f) * 0.5;
                    }
                    _currentY = (float)(_startY + (_targetY - _startY) * ease);
                }
            }

                // ========================================================
                // 二维 (X轴宽度与Y轴高度) 弹簧动画逻辑
                // ========================================================
                bool currentActive = _media.IsActive;

                // 决策尺寸 (分别引用专属宽度和高度)
                float expectedTargetWidth = isToastActive ? Renderer.TOAST_WIDTH : (currentActive ? Renderer.MEDIA_WIDTH : Renderer.STANDBY_WIDTH);
                float expectedTargetHeight = isToastActive ? Renderer.TOAST_HEIGHT : (currentActive ? Renderer.MEDIA_HEIGHT : Renderer.BASE_HEIGHT);

                // 形态(刘海/灵动岛) 弹簧物理插值引擎
                float expectedStyleTarget = Renderer.NotchStyle;
                if (Math.Abs(expectedStyleTarget - _targetStyleProgress) > 0.001f)
                {
                    _startStyleProgress = _currentStyleProgress;
                    _targetStyleProgress = expectedStyleTarget;
                    _styleAnimStartTime = DateTime.Now;
                    _isStyleAnimating = true;
                }

                if (_isStyleAnimating)
                {
                    double elapsedS = (DateTime.Now - _styleAnimStartTime).TotalSeconds;
                    double durationS = 0.400; // 与你的长宽缩放保持同频 400ms

                    if (elapsedS >= durationS)
                    {
                        _isStyleAnimating = false;
                        _currentStyleProgress = _targetStyleProgress;
                    }
                    else
                    {
                        // 完全复用原有的完美物理尼奎斯特阻尼公式
                        double freq = 2.4;
                        double decay = 12.0;
                        double spring = 1.0 - Math.Cos(freq * elapsedS * 2.0 * Math.PI) * Math.Exp(-decay * elapsedS);
                        _currentStyleProgress = (float)(_startStyleProgress + (_targetStyleProgress - _startStyleProgress) * spring);
                    }
                }

                // 当预期尺寸和当前目标尺寸不同时，立刻重新锚定弹簧起点，不打断原有动量
                if (Math.Abs(expectedTargetWidth - _targetWidth) > 0.1f || Math.Abs(expectedTargetHeight - _targetHeight) > 0.1f)
            {
                _startWidth = _currentWidth;
                _targetWidth = expectedTargetWidth;

                _startHeight = _currentHeight;
                _targetHeight = expectedTargetHeight;

                _animStartTime = DateTime.Now;
                _isAnimating = true;
            }

            if (_isAnimating)
            {
                double elapsed = (DateTime.Now - _animStartTime).TotalSeconds;
                double duration = 0.400; // 动画总时长 400ms

                if (elapsed >= duration)
                {
                    _isAnimating = false;
                    _currentWidth = _targetWidth;
                    _currentHeight = _targetHeight;
                }
                else
                {
                    double freq = 2.4;
                    double decay = 12.0;
                    double spring = 1.0 - Math.Cos(freq * elapsed * 2.0 * Math.PI) * Math.Exp(-decay * elapsed);

                    // X 和 Y 同步套用一个物理弹性引擎，保证视效极度统一协调
                    _currentWidth = (float)(_startWidth + (_targetWidth - _startWidth) * spring);
                    _currentHeight = (float)(_startHeight + (_targetHeight - _startHeight) * spring);
                }
            }

            // ================= 3. 其它效果 (淡入/音频柱) =================
            double uptime = (DateTime.Now - _appStartTime).TotalSeconds;
            float startupProgress = 1f;
            if (uptime < 0.6)
            {
                double t = uptime / 0.6;
                double invT = 1.0 - t;
                startupProgress = (float)(1.0 - (invT * invT * invT));
            }

            var targetBars = _audioAnalyzer.GetBars();
            for (int i = 0; i < 5; i++)
            {
                float target = targetBars[i];
                if (target > _currentBars[i])
                {
                    _currentBars[i] += (target - _currentBars[i]) * 0.75f;
                }
                else
                {
                    _currentBars[i] += (target - _currentBars[i]) * 0.12f;
                }
            }

            // ================= 4. 渲染调用更新 =================
            var canvas = _renderSurface!.Canvas;
            canvas.Clear(SKColors.Transparent); // 清空上一帧的残留

            // 存档矩阵状态，避免缩放无限叠加
            canvas.Save();

                // 让底层 C++ 引擎接管坐标放大
                canvas.Scale(_dpiScale);

                // 传入 currentHeight 和 _currentToast
                Renderer.Draw(canvas, _media, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress);

                // 恢复原始矩阵状态
                canvas.Restore();

                UpdateWindow();
            }
            finally
            {
                // 渲染安全结束，释放标记，允许下一帧进入
                System.Threading.Interlocked.Exchange(ref _isRendering, 0);
            }
        }

        private void UpdateWindow()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT { x = 0, y = 0 };

            // 获取主屏幕实时宽度，减去当前缩放宽度后除以 2，保证刘海永远严格居中
            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            ptDst.x = (screenWidth - _scaledWidth) / 2;
            ptDst.y = (int)_currentY;

            var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = Win32.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32.AC_SRC_ALPHA
            };

            // 直接提交已经画好的 _memDc
            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);

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

                    if (_isHovered && _media.IsActive && _currentToast == null) // 当 Toast 存在时拦截控制按钮点击区域
                    {
                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                        float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                        int btnPrevX = (int)right - 90;
                        int btnPlayX = (int)right - 60;
                        int btnNextX = (int)right - 30;

                        // 媒体控制按钮的悬停交互位移补偿
                        float hitTopY = 12f * _currentStyleProgress;

                        bool overPrev = x >= btnPrevX + 6 && x <= btnPrevX + 24 && y >= 8 + hitTopY && y <= 26 + hitTopY;
                        bool overPlay = x >= btnPlayX + 6 && x <= btnPlayX + 24 && y >= 8 + hitTopY && y <= 26 + hitTopY;
                        bool overNext = x >= btnNextX + 6 && x <= btnNextX + 24 && y >= 8 + hitTopY && y <= 26 + hitTopY;

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
                    _isManuallyExpanded = false;
                    break;

                case Win32.WM_LBUTTONDOWN:
                    if (IsAutoHideEnabled && !_media.IsActive && _currentY < -5f)
                    {
                        _isManuallyExpanded = true;
                        return (IntPtr)0;
                    }

                    if (_isHovered && _media.IsActive && _isCursorOverIcon && _currentToast == null)
                    {
                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        // 新增这一行：从系统底层消息中解析出 Y 轴真实的点击坐标并除以缩放比例
                        int clickY = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                        float hitTopY = 12f * _currentStyleProgress;

                        // 点击时也必须补偿 Y 轴热区位移
                        if (clickY >= 8 + hitTopY && clickY <= 26 + hitTopY)
                        {
                            float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;

                            if (x >= right - 84 && x <= right - 66)
                                _media.Previous();
                            else if (x >= right - 54 && x <= right - 36)
                                _media.TogglePlayPause();
                            else if (x >= right - 24 && x <= right - 6)
                                _media.Next();
                        }
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

        // 零拷贝显存通道
        private void InitRenderBuffer()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            _memDc = Win32.CreateCompatibleDC(screenDc);

            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = _scaledWidth,
                    biHeight = -_scaledHeight, // 负数保证从上到下渲染
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            // 申请一块持久的 Windows 内存
            _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);

            // 将 Skia 直接绑定到这块系统内存上，彻底消灭 Buffer.MemoryCopy
            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _renderSurface = SKSurface.Create(info, _pBits, _scaledWidth * 4);

            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}