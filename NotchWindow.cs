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
        public static bool IsClipboardEnabled = true; // 📋 剪贴板链接检测开关（交互设置，默认开启）
        public static bool IsTopmostEnabled = true; // 默认开启置顶
        public static IntPtr InstanceHandle { get; private set; } // 暴露给设置面板调用的句柄
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
        private readonly float[] _spectrumBars = new float[5]; // LyricServer 12 频段压缩为 5 柱的复用缓冲（仅 RenderLoop 单线程内写入并当帧消费）
        private bool _wasUsingSoloSpectrum; // 上一帧是否在用 LyricServer 频谱，用于感知独占播放结束
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon; // 托盘与自启常量
        private const string AppName = "NotchPeninsula";
        private static System.Windows.Forms.ToolStripMenuItem? _autoStartItem; // 提权为静态，方便全局同步
        private static bool _isSyncingState = false; // 防重入锁，性能消耗几乎为 0
        public static bool IsAutoHideEnabled = false; // 全局自动隐藏开关
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // Toast 监听器
        // 📋 剪贴板链接监听（事件驱动，仅在复制时读一次剪贴板，稳态零占用）
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private string? _clipboardUrl;         // 当前正在展示的链接
        private string? _pendingClipboardUrl;  // 被更高级别通知挤下后退回队列等待的链接（单槽位复用，零额外内存）
        private DateTime _clipboardEndTime;    // 链接展示截止时间
        public bool isClipboardActive;         // 本帧剪贴板面板是否激活
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
                _audioAnalyzer.Dispose(); // 停掉看门狗并释放捕获/COM 订阅
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
            // 🧩 插件系统：先把插件提醒接入 Toast 流，再初始化运行时自动加载已启用插件
            PluginManager.Instance.Host.ReminderPosted += OnPluginReminder;
            PluginManager.Instance.Initialize();
            _ = InitializeListenerAsync();

            // 📋 订阅剪贴板监听：窗口句柄就绪后注册 WM_CLIPBOARDUPDATE
            _clipboardMonitor.OnUrlDetected += OnClipboardUrlDetected;
            _clipboardMonitor.Attach(_hwnd);
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

        /// <summary>插件通过 IPluginHost.PostReminder 投递的提醒，复用现有 Toast 展示通道。</summary>
        private void OnPluginReminder(ToastData toast)
        {
            if (toast == null) return;
            // 插件提醒来自后台线程，切回 UI 线程更新共享的 Toast 状态
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnPluginReminder(toast)); return; }
            if (!IsToastEnabled) return;

            _currentToast = toast;
            _toastEndTime = DateTime.Now.AddSeconds(4);
            clicked_info = false;
        }

        /// <summary>剪贴板识别到链接：级别低于系统通知、高于媒体控制器，通知展示期间先排队等待。</summary>
        private void OnClipboardUrlDetected(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!IsClipboardEnabled) return; // 开关关闭：直接丢弃，不弹面板
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnClipboardUrlDetected(url)); return; }

            // 通知优先：通知展示中先把链接挂起，等通知结束再显示
            if (isToastActive) { _pendingClipboardUrl = url; return; }

            _clipboardUrl = url;
            _clipboardEndTime = DateTime.Now.AddSeconds(3); // 链接停留 3s
        }

        /// <summary>在默认浏览器打开当前链接，并立即收起面板。</summary>
        private void OpenClipboardUrl()
        {
            string? url = _clipboardUrl;
            if (string.IsNullOrEmpty(url)) return;
            if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;

            _clipboardUrl = null;
            _pendingClipboardUrl = null;
            _clipboardEndTime = default;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Info($"[剪贴板] 已在默认浏览器打开链接: {url}");
            }
            catch (Exception ex) { Error("[剪贴板] 打开链接失败", ex); }
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
                // 🧩 插件详情页状态必须最先同步：WINDOW_WIDTH / MAX_WINDOW_HEIGHT 会随详情页尺寸变化，
                //    而下面重建底层显存缓冲的判断恰好依赖这两个值。
                //    详情页 Measure 抛异常被熔断时，这里顺手把宿主状态收起，岛体恢复原状。
                Renderer.RefreshDetailPageState();
                if (Renderer.ConsumeDetailCloseRequest()) PluginManager.Instance.Host.CloseDetailPage();

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

                // 📋 级别调度（消息队列，零额外分配）：系统通知 > 剪贴板链接 > 媒体控制器
                // 开关关闭时立即收起正在展示的链接并清空排队槽位
                if (!IsClipboardEnabled)
                {
                    _clipboardUrl = null;
                    _pendingClipboardUrl = null;
                    _clipboardEndTime = default;
                }
                else if (isToastActive)
                {
                    // 通知到来：正在展示的链接退回单槽队列，等通知结束后再回来
                    if (_clipboardUrl != null) { _pendingClipboardUrl = _clipboardUrl; _clipboardUrl = null; }
                }
                else if (_pendingClipboardUrl != null)
                {
                    // 通知结束：把排队的链接提上来，并重新计时 3s
                    _clipboardUrl = _pendingClipboardUrl;
                    _pendingClipboardUrl = null;
                    _clipboardEndTime = DateTime.Now.AddSeconds(3);
                }
                // 剪贴板激活期判定 + 超时清理
                isClipboardActive = _clipboardUrl != null && DateTime.Now < _clipboardEndTime;
                if (!isClipboardActive && _clipboardUrl != null) { _clipboardUrl = null; _clipboardEndTime = default; }

                // 🧩 展开态的收起策略（用户 2026-09-19 要求）：
                //    · **鼠标移走不收起** —— 手滑划出岛体不该把面板弄没。原本的三条自动折叠路径已全部停用：
                //        `_isManuallyExpanded && !_isHovered && 左键` / `HasActiveDetailPage && !_isHovered` /
                //        媒体面板的 `WM_MOUSELEAVE → IsMediaExpanded = false`
                //    · **点击屏幕其他地方（岛外）才收起** —— 这是用户主动表达「我看完了」，语义清晰。
                //    实现要点：窗口只有鼠标在它范围内时才收得到鼠标消息，岛外点击根本不会派发 WM_LBUTTONDOWN，
                //    所以这里用 GetCursorPos 底层轮询判断「左键按下 且 光标不在岛体矩形内」
                //    （坐标换算与上面穿透模式那段完全同一套：减去显示器原点、减窗口 Y 偏移、再除 DPI）。
                //    只在「确实有东西展开着」时才轮询，三个状态全 false 时这段直接跳过，稳态零开销。
                if (_isManuallyExpanded || Renderer.IsMediaExpanded || Renderer.HasActiveDetailPage)
                {
                    float expLeft = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float expTopY = 12f * _currentStyleProgress;
                    Win32.GetCursorPos(out var expPt);
                    float expX = (expPt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    float expY = (expPt.y - _cachedMonitorY - _currentY) / _dpiScale;
                    bool isOverIsland = expX >= expLeft && expX <= expLeft + _currentWidth
                                        && expY >= expTopY && expY <= expTopY + _currentHeight;

                    if (!isOverIsland && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                    {
                        _isManuallyExpanded = false;
                        Renderer.IsMediaExpanded = false;
                        if (Renderer.HasActiveDetailPage) PluginManager.Instance.Host.CloseDetailPage();
                    }
                }


                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出时绝对不允许隐藏；插件详情页展开时同样不允许隐藏
                bool shouldHide = IsAutoHideEnabled && !_media.IsActive && !_isManuallyExpanded && !isToastActive
                                  && !isClipboardActive && !Renderer.HasActiveDetailPage;

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
                int currentDisplayState = isToastActive ? 3 : (isClipboardActive ? 4 : (currentActive ? (Renderer.IsMediaExpanded ? 2 : 1) : 0));
                if (currentDisplayState != _lastDisplayState)
                {
                    _lastDisplayState = currentDisplayState;
                    _stateChangeTime = DateTime.Now;
                }
                float transitionAlpha = (float)Math.Clamp((DateTime.Now - _stateChangeTime).TotalSeconds / 0.3, 0, 1);

                // 决策尺寸 (如果处于媒体模式且展开，直接锁定 320x130)
                // 🧩 插件行独立占据岛体最右侧：非组合模式下恒定追加其预留宽度，
                //    因此不论待机显示什么内容、媒体是否开启，插件都会稳定显示在原生内容之后。
                // 🧩 组合模式下插件已并入「内容顺序表」与原生模块混排，宽度由 GetCompositeWidth 一并算出，
                //    因此不再额外追加插件预留宽度；其余模式仍按整行贴在右侧预留。
                // 🧩 插件详情页展开时：岛体尺寸完全由详情页决定（插件通过 MeasureWidth/MeasureHeight 指定），
                //    此时忽略原生内容与插件行的预留宽度，岛体只显示详情页内容。
                //    Toast 优先于详情页（通知到来时先显示通知，通知结束后详情页自动回来）。
                float detailW = 0f, detailH = 0f;
                bool detailOpen = !isToastActive && !isClipboardActive && Renderer.TryGetDetailPageSize(out detailW, out detailH);

                // 🧩 原生内容（不含插件行）本帧需要多宽：媒体激活时，长歌词会自适应把岛体撑宽
                float nativeWidth = currentActive
                    ? (Renderer.IsMediaExpanded ? 320f : Renderer.MEDIA_WIDTH)
                    : Renderer.STANDBY_WIDTH;
                if (currentActive && !Renderer.IsMediaExpanded && !Renderer.CompositeModeEnabled)
                {
                    float textWidth = (!string.IsNullOrEmpty(_media.CurrentLyric) && MediaController.IsLyricsEnabled)
                        ? Renderer.MeasureCurrentLyricWidth(_media.CurrentLyric)
                        : (string.IsNullOrEmpty(_media.Artist)
                            ? Renderer.MeasureCurrentLyricWidth(_media.Title)
                            : Renderer.MeasureCurrentLyricWidth(_media.Artist) + Renderer.MeasureCurrentLyricWidth(_media.Title) + 15f); // 15f 为 " - " 符号的预估宽度补偿

                    // 🎵 译文第二行：文字区宽度要容得下更宽的那一行，否则长译文会被遮罩截掉半句
                    if (Renderer.IsTranslationLineVisible(_media))
                        textWidth = Math.Max(textWidth, Renderer.MeasureLyricTranslationWidth(_media.CurrentLyricTranslation));

                    // 🎵 文本区长度封顶（MEDIA_TEXT_MAX_WIDTH）：长标题 / 长歌词不再把岛体无限撑宽，
                    //    否则右侧的律动频谱与播放按钮会被顶到很偏的位置，插件行也彻底没余量。
                    //    超出部分由渲染侧既有的文字遮罩做渐隐截断，视觉上是自然淡出而不是硬切。
                    nativeWidth = Math.Max(nativeWidth, Math.Min(textWidth, Renderer.MEDIA_TEXT_MAX_WIDTH) + 115f);
                }
                nativeWidth = Math.Min(nativeWidth, Renderer.MAX_ISLAND_WIDTH); // 岛体总长上限，窄屏也不会被撑破

                // 🧩 插件行取舍：按「组件声明的所需宽度能否完整落进剩余空间」判定。
                //    每个组件通过 IWidget.MeasureWidth 声明「完整显示我的内容需要多宽」，
                //    宿主用「岛体总长上限 − 原生内容本帧占用宽度」得出插件行预算，逐个贪心放行：
                //    装得下的组件完整显示，装不下的组件本帧整体不显示 —— 宿主绝不替它压缩或截断，
                //    所以不会出现「文字被省略号砍掉半截」这种显示不全的情况。
                //    原生内容（尤其是开着媒体控制 + 长歌词自适应）一样照常显示，岛体也不会被撑过上限。
                //    例外：媒体控制面板展开（IsMediaExpanded）时插件行整体不显示 —— 那是块独立面板，
                //    插件贴上去只会把面板和岛体一起撑宽，见下面的分支。
                float pluginReserve = 0f;
                if (Renderer.CompositeModeEnabled)
                {
                    // 组合模式：插件已并入「内容顺序表」与原生模块混排。预算必须知道「一整行原生模块」的总宽，
                    // 所以先量原生（不含插件）、定好预算，随后算含插件的总宽时就会按它放行。
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - Renderer.GetCompositeNativeWidth(_media));
                }
                else if (!isToastActive && !isClipboardActive && !detailOpen
                    && !(currentActive && Renderer.IsMediaExpanded))
                {
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - nativeWidth);
                    pluginReserve = Renderer.GetPluginRowReserve();
                }
                else
                {
                    // 通知 / 剪贴板 / 详情页 / 媒体控制面板展开：整块岛体被接管，本帧不给插件行任何宽度。
                    // 媒体面板（右键展开）尤其明显：那是 320×130 的独立面板，再塞一行插件
                    // 会把面板与岛体一起撑宽，所以展开期间插件行整体隐藏（收起后自动恢复）。
                    Renderer.SetPluginRowBudget(0f);
                }

                float expectedTargetWidth;
                float expectedTargetHeight;
                if (detailOpen)
                {
                    expectedTargetWidth = detailW;
                    expectedTargetHeight = detailH;
                }
                else if (isToastActive)
                {
                    expectedTargetWidth = Renderer.GetToastAutoWidth();
                    expectedTargetHeight = Renderer.TOAST_HEIGHT;
                }
                else if (isClipboardActive)
                {
                    // 📋 剪贴板链接面板：沿用媒体控制器同款尺寸（高度一致，宽度按链接长度自适应并封顶）
                    expectedTargetWidth = Renderer.GetClipboardAutoWidth(_clipboardUrl!);
                    expectedTargetHeight = Renderer.MEDIA_HEIGHT;
                }
                else
                {
                    // 组合模式走渲染器里的像素级精确动态宽度计算，拒绝任何多余空白与错位；
                    // 其余模式 = 原生内容宽度 + 插件行预留（插件行放不下时预留已归零）
                    expectedTargetWidth = Renderer.CompositeModeEnabled
                        ? Renderer.GetCompositeWidth(_media)
                        : nativeWidth + pluginReserve;

                    expectedTargetHeight = currentActive ? (Renderer.IsMediaExpanded ? Renderer.GetExpandedHeight(_media) : Renderer.MEDIA_HEIGHT) : Renderer.BASE_HEIGHT;
                }

                // 🧩 把目标宽度交给渲染侧：它据此把「插件行预留」按动画进度等比缩放，
                //    免得岛体还没长到时候，原生内容（媒体文字 / 频谱 / 播放按钮）先被全额预留挤扁。
                Renderer.IslandTargetWidth = expectedTargetWidth;

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

            // 频谱优先取 Just Solo LyricServer 推送（独占音频输出时本地采集拿不到数据），
            // 不可用（未连接 / 服务端不支持 / 已暂停）时回退到原来的 WASAPI 采集
            bool useSoloSpectrum = _media.TryGetSoloSpectrum(out float[] soloBands) && soloBands.Length >= 12;
            if (_wasUsingSoloSpectrum && !useSoloSpectrum)
                _audioAnalyzer.EnsureCaptureAlive(); // LyricServer 频谱刚结束，让它立即复核本地采集
            _wasUsingSoloSpectrum = useSoloSpectrum;

            float[] targetBars = useSoloSpectrum ? MapSoloSpectrum(soloBands) : _audioAnalyzer.GetBars();
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
                Renderer.Draw(canvas, _media, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress, transitionAlpha, isClipboardActive ? _clipboardUrl : null);

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

        // 把 LyricServer 的 12 个频段（低频→高频）按区间取峰值压缩为渲染层的 5 根柱。
        // 返回复用缓冲以避免每帧分配；调用方只有 RenderLoop，且它由 _isRendering 保证串行执行，
        // 写入后当帧立即被消费，不存在跨线程/跨帧共享。
        private float[] MapSoloSpectrum(float[] bands)
        {
            _spectrumBars[0] = Math.Max(bands[0], bands[1]);
            _spectrumBars[1] = Math.Max(bands[2], Math.Max(bands[3], bands[4]));
            _spectrumBars[2] = Math.Max(bands[5], bands[6]);
            _spectrumBars[3] = Math.Max(bands[7], Math.Max(bands[8], bands[9]));
            _spectrumBars[4] = Math.Max(bands[10], bands[11]);
            return _spectrumBars;
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
                // 📋 剪贴板内容变化（事件驱动，仅在复制/剪切导致剪贴板内容变化时触发一次读取；开关关闭直接忽略）
                case Win32.WM_CLIPBOARDUPDATE:
                    if (IsClipboardEnabled) _clipboardMonitor.HandleClipboardUpdate();
                    return (IntPtr)0;

                case Win32.WM_DESTROY:
                    // 📋 窗口销毁前反注册剪贴板监听，避免系统继续向已销毁窗口投递消息
                    _clipboardMonitor.Detach();
                    break;

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
                        // 🧩 记录鼠标逻辑坐标，供插件组件的悬停判定使用
                        Renderer.UpdatePluginMouse(mx, my);
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

                        // 🧩 插件详情页展开时跳过原生悬停判定：详情页内容与交互完全由插件自己负责
                        if (Renderer.HasActiveDetailPage)
                        {
                            _isCursorOverIcon = false;
                            break;
                        }

                        // 🎵 时间轴拖动进行中：最优先接管（此时已 SetCapture，鼠标可能早已移出岛体）。
                        //    只改本地缓存，不打任何 COM / IO —— 这是频繁拖动不卡顿的关键。
                        if (_media.IsDragging)
                        {
                            _media.DragTo(Renderer.TimelineRatio(mx));
                            _isCursorOverIcon = true;
                            break;
                        }

                        if (_isHovered && isClipboardActive)
                        {
                            // 📋 剪贴板面板：仅「打开」按钮范围显示手型
                            _isCursorOverIcon = Renderer.HitClipboardOpen(mx, my - hitTopY);
                        }
                        else if (_isHovered && _currentToast != null)
                        {
                            _isCursorOverIcon = true;
                        }
                        else if (_isHovered && _media.IsActive && _currentToast == null && !isClipboardActive)
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
                                // 🎵 悬停到时间轴上也要切小手（y 需扣掉岛体下沉偏移，与 Draw 共用同一套坐标）
                                _isCursorOverIcon = Renderer.HoveredExpandedButton != -1 || Renderer.HitTimeline(mx, my - hitTopY);
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
                                    // 媒体按钮锚定「媒体模块右边界」，与 Renderer.Draw 保持一致
                                    // （组合模式下插件可能被排到媒体右边，因此由渲染器给出真实边界）
                                    float right = Renderer.GetMediaRight(Renderer.WINDOW_WIDTH, _currentWidth, _currentToast != null);
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

                case Win32.WM_LBUTTONUP:
                    // 🎵 松手：解除状态锁并把落点提交给播放器（拖动期间攒下的所有改动只在这一刻提交一次）
                    if (_media.IsDragging)
                    {
                        _media.EndDrag();
                        Win32.ReleaseCapture();
                        return (IntPtr)0;
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    {
                        _isTrackingMouse = false;
                        _isHovered = false;
                        _isCursorOverIcon = false;
                        Renderer.HoveredExpandedButton = -1;
                        // 🧩 鼠标离开灵动岛，清空插件组件悬停状态
                        Renderer.UpdatePluginMouse(-1f, -1f);
                        // 🎵 拖动中（已 SetCapture）：不收起岛体、也不解除状态锁，松手统一交给 WM_LBUTTONUP。
                        //    若消息丢失导致左键其实早已抬起，这里兜底解锁，避免进度条永久卡在拖动态。
                        if (_media.IsDragging)
                        {
                            if ((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0) break;
                            _media.EndDrag();
                            Win32.ReleaseCapture();
                        }
                        // 🧩 鼠标移走**不收起**媒体面板（用户 2026-09-19 要求）：
                        //    原来这里是 `Renderer.IsMediaExpanded = false`，移走即折叠。
                        //    现在收起只认「点击岛外」（见 Render 里的统一判定），鼠标单纯移开不管。
                        break;
                    }

                case Win32.WM_LBUTTONDOWN:
                    {
                        int cx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int cy = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

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

                        // 📋 剪贴板链接面板：命中右侧「打开」按钮 → 默认浏览器打开链接
                        if (isClipboardActive && Renderer.HitClipboardOpen(cx, cy - hitTopY))
                        {
                            OpenClipboardUrl();
                            return (IntPtr)0;
                        }

                        // 🧩 插件详情页展开时：岛内左键优先交给详情页（HitTest → OnAction）。
                        //    即使没有命中任何动作也消费掉这次点击，避免误触到底层原生媒体按钮。
                        if (_isHovered && Renderer.HasActiveDetailPage)
                        {
                            Renderer.DispatchDetailPageClick(cx, cy - hitTopY);
                            return (IntPtr)0;
                        }

                        // 🧩 插件组件左键交互：命中插件绘制区则交给插件决定做什么，不再走媒体控制逻辑
                        if (_isHovered && _currentToast == null && !isClipboardActive && Renderer.DispatchPluginLeftClick(cx, cy - hitTopY))
                        {
                            return (IntPtr)0;
                        }

                        if (_isHovered && _media.IsActive && _currentToast == null && !isClipboardActive)
                        {
                            // 🎵 命中时间轴：进入拖动并锁住鼠标，同时消费这次点击
                            //    （不能落到下面「点任意处就展开」的兜底分支）
                            if (Renderer.IsMediaExpanded && Renderer.HitTimeline(cx, cy - hitTopY)
                                && _media.BeginDrag(Renderer.TimelineRatio(cx)))
                            {
                                Win32.SetCapture(hwnd);
                                return (IntPtr)0;
                            }

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
                                    // 与 Renderer.Draw 的媒体按钮位置保持一致（组合模式下取渲染器给出的模块右边界）
                                    float right = Renderer.GetMediaRight(Renderer.WINDOW_WIDTH, _currentWidth, _currentToast != null);
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
                        // 🧩 岛内右键的优先级：详情页收起 → 媒体标题展开媒体控制 → 插件组件广播 → 设置窗口。
                        //    （前两步都是「原生区域」，插件组件占的是岛体右侧独立预留区，几何上不重叠）
                        if (_currentToast == null)
                        {
                            // 详情页已展开：岛内右键直接收起详情页（此时插件行未绘制，无需再广播）
                            if (Renderer.HasActiveDetailPage)
                            {
                                PluginManager.Instance.Host.CloseDetailPage();
                                return (IntPtr)0;
                            }

                            int rx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                            int ry = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                            float rtY = 12f * _currentStyleProgress;

                            // 🎵 折叠态右键媒体标题 → 直接展开媒体控制面板（消费这次右键，不弹设置窗口）。
                            //    展开交互模式（MediaInteractionMode == 1）下左键点空白处本就能展开，这里是给
                            //    「直接交互模式 / 左键被插件拿走」准备的等价入口；组合模式固定为直接交互不适用。
                            //    命中区由渲染侧「本帧真的画了标题文本」才登记，所以待机 / 通知 / 剪贴板 / 展开态都不会误判。
                            //    ⚠️ 仅当插件行真的有内容时才启用这个快捷入口：纯媒体控制器（无插件组件）时没有插件预留区，
                            //       岛体几乎整片都是标题热区，右键会被整片吃掉、只剩最右侧一条窄边能开设置窗口。
                            //       此时恢复默认交互 —— 右键直接打开设置窗口。
                            if (_media.IsActive && !Renderer.IsMediaExpanded && !Renderer.CompositeModeEnabled
                                && Renderer.HasPluginRowContent
                                && Renderer.HitMediaTitle(rx, ry - rtY))
                            {
                                Renderer.IsMediaExpanded = true;
                                return (IntPtr)0;
                            }

                            string? detailWidget = Renderer.DispatchPluginRightClick(rx, ry - rtY);

                            // 主机默认行为：命中的组件提供了详情页 → 在灵动岛展开该组件的详情页（消费这次右键，不弹设置窗口）
                            if (detailWidget != null)
                            {
                                PluginManager.Instance.Host.ToggleDetailPage(detailWidget);
                                return (IntPtr)0;
                            }
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