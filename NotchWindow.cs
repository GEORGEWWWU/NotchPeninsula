using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Microsoft.Win32;
using Timer = System.Timers.Timer;
using static NotchPeninsula.Logger;
using System.Windows.Threading;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public class WindowClickEventArgs : EventArgs
    {
        
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsLeftButton { get; set; } = true;
        public string? HitTarget { get; set; }
    }

    public class NotchWindow
    {
        public bool clicked_info =true;
        public bool isToastActive;
        public event EventHandler<WindowClickEventArgs>? WindowClicked;

        public static bool IsToastEnabled = true;
        public static bool IsTopmostEnabled = true; // 默认开启置顶
        public static IntPtr InstanceHandle { get; private set; } // 暴露给设置面板调用的句柄
        float _currentVolume = 0f;
        private readonly IntPtr _hwnd;
        private readonly MediaController _media;
        public static readonly PluginHost PluginHostInstance = new();
        private readonly List<IWidget> _widgetRow = new();
        private string _cachedWidgetOrder = "";
        private int _cachedWidgetCount = -1;
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
        private ToastData? _currentToast = new ToastData();
        public ToastData? CurrentToast => _currentToast;
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
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // Toast 监听器
        // Y轴动画引擎状态
        private float _currentY = 0f;
        private float _targetY = 0f;
        private float _startY = 0f;
        private bool _isYAnimating = false;
        private DateTime _yAnimStartTime;
        private bool _isManuallyExpanded = false; // 用户是否点击了尾巴展开
        public static bool _isPassthroughAwake = false; // 本体是否已被唤醒并锁定交互
        // 用于跟踪内容状态，实现 0.3s 叠化过渡
        private int _lastDisplayState = -1;
        private DateTime _stateChangeTime;
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
        private static int _cachedMonitorIndex = -1;
        private static int _cachedMonitorX = 0;
        private static int _cachedMonitorY = 0;
        private static int _cachedMonitorWidth = 1920;

        private void UpdateMonitorBounds()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Renderer.TargetMonitorIndex < screens.Length ? Renderer.TargetMonitorIndex : 0;
            _cachedMonitorX = screens[idx].Bounds.X;
            _cachedMonitorY = screens[idx].Bounds.Y;
            _cachedMonitorWidth = screens[idx].Bounds.Width;
            _cachedMonitorIndex = Renderer.TargetMonitorIndex;
        }

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
                hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
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

            UpdateMonitorBounds();
            int x = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            int y = _cachedMonitorY;

            // 动态判定是否追加置顶属性
            int exStyle = Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED;
            if (IsTopmostEnabled) exStyle |= Win32.WS_EX_TOPMOST;

            _hwnd = Win32.CreateWindowEx(
                exStyle,
                "NotchPeninsulaClass", "Notch",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                x, y, _scaledWidth, _scaledHeight, // 传入缩放后的尺寸
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero
            );

            InitRenderBuffer();

            if (_hwnd == IntPtr.Zero)
                throw new Exception($"创建窗口失败！错误码: {Marshal.GetLastWin32Error()}");
            else Info($"窗口创建成功，句柄: {_hwnd}");
            InstanceHandle = _hwnd;
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
            aud.Elapsed += (s, e) => {
                float vol = audio.GetSystemVolume(); // 只读取一次，减少底层通信开销
                if (_currentVolume != vol)
                {
                    _currentVolume = vol;
                    audioVolumeChanged();
                }
            };
            aud.Start();

            // 订阅插件提醒（复用现有 Toast 展示流）
            PluginHostInstance.ReminderPosted += OnToastDetected;

            // 加载 plugins 目录下的插件 DLL
            try
            {
                var plugins = PluginLoader.LoadAll(PluginHostInstance);
                Info($"[插件] 共加载 {plugins.Count} 个插件");
            }
            catch (Exception ex)
            {
                Error("插件加载失败", ex);
            }
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
            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _pollingTimer.Tick += (_, __) => _ = _listener?.FetchLatestNotificationAsync();
            _pollingTimer.Start();
        }

        private void OnToastDetected(ToastData toast)
        {
            if (toast == null) return;
            clicked_info = false;
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
                    // 在删除 GDI 对象前，必须先把旧的备用位图选回 DC 中解锁，否则内存永远无法释放
                    if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
                    {
                        Win32.SelectObject(_memDc, _oldBitmap);
                    }
                    Win32.DeleteObject(_hBitmap);
                    Win32.DeleteDC(_memDc);
                    InitRenderBuffer(); // 重新向系统申请足够大尺寸的内存
                    _needsBufferResize = false;
                }

                // 判断当前 Toast 是否处于激活期
                isToastActive = _currentToast != null && DateTime.Now < _toastEndTime;
                // 实时穿透与 0% 透明度智能判定
                if (Renderer.PassthroughModeEnabled)
                {
                    float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float topY = 12f * _currentStyleProgress;

                    // 因为开启穿透后系统收不到鼠标消息，必须用 GetCursorPos 底层轮询
                    Win32.GetCursorPos(out var pt);
                    float logX = (pt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    float logY = (pt.y - _cachedMonitorY - _currentY) / _dpiScale;
                    bool isOverNotch = logX >= left && logX <= left + _currentWidth && logY >= topY && logY <= topY + _currentHeight;

                    // 如果处于唤醒状态，但鼠标点击了本体外任意地方，立刻进入睡眠
                    if (_isPassthroughAwake && !isOverNotch && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                        _isPassthroughAwake = false;

                    // 当处于睡眠状态且鼠标悬停时，目标透明度为 0f（0%），系统会自动让其完全物理穿透！
                    float targetAlpha = 1.0f;
                    if (!_isPassthroughAwake && isOverNotch) targetAlpha = 0.0f;

                    Renderer.PassthroughAlpha += (targetAlpha - Renderer.PassthroughAlpha) * 0.18f;

                    // 解决极小浮点数(0.001f)未彻底归零，导致 Windows 底层未将窗口判定为全透明，从而导致穿透卡顿的问题
                    if (Renderer.PassthroughAlpha < 0.01f) Renderer.PassthroughAlpha = 0f;
                    if (Renderer.PassthroughAlpha > 0.99f) Renderer.PassthroughAlpha = 1f;
                }
                else
                {
                    Renderer.PassthroughAlpha = 1.0f;
                    _isPassthroughAwake = false;
                }
                if (!isToastActive && _currentToast != null) {_currentToast = null;clicked_info = true;}; // 超时清理

                // 如果灵动岛已展开，且鼠标不在岛上(!_isHovered)，且按下了左键(0x01)
                if (_isManuallyExpanded && !_isHovered && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                {
                    _isManuallyExpanded = false; // 触发收起
                }

                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出时绝对不允许隐藏
                bool shouldHide = IsAutoHideEnabled && !_media.IsActive && !_isManuallyExpanded && !isToastActive;

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

                // 状态叠化透明度计算 (0.3s 平滑过渡，将媒体展开与折叠拆分为独立状态触发叠化)
                int currentDisplayState = isToastActive ? 3 : (Renderer.ActiveDetailWidget != null ? 4 : 0);
                if (currentDisplayState != _lastDisplayState)
                {
                    _lastDisplayState = currentDisplayState;
                    _stateChangeTime = DateTime.Now;
                }
                float transitionAlpha = (float)Math.Clamp((DateTime.Now - _stateChangeTime).TotalSeconds / 0.3, 0, 1);

                // 决策尺寸 (如果处于媒体模式且展开，直接锁定 320x130)
                float expectedTargetWidth;
                if (isToastActive)
                    expectedTargetWidth = Renderer.GetToastAutoWidth();
                else if (Renderer.ActiveDetailWidget != null)
                    expectedTargetWidth = 320f;
                else if (Renderer.WidgetRow is { Count: > 0 })
                    expectedTargetWidth = Math.Clamp(WidgetLayout.MeasureRowWidth(Renderer.WidgetRow, Renderer.BASE_HEIGHT, 12f) + 32f, 60f, 900f);
                else
                    expectedTargetWidth = Renderer.STANDBY_WIDTH;

                // 自动文本长度自适应逻辑
                // 如果在组合模式下，完全跳过外层的媒体自适应逻辑，避免没勾选却幽灵撑宽
                bool bypassAutoWidth = Renderer.CompositeModeEnabled;
                if (currentActive && !Renderer.IsMediaExpanded && !bypassAutoWidth)
                {
                    float textWidth = (!string.IsNullOrEmpty(_media.CurrentLyric) && MediaController.IsLyricsEnabled)
                        ? Renderer.MeasureCurrentLyricWidth(_media.CurrentLyric)
                        : (string.IsNullOrEmpty(_media.Artist)
                            ? Renderer.MeasureCurrentLyricWidth(_media.Title)
                            : Renderer.MeasureCurrentLyricWidth(_media.Artist) + Renderer.MeasureCurrentLyricWidth(_media.Title) + 15f); // 15f 为 " - " 符号的预估宽度补偿

                    float requiredWidth = textWidth + 115f;
                    if (requiredWidth > expectedTargetWidth) expectedTargetWidth = requiredWidth;
                }
                float expectedTargetHeight = isToastActive ? Renderer.TOAST_HEIGHT : (Renderer.ActiveDetailWidget != null ? 130f : Renderer.BASE_HEIGHT);

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
                    double durationS = 0.450; // 稍微放宽 50ms 时长，保证 Q 弹尾迹完整渲染不被硬切

                    if (elapsedS >= durationS)
                    {
                        _isStyleAnimating = false;
                        _currentStyleProgress = _targetStyleProgress;
                    }
                    else
                    {
                        // 提高振动频率让爆发力更干脆，微微降低阻尼多保留一丝余震，果味更浓
                        double freq = 2.65;
                        double decay = 10.8;
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
                    double duration = 0.450; // 保持与上方形态切换同频

                    if (elapsed >= duration)
                    {
                        _isAnimating = false;
                        _currentWidth = _targetWidth;
                        _currentHeight = _targetHeight;
                    }
                    else
                    {
                        double freq = 2.65;  // 匹配形态切换的弹簧张力
                        double decay = 10.8; // 匹配形态切换的阻尼衰减
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

                _media.UpdateLyrics(); // 更新歌词

                // 传入 currentHeight 和 _currentToast
                // 构建组件行（按 WidgetOrder 顺序，变化时才重建）
                BuildWidgetRowIfChanged();
                Renderer.WidgetRow = _widgetRow;
                Renderer.PluginWidgets = PluginHostInstance.Widgets;

                Renderer.Draw(canvas, _media, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress, transitionAlpha);

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

            if (_cachedMonitorIndex != Renderer.TargetMonitorIndex) UpdateMonitorBounds();
            ptDst.x = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            ptDst.y = _cachedMonitorY + (int)_currentY;

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

        // 读取组件顺序配置（注册表 WidgetOrder，逗号分隔的组件 ID）
        private static string GetWidgetOrder()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula");
                return key?.GetValue("WidgetOrder") as string ?? "";
            }
            catch { return ""; }
        }

        // 按 WidgetOrder 顺序重建组件行（顺序或插件数变化时才重建，避免每帧分配）
        private void BuildWidgetRowIfChanged()
        {
            int count = PluginHostInstance.Widgets.Count;
            string order = GetWidgetOrder();
            if (string.IsNullOrWhiteSpace(order))
                order = "builtin.clock,builtin.hardware,builtin.media"; // 默认顺序
            if (order == _cachedWidgetOrder && count == _cachedWidgetCount && _widgetRow.Count > 0) return;
            _cachedWidgetOrder = order;
            _cachedWidgetCount = count;

            var all = new List<IWidget>();
            foreach (var w in PluginHostInstance.Widgets) all.Add(w);

            _widgetRow.Clear();
            if (!string.IsNullOrWhiteSpace(order))
            {
                foreach (var id in order.Split(','))
                {
                    var w = all.FirstOrDefault(x => x.Id == id.Trim());
                    if (w != null && !_widgetRow.Contains(w)) _widgetRow.Add(w);
                }
            }
            foreach (var w in all)
            {
                if (!_widgetRow.Contains(w)) _widgetRow.Add(w);
            }
        }

        // 移动组件顺序（direction: -1 上移, +1 下移），保存到注册表 WidgetOrder
        public static void MoveWidget(string id, int direction)
        {
            var row = Renderer.WidgetRow;
            if (row == null) return;
            int idx = -1;
            for (int i = 0; i < row.Count; i++) { if (row[i].Id == id) { idx = i; break; } }
            if (idx < 0) return;
            int newIdx = idx + direction;
            if (newIdx < 0 || newIdx >= row.Count) return;

            var list = new List<IWidget>(row);
            (list[idx], list[newIdx]) = (list[newIdx], list[idx]);
            Program.SaveSetting("WidgetOrder", string.Join(",", list.Select(w => w.Id)));
        }

        // 右键命中插件组件时，若有详情页则打开
        private bool TryOpenPluginDetail(int cx, int cy)
        {
            var slots = Renderer.WidgetRowSlots;
            if (slots == null) return false;
            float topY = Renderer.WidgetRowTopY;
            foreach (var slot in slots)
            {
                var rect = slot.Rect;
                var hitRect = new SKRect(rect.Left, rect.Top + topY, rect.Right, rect.Bottom + topY);
                if (hitRect.Contains(cx, cy) && slot.Widget.DetailPage != null)
                {
                    Renderer.ActiveDetailWidget = slot.Widget;
                    return true;
                }
            }
            return false;
        }

        private void RaiseWindowClicked(int x, int y, string? hitTarget = null)
        {
            WindowClicked?.Invoke(this, new WindowClickEventArgs
            {
                X = x,
                Y = y,
                IsLeftButton = true,
                HitTarget = hitTarget
            });
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
                    {
                        if (!_isTrackingMouse)
                        {
                            var tme = new Win32.TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf(typeof(Win32.TRACKMOUSEEVENT)), dwFlags = 2, hwndTrack = hwnd, dwHoverTime = 0 };
                            Win32.TrackMouseEvent(ref tme);
                            _isTrackingMouse = true;
                            _isHovered = true;
                        }

                        // 统一提炼坐标，大括号隔离作用域，彻底告别编译报错
                        int mx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int my = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 1. 最高优先级拦截：精准计算唤醒按钮垂直居中热区，解决没有手型指针的问题
                        if (Renderer.PassthroughModeEnabled && !_isPassthroughAwake)
                        {
                            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                            float wakeBtnY = hitTopY + (_currentHeight - 36f) / 2f;

                            if (mx >= left && mx <= left + 36 && my >= wakeBtnY && my <= wakeBtnY + 36)
                            {
                                _isCursorOverIcon = true;
                                break; // 击中唤醒按钮，直接切小手并短路
                            }
                            else
                            {
                                _isCursorOverIcon = false;
                                break; // 处于睡眠态时，绝对阻断底层媒体控制器的幽灵 Hover
                            }
                        }

                        if (_isHovered && _currentToast != null)
                        {
                            _isCursorOverIcon = true;
                        }
                        else if (_isHovered && _media.IsActive && _currentToast == null)
                        {
                            if (Renderer.IsMediaExpanded)
                            {
                                float btnY = (_currentHeight - 32f) + hitTopY;
                                float center = Renderer.WINDOW_WIDTH / 2f;
                                bool inY = my >= btnY - 12 && my <= btnY + 30;
                                bool hitPrev = mx >= center - 75 && mx <= center - 34;
                                bool hitPlay = mx >= center - 20 && mx <= center + 22;
                                bool hitNext = mx >= center + 32 && mx <= center + 75;
                                Renderer.HoveredExpandedButton = inY ? (hitPrev ? 0 : (hitPlay ? 1 : (hitNext ? 2 : -1))) : -1;
                                _isCursorOverIcon = Renderer.HoveredExpandedButton != -1;
                            }
                            else
                            {
                                if (Renderer.MediaInteractionMode == 1 && !Renderer.CompositeModeEnabled)
                                {
                                    float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                                    float right = left + _currentWidth;
                                    _isCursorOverIcon = (mx >= left && mx <= right && my >= hitTopY && my <= hitTopY + _currentHeight);
                                }
                                else
                                {
                                    float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                                    int btnPrevX = (int)right - 90; int btnPlayX = (int)right - 60; int btnNextX = (int)right - 30;
                                    float btnStartY = (_currentHeight - 18f) / 2f + hitTopY; float btnEndY = btnStartY + 18f;
                                    _isCursorOverIcon = (my >= btnStartY && my <= btnEndY) && ((mx >= btnPrevX + 6 && mx <= btnPrevX + 24) || (mx >= btnPlayX + 6 && mx <= btnPlayX + 24) || (mx >= btnNextX + 6 && mx <= btnNextX + 24));
                                }
                            }
                        }
                        else
                        {
                            _isCursorOverIcon = false;
                        }
                        break;
                    }

                case Win32.WM_MOUSELEAVE:
                    {
                        _isTrackingMouse = false;
                        _isHovered = false;
                        _isCursorOverIcon = false;
                        Renderer.HoveredExpandedButton = -1;
                        Renderer.IsMediaExpanded = false;
                        break;
                    }

                case Win32.WM_LBUTTONDOWN:
                    {
                        int cx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int cy = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 详情页交互：命中则执行动作，未命中则关闭
                        if (Renderer.ActiveDetailWidget?.DetailPage is { } detail)
                        {
                            var drect = Renderer.ActiveDetailRect;
                            float dx = cx - drect.Left;
                            float dy = cy - Renderer.WidgetRowTopY;
                            var hit = detail.HitTest(dx, dy, drect);
                            if (hit.IsHit)
                            {
                                detail.OnAction(hit.Action, dx, dy);
                                return (IntPtr)0;
                            }
                        }
                        Renderer.ActiveDetailWidget = null; // 未命中，关闭详情

                        // 完美对齐渲染中心点，精准拦截唤醒点击
                        if (Renderer.PassthroughModeEnabled && !_isPassthroughAwake)
                        {
                            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                            float wakeBtnY = hitTopY + (_currentHeight - 36f) / 2f;
                            if (cx >= left && cx <= left + 36 && cy >= wakeBtnY && cy <= wakeBtnY + 36)
                            {
                                _isPassthroughAwake = true;
                                return (IntPtr)0;
                            }
                        }

                        RaiseWindowClicked(cx, cy, "main-window");

                        if (IsAutoHideEnabled && !_media.IsActive && _currentY < -5f)
                        {
                            _isManuallyExpanded = true;
                            return (IntPtr)0;
                        }

                        if (_isHovered && _media.IsActive && _currentToast == null)
                        {
                            bool hitButtons = false;
                            if (Renderer.IsMediaExpanded)
                            {
                                float btnY = (_currentHeight - 32f) + hitTopY;
                                float center = Renderer.WINDOW_WIDTH / 2f;
                                if (cy >= btnY - 5 && cy <= btnY + 25)
                                {
                                    if (cx >= center - 65 && cx <= center - 35) { _media.Previous(); hitButtons = true; }
                                    else if (cx >= center - 15 && cx <= center + 15) { _media.TogglePlayPause(); hitButtons = true; }
                                    else if (cx >= center + 35 && cx <= center + 65) { _media.Next(); hitButtons = true; }
                                }
                            }
                            else
                            {
                                if (Renderer.MediaInteractionMode == 0 || Renderer.CompositeModeEnabled)
                                {
                                    float right = (Renderer.WINDOW_WIDTH + _currentWidth) / 2f;
                                    float btnStartY = (_currentHeight - 18f) / 2f + hitTopY;
                                    if (cy >= btnStartY && cy <= btnStartY + 18f)
                                    {
                                        if (cx >= right - 84 && cx <= right - 66) { _media.Previous(); hitButtons = true; }
                                        else if (cx >= right - 54 && cx <= right - 36) { _media.TogglePlayPause(); hitButtons = true; }
                                        else if (cx >= right - 24 && cx <= right - 6) { _media.Next(); hitButtons = true; }
                                    }
                                }
                            }

                            if (!hitButtons && Renderer.MediaInteractionMode == 1 && !Renderer.CompositeModeEnabled)
                            {
                                Renderer.IsMediaExpanded = true;
                            }
                        }
                        break;
                    }

                case Win32.WM_RBUTTONDOWN:
                    if (_isHovered)
                    {
                        int rx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int ry = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        if (TryOpenPluginDetail(rx, ry))
                        {
                            return (IntPtr)0; // 已打开插件详情，短路
                        }
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