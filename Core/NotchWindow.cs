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
        private readonly IntPtr _hwnd;
        private readonly MediaController _media;
        private bool _isHovered = false;
        private bool _isTrackingMouse = false;
        private readonly Timer _renderTimer;
        private readonly Win32.WndProc _wndProcDelegate;

        /// <summary>岛体的 OLE 拖入目标（详情页拖放用）。同时是 CCW 的强引用持有者，掉了可能被 GC 回收。</summary>
        private IslandDropTarget? _islandDropTarget;

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
        private readonly SystemSettingsManager audio;
        private readonly IntPtr _hCursorHand;
        private bool _isCursorOverIcon = false;
        private ToastNotificationListener? _listener;
        // 通知轮询定时器：**刻意用线程池定时器（System.Timers.Timer），不要换回 DispatcherTimer**。
        // DispatcherTimer 依赖 WPF Dispatcher 的队列被"泵"，而本程序的主循环是纯 Win32 的 Run()
        // （GetMessage/DispatchMessage，没有 Dispatcher.Run/PushFrame）。实测它在启动后只跳几次就静默停摆：
        // 2026-09-25 部署了带心跳的版本，2 分半内 0 条心跳（心跳在每次调用开头就打），而同一时刻 dispatcher
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
        public static bool IsAutoHideEnabled = false; // 全局自动隐藏开关（**用户的偏好**，不等于真的生效）

        /// <summary>
        /// 自动隐藏**实际是否生效**。穿透模式下强制失效。
        ///
        /// 设置面板在穿透模式开启时会把「自动隐藏」开关置灰并**显示为关闭**（副标题：穿透模式下禁止自动隐藏），
        /// 运行时必须和这个承诺完全一致 —— 否则就会出现「开关显示已关、岛体却还在躲」。
        /// 判据必须与设置面板用的是同一个（`Renderer.PassthroughModeEnabled`），别再各写一份。
        ///
        /// 注意这里**不销毁用户偏好**：`IsAutoHideEnabled` 原样保留，关掉穿透模式后自动隐藏会自动回来。
        /// 所有「自动隐藏要不要生效」的判断都请读这个属性，不要直接读 `IsAutoHideEnabled`。
        /// </summary>
        public static bool IsAutoHideEffective => IsAutoHideEnabled && !Renderer.PassthroughModeEnabled;

        /// <summary>
        /// 「暂停播放后自动隐藏」开关（**用户的偏好**）。是自动隐藏的附属扩展功能，默认关闭。
        /// 语义：媒体会话还在（岛体本来会因为 `_media.IsActive` 而拒绝隐藏），但**没有在播放**
        /// （SMTC 处于暂停 / 停止）时，允许继承自动隐藏逻辑把岛体藏起来。
        /// </summary>
        public static bool IsPauseAutoHideEnabled = false;

        /// <summary>
        /// 「暂停播放后自动隐藏」**实际是否生效**。判据 = 自身开关 且 <see cref="IsAutoHideEffective"/>。
        ///
        /// 之所以直接挂在 <see cref="IsAutoHideEffective"/> 上而不是各写一份，是因为它天然继承了两条既有约束：
        ///   1. 自动隐藏关掉时它一并失效（设置面板也会连带把开关关掉并置灰）；
        ///   2. 穿透模式下自动隐藏强制失效 → 它也强制失效，与设置面板「两个开关都置灰」的承诺一致。
        /// 所有「暂停后要不要隐藏」的判断都请读这个属性。
        /// </summary>
        public static bool IsPauseAutoHideEffective => IsPauseAutoHideEnabled && IsAutoHideEffective;

        /// <summary>
        /// 「全屏自动隐藏」开关（**用户的偏好**）。自动隐藏的附属扩展功能，默认关闭。
        /// 语义：检测到有全屏应用在跑（全屏视频 / 全屏游戏，含独占模式 D3D）时，**无条件**让位隐藏，
        /// 哪怕音乐正在播放 —— 全屏场景下岛体压在顶上就是纯打扰。
        ///
        /// 与 <see cref="IsPauseAutoHideEnabled"/> **互斥**：面板上只允许开一个，开启一个会自动关掉另一个。
        /// 理由：两者都是「放宽允许隐藏的条件」，同时开着只会让「到底因为哪条才藏的」变得难以预期。
        /// </summary>
        public static bool IsFullscreenAutoHideEnabled = false;

        /// <summary>
        /// 「全屏自动隐藏」**实际是否生效**。与暂停隐藏同样直接挂在 <see cref="IsAutoHideEffective"/> 上，
        /// 天然继承「自动隐藏关闭即失效」与「穿透模式强制压制」两条既有约束，不用各写一份。
        /// </summary>
        public static bool IsFullscreenAutoHideEffective => IsFullscreenAutoHideEnabled && IsAutoHideEffective;

        // ==================== 全屏检测（「全屏自动隐藏」专用） ====================
        // 轻量化的三个关键：
        //   1. 只调**一次** Win32（SHQueryUserNotificationState），不自己枚举窗口比对显示器矩形；
        //   2. **节流**：最多每 0.8s 探一次 —— 全屏切换是秒级事件，不需要 16ms 级延迟；
        //   3. **功能没开就一次系统调用都不发**，直接把缓存压回 false。
        // 探测与消费都在渲染循环线程上，所以缓存不需要加锁。
        private static bool _isFullscreenCached;
        private static DateTime _fullscreenProbeAt = DateTime.MinValue;
        private const double FullscreenProbeIntervalSeconds = 0.8;

        /// <summary>
        /// 节流刷新全屏检测缓存。由渲染循环在算 <c>shouldHide</c> 之前调用一次。
        /// 唤醒点击分支只**读**缓存（<see cref="IsFullscreenHideActive"/>），不重复探测。
        /// </summary>
        private static void TickFullscreenProbe()
        {
            // 功能没开（或自动隐藏关了 / 穿透模式压着）就彻底不探测，顺手把缓存压回 false，
            // 免得残留上一次的 true 让「刚关掉开关岛体还躲着」。
            if (!IsFullscreenAutoHideEffective) { _isFullscreenCached = false; return; }

            var now = DateTime.UtcNow;
            if ((now - _fullscreenProbeAt).TotalSeconds < FullscreenProbeIntervalSeconds) return;
            _fullscreenProbeAt = now;

            _isFullscreenCached = false;
            try
            {
                // 返回 HRESULT：非 0 表示查询失败，out 值不可信 → 保持 false。
                // 这里刻意**静默** catch（不写日志）：本方法每 0.8s 跑一次，一旦失败会持续失败，
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
        /// 「现在允许自动隐藏吗」——**自动隐藏判定的单一真源**。
        /// <c>shouldHide</c>（藏不藏）与 <c>WM_LBUTTONDOWN</c> 的唤醒分支（点了能不能唤回）**必须共用它**，
        /// 否则就会出现「藏得下去、点不回来」。
        ///
        /// 三种模式**互斥**（面板上只允许开一个），共同点都是「放宽允许隐藏的条件」：
        ///   · 普通自动隐藏：没有媒体会话 → 允许
        ///   · 暂停播放后自动隐藏：媒体**暂停 / 停止**时 → 允许（原本是「媒体激活即一律不隐藏」）
        ///   · 全屏自动隐藏：检测到全屏应用 → **无条件允许**（正在播放也要让位，这正是它的用途）
        ///
        /// 历史坑：唤醒分支曾自己写死 `!_media.IsActive`。加了「暂停后隐藏」之后，岛体会在
        /// `_media.IsActive == true`（暂停中）的状态下藏起来，写死的判据就变成「藏得下去、点不回来」。
        /// 所以两边一律读这里，别再各写一份。
        /// </summary>
        private bool CanAutoHideNow
        {
            get
            {
                if (!IsAutoHideEffective) return false;   // 未开启 / 穿透模式压制
                if (IsFullscreenHideActive) return true;  // 全屏优先：播放中也要让位
                if (_media.IsActive) return IsPauseAutoHideEffective && !_media.IsPlaying;
                return true;                              // 无媒体会话 → 普通自动隐藏
            }
        }
        private readonly ToastNotificationListener _toastListener = new ToastNotificationListener(); // Toast 监听器
        // 📋 剪贴板链接监听（事件驱动，仅在复制时读一次剪贴板，稳态零占用）
        private readonly ClipboardMonitor _clipboardMonitor = new ClipboardMonitor();
        private string? _clipboardUrl;         // 当前正在展示的链接
        private string? _pendingClipboardUrl;  // 被更高级别通知挤下后退回队列等待的链接（单槽位复用，零额外内存）
        private DateTime _clipboardEndTime;    // 链接展示截止时间
        public bool isClipboardActive;         // 本帧剪贴板面板是否激活
        // ==================== 「Q 弹」弹簧动画引擎（三处共用） ====================
        // 岛体尺寸变化（宽/高）、形态切换（刘海 ⇄ 灵动岛）、自动隐藏位移（Y 轴）**共用同一条曲线**，
        // 所以三处的手感完全一致 —— 这正是把它们抽出来的目的：以前是同一个公式抄三份、常量各写一遍，
        // 改一处忘一处就会出现「这个动画弹、那个不弹」。新增位移动画时请直接调 SpringEase()。
        //
        // 曲线：1 - cos(freq·t·2π)·e^(-decay·t)
        //   freq 越大爆发越干脆（振荡更快），decay 越小阻尼越低、余震越多（果味更浓）。
        //   当前取值下最大过冲约 15.9%（峰值出现在 t≈0.154s），也就是「Q 弹」的来源。
        private const double SpringFrequency = 2.65;
        private const double SpringDecay = 10.8;
        // 动画时长：取到曲线基本归位（≈99.7%）的时刻，再长只是空转。
        // 注意是**时间**而不是进度 —— 弹簧是时间驱动，不能按 t/duration 归一化后再套。
        private const double SpringDurationSeconds = 0.450;

        /// <summary>
        /// 弹簧缓动：传入**已过去的秒数**，返回 0→1 的插值系数（中途会过冲，大于 1 是正常的）。
        /// 与 <see cref="SpringDurationSeconds"/> 配套使用。
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
        // 🎯 「唤醒那一次左键按下还没松开」标记。
        //    为什么需要它：点击屏幕顶边唤醒岛体时，用户点的是 y≈0 的位置，而岛体下沉后**可见矩形从
        //    y = 12 才开始**（12f * _currentStyleProgress），所以这次点击的坐标**天然落在岛体之外**。
        //    于是下面那段「左键按下 且 光标不在岛体矩形内 → 收起」的兜底轮询，会把**唤醒自己的这一次点击**
        //    判成「岛外点击」，岛刚滑出来就被收回去 —— 用户看到的就是「抽一下又回去了」。
        //    · 抑制范围 = **这一次按键的 down→up 全程**，不多不少。
        //      解除不靠 WM_LBUTTONUP，而是靠轮询里每帧读一次 `GetAsyncKeyState(0x01)`：
        //      岛体滑回后，光标所在的那条屏幕顶边在窗口里是**透明像素**，分层窗口的透明区域不参与
        //      命中测试，up 消息很可能根本派发不到本窗口。直接观察物理按键状态是精确且不丢信号的。
        //    · 刻意**不加时间上限**：上限会让「长按超过 N 秒」重新踩回这个 bug（实测 1.5s 上限时
        //      按住 1.6s 仍会抽一下又回去）。而按键松开是每帧实测的，不会漏，所以不需要兜底。
        private bool _wakeClickPending = false;

        // ================= 🧩 展开面板统一管理 =================
        // 媒体控制面板（builtin.media）与插件组件详情页共用同一套开合逻辑与时序，不再各写一份：
        //   ExpandPanel(id)        展开某个组件的面板（同一时刻只留一块，另一块让位）
        //   RequestPanelCollapse() 鼠标离开岛体 → 挂延迟折叠（两块延迟不同，见下面两个常量）
        //   CancelPanelCollapse()  鼠标回到岛上 → 取消挂起
        //   ClosePanelsNow()       岛外点击这种明确动作 → 立即折叠
        //   TickPanelCollapse()    每帧结算到期的折叠
        // 面板内容仍各归各自的宿主持有（媒体 = Renderer.IsMediaExpanded，详情页 = PluginHost），
        // 这里统一的是**开合入口与时序**。做成静态：全局只有一块岛体，渲染循环与设置窗口都要能调。

        private static readonly PanelCollapseTimer _mediaPanelCollapse = new(MediaCollapseDelayMs);
        private static readonly PanelCollapseTimer _detailPanelCollapse = new(DetailCollapseDelayMs);
        // 挂起的那次延迟折叠是冲着哪个组件去的。到期时只收这一个 —— 万一延迟期间插件换了另一张
        // 详情页（前一张自己收起、后一张打开），不能把用户刚看到的新页面顺手收掉。
        private static string? _detailCollapseWidgetId;

        /// <summary>媒体控制面板的延迟折叠时长。</summary>
        private const int MediaCollapseDelayMs = 3000;
        /// <summary>插件详情页的延迟折叠时长（用户 2026-09-19 要求 0.8~1s）。</summary>
        private const int DetailCollapseDelayMs = 900;

        /// <summary>一块展开面板的延迟折叠计时：只管「什么时候收」，展开状态由各自的宿主持有。</summary>
        private sealed class PanelCollapseTimer
        {
            private readonly int _delayMs;
            private DateTime _deadline = DateTime.MinValue;

            public PanelCollapseTimer(int delayMs) => _delayMs = delayMs;

            /// <summary>
            /// 挂起延迟折叠（重复挂起按最后一次重新计时）。
            /// <paramref name="delayMs"/> 传 null 就用构造时的默认值 ——
            /// 插件详情页允许自定义这段时长（<c>IDetailPage.AutoCollapseDelay</c>），所以这里得能被覆盖。
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
            // 🖱 让岛体也能接文件拖放：右键展开的插件详情页靠它实现「拖入 / 拖出」
            SetupIslandDropTarget();
            // 将定时器提速至 16ms (~60FPS)，保障 Q弹 动画的丝滑度
            _renderTimer = new Timer(16);
            // 具名方法而非 lambda：才能在退出时 -= 退订（lambda 会把 this 钉在计时器上）
            _renderTimer.Elapsed += OnRenderTick;
            _renderTimer.Start();

            // 🛠️ 托盘图标与右键菜单（自绘纯色菜单，见 TrayMenuWindow）
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
                TrayMenuWindow.Show(pt.x, pt.y, ConsoleWindow.Toggle, ExitApplication);
            };

            _notifyIcon.Visible = true;
            Debug($"初始音量读取完成，当前音量：{audio.Volume:F2}");
            // 🔉 内置音量（系统主音量）只有一个下游：接了 Just Solo 的 WS 就下发播放器，没接才改系统主音量。
            //    Just Solo 自己的音量是另一个变量（MediaController.TryGetJustSoloVolume），两边互不覆盖。
            audio.VolumeSink = _media.TrySyncVolumeToJustSolo;
            // 🧩 插件系统：先把插件提醒接入 Toast 流，再初始化运行时自动加载已启用插件
            PluginManager.Instance.Host.ReminderPosted += OnPluginReminder;
            PluginManager.Instance.Initialize();
            _ = InitializeListenerAsync();

            // 📋 订阅剪贴板监听：窗口句柄就绪后注册 WM_CLIPBOARDUPDATE
            _clipboardMonitor.OnUrlDetected += OnClipboardUrlDetected;
            _clipboardMonitor.Attach(_hwnd);
            // 🔉 每 500ms 读一次系统音量，发现不经过 SystemSettingsManager 的改动（音量键 / 系统 OSD / 其它软件）
            _audioWatchTimer = new Timer(500);
            _audioWatchTimer.Elapsed += OnAudioWatchTick;
            _audioWatchTimer.Start();
        }

        /// <summary>渲染时钟（16ms）。具名方法：退出时能 -= 退订。</summary>
        private void OnRenderTick(object? sender, System.Timers.ElapsedEventArgs e) => RenderLoop();

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
            // 1) 渲染时钟：置位退出标记 → 停表 → 退订 → Dispose
            _shuttingDown = true;
            try
            {
                _renderTimer.Elapsed -= OnRenderTick;
                _renderTimer.Stop();
                _renderTimer.Dispose();
            }
            catch { }

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
        }

        /// <summary>
        /// 供静态退出/托盘回调使用的实例引用。
        /// NotchWindow 本身是实例类，但托盘回调是静态语义，需要一条稳定的引用链。
        /// </summary>
        private static NotchWindow? _instanceForExit;

        /// <summary>
        /// 当前活跃的岛体实例 —— 给 <see cref="StartFileDragOnIsland"/> 这类静态入口
        /// 回过头调用实例方法用（拖出结束后要补一次悬停判定，见 <see cref="NotifyDragExit"/>）。
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

        // ================= 🔔 通知轮询看门狗 =================
        private long _watchdogLastCheckTicks;
        private long _watchdogLastWarnTicks;

        /// <summary>
        /// 轮询看门狗（挂在渲染循环上，每 ~10s 抽检一次）。
        ///
        /// 只看一件事：**轮询还有没有在发起调用**。判据用"发起时刻"而不是"取到数据的时刻"——
        /// 取不到数据（超时、权限失效、快照冻结）由轮询自己的诊断负责，这里专治"轮询压根没在跑"
        /// 这一类静默故障：2026-09-25 就是 DispatcherTimer 在本程序的主循环下跳几次就不动了，
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
        /// 🎵 在消息真正上岛时投递提示音。
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
                string? path = ToastSoundConfig.ResolveCurrentPath();
                if (path == null) return;
                ToastSoundPlayer.Enqueue(path, ToastSoundConfig.VolumePercent);
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

            _currentToast = toast;
            _toastEndTime = DateTime.Now.AddSeconds(4); // 消息展示4秒自动消失
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
            _toastEndTime = DateTime.Now.AddSeconds(4);
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
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Info($"[剪贴板] 已在默认浏览器打开链接: {url}");
            }
            catch (Exception ex) { Error("[剪贴板] 打开链接失败", ex); }
        }

        /// <summary>
        /// 穿透唤醒按钮的命中判定（逻辑坐标）。
        /// 位置算式的唯一真源在渲染侧（<see cref="Renderer.WakeButtonX"/> / <see cref="Renderer.WAKE_BTN_SIZE"/>），
        /// 这里只补上岛体的垂直偏移。**鼠标移动（手型指针）与左键按下（唤醒）必须共用它** ——
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
            if (!sourceIsTray)
            {
                // 设置面板改的 → 把自绘托盘菜单的 ✅ 对齐，
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
                // 🔔 轮询看门狗：借用渲染循环这个最可靠的时钟（16ms 线程池定时器）去盯"通知轮询还在不在跑"。
                //    2026-09-25 的教训就是：自检不能放在被检对象自己身上 —— DispatcherTimer 停摆后，
                //    它自己的诊断日志也一起哑了，从外部完全看不出原因。
                TickPollingWatchdog();

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

                // 📋 级别调度（消息队列，零额外分配）：系统通知 > 剪贴板链接 > 媒体控制器
                // 开关关闭时立即收起正在展示的链接并清空排队槽位
                // ⚠️ 本块必须排在下面的穿透判定**之前**：穿透逻辑要读本帧的 isClipboardActive
                //    （Toast 的 isToastActive 在更上面就已算好）来决定是否临时退出穿透（见下），
                //    排在后面会慢一帧、且与渲染状态不同步。
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
                    // 例外：**系统主动弹出的内容展示期间临时禁用穿透** —— 剪贴板链接面板与 Toast 通知。
                    // 二者的共同点是「弹出时机不由用户决定、且本身需要被看见和点击」：一旦悬停就变透明，
                    // 用户既看不到也点不到，靠唤醒按钮也救不回来 —— 面板只停 3s，
                    // 等你去点唤醒按钮时它已经消失了。内容一结束（点开 / 超时 / 被通知挤下）穿透自动恢复 ——
                    // 不需要任何额外状态：两个 is*Active 标志位都由本帧的调度逻辑维护。
                    // 🖱 第三个例外：**文件正被拖着经过岛体时**（Renderer.FileDragInProgress）同样不许淡出。
                    //    这一条比上面两条更硬：淡到全透明 = 岛体像素从 OLE 命中测试里消失，
                    //    拖放目标当场丢失，用户手里的文件就再也放不进详情页了（拖放源那边也不会补发第二次 DragEnter）。
                    //    标志位由 IslandDropTarget 在 DragEnter / DragLeave / Drop 维护，拖放一结束穿透自动回来。
                    float targetAlpha = 1.0f;
                    if (!_isPassthroughAwake && isOverNotch && !isClipboardActive && !isToastActive
                        && !Renderer.FileDragInProgress) targetAlpha = 0.0f;

                    // 🛟 兜底自动复位：拖放源被杀 / 崩溃时 DragLeave、Drop 一个都不会来，
                    //    标志位若一直挂着，穿透淡出就永久失效（而且看不出是谁干的）。
                    //    文件拖放全程必须按住左键，松开就说明这一轮早就结束了 —— 一个系统调用就能把它收干净。
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

                // 🧩 展开态的收起策略：**只要鼠标离开灵动岛，展开的面板就自动折叠**。
                //    · 触发在 WM_MOUSELEAVE（见下）：两块面板都只挂一个延迟截止时间（见 RequestPanelCollapse），
                //      自动隐藏的「手动展开」刻意不跟，理由也写在 ClosePanelsNow / CollapseAllExpanded 上。
                //    · 到点由下面这行统一结算（每帧一次 DateTime 比较，可忽略）。
                TickPanelCollapse();

                //    · 这里是一层兜底轮询：窗口只在鼠标进入它范围内时才收得到鼠标消息，岛外点击根本不会派发
                //      WM_LBUTTONDOWN，且 SetCapture（拖时间轴）期间 WM_MOUSELEAVE 会被吞掉，
                //      所以额外判断一次「左键按下 且 光标不在岛体矩形内」，命中就收起
                //      （坐标换算与上面穿透模式那段完全同一套：减去显示器原点、减窗口 Y 偏移、再除 DPI）。
                //    · 拖动中一律不收起 —— 拖时间轴时鼠标合法地待在岛外，此时收起会把面板从手里抽走；
                //      松手若仍在岛外，由 WM_LBUTTONUP 补一次判定。
                //    只在「确实有东西展开着」时才轮询，三个状态全 false 时这段直接跳过，稳态零开销。
                //    · `_wakeClickPending` 也纳入轮询条件：它的解除靠下面每帧观察按键是否松开
                //      （不能只靠 WM_LBUTTONUP —— 岛体滑回后，光标所在的那条屏幕顶边在窗口里是**透明像素**，
                //       分层窗口的透明区域不参与命中测试，up 消息很可能根本派发不到本窗口）。
                if (!_media.IsDragging
                    && (_isManuallyExpanded || Renderer.IsMediaExpanded || Renderer.HasActiveDetailPage
                        || _wakeClickPending))
                {
                    bool leftDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;

                    // 🎯 唤醒那一次点击的按键已经松开 → 立刻解除抑制，用户再点岛外照常收起。
                    if (_wakeClickPending && !leftDown) _wakeClickPending = false;

                    float expLeft = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
                    float expTopY = 12f * _currentStyleProgress;
                    Win32.GetCursorPos(out var expPt);
                    float expX = (expPt.x - _cachedMonitorX - (_cachedMonitorWidth - _scaledWidth) / 2) / _dpiScale;
                    // 同上：窗口 Y 含岛体垂直基准，换算回窗口内坐标要一起减掉
                    float expY = (expPt.y - _cachedMonitorY - Renderer.IslandBaseY * _dpiScale - _currentY) / _dpiScale;
                    bool isOverIsland = expX >= expLeft && expX <= expLeft + _currentWidth
                                        && expY >= expTopY && expY <= expTopY + _currentHeight;

                    // 🎯 唤醒那一次按键（还没松开）不算「岛外点击」—— 见 _wakeClickPending 上的说明：
                    //    它点的屏幕顶边坐标天然落在岛体可见矩形之外，不排除掉就会「抽一下又回去」。
                    //
                    // 🧲 插件详情页展开期间，岛外的左键一律不管（末尾那个 !HasActiveDetailPage）：
                    //    ① 用左键点组件展开时，用户的手还按在按键上，紧接着这几帧都会落进这个判定；
                    //       而展开那一瞬间 WINDOW_WIDTH / _scaledWidth 正在变，换算出的 expLeft / expX 会偏，
                    //       一旦判成「岛外点击」就把刚展开的面板收掉了 —— 肉眼就是「点一下闪一下、展不开」。
                    //       （右键展开没这个问题：那时 leftDown 是 false，压根不进这个分支。）
                    //    ② 正在从资源管理器往面板里拖文件的用户，鼠标本来就该待在岛外。
                    //    收起详情页仍有两条明确路径：岛内右键、插件自己调 CloseDetailPage()。
                    if (!isOverIsland && !_wakeClickPending && leftDown && !Renderer.HasActiveDetailPage)
                    {
                        CollapseAllExpanded();
                    }
                }


                // 🖥 全屏检测：节流刷新缓存（功能没开时这个方法直接返回，零系统调用）。
                //    必须在下面读 CanAutoHideNow 之前调用，否则会用到上一帧的旧值。
                TickFullscreenProbe();

                // 自动隐藏 (Y轴) 逻辑更新：Toast 弹出时绝对不允许隐藏；插件详情页展开时同样不允许隐藏。
                // 用 IsAutoHideEffective 而不是 IsAutoHideEnabled —— 穿透模式下必须真的不隐藏，
                // 与设置面板里「自动隐藏开关置灰且显示为关闭」保持一致。
                // 🎵 「允许隐藏」这一项统一由 CanAutoHideNow 回答（三模式单一真源）：
                //    普通 / 暂停播放后 / 全屏时，三者互斥，都是「放宽允许隐藏的条件」。
                //    它已经含 IsAutoHideEffective，所以这里不再重复写。
                // ⚠️ Toast 的 `!isToastActive` 必须原样保留 —— Toast 是「系统主动弹出且需要用户交互」的，
                //    任何自动隐藏开关都不能把它压掉。**全屏时也一样**：用户开这个功能的初衷就是
                //    「既能不被打扰、又不漏通知」，所以全屏下收到消息岛体照样要弹出来。
                //    剪贴板与插件详情页同理。
                // 🖐 `!_media.IsDragging` 是给「暂停后隐藏」配的保护：部分播放器在 seek 期间会短暂上报
                //    Paused，若不挡住就会在用户拖进度条拖到一半时把面板抽走。
                //    只在媒体激活时才可能为 true，所以对原有「无媒体」路径零影响。
                // 注：`_isManuallyExpanded` 依旧优先 —— 用户主动点顶部细边唤醒出来的岛体，不会被自动收走
                //    （要收就点岛外，走 CollapseAllExpanded）。这是「手动展开优先」的既有语义，刻意保留。
                //    全屏场景同理：真在全屏里点了顶边唤回，就说明他想看，别立刻又藏回去。
                // 同理 `!Renderer.IsMediaExpanded`：用户主动点开的媒体展开面板，不该被暂停 / 全屏抽走。
                //    鼠标离开岛体时 RequestPanelCollapse() 会把它收掉，那时才轮到自动隐藏接手。
                bool shouldHide = CanAutoHideNow && !_media.IsDragging
                                  && !_isManuallyExpanded && !Renderer.IsMediaExpanded && !isToastActive
                                  && !isClipboardActive && !Renderer.HasActiveDetailPage;

                // Y 轴的位移量必须基于「岛体自身的高度」计算，不能写死待机高度：
                // 媒体控制器 / 组合模式会把岛体撑到 MEDIA_HEIGHT(35)，若仍按 BASE_HEIGHT(29) 算，
                // 就会多露出 (35 - 29) = 6px 的尾巴 —— 待机露 4px、媒体模式露 10px，
                // 全屏看视频时正好挡视野。（用户 2026-09-20 反馈）
                // 取 `Math.Min(_currentHeight, _targetHeight)` =「尺寸动画结束后岛体的高度」：
                //   · 岛体正在**长高**（媒体刚接管）时取当前值 → 露出尾巴恒为 4px；
                //   · 岛体正在**收缩**（收起 320×130 的媒体展开面板 / 关闭插件详情页）时取目标值，
                //     否则会按旧的大高度算出一个很深的位移，把岛体先弹飞再落回。
                // 隐藏位移量还必须加上灵动岛专属的下沉高度，否则藏不进屏幕。
                float currentTopY = 12f * _currentStyleProgress;
                float settledHeight = Math.Min(_currentHeight, _targetHeight);

                // 🌑 隐藏方式由「岛体垂直基准」（Renderer.IslandBaseY，位置自定义的唯一真源）决定：
                //    · 基准贴顶（默认）→ 上移法：整窗顶出目标显示器上边缘、留 4px 细边，点细边唤醒（现状）
                //    · 基准离开顶部      → 上移法会在屏幕中间留下一条 4px 岛体残影（而且岛体会从屏幕中间
                //                          "飞"到顶部再消失），所以改用「完全隐藏」：原地整块淡出到 0% 透明
                //                          （全透明像素会被 Windows 判定为物理穿透），唤醒入口复用岛体正中的
                //                          唤醒按钮（与穿透睡眠态同一颗）。
                //    两者互斥，贴顶时 FullHideAlpha 恒为 1 → 线上行为与本改动前完全一致。
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

                // 🌑 「完全隐藏」不透明度：只在「基准离开顶部 + 该隐藏」时淡到 0，其余情况恒为 1。
                //    平滑节奏与穿透那套保持一致（0.18 + 归零钳制），避免小浮点让 Windows 判定不出全透明。
                float targetFullHide = shouldHide && !slideOutHide ? 0f : 1f;
                Renderer.FullHideAlpha += (targetFullHide - Renderer.FullHideAlpha) * 0.18f;
                if (Renderer.FullHideAlpha < 0.01f) Renderer.FullHideAlpha = 0f;
                if (Renderer.FullHideAlpha > 0.99f) Renderer.FullHideAlpha = 1f;

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

                    // 🎵 文本区长度**不再单独封顶**（2026-09-20 用户要求「媒体控制器的长度放开，多长都无所谓」）。
                    //    原先这里夹了一个 MEDIA_TEXT_MAX_WIDTH（480 ≈ 27 个汉字），长歌词先撞到它 →
                    //    超出部分被文字渐隐遮罩截断，而且原生内容宽度被钉在 595，
                    //    插件行预算 = 800 − 595 = 205 被吃光 → 装不下的插件**整帧不显示**
                    //    （用户反馈：「多的插件在灵动岛上就直接不显示了」「这个长度只显示这个插件，
                    //      另一个长度只显示另一个插件」）。
                    //    现在只受下面的 MAX_ISLAND_WIDTH（已放宽到 1920）约束，真实歌词行远达不到。
                    nativeWidth = Math.Max(nativeWidth, textWidth + 115f);
                }
                nativeWidth = Math.Min(nativeWidth, Renderer.MAX_ISLAND_WIDTH); // 岛体总长上限（1920），窄屏也不会被撑破

                // 🧩 插件行取舍：按「组件声明的所需宽度能否完整落进剩余空间」判定。
                //    每个组件通过 IWidget.MeasureWidth 声明「完整显示我的内容需要多宽」，
                //    宿主用「岛体总长上限 − 原生内容本帧占用宽度」得出插件行预算，逐个贪心放行：
                //    装得下的组件完整显示，装不下的组件本帧整体不显示 —— 宿主绝不替它压缩或截断，
                //    所以不会出现「文字被省略号砍掉半截」这种显示不全的情况。
                //    原生内容（尤其是开着媒体控制 + 长歌词自适应）一样照常显示，岛体也不会被撑过上限。
                //    例外：媒体控制面板展开（IsMediaExpanded）时插件行整体不显示 —— 那是块独立面板，
                //    插件贴上去只会把面板和岛体一起撑宽，见下面的分支。
                // 🎵 该例外对**组合模式同样生效**（2026-09-25）：组合模式现在也能展开媒体面板，
                //    展开期间岛体只剩面板，插件行与其它原生模块本帧都不参与。
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
                    // 📋 剪贴板链接面板：沿用媒体控制器同款尺寸（高度一致，宽度按链接长度自适应并封顶）
                    expectedTargetWidth = Renderer.GetClipboardAutoWidth(_clipboardUrl!);
                    expectedTargetHeight = Renderer.MEDIA_HEIGHT;
                }
                else
                {
                    // 🎵 媒体展开面板：宽度锁定面板尺寸（nativeWidth 此时恒为 320、pluginReserve 为 0），
                    //    组合模式与非组合模式同一条算式 —— 不再因为「组合模式」而去累加模块宽度。
                    // 组合模式走渲染器里的像素级精确动态宽度计算，拒绝任何多余空白与错位；
                    // 其余模式 = 原生内容宽度 + 插件行预留（插件行放不下时预留已归零）
                    expectedTargetWidth = Renderer.CompositeModeEnabled && !mediaPanel
                        ? Renderer.GetCompositeWidth(_media)
                        : nativeWidth + pluginReserve;

                    expectedTargetHeight = currentActive ? (Renderer.IsMediaExpanded ? Renderer.GetExpandedHeight(_media) : Renderer.MEDIA_HEIGHT) : Renderer.BASE_HEIGHT;
                }

                // 🧩 注意：**不要**再把目标宽度喂给渲染侧去「按动画进度缩放插件行预留」。
                //    那个做法（曾用 Renderer.IslandTargetWidth + GetScaledPluginReserve）会在
                //    媒体控制器长度变化时把预留瞬间缩小：换歌词 / 换标题 → 目标宽度变大 →
                //    缩放系数从 1 掉下来 → 插件行与原生内容边界整体挪一下再挪回去，
                //    表现就是「插件闪现回原位又闪回来」。用户 2026-09-20 反馈，已整套删除。
                //    现在预留一律用未缩放值，边界只跟着岛体边缘平滑移动。

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

        /// <summary>
        /// 拖放离开岛体（或拖放被取消）时由 <see cref="IslandDropTarget"/> 调用。
        ///
        /// <para>
        /// 为什么需要它：拖放期间鼠标被 OLE 的拖放循环接管，窗口<b>收不到 WM_MOUSELEAVE</b>，
        /// 于是「鼠标离开岛体 → 挂延迟折叠」这条常规路径整个被跳过了。
        /// 不在这里补一刀，详情页就会一直停在「正在拖入」的样子 —— 高亮不灭、也不走折叠计时，
        /// 看起来就是「卡在拖入」。
        /// </para>
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

            // ⚠️ 到这里**刻意不去动 _isHovered**，原因很关键：
            //    _isHovered 只由 WM_MOUSEMOVE 的「首次进入」分支（if (!_isTrackingMouse)）置 true、
            //    由 WM_MOUSELEAVE 置 false。拖放期间这两个消息都被 OLE 吞掉了，所以它现在可能不准。
            //    而拖放结束时 _isTrackingMouse 已经是 true —— 一旦在这里把它置成 false，
            //    鼠标哪怕还停在岛上，也再没有任何消息会把它恢复（首次进入分支不会再走）。
            //    后果是 WM_LBUTTONDOWN 里 `if (_isHovered && HasActiveDetailPage)` 这道门永远过不去，
            //    详情页彻底收不到左键 —— 表现就是「拖不动、也点不动」。
            //    悬停标志交给系统消息自己维护，这里只负责面板折叠时序。

            // ✅ 但要做这件事：把 TrackMouseEvent 的订阅强行作废。
            //    系统对 WM_MOUSELEAVE 是「只发一次、发完即失效」的，而拖放期间那次它发给了被 OLE
            //    接管的消息循环、我们根本没收到。订阅已经消耗掉、_isTrackingMouse 却还停在 true，
            //    于是「鼠标离开」永远不会再被检测到，_isHovered 也会一直挂着。
            //    置 false 之后，下一次 WM_MOUSEMOVE 会重新走「首次进入」分支，把状态拉回正轨。
            //    （WM_MOUSEMOVE 按岛体可见形状派发，分层窗口的透明像素不吃消息，所以这一支是可信的。）
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

        // ================= 🖱 岛体拖放（右键展开的详情页拖入 / 拖出） =================
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
        /// 这个方法会在主线程里进入 OLE 的模态循环，但宿主的渲染是独立计时器驱动的（见 _renderTimer），
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

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {            switch (msg)
            {
                // 📋 剪贴板内容变化（事件驱动，仅在复制/剪切导致剪贴板内容变化时触发一次读取；开关关闭直接忽略）
                case Win32.WM_CLIPBOARDUPDATE:
                    if (IsClipboardEnabled) _clipboardMonitor.HandleClipboardUpdate();
                    return (IntPtr)0;

                case Win32.WM_DESTROY:
                    // 📋 窗口销毁前反注册剪贴板监听，避免系统继续向已销毁窗口投递消息
                    _clipboardMonitor.Detach();
                    // 🖱 同理：OLE 那边还捏着一个指向本窗口的拖入目标，销毁前必须摘掉
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
                            // 🧩 鼠标回到岛上 → 取消两块展开面板挂起的延迟折叠（还没到期就当没发生过）
                            CancelPanelCollapse();
                        }

                        // 统一提炼坐标，大括号隔离作用域，彻底告别编译报错
                        int mx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int my = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        // 🧩 记录鼠标逻辑坐标，供插件组件的悬停判定使用
                        Renderer.UpdatePluginMouse(mx, my);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 🖱 详情页展开时把鼠标移动也转给它 —— 插件靠「按下之后位移超过阈值」来发起拖出，
                        //    没有这条就只能在按下那一瞬间进拖放循环，普通单击会被当成拖拽。
                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseMove(mx, my - hitTopY);

                        // 1. 最高优先级拦截：唤醒按钮热区（位置真源在 Renderer.WakeButtonX，与渲染共用）
                        //    两种「整块不可见」的形态都要短路：穿透睡眠态、完全隐藏态（岛体基准离开顶部）
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
                                if (Renderer.MediaInteractionMode == 1)
                                {
                                    // 🎵 展开交互：媒体模块自身就是「点击展开」的热区，所以鼠标落在它上面就给小手。
                                    //    组合模式下媒体只是岛体里的一段（左右还挨着时钟 / 硬件 / 插件），
                                    //    用渲染时登记的真实区间判定 —— 不能整岛都给小手，否则点时钟也会展开媒体。
                                    _isCursorOverIcon = Renderer.HitMediaZone(mx) && my >= hitTopY && my <= hitTopY + _currentHeight;
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
                    // 🎯 唤醒那次点击到此结束：解除岛外收起的抑制，之后用户再点岛外照常收起。
                    //    必须放在最前面 —— 上面拖动分支会 return，别让标记挂在拖动路径上漏掉。
                    _wakeClickPending = false;
                    // 🖱 详情页展开时把「抬起」也转给它（按住拖出的收尾全靠这条）。同样放在最前面，
                    //    免得被下面媒体拖动分支的 return 漏掉。
                    if (Renderer.HasActiveDetailPage)
                    {
                        int ux = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int uy = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        Renderer.DispatchDetailPageMouseUp(ux, uy - 12f * _currentStyleProgress);
                    }
                    // 🎵 松手：解除状态锁并把落点提交给播放器（拖动期间攒下的所有改动只在这一刻提交一次）
                    if (_media.IsDragging)
                    {
                        _media.EndDrag();
                        Win32.ReleaseCapture();
                        // 🧩 拖到岛外松手：拖动期间的 WM_MOUSELEAVE 被上面「拖动中不收起」的分支吃掉了，
                        //    松手后系统不会再来第二次，这里补一次判定，免得面板挂在岛外一直不收。
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
                        // 🖱 详情页展开时也通知一次「鼠标离开」：按下之后把鼠标拖出岛体再松手，
                        //    WM_LBUTTONUP 不会来，详情页只能靠这条复位「按住」状态。
                        if (Renderer.HasActiveDetailPage) Renderer.DispatchDetailPageMouseLeave();

                        // 🧩 鼠标离开灵动岛 → **展开的面板一律自动折叠**（媒体面板与插件详情页同一条管线，见 RequestPanelCollapse）。
                        //    WM_MOUSELEAVE 由系统按「岛体可见形状」派发（分层窗口的透明像素不吃鼠标消息），
                        //    所以这里判定等价于「鼠标真的离开了灵动岛」，不用轮询。
                        //    注意：时间轴拖动中已在上面的分支里 break 掉，不会误伤正在拖动的面板。
                        RequestPanelCollapse();
                        break;
                    }

                case Win32.WM_LBUTTONDOWN:
                    {
                        int cx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int cy = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float hitTopY = 12f * _currentStyleProgress;

                        // 完美对齐渲染中心点，精准拦截唤醒点击
                        if (Renderer.PassthroughModeEnabled && !_isPassthroughAwake && HitWakeButton(cx, cy))
                        {
                            _isPassthroughAwake = true;
                            return (IntPtr)0;
                        }

                        // 🌑 完全隐藏态（岛体基准离开顶部）：点岛体正中的唤醒按钮唤回，与穿透睡眠态同一条路径。
                        //     复用「手动展开」标记锁住显示 —— shouldHide 本来就排除 _isManuallyExpanded，
                        //     所以岛体立刻淡回可见；之后点岛外由既有的兜底轮询收回（无需新状态位）。
                        if (Renderer.FullHideAlpha < 0.99f && HitWakeButton(cx, cy))
                        {
                            _isManuallyExpanded = true;
                            return (IntPtr)0;
                        }

                        RaiseWindowClicked(cx, cy, "main-window");

                        // 「点击已隐藏的岛体把它唤回来」。
                        // ⚠️ 判据必须与 shouldHide 同源，一律读 CanAutoHideNow：
                        //    它已经含 IsAutoHideEffective（穿透模式下 auto-hide 已失效，但刚开启穿透时岛体可能
                        //    还在回滑动画里、_currentY 仍 < -5，此时点击不该被当成「唤醒」而莫名锁上手动展开），
                        //    也含「暂停后隐藏」与「全屏时隐藏」两种放宽模式 —— 写死 `!_media.IsActive`
                        //    会导致那两种模式下「藏得下去、点不回来」。
                        //    `_currentY < -5f` 是「确实已经藏起来了」的兜底闸门。
                        if (CanAutoHideNow && _currentY < -5f)
                        {
                            _isManuallyExpanded = true;
                            // 🎯 屏蔽掉「本次按键」引发的岛外点击收起判定。用户点的是屏幕顶边（y≈0），
                            //    而岛体下沉后可见区从 y=12 起，所以这次点击坐标天然在岛体之外；
                            //    不屏蔽的话岛刚滑回来就会被上面那段兜底轮询收走 —— 「抽一下又回去」。
                            _wakeClickPending = true;
                            return (IntPtr)0;
                        }

                        // 📋 剪贴板链接面板：命中右侧「打开」按钮 → 默认浏览器打开链接
                        if (isClipboardActive && Renderer.HitClipboardOpen(cx, cy - hitTopY))
                        {
                            OpenClipboardUrl();
                            return (IntPtr)0;
                        }

                        // 🧩 插件详情页展开时：岛内左键优先交给详情页。
                        //    先走新的「鼠标事件」通道（插件靠按下 + 移动的位移来发起拖出），
                        //    再走老的 HitTest / OnAction（详情页的 HitTest 返回 None 时会自然跳过）。
                        //    即使两边都没命中也消费掉这次点击，避免误触到底层原生媒体按钮。
                        if (_isHovered && Renderer.HasActiveDetailPage)
                        {
                            Renderer.DispatchDetailPageMouseDown(cx, cy - hitTopY);
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
                            else if (Renderer.MediaInteractionMode == 0)
                            {
                                // 折叠态播放按钮只在**直接交互**模式下绘制（见 Renderer.MediaWidget.cs 的
                                // DrawMediaInline），因此也只有该模式吃这里的点击；展开交互模式点这一带会
                                // 落到下面「点媒体区即展开面板」的分支，与「悬停不显示控件」保持一致。
                                // 位置与渲染侧共用同一个锚点：GetMediaRight 返回的就是媒体模块右缘。
                                float right = Renderer.GetMediaRight(Renderer.WINDOW_WIDTH, _currentWidth, _currentToast != null);
                                float btnStartY = (_currentHeight - 18f) / 2f + hitTopY;
                                if (cy >= btnStartY && cy <= btnStartY + 18f)
                                {
                                    if (cx >= right - 84 && cx <= right - 66) { _media.Previous(); hitButtons = true; }
                                    else if (cx >= right - 54 && cx <= right - 36) { _media.TogglePlayPause(); hitButtons = true; }
                                    else if (cx >= right - 24 && cx <= right - 6) { _media.Next(); hitButtons = true; }
                                }
                            }

                            // 🎵 展开交互：点在媒体模块上（且没点到按钮）就展开 —— 组合 / 非组合同一套判定，
                            //    热区用渲染时登记的媒体区间，所以组合模式下点时钟 / 硬件不会误展开媒体。
                            //    直接交互模式不提供展开入口（点空白处不做事）。
                            if (!hitButtons && Renderer.MediaInteractionMode == 1 && Renderer.HitMediaZone(cx))
                            {
                                ExpandPanel(Plugins.BuiltinWidgets.Media);
                            }
                        }
                        break;
                    }

                case Win32.WM_RBUTTONDOWN:
                    if (_isHovered)
                    {
                        // 🧩 岛内右键的优先级：详情页收起 → 插件组件广播 → 设置窗口。
                        //
                        // ⚠️ 原生媒体控制器区域（标题文字 / 歌词 / 频谱 / 播放按钮 / 空白）**一律不消费右键**，
                        //    整个媒体控制器的右键都只打开设置窗口 —— 用户 2026-09-19 明确要求。
                        //    早先这里有个「右键折叠态媒体标题 → 展开媒体面板」的快捷入口（05df004 加的），
                        //    它把标题文字那一段的右键整片吃掉（热区高 = 整个岛体高），用户想开设置窗口
                        //    还得精确点到岛体最右侧那条窄边。已整体删除，不再登记任何媒体标题热区。
                        int rx = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int ry = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        float rtY = 12f * _currentStyleProgress;

                        if (_currentToast == null)
                        {
                            // 详情页已展开：岛内右键直接收起详情页（此时插件行未绘制，无需再广播）
                            // 传 true：这是用户明确要关它，即使插件声明了「鼠标离开也不收起」也照收 ——
                            // 否则选了那一档的详情页就彻底没有关闭入口了。
                            if (Renderer.HasActiveDetailPage)
                            {
                                ClosePanelsNow(forceCloseDetail: true);
                                return (IntPtr)0;
                            }

                            string? detailWidget = Renderer.DispatchPluginRightClick(rx, ry - rtY);

                            // 主机默认行为：命中的组件提供了详情页 → 在灵动岛展开该组件的详情页（消费这次右键，不弹设置窗口）
                            if (detailWidget != null)
                            {
                                ExpandPanel(detailWidget);
                                return (IntPtr)0;
                            }
                        }

                        // 🖱️ 按「右键落在哪块原生内容上」直达对应设置页签（用户 2026-09-23 建议）：
                        //    媒体控制器 → 媒体设置；时间/日期、CPU/RAM → 显示设置；
                        //    其他（空白待机 / 插件行 / 剪贴板面板…）→ 保持原行为，打开设置窗口的当前页签。
                        //    命中区由渲染器本帧登记（Renderer.Layout.cs），所以通知 / 详情页接管岛体期间不会误命中。
                        int targetTab = Renderer.NativeRightClickTab(rx);
                        if (targetTab >= 0) ConsoleWindow.ShowTab(targetTab);
                        else ConsoleWindow.Toggle();
                    }
                    break;
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 展开指定组件（builtin.media 或插件组件 Id）的面板。
        /// 同一时刻只留一块：开这块之前先把另一块收掉。
        ///
        /// <para>
        /// 两个调用方：岛内左键/右键命中组件，以及<a>把文件拖到收起态组件上</a>时的自动展开
        /// （见 <see cref="IslandDropTarget"/>，组件需声明 <c>IWidget.AcceptsFileDropWhenCollapsed</c>）。
        /// 后者同样要先把两个折叠计时取消掉，否则刚展开的面板可能立刻被挂上收起计时。
        /// </para>
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
        /// 鼠标离开岛体：给两块面板各挂一个延迟折叠（媒体 <see cref="MediaCollapseDelayMs"/>、
        /// 详情页 <see cref="DetailCollapseDelayMs"/>），由 <see cref="TickPanelCollapse"/> 到期才真的折叠；
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
        /// 每帧调一次（RenderLoop），代价只有两次 <see cref="DateTime"/> 比较。
        /// </summary>
        private static void TickPanelCollapse()
        {
            if (_mediaPanelCollapse.Tick()) Renderer.IsMediaExpanded = false;

            if (!_detailPanelCollapse.Tick()) return;

            string? scheduled = _detailCollapseWidgetId;
            _detailCollapseWidgetId = null;

            // 只收当初挂时间戳的那一张：期间插件若已经换了别的详情页，说明用户在看新东西，不动它
            //
            // 🧲 这里必须再确认一次「插件此刻是否要求永不收起」：
            //    计时是几秒前挂上的，这中间插件的 AutoCollapseDelay 完全可能已经变成负值
            //    （同一个详情页改了策略）—— 挂计时那一刻检查过，不代表结算这一刻还成立。
            //    少了这一判，声明「永不收起」的面板会被一个几秒前埋下的计时器收掉。
            if (scheduled != null
                && Renderer.HasActiveDetailPage
                && !Renderer.ActiveDetailKeepsOpen
                && string.Equals(PluginManager.Instance.Host.ActiveDetailWidgetId, scheduled, StringComparison.OrdinalIgnoreCase))
            {
                PluginManager.Instance.Host.CloseDetailPage();
            }
        }

        /// <summary>立即折叠全部展开面板（岛外点击这种明确的用户动作，不延迟）。</summary>
        /// <param name="forceCloseDetail">
        /// true = 连声明了「鼠标离开也不收起」的详情页也一并收掉。
        /// 这个值专供「岛内右键」——那是用户明确冲着面板来的关闭手势，
        /// 若也尊重插件的不收起，插件选了这个档之后就<b>再也没有任何办法关掉它</b>了。
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
        /// 收起全部展开态：两块面板（**立即**）+ 自动隐藏唤醒出来的「手动展开」。
        ///
        /// 只在**岛外点击**时用（用户主动表达「我看完了」，再等延迟反而像卡住）。
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