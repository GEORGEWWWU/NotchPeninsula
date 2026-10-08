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
        public static bool IsClipboardEnabled = true; // 剪贴板链接检测开关（交互设置，默认开启）
        public static bool IsTopmostEnabled = true; // 默认开启置顶
        public static IntPtr InstanceHandle { get; private set; } // 暴露给设置面板调用的句柄
        private readonly IntPtr _hwnd;
        private readonly MediaController _media;
        private bool _isHovered = false;
        private bool _isTrackingMouse = false;

        // ---- 渲染线程 ----
        private Thread? _renderThread;
        private readonly Win32.WndProc _wndProcDelegate;

        private IslandDropTarget? _islandDropTarget;

        // 动画引擎核心状态
        private bool _isAnimating = false;
        private float _currentWidth = Renderer.STANDBY_WIDTH;
        private float _startWidth = Renderer.STANDBY_WIDTH;
        private float _targetWidth = Renderer.STANDBY_WIDTH;
        private float _currentHeight = Renderer.MEDIA_HEIGHT;
        private float _startHeight = Renderer.MEDIA_HEIGHT;
        private float _targetHeight = Renderer.MEDIA_HEIGHT;
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
        private readonly SystemSettingsManager audio;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private ToastNotificationListener? _listener;
        private Timer? _pollingTimer;
        private readonly Dispatcher _dispatcher;
        private readonly DateTime _appStartTime = DateTime.Now;
        private readonly AudioAnalyzer _audioAnalyzer;
        private float[] _currentBars = new float[5]; // 用于渲染线程的平滑过渡
        private readonly float[] _spectrumBars = new float[5]; // LyricServer 12 频段压缩为 5 柱的复用缓冲（仅 RenderLoop 单线程内写入并当帧消费）
        private bool _wasUsingSoloSpectrum; // 上一帧是否在用 LyricServer 频谱，用于感知独占播放结束
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon; // 托盘与自启常量
        private System.Drawing.Icon? _trayIcon;
        // 既回收不掉也 Dispose 不了，所以提成字段。
        private Timer? _audioWatchTimer;
        private volatile bool _shuttingDown;
        private const string AppName = "NotchPeninsula";
        private static bool _isSyncingState = false; // 防重入锁，性能消耗几乎为 0
        public static bool IsAutoHideEnabled = false;

        public static bool IsAutoHideEffective => IsAutoHideEnabled;

        public static bool IsFocusAutoHideEnabled = false;

        public static bool IsFocusAutoHideEffective => IsAutoHideEffective && IsFocusAutoHideEnabled;

        public static bool IsPauseAutoHideEnabled = false;

        public static bool IsPauseAutoHideEffective => IsAutoHideEffective && IsPauseAutoHideEnabled;

        public static bool IsFullscreenAutoHideEnabled = false;

        public static bool IsFullscreenAutoHideEffective => IsAutoHideEffective && IsFullscreenAutoHideEnabled;

        // ---- 全屏检测（「全屏自动隐藏」专用） ----
        // 轻量化的三个关键：
        // 探测与消费都在渲染循环线程上，所以缓存不需要加锁。
        private static bool _isFullscreenCached;
        private static DateTime _fullscreenProbeAt = DateTime.MinValue;
        private const double FullscreenProbeIntervalSeconds = 0.8;

        private static void TickFullscreenProbe()
        {
            if (!IsFullscreenAutoHideEffective) { _isFullscreenCached = false; return; }

            var now = DateTime.UtcNow;
            if ((now - _fullscreenProbeAt).TotalSeconds < FullscreenProbeIntervalSeconds) return;
            _fullscreenProbeAt = now;

            _isFullscreenCached = false;
            try
            {
                if (Win32.SHQueryUserNotificationState(out int state) != 0) return;

                _isFullscreenCached = state == Win32.QUNS_BUSY                    // 全屏应用 / 演示文稿设置
                                   || state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN // 独占模式全屏 D3D
                                   || state == Win32.QUNS_PRESENTATION_MODE;      // 演示文稿模式
            }
            catch { _isFullscreenCached = false; }
        }

        private static bool IsFullscreenHideActive => IsFullscreenAutoHideEffective && _isFullscreenCached;

        private bool CanAutoHideNow
        {
            get
            {
                if (IsFullscreenHideActive) return true;  // 全屏优先：播放中也要让位
                if (_media.IsActive) return IsPauseAutoHideEffective && !_media.IsPlaying;
                return IsFocusAutoHideEffective;          // 无媒体会话 → 「焦点离开时自动隐藏」说了算
            }
        }

        private bool HasAnyExpanded
            => _isManuallyExpanded || Renderer.IsMediaExpanded || Renderer.HasActiveDetailPage;
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // Toast 监听器
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private string? _clipboardUrl;         // 当前正在展示的链接
        private string? _pendingClipboardUrl;  // 被更高级别通知挤下后退回队列等待的链接（单槽位复用，零额外内存）
        private DateTime _clipboardEndTime;    // 链接展示截止时间
        public bool isClipboardActive;         // 本帧剪贴板面板是否激活
        // ---- 弹簧动画引擎（三处共用） ----
        //（峰值在 t≈0.154s），即「Q 弹」的来源。
        private const double SpringFrequency = 2.65;
        private const double SpringDecay = 10.8;
        private const double SpringDurationSeconds = 0.450;

        private static double SpringEase(double elapsedSeconds)
            => 1.0 - Math.Cos(SpringFrequency * elapsedSeconds * 2.0 * Math.PI) * Math.Exp(-SpringDecay * elapsedSeconds);

        // Y轴动画引擎状态
        private float _currentY = 0f;
        private float _targetY = 0f;
        private float _startY = 0f;
        private bool _isYAnimating = false;
        private DateTime _yAnimStartTime;
        private bool _isManuallyExpanded = false; // 用户是否点击了尾巴展开
        // 落到岛体之外，被兜底轮询误判：
        //     —— 用户看到的是「抽一下又回去了」。
        private bool _suppressOutsideCollapse = false;

        // ---- 展开面板统一管理 ----

        private static readonly PanelCollapseTimer _mediaPanelCollapse = new(MediaCollapseDelayMs);
        private static readonly PanelCollapseTimer _detailPanelCollapse = new(DetailCollapseDelayMs);
        private static string? _detailCollapseWidgetId;

        private const int MediaCollapseDelayMs = 3000;
        private const int DetailCollapseDelayMs = 900;

        private sealed class PanelCollapseTimer
        {
            private readonly int _delayMs;
            private DateTime _deadline = DateTime.MinValue;

            public PanelCollapseTimer(int delayMs) => _delayMs = delayMs;

            public void Schedule(int? delayMs = null)
                => _deadline = DateTime.Now.AddMilliseconds(delayMs ?? _delayMs);

            public void Cancel() => _deadline = DateTime.MinValue;

            public bool Tick()
            {
                if (_deadline == DateTime.MinValue || DateTime.Now < _deadline) return false;
                _deadline = DateTime.MinValue;
                return true;
            }
        }
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
            _instanceForExit = this; // 托盘"退出"回调需要一条静态可达的引用链
            _liveInstance = this;
            audio = new SystemSettingsManager();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _media = new MediaController();
            _audioAnalyzer = new AudioAnalyzer();
            _wndProcDelegate = WndProcSafe;

            var wc = new Win32.WNDCLASS
            {
                style = Win32.CS_DBLCLKS,
                lpfnWndProc = _wndProcDelegate,
                hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
                lpszClassName = "NotchPeninsulaClass",
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };

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
            SetupIslandDropTarget();
            _renderThread = new Thread(RenderThreadLoop)
            {
                IsBackground = true,
                Name = "NPS-Render",
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
            _topmostTimer = new System.Threading.Timer(
                _ => EnsureTopmostAlive(), null, TOPMOST_KEEPALIVE_MS, TOPMOST_KEEPALIVE_MS);

            // 1. 先实例化托盘对象，防止闭包捕获到未初始化的变量
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            // 2. 最后再给托盘对象的各项属性赋值
            _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName);
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Text = "NotchPeninsula";

            // 右键弹自绘菜单；左键沿用系统默认行为（这里不接管）
            _notifyIcon.MouseUp += (s, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Right) return;

                Win32.GetCursorPos(out var pt);
                TrayMenuWindow.Show(pt.x, pt.y, ConsoleWindow.Toggle, RequestWakeIsland, ExitApplication);
            };

            _notifyIcon.Visible = true;
            Debug($"初始音量读取完成，当前音量：{audio.Volume:F2}");
            audio.VolumeSink = _media.TrySyncVolumeToJustSolo;
            PluginManager.Instance.Host.ReminderPosted += OnPluginReminder;
            PluginManager.Instance.Initialize();
            _ = InitializeListenerAsync();

            _clipboardMonitor.OnUrlDetected += OnClipboardUrlDetected;
            _clipboardMonitor.Attach(_hwnd);
            MediaHotkeys.Attach(_hwnd);
            _audioWatchTimer = new Timer(500);
            _audioWatchTimer.Elapsed += OnAudioWatchTick;
            _audioWatchTimer.Start();
        }

        // ---- 渲染线程主体 ----
        private const int FrameIntervalMs = 16;

        private const int FrameIdleWarnMs = 120;

        private long _lastFrameEndMs = -1;
        private long _lastPauseLogMs;

        private void RenderThreadLoop()
        {
            var clock = Stopwatch.StartNew();
            long nextTick = 0;

            while (!_shuttingDown)
            {
                long frameStart = clock.ElapsedMilliseconds;
                if (_lastFrameEndMs >= 0)
                {
                    long idle = frameStart - _lastFrameEndMs;
                    if (idle >= FrameIdleWarnMs && frameStart - _lastPauseLogMs >= 1000)
                    {
                        _lastPauseLogMs = frameStart;
                        Warn($"[渲染停顿] 距上一帧 {idle}ms，而本帧自身并不慢 —— "
                            + "不是渲染线程被占用（那会同时出现 [渲染卡顿]），而是它迟迟没被调度");
                    }
                }

                RenderLoop();   // 内部自带帧内分段计时，超阈值写 [渲染卡顿]

                long frameEnd = clock.ElapsedMilliseconds;
                _lastFrameEndMs = frameEnd;

                nextTick += FrameIntervalMs;
                long remain;
                while (!_shuttingDown && (remain = nextTick - clock.ElapsedMilliseconds) > 0)
                {
                    // 富余多就让出 CPU；最后几毫秒改成忙等 ——
                    // 全交给它对齐 16ms 网格会把帧率压到 30 上下。
                    if (remain > 4) Thread.Sleep(1);
                    else Thread.Yield();
                }
                if (nextTick < clock.ElapsedMilliseconds) nextTick = clock.ElapsedMilliseconds;
            }
        }

        private const int TOPMOST_KEEPALIVE_MS = 2000;
        private System.Threading.Timer? _topmostTimer;
        private int _topmostKeepAliveRunning;

        private void EnsureTopmostAlive()
        {
            if (_shuttingDown || !IsTopmostEnabled || _hwnd == IntPtr.Zero) return;
            if (Interlocked.Exchange(ref _topmostKeepAliveRunning, 1) == 1) return;

            try
            {
                IntPtr foreground = Win32.GetForegroundWindow();
                if (foreground != IntPtr.Zero && foreground != _hwnd)
                {
                    _ = Win32.GetWindowThreadProcessId(foreground, out uint pid);
                    if (pid == (uint)Environment.ProcessId) return;
                }

                Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Debug($"置顶保活异常（忽略）: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _topmostKeepAliveRunning, 0);
            }
        }

        private void OnAudioWatchTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            audio.RefreshFromSystem();
            ReclaimIdleMemory();
        }

        private const long IdleReclaimIntervalTicks = TimeSpan.TicksPerMinute * 10;
        private long _lastIdleReclaimTicks;

        private void ReclaimIdleMemory()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - _lastIdleReclaimTicks < IdleReclaimIntervalTicks) return;
            if (_shuttingDown || _currentToast != null || _isHovered || _media.IsPlaying) return;

            _lastIdleReclaimTicks = now;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        }

        private static void ExitApplication()
        {
            try
            {
                if (_instanceForExit?._notifyIcon != null)
                {
                    _instanceForExit._notifyIcon.Visible = false;
                    _instanceForExit._notifyIcon.Dispose();
                }
            }
            catch (Exception ex)
            {
                Error("释放托盘图标失败", ex);
            }

            try { _instanceForExit?.ShutdownResources(); }
            catch (Exception ex) { Error("释放窗口资源失败", ex); }

            Info("程序退出");
            _instanceForExit?._audioAnalyzer.Dispose(); // 停掉看门狗并释放捕获/COM 订阅
            // 退出路径上补一次，别把释放全推给进程终止。
            try { _instanceForExit?.audio.Dispose(); } catch (Exception ex) { Error("释放系统音量管理器失败", ex); }
            Environment.Exit(0);
        }

        private void ShutdownResources()
        {
            _shuttingDown = true;
            try
            {
                var t = _renderThread;
                if (t != null && !t.Join(500)) Warn("[渲染线程] 退出前未在 500ms 内收尾，交给进程终止兜底");
            }
            catch { }

            // 1b) 置顶保活定时器
            try { _topmostTimer?.Dispose(); _topmostTimer = null; } catch { }

            // 2) 系统音量看门狗
            try
            {
                if (_audioWatchTimer != null)
                {
                    _audioWatchTimer.Elapsed -= OnAudioWatchTick;
                    _audioWatchTimer.Stop();
                    _audioWatchTimer.Dispose();
                    _audioWatchTimer = null;
                }
            }
            catch { }

            // 3) 通知轮询
            try
            {
                if (_pollingTimer != null)
                {
                    _pollingTimer.Elapsed -= OnPollingTick;
                    _pollingTimer.Stop();
                    _pollingTimer.Dispose(); // 与上面两个定时器同款收尾：只 Stop 不 Dispose 会留下未释放的定时器资源
                    _pollingTimer = null;
                }
            }
            catch { }

            try
            {
                if (_listener != null)
                {
                    _listener.OnToastDetected -= OnToastDetected;
                    _listener.Dispose();
                    _listener = null;
                }
            }
            catch { }

            try { _clipboardMonitor.Detach(); } catch { }

            try
            {
                _renderSurface?.Dispose();
                _renderSurface = null;
                if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
                if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
                if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
                _hBitmap = _memDc = _oldBitmap = IntPtr.Zero;
            }
            catch { }

            // 7) 托盘图标句柄
            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;

            try { _media.Shutdown(); } catch { }

            try { Plugins.PluginManager.Instance.ShutdownAll(); }
            catch (Exception ex) { Logger.Error("释放插件失败", ex); }
        }

        private static NotchWindow? _instanceForExit;

        private static NotchWindow? _liveInstance;
        #region 监听
        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            var listener = new ToastNotificationListener();
            _listener = listener;
            var (ok, msg) = await listener.InitializeAsync();

            if (_shuttingDown)
            {
                try { listener.Dispose(); } catch { }
                return;
            }

            if (!ok) { Error($"监听失败：{msg}"); return; }
            listener.OnToastDetected += OnToastDetected;
            Info("通知监听已启动");

            _pollingTimer = new Timer(2000) { AutoReset = true };
            _pollingTimer.Elapsed += OnPollingTick;
            _pollingTimer.Start();
        }

        private void OnPollingTick(object? sender, System.Timers.ElapsedEventArgs e) => _ = _listener?.FetchLatestNotificationAsync();

        // ---- 通知轮询看门狗 ----
        private long _watchdogLastCheckTicks;
        private long _watchdogLastWarnTicks;

        /// 只看一件事：轮询还有没有在发起调用。判据用"发起时刻"而不是"取到数据的时刻"——
        private void TickPollingWatchdog()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _watchdogLastCheckTicks < TimeSpan.TicksPerSecond * 10) return;   // 10s 抽检一次
            _watchdogLastCheckTicks = nowTicks;

            var listener = _listener;
            if (listener == null || !IsToastEnabled) return;   // 通知功能没开：不适用

            long lastAttemptTicks = listener.LastPollAttemptUtcTicks;
            if (lastAttemptTicks == 0) return;                 // 还没跑过第一轮，谈不上停摆

            double silentSeconds = (nowTicks - lastAttemptTicks) / (double)TimeSpan.TicksPerSecond;
            if (silentSeconds < 30) return;

            if (nowTicks - _watchdogLastWarnTicks >= TimeSpan.TicksPerMinute * 5)
            {
                _watchdogLastWarnTicks = nowTicks;
                Warn($"[通知轮询] 看门狗：轮询已 {silentSeconds:F0} 秒没有任何一次触发（定时器疑似停摆），正尝试重启轮询定时器");
            }

            try
            {
                var timer = _pollingTimer;
                if (timer != null) { timer.Stop(); timer.Start(); }
            }
            catch (Exception ex)
            {
                Error("[通知轮询] 看门狗重启轮询定时器失败", ex);
            }
        }

        private static void PlayToastSound()
        {
            try
            {
                if (!IsToastEnabled) return;              // 通知总开关关闭 → 提示音一起静默
                if (!ToastSoundConfig.IsEnabled) return;  // 提示音自己的开关关闭（默认关）

                // 音源有两种可能：磁盘上的文件，或 exe 内嵌资源
                var src = ToastSoundConfig.ResolveCurrentSource();
                if (!src.IsValid) return;

                if (src.Path.Length > 0)
                    ToastSoundPlayer.Enqueue(src.Path, ToastSoundConfig.VolumePercent);
                else
                    ToastSoundPlayer.EnqueueResource(src.ResourceName, ToastSoundConfig.VolumePercent);
            }
            catch (Exception ex)
            {
                Error("[提示音] 投递异常", ex);
            }
        }

        private void OnToastDetected(ToastData toast)
        {
            if (toast == null) return;
            clicked_info = false;
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnToastDetected(toast)); return; }

            try { Plugins.PluginDataBridge.PublishNotification(toast); } catch { }

            _currentToast = toast;
            _toastEndTime = DateTime.Now.Add(toast.Duration);
            PlayToastSound();
        }

        private void OnPluginReminder(ToastData toast)
        {
            if (toast == null) return;
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnPluginReminder(toast)); return; }
            if (!IsToastEnabled) return;

            _currentToast = toast;
            _toastEndTime = DateTime.Now.Add(toast.Duration);   // 同上：插件提醒可自定义展示时长
            clicked_info = false;
            PlayToastSound();
        }

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
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
                Info($"[剪贴板] 已在默认浏览器打开链接: {url}");
            }
            catch (Exception ex) { Error("[剪贴板] 打开链接失败", ex); }
        }

        private bool HitWakeButton(int mx, int my)
        {
            float x = Renderer.WakeButtonX;
            float y = 12f * _currentStyleProgress + (_currentHeight - Renderer.WAKE_BTN_SIZE) / 2f;
            return mx >= x && mx <= x + Renderer.WAKE_BTN_SIZE
                   && my >= y && my <= y + Renderer.WAKE_BTN_SIZE;
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
            try
            {
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                if (!string.IsNullOrEmpty(module?.FileName)) return module!.FileName;
            }
            catch
            {
                // 拿不到就退回环境变量给的路径（两者都拿不到才算失败）
            }

            return Environment.ProcessPath ?? string.Empty;
        }

        private static string NormalizeRunValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Trim();
        }

        // 开机自启注册表逻辑
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
            if (!sourceIsTray)
            {
                // 设置面板改的 → 把自绘托盘菜单的 对齐，
                // 这样下次右键弹出（或菜单正开着）看到的就是真实状态
                TrayMenuWindow.SyncAutoStart(enable);
            }
            else
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
            if (_shuttingDown) return;

            if (System.Threading.Interlocked.Exchange(ref _isRendering, 1) == 1) return;

            try
            {
                // 它自己的诊断日志也一起哑了，从外部完全看不出原因。
                TickPollingWatchdog();

                // 而下面重建底层显存缓冲的判断恰好依赖这两个值。
                Renderer.RefreshDetailPageState();
                if (Renderer.ConsumeDetailCloseRequest()) PluginManager.Instance.Host.CloseDetailPage();

                // 实时追踪目标尺寸，动态安全重建底层显存画布
                float currentTargetDpi = (Win32.GetDpiForSystem() / 96f) * Renderer.GLOBAL_DPI;
                int targetScaledWidth = (int)(Renderer.WINDOW_WIDTH * currentTargetDpi);
                int targetScaledHeight = (int)(Renderer.MAX_WINDOW_HEIGHT * currentTargetDpi);

                if (Math.Abs(_dpiScale - currentTargetDpi) > 0.01f || _scaledWidth != targetScaledWidth || _scaledHeight != targetScaledHeight || _needsBufferResize)
                {
                    _dpiScale = currentTargetDpi;
                    _scaledWidth = targetScaledWidth;
                    _scaledHeight = targetScaledHeight;

                    _renderSurface?.Dispose();
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

                // 开关关闭时立即收起正在展示的链接并清空排队槽位
                // 排在后面会慢一帧、且与渲染状态不同步。
                if (!IsClipboardEnabled)
                {
                    _clipboardUrl = null;
                    _pendingClipboardUrl = null;
                    _clipboardEndTime = default;
                }
                else if (isToastActive)
                {
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

                // 实时穿透与 0% 透明度智能判定
                if (Renderer.PassthroughModeEnabled)
                {
                    float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float topY = 12f * _currentStyleProgress;

                    Win32.GetCursorPos(out var pt);
                    float logX = (pt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    float logY = (pt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;
                    bool isOverNotch = logX >= left && logX <= left + _currentWidth && logY >= topY && logY <= topY + _currentHeight;

                    if (_isPassthroughAwake && !isOverNotch && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                        _isPassthroughAwake = false;

                    // 第五个例外：任一展开态存在时一律不淡出。
                    float targetAlpha = 1.0f;
                    if (!_isPassthroughAwake && isOverNotch && _currentY >= -5f && !isClipboardActive && !isToastActive
                        && !Renderer.FileDragInProgress && !HasAnyExpanded) targetAlpha = 0.0f;

                    if (Renderer.FileDragInProgress && (Win32.GetAsyncKeyState(0x01) & 0x8000) == 0)
                        Renderer.FileDragInProgress = false;

                    Renderer.PassthroughAlpha += (targetAlpha - Renderer.PassthroughAlpha) * 0.18f;

                    if (Renderer.PassthroughAlpha < 0.01f) Renderer.PassthroughAlpha = 0f;
                    if (Renderer.PassthroughAlpha > 0.99f) Renderer.PassthroughAlpha = 1f;
                }
                else
                {
                    Renderer.PassthroughAlpha = 1.0f;
                    _isPassthroughAwake = false;
                }
                if (!isToastActive && _currentToast != null) {_currentToast = null;clicked_info = true;}; // 超时清理

                TickPanelCollapse();
                TickRightDoubleClickPending();

                // 没有待办时只是一次 Count 判断，稳态零开销。
                PluginManager.Instance.Host.DrainPendingWindowClose();

                if (!_media.IsDragging
                    && (HasAnyExpanded || _suppressOutsideCollapse))
                {
                    bool leftDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;

                    if (_suppressOutsideCollapse && !leftDown) _suppressOutsideCollapse = false;

                    float expLeft = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float expTopY = 12f * _currentStyleProgress;
                    Win32.GetCursorPos(out var expPt);
                    float expX = (expPt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    float expY = (expPt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;
                    bool isOverIsland = expX >= expLeft && expX <= expLeft + _currentWidth
                                        && expY >= expTopY && expY <= expTopY + _currentHeight;

                    if (!isOverIsland && !_suppressOutsideCollapse && leftDown && !Renderer.HasActiveDetailPage)
                    {
                        CollapseAllExpanded();
                    }
                }

                TickFullscreenProbe();

                // 三项都在它内部合成，所以这里不再重复写。
                // 剪贴板与插件详情页同理。
                bool shouldHide = CanAutoHideNow && !_media.IsDragging && !HasAnyExpanded && !isToastActive
                                  && !isClipboardActive;

                float currentTopY = 12f * _currentStyleProgress;
                float settledHeight = Math.Min(_currentHeight, _targetHeight);

                // 唤醒按钮（与穿透睡眠态同一颗）。
                bool slideOutHide = Renderer.IslandBaseY <= 0.5f;
                float expectedTargetY = shouldHide && slideOutHide ? -((settledHeight + currentTopY - 4) * _dpiScale) : 0f;

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

                if (elapsedY >= SpringDurationSeconds)
                {
                    _isYAnimating = false;
                    _currentY = _targetY;
                }
                else
                {
                    _currentY = (float)(_startY + (_targetY - _startY) * SpringEase(elapsedY));
                }
            }

                float targetFullHide = shouldHide && !slideOutHide ? 0f : 1f;
                Renderer.FullHideAlpha += (targetFullHide - Renderer.FullHideAlpha) * 0.18f;
                if (Renderer.FullHideAlpha < 0.01f) Renderer.FullHideAlpha = 0f;
                if (Renderer.FullHideAlpha > 0.99f) Renderer.FullHideAlpha = 1f;

                // 二维 (X轴宽度与Y轴高度) 弹簧动画逻辑
                bool currentActive = _media.IsActive;

                int currentDisplayState = isToastActive ? 3 : (isClipboardActive ? 4 : (currentActive ? (Renderer.IsMediaExpanded ? 2 : 1) : 0));
                if (currentDisplayState != _lastDisplayState)
                {
                    _lastDisplayState = currentDisplayState;
                    _stateChangeTime = DateTime.Now;
                }
                float transitionAlpha = (float)Math.Clamp((DateTime.Now - _stateChangeTime).TotalSeconds / 0.3, 0, 1);

                float detailW = 0f, detailH = 0f;
                bool detailOpen = !isToastActive && !isClipboardActive && Renderer.TryGetDetailPageSize(out detailW, out detailH);

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

                    if (Renderer.IsTranslationLineVisible(_media))
                        textWidth = Math.Max(textWidth, Renderer.MeasureLyricTranslationWidth(_media.CurrentLyricTranslation));

                    // （表现为「多出来的插件在灵动岛上就直接不显示」、
                    nativeWidth = Math.Max(nativeWidth, textWidth + 115f);
                }
                nativeWidth = Math.Min(nativeWidth, Renderer.MAX_ISLAND_WIDTH); // 岛体总长上限（1920），窄屏也不会被撑破

                // 插件贴上去只会把面板和岛体一起撑宽，见下面的分支。
                bool mediaPanel = currentActive && Renderer.IsMediaExpanded;

                float pluginReserve = 0f;
                if (mediaPanel)
                {
                    Renderer.SetPluginRowBudget(0f);
                }
                else if (Renderer.CompositeModeEnabled)
                {
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - Renderer.GetCompositeNativeWidth(_media));
                }
                else if (!isToastActive && !isClipboardActive && !detailOpen)
                {
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - nativeWidth);
                    pluginReserve = Renderer.GetPluginRowReserve();
                }
                else
                {
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
                    expectedTargetWidth = Renderer.GetClipboardAutoWidth(_clipboardUrl!);
                    expectedTargetHeight = Renderer.MEDIA_HEIGHT;
                }
                else
                {
                    expectedTargetWidth = Renderer.CompositeModeEnabled && !mediaPanel
                        ? Renderer.GetCompositeWidth(_media)
                        : nativeWidth + pluginReserve;

                    expectedTargetHeight = currentActive && Renderer.IsMediaExpanded
                        ? Renderer.GetExpandedHeight(_media)
                        : Renderer.MEDIA_HEIGHT;
                }

                // 现在预留一律用未缩放值，边界只跟着岛体边缘平滑移动。

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

                    if (elapsedS >= SpringDurationSeconds)
                    {
                        _isStyleAnimating = false;
                        _currentStyleProgress = _targetStyleProgress;
                    }
                    else
                    {
                        _currentStyleProgress = (float)(_startStyleProgress + (_targetStyleProgress - _startStyleProgress) * SpringEase(elapsedS));
                    }
                }

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

                    if (elapsed >= SpringDurationSeconds)
                    {
                        _isAnimating = false;
                        _currentWidth = _targetWidth;
                        _currentHeight = _targetHeight;
                    }
                    else
                    {
                        double spring = SpringEase(elapsed);

                    _currentWidth = (float)(_startWidth + (_targetWidth - _startWidth) * spring);
                    _currentHeight = (float)(_startHeight + (_targetHeight - _startHeight) * spring);
                }
            }

            // ---- 3. 其它效果 (淡入/音频柱) ----
            double uptime = (DateTime.Now - _appStartTime).TotalSeconds;
            float startupProgress = 1f;
            if (uptime < 0.6)
            {
                double t = uptime / 0.6;
                double invT = 1.0 - t;
                startupProgress = (float)(1.0 - (invT * invT * invT));
            }

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

            // ---- 4. 渲染调用更新 ----
            long tFrameStart = Environment.TickCount64;
            long tLyricDone = tFrameStart, tDrawDone = tFrameStart;

            var canvas = _renderSurface!.Canvas;
            canvas.Clear(SKColors.Transparent); // 清空上一帧的残留

            // 存档矩阵状态，避免缩放无限叠加
            // 关键是"回滚到基线"而不是"Restore 一次"：异常可能发生在 Draw 内部的第 N 级 save 之后，
            canvas.Save();
            int saveBaseline = canvas.SaveCount;
            try
            {
                // 让底层 C++ 引擎接管坐标放大
                canvas.Scale(_dpiScale);

                _media.UpdateLyrics(); // 更新歌词
                tLyricDone = Environment.TickCount64;

                Renderer.Draw(canvas, _media, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress, transitionAlpha, isClipboardActive ? _clipboardUrl : null);
                tDrawDone = Environment.TickCount64;

                // 恢复原始矩阵状态
                canvas.Restore();
            }
            catch
            {
                try { canvas.RestoreToCount(saveBaseline); } catch { }
                throw;
            }

            UpdateWindow();

            // 只有「岛体真的卡住了」才会出现这一行，平时完全静音。
            long tFrameEnd = Environment.TickCount64;
            long frameMs = tFrameEnd - tFrameStart;
            if (frameMs >= FrameStallLogMs && tFrameEnd - _lastStallLogTick >= 1000)
            {
                _lastStallLogTick = tFrameEnd;
                Warn($"[渲染卡顿] 单帧 {frameMs}ms —— 歌词/采样 {tLyricDone - tFrameStart}ms"
                    + $" · 绘制 {tDrawDone - tLyricDone}ms · 提交 {tFrameEnd - tDrawDone}ms");
            }
            }
            finally
            {
                // 渲染安全结束，释放标记，允许下一帧进入
                System.Threading.Interlocked.Exchange(ref _isRendering, 0);
            }
        }

        private const long FrameStallLogMs = 120;

        private long _lastStallLogTick;

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
            if (screenDc == IntPtr.Zero) return;

            // 从取到 DC 到归还之间不许有裸异常路径：
            try
            {
                var ptSrc = new Win32.POINT(0, 0);
                var ptDst = new Win32.POINT { x = 0, y = 0 };

                if (_cachedMonitorIndex != Renderer.TargetMonitorIndex) UpdateMonitorBounds();
                ptDst.x = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
                ptDst.y = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale) + (int)_currentY;

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
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
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

        internal void NotifyDragExit()
        {
            if (IsCursorOverIslandNow())
            {
                CancelPanelCollapse();
                return;
            }

            RequestPanelCollapse();

            // 到这里刻意不去动 _isHovered，原因很关键：
            // 悬停标志交给系统消息自己维护，这里只负责面板折叠时序。

            _isTrackingMouse = false;
        }

        private bool IsCursorOverIslandNow()
        {
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float topY = 12f * _currentStyleProgress;

            Win32.GetCursorPos(out var pt);
            float x = (pt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
            float y = (pt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;

            return x >= left && x <= left + _currentWidth && y >= topY && y <= topY + _currentHeight;
        }

        private void SetupIslandDropTarget()
        {
            try
            {
                if (!Win32.EnsureOleInitialized())
                {
                    Logger.Warn("[NotchWindow] OLE 不可用，详情页拖放已禁用");
                    return;
                }

                var target = new IslandDropTarget(this);
                int hr = Win32.RegisterDragDrop(_hwnd, target);
                if (hr != 0)
                {
                    Logger.Warn($"[NotchWindow] 岛体 RegisterDragDrop 失败：0x{hr:X8}，详情页拖放已禁用");
                    return;
                }

                _islandDropTarget = target;
                Logger.Info("[NotchWindow] 岛体拖放目标已就绪（详情页可接收拖入 / 发起拖出）");
            }
            catch (Exception ex)
            {
                Logger.Error("[NotchWindow] 岛体拖放登记异常", ex);
            }
        }

        private void RevokeIslandDropTarget()
        {
            if (_islandDropTarget == null) return;
            _islandDropTarget = null;
            try { Win32.RevokeDragDrop(_hwnd); } catch { /* 窗口已销毁 */ }
        }

        internal bool TryScreenToIslandLogical(Win32.POINT screenPt, out float x, out float y)
        {
            x = y = 0f;
            if (_hwnd == IntPtr.Zero || _dpiScale <= 0f) return false;

            var p = new Win32.POINT(screenPt.x, screenPt.y);
            if (!Win32.ScreenToClient(_hwnd, ref p)) return false;

            x = p.x / _dpiScale;
            y = p.y / _dpiScale - 12f * _currentStyleProgress;
            return true;
        }

        internal void KeepAliveForDrop()
        {
            _isHovered = true;
            CancelPanelCollapse();
        }

        public static bool StartFileDragOnIsland(IReadOnlyList<string> paths, bool allowMove = false)
        {
            var hwnd = InstanceHandle;
            if (hwnd == IntPtr.Zero || paths == null || paths.Count == 0) return false;

            var valid = new List<string>(paths.Count);
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    if (System.IO.File.Exists(p) || System.IO.Directory.Exists(p)) valid.Add(p);
                }
                catch { /* 路径含非法字符等，跳过这一条即可 */ }
            }
            if (valid.Count == 0) return false;

            if (!Win32.EnsureOleInitialized()) return false;

            DragOutState.Enter(hwnd);   // 标记「从岛体发起」，免得刚拖出去就被岛体自己接回来
            try
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetData(System.Windows.Forms.DataFormats.FileDrop, valid.ToArray());

                uint allowed = allowMove
                    ? Win32.DROPEFFECT_COPY | Win32.DROPEFFECT_MOVE
                    : Win32.DROPEFFECT_COPY;

                int hr = Win32.DoDragDrop(data, new FileDropSource(), allowed, out uint effect);
                return hr == 0 /* S_OK */ && effect != 0;
            }
            catch (Exception ex)
            {
                Logger.Error("[NotchWindow] 岛体发起拖出失败", ex);
                return false;
            }
            finally
            {
                DragOutState.Exit();

                // 这里补一次真实判定把它掰回来。
                _liveInstance?.NotifyDragExit();
            }
        }

        private IntPtr WndProcSafe(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return WndProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Logger.Error($"窗口过程处理消息 0x{msg:X4} 时异常，已忽略", ex);
                return IntPtr.Zero;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {            switch (msg)
            {
                case Win32.WM_CLIPBOARDUPDATE:
                    if (IsClipboardEnabled) _clipboardMonitor.HandleClipboardUpdate();
                    return (IntPtr)0;

                case Win32.WM_HOTKEY:
                    MediaHotkeys.Handle(Win32.Low32(wParam));
                    return (IntPtr)0;

                case Win32.WM_DESTROY:
                    _clipboardMonitor.Detach();
                    MediaHotkeys.Detach();
                    RevokeIslandDropTarget();
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
                            CancelPanelCollapse();
                        }

                        // 统一提炼坐标，大括号隔离作用域，彻底告别编译报错
                        int mx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int my = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        // 记录鼠标逻辑坐标，供插件组件的悬停判定使用
                        Renderer.UpdatePluginMouse(mx, my);
                        float hitTopY = 12f * _currentStyleProgress;

                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseMove(mx, my - hitTopY);

                        if ((Renderer.PassthroughModeEnabled && !_isPassthroughAwake) || Renderer.FullHideAlpha < 0.99f)
                        {
                            if (HitWakeButton(mx, my))
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

                        if (Renderer.HasActiveDetailPage)
                        {
                            _isCursorOverIcon = false;
                            break;
                        }

                        if (_media.IsDragging)
                        {
                            _media.DragTo(Renderer.TimelineRatio(mx));
                            _isCursorOverIcon = true;
                            break;
                        }

                        if (_isHovered && isClipboardActive)
                        {
                            // 剪贴板面板：仅「打开」按钮范围显示手型
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
                                int hoveredBtn = Renderer.HitExpandedButton(mx, my - hitTopY, _currentHeight);
                                Renderer.HoveredExpandedButton = hoveredBtn;
                                _isCursorOverIcon = hoveredBtn != -1 || Renderer.HitTimeline(mx, my - hitTopY);
                            }
                            else
                            {
                                if (Renderer.MediaExpandByLeftClick)
                                {
                                    // 媒体模块整块就是「点下去会展开」的热区 → 给小手。
                                    _isCursorOverIcon = Renderer.HitMediaZone(mx)
                                        && my >= hitTopY && my <= hitTopY + _currentHeight;
                                }
                                else if (Renderer.MediaExpandByRightClick)
                                {
                                    // 两种口径在这里保持一致。
                                    _isCursorOverIcon = false;
                                }
                                else
                                {
                                    float right = Renderer.GetMediaRight(Renderer.WINDOW_WIDTH, _currentWidth, _currentToast != null);
                                    _isCursorOverIcon = Renderer.HitInlineButton(mx, my - hitTopY, right, _currentHeight) != -1;
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
                    _suppressOutsideCollapse = false;
                    // 免得被下面媒体拖动分支的 return 漏掉。
                    if (Renderer.HasActiveDetailPage)
                    {
                        int ux = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int uy = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        Renderer.DispatchDetailPageMouseUp(ux, uy - 12f * _currentStyleProgress);
                    }
                    if (_media.IsDragging)
                    {
                        _media.EndDrag();
                        Win32.ReleaseCapture();
                        if (!_isHovered) RequestPanelCollapse();
                        return (IntPtr)0;
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    {
                        _isTrackingMouse = false;
                        _isHovered = false;
                        _isCursorOverIcon = false;
                        Renderer.HoveredExpandedButton = -1;
                        // 鼠标离开灵动岛，清空插件组件悬停状态
                        Renderer.UpdatePluginMouse(-1f, -1f);
                        if (_media.IsDragging)
                        {
                            if ((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0) break;
                            _media.EndDrag();
                            Win32.ReleaseCapture();
                        }
                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseLeave();

                        // 所以这里判定等价于「鼠标真的离开了灵动岛」，不用轮询。
                        RequestPanelCollapse();
                        break;
                    }

                case Win32.WM_LBUTTONDBLCLK:
                    {
                        // → 跳回正在放媒体的那个应用。
                        // 一次干净的双击直接跳转。
                        // · 展开态：封面那一格双击 → 跳转。
                        int dx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int dy = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);

                        if ((Renderer.PassthroughModeEnabled && !_isPassthroughAwake) || Renderer.FullHideAlpha < 0.99f) break;

                        // 它主动声明要双击，语义比宿主的内置手势更明确）。
                        {
                            float dblTopY = 12f * _currentStyleProgress;
                            if (_isHovered && _currentToast == null && !isClipboardActive)
                            {
                                if (Renderer.HasActiveDetailPage)
                                {
                                    if (Renderer.DispatchDetailPageDoubleClick(false, dx, dy - dblTopY)) return (IntPtr)0;
                                }
                                else if (Renderer.DispatchPluginDoubleClick(false, dx, dy - dblTopY))
                                {
                                    return (IntPtr)0;
                                }
                            }
                        }

                        bool launchEnabled = MediaController.IsAppLaunchEnabled;
                        bool onCover = Renderer.HitMediaLaunchZone(dx, dy);

                        Logger.Info($"媒体跳转[诊断]：双击 ({dx},{dy}) 开关={launchEnabled} 悬停={_isHovered} "
                            + $"媒体激活={_media.IsActive} 通知={_currentToast != null} 剪贴板={isClipboardActive} "
                            + $"详情页={Renderer.HasActiveDetailPage} 面板={Renderer.IsMediaPanelShowing(_media)} "
                            + $"命中封面={onCover}");

                        if (launchEnabled && _isHovered && _media.IsActive
                            && _currentToast == null && !isClipboardActive
                            && !Renderer.HasActiveDetailPage
                            && onCover)
                        {
                            _media.OpenCurrentApp();
                            return (IntPtr)0; // 消费掉：别再让第二下点到底下的播放按钮上
                        }

                        if (Renderer.StandbyToggleByDoubleClick && _isHovered
                            && _currentToast == null && !isClipboardActive
                            && !Renderer.HasActiveDetailPage)
                        {
                            // 进入 / 退出的命中区：
                            //   · 非待机态：双击空白进入；
                            bool onBlank = Renderer.IsBlankAt(dx, dy, _currentHeight);
                            bool hitsToggleZone = Renderer.StandbyActive && Renderer.StandbyScene == 3
                                ? Renderer.HitMediaZone(dx) || onBlank
                                : onBlank;

                            if (hitsToggleZone)
                            {
                                Renderer.StandbyActive = !Renderer.StandbyActive;
                                Program.SaveSetting("StandbyActive", Renderer.StandbyActive ? 1 : 0);
                                Logger.Info($"[待机模式] {(Renderer.StandbyActive ? "进入" : "退出")}"
                                    + $"（双击 {dx},{dy}，场景={Renderer.StandbyScene}）");
                                return (IntPtr)0;
                            }
                        }
                        break;
                    }

                case Win32.WM_LBUTTONDOWN:
                    {
                        int cx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int cy = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 完美对齐渲染中心点，精准拦截唤醒点击
                        if (Renderer.PassthroughModeEnabled && !_isPassthroughAwake && HitWakeButton(cx, cy))
                        {
                            _isPassthroughAwake = true;
                            if (_currentY < -5f)
                            {
                                _isManuallyExpanded = true;
                                _suppressOutsideCollapse = true;
                            }
                            return (IntPtr)0;
                        }

                        if (Renderer.FullHideAlpha < 0.99f && HitWakeButton(cx, cy))
                        {
                            _isManuallyExpanded = true;
                            return (IntPtr)0;
                        }

                        RaiseWindowClicked(cx, cy, "main-window");

                        // 「点击已隐藏的岛体把它唤回来」。
                        // 此时点击不该被当成「唤醒」而莫名锁上手动展开），
                        // 会导致那两种模式下「藏得下去、点不回来」。
                        if (CanAutoHideNow && _currentY < -5f)
                        {
                            _isManuallyExpanded = true;
                            _suppressOutsideCollapse = true;
                            return (IntPtr)0;
                        }

                        if (_isHovered && _currentToast != null
                            && cy >= hitTopY && cy <= hitTopY + Renderer.TOAST_HEIGHT)
                        {
                            var toastClick = _currentToast.OnClick;
                            if (toastClick != null)
                            {
                                _currentToast = null;
                                _toastEndTime = default;
                                try { toastClick(); }
                                catch (Exception ex) { Logger.Error("[NotchWindow] 插件提醒点击回调异常", ex); }
                                return (IntPtr)0;
                            }
                        }

                        if (isClipboardActive && Renderer.HitClipboardOpen(cx, cy - hitTopY))
                        {
                            OpenClipboardUrl();
                            return (IntPtr)0;
                        }

                        // 插件详情页展开时：岛内左键优先交给详情页。
                        if (_isHovered && Renderer.HasActiveDetailPage)
                        {
                            Renderer.DispatchDetailPageMouseDown(cx, cy - hitTopY);
                            Renderer.DispatchDetailPageClick(cx, cy - hitTopY);
                            return (IntPtr)0;
                        }

                        if (_isHovered && _currentToast == null && !isClipboardActive && Renderer.DispatchPluginLeftClick(cx, cy - hitTopY))
                        {
                            return (IntPtr)0;
                        }

                        if (_isHovered && _media.IsActive && _currentToast == null && !isClipboardActive)
                        {

                            // 命中时间轴：进入拖动并锁住鼠标，同时消费这次点击
                            // （不能落到下面「点媒体区就展开」的那条分支）
                            if (Renderer.IsMediaExpanded && Renderer.HitTimeline(cx, cy - hitTopY)
                                && _media.BeginDrag(Renderer.TimelineRatio(cx)))
                            {
                                Win32.SetCapture(hwnd);
                                return (IntPtr)0;
                            }

                            // 「不提供展开入口」的口径一致。
                            if (Renderer.IsMediaExpanded)
                            {
                                switch (Renderer.HitExpandedButton(cx, cy - hitTopY, _currentHeight))
                                {
                                    case 0: _media.Previous(); break;
                                    case 1: _media.TogglePlayPause(); break;
                                    case 2: _media.Next(); break;
                                }
                            }
                            else if (Renderer.MediaInteractionMode == 0)
                            {
                                // 与「悬停不显示控件」保持一致。
                                float right = Renderer.GetMediaRight(Renderer.WINDOW_WIDTH, _currentWidth, _currentToast != null);
                                switch (Renderer.HitInlineButton(cx, cy - hitTopY, right, _currentHeight))
                                {
                                    case 0: _media.Previous(); break;
                                    case 1: _media.TogglePlayPause(); break;
                                    case 2: _media.Next(); break;
                                }
                            }
                            else if (Renderer.MediaExpandByLeftClick && Renderer.HitMediaZone(cx))
                            {
                                // 折叠态左键单击展开：入口跟着
                                // 右键分支（左键整块留给双击跳转）。
                                ExpandPanel(Plugins.BuiltinWidgets.Media);
                                // 被兜底轮询判成「岛外点击」把面板当场收走。
                                _suppressOutsideCollapse = true;
                                Logger.Info($"媒体展开：折叠态左键单击 ({cx},{cy}) 命中媒体区 → 已展开媒体面板"
                                    + "（跳转关闭，右键仍打开设置）");
                                return (IntPtr)0;
                            }

                            // 跳转关掉时由上面那条分支展开。
                        }
                        break;
                    }

                case Win32.WM_RBUTTONDOWN:
                    if (_isHovered)
                    {
                        int rx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int ry = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        HandleIslandRightClick(rx, ry, 12f * _currentStyleProgress, fromDoubleClickTimeout: false);
                    }
                    break;

                case Win32.WM_RBUTTONDBLCLK:
                    {
                        if (_isHovered)
                        {
                            int bx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                            int by = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                            float bTopY = 12f * _currentStyleProgress;
                            if (ConsumeRightDoubleClick(bx, by - bTopY)) return (IntPtr)0;
                            // 它们没挂待定，所以要走另一条路重新命中一次。
                            if (Renderer.DispatchPassthroughRightDoubleClick(bx, by - bTopY)) return (IntPtr)0;
                        }
                        break;
                    }
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // 而是先挂一个与系统双击判定窗口同源的截止时间：
        private static DateTime _rightDblDeadline = DateTime.MinValue;
        private static bool _rightDblOnDetail;
        private static string? _rightDblWidgetId;
        private static float _rightDblLx, _rightDblLy;
        private static int _rightDblRx, _rightDblRy;
        private static float _rightDblTopY;

        private static void ScheduleRightDoubleClick(bool onDetail, int rx, int ry, float rtY,
            string? widgetId, float lx, float ly)
        {
            _rightDblOnDetail = onDetail;
            _rightDblWidgetId = widgetId;
            _rightDblLx = lx;
            _rightDblLy = ly;
            _rightDblRx = rx;
            _rightDblRy = ry;
            _rightDblTopY = rtY;
            _rightDblDeadline = DateTime.Now.AddMilliseconds(Win32.GetDoubleClickTime() + 30);
        }

        private static void ClearRightDoubleClickPending()
        {
            _rightDblDeadline = DateTime.MinValue;
            _rightDblOnDetail = false;
            _rightDblWidgetId = null;
        }

        private static bool ConsumeRightDoubleClick(float x, float y)
        {
            if (_rightDblDeadline == DateTime.MinValue) return false;

            bool onDetail = _rightDblOnDetail;
            string? widgetId = _rightDblWidgetId;
            float lx = _rightDblLx, ly = _rightDblLy;
            ClearRightDoubleClickPending();

            if (onDetail)
            {
                Logger.Info($"[插件] 右键双击透传 → 详情页 ({x:F0},{y:F0})");
                return Renderer.DispatchDetailPageDoubleClick(true, x, y);
            }
            if (widgetId != null)
            {
                Logger.Info($"[插件] 右键双击透传 → 组件 {widgetId}");
                return Renderer.DispatchWidgetDoubleClick(true, widgetId, lx, ly);
            }
            return false;
        }

        private static void TickRightDoubleClickPending()
        {
            if (_rightDblDeadline == DateTime.MinValue || DateTime.Now < _rightDblDeadline) return;

            bool onDetail = _rightDblOnDetail;
            string? widgetId = _rightDblWidgetId;
            int rx = _rightDblRx, ry = _rightDblRy;
            float rtY = _rightDblTopY;
            ClearRightDoubleClickPending();

            if (onDetail)
            {
                ClosePanelsNow(forceCloseDetail: true);
                return;
            }
            if (widgetId == null) return;

            // 两条默认路径都能原样走到，不必在这里各写一份。
            _liveInstance?.HandleIslandRightClick(rx, ry, rtY, fromDoubleClickTimeout: true);
        }

        private void HandleIslandRightClick(int rx, int ry, float rtY, bool fromDoubleClickTimeout)
        {
            if (_currentToast == null)
            {
                // 否则选了那一档的详情页就彻底没有关闭入口了。
                if (Renderer.HasActiveDetailPage)
                {
                    if (!fromDoubleClickTimeout && Renderer.DetailPageAcceptsRightClick
                        && Renderer.DispatchDetailPageRightClick(rx, ry - rtY))
                        return;

                    if (!fromDoubleClickTimeout && Renderer.DetailPageAcceptsDoubleClick)
                    {
                        ScheduleRightDoubleClick(onDetail: true, rx, ry, rtY, widgetId: null, lx: 0f, ly: 0f);
                        return;
                    }

                    ClosePanelsNow(forceCloseDetail: true);
                    return;
                }

                if (!fromDoubleClickTimeout
                    && Renderer.TryHitRightClickWidget(rx, ry - rtY, out var rcWidgetId, out float rcLx, out float rcLy,
                        out bool rcRight, out bool rcDouble))
                {
                    if (rcRight && Renderer.DispatchWidgetRightClick(rcWidgetId)) return;

                    if (rcDouble)
                    {
                        ScheduleRightDoubleClick(onDetail: false, rx, ry, rtY, rcWidgetId, rcLx, rcLy);
                        return;
                    }
                }

                string? detailWidget = Renderer.DispatchPluginRightClick(rx, ry - rtY);

                if (detailWidget != null)
                {
                    ExpandPanel(detailWidget);
                    return;
                }
            }

            // 后来细化为「入口跟着跳转开关走」）：
            if (_currentToast == null
                && Renderer.MediaExpandByRightClick
                && !Renderer.IsMediaExpanded
                && Renderer.HitMediaZone(rx))
            {
                ExpandPanel(Plugins.BuiltinWidgets.Media);
                _suppressOutsideCollapse = true;
                Logger.Info($"媒体展开：折叠态右键 ({rx},{ry}) 命中媒体区 → 已展开媒体面板"
                    + "（跳转开启时的入口；展开态右键仍打开设置）");
                return;
            }

            // 按「右键落在哪块原生内容上」直达对应设置页签：
            int targetTab = Renderer.NativeRightClickTab(rx);
            if (targetTab >= 0) ConsoleWindow.ShowTab(targetTab);
            else ConsoleWindow.Toggle();
        }

        internal static void ExpandPanel(string componentId)
        {
            _mediaPanelCollapse.Cancel();
            _detailPanelCollapse.Cancel();

            if (string.Equals(componentId, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase))
            {
                if (Renderer.HasActiveDetailPage) PluginManager.Instance.Host.CloseDetailPage();
                Renderer.IsMediaExpanded = true;
            }
            else
            {
                Renderer.IsMediaExpanded = false;
                PluginManager.Instance.Host.OpenDetailPage(componentId);
            }
        }

        public static void CloseMediaPanel()
        {
            _mediaPanelCollapse.Cancel();
            Renderer.IsMediaExpanded = false;
        }

        public void RequestWakeIsland()
        {
            _isManuallyExpanded = true;
            _suppressOutsideCollapse = true;
            _isPassthroughAwake = true;
        }

        private static void RequestPanelCollapse()
        {
            if (Renderer.IsMediaExpanded) _mediaPanelCollapse.Schedule();
            if (Renderer.HasActiveDetailPage)
            {
                int? delay = Renderer.ActiveDetailCollapseDelayMs;

                if (delay != Renderer.CollapseNever)
                {
                    _detailPanelCollapse.Schedule(delay);
                    _detailCollapseWidgetId = PluginManager.Instance.Host.ActiveDetailWidgetId;
                }
            }
        }

        private static void CancelPanelCollapse()
        {
            _mediaPanelCollapse.Cancel();
            _detailPanelCollapse.Cancel();
            _detailCollapseWidgetId = null;
        }

        private static void TickPanelCollapse()
        {
            if (_mediaPanelCollapse.Tick()) Renderer.IsMediaExpanded = false;

            if (!_detailPanelCollapse.Tick()) return;

            string? scheduled = _detailCollapseWidgetId;
            _detailCollapseWidgetId = null;

            // 这里必须再确认一次「插件此刻是否要求永不收起」：
            if (scheduled != null
                && Renderer.HasActiveDetailPage
                && !Renderer.ActiveDetailKeepsOpen
                && string.Equals(PluginManager.Instance.Host.ActiveDetailWidgetId, scheduled, StringComparison.OrdinalIgnoreCase))
            {
                PluginManager.Instance.Host.CloseDetailPage();
            }
        }

        private static void ClosePanelsNow(bool forceCloseDetail = false)
        {
            CancelPanelCollapse();
            Renderer.IsMediaExpanded = false;

            if (Renderer.HasActiveDetailPage
                && (forceCloseDetail || !Renderer.ActiveDetailKeepsOpen))
            {
                PluginManager.Instance.Host.CloseDetailPage();
            }
        }

        private void CollapseAllExpanded()
        {
            _isManuallyExpanded = false;
            ClosePanelsNow();
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

            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _renderSurface = SKSurface.Create(info, _pBits, _scaledWidth * 4);

            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}