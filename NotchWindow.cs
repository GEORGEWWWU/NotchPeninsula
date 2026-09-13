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
        public static bool IsClipboardLinkEnabled = true; // 剪贴板链接识别开关（默认开启）
        public static bool IsTopmostEnabled = true; // 默认开启置顶
        public static IntPtr InstanceHandle { get; private set; } // 暴露给设置面板调用的句柄
        float _currentVolume = 0f;
        private readonly IntPtr _hwnd;
        public static readonly PluginHost PluginHostInstance = new();
        private readonly List<IWidget> _widgetRow = new();
        private string _cachedWidgetOrder = "";
        private string _cachedDisabled = "";
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

        // 剪贴板链接状态控制
        private string? _currentClipboardLink;
        private string? _pendingClipboardLink; // 通知优先时的单槽等待位（仅存引用，不额外分配队列内存）
        private DateTime _clipboardEndTime;
        private const double ClipboardLinkDurationSeconds = 3; // 链接展示时长
        // 提取文本中第一个 http/https 链接（沿用 RFC3986 合法字符集，天然在中文/空格处截断）
        private static readonly System.Text.RegularExpressions.Regex ClipboardLinkRegex =
            new(@"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private DateTime _animStartTime;
        private readonly IntPtr _hCursorArrow;
        private readonly SystemSettingsManager? audio;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private ToastNotificationListener? _listener;
        private DispatcherTimer? _pollingTimer;
        private readonly Dispatcher _dispatcher;
        private readonly DateTime _appStartTime = DateTime.Now;
        private readonly float[] _currentBars = new float[5]; // 平滑过渡（暂不采集，未来由媒体插件提供频谱）
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

            // 注册剪贴板内容变化监听，实现"每次复制即检测"
            if (!Win32.AddClipboardFormatListener(_hwnd))
                Warn("剪贴板监听注册失败，链接识别将不可用");
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

            // 通知优先级高于剪贴板：若链接岛正在展示，先让位给通知，等通知结束后再补展示
            if (!string.IsNullOrEmpty(_currentClipboardLink))
            {
                _pendingClipboardLink = _currentClipboardLink;
                _currentClipboardLink = null;
                Renderer.ClipboardButtonHovered = false;
                _isCursorOverIcon = false;
            }

            _currentToast = toast;
            _toastEndTime = DateTime.Now.AddSeconds(4); // 消息展示4秒自动消失
        }

        // 剪贴板内容变化回调：提取第一个 http/https 链接并展示
        private void OnClipboardUpdate()
        {
            if (!IsClipboardLinkEnabled) return;

            string? link = null;
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsText()) return;
                string text = System.Windows.Forms.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                var match = ClipboardLinkRegex.Match(text);
                if (!match.Success) return;

                // 去掉链接尾部的常见中英文标点，避免把 "https://a.com，" 这类符号带进去
                link = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '，', '。', '；', '：', '！', '？', '、');
                if (string.IsNullOrWhiteSpace(link)) return;
            }
            catch (Exception ex)
            {
                // 剪贴板可能被其他进程短暂占用，静默忽略即可
                Debug($"读取剪贴板失败：{ex.Message}");
                return;
            }

            if (!_dispatcher.CheckAccess()) { _dispatcher.Invoke(() => ApplyClipboardLink(link!)); return; }
            ApplyClipboardLink(link);
        }

        private void ApplyClipboardLink(string link)
        {
            // 同一个链接若正在展示中，不重复触发动画
            if (link == _currentClipboardLink && DateTime.Now < _clipboardEndTime) return;
            if (link == _pendingClipboardLink) return;

            // 通知优先级更高：通知展示期间先放入等待位，待通知结束后再展示
            if (_currentToast != null && DateTime.Now < _toastEndTime)
            {
                _pendingClipboardLink = link;
                Info($"检测到剪贴板链接，等待通知结束后展示：{link}");
                return;
            }

            _currentClipboardLink = link;
            _clipboardEndTime = DateTime.Now.AddSeconds(ClipboardLinkDurationSeconds);
            Info($"检测到剪贴板链接：{link}");
        }

        // 链接岛是否处于展示期（通知优先，通知展示期间链接岛让位）
        private bool IsClipboardLinkActive() => IsClipboardLinkEnabled && !isToastActive && !string.IsNullOrEmpty(_currentClipboardLink) && DateTime.Now < _clipboardEndTime;

        // 判断逻辑坐标是否落在链接岛右侧的跳转按钮上
        private bool IsOverClipboardButton(int mx, int my)
        {
            float topY = 12f * _currentStyleProgress;
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float right = left + _currentWidth;
            float btnX = right - Renderer.CLIPBOARD_PAD - Renderer.CLIPBOARD_BTN;
            float btnY = topY + (_currentHeight - Renderer.CLIPBOARD_BTN) / 2f;
            return mx >= btnX - 4 && mx <= btnX + Renderer.CLIPBOARD_BTN + 4
                && my >= btnY - 4 && my <= btnY + Renderer.CLIPBOARD_BTN + 4;
        }

        // 用系统默认浏览器打开当前链接，并立即收起链接岛
        private void OpenCurrentClipboardLink()
        {
            string? link = _currentClipboardLink;
            if (string.IsNullOrEmpty(link)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
                Info($"已用默认浏览器打开链接：{link}");
            }
            catch (Exception ex)
            {
                Error($"打开链接失败：{link}", ex);
            }

            _currentClipboardLink = null; // 打开后立即收起
            Renderer.ClipboardButtonHovered = false;
            _isCursorOverIcon = false;
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
                // 消息队列：通知优先级最高，通知结束后把等待位中的链接提升为展示（3s 计时从此刻开始）
                if (!isToastActive && _pendingClipboardLink != null)
                {
                    _currentClipboardLink = _pendingClipboardLink;
                    _pendingClipboardLink = null;
                    _clipboardEndTime = DateTime.Now.AddSeconds(ClipboardLinkDurationSeconds);
                }
                // 判断剪贴板链接是否处于激活期
                bool isClipboardActive = IsClipboardLinkActive();
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
                    if (!_isPassthroughAwake && isOverNotch && !isClipboardActive) targetAlpha = 0.0f;

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
                if (!isClipboardActive && _currentClipboardLink != null) { _currentClipboardLink = null; Renderer.ClipboardButtonHovered = false; _isCursorOverIcon = false; } // 链接超时清理

                // 如果灵动岛已展开，且鼠标不在岛上(!_isHovered)，且按下了左键(0x01)
                if (_isManuallyExpanded && !_isHovered && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                {
                    _isManuallyExpanded = false; // 触发收起
                }

                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出或链接岛展示时绝对不允许隐藏
                bool shouldHide = IsAutoHideEnabled && !Renderer.MediaActive && !_isManuallyExpanded && !isToastActive && !isClipboardActive;

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

                // 状态叠化透明度计算 (0.3s 平滑过渡，将媒体展开与折叠拆分为独立状态触发叠化)
                int currentDisplayState = isClipboardActive ? 5 : (isToastActive ? 3 : (Renderer.ActiveDetailWidget != null ? 4 : 0));
                if (currentDisplayState != _lastDisplayState)
                {
                    _lastDisplayState = currentDisplayState;
                    _stateChangeTime = DateTime.Now;
                }
                float transitionAlpha = (float)Math.Clamp((DateTime.Now - _stateChangeTime).TotalSeconds / 0.3, 0, 1);

                // 决策尺寸 (如果处于媒体模式且展开，直接锁定 320x130)
                float expectedTargetWidth;
                if (isClipboardActive)
                    expectedTargetWidth = Renderer.GetClipboardAutoWidth(_currentClipboardLink);
                else if (isToastActive)
                    expectedTargetWidth = Renderer.GetToastAutoWidth();
                else if (Renderer.ActiveDetailWidget?.DetailPage is { } detailPage)
                    expectedTargetWidth = detailPage.MeasureWidth();
                else if (Renderer.WidgetRow is { Count: > 0 })
                    expectedTargetWidth = Math.Clamp(WidgetLayout.MeasureRowWidth(Renderer.WidgetRow, Renderer.BASE_HEIGHT, 12f) + 32f, 60f, 900f);
                else
                    expectedTargetWidth = Renderer.STANDBY_WIDTH;

                // 高度优先级：剪贴板 → 通知 → 详情页 → 媒体态（展开 / 折叠） → 兜底
                float expectedTargetHeight = isClipboardActive ? Renderer.CLIPBOARD_HEIGHT
                    : (isToastActive ? Renderer.TOAST_HEIGHT
                    : (Renderer.ActiveDetailWidget?.DetailPage is { } activeDetail
                        ? Math.Clamp(activeDetail.MeasureHeight(), 130f, Renderer.MAX_WINDOW_HEIGHT)
                        : (Renderer.MediaActive
                            ? (Renderer.IsMediaExpanded ? 130f : Renderer.MEDIA_HEIGHT)
                            : Renderer.BASE_HEIGHT)));

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

            // ================= 4. 渲染调用更新 =================
            var canvas = _renderSurface!.Canvas;
            canvas.Clear(SKColors.Transparent); // 清空上一帧的残留

            // 存档矩阵状态，避免缩放无限叠加
            canvas.Save();

                // 让底层 C++ 引擎接管坐标放大
                canvas.Scale(_dpiScale);

                // 传入 currentHeight 和 _currentToast
                // 构建组件行（按 WidgetOrder 顺序，变化时才重建）
                BuildWidgetRowIfChanged();
                Renderer.WidgetRow = _widgetRow;
                Renderer.PluginWidgets = PluginHostInstance.Widgets;

                Renderer.Draw(canvas, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress, transitionAlpha, isClipboardActive ? _currentClipboardLink : null);

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
            string disabledStr = GetDisabledWidgetsStr();
            if (order == _cachedWidgetOrder && count == _cachedWidgetCount && disabledStr == _cachedDisabled && _widgetRow.Count > 0) return;
            _cachedWidgetOrder = order;
            _cachedWidgetCount = count;
            _cachedDisabled = disabledStr;

            var disabled = new HashSet<string>(disabledStr.Split(',', StringSplitOptions.RemoveEmptyEntries));
            var all = new List<IWidget>();
            foreach (var w in PluginHostInstance.Widgets) all.Add(w);

            _widgetRow.Clear();
            foreach (var id in order.Split(','))
            {
                if (disabled.Contains(id.Trim())) continue;
                var w = all.FirstOrDefault(x => x.Id == id.Trim());
                if (w != null && !_widgetRow.Contains(w)) _widgetRow.Add(w);
            }
            foreach (var w in all)
            {
                if (disabled.Contains(w.Id)) continue;
                if (!_widgetRow.Contains(w)) _widgetRow.Add(w);
            }
        }

        private static string GetDisabledWidgetsStr()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula");
                return key?.GetValue("DisabledWidgets") as string ?? "";
            }
            catch { return ""; }
        }

        public static bool IsWidgetDisabled(string id)
        {
            var disabled = new HashSet<string>(GetDisabledWidgetsStr().Split(',', StringSplitOptions.RemoveEmptyEntries));
            return disabled.Contains(id);
        }

        public static void ToggleWidgetEnabled(string id)
        {
            var disabled = new HashSet<string>(GetDisabledWidgetsStr().Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (!disabled.Add(id)) disabled.Remove(id);
            Program.SaveSetting("DisabledWidgets", string.Join(",", disabled));
        }

        // 返回全部组件（含停用）按 WidgetOrder 排序
        public static IReadOnlyList<IWidget> GetAllWidgetsInOrder()
        {
            var all = new List<IWidget>();
            foreach (var w in PluginHostInstance.Widgets) all.Add(w);
            string order = GetWidgetOrder();
            if (string.IsNullOrWhiteSpace(order)) order = "builtin.clock,builtin.hardware,builtin.media";
            var result = new List<IWidget>();
            foreach (var id in order.Split(','))
            {
                var w = all.FirstOrDefault(x => x.Id == id.Trim());
                if (w != null && !result.Contains(w)) result.Add(w);
            }
            foreach (var w in all)
            {
                if (!result.Contains(w)) result.Add(w);
            }
            return result;
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

        // 行内组件内部热区命中（用于手型指针）
        private static bool IsOverWidgetHotzone(int cx, int cy)
        {
            var slots = Renderer.WidgetRowSlots;
            if (slots == null) return false;
            float topY = Renderer.WidgetRowTopY;
            foreach (var slot in slots)
            {
                var rect = slot.Rect;
                if (cx < rect.Left || cx > rect.Right) continue;
                if (slot.Widget.HitTest(cx - rect.Left, cy - topY, rect).IsHit) return true;
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
                case Win32.WM_CLIPBOARDUPDATE:
                    OnClipboardUpdate();
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
                        float hitTopY = 12f * _currentStyleProgress;

                        // 0. 链接岛展示期：仅右侧跳转按钮可交互
                        if (IsClipboardLinkActive())
                        {
                            bool overBtn = IsOverClipboardButton(mx, my);
                            if (overBtn != Renderer.ClipboardButtonHovered || overBtn != _isCursorOverIcon)
                            {
                                Renderer.ClipboardButtonHovered = overBtn;
                                _isCursorOverIcon = overBtn;
                            }
                            break;
                        }

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
                        else
                        {
                            // 行内组件内部热区（如媒体播放控制）显示手型
                            _isCursorOverIcon = IsOverWidgetHotzone(mx, my);
                        }
                        break;
                    }

                case Win32.WM_MOUSELEAVE:
                    {
                        _isTrackingMouse = false;
                        _isHovered = false;
                        _isCursorOverIcon = false;
                        Renderer.HoveredExpandedButton = -1;
                        Renderer.ClipboardButtonHovered = false;
                        Renderer.IsMediaExpanded = false;
                        break;
                    }

                case Win32.WM_LBUTTONDOWN:
                    {
                        int cx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int cy = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 链接岛展示期：点击右侧跳转按钮用默认浏览器打开链接
                        if (IsClipboardLinkActive())
                        {
                            if (IsOverClipboardButton(cx, cy)) OpenCurrentClipboardLink();
                            return (IntPtr)0;
                        }

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
                            Renderer.ActiveDetailWidget = null; // 点击空白，关闭详情
                            return (IntPtr)0; // 关闭详情时不处理行内点击
                        }

                        // 行内组件点击（无详情时）
                        var wslots = Renderer.WidgetRowSlots;
                        if (wslots != null)
                        {
                            foreach (var slot in wslots)
                            {
                                var hitRect = new SKRect(slot.Rect.Left, slot.Rect.Top + Renderer.WidgetRowTopY, slot.Rect.Right, slot.Rect.Bottom + Renderer.WidgetRowTopY);
                                if (!hitRect.Contains(cx, cy)) continue;

                                var wh = slot.Widget.HitTest(cx - slot.Rect.Left, cy - Renderer.WidgetRowTopY, slot.Rect);
                                if (wh.IsHit)
                                {
                                    slot.Widget.OnLeftClick(wh.Action, cx - slot.Rect.Left, cy - Renderer.WidgetRowTopY);
                                    return (IntPtr)0;
                                }

                                // 未命中组件内部热区：有详情页则左键展开该插件
                                if (slot.Widget.DetailPage != null)
                                {
                                    Renderer.ActiveDetailWidget = slot.Widget;
                                    return (IntPtr)0;
                                }
                                break;
                            }
                        }

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

                        if (IsAutoHideEnabled && !Renderer.MediaActive && _currentY < -5f)
                        {
                            _isManuallyExpanded = true;
                            return (IntPtr)0;
                        }

                        break;
                    }

                case Win32.WM_RBUTTONDOWN:
                    // 右键固定打开设置窗口（插件详情改由左键展开）
                    if (_isHovered) ConsoleWindow.Toggle();
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