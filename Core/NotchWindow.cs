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
        // 为什么不用 System.Timers.Timer：它的回调跑在**线程池**上。而本进程里有大量会阻塞的
        // 阻塞型 COM 调用（SMTC 的 GetTimelineProperties / GetPlaybackInfo —— 部分播放器
        // 在切歌、弹会员窗口这类繁忙时刻能把线程挂住数秒）。线程池线程一旦被成片占住，
        // 「下一帧」的回调就排在后面等调度 —— 岛体表现为整块冻住，而音乐照常在放。
        // 渲染节拍必须有自己的线程：别人的卡顿只能拖慢别人，拖不动岛体。
        private Thread? _renderThread;
        private readonly Win32.WndProc _wndProcDelegate;

        /// <summary>岛体的 OLE 拖入目标（详情页拖放用）。同时是 CCW 的强引用持有者，掉了可能被 GC 回收。</summary>
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
        // 通知轮询定时器：刻意用线程池定时器（System.Timers.Timer），不要换回 DispatcherTimer。
        // DispatcherTimer 依赖 WPF Dispatcher 的队列被"泵"，而本程序的主循环是纯 Win32 的 Run()
        // （GetMessage/DispatchMessage，没有 Dispatcher.Run/PushFrame）。实测它在启动后只跳几次就静默停摆：
        // 曾部署过一版带心跳的构建，2 分半内 0 条心跳（心跳在每次调用开头就打），而同一时刻 dispatcher
        // 明明是活的（HTTP 探针触发的 _dispatcher.Invoke 顺利回到 UI 线程并返回 200）——
        // 这就是"重启后能收几条、随后彻底收不到且日志全空"的根因。渲染循环与音量看门狗一直用
        // System.Timers.Timer，从未出现此问题。取快照这一步允许在 MTA 线程调用
        // （UserNotificationListener 声明的就是 MTA），真正需要 UI 线程的 OnToastDetected 自己会切回去。
        private Timer? _pollingTimer;
        private readonly Dispatcher _dispatcher;
        private readonly DateTime _appStartTime = DateTime.Now;
        private readonly AudioAnalyzer _audioAnalyzer;
        private float[] _currentBars = new float[5]; // 用于渲染线程的平滑过渡
        private readonly float[] _spectrumBars = new float[5]; // LyricServer 12 频段压缩为 5 柱的复用缓冲（仅 RenderLoop 单线程内写入并当帧消费）
        private bool _wasUsingSoloSpectrum; // 上一帧是否在用 LyricServer 频谱，用于感知独占播放结束
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon; // 托盘与自启常量
        // 托盘图标：NotifyIcon 不会释放赋给它的 Icon（那是调用方的东西），所以由本类持有并在退出时释放。
        // 之前这里是直接把 Icon.ExtractAssociatedIcon(...) 挂上去、不留引用 —— 等于让一个 HICON 无主。
        private System.Drawing.Icon? _trayIcon;
        // 系统音量看门狗（原为 CreateWindow 里的局部变量 `Timer aud`）：
        // 局部变量在 System.Timers.Timer 上是永生的（内部 AppDomain 计时器表持有注册），
        // 既回收不掉也 Dispose 不了，所以提成字段。
        private Timer? _audioWatchTimer;
        // 退出中标记：置位后渲染循环立刻停止，避免正在跑的帧撞上已被释放的画布
        private volatile bool _shuttingDown;
        private const string AppName = "NotchPeninsula";
        private static bool _isSyncingState = false; // 防重入锁，性能消耗几乎为 0
        /// <summary>「自动隐藏」总开关（面板小字：允许灵动岛自动隐藏）。关掉时下面三种模式一起失效。</summary>
        public static bool IsAutoHideEnabled = false;

        /// <summary>总开关实际是否生效：只由总开关决定，穿透模式不参与。</summary>
        public static bool IsAutoHideEffective => IsAutoHideEnabled;

        /// <summary>「焦点离开时自动隐藏岛」：没有媒体会话时，失去焦点就收起。</summary>
        public static bool IsFocusAutoHideEnabled = false;

        /// <summary>「焦点离开时自动隐藏岛」实际是否生效：总开关放行 且 自身开启。</summary>
        public static bool IsFocusAutoHideEffective => IsAutoHideEffective && IsFocusAutoHideEnabled;

        /// <summary>「暂停播放后自动隐藏」：媒体暂停 / 停止时，也把岛藏起来。</summary>
        public static bool IsPauseAutoHideEnabled = false;

        /// <summary>「暂停播放后自动隐藏」实际是否生效：总开关放行 且 自身开启。</summary>
        public static bool IsPauseAutoHideEffective => IsAutoHideEffective && IsPauseAutoHideEnabled;

        /// <summary>「全屏自动隐藏」：检测到全屏视频 / 游戏（含独占 D3D）时无条件让位，播放中也不显示。</summary>
        public static bool IsFullscreenAutoHideEnabled = false;

        /// <summary>「全屏自动隐藏」实际是否生效：总开关放行 且 自身开启。</summary>
        public static bool IsFullscreenAutoHideEffective => IsAutoHideEffective && IsFullscreenAutoHideEnabled;

        // ---- 全屏检测（「全屏自动隐藏」专用） ----
        // 轻量化的三个关键：
        // 1. 只调一次 Win32（SHQueryUserNotificationState），不自己枚举窗口比对显示器矩形；
        // 2. 节流：最多每 0.8s 探一次 —— 全屏切换是秒级事件，不需要 16ms 级延迟；
        // 3. 功能没开就一次系统调用都不发，直接把缓存压回 false。
        // 探测与消费都在渲染循环线程上，所以缓存不需要加锁。
        private static bool _isFullscreenCached;
        private static DateTime _fullscreenProbeAt = DateTime.MinValue;
        private const double FullscreenProbeIntervalSeconds = 0.8;

        /// <summary>
        /// 节流刷新全屏检测缓存。由渲染循环在算 shouldHide 之前调用一次。
        /// 唤醒点击分支只读缓存（IsFullscreenHideActive），不重复探测。
        /// </summary>
        private static void TickFullscreenProbe()
        {
            // 功能没开（或穿透模式压着）就彻底不探测，顺手把缓存压回 false，
            // 免得残留上一次的 true 让「刚关掉开关岛体还躲着」。
            if (!IsFullscreenAutoHideEffective) { _isFullscreenCached = false; return; }

            var now = DateTime.UtcNow;
            if ((now - _fullscreenProbeAt).TotalSeconds < FullscreenProbeIntervalSeconds) return;
            _fullscreenProbeAt = now;

            _isFullscreenCached = false;
            try
            {
                // 返回 HRESULT：非 0 表示查询失败，out 值不可信 → 保持 false。
                // 这里刻意静默 catch（不写日志）：本方法每 0.8s 跑一次，一旦失败会持续失败，
                // 打日志等于把日志刷爆；而且失败时「当成没有全屏」是安全的一侧（岛体保持原样，不会乱躲）。
                if (Win32.SHQueryUserNotificationState(out int state) != 0) return;

                _isFullscreenCached = state == Win32.QUNS_BUSY                    // 全屏应用 / 演示文稿设置
                                   || state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN // 独占模式全屏 D3D
                                   || state == Win32.QUNS_PRESENTATION_MODE;      // 演示文稿模式
            }
            catch { _isFullscreenCached = false; }
        }

        /// <summary>「全屏自动隐藏」此刻是否真的在起作用（开关生效 且 缓存里检测到全屏）。</summary>
        private static bool IsFullscreenHideActive => IsFullscreenAutoHideEffective && _isFullscreenCached;

        /// <summary>
        /// 「现在允许自动隐藏吗」——自动隐藏判定的单一真源。
        /// shouldHide（藏不藏）与 WM_LBUTTONDOWN 的唤醒分支（点了能不能唤回）必须共用它，
        /// 否则就会出现「藏得下去、点不回来」。
        ///
        /// 三种模式互相独立、可任意组合，且都被总开关拦住（总开关关掉时三个 Effective 全为 false）：
        /// · 焦点离开时自动隐藏：没有媒体会话 → 允许
        /// · 暂停播放后自动隐藏：媒体暂停 / 停止时 → 允许
        /// · 全屏自动隐藏：检测到全屏应用 → 无条件允许
        ///
        /// 历史坑：唤醒分支曾自己写死 `!_media.IsActive`。加了「暂停后隐藏」之后，岛体会在
        /// `_media.IsActive == true`（暂停中）的状态下藏起来，写死的判据就变成「藏得下去、点不回来」。
        /// 所以两边一律读这里，别再各写一份。
        /// </summary>
        private bool CanAutoHideNow
        {
            get
            {
                if (IsFullscreenHideActive) return true;  // 全屏优先：播放中也要让位
                if (_media.IsActive) return IsPauseAutoHideEffective && !_media.IsPlaying;
                return IsFocusAutoHideEffective;          // 无媒体会话 → 「焦点离开时自动隐藏」说了算
            }
        }

        /// <summary>
        /// 当前是否存在任一展开态 —— 交互语义的单一真源。三个来源任一为真即视为展开中：
        /// 手动展开的岛体（_isManuallyExpanded）、媒体面板（Renderer.IsMediaExpanded）、
        /// 插件详情页（Renderer.HasActiveDetailPage）。
        ///
        /// 语义：展开态一律不自动收起，只有外部点击或显式操作才折叠。这条同时约束
        /// 自动隐藏与穿透悬停淡出两处 —— 必须共用本属性，否则又会出现「一处记得排除、另一处忘了」。
        /// </summary>
        private bool HasAnyExpanded
            => _isManuallyExpanded || Renderer.IsMediaExpanded || Renderer.HasActiveDetailPage;
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // Toast 监听器
        // 剪贴板链接监听（事件驱动，仅在复制时读一次剪贴板，稳态零占用）
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private string? _clipboardUrl;         // 当前正在展示的链接
        private string? _pendingClipboardUrl;  // 被更高级别通知挤下后退回队列等待的链接（单槽位复用，零额外内存）
        private DateTime _clipboardEndTime;    // 链接展示截止时间
        public bool isClipboardActive;         // 本帧剪贴板面板是否激活
        // ---- 弹簧动画引擎（三处共用） ----
        // 岛体尺寸（宽/高）、形态切换（刘海 ⇄ 灵动岛）、自动隐藏位移（Y 轴）共用同一条曲线，
        // 所以三处手感一致。以前同一公式抄三份、常量各写一遍，改一处忘一处就会出现
        // 「这个动画弹、那个不弹」。新增位移动画直接调 SpringEase()。
        //
        // 曲线：1 - cos(freq·t·2π)·e^(-decay·t)
        // freq 越大爆发越干脆，decay 越小阻尼越低、余震越多。当前取值最大过冲约 15.9%
        //（峰值在 t≈0.154s），即「Q 弹」的来源。
        private const double SpringFrequency = 2.65;
        private const double SpringDecay = 10.8;
        // 取到曲线基本归位（≈99.7%）的时刻，再长只是空转。注意是时间而非进度：
        // 弹簧由时间驱动，不能按 t/duration 归一化后再套。
        private const double SpringDurationSeconds = 0.450;

        /// <summary>
        /// 弹簧缓动：传入已过去的秒数，返回 0→1 的插值系数（中途会过冲，大于 1 是正常的）。
        /// </summary>
        private static double SpringEase(double elapsedSeconds)
            => 1.0 - Math.Cos(SpringFrequency * elapsedSeconds * 2.0 * Math.PI) * Math.Exp(-SpringDecay * elapsedSeconds);

        // Y轴动画引擎状态
        private float _currentY = 0f;
        private float _targetY = 0f;
        private float _startY = 0f;
        private bool _isYAnimating = false;
        private DateTime _yAnimStartTime;
        private bool _isManuallyExpanded = false; // 用户是否点击了尾巴展开
        // 「刚引发状态变化的那一次左键按下还没松开」标记：本次按下期间不把「岛外点击」当收起手势。
        // 两个来源本质相同 —— 按下那一刻的岛体几何与随后不同，于是这次点击的坐标在变化之后
        // 落到岛体之外，被兜底轮询误判：
        //   ① 点击屏幕顶边唤醒岛体：点的是 y≈0，而岛体下沉后可见矩形从 y = 12 才开始，这次点击
        //      天然落在岛体之外。不屏蔽的话，唤醒自己的点击会被判成「岛外点击」，岛刚滑出就被收回
        //     —— 用户看到的是「抽一下又回去了」。
        //   ② 点折叠态媒体区展开面板：折叠态岛体可能比展开面板（锁死 320）更宽（长歌词自适应 /
        //      组合模式），展开瞬间岛体变窄，按下时还在岛内的坐标随即落到岛外。不屏蔽的话，面板
        //      刚展开就被 CollapseAllExpanded 收回 —— 用户看到的是「点一下展开、又立刻收回去」。
        //
        // 解除不靠 WM_LBUTTONUP，而是每帧读一次 GetAsyncKeyState(0x01)：岛体滑回后，光标所在
        // 那条屏幕顶边在窗口里是透明像素，分层窗口的透明区域不参与命中测试，up 消息很可能派发不到
        // 本窗口；直接观察物理按键状态精确且不丢信号。刻意不加时间上限：上限会让「长按超过 N 秒」
        // 重新踩回这个 bug（实测 1.5s 上限时按住 1.6s 仍会抽一下又回去），而按键松开是每帧实测的。
        private bool _suppressOutsideCollapse = false;

        // ---- 展开面板统一管理 ----
        // 媒体控制面板（builtin.media）与插件组件详情页共用同一套开合逻辑与时序，不再各写一份：
        // ExpandPanel(id)        展开某个组件的面板（同一时刻只留一块，另一块让位）
        // RequestPanelCollapse() 鼠标离开岛体 → 挂延迟折叠（两块延迟不同，见下面两个常量）
        // CancelPanelCollapse()  鼠标回到岛上 → 取消挂起
        // ClosePanelsNow()       岛外点击这种明确动作 → 立即折叠
        // TickPanelCollapse()    每帧结算到期的折叠
        // 面板内容仍各归各自的宿主持有（媒体 = Renderer.IsMediaExpanded，详情页 = PluginHost），
        // 这里统一的是开合入口与时序。做成静态：全局只有一块岛体，渲染循环与设置窗口都要能调。

        private static readonly PanelCollapseTimer _mediaPanelCollapse = new(MediaCollapseDelayMs);
        private static readonly PanelCollapseTimer _detailPanelCollapse = new(DetailCollapseDelayMs);
        // 挂起的那次延迟折叠是冲着哪个组件去的。到期时只收这一个 —— 万一延迟期间插件换了另一张
        // 详情页（前一张自己收起、后一张打开），不能把用户刚看到的新页面顺手收掉。
        private static string? _detailCollapseWidgetId;

        /// <summary>媒体控制面板的延迟折叠时长。</summary>
        private const int MediaCollapseDelayMs = 3000;
        /// <summary>插件详情页的延迟折叠时长（取 0.8~1s 这个区间的手感）。</summary>
        private const int DetailCollapseDelayMs = 900;

        /// <summary>一块展开面板的延迟折叠计时：只管「什么时候收」，展开状态由各自的宿主持有。</summary>
        private sealed class PanelCollapseTimer
        {
            private readonly int _delayMs;
            private DateTime _deadline = DateTime.MinValue;

            public PanelCollapseTimer(int delayMs) => _delayMs = delayMs;

            /// <summary>
            /// 挂起延迟折叠（重复挂起按最后一次重新计时）。
            ///  传 null 就用构造时的默认值 ——
            /// 插件详情页允许自定义这段时长（IDetailPage.AutoCollapseDelay），所以这里得能被覆盖。
            /// </summary>
            public void Schedule(int? delayMs = null)
                => _deadline = DateTime.Now.AddMilliseconds(delayMs ?? _delayMs);

            /// <summary>取消挂起（鼠标回到岛上）。</summary>
            public void Cancel() => _deadline = DateTime.MinValue;

            /// <summary>到点返回 true 并清零截止时间；没到点返回 false。</summary>
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
                // CS_DBLCLKS：声明「本类窗口要收双击消息」，系统才会把同一位置的第二次按下
                // 升格成 WM_LBUTTONDBLCLK（媒体控制的双击跳转就靠它）。
                // 不给这个样式的话，第二次按下依然只是普通 WM_LBUTTONDOWN，双击无从判定。
                style = Win32.CS_DBLCLKS,
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
            // 让岛体也能接文件拖放：右键展开的插件详情页靠它实现「拖入 / 拖出」
            SetupIslandDropTarget();
            // 渲染循环：独立线程 + 16ms 节拍（理由见 _renderThread 处）。
            // 具名方法而不是 lambda：线程要能被命名、能在退出时观察（IsBackground 保证进程退出不被它拖住）。
            _renderThread = new Thread(RenderThreadLoop)
            {
                IsBackground = true,
                Name = "NPS-Render",
                // 略高于普通线程：岛体的 60FPS 不能被后台的采样 / 网络链挤掉。
                // 不到 Highest —— 系统级实时优先级会让整机都跟着卡。
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
            // 置顶保活：独立定时器（不在渲染线程上，见 EnsureTopmostAlive）
            _topmostTimer = new System.Threading.Timer(
                _ => EnsureTopmostAlive(), null, TOPMOST_KEEPALIVE_MS, TOPMOST_KEEPALIVE_MS);

            // 托盘图标与右键菜单（自绘纯色菜单，见 TrayMenuWindow）
            // 1. 先实例化托盘对象，防止闭包捕获到未初始化的变量
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            // 2. 最后再给托盘对象的各项属性赋值
            _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName);
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Text = "NotchPeninsula";

            // 右键弹自绘菜单；左键沿用系统默认行为（这里不接管）
            // 坐标锚点用鼠标位置，比托盘图标矩形更稳（NotifyIcon 拿不到图标 rect）
            _notifyIcon.MouseUp += (s, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Right) return;

                Win32.GetCursorPos(out var pt);
                TrayMenuWindow.Show(pt.x, pt.y, ConsoleWindow.Toggle, RequestWakeIsland, ExitApplication);
            };

            _notifyIcon.Visible = true;
            Debug($"初始音量读取完成，当前音量：{audio.Volume:F2}");
            // 内置音量（系统主音量）只有一个下游：接了 Just Solo 的 WS 就下发播放器，没接才改系统主音量。
            // Just Solo 自己的音量是另一个变量（MediaController.TryGetJustSoloVolume），两边互不覆盖。
            audio.VolumeSink = _media.TrySyncVolumeToJustSolo;
            // 插件系统：先把插件提醒接入 Toast 流，再初始化运行时自动加载已启用插件
            PluginManager.Instance.Host.ReminderPosted += OnPluginReminder;
            PluginManager.Instance.Initialize();
            _ = InitializeListenerAsync();

            // 订阅剪贴板监听：窗口句柄就绪后注册 WM_CLIPBOARDUPDATE
            _clipboardMonitor.OnUrlDetected += OnClipboardUrlDetected;
            _clipboardMonitor.Attach(_hwnd);
            // 媒体全局快捷键：同样等句柄就绪后注册（开关关闭时内部什么都不做）。
            // 挂在岛主窗口而不是设置窗口上 —— 设置窗口关掉后热键要照常生效。
            MediaHotkeys.Attach(_hwnd);
            // 每 500ms 读一次系统音量，发现不经过 SystemSettingsManager 的改动（音量键 / 系统 OSD / 其它软件）
            _audioWatchTimer = new Timer(500);
            _audioWatchTimer.Elapsed += OnAudioWatchTick;
            _audioWatchTimer.Start();
        }

        // ---- 渲染线程主体 ----
        /// <summary>帧间隔（毫秒）—— 60FPS。</summary>
        private const int FrameIntervalMs = 16;

        /// <summary>两帧之间空闲多久算「停顿」：比 16ms 多出这么多就记一行日志。</summary>
        private const int FrameIdleWarnMs = 120;

        private long _lastFrameEndMs = -1;
        private long _lastPauseLogMs;

        /// <summary>
        /// 渲染线程：固定 16ms 节拍画岛体。
        ///
        /// 分段计时与停顿日志的分工（排查卡死的唯一凭据，别删）：
        ///   · 「[渲染卡顿] 单帧 Nms —— 歌词/采样 a · 绘制 b · 提交 c」由 RenderLoop 写：
        ///     本帧**自己**花掉的时间太久 —— 说明渲染线程里有人在等（阻塞调用、插件代码）。
        ///   · 「[渲染停顿] 距上一帧 Nms」由本方法写：帧间空闲异常长，而本帧自身并不慢 ——
        ///     说明**不是**本帧干的，而是这一帧迟迟没被调度（CPU 被抢 / 系统繁忙）。
        ///     本线程是专用线程，所以运维上再出现这一条就只剩「整机 CPU 挤爆」这一种解释。
        /// </summary>
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

                // 下一拍 = 16ms 网格。落后了就把网格拉回当前时刻 —— **不追帧**：
                // 追帧会在一次停顿之后连着补画好几帧，观感反而是二次顿挫。
                nextTick += FrameIntervalMs;
                long remain;
                while (!_shuttingDown && (remain = nextTick - clock.ElapsedMilliseconds) > 0)
                {
                    // 富余多就让出 CPU；最后几毫秒改成忙等 ——
                    // Thread.Sleep(1) 的实际粒度取决于系统定时器分辨率（可能是 15.6ms），
                    // 全交给它对齐 16ms 网格会把帧率压到 30 上下。
                    if (remain > 4) Thread.Sleep(1);
                    else Thread.Yield();
                }
                if (nextTick < clock.ElapsedMilliseconds) nextTick = clock.ElapsedMilliseconds;
            }
        }

        // 置顶保活：WS_EX_TOPMOST 只是窗口的一个样式位，Windows 并不替我们看守 topmost 组内部的次序。
        // 任何别的置顶窗口（任务栏组件、托盘菜单、其它悬浮工具）每被激活一次就排到本岛前面，
        // 而本岛几乎从不激活（它刻意不抢焦点），于是永远轮不到自己往回排 ——
        // 症状就是「置顶有时候失效」，重新拨一次设置里的置顶开关立刻恢复
        // （那次 SetWindowPos 把它重新提到了组首）。这里按固定间隔补同样的调用，省掉手工那一步。

        private const int TOPMOST_KEEPALIVE_MS = 2000;
        private System.Threading.Timer? _topmostTimer;
        private int _topmostKeepAliveRunning;

        /// <summary>
        /// 补一次置顶。
        ///
        /// ⚠️ 必须跑在**独立定时器的线程池线程**上，绝不能放回渲染线程（曾经由渲染 tick 每次调用）：
        /// `SetWindowPos(HWND_TOPMOST)` 要改动全局 Z 序，当系统里正有别的置顶窗口在出现 / 消失
        /// （别的播放器弹会员窗、任务栏组件、托盘菜单…）时，这个调用会去等窗口管理器与对方线程，
        /// 可能挂住几十毫秒到几秒 —— 挂在渲染线程上就是「别的软件一弹窗，我的灵动岛就冻住」。
        /// 放在这里：就算它卡住，也只是这一拍迟到（重入闸会丢弃重叠的下一拍），岛体照常出帧。
        /// </summary>
        private void EnsureTopmostAlive()
        {
            if (_shuttingDown || !IsTopmostEnabled || _hwnd == IntPtr.Zero) return;
            if (Interlocked.Exchange(ref _topmostKeepAliveRunning, 1) == 1) return;

            try
            {
                // 用户正在操作本进程的其它窗口（设置窗口 / 插件窗口）时不动 Z 序：
                // 那些窗口与岛矩形重叠，把岛提到最前会让点击落到岛身上，设置窗口就点不动了。
                IntPtr foreground = Win32.GetForegroundWindow();
                if (foreground != IntPtr.Zero && foreground != _hwnd)
                {
                    _ = Win32.GetWindowThreadProcessId(foreground, out uint pid);
                    if (pid == (uint)Environment.ProcessId) return;
                }

                // SWP_NOACTIVATE 必带：补 Z 序不能顺手把焦点从用户正在用的窗口抢过来。
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

        /// <summary>系统音量看门狗（500ms）。具名方法：退出时能 -= 退订。</summary>
        private void OnAudioWatchTick(object? sender, System.Timers.ElapsedEventArgs e) => audio.RefreshFromSystem();

        /// <summary>
        /// 自绘托盘菜单「退出」项的执行体。原封不动搬自旧的 ToolStripMenuItem 闭包，
        /// 顺序很重要：先摘掉托盘图标，再停音频看门狗，最后才 Exit。
        /// </summary>
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

            // 释放本窗口持有的全部长期资源（定时器 / 渲染缓冲 / 托盘图标 / 监听器）。
            // 之前只摘了托盘图标就直接 Exit —— 靠进程终止兜底，等于把"没释放"这件事藏起来了。
            try { _instanceForExit?.ShutdownResources(); }
            catch (Exception ex) { Error("释放窗口资源失败", ex); }

            Info("程序退出");
            _instanceForExit?._audioAnalyzer.Dispose(); // 停掉看门狗并释放捕获/COM 订阅
            // 系统音量管理器持有 IMMDevice / IAudioEndpointVolume 两个 COM 对象。
            // 它的 Dispose 以前从没被任何地方调用过（整个 IDisposable 实现是死代码），
            // 退出路径上补一次，别把释放全推给进程终止。
            try { _instanceForExit?.audio.Dispose(); } catch (Exception ex) { Error("释放系统音量管理器失败", ex); }
            Environment.Exit(0);
        }

        /// <summary>
        /// 退出前释放本窗口持有的全部长期资源。
        ///
        /// 顺序不能反：先把所有"会回调进来的源头"停掉（定时器 / 轮询 / 监听器 / 剪贴板），
        /// 再释放它们会碰到的资源（SKSurface 及其绑定的 DIB、图标句柄）——
        /// 反过来做就是让还在跑的定时器撞上已释放的画布。
        /// </summary>
        private void ShutdownResources()
        {
            // 1) 渲染线程：置退出标记 → 等它自己收尾（最长 500ms，超时不硬杀 ——
            //    它是后台线程，进程退出不会等它，这里只是尽量让它把最后一帧收干净）
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

            // 4) 通知监听器（HttpListener 占着 47300 端口 + 一条后台循环）
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

            // 5) 剪贴板监听反注册（WM_DESTROY 也会做一次，两处都幂等）
            try { _clipboardMonitor.Detach(); } catch { }

            // 6) 渲染资源：SKSurface 绑在 pBits 上，必须先于 DIB 释放
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

            // 8) 媒体侧的后台连接（Just Solo LyricServer 的 WebSocket 重连循环）
            try { _media.Shutdown(); } catch { }

            // 9) 插件：让每个插件走一遍 Dispose + 宿主注销 + ALC 卸载。
            // 刻意不做 GC 验证（进程随后就退出），目的只是别让插件的清理逻辑被进程终止整块吞掉。
            // 此刻渲染时钟已停（第 1 步），所以插件卸载不会与渲染帧并发。
            try { Plugins.PluginManager.Instance.ShutdownAll(); }
            catch (Exception ex) { Logger.Error("释放插件失败", ex); }
        }

        /// <summary>
        /// 供静态退出/托盘回调使用的实例引用。
        /// NotchWindow 本身是实例类，但托盘回调是静态语义，需要一条稳定的引用链。
        /// </summary>
        private static NotchWindow? _instanceForExit;

        /// <summary>
        /// 当前活跃的岛体实例 —— 给 StartFileDragOnIsland 这类静态入口
        /// 回过头调用实例方法用（拖出结束后要补一次悬停判定，见 NotifyDragExit）。
        /// </summary>
        private static NotchWindow? _liveInstance;
        #region 监听
        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            var listener = new ToastNotificationListener();
            _listener = listener;
            var (ok, msg) = await listener.InitializeAsync();

            // await 期间用户可能已经退出：ShutdownResources 会把 _listener 置空、停掉定时器，
            // 这里必须先判退出标记，否则续体会往已释放的对象上挂事件、并重新拉起一个定时器。
            if (_shuttingDown)
            {
                try { listener.Dispose(); } catch { }
                return;
            }

            if (!ok) { Error($"监听失败：{msg}"); return; }
            listener.OnToastDetected += OnToastDetected;
            Info("通知监听已启动");

            // 轮询用线程池定时器（原因见字段声明处）：DispatcherTimer 在本程序的主循环下会静默停摆。
            // 取快照本身允许在 MTA 线程调用，弹通知由 OnToastDetected 自己切回 UI 线程。
            _pollingTimer = new Timer(2000) { AutoReset = true };
            _pollingTimer.Elapsed += OnPollingTick;
            _pollingTimer.Start();
        }

        /// <summary>通知轮询（2s）。具名方法：退出时能 -= 退订。</summary>
        private void OnPollingTick(object? sender, System.Timers.ElapsedEventArgs e) => _ = _listener?.FetchLatestNotificationAsync();

        // ---- 通知轮询看门狗 ----
        private long _watchdogLastCheckTicks;
        private long _watchdogLastWarnTicks;

        /// <summary>
        /// 轮询看门狗（挂在渲染循环上，每 ~10s 抽检一次）。
        ///
        /// 只看一件事：轮询还有没有在发起调用。判据用"发起时刻"而不是"取到数据的时刻"——
        /// 取不到数据（超时、权限失效、快照冻结）由轮询自己的诊断负责，这里专治"轮询压根没在跑"
        /// 这一类静默故障：DispatcherTimer 在本程序的主循环下跳几次就不动了，
        /// 而它自己的诊断日志也随之消失，从外部完全无痕。发现停摆就重启定时器并留下日志。
        /// </summary>
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

            // 告警节流到 5 分钟一条；但重启动作每次都做（很便宜，能尽早把轮询拉回来）。
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

        /// <summary>
        /// 在消息真正上岛时投递提示音。
        ///
        /// 三个入口（系统通知 / HTTP 推送 / 插件提醒）共用这一个方法，保证行为一致：
        /// - 总开关「系统消息通知」关着 → 不响（HTTP 分支本身不受总开关拦截，这里补齐判定）；
        /// - 提示音开关「消息提示音」关着 → 不响（默认就是关的，想听要用户主动开）；
        /// - 用户选了「无」→ 不响；
        /// - 路径失效 / 超限 → 静默不响，绝不影响消息本身的展示。
        ///
        /// 入队是纯内存操作，几乎零耗时，不会拖慢渲染线程或 HTTP 响应。
        /// </summary>
        private static void PlayToastSound()
        {
            try
            {
                if (!IsToastEnabled) return;              // 通知总开关关闭 → 提示音一起静默
                if (!ToastSoundConfig.IsEnabled) return;  // 提示音自己的开关关闭（默认关）

                // 音源有两种可能：磁盘上的文件，或 exe 内嵌资源
                // （单文件发布时 data\sound 未必在磁盘上 —— exe 被单独拷走就只剩内嵌那份）
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
            // 用 BeginInvoke（不等待）而不是 Invoke：调用方可能是 HTTP 接收线程或轮询的线程池线程，
            // 这里只是赋值 + 入队提示音，不需要返回值 —— 别让它们被 UI 线程的忙闲拖着走。
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnToastDetected(toast)); return; }

            // 转发给插件只读数据桥：插件版的「系统通知」通道（同一个插件只收到一次，
            // 因为上面已把非 UI 线程的重入切回 UI 线程，本行只会执行一次）。
            try { Plugins.PluginDataBridge.PublishNotification(toast); } catch { }

            _currentToast = toast;
            // 展示时长：默认 4 秒（ToastData.Duration 的默认值）——
            // 插件提醒可以用 ReminderData.Duration 覆盖它（宿主已夹到 1~60 秒）。
            _toastEndTime = DateTime.Now.Add(toast.Duration);
            PlayToastSound();
        }

        /// <summary>插件通过 IPluginHost.PostReminder 投递的提醒，复用现有 Toast 展示通道。</summary>
        private void OnPluginReminder(ToastData toast)
        {
            if (toast == null) return;
            // 插件提醒来自后台线程，切回 UI 线程更新共享的 Toast 状态
            if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => OnPluginReminder(toast)); return; }
            if (!IsToastEnabled) return;

            _currentToast = toast;
            _toastEndTime = DateTime.Now.Add(toast.Duration);   // 同上：插件提醒可自定义展示时长
            clicked_info = false;
            PlayToastSound();
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
                // using：Process 是可释放对象（持有进程句柄 / 内部状态），启动后立刻释放即可，
                // 不会影响被启动的程序（Dispose 只放包装对象，不碰目标进程）
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
                Info($"[剪贴板] 已在默认浏览器打开链接: {url}");
            }
            catch (Exception ex) { Error("[剪贴板] 打开链接失败", ex); }
        }

        /// <summary>
        /// 穿透唤醒按钮的命中判定（逻辑坐标）。
        /// 位置算式的唯一真源在渲染侧（Renderer.WakeButtonX / Renderer.WAKE_BTN_SIZE），
        /// 这里只补上岛体的垂直偏移。鼠标移动（手型指针）与左键按下（唤醒）必须共用它 ——
        /// 之前两处各写一份算式，改位置时漏了一处，结果按钮移到了中心、hover 却没有小手。
        /// </summary>
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
            // Process / MainModule 都持有原生句柄，必须确定性释放 ——
            // 本方法每次读写「开机自启」都会调用（设置界面、托盘菜单都走），不能靠 GC 兜底。
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

            // 兼容 "C:\...\App.exe" 这种带引号的写法
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
            // 退出中：画布与 DIB 即将（或已经）被释放，这一帧直接不画。
            // 必须放在重入判定之前 —— 放后面会在返回时漏掉 _isRendering 的复位。
            if (_shuttingDown) return;

            if (System.Threading.Interlocked.Exchange(ref _isRendering, 1) == 1) return;

            try
            {
                // 轮询看门狗：借用渲染循环这个最可靠的时钟（16ms 线程池定时器）去盯"通知轮询还在不在跑"。
                // 教训就是：自检不能放在被检对象自己身上 —— DispatcherTimer 停摆后，
                // 它自己的诊断日志也一起哑了，从外部完全看不出原因。
                TickPollingWatchdog();

                // 插件详情页状态必须最先同步：WINDOW_WIDTH / MAX_WINDOW_HEIGHT 会随详情页尺寸变化，
                // 而下面重建底层显存缓冲的判断恰好依赖这两个值。
                // 详情页 Measure 抛异常被熔断时，这里顺手把宿主状态收起，岛体恢复原状。
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

                // 级别调度（消息队列，零额外分配）：系统通知 > 剪贴板链接 > 媒体控制器
                // 开关关闭时立即收起正在展示的链接并清空排队槽位
                // 本块必须排在下面的穿透判定之前：穿透逻辑要读本帧的 isClipboardActive
                // （Toast 的 isToastActive 在更上面就已算好）来决定是否临时退出穿透（见下），
                // 排在后面会慢一帧、且与渲染状态不同步。
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

                // 实时穿透与 0% 透明度智能判定
                if (Renderer.PassthroughModeEnabled)
                {
                    float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float topY = 12f * _currentStyleProgress;

                    // 因为开启穿透后系统收不到鼠标消息，必须用 GetCursorPos 底层轮询
                    Win32.GetCursorPos(out var pt);
                    float logX = (pt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    // 窗口 Y = 显示器原点 + 岛体垂直基准 + _currentY，换算回窗口内坐标要把基准一起减掉
                    float logY = (pt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;
                    bool isOverNotch = logX >= left && logX <= left + _currentWidth && logY >= topY && logY <= topY + _currentHeight;

                    // 如果处于唤醒状态，但鼠标点击了本体外任意地方，立刻进入睡眠
                    if (_isPassthroughAwake && !isOverNotch && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0)
                        _isPassthroughAwake = false;

                    // 当处于睡眠状态且鼠标悬停时，目标透明度为 0f（0%），系统会自动让其完全物理穿透！
                    // 例外：系统主动弹出的内容展示期间临时禁用穿透 —— 剪贴板链接面板与 Toast 通知。
                    // 二者的共同点是「弹出时机不由用户决定、且本身需要被看见和点击」：一旦悬停就变透明，
                    // 用户既看不到也点不到，靠唤醒按钮也救不回来 —— 面板只停 3s，
                    // 等你去点唤醒按钮时它已经消失了。内容一结束（点开 / 超时 / 被通知挤下）穿透自动恢复 ——
                    // 不需要任何额外状态：两个 is*Active 标志位都由本帧的调度逻辑维护。
                    // 第三个例外：文件正被拖着经过岛体时（Renderer.FileDragInProgress）同样不许淡出。
                    // 这一条比上面两条更硬：淡到全透明 = 岛体像素从 OLE 命中测试里消失，
                    // 拖放目标当场丢失，用户手里的文件就再也放不进详情页了（拖放源那边也不会补发第二次 DragEnter）。
                    // 标志位由 IslandDropTarget 在 DragEnter / DragLeave / Drop 维护，拖放一结束穿透自动回来。
                    // 第四个例外：岛体已上移隐藏时（`_currentY < -5f`，只剩屏幕顶部那条 4px 细边）。
                    // 此时若还按悬停淡出，用户一靠近细边它就变透明 —— 细边是唯一的唤回入口，淡掉就再也点不回来。
                    // 保持不透明同时也消掉了「手一靠近细边它就闪一下」的观感问题。用上一帧的 _currentY
                    // 判定即可（16ms 延迟无感），与唤回分支用的同一个闸门。
                    // 第五个例外：任一展开态存在时一律不淡出。
                    // 媒体面板 / 插件详情页 / 手动展开，三者都是「用户主动打开、需要持续看见并操作」的内容，
                    // 鼠标一悬停就让它们淡到 0%，等于面板当场消失 —— 既看不见也点不到，还会因为全透明
                    // 像素脱离 OLE 命中测试而连带影响拖放。展开态本来就不该自动收起（要收只走外部点击
                    // 或显式操作），穿透淡出属于「自动隐藏」的一种，同一条规范覆盖。
                    // 判据统一走 HasAnyExpanded（与 shouldHide 同源），别再各写一份三连判断。
                    float targetAlpha = 1.0f;
                    if (!_isPassthroughAwake && isOverNotch && _currentY >= -5f && !isClipboardActive && !isToastActive
                        && !Renderer.FileDragInProgress && !HasAnyExpanded) targetAlpha = 0.0f;

                    // 兜底自动复位：拖放源被杀 / 崩溃时 DragLeave、Drop 一个都不会来，
                    // 标志位若一直挂着，穿透淡出就永久失效（而且看不出是谁干的）。
                    // 文件拖放全程必须按住左键，松开就说明这一轮早就结束了 —— 一个系统调用就能把它收干净。
                    if (Renderer.FileDragInProgress && (Win32.GetAsyncKeyState(0x01) & 0x8000) == 0)
                        Renderer.FileDragInProgress = false;

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

                // 展开态的收起策略：只要鼠标离开灵动岛，展开的面板就自动折叠。
                // · 触发在 WM_MOUSELEAVE（见下）：两块面板都只挂一个延迟截止时间（见 RequestPanelCollapse），
                // 自动隐藏的「手动展开」刻意不跟，理由也写在 ClosePanelsNow / CollapseAllExpanded 上。
                // · 到点由下面这行统一结算（每帧一次 DateTime 比较，可忽略）。
                TickPanelCollapse();
                // 右键双击待定同理：到期说明用户只按了一下右键 → 补执行原来的单击行为。
                TickRightDoubleClickPending();

                // 卸载插件时没关掉的窗口在这里逐帧重试（拖放进行中被禁用/重载的那类窗口）。
                // 没有待办时只是一次 Count 判断，稳态零开销。
                PluginManager.Instance.Host.DrainPendingWindowClose();

                // · 这里是一层兜底轮询：窗口只在鼠标进入它范围内时才收得到鼠标消息，岛外点击根本不会派发
                // WM_LBUTTONDOWN，且 SetCapture（拖时间轴）期间 WM_MOUSELEAVE 会被吞掉，
                // 所以额外判断一次「左键按下 且 光标不在岛体矩形内」，命中就收起
                // （坐标换算与上面穿透模式那段完全同一套：减去显示器原点、减窗口 Y 偏移、再除 DPI）。
                // · 拖动中一律不收起 —— 拖时间轴时鼠标合法地待在岛外，此时收起会把面板从手里抽走；
                // 松手若仍在岛外，由 WM_LBUTTONUP 补一次判定。
                // 只在「确实有东西展开着」时才轮询（HasAnyExpanded），全无展开时这段直接跳过，稳态零开销。
                // · `_suppressOutsideCollapse` 也纳入轮询条件：它的解除靠下面每帧观察按键是否松开
                // （不能只靠 WM_LBUTTONUP —— 岛体滑回后，光标所在的那条屏幕顶边在窗口里是透明像素，
                // 分层窗口的透明区域不参与命中测试，up 消息很可能根本派发不到本窗口）。
                if (!_media.IsDragging
                    && (HasAnyExpanded || _suppressOutsideCollapse))
                {
                    bool leftDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;

                    // 这一次点击的按键已经松开 → 立刻解除抑制，用户再点岛外照常收起。
                    if (_suppressOutsideCollapse && !leftDown) _suppressOutsideCollapse = false;

                    float expLeft = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float expTopY = 12f * _currentStyleProgress;
                    Win32.GetCursorPos(out var expPt);
                    float expX = (expPt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    // 同上：窗口 Y 含岛体垂直基准，换算回窗口内坐标要一起减掉
                    float expY = (expPt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;
                    bool isOverIsland = expX >= expLeft && expX <= expLeft + _currentWidth
                                        && expY >= expTopY && expY <= expTopY + _currentHeight;

                    // 刚引发状态变化的那一次按键（还没松开）不算「岛外点击」—— 见 _suppressOutsideCollapse
                    // 上的说明：唤醒时点的屏幕顶边坐标天然在岛体可见矩形之外；左键展开媒体面板时岛体
                    // 会变窄，按下时还在岛内的坐标随即落到岛外。两种都必须排除，否则「点一下就被收回」。
                    //
                    // 插件详情页展开期间，岛外的左键一律不管（末尾那个 !HasActiveDetailPage）：
                    // ① 用左键点组件展开时，用户的手还按在按键上，紧接着这几帧都会落进这个判定；
                    // 而展开那一瞬间 WINDOW_WIDTH / _scaledWidth 正在变，换算出的 expLeft / expX 会偏，
                    // 一旦判成「岛外点击」就把刚展开的面板收掉了 —— 肉眼就是「点一下闪一下、展不开」。
                    // （右键展开没这个问题：那时 leftDown 是 false，压根不进这个分支。）
                    // ② 正在从资源管理器往面板里拖文件的用户，鼠标本来就该待在岛外。
                    // 收起详情页仍有两条明确路径：岛内右键、插件自己调 CloseDetailPage()。
                    if (!isOverIsland && !_suppressOutsideCollapse && leftDown && !Renderer.HasActiveDetailPage)
                    {
                        CollapseAllExpanded();
                    }
                }


                // 全屏检测：节流刷新缓存（功能没开时这个方法直接返回，零系统调用）。
                // 必须在下面读 CanAutoHideNow 之前调用，否则会用到上一帧的旧值。
                TickFullscreenProbe();

                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出时绝对不允许隐藏；插件详情页展开时同样不允许隐藏。
                // 判据统一走 CanAutoHideNow —— 它是三种模式的单一真源，穿透模式不参与：
                // 开了穿透照常自动隐藏，隐藏态的唤回入口与平时一样是屏幕顶部那条 4px 细边。
                // 「允许隐藏」这一项统一由 CanAutoHideNow 回答（三模式单一真源）：
                // 焦点离开时 / 暂停播放后 / 全屏时，三者互相独立、可任意组合，都是「放宽允许隐藏的条件」。
                // 三项都在它内部合成，所以这里不再重复写。
                // Toast 的 `!isToastActive` 必须原样保留 —— Toast 是「系统主动弹出且需要用户交互」的，
                // 任何自动隐藏开关都不能把它压掉。全屏时也一样：用户开这个功能的初衷就是
                // 「既能不被打扰、又不漏通知」，所以全屏下收到消息岛体照样要弹出来。
                // 剪贴板与插件详情页同理。
                // `!_media.IsDragging` 是给「暂停后隐藏」配的保护：部分播放器在 seek 期间会短暂上报
                // Paused，若不挡住就会在用户拖进度条拖到一半时把面板抽走。
                // 只在媒体激活时才可能为 true，所以对原有「无媒体」路径零影响。
                // 注：`_isManuallyExpanded` 依旧优先 —— 用户主动点顶部细边唤醒出来的岛体，不会被自动收走
                // （要收就点岛外，走 CollapseAllExpanded）。这是「手动展开优先」的既有语义，刻意保留。
                // 全屏场景同理：真在全屏里点了顶边唤回，就说明他想看，别立刻又藏回去。
                // 同理 `!Renderer.IsMediaExpanded`：用户主动点开的媒体展开面板，不该被暂停 / 全屏抽走。
                // 鼠标离开岛体时 RequestPanelCollapse() 会把它收掉，那时才轮到自动隐藏接手。
                // 上面这三项（手动展开 / 媒体面板 / 详情页）现在统一由 HasAnyExpanded 表达 —— 单一真源，
                // 与穿透淡出共用；将来再新增展开态（例如新的独立面板）只需改它一处。
                bool shouldHide = CanAutoHideNow && !_media.IsDragging && !HasAnyExpanded && !isToastActive
                                  && !isClipboardActive;

                // Y 轴的位移量必须基于「岛体自身的高度」计算，不能写死某个折叠态高度：
                // 媒体展开面板（130 / 158）会明显撑高岛体，若仍按折叠态高度算，
                // 就会多露出「面板高 − 折叠高」的尾巴，全屏看视频时正好挡视野。
                // 取 `Math.Min(_currentHeight, _targetHeight)` =「尺寸动画结束后岛体的高度」：
                // · 岛体正在长高（媒体刚接管）时取当前值 → 露出尾巴恒为 4px；
                // · 岛体正在收缩（收起 320×130 的媒体展开面板 / 关闭插件详情页）时取目标值，
                // 否则会按旧的大高度算出一个很深的位移，把岛体先弹飞再落回。
                // 隐藏位移量还必须加上灵动岛专属的下沉高度，否则藏不进屏幕。
                float currentTopY = 12f * _currentStyleProgress;
                float settledHeight = Math.Min(_currentHeight, _targetHeight);

                // 隐藏方式由「岛体垂直基准」（Renderer.IslandBaseY，位置自定义的唯一真源）决定：
                // · 基准贴顶（默认）→ 上移法：整窗顶出目标显示器上边缘、留 4px 细边，点细边唤醒（现状）
                // · 基准离开顶部      → 上移法会在屏幕中间留下一条 4px 岛体残影（而且岛体会从屏幕中间
                // "飞"到顶部再消失），所以改用「完全隐藏」：原地整块淡出到 0% 透明
                // （全透明像素会被 Windows 判定为物理穿透），唤醒入口复用岛体正中的
                // 唤醒按钮（与穿透睡眠态同一颗）。
                // 两者互斥，贴顶时 FullHideAlpha 恒为 1 → 线上行为与本改动前完全一致。
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
                    // 与岛体尺寸变化 / 形态切换同款的 Q 弹弹簧，不再是原来的三次缓入缓出。
                    // 过冲会朝目标方向多走约 15.9%：隐藏时多缩一点（反正在屏幕外），
                    // 唤回时会往下多沉一点再弹回顶边 —— 这就是想要的「位移动画」。
                    _currentY = (float)(_startY + (_targetY - _startY) * SpringEase(elapsedY));
                }
            }

                // 「完全隐藏」不透明度：只在「基准离开顶部 + 该隐藏」时淡到 0，其余情况恒为 1。
                // 平滑节奏与穿透那套保持一致（0.18 + 归零钳制），避免小浮点让 Windows 判定不出全透明。
                float targetFullHide = shouldHide && !slideOutHide ? 0f : 1f;
                Renderer.FullHideAlpha += (targetFullHide - Renderer.FullHideAlpha) * 0.18f;
                if (Renderer.FullHideAlpha < 0.01f) Renderer.FullHideAlpha = 0f;
                if (Renderer.FullHideAlpha > 0.99f) Renderer.FullHideAlpha = 1f;

                // 二维 (X轴宽度与Y轴高度) 弹簧动画逻辑
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
                // 插件行独立占据岛体最右侧：非组合模式下恒定追加其预留宽度，
                // 因此不论待机显示什么内容、媒体是否开启，插件都会稳定显示在原生内容之后。
                // 组合模式下插件已并入「内容顺序表」与原生模块混排，宽度由 GetCompositeWidth 一并算出，
                // 因此不再额外追加插件预留宽度；其余模式仍按整行贴在右侧预留。
                // 插件详情页展开时：岛体尺寸完全由详情页决定（插件通过 MeasureWidth/MeasureHeight 指定），
                // 此时忽略原生内容与插件行的预留宽度，岛体只显示详情页内容。
                // Toast 优先于详情页（通知到来时先显示通知，通知结束后详情页自动回来）。
                float detailW = 0f, detailH = 0f;
                bool detailOpen = !isToastActive && !isClipboardActive && Renderer.TryGetDetailPageSize(out detailW, out detailH);

                // 原生内容（不含插件行）本帧需要多宽：媒体激活时，长歌词会自适应把岛体撑宽
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

                    // 译文第二行：文字区宽度要容得下更宽的那一行，否则长译文会被遮罩截掉半句
                    if (Renderer.IsTranslationLineVisible(_media))
                        textWidth = Math.Max(textWidth, Renderer.MeasureLyricTranslationWidth(_media.CurrentLyricTranslation));

                    // 文本区长度不再单独封顶（媒体控制器长度完全放开，多长都无所谓）。
                    // 原先这里夹了一个 MEDIA_TEXT_MAX_WIDTH（480 ≈ 27 个汉字），长歌词先撞到它 →
                    // 超出部分被文字渐隐遮罩截断，而且原生内容宽度被钉在 595，
                    // 插件行预算 = 800 − 595 = 205 被吃光 → 装不下的插件整帧不显示
                    // （表现为「多出来的插件在灵动岛上就直接不显示」、
                    // 「这个长度只显示这个插件，另一个长度只显示另一个插件」）。
                    // 现在只受下面的 MAX_ISLAND_WIDTH（已放宽到 1920）约束，真实歌词行远达不到。
                    nativeWidth = Math.Max(nativeWidth, textWidth + 115f);
                }
                nativeWidth = Math.Min(nativeWidth, Renderer.MAX_ISLAND_WIDTH); // 岛体总长上限（1920），窄屏也不会被撑破

                // 插件行取舍：按「组件声明的所需宽度能否完整落进剩余空间」判定。
                // 每个组件通过 IWidget.MeasureWidth 声明「完整显示我的内容需要多宽」，
                // 宿主用「岛体总长上限 − 原生内容本帧占用宽度」得出插件行预算，逐个贪心放行：
                // 装得下的组件完整显示，装不下的组件本帧整体不显示 —— 宿主绝不替它压缩或截断，
                // 所以不会出现「文字被省略号砍掉半截」这种显示不全的情况。
                // 原生内容（尤其是开着媒体控制 + 长歌词自适应）一样照常显示，岛体也不会被撑过上限。
                // 例外：媒体控制面板展开（IsMediaExpanded）时插件行整体不显示 —— 那是块独立面板，
                // 插件贴上去只会把面板和岛体一起撑宽，见下面的分支。
                // 该例外对组合模式同样生效：组合模式现在也能展开媒体面板，
                // 展开期间岛体只剩面板，插件行与其它原生模块本帧都不参与。
                bool mediaPanel = currentActive && Renderer.IsMediaExpanded;

                float pluginReserve = 0f;
                if (mediaPanel)
                {
                    Renderer.SetPluginRowBudget(0f);
                }
                else if (Renderer.CompositeModeEnabled)
                {
                    // 组合模式：插件已并入「内容顺序表」与原生模块混排。预算必须知道「一整行原生模块」的总宽，
                    // 所以先量原生（不含插件）、定好预算，随后算含插件的总宽时就会按它放行。
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - Renderer.GetCompositeNativeWidth(_media));
                }
                else if (!isToastActive && !isClipboardActive && !detailOpen)
                {
                    Renderer.SetPluginRowBudget(Renderer.MAX_ISLAND_WIDTH - nativeWidth);
                    pluginReserve = Renderer.GetPluginRowReserve();
                }
                else
                {
                    // 通知 / 剪贴板 / 详情页：整块岛体被接管，本帧不给插件行任何宽度。
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
                    // 剪贴板链接面板：沿用媒体控制器同款尺寸（高度一致，宽度按链接长度自适应并封顶）
                    expectedTargetWidth = Renderer.GetClipboardAutoWidth(_clipboardUrl!);
                    expectedTargetHeight = Renderer.MEDIA_HEIGHT;
                }
                else
                {
                    // 媒体展开面板：宽度锁定面板尺寸（nativeWidth 此时恒为 320、pluginReserve 为 0），
                    // 组合模式与非组合模式同一条算式 —— 不再因为「组合模式」而去累加模块宽度。
                    // 组合模式走渲染器里的像素级精确动态宽度计算，拒绝任何多余空白与错位；
                    // 其余模式 = 原生内容宽度 + 插件行预留（插件行放不下时预留已归零）
                    expectedTargetWidth = Renderer.CompositeModeEnabled && !mediaPanel
                        ? Renderer.GetCompositeWidth(_media)
                        : nativeWidth + pluginReserve;

                    // 折叠态高度只有一个真源（MEDIA_HEIGHT = 全局折叠态高度），待机与媒体折叠态不再各用各的；
                    // 只有媒体展开面板才另按 GetExpandedHeight 撑高。
                    expectedTargetHeight = currentActive && Renderer.IsMediaExpanded
                        ? Renderer.GetExpandedHeight(_media)
                        : Renderer.MEDIA_HEIGHT;
                }

                // 注意：不要再把目标宽度喂给渲染侧去「按动画进度缩放插件行预留」。
                // 那个做法（曾用 Renderer.IslandTargetWidth + GetScaledPluginReserve）会在
                // 媒体控制器长度变化时把预留瞬间缩小：换歌词 / 换标题 → 目标宽度变大 →
                // 缩放系数从 1 掉下来 → 插件行与原生内容边界整体挪一下再挪回去，
                // 表现就是「插件闪现回原位又闪回来」。这套做法曾经引发该现象，已整套删除。
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

                    if (elapsed >= SpringDurationSeconds)
                    {
                        _isAnimating = false;
                        _currentWidth = _targetWidth;
                        _currentHeight = _targetHeight;
                    }
                    else
                    {
                        double spring = SpringEase(elapsed);

                    // X 和 Y 同步套用一个物理弹性引擎，保证视效极度统一协调
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

            // ---- 4. 渲染调用更新 ----
            // 卡顿自检的计时起点。岛体的「卡死」只可能有一个来源 —— 渲染线程被谁挡住了，
            // 但那一段藏着几十个调用，光看现象只能猜（本次「切歌卡死」就是这样：
            // 躲在 UpdateLyrics 里的 SMTC COM 调用肉眼看不见）。分三段计时后一眼能定位。
            long tFrameStart = Environment.TickCount64;
            long tLyricDone = tFrameStart, tDrawDone = tFrameStart;

            var canvas = _renderSurface!.Canvas;
            canvas.Clear(SKColors.Transparent); // 清空上一帧的残留

            // 存档矩阵状态，避免缩放无限叠加
            //
            // Save / Restore 必须自己兜住异常：`Renderer.Draw` 内部还有三级
            // Save（含一次 `SaveLayer` 整窗离屏层 ≈2MB，高 DPI 下更大），全靠它自己的出口配平。
            // 一旦某个媒体属性抛异常（`Thumbnail` 被并发 Dispose 后访问、COM 对象已断开……）穿过
            // Draw 冒到这里，本帧的 save 就永久留在画布栈上：离屏层被栈钉住不释放，
            // 渲染循环是每 16ms 一次 —— 每帧漏一层就是每秒几十 MB，几分钟内就能把内存吃光。
            //
            // 关键是"回滚到基线"而不是"Restore 一次"：异常可能发生在 Draw 内部的第 N 级 save 之后，
            // 弹一层只能退掉最外那层，里面几层照样留着。所以记下进入 Draw 之前的 SaveCount
            // （刚做完上面那次 Save，即基线），catch 里用 RestoreToCount 一次性退回基线。
            canvas.Save();
            int saveBaseline = canvas.SaveCount;
            try
            {
                // 让底层 C++ 引擎接管坐标放大
                canvas.Scale(_dpiScale);

                _media.UpdateLyrics(); // 更新歌词
                tLyricDone = Environment.TickCount64;

                // 传入 currentHeight 和 _currentToast
                Renderer.Draw(canvas, _media, _isHovered, _currentWidth, _currentHeight, startupProgress, _currentBars, _currentToast, _currentStyleProgress, transitionAlpha, isClipboardActive ? _clipboardUrl : null);
                tDrawDone = Environment.TickCount64;

                // 恢复原始矩阵状态
                canvas.Restore();
            }
            catch
            {
                // 绘制中途失败：把画布保存栈退回到进入 Draw 之前的基线再往外抛
                //（外层 finally 负责复位 _isRendering）。RestoreToCount 本身也可能抛
                //（画布已被释放），所以吞掉它 —— 此时能做的只有别让栈继续涨。
                try { canvas.RestoreToCount(saveBaseline); } catch { }
                throw;
            }

            UpdateWindow();

            // 卡顿自检：整帧超过阈值就把三段耗时落一条 WARN（一秒最多一条，避免刷屏）。
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

        /// <summary>单帧超过它就记一条卡顿日志（正常帧 16ms 上下，120ms 已是肉眼可见的顿挫）。</summary>
        private const long FrameStallLogMs = 120;

        /// <summary>上一条卡顿日志的时刻，用于限流（Environment.TickCount64）。</summary>
        private long _lastStallLogTick;

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
            if (screenDc == IntPtr.Zero) return;

            // 从取到 DC 到归还之间不许有裸异常路径：
            // 中间那句 UpdateMonitorBounds() 会走 Screen.AllScreens（多屏热插拔时可能抛），
            // 一旦它抛出，这一帧的 screen DC 就再也回不去 —— 每帧一次，句柄很快见底。
            // 包成 try/finally 后，无论中间发生什么，DC 一定归还。
            try
            {
                var ptSrc = new Win32.POINT(0, 0);
                var ptDst = new Win32.POINT { x = 0, y = 0 };

                if (_cachedMonitorIndex != Renderer.TargetMonitorIndex) UpdateMonitorBounds();
                ptDst.x = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
                // 垂直基准走 Renderer.IslandBaseY（岛体位置自定义的唯一真源，默认 0 = 贴顶）
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

        /// <summary>
        /// 拖放离开岛体（或拖放被取消）时由 IslandDropTarget 调用。
        ///
        /// 为什么需要它：拖放期间鼠标被 OLE 的拖放循环接管，窗口收不到 WM_MOUSELEAVE，
        /// 于是「鼠标离开岛体 → 挂延迟折叠」这条常规路径整个被跳过了。
        /// 不在这里补一刀，详情页就会一直停在「正在拖入」的样子 —— 高亮不灭、也不走折叠计时，
        /// 看起来就是「卡在拖入」。
        /// </summary>
        internal void NotifyDragExit()
        {
            if (IsCursorOverIslandNow())
            {
                // 鼠标还在岛上（拖放刚被 Esc 取消之类）：撤销可能挂起的折叠就好
                CancelPanelCollapse();
                return;
            }

            RequestPanelCollapse();

            // 到这里刻意不去动 _isHovered，原因很关键：
            // _isHovered 只由 WM_MOUSEMOVE 的「首次进入」分支（if (!_isTrackingMouse)）置 true、
            // 由 WM_MOUSELEAVE 置 false。拖放期间这两个消息都被 OLE 吞掉了，所以它现在可能不准。
            // 而拖放结束时 _isTrackingMouse 已经是 true —— 一旦在这里把它置成 false，
            // 鼠标哪怕还停在岛上，也再没有任何消息会把它恢复（首次进入分支不会再走）。
            // 后果是 WM_LBUTTONDOWN 里 `if (_isHovered && HasActiveDetailPage)` 这道门永远过不去，
            // 详情页彻底收不到左键 —— 表现就是「拖不动、也点不动」。
            // 悬停标志交给系统消息自己维护，这里只负责面板折叠时序。

            // 但要做这件事：把 TrackMouseEvent 的订阅强行作废。
            // 系统对 WM_MOUSELEAVE 是「只发一次、发完即失效」的，而拖放期间那次它发给了被 OLE
            // 接管的消息循环、我们根本没收到。订阅已经消耗掉、_isTrackingMouse 却还停在 true，
            // 于是「鼠标离开」永远不会再被检测到，_isHovered 也会一直挂着。
            // 置 false 之后，下一次 WM_MOUSEMOVE 会重新走「首次进入」分支，把状态拉回正轨。
            // （WM_MOUSEMOVE 按岛体可见形状派发，分层窗口的透明像素不吃消息，所以这一支是可信的。）
            _isTrackingMouse = false;
        }

        /// <summary>
        /// 鼠标当前是否真的落在岛体可见矩形内。换算口径与渲染循环里那段「岛外点击」轮询一致，
        /// 别单独改其中一处 —— 两边不一致就会出现「判定说在岛外、实际在岛上」这类鬼问题。
        /// </summary>
        private bool IsCursorOverIslandNow()
        {
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float topY = 12f * _currentStyleProgress;

            Win32.GetCursorPos(out var pt);
            float x = (pt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
            float y = (pt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;

            return x >= left && x <= left + _currentWidth && y >= topY && y <= topY + _currentHeight;
        }

        // ---- 岛体拖放（右键展开的详情页拖入 / 拖出） ----
        // 岛体是个纯自绘的分层窗口，原本只处理鼠标与键盘消息，所以详情页收不到任何拖入事件。
        // 这里给它挂一个 OLE 的 IDropTarget，把文件拖放转发到「当前展开的详情页」。
        // 全套逻辑都在岛体之外（IslandDropTarget 判定落点、Renderer 分发），本类只负责登记与坐标换算。

        /// <summary>
        /// 把岛体登记成 OLE 拖入目标。只登记一次；失败也只是「详情页不能拖放」，
        /// 不影响岛体的任何既有功能，所以整段包在 try 里。
        /// </summary>
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

        /// <summary>窗口销毁前摘掉 OLE 那边的登记（失败也无所谓，进程随后就退了）。</summary>
        private void RevokeIslandDropTarget()
        {
            if (_islandDropTarget == null) return;
            _islandDropTarget = null;
            try { Win32.RevokeDragDrop(_hwnd); } catch { /* 窗口已销毁 */ }
        }

        /// <summary>
        /// 把拖放的屏幕坐标换算成「详情页 / 组件」口径的岛内逻辑坐标 ——
        /// 必须与鼠标点击那一套完全一致（见 WM_MOUSEMOVE 里的 mx/my 与 hitTopY）。
        /// 少任何一步，落点就会整体偏移，表现为「拖到卡片左边却删掉了右边那张」。
        /// </summary>
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

        /// <summary>
        /// 拖放进行中时续一口「鼠标还在岛上」。
        ///
        /// 拖放期间鼠标被 OLE 的拖放循环接管，窗口收不到 WM_MOUSEMOVE，也就刷不到 _isHovered；
        /// 而详情页有「鼠标移开就收起」的延迟计时 —— 不续这一口，面板会在拖放途中把自己收掉，
        /// 拖放目标当场消失（用户看到的就是「拖到一半面板没了」）。
        /// </summary>
        internal void KeepAliveForDrop()
        {
            _isHovered = true;
            CancelPanelCollapse();
        }

        /// <summary>
        /// 在岛体上发起一次系统拖放（详情页把条目「拖出去」时用）。阻塞到用户松手或按 Esc 取消。
        ///
        /// 这个方法会在主线程里进入 OLE 的模态循环，但宿主的渲染由独立的渲染线程驱动（见 _renderThread），
        /// 所以这段时间岛体动画照常，不会卡死。
        /// </summary>
        public static bool StartFileDragOnIsland(IReadOnlyList<string> paths, bool allowMove = false)
        {
            var hwnd = InstanceHandle;
            if (hwnd == IntPtr.Zero || paths == null || paths.Count == 0) return false;

            // 过滤掉已不存在的路径：把一条硬盘上已经没有的路径丢进拖放，目标只会报错或毫无反应
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
                // CF_HDROP 的封装交给 WinForms 的 DataObject（SetData(FileDrop, string[]) 是它的标准用法）
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

                // 拖出结束：拖放期间 OLE 接管鼠标，窗口收不到 WM_MOUSELEAVE。
                // 用户若是拖到岛外松手（正常拖走的情形就是如此），悬停态与折叠计时都会卡住，
                // 这里补一次真实判定把它掰回来。
                _liveInstance?.NotifyDragExit();
            }
        }

        /// <summary>
        /// 窗口过程的异常兜底。WndProc 是最外层回调，没有调用方能接住异常 —— 一旦逃出去进程立刻退出，
        /// 用户看到的就是「莫名闪退」。这里记日志后吞掉，坏的只是这一次交互。
        /// 注册窗口类时挂的是这个方法，不是 WndProc 本身。
        /// </summary>
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
                // 剪贴板内容变化（事件驱动，仅在复制/剪切导致剪贴板内容变化时触发一次读取；开关关闭直接忽略）
                case Win32.WM_CLIPBOARDUPDATE:
                    if (IsClipboardEnabled) _clipboardMonitor.HandleClipboardUpdate();
                    return (IntPtr)0;

                // 媒体全局快捷键：不管前台是谁都会投到这里，按注册 id 分派给媒体控制器。
                // 吃下消息即可（DefWindowProc 对 WM_HOTKEY 没有额外处理）。
                case Win32.WM_HOTKEY:
                    MediaHotkeys.Handle(Win32.Low32(wParam));
                    return (IntPtr)0;

                case Win32.WM_DESTROY:
                    // 窗口销毁前反注册剪贴板监听，避免系统继续向已销毁窗口投递消息
                    _clipboardMonitor.Detach();
                    // 全局热键同理：句柄一失效，系统里的注册就再也撤不掉了，会一直占着那几组键
                    MediaHotkeys.Detach();
                    // 同理：OLE 那边还捏着一个指向本窗口的拖入目标，销毁前必须摘掉
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
                            // 鼠标回到岛上 → 取消两块展开面板挂起的延迟折叠（还没到期就当没发生过）
                            CancelPanelCollapse();
                        }

                        // 统一提炼坐标，大括号隔离作用域，彻底告别编译报错
                        int mx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int my = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        // 记录鼠标逻辑坐标，供插件组件的悬停判定使用
                        Renderer.UpdatePluginMouse(mx, my);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 详情页展开时把鼠标移动也转给它 —— 插件靠「按下之后位移超过阈值」来发起拖出，
                        // 没有这条就只能在按下那一瞬间进拖放循环，普通单击会被当成拖拽。
                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseMove(mx, my - hitTopY);

                        // 1. 最高优先级拦截：唤醒按钮热区（位置真源在 Renderer.WakeButtonX，与渲染共用）
                        // 两种「整块不可见」的形态都要短路：穿透睡眠态、完全隐藏态（岛体基准离开顶部）
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

                        // 插件详情页展开时跳过原生悬停判定：详情页内容与交互完全由插件自己负责
                        if (Renderer.HasActiveDetailPage)
                        {
                            _isCursorOverIcon = false;
                            break;
                        }

                        // 时间轴拖动进行中：最优先接管（此时已 SetCapture，鼠标可能早已移出岛体）。
                        // 只改本地缓存，不打任何 COM / IO —— 这是频繁拖动不卡顿的关键。
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
                                // 悬停与点击共用 Renderer.HitExpandedButton 一套几何：
                                // 高亮圈、手型指针、可点范围三者完全重合，不再出现「亮着却点不动」。
                                int hoveredBtn = Renderer.HitExpandedButton(mx, my - hitTopY, _currentHeight);
                                Renderer.HoveredExpandedButton = hoveredBtn;
                                // 悬停到时间轴上也要切小手（y 需扣掉岛体下沉偏移，与 Draw 共用同一套坐标）
                                _isCursorOverIcon = hoveredBtn != -1 || Renderer.HitTimeline(mx, my - hitTopY);
                            }
                            else
                            {
                                if (Renderer.MediaExpandByLeftClick)
                                {
                                    // 展开入口是左键单击（「双击封面跳转应用」关掉，左键空闲）：
                                    // 媒体模块整块就是「点下去会展开」的热区 → 给小手。
                                    // 判据与 WM_LBUTTONDOWN 的展开分支同源（HitMediaZone + 高度范围），
                                    // 组合模式下用渲染时登记的真实区间，点时钟 / 硬件不会误判。
                                    _isCursorOverIcon = Renderer.HitMediaZone(mx)
                                        && my >= hitTopY && my <= hitTopY + _currentHeight;
                                }
                                else if (Renderer.MediaExpandByRightClick)
                                {
                                    // 展开入口是右键（「双击封面跳转应用」开着，左键留给双击跳转）：
                                    // 折叠态左键什么也不做，所以不给小手 —— 手型是「点下去有反应」的承诺。
                                    // 悬停高亮的播放控件本来也只在直接交互模式下画（见 Renderer.MediaWidget），
                                    // 两种口径在这里保持一致。
                                    _isCursorOverIcon = false;
                                }
                                else
                                {
                                    // 折叠态按钮同样走共用的命中几何（Renderer.HitInlineButton），
                                    // 锚点是渲染器给出的媒体模块真实右边界（组合模式下插件可能排在媒体右边）
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
                    // 唤醒那次点击到此结束：解除岛外收起的抑制，之后用户再点岛外照常收起。
                    // 必须放在最前面 —— 上面拖动分支会 return，别让标记挂在拖动路径上漏掉。
                    _suppressOutsideCollapse = false;
                    // 详情页展开时把「抬起」也转给它（按住拖出的收尾全靠这条）。同样放在最前面，
                    // 免得被下面媒体拖动分支的 return 漏掉。
                    if (Renderer.HasActiveDetailPage)
                    {
                        int ux = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int uy = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                        Renderer.DispatchDetailPageMouseUp(ux, uy - 12f * _currentStyleProgress);
                    }
                    // 松手：解除状态锁并把落点提交给播放器（拖动期间攒下的所有改动只在这一刻提交一次）
                    if (_media.IsDragging)
                    {
                        _media.EndDrag();
                        Win32.ReleaseCapture();
                        // 拖到岛外松手：拖动期间的 WM_MOUSELEAVE 被上面「拖动中不收起」的分支吃掉了，
                        // 松手后系统不会再来第二次，这里补一次判定，免得面板挂在岛外一直不收。
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
                        // 拖动中（已 SetCapture）：不收起岛体、也不解除状态锁，松手统一交给 WM_LBUTTONUP。
                        // 若消息丢失导致左键其实早已抬起，这里兜底解锁，避免进度条永久卡在拖动态。
                        if (_media.IsDragging)
                        {
                            if ((Win32.GetAsyncKeyState(0x01) & 0x8000) != 0) break;
                            _media.EndDrag();
                            Win32.ReleaseCapture();
                        }
                        // 详情页展开时也通知一次「鼠标离开」：按下之后把鼠标拖出岛体再松手，
                        // WM_LBUTTONUP 不会来，详情页只能靠这条复位「按住」状态。
                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseLeave();

                        // 鼠标离开灵动岛 → 展开的面板一律自动折叠（媒体面板与插件详情页同一条管线，见 RequestPanelCollapse）。
                        // WM_MOUSELEAVE 由系统按「岛体可见形状」派发（分层窗口的透明像素不吃鼠标消息），
                        // 所以这里判定等价于「鼠标真的离开了灵动岛」，不用轮询。
                        // 注意：时间轴拖动中已在上面的分支里 break 掉，不会误伤正在拖动的面板。
                        RequestPanelCollapse();
                        break;
                    }

                case Win32.WM_LBUTTONDBLCLK:
                    {
                        // 双击封面（折叠态是媒体模块左半边那一格、展开态是那块封面，两种形态同一条判据）
                        // → 跳回正在放媒体的那个应用。
                        //
                        // 与折叠态单击的关系：跳转开着时折叠态左键单击什么都不做（那时展开入口是右键，
                        // 左键只负责双击跳转），所以不存在「第二下被展开吃掉」的问题，也不需要分辨单双击 ——
                        // 一次干净的双击直接跳转。
                        // 跳转关掉时这个分支整体不消费（launchEnabled = false）：那时折叠态左键单击
                        // 是展开入口（见 WM_LBUTTONDOWN），双击退化成两次普通单击 —— 第一次已经展开面板，
                        // 第二下落在展开面板上同样什么都不做，不会有意外的副作用。
                        // 落在不合法的地方（标题 / 歌词 / 频谱 / 时间轴 / 播放按钮 / 通知 / 剪贴板接管期间）
                        // 就完全不消费，消息继续往下走，双击退化成两次普通单击，不引入任何新行为。
                        //
                        // 折叠态左右两半的归属（与 HitMediaLaunchZone 同口径）：
                        // · 左半边（媒体模块左半，整条高度都算，不是只有缩略图那一小块）：双击 → 跳转；
                        // · 右半边（频谱那一带）：双击热区压根不覆盖，永远只走原有交互
                        // （直接交互模式下是悬停显示播放控件；展开交互模式下看跳转开关：
                        // 开着 → 右键展开、左键不做事；关掉 → 左键单击展开）；
                        // · 展开态：封面那一格双击 → 跳转。
                        int dx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int dy = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);

                        // 岛体「整块不可见」的两种形态（穿透睡眠态 / 完全隐藏态）：双击只当唤醒用，
                        // 与 WM_LBUTTONDOWN 里 HitWakeButton 的优先级保持一致，不在这里触发跳转。
                        if ((Renderer.PassthroughModeEnabled && !_isPassthroughAwake) || Renderer.FullHideAlpha < 0.99f) break;

                        // 插件双击（左键）：注册接收双击的详情页 / 组件优先拿走这次双击。
                        // 排在媒体跳转与待机切换之前 —— 插件组件画在岛体插件行、媒体封面在左端，
                        // 常态不重叠；组合模式下两者可能相邻，这里按「插件优先」定序（那块是插件自己画的，
                        // 它主动声明要双击，语义比宿主的内置手势更明确）。
                        // 没声明接收双击的插件一个字节都不受影响：两条分发都会立刻返回 false。
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

                        // 待机模式切换（开关打开时）：默认态双击「空白」进入；待机态双击空白退出 ——
                        // 而场景选「折叠媒体控制」时岛内被媒体模块占满、没有空白，改用双击整块媒体区退出。
                        // 判定排在插件分发与封面跳转之后，插件组件上与封面上永远不会触发。
                        if (Renderer.StandbyToggleByDoubleClick && _isHovered
                            && _currentToast == null && !isClipboardActive
                            && !Renderer.HasActiveDetailPage)
                        {
                            // 进入 / 退出的命中区：
                            //   · 非待机态：双击空白进入；
                            //   · 待机态 + 场景 = 媒体控制：岛内被媒体模块占满、没有空白，退出认整块媒体区
                            //     （折叠态与展开态都算 —— 单击那一块是展开面板，双击才是退出待机）；
                            //     而当前没有媒体播放时岛上退化成空白（媒体模块压根没画、热区不存在），
                            //     这时改认空白 —— 两条合起来保证任何情况下都退得出来；
                            //   · 待机态 + 场景 = 时间 / 空白：双击空白退出。
                            bool onBlank = Renderer.IsBlankAt(dx, dy, _currentHeight);
                            bool hitsToggleZone = Renderer.StandbyActive && Renderer.StandbyScene == 3
                                ? Renderer.HitMediaZone(dx) || onBlank
                                : onBlank;

                            if (hitsToggleZone)
                            {
                                Renderer.StandbyActive = !Renderer.StandbyActive;
                                // 与设置页里点「显示模式」同一个口径：待机 / 普通要跨重启保留
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
                            // 岛体此刻处于「上移隐藏」态时，只解除穿透睡眠是不够的：shouldHide 仍为真、
                            // _currentY 不动，岛体会一直留在屏外回不来。一并锁上「手动展开」并屏蔽本次
                            // 按键引发的岛外收起判定 —— 与下面那条「上移隐藏态唤回」分支完全同源。
                            if (_currentY < -5f)
                            {
                                _isManuallyExpanded = true;
                                _suppressOutsideCollapse = true;
                            }
                            return (IntPtr)0;
                        }

                        // 完全隐藏态（岛体基准离开顶部）：点岛体正中的唤醒按钮唤回，与穿透睡眠态同一条路径。
                        // 复用「手动展开」标记锁住显示 —— shouldHide 本来就排除 _isManuallyExpanded，
                        // 所以岛体立刻淡回可见；之后点岛外由既有的兜底轮询收回（无需新状态位）。
                        if (Renderer.FullHideAlpha < 0.99f && HitWakeButton(cx, cy))
                        {
                            _isManuallyExpanded = true;
                            return (IntPtr)0;
                        }

                        RaiseWindowClicked(cx, cy, "main-window");

                        // 「点击已隐藏的岛体把它唤回来」。
                        // 判据必须与 shouldHide 同源，一律读 CanAutoHideNow：
                        // 它已经含非穿透模式判定（刚开启穿透时岛体可能还在回滑动画里、_currentY 仍 < -5，
                        // 此时点击不该被当成「唤醒」而莫名锁上手动展开），
                        // 也含「暂停后隐藏」与「全屏时隐藏」两种放宽模式 —— 写死 `!_media.IsActive`
                        // 会导致那两种模式下「藏得下去、点不回来」。
                        // `_currentY < -5f` 是「确实已经藏起来了」的兜底闸门。
                        if (CanAutoHideNow && _currentY < -5f)
                        {
                            _isManuallyExpanded = true;
                            // 屏蔽掉「本次按键」引发的岛外点击收起判定。用户点的是屏幕顶边（y≈0），
                            // 而岛体下沉后可见区从 y=12 起，所以这次点击坐标天然在岛体之外；
                            // 不屏蔽的话岛刚滑回来就会被上面那段兜底轮询收走 —— 「抽一下又回去」。
                            _suppressOutsideCollapse = true;
                            return (IntPtr)0;
                        }

                        // 通知（Toast）点击：岛体此刻整块被通知占着，把这一下交给通知自己的回调。
                        // 只有插件提醒会带回调（ReminderData.OnClick）；系统通知的 OnClick 为 null，
                        // 那种情况原样往下走 —— 点击行为与改动前完全一致（点了等于没点）。
                        if (_isHovered && _currentToast != null
                            && cy >= hitTopY && cy <= hitTopY + Renderer.TOAST_HEIGHT)
                        {
                            var toastClick = _currentToast.OnClick;
                            if (toastClick != null)
                            {
                                // 先收起这条通知再回调：回调里多半要弹面板 / 再发一条提醒，留着会打架。
                                // （与超时清理同一套收尾：摘掉当前通知，下一帧岛体自然回到正常内容。）
                                _currentToast = null;
                                _toastEndTime = default;
                                try { toastClick(); }
                                catch (Exception ex) { Logger.Error("[NotchWindow] 插件提醒点击回调异常", ex); }
                                return (IntPtr)0;
                            }
                        }

                        // 剪贴板链接面板：命中右侧「打开」按钮 → 默认浏览器打开链接
                        if (isClipboardActive && Renderer.HitClipboardOpen(cx, cy - hitTopY))
                        {
                            OpenClipboardUrl();
                            return (IntPtr)0;
                        }

                        // 插件详情页展开时：岛内左键优先交给详情页。
                        // 先走新的「鼠标事件」通道（插件靠按下 + 移动的位移来发起拖出），
                        // 再走老的 HitTest / OnAction（详情页的 HitTest 返回 None 时会自然跳过）。
                        // 即使两边都没命中也消费掉这次点击，避免误触到底层原生媒体按钮。
                        if (_isHovered && Renderer.HasActiveDetailPage)
                        {
                            Renderer.DispatchDetailPageMouseDown(cx, cy - hitTopY);
                            Renderer.DispatchDetailPageClick(cx, cy - hitTopY);
                            return (IntPtr)0;
                        }

                        // 插件组件左键交互：命中插件绘制区则交给插件决定做什么，不再走媒体控制逻辑
                        if (_isHovered && _currentToast == null && !isClipboardActive && Renderer.DispatchPluginLeftClick(cx, cy - hitTopY))
                        {
                            return (IntPtr)0;
                        }

                        if (_isHovered && _media.IsActive && _currentToast == null && !isClipboardActive)
                        {
                            // 待机模式选「媒体控制」时不再特判右半边：整块媒体区（含频谱那一带）左键都展开，
                            // 退出待机走双击（见 WM_LBUTTONDBLCLK，热区同为整块媒体区）。
                            // 曾经的「右半边左键显式消费」是为了保住双击退出，代价是那一半点了没反应 ——
                            // 悬停给小手、点下去却什么都不发生，与被消费的那一下正好凑成这个 bug。

                            // 命中时间轴：进入拖动并锁住鼠标，同时消费这次点击
                            // （不能落到下面「点媒体区就展开」的那条分支）
                            if (Renderer.IsMediaExpanded && Renderer.HitTimeline(cx, cy - hitTopY)
                                && _media.BeginDrag(Renderer.TimelineRatio(cx)))
                            {
                                Win32.SetCapture(hwnd);
                                return (IntPtr)0;
                            }

                            // 播放控件：展开态走面板底部那三颗，折叠态只在直接交互模式下有
                            // （见 Renderer.MediaWidget.cs 的 DrawMediaInline）。三条分支互斥，都是
                            // else-if —— 直接交互模式（总闸关闭）永远走不到下面的展开分支，与
                            // 「不提供展开入口」的口径一致。
                            if (Renderer.IsMediaExpanded)
                            {
                                // 与悬停高亮共用同一套命中几何（见 Renderer.HitExpandedButton）：
                                // 高亮在哪儿，点下去就一定生效，不再有「亮着却点不动」的空隙。
                                switch (Renderer.HitExpandedButton(cx, cy - hitTopY, _currentHeight))
                                {
                                    case 0: _media.Previous(); break;
                                    case 1: _media.TogglePlayPause(); break;
                                    case 2: _media.Next(); break;
                                }
                            }
                            else if (Renderer.MediaInteractionMode == 0)
                            {
                                // 折叠态播放按钮只在直接交互模式下绘制（见 Renderer.MediaWidget.cs 的
                                // DrawMediaInline），因此也只有该模式吃这里的点击；展开交互模式不画这两颗按钮，
                                // 点这一带不会命中任何控件（那时的展开入口在下面那条分支 / 右键），
                                // 与「悬停不显示控件」保持一致。
                                // 位置与渲染侧共用同一个锚点：GetMediaRight 返回的就是媒体模块右缘。
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
                                // 「双击封面跳转应用」走 —— 跳转关掉时左键空闲（双击不再跳转），
                                // 所以恢复成「点媒体区就展开」这个最顺手的入口；跳转开着时走上面那条
                                // 右键分支（左键整块留给双击跳转）。
                                // 这里不需要之前那套「等系统双击判定窗口再展开」的排队逻辑：
                                // 排队是为了把同一坐标上的「单击展开」与「双击跳转」分开，而现在跳转是关的，
                                // 第二下不会触发任何事，直接展开即可（也就没有那 500ms 的迟滞）。
                                ExpandPanel(Plugins.BuiltinWidgets.Media);
                                // 与右键展开同一条理由：折叠态岛体可能比 320 的面板更宽（长歌词自适应 /
                                // 组合模式），展开瞬间变窄，按下时还在岛内的坐标可能随即落到岛外，
                                // 被兜底轮询判成「岛外点击」把面板当场收走。
                                _suppressOutsideCollapse = true;
                                Logger.Info($"媒体展开：折叠态左键单击 ({cx},{cy}) 命中媒体区 → 已展开媒体面板"
                                    + "（跳转关闭，右键仍打开设置）");
                                return (IntPtr)0;
                            }

                            // 折叠态的展开入口不再是固定的右键（曾经定的是右键，后来细化为
                            // 「跳转开着才走右键，否则左键单击展开」）：跳转开着时左键只剩
                            // 「双击封面跳转应用」一件事（在 WM_LBUTTONDBLCLK 里），单击天然什么都不做；
                            // 跳转关掉时由上面那条分支展开。
                            //
                            // 跳转开着时不要在这里恢复「点一下即展开」：那正是折叠态左半边单双击
                            // 互相吃掉的根源（缩略图与展开态封面同在岛内左端，第二下会落进封面的双击热区）。
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
                        // 第二下右键到了 → 把「待定中」的这次双击透传给注册接收双击的插件目标
                        // （详情页 / 组件），同时撤销第一下本该执行的默认行为（折叠面板 / 展开详情页）。
                        // 没有待定（目标没注册接收双击、或已经过期）时什么都不做，消息继续落到
                        // DefWindowProc —— 与改动前完全一致，老插件与原生内容一个都不受影响。
                        if (_isHovered)
                        {
                            int bx = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                            int by = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
                            float bTopY = 12f * _currentStyleProgress;
                            if (ConsumeRightDoubleClick(bx, by - bTopY)) return (IntPtr)0;
                            // 第一下已经当「右键单击」透传出去的目标（两档都开）在这里补一次双击 ——
                            // 它们没挂待定，所以要走另一条路重新命中一次。
                            if (Renderer.DispatchPassthroughRightDoubleClick(bx, by - bTopY)) return (IntPtr)0;
                        }
                        break;
                    }
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // ---- 右键双击待定（插件注册接收双击时的「等第二下」）----
        // 命中「注册接收双击」的组件 / 详情页时，第一下右键不立即执行默认行为（展开详情页 / 折叠面板），
        // 而是先挂一个与系统双击判定窗口同源的截止时间：
        //   · 截止前收到 WM_RBUTTONDBLCLK → 撤销待定，把双击通知给插件，默认行为一次都不执行；
        //   · 截止后什么都没来 → 说明用户只想单击 → 重放一次原来的右键处理（fromDoubleClickTimeout = true，
        //     不会再挂待定），行为与没开这个开关时完全一致，只是晚了约一个双击窗口。
        //
        // 为什么必须「等」：Windows 的双击是「第二下按下时」才把消息升格成 WM_RBUTTONDBLCLK 的。
        // 第一下按下时宿主无从知道后面还有没有第二下 —— 想同时保住「单击折叠」与「双击透传」，
        // 只能等一个窗口。代价（单击慢半拍）只落在主动声明接收双击的插件身上，其它目标零变化。
        private static DateTime _rightDblDeadline = DateTime.MinValue;
        private static bool _rightDblOnDetail;
        private static string? _rightDblWidgetId;
        private static float _rightDblLx, _rightDblLy;
        private static int _rightDblRx, _rightDblRy;
        private static float _rightDblTopY;

        /// <summary>
        /// 挂一次右键双击待定。
        /// 窗口 = 系统双击判定间隔（GetDoubleClickTime，默认 500ms）+ 30ms 余量：必须 ≥ 系统值，
        /// 否则系统的第二下升格消息还没到、我们这边已经先执行默认行为了。
        /// </summary>
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

        /// <summary>
        /// 收到 WM_RBUTTONDBLCLK：把待定中的双击透传给注册接收双击的插件目标。
        /// 返回 true = 这次右键归属插件（宿主不执行折叠 / 展开）；false = 没有待定，按原样放行。
        /// </summary>
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
                // 组件用待定时算好的局部坐标：待定窗口期间布局可能已变，重新按矩形换算会错位。
                Logger.Info($"[插件] 右键双击透传 → 组件 {widgetId}");
                return Renderer.DispatchWidgetDoubleClick(true, widgetId, lx, ly);
            }
            return false;
        }

        /// <summary>
        /// 每帧结算右键双击待定。到期 = 用户只按了一下 → 补执行原来的单击行为。
        /// 与 TickPanelCollapse 同一处调用，稳态下只有一次 DateTime 比较。
        /// </summary>
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
                // 详情页上右键单击 = 收起面板（与改动前的语义、以及 forceCloseDetail 的理由完全一致）
                ClosePanelsNow(forceCloseDetail: true);
                return;
            }
            if (widgetId == null) return;

            // 重放一次完整右键处理：组件有详情页（展开）与没有详情页（打开设置窗口 / 直达页签）
            // 两条默认路径都能原样走到，不必在这里各写一份。
            _liveInstance?.HandleIslandRightClick(rx, ry, rtY, fromDoubleClickTimeout: true);
        }

        /// <summary>
        /// 岛内右键的统一入口（原来的 WM_RBUTTONDOWN 函数体）。
        ///
        /// 优先级：详情页收起 → 插件组件广播 → 媒体面板展开 → 设置窗口。
        ///
        /// fromDoubleClickTimeout = true 表示这是「右键双击待定到期后的重放」：
        /// 跳过所有待定判定，老实执行单击的默认行为，避免再挂一次待定（自己递归自己）。
        /// </summary>
        private void HandleIslandRightClick(int rx, int ry, float rtY, bool fromDoubleClickTimeout)
        {
            if (_currentToast == null)
            {
                // 详情页已展开：岛内右键直接收起详情页（此时插件行未绘制，无需再广播）
                // 传 true：这是显式要关它，即使插件声明了「鼠标离开也不收起」也照收 ——
                // 否则选了那一档的详情页就彻底没有关闭入口了。
                if (Renderer.HasActiveDetailPage)
                {
                    // ① 右键单击也透传（IDetailPage.AcceptsRightClick）：当场交给插件，面板不折叠、也不挂待定 ——
                    //    第二下到了由下面的 WM_RBUTTONDBLCLK 补一次双击。
                    if (!fromDoubleClickTimeout && Renderer.DetailPageAcceptsRightClick
                        && Renderer.DispatchDetailPageRightClick(rx, ry - rtY))
                        return;

                    // ② 只开了双击（AcceptsDoubleClick）：第一下先挂待定 ——
                    //    双击窗口内来了第二下 → WM_RBUTTONDBLCLK 里通知插件，面板不折叠；
                    //    窗口过了没来 → 待定到期，重放本方法走下面的折叠。
                    if (!fromDoubleClickTimeout && Renderer.DetailPageAcceptsDoubleClick)
                    {
                        ScheduleRightDoubleClick(onDetail: true, rx, ry, rtY, widgetId: null, lx: 0f, ly: 0f);
                        return;
                    }

                    ClosePanelsNow(forceCloseDetail: true);
                    return;
                }

                // 命中的组件注册了右键 / 双击能力（IWidget.AcceptsRightClick / AcceptsDoubleClick，两档独立）：
                if (!fromDoubleClickTimeout
                    && Renderer.TryHitRightClickWidget(rx, ry - rtY, out var rcWidgetId, out float rcLx, out float rcLy,
                        out bool rcRight, out bool rcDouble))
                {
                    // ① 右键单击也透传：当场交给组件，既不展开详情页、也不打开设置窗口
                    if (rcRight && Renderer.DispatchWidgetRightClick(rcWidgetId)) return;

                    // ② 只开了双击：第一下挂待定 —— 否则第一下会先把详情页展开，
                    //    第二下就落在那张新面板上了，插件永远收不到这次双击。
                    if (rcDouble)
                    {
                        ScheduleRightDoubleClick(onDetail: false, rx, ry, rtY, rcWidgetId, rcLx, rcLy);
                        return;
                    }
                }

                string? detailWidget = Renderer.DispatchPluginRightClick(rx, ry - rtY);

                // 主机默认行为：命中的组件提供了详情页 → 在灵动岛展开该组件的详情页（消费这次右键，不弹设置窗口）
                if (detailWidget != null)
                {
                    ExpandPanel(detailWidget);
                    return;
                }
            }

            // 注：待机模式选「媒体控制」时不再为频谱那一带单开右键分支 ——
            //     岛内被媒体模块占满，左右两半本就该同一条规则（跳转开启时右键展开，
            //     否则右键直达媒体设置页签），与左键展开（见 WM_LBUTTONDOWN）保持同一口径。
            //     曾经那条例外让右半边右键固定开设置，左半边却照常展开 —— 同一条媒体块两套行为。

            // 折叠态媒体区右键 = 展开媒体面板（曾经定下展开入口在右键，
            // 后来细化为「入口跟着跳转开关走」）：
            // 「开启『双击封面跳转应用』就右键展开，否则正常左键点击展开」。
            // 三条判据缺一不可：① 消息提示音接管岛体时不抢（_currentToast == null，与上面同一道闸）；
            // ② Renderer.MediaExpandByRightClick —— 展开功能总闸（媒体交互方式）开着且跳转开着；
            // 总闸关掉或跳转关掉时都没有右键展开这一说，右键照旧直达设置页签
            // （跳转关掉时展开入口在左键单击，见 WM_LBUTTONDOWN）；
            // ③ 面板此刻确实还没展开（展开态右键归设置窗口，且面板已展开时再展开一次没有意义）。
            // 媒体没激活时绘制侧压根不登记媒体区间（HitMediaZone 恒 false），这里不必另判。
            if (_currentToast == null
                && Renderer.MediaExpandByRightClick
                && !Renderer.IsMediaExpanded
                && Renderer.HitMediaZone(rx))
            {
                ExpandPanel(Plugins.BuiltinWidgets.Media);
                // 与原先「左键展开」同一条理由：折叠态岛体可能比 320 的面板更宽，展开瞬间变窄，
                // 按下时还在岛内的坐标可能随即落到岛外，被兜底轮询判成「岛外点击」把面板当场收走。
                // 右键不产生 WM_LBUTTONUP，所以靠按下时置位、由每帧观察左键状态的那段逻辑清掉 ——
                // 右键场景下左键本来就是抬起的，下一帧即自动复位，只覆盖展开那一瞬间。
                _suppressOutsideCollapse = true;
                Logger.Info($"媒体展开：折叠态右键 ({rx},{ry}) 命中媒体区 → 已展开媒体面板"
                    + "（跳转开启时的入口；展开态右键仍打开设置）");
                return;
            }

            // 按「右键落在哪块原生内容上」直达对应设置页签：
            // 媒体控制器 → 媒体设置；时间/日期、CPU/RAM → 显示设置；
            // 其他（空白待机 / 插件行 / 剪贴板面板…）→ 保持原行为，打开设置窗口的当前页签。
            // 命中区由渲染器本帧登记（Renderer.Layout.cs），所以通知 / 详情页接管岛体期间不会误命中。
            // 这里不消费媒体区的右键：整个媒体控制器的右键都照旧只打开设置窗口
            // （这是既定口径），上面那条分支只是「折叠态 + 跳转开启」这一种情况下的例外；
            // 跳转关掉时展开入口在左键单击（见 WM_LBUTTONDOWN），右键同样照旧直达媒体设置。
            int targetTab = Renderer.NativeRightClickTab(rx);
            if (targetTab >= 0) ConsoleWindow.ShowTab(targetTab);
            else ConsoleWindow.Toggle();
        }

        /// <summary>
        /// 展开指定组件（builtin.media 或插件组件 Id）的面板。
        /// 同一时刻只留一块：开这块之前先把另一块收掉。
        ///
        /// 两个调用方：岛内左键/右键命中组件，以及把文件拖到收起态组件上时的自动展开
        /// （见 IslandDropTarget，组件需声明 IWidget.AcceptsFileDropWhenCollapsed）。
        /// 后者同样要先把两个折叠计时取消掉，否则刚展开的面板可能立刻被挂上收起计时。
        /// </summary>
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

        /// <summary>收起媒体控制面板（设置里关掉「展开交互」时调用）。</summary>
        public static void CloseMediaPanel()
        {
            _mediaPanelCollapse.Cancel();
            Renderer.IsMediaExpanded = false;
        }

        /// <summary>
        /// 从托盘菜单「唤回灵动岛」把岛体叫回来：锁上「手动展开」，让自动隐藏的判定回到显示侧；
        /// 同时屏蔽本次触发的岛外收起判定，避免刚滑回来又被收回。
        ///
        /// 穿透模式开启时还要一并唤醒穿透睡眠态 —— 否则岛体滑回后鼠标一悬停就又被淡出到全透明，
        /// 看上去像「唤不回」。穿透关闭时该标记由渲染循环下一帧自动复位，无副作用。
        ///
        /// 与「点屏幕顶部细边唤回」同源（见 WM_LBUTTONDOWN 的两条隐藏态分支），
        /// 是细边被遮挡 / 折叠高度过大导致点不到时的兜底入口。
        /// </summary>
        public void RequestWakeIsland()
        {
            _isManuallyExpanded = true;
            _suppressOutsideCollapse = true;
            _isPassthroughAwake = true;
        }

        /// <summary>
        /// 鼠标离开岛体：给两块面板各挂一个延迟折叠（媒体 MediaCollapseDelayMs、
        /// 详情页 DetailCollapseDelayMs），由 TickPanelCollapse 到期才真的折叠；
        /// 期间鼠标回到岛上会取消。延迟的理由：面板展开后（详情页尺寸由插件决定，可能比原岛体更窄 / 更矮）
        /// 光标可能正好落在新矩形之外，立即收会变成「刚展开就自己没了」。
        /// </summary>
        private static void RequestPanelCollapse()
        {
            if (Renderer.IsMediaExpanded) _mediaPanelCollapse.Schedule();
            if (Renderer.HasActiveDetailPage)
            {
                // 详情页可以自己指定「鼠标离开后多久收起」（IDetailPage.AutoCollapseDelay）：
                // 需要用户离开面板去别处取东西的插件（比如文件中转站要从资源管理器挑文件再拖回来）
                // 会把它调长，否则鼠标刚移开面板就没了、拖放目标当场消失。
                // 没指定时返回 null，沿用宿主内置的 DetailCollapseDelayMs。
                int? delay = Renderer.ActiveDetailCollapseDelayMs;

                // CollapseNever：插件明确要求「鼠标移开也别收」→ 这次干脆不挂计时，面板一直开着。
                // 不会因此关不掉：岛外点击走的是 ClosePanelsNow()（立即收，不经过这里），
                // 岛内再右键、以及插件自己调 CloseDetailPage() 也都照常有效。
                if (delay != Renderer.CollapseNever)
                {
                    _detailPanelCollapse.Schedule(delay);
                    _detailCollapseWidgetId = PluginManager.Instance.Host.ActiveDetailWidgetId;
                }
            }
        }

        /// <summary>鼠标回到岛上：取消两块面板挂起的延迟折叠（还没到期就当没发生过）。</summary>
        private static void CancelPanelCollapse()
        {
            _mediaPanelCollapse.Cancel();
            _detailPanelCollapse.Cancel();
            _detailCollapseWidgetId = null;
        }

        /// <summary>
        /// 结算挂起的延迟折叠：到点了才真的折叠，没到点什么都不做。
        /// 每帧调一次（RenderLoop），代价只有两次 DateTime 比较。
        /// </summary>
        private static void TickPanelCollapse()
        {
            if (_mediaPanelCollapse.Tick()) Renderer.IsMediaExpanded = false;

            if (!_detailPanelCollapse.Tick()) return;

            string? scheduled = _detailCollapseWidgetId;
            _detailCollapseWidgetId = null;

            // 只收当初挂时间戳的那一张：期间插件若已经换了别的详情页，说明用户在看新东西，不动它
            //
            // 这里必须再确认一次「插件此刻是否要求永不收起」：
            // 计时是几秒前挂上的，这中间插件的 AutoCollapseDelay 完全可能已经变成负值
            // （同一个详情页改了策略）—— 挂计时那一刻检查过，不代表结算这一刻还成立。
            // 少了这一判，声明「永不收起」的面板会被一个几秒前埋下的计时器收掉。
            if (scheduled != null
                && Renderer.HasActiveDetailPage
                && !Renderer.ActiveDetailKeepsOpen
                && string.Equals(PluginManager.Instance.Host.ActiveDetailWidgetId, scheduled, StringComparison.OrdinalIgnoreCase))
            {
                PluginManager.Instance.Host.CloseDetailPage();
            }
        }

        /// <summary>立即折叠全部展开面板（岛外点击这种明确的外部动作，不延迟）。</summary>
        /// <param name="forceCloseDetail">
        /// true = 连声明了「鼠标离开也不收起」的详情页也一并收掉。
        /// 这个值专供「岛内右键」——那是明确冲着面板来的关闭手势，
        /// 若也尊重插件的不收起，插件选了这个档之后就再也没有任何办法关掉它了。
        ///
        /// 岛外点击传 false（默认）。不过注意：岛外左键现在在渲染循环那段轮询里就已经被拦掉了
        /// （详情页展开期间根本不会调到这里），这里保留这个判断是为了兜住将来可能新增的
        /// 「岛外立即关闭」路径 —— 它们同样应当尊重插件的不收起选择。
        /// </param>
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

        /// <summary>
        /// 收起全部展开态：两块面板（立即）+ 自动隐藏唤醒出来的「手动展开」。
        ///
        /// 只在岛外点击时用（用户主动表达「我看完了」，再等延迟反而像卡住）。
        /// 刻意不挂到鼠标离开上：自动隐藏的唤醒是「点一下顶部那条边 → 岛体滑下来」，
        /// 滑下来之后光标本来就落在岛体上方，若跟着鼠标离开一起收，会立刻弹回隐藏态 —— 变成点一下闪一下。
        /// </summary>
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

            // 将 Skia 直接绑定到这块系统内存上，彻底消灭 Buffer.MemoryCopy
            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _renderSurface = SKSurface.Create(info, _pBits, _scaledWidth * 4);

            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}