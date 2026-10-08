using System.Text.Json;
using Msp.Core;
using NotchPeninsula.Plugins;

namespace NotchPeninsula;

/// <summary>
/// MSP（Model Sensing Protocol）原生接入桥 —— “local software” 级节点。
///
/// 与第一版（<c>Preset.LocalTool</c>，只宣告不发现、只应答不广播）的区别：
///   * **自定义预设 <c>LOCAL_SOFTWARE</c>**：共享目录的**发现 + 宣告**都开，两种基础模式
///     （<c>msp/request-reply</c> + <c>msp/publish-subscribe</c>）权限完整，四项能力
///     （pub / sub / req / rep）齐全，发现即连（eager）—— 否则收不到别人的广播。
///   * **广播信号**：把灵动岛自己产生的事件发到广播里（系统通知 / 插件提醒 / 媒体变化）。
///   * **订阅 notify 域的信号**：任何对端按约定前缀 <c>notify.**</c> 发布的信号，
///     都会在灵动岛上显示出来。
///
/// 信号路径遵循 MSP 的「域优先」约定 <c>&lt;域&gt;.&lt;实体&gt;.&lt;动作&gt;</c>
/// （见 ModelSensingProtocol 的 <c>docs/DESIGN.md</c> 命名规范章节）：
///   - <c>notify.toast.raised</c>      系统通知（Windows Toast）到达
///   - <c>notify.notchpeninsula.reminder.raised</c>   宿主内部提醒（插件 PostReminder）
///   - <c>media.track.changed</c>      曲目变化（标题 / 艺术家 / 应用）
///   - <c>media.playback.changed</c>   播放状态变化（播放 / 暂停 / 会话有无）
///
/// <para>
/// <b>为什么 <c>reminder</c> 要多一段 <c>notchpeninsula</c></b>：上面三条的实体
/// （<c>toast</c> / <c>track</c> / <c>playback</c>）都是系统里客观存在、换个软件也讲得通的
/// 来源；而「提醒」是宿主自己的插件 API 概念（<c>IPluginHost.PostReminder</c>），
/// 别的软件没有这个东西。直接写成 <c>notify.reminder.raised</c> 等于把私有概念
/// 伪装成通用信号，对端没办法据此判断「这条到底是不是标准通知」。
/// 加一段应用名之后，语义变成「notify 域下、来源是 notchpeninsula 的提醒」——
/// 既说清了私有归属，又仍然落在 <c>notify.**</c> 里，对端用一个过滤器就能全收。
/// 这也是 MSP 文档「域优先」那条理由最直接的用法：域稳定、实体可变。
/// </para>
///
/// <para>
/// <b>为什么要有「点对点」和「广播里」两套通知语义</b>（写给以后的维护者）：
/// <list type="bullet">
///   <item><c>notify</c> <b>工具</b>是 request-reply，点对点：让这一台灵动岛弹一下，
///         不广播。<b>不</b>广播的理由见下面的回声问题。</item>
///   <item>要广播给别人看，对端自己 <c>Publish("notify.&lt;实体&gt;.&lt;动作&gt;")</c> 就行 ——
///         那才是 pub-sub 该干的事。</item>
///   <item>本机**自发**的事件（系统 Toast、插件提醒）无人点单，属于广播语义，自动广播。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>notify 载荷约定</b>（信号 payload 与 <c>notify</c> 工具形参共用一套形状）：
/// <code>
/// {
///   "kind":     "来源标识",        // 可选
///   "title":    "标题",            // 文本降级通道
///   "body":     "正文",
///   "duration": 6,                 // 可选，秒；宿主夹到 1~60
///   "content":  [ ...内容块... ]   // 可选，MSP/MCP 的 content block
/// }
/// </code>
/// <list type="number">
///   <item><c>title</c> / <c>body</c> 是**降级通道**，<c>content</c> 是富内容通道，推荐两个都给：
///         只想要文字的对端读前两个字段即可，不必解析内容块。</item>
///   <item><c>content</c> 的元素就是 MSP 的六种 content block（text / image / audio /
///         resource / resource_link / blob_ref）。宿主按自己的渲染能力取用，
///         <b>不认识的 type 直接跳过</b>，不影响整条通知显示 —— 与
///         <c>ContentBlock.From</c> 的宽容读取行为一致。</item>
///   <item><b>图标是可选的，而且没有图标才是主路径</b>。取法分两级：
///         先找第一个 <c>annotations.role == "icon"</c> 的 <c>image</c> 或
///         <c>resource_link</c> 块；一个都没有时，直接拿第一个 <c>image</c> /
///         <c>resource_link</c> 块当图标 —— 一条通知载荷里的图像块，唯一合理的读法就是图标，
///         不该强迫发送端为每张图写 annotations（何况 <c>resource_link</c> 上的 annotations
///         会被 MSP 自己的解析器丢掉，见 <see cref="ParseBlocks"/>）。
///         整条通知没带 <c>content</c>、或块里压根没图 → 用默认图标。
///         两种形态最终都收敛成一个「图标描述」字符串交给宿主已有的 ToastIconProvider：
///         <c>resource_link.uri</c> 直接透传（内置别名 / 图片链接 / file:// / 本地路径），
///         <c>image</c> 块拼成 <c>data:&lt;mime&gt;;base64,&lt;data&gt;</c>。
///         所以「无图标」路径不引入任何新分支，行为与加这个功能之前逐字一致。</item>
///   <item><c>title</c> 与 <c>body</c> 都为空时，才回头拿 <c>content</c> 里第一个
///         <c>text</c> 块当正文。</item>
/// </list>
/// 域特定字段（例如 toast 的 <c>aumid</c> / <c>receivedAt</c>）允许与上面这些并列共存。
/// </para>
///
/// <para>
/// <b>回声问题</b>：从对端收到的通知要显示，就得走 <c>PostReminder</c>；而 <c>PostReminder</c>
/// 正好是「本机自发提醒」的广播触发点。若不加以区分，A 显示 → A 广播 → B 显示 → B 广播 →
/// A 显示……两个都订阅了通知的节点会无限互转。这里用 <see cref="_suppressBroadcast"/>
/// 在同一次同步调用内打断回程（<c>ReminderPosted</c> 是同步 <c>Invoke</c>，所以线程静态标志足够）。
/// 另外已实测「节点自己 Publish 的信号不会回流触发自己的 On」，所以不存在本地自环。
/// </para>
///
/// <para>
/// 已知代价：订阅 <see cref="PluginDataBridge.MediaChanged"/> 会启动宿主的 250ms 媒体采样器，
/// 且本桥的订阅是常驻的，所以采样器会在进程生命周期内一直跑（这正是该桥设计里的
/// 「有订阅者才采样」契约，只是订阅者永不归零）。采样本身只是几次属性读取 + 拼串，
/// 但会让一份媒体快照和封面 PNG 常驻内存。
/// </para>
/// </summary>
public static class MspNotchBridge
{
    /// <summary>节点 id（也是共享目录里的声明文件名 <c>~/.msp/nodes/notchpeninsula.json</c>）。</summary>
    public const string NodeId = "notchpeninsula";

    /// <summary>
    /// 总开关（设置 → 通用设置 → MSP 接入，界面上标注为**实验性**）。**默认关闭。**
    ///
    /// 为什么默认关：打开它会**监听一个本地 TCP 端口**，并让本机任何 MSP 节点都能弹通知、
    /// 读甚至控制系统媒体 —— 这是对外暴露的接口面，不该在用户不知情的情况下开着。
    /// 由 <c>Core/Program.cs</c> 从注册表读入；设置界面里改完立刻生效（走 <see cref="Enable"/>）。
    /// </summary>
    public static bool IsEnabled;

    // ---- 自定义预设：local software 级 ----
    //
    // 对照内置预设看差异：
    //   LocalTool  = 发现[]      宣告[directory] 能力[pub,rep]        lazy   ← 只应答，看不见别人
    //   LocalAi    = 发现[directory] 宣告[directory] 能力[pub,sub,req,rep] eager ← 全模式（含 multi-round）
    //   LOCAL_SOFTWARE（本预设）= 同 LocalAi 的发现/宣告/能力与 eager，但**只给两种基础模式**：
    //   不要 multi-round —— 那是给「工具反过来向调用方追问」的交互式场景用的，
    //   灵动岛只会同步地回一个结果，用不到；宣告了自己不实现的模式反而是撒谎。
    //
    //   ⚠️ eager 的已知代价（写给以后想改成 lazy 的人）：两侧都 eager 时双方会互相拨号，
    //   协议层按 node_id 字典序去重、只保留规范方向，另一条被关掉；对端「连上就立刻调用」
    //   有可能正好走在被关掉的那条上，表现成「连接断开」或超时（实测连续跑时 2/3 中招）。
    //   这是协议层的连接去重行为，**不是本节点能绕开的**：lazy 看似能躲掉，但那会让本节点
    //   退化成「只接受拨入」，不符合这个「本机软件」角色的定位。
    //   正确做法是对端首调重试一次（见 NPS_MSP.md 的常见问题）。
    private static readonly Preset LocalSoftware = new(
        Name: "LOCAL_SOFTWARE",
        Discovery: new HashSet<string> { "directory" },                      // 扫描共享目录，看得见别人
        Presence: new HashSet<string> { "directory" },                       // 把自己写进共享目录
        Transports: new HashSet<string> { "tcp" },
        Patterns: new HashSet<string> { "msp/request-reply", "msp/publish-subscribe" },
        PrimaryTransport: "tcp",
        Capabilities: new HashSet<string> { "pub", "sub", "req", "rep" },    // 四项齐全
        ConnectPolicy: "eager");                                             // 发现即连，不然收不到广播

    // ---- 本节点会广播的信号路径 ----
    public const string SignalToastRaised = "notify.toast.raised";
    public const string SignalReminderRaised = "notify.notchpeninsula.reminder.raised";
    public const string SignalTrackChanged = "media.track.changed";
    public const string SignalPlaybackChanged = "media.playback.changed";

    /// <summary>订阅的过滤器：notify 域下的**任意深度**（已实测 <c>**</c> 匹配 ≥1 段，<c>*</c> 只匹配 1 段）。</summary>
    private const string NotifySubscription = "notify.**";

    // ---- content block 里用来标记「这块是图标」的注解 ----
    // 取值约定见类注释：content 里第一个 role == "icon" 的 image / resource_link 块即图标。
    private const string AnnotationsKey = "annotations";
    private const string RoleKey = "role";
    private const string IconRole = "icon";

    /// <summary>通知默认显示时长（秒）。ReminderData.Duration 缺省是 4 秒，MSP 这边统一给 6 秒。</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(6);

    private static MspNode? _node;
    private static DateTime _startedAt;

    // 宿主事件订阅句柄：退订要用同一个委托实例，所以留着。
    private static Action<ToastNotice>? _onSystemNotification;
    private static Action<ToastData>? _onReminderPosted;
    private static Action<MediaSnapshot>? _onMediaChanged;

    // ---- 去重 / 基线状态（各自只在对应回调里用，加锁只为防采样器回调重入）----
    private static readonly object _stateGate = new();
    private static long _lastNoticeId = -1;
    private static string _lastTrackKey = "";
    private static bool _lastPlaying;
    private static bool _lastActive;
    private static bool _mediaBaselineSet;

    /// <summary>
    /// 「这条提醒别广播出去」的抑制标志。<b>线程静态</b>：只在发起 PostReminder 的那次同步调用里有效，
    /// 别的线程同时在投递本机提醒时不受影响。详见类注释里的回声问题。
    /// </summary>
    [ThreadStatic] private static bool _suppressBroadcast;

    /// <summary>当前 MSP 节点实例；未启动时为 null。</summary>
    public static MspNode? Node => _node;

    /// <summary>启动节点（幂等）。默认使用跨语言共享的目录发现目录 ~/.msp/nodes。</summary>
    public static void Start(string nodesDir = "~/.msp/nodes")
    {
        if (_node != null) return;
        _startedAt = DateTime.UtcNow;

        var node = new MspNode(NodeId, LocalSoftware, nodesDir);
        try
        {
            RegisterTools(node);
            DeclareSignals(node);

            // 订阅 notify 域的信号。返回值是退订委托，但节点一停就整个作废，
            // 而 Stop() 之后要么进程退出、要么丢掉整个 MspNode，不存在残留订阅，所以不存它。
            node.On(NotifySubscription, OnPeerNotify);

            AttachHostEvents();
            node.Start();

            _node = node;
            Logger.Info($"[MSP] 原生接入已启动：节点 {NodeId}（预设 {LocalSoftware.Name}，"
                        + $"发现+宣告 + 两模式 + pub/sub/req/rep，{node.Endpoint?.ToString() ?? "未宣告"}）");
        }
        catch
        {
            // 半路失败要把已经挂上的宿主事件摘掉，否则一个没启动的桥会一直收通知。
            DetachHostEvents();
            try { node.Stop(); } catch { /* 清理失败无所谓，反正要往上抛 */ }
            throw;
        }
    }

    /// <summary>停止节点（幂等）。撤下共享目录里的声明文件，并摘掉全部宿主事件订阅。</summary>
    public static void Stop()
    {
        var node = _node;
        _node = null;

        DetachHostEvents();

        try { node?.Stop(); }
        catch (Exception ex) { Logger.Warn($"[MSP] 节点停止失败: {ex.Message}"); }

        if (node != null) Logger.Info("[MSP] 原生接入已停止");

        // 复位基线：重启后第一次采样只建立基线，不该被当成「变化」广播出去。
        lock (_stateGate)
        {
            _lastNoticeId = -1;
            _lastTrackKey = "";
            _lastPlaying = false;
            _lastActive = false;
            _mediaBaselineSet = false;
        }
    }

    /// <summary>
    /// 设置界面用：打开 / 关闭并**立刻生效**，不用重启。
    ///
    /// 打开失败时会把开关拨回关闭 —— 免得界面显示「已开启」而实际上节点根本没起来
    /// （端口被占、被安全软件拦下都可能）。关闭是幂等的，不会失败。
    /// </summary>
    public static void Enable(bool on)
    {
        if (on == IsEnabled) return;

        if (on)
        {
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Logger.Error("[MSP] 打开失败，开关已拨回关闭", ex);
                IsEnabled = false;
                return;
            }
        }
        else
        {
            Stop();
        }

        IsEnabled = on;
        Logger.Info($"[MSP] 用户{(on ? "开启" : "关闭")}了 MSP 接入");
    }

    // ---- 工具（request-reply 模式） ----

    private static void RegisterTools(MspNode node)
    {
        node.Tool("ping", () => Ping(node), "健康检查：返回节点标识、预设、运行时长与已连接对端");

        // 刻意传**方法组**而不是 lambda：Notify 的 content 形参带默认值（= null），
        // SDK 是从 MethodInfo 上读默认值的，只有方法组才保得住 —— 于是自动生成的
        // inputSchema 会把 content 留在 properties 里但不进 required，也就是「可选」。
        // 换成 lambda 的话 required 会误带上 content（实测），schema 就撒谎了。
        node.Tool("notify", (Func<string, string, string, List<JsonElement>, object>)Notify,
            "在灵动岛顶部弹出一条通知。点对点，不广播到 notify 域。"
            + "title/body 是文本；content 可选，是 MSP/MCP 的 content block 数组，"
            + "其中 annotations.role == \"icon\" 的 image / resource_link 块会被当作图标（不传就用默认图标）");

        node.Tool("media_status", () => MediaStatus(), "只读当前媒体播放状态（活动/播放/标题/艺术家/进度）");
        node.Tool("media_control", (string action) => MediaControl(action),
            "控制媒体：toggle / play / pause / next / prev");
    }

    /// <summary>宣告本节点会发布的信号，让对端能通过 <c>peer.Signals()</c> / <c>HasSignal()</c> 看到。</summary>
    private static void DeclareSignals(MspNode node)
    {
        node.DeclareSignal(SignalToastRaised, description: "系统通知（Windows Toast）到达");
        node.DeclareSignal(SignalReminderRaised, description: "宿主内部提醒（插件 PostReminder）");
        node.DeclareSignal(SignalTrackChanged, description: "媒体曲目变化（标题 / 艺术家 / 应用）");
        node.DeclareSignal(SignalPlaybackChanged, description: "媒体播放状态变化（播放 / 暂停 / 会话有无）");
    }

    // ---- 宿主事件 → 广播 ----

    private static void AttachHostEvents()
    {
        _onSystemNotification = OnSystemNotification;
        PluginDataBridge.SystemNotification += _onSystemNotification;

        _onReminderPosted = OnReminderPosted;
        PluginManager.Instance.Host.ReminderPosted += _onReminderPosted;

        // 订阅即启动宿主的媒体采样器（250ms），并会在本桥存续期间常驻。
        _onMediaChanged = OnMediaChanged;
        PluginDataBridge.MediaChanged += _onMediaChanged;
    }

    private static void DetachHostEvents()
    {
        if (_onSystemNotification != null)
        {
            try { PluginDataBridge.SystemNotification -= _onSystemNotification; } catch { }
            _onSystemNotification = null;
        }

        if (_onReminderPosted != null)
        {
            try { PluginManager.Instance.Host.ReminderPosted -= _onReminderPosted; } catch { }
            _onReminderPosted = null;
        }

        if (_onMediaChanged != null)
        {
            // 退订会让宿主采样器归零停表（本桥没有调过 EnsureStarted，所以这里能真的停掉）。
            try { PluginDataBridge.MediaChanged -= _onMediaChanged; } catch { }
            _onMediaChanged = null;
        }
    }

    /// <summary>系统通知到达 → 广播 <c>notify.toast.raised</c>。</summary>
    private static void OnSystemNotification(ToastNotice notice)
    {
        // 同一条通知会发两帧（首帧用兜底图标抢时间，第二帧补真实图标），NoticeId 相同、
        // Version 递增。只在首帧广播，否则对端会把同一条通知显示两遍。
        if (notice.Version != 1) return;

        lock (_stateGate)
        {
            if (notice.NoticeId == _lastNoticeId) return;
            _lastNoticeId = notice.NoticeId;
        }

        // 带上图标块。首帧的 IconPng 是宿主当场能拿到的那张（自定义图 / data\image 里按
        // 进程名或应用名命中的内置图标 / 兜底的 windows 图标），对绝大多数应用来说就是它自己的图标。
        // 诚实记一笔：第二帧才是 WinRT 取到的「真·应用 logo」，但我们不重复广播第二帧，
        // 所以对端拿到的可能是兜底图标。要更准就得再定一条「补图」信号，那是另一个话题。
        var icon = ImageBlock(notice.IconPng, IconRole);

        Publish(SignalToastRaised, new
        {
            kind = notice.AppName,
            title = notice.Title,
            body = notice.Body,
            content = icon == null ? null : new[] { icon.Value },
            aumid = notice.Aumid,
            receivedAt = notice.ReceivedUtc.ToString("o"),
        });
    }

    /// <summary>宿主内部提醒（插件 PostReminder）→ 广播 <c>notify.notchpeninsula.reminder.raised</c>。</summary>
    private static void OnReminderPosted(ToastData toast)
    {
        // 从对端收到的通知也走 PostReminder 去显示，但绝不能再广播回去 —— 否则两个都接了
        // 通知的节点会互相把对方的通知无限转发下去。详见类注释的「回声问题」。
        if (_suppressBroadcast) return;

        Publish(SignalReminderRaised, new
        {
            kind = toast.AppName,
            title = toast.Title,
            body = toast.Body,
        });
    }

    /// <summary>媒体状态变化 → 按「事件级」而非「进度级」广播。</summary>
    private static void OnMediaChanged(MediaSnapshot m)
    {
        // 这个回调在 250ms 采样线程上触发，而且签名里含歌词与进度 —— 也就是每秒可能好几次。
        // 广播只关心事件级的变化：曲目换了、播放状态变了。进度一滴一滴地发会把广播刷屏，
        // 所以这里自己再聚一次，只认「曲目键」和「播放态」两种跃迁。
        string trackKey = string.Concat(m.Title, "\u0001", m.Artist, "\u0001", m.AppId);

        bool trackChanged;
        bool playbackChanged;
        bool isBaseline;

        lock (_stateGate)
        {
            isBaseline = !_mediaBaselineSet;
            trackChanged = !isBaseline && trackKey != _lastTrackKey;
            playbackChanged = !isBaseline && (m.IsPlaying != _lastPlaying || m.IsActive != _lastActive);

            _lastTrackKey = trackKey;
            _lastPlaying = m.IsPlaying;
            _lastActive = m.IsActive;
            _mediaBaselineSet = true;
        }

        // 首帧只建立基线：程序刚起来 / 刚重启时不该往广播里喷一条「媒体状态」。
        if (isBaseline) return;

        if (trackChanged)
        {
            Publish(SignalTrackChanged, new
            {
                title = m.Title,
                artist = m.Artist,
                app = m.AppId,
                playing = m.IsPlaying,
            });
        }

        if (playbackChanged)
        {
            Publish(SignalPlaybackChanged, new
            {
                active = m.IsActive,
                playing = m.IsPlaying,
                title = m.Title,
                artist = m.Artist,
            });
        }
    }

    // ---- 订阅 notify 域的信号 ----

    /// <summary>收到对端 <c>notify.*</c> 广播 → 在灵动岛上显示（不再转发，避免回声放大）。</summary>
    private static void OnPeerNotify(Signal sig)
    {
        // 已实测自己 Publish 的信号不会回流到自己的 On；这里再兜一层，
        // 免得将来 SDK 改成「本地也派发」之后变成自环。
        if (string.Equals(sig.Source, NodeId, StringComparison.Ordinal)) return;

        var n = ParseNotify(sig.Payload);
        if (string.IsNullOrWhiteSpace(n.Title) && string.IsNullOrWhiteSpace(n.Body)) return;

        // 来源标签：优先用发送方自己给的 kind（那是它给自己的定位），
        // 没给就退回节点 id —— 总比所有通知都顶着同一个名字强。
        string peer = string.IsNullOrWhiteSpace(sig.Source) ? "MSP" : sig.Source!.Trim();
        string source = string.IsNullOrWhiteSpace(n.Kind) ? peer : n.Kind;

        // 标题不再裹 [来源] 前缀：来源已经由宿主那一栏专门显示了，前缀纯属重复。
        string title = n.Title.Length > 0 ? n.Title : n.Body;

        ShowOnIsland(title, n.Body, n.IconSpec, source, n.Duration);

        // 措辞是「已受理」不是「已显示」：投递走后台（见 ShowOnIsland），
        // 这一行只保证请求被接下了、载荷解析没问题。真正落到岛上由宿主那行
        // [PluginHost] 插件提醒已投递 作证。
        Logger.Info($"[MSP] 对端通知已受理：{sig.Path} ← {peer}（{title}）"
                    + $"，来源: {source}"
                    + $"，图标: {ToastIconProvider.Describe(n.IconSpec)}"
                    + (n.ExtraBlocks > 0 ? $"，另有 {n.ExtraBlocks} 个块未渲染" : ""));
    }

    // ---- notify 载荷解析（信号 payload 与 notify 工具共用） ----

    /// <summary>
    /// 解析出来的一条通知：来源标识 + 文本降级通道 + 可选图标描述 + 没能渲染的块数。
    /// <c>Kind</c> 就是载荷里的 <c>kind</c>，用来当岛上的「来源」标签。
    /// </summary>
    private readonly record struct NotifyPayload(
        string Kind, string Title, string Body, string? IconSpec, TimeSpan Duration, int ExtraBlocks);

    /// <summary>
    /// 按类注释里的 notify 约定解析一个 payload 对象。
    /// 任何一步失败都退化成「尽量能显示」，绝不因为一个坏块把整条通知丢掉。
    /// </summary>
    private static NotifyPayload ParseNotify(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new NotifyPayload("", "", "", null, DefaultDuration, 0);

        string kind = ReadString(payload, "kind", "Kind", "source", "app", "AppName");
        string title = ReadString(payload, "title", "Title", "name");
        string body = ReadString(payload, "body", "Body", "text", "message");
        var duration = ReadDuration(payload);

        string? icon = null;
        string textFallback = "";
        int extra = 0;

        if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            (icon, textFallback, extra) = ParseBlocks(content.EnumerateArray());

        // title/body 是权威文本；两个都空才回头用 content 里的 text 块。
        if (string.IsNullOrWhiteSpace(body)) body = textFallback;

        return new NotifyPayload(kind, title, body, icon, duration, extra);
    }

    /// <summary>
    /// 遍历内容块，挑出图标块与正文兜底块，其余按「宿主渲染不了」计数。
    ///
    /// 图标的两级判定（<b>显式标记优先，没标记就取第一个图像块</b>）：
    ///   1. 第一个 <c>annotations.role == "icon"</c> 的 image / resource_link 块；
    ///   2. 一个都没有时，第一个 image / resource_link 块 —— 因为一条**通知**载荷里的
    ///      图像块，唯一合理的读法就是图标；而「记得给每张图写 annotations」这件事既啰嗦，
    ///      又有下面这个坑，所以不能让显式标记成为必需品。
    ///
    /// <b>annotations 必须从原始块读</b>：<c>Msp.Core.ResourceLink</c> 没有 Annotations 成员
    /// （Python 侧 <c>ResourceLink</c> 同样没有），所以 <c>ContentBlock.From</c> → <c>ToElement</c>
    /// 这一趟往返会把 resource_link 上的 <c>role=icon</c> 标记悄悄丢掉（MCP 规范里
    /// ResourceLink 是带 annotations 的，这是 MSP 的一处缺口）。image 块没这个问题。
    ///
    /// 已知的解析器走 <see cref="ContentBlock.From"/>：它是协议自带的，已知类型会被规范化
    /// （字段名统一 camelCase，顺带容错），未知 type 原样返回 —— 「宽容读取」由协议层保证。
    /// </summary>
    private static (string? IconSpec, string TextFallback, int ExtraBlocks) ParseBlocks(
        IEnumerable<JsonElement> rawBlocks)
    {
        string? markedIcon = null;     // 显式标了 role=icon 的第一个
        string? firstImageish = null;  // 没标记，但长得像图标的第一个
        int imageishCount = 0;

        string textFallback = "";
        int extra = 0;

        foreach (var raw in rawBlocks)
        {
            if (raw.ValueKind != JsonValueKind.Object) { extra++; continue; }

            // 角色标记读原始块；字段值读规范化后的块（换 camelCase 容错）。
            bool marked = IsIconBlock(raw);
            JsonElement el = NormalizeBlock(raw);
            string type = ReadString(el, "type");

            string? spec = type switch
            {
                // 内嵌图 → data URI。宿主已有的 ToastIconProvider 认这个格式。
                "image" => ImageSpecOf(el),
                // 资源引用 → uri 直接透传（内置别名 / 图片链接 / file:// / 本地路径都认）。
                "resource_link" => ReadString(el, "uri"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(spec))
            {
                imageishCount++;
                if (marked && markedIcon == null) markedIcon = spec;
                firstImageish ??= spec;
                continue;
            }

            if (type == "text" && string.IsNullOrWhiteSpace(textFallback))
            {
                textFallback = ReadString(el, "text");
                continue;
            }

            // audio / resource / blob_ref / 未知 type（以及没解析出 uri、data 的图像块）：
            // 宿主目前画不出来，计数上报但不报错 —— 约定的第 2 条。
            extra++;
        }

        string? icon = markedIcon ?? firstImageish;

        // 除了被选作图标的那一张，其余图像块也是「画不出来」的，一并计入。
        if (icon != null && imageishCount > 1) extra += imageishCount - 1;

        return (icon, textFallback, extra);
    }

    /// <summary>把块过一遍协议自带的解析器，拿到字段名规范化的版本；失败就退回原始块。</summary>
    private static JsonElement NormalizeBlock(JsonElement raw)
    {
        try
        {
            return ContentBlock.From(raw) is IContentBlock cb ? cb.ToElement() : raw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[MSP] 内容块解析失败，按原始 JSON 读: {ex.GetType().Name}: {ex.Message}");
            return raw;
        }
    }

    /// <summary>这块是不是被标成了图标（<c>annotations.role == "icon"</c>）。</summary>
    private static bool IsIconBlock(JsonElement block)
    {
        if (!block.TryGetProperty(AnnotationsKey, out var ann) || ann.ValueKind != JsonValueKind.Object)
            return false;

        return string.Equals(ReadString(ann, RoleKey), IconRole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 image 块拼成 ToastIconProvider 认的 data URI。</summary>
    private static string? ImageSpecOf(JsonElement block)
    {
        string data = ReadString(block, "data");
        if (string.IsNullOrWhiteSpace(data)) return null;

        string mime = ReadString(block, "mimeType");
        if (string.IsNullOrWhiteSpace(mime)) mime = "image/png";

        return $"data:{mime};base64,{data}";
    }

    /// <summary>把一段 PNG 字节包成 <c>role=icon</c> 的 image 块（出站广播用）。</summary>
    private static JsonElement? ImageBlock(byte[]? png, string role)
    {
        if (png == null || png.Length == 0) return null;

        try
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "image",
                data = Convert.ToBase64String(png),
                mimeType = "image/png",
                annotations = new Dictionary<string, string> { [RoleKey] = role },
            });
        }
        catch (Exception ex)
        {
            Logger.Debug($"[MSP] 图标块构造失败: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>可选的 duration（秒），夹到 1~60；缺省 / 非法就给默认值。</summary>
    private static TimeSpan ReadDuration(JsonElement payload)
    {
        if (!payload.TryGetProperty("duration", out var d)) return DefaultDuration;

        double seconds;
        if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var num)) seconds = num;
        else if (d.ValueKind == JsonValueKind.String && double.TryParse(d.GetString(), out var parsed)) seconds = parsed;
        else return DefaultDuration;

        if (double.IsNaN(seconds) || seconds <= 0) return DefaultDuration;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    private static string ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        }
        return "";
    }

    // ---- 工具实现 ----

    private static object Ping(MspNode node) => new
    {
        node = node.NodeId,
        preset = node.Preset?.Name,
        patterns = node.Preset?.Patterns?.ToList(),
        capabilities = node.Preset?.Capabilities?.ToList(),
        uptime_sec = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
        peers = node.Connections(),
    };

    /// <summary>
    /// <c>notify</c> 工具。形参形状与 <c>notify.*</c> 信号 payload 一致 ——
    /// 同一份约定，两个载体（点对点调用 / 广播）。
    ///
    /// <b>content 必须是可选形参</b>（<c>= null</c>）：不带 content 的纯文本通知是最普遍的情况，
    /// 不能因为「现在支持块了」就要求调用方每次都给数组。
    /// </summary>
    private static object Notify(string kind, string title, string body, List<JsonElement>? content = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)
            && (content == null || content.Count == 0))
        {
            return new { ok = false, error = "title / body / content 至少要给一个" };
        }

        string? icon = null;
        string textFallback = "";
        int extra = 0;

        // content 传的是裸数组，不是 payload 对象，所以直接喂给块解析。
        if (content is { Count: > 0 })
            (icon, textFallback, extra) = ParseBlocks(content);

        // 来源标签：工具是 SDK 按参数名派发的，**处理函数拿不到调用方是谁**
        // （已实测：往形参里加 source / ctx / sender / from 都不会被注入，一律是 null）。
        // 所以点对点这条路只能信调用方自己给的 kind —— 它的定义本来就是「来源标识」。
        var kindLabel = string.IsNullOrWhiteSpace(kind) ? "MSP" : kind.Trim();
        string effectiveBody = string.IsNullOrWhiteSpace(body) ? textFallback : body;
        string effectiveTitle = string.IsNullOrWhiteSpace(title) ? effectiveBody : title;

        // 标题不再裹 [来源] 前缀：来源由宿主那一栏专门显示，前缀纯属重复。
        ShowOnIsland(effectiveTitle, effectiveBody, icon, kindLabel, DefaultDuration);

        return new
        {
            ok = true,
            kind = kindLabel,
            source = kindLabel,
            title = effectiveTitle,
            body = effectiveBody,
            // 回执里只报图标的「描述」，绝不把 base64 原样吐回去 —— 那会把一次 tool_result 撑到几 MB。
            icon = ToastIconProvider.Describe(icon),
            blocks = extra,
        };
    }

    private static object MediaStatus()
    {
        var c = MediaController.Instance;
        if (c == null)
            return new { active = false, playing = false, title = "", artist = "", elapsed = "0:00", progress = 0f };

        return new
        {
            active = c.IsActive,
            playing = c.IsPlaying,
            title = c.Title,
            artist = c.Artist,
            elapsed = c.TimelineElapsed,
            progress = c.TimelineProgress,
        };
    }

    private static object MediaControl(string action)
    {
        var c = MediaController.Instance;
        if (c == null)
            return new { ok = false, error = "媒体引擎未初始化" };

        var act = action?.Trim().ToLowerInvariant();
        switch (act)
        {
            case "toggle":
                c.TogglePlayPause();
                break;
            case "play":
                if (!c.IsPlaying) c.TogglePlayPause();
                break;
            case "pause":
                if (c.IsPlaying) c.TogglePlayPause();
                break;
            case "next":
                c.Next();
                break;
            case "prev":
            case "previous":
                c.Previous();
                break;
            default:
                return new { ok = false, error = $"未知动作 '{action}'，可用：toggle / play / pause / next / prev" };
        }

        return new { ok = true, action = act };
    }

    // ---- 内部 ----

    /// <summary>
    /// 展示任务的串行链。见 <see cref="ShowOnIsland"/>：投递走后台 + 串成一条链，
    /// 既保证 RPC 立刻返回，又保证先到的通知先显示。
    /// </summary>
    private static Task _showChain = Task.CompletedTask;
    private static readonly object _showGate = new();

    /// <summary>
    /// 在灵动岛上显示一条提醒，且**不广播**（点对点语义）。
    ///
    /// <b>绝不在调用线程上同步执行宿主的展示链路。</b> 这是条不变量，不是「现在恰好很快」：
    /// <list type="bullet">
    ///   <item><c>Duration</c> 是「显示多久」，与「这次调用耗时多久」毫无关系 —— 宿主的
    ///         <c>PostReminder</c> 只是把 <c>_toastEndTime</c> 往未来推，渲染循环到点自己撤，
    ///         整条路径没有任何 <c>Sleep(Duration)</c>。</item>
    ///   <item>但 <c>PostReminder</c> 会**同步** <c>Invoke</c> <c>ReminderPosted</c>，
    ///         而那个事件是公开的：任何订阅方（插件、以后加的宿主逻辑）只要在里面阻塞，
    ///         就会沿着 RPC 一路传出去，变成**对端眼里的超时**。</item>
    ///   <item>所以这里 fire-and-forget：<c>notify</c> 的回执语义是「已受理」，不是「已显示完」。
    ///         这是 RPC 该有的语义 —— 显示是个持续若干秒的副作用，本来就不该让它决定调用耗时。</item>
    /// </list>
    /// 用 <c>ContinueWith</c> 串成一条链而不是裸 <c>Task.Run</c>：线程池不保证顺序，
    /// 连发两条通知时后一条可能先执行、再被前一条覆盖。串起来就还是「先到先显示」。
    /// </summary>
    private static void ShowOnIsland(string title, string body, string? iconSpec, string source, TimeSpan duration)
    {
        // iconSpec 为 null 时就是「无图标」这条主路径：IconPath 给 null，宿主 PostReminder
        // 内部对 null 的图标描述是直接 return，随后回退到默认图标 —— 整条路径与加 content block
        // 之前逐字一致，没有多出任何判断。
        //
        // source 是岛上那一栏「来源」标签。不给的话宿主会写死成「插件提醒」，
        // 所有 MSP 通知来源就全糊成一样（见 Plugin/PluginHost.cs 的 PostReminder）。
        var reminder = new ReminderData
        {
            Title = title,
            Body = body,
            Source = string.IsNullOrWhiteSpace(source) ? "MSP" : source,
            IconPath = string.IsNullOrWhiteSpace(iconSpec) ? null : iconSpec,
            Duration = duration,
        };

        lock (_showGate)
        {
            _showChain = _showChain.ContinueWith(_ => PostReminderSafely(reminder), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 在后台链上真正投递。
    /// <c>_suppressBroadcast</c> 是线程静态的、且 <c>ReminderPosted</c> 是同步 Invoke，
    /// 所以标志必须和 <c>PostReminder</c> 在同一次调用里设置与清除 —— 放在这里正好。
    /// </summary>
    private static void PostReminderSafely(ReminderData reminder)
    {
        _suppressBroadcast = true;
        try
        {
            PluginManager.Instance.Host.PostReminder(reminder);
        }
        catch (Exception ex)
        {
            // 已经跑到后台了，异常抛出去没人接 —— 记日志，绝不静默吞。
            // 代价说明：对端此时拿到的回执已经是 ok（已受理），所以投递失败只能靠日志发现。
            Logger.Warn($"[MSP] 通知投递失败（对端已收到受理回执）: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _suppressBroadcast = false; }
    }

    /// <summary>把一条信号扇出到所有已连接对端。未启动 / 异常一律只记日志，绝不打断宿主。</summary>
    private static void Publish(string path, object payload)
    {
        var node = _node;
        if (node == null) return;

        try { node.Publish(path, payload); }
        catch (Exception ex) { Logger.Warn($"[MSP] 广播 {path} 失败: {ex.Message}"); }
    }
}
