using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using SkiaSharp;
using System.Net.Http;
using System.Text.Json;

namespace NotchPeninsula
{
    public partial class MediaController
    {
        // 暴露给 UI 的静态配置和单例，方便极速调用
        public static MediaController? Instance { get; private set; }
        internal static string TargetPlatform = "other"; // 默认通用媒体
        internal static bool IsMediaControlEnabled = true; // 媒体开关
        // 通用媒体下的会话匹配方式：false = 自动匹配（只接管正在播放的会话），true = 手动指定软件
        internal static bool IsManualSessionMatch = false;
        // 手动模式锁定的目标软件，直接存 SMTC 的 SourceAppUserModelId
        internal static string ManualSessionAppId = "";
        // 系统当前是否存在任何 SMTC 会话（设置界面据此清空「手动选择软件」选项框）
        internal static bool HasActiveSessions { get; private set; }
        // 双击封面（折叠态与展开态都算）是否跳回正在放媒体的那个应用。
        // 关掉后双击完全不消费、不做事，折叠态媒体区的展开入口同时变成左键单击
        // （见 Renderer.MediaExpandByLeftClick / MediaExpandByRightClick）。
        //
        // 默认关闭：这是「跳回媒体软件」这种会抢走前台焦点的动作，
        // 不该在老用户升级后不告而开 —— 注册表里没有 MediaAppLaunchEnabled 这个键时一律按关闭处理
        // （见 Program.LoadSettings）。顺带的好处是：升级用户默认拿到的就是他们熟悉的
        // 「左键单击展开媒体面板」（旧口径），而双击跳转变成显式开启的功能。
        // 注册表里有值的照旧优先，不会被这次改默认值影响。
        internal static bool IsAppLaunchEnabled = false;

        internal static bool IsLyricsEnabled = true;
        /// <summary>
        /// 歌词扫光总闸（唯一开关）：开着时歌词随演唱进度扫光，关掉则画纯实体文字。
        ///
        /// 扫光由同一条链驱动（见 ComputeScanProgress）：逐字优先，
        /// 逐字效果不可用时自动回退到整行均匀扫光。「不可用」有三种情形 —— 本行没对上字级数据、
        /// 整首歌拿不到逐字数据（只有落月的两个源会带 yrc，网易云侧还只有部分歌有）、
        /// 或本开关没开。三者走同一条回退分支，所以「不扫光」只由本开关一个条件决定。
        ///
        /// 关闭时连逐字数据都不解析（见 FetchLyricsAsync 的建表段），
        /// 行为与只有整行时间轴时完全一致。
        /// </summary>
        internal static bool IsLyricScanEnabled = true;

        // 翻译歌词：开启后把当前句的译文作为第二行画在原文下方（仅在有译文时生效）
        internal static bool IsTranslationEnabled = true;

        internal static float LyricDelayOffset = 0f;
        // 四个歌词引擎与网络封面下载共用同一个 UA
        private const string HttpUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

        private static readonly HttpClient _http = new(new HttpClientHandler // 注入无条件放行的证书校验回调，彻底解决 SSL 报错，同时增加超时容错
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        })
        { Timeout = TimeSpan.FromSeconds(4) };

        // 声明一个容量为 1 的异步锁，控制网络请求只能单线进行
        private static readonly System.Threading.SemaphoreSlim _fetchLock = new(1, 1);

        private (TimeSpan Time, string Text, string Translation)[] _lyrics = Array.Empty<(TimeSpan, string, string)>();

        /// <summary>
        /// 逐字时间轴，与 _lyrics 逐行一一对应（同一趟循环里一起建表，索引天然对齐）。
        ///
        /// 只有落月的两个源会带回逐字数据（歌词接口的 data.yrc），其余源头完全没有；
        /// 源头有逐字、但某一行没匹配上时，那个元素也是 null。
        /// 这两种情况那一行都会由 ComputeScanProgress 自动回退到卡拉 OK 的整行扫光。
        /// 整个数组为 null 表示这首歌压根没有逐字数据。
        /// </summary>
        private LyricWordTiming?[]? _lyricWordTimings;

        // 当前时间轴里是否存在译文行（随 _lyrics 一起更新，避免每帧遍历数组）
        private bool _lyricsHasTranslation;
        public string CurrentLyric { get; private set; } = "";
        // 当前歌词行的译文（无译文时为空串）。渲染层据此在原文下方再画一行。
        public string CurrentLyricTranslation { get; private set; } = "";
        // 当前这首歌的时间轴里是否有译文：高度补偿只认它，避免逐句有无译文导致岛体忽高忽低
        public bool HasLyricTranslation { get; private set; }
        public float CurrentLyricProgress { get; private set; } = 0f;
        // 拖动松手后的静默期。播放器执行 seek 要几十~几百毫秒，这段时间它上报的仍是旧位置；
        // 直接按跳变处理会把进度条与歌词弹回原处，所以静默期内只做缓慢纠偏、不做跳变对齐。
        private DateTime _seekSettleUntil = DateTime.MinValue;
        private const double SeekSettleSeconds = 1.5;

        // 上一次采样到的 SMTC 位置，用来判断「SMTC 自己有没有往回走」——
        // 那才说明用户把进度往回拖了。我们领先它，多半只是它报得慢 / 报得粗。
        private TimeSpan _prevSmtcPos;
        private bool _hasPrevSmtcPos;
        // 本地位置领先 SMTC：接下来按慢速推进把偏差吃掉。位置始终单调不减，绝不往回退。
        private bool _timelineAhead;
        // 上一次采样算出的误差（smtcPos − 本地位置），供「领先量是否还在扩大」的安全阀比较用。
        // 初值取负无穷 ⇒ 首次采样永不触发安全阀。
        private double _prevDelta = double.NegativeInfinity;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private string _lastFetchedTitle = "";
        private string _lastFetchedArtist = "";

        // ---- 取词失败的重试记账 ----
        //
        // 为什么必须有：歌词槽位是在发起取词之前就绑定的（见 FetchMediaAsync），
        // 所以同一首歌再进来时 IsLyricOwner 判为已归属、直接返回；而 RefreshPropertiesCore
        // 那边又只在「标题/歌手变了」时才触发取词 —— 取词失败并不会改变标题。
        // 两处叠起来的结果就是：一次失败 = 这首歌永久没有歌词，用户只能手动切歌重来。
        //
        // 重试由渲染循环驱动（与封面兜底同一个时钟，见 UpdateLyrics）：RefreshProperties 只在
        // 元数据 / 播放状态变化时才跑，播放中一次都不会来，靠它自己重试是不可能的。
        // 节流 2 秒、最多 3 次：足够覆盖「SMTC 时长还没跟上」「网络偶发失败」「被反爬拦一次」
        // 这几类瞬时原因，又不会在真的没有歌词源时无限刷请求。
        private const int LyricRetryMax = 3;
        private static readonly TimeSpan LyricRetryInterval = TimeSpan.FromSeconds(2);
        private int _lyricRetryCount;
        private DateTime _lastLyricRetryAt = DateTime.MinValue;

        /// <summary>
        /// 申请一次取词重试额度。返回 false 表示不满足条件（次数用尽 / 距上次太近）。
        /// 额度在申请时就扣掉，避免渲染循环每帧都来问一次。
        /// </summary>
        private bool TryBeginLyricRetry()
        {
            if (_lyricRetryCount >= LyricRetryMax) return false;

            var now = DateTime.UtcNow;
            if (now - _lastLyricRetryAt < LyricRetryInterval) return false;

            _lyricRetryCount++;
            _lastLyricRetryAt = now;
            return true;
        }

        /// <summary>
        /// 当前会话的总时长（秒），给取词链当「同名不同版本」的筛选依据。
        ///
        /// 兜底顺序与 UpdateLyrics 的采样点保持一致：EndTime → MaxSeekTime → 0。
        /// 两家播放器「把总长填在哪一栏」并不统一，少数只填可 seek 上界（EndTime 恒 0）。
        /// 返回 0 表示这一拍没拿到 —— 取词链对 0 是放行的（不校验时长），不会误杀。
        /// </summary>
        private long CurrentTimelineSeconds()
        {
            try
            {
                if (_currentSession?.GetTimelineProperties() is { } t)
                {
                    var end = t.EndTime > TimeSpan.Zero ? t.EndTime
                        : t.MaxSeekTime > TimeSpan.Zero ? t.MaxSeekTime
                        : TimeSpan.Zero;
                    return (long)end.TotalSeconds;
                }
            }
            catch { }
            return 0;
        }

        // 「本曲目的封面已经由外部来源就位」的记账（会话 + 标题）。命中时属性刷新一律不再碰封面。
        //
        // 外部来源有两种：网络搜索到的专辑图（FetchCoverAsync）与会话自带封面（FetchSmtcCoverAsync，
        // 只有 SmtcCoverPreferredIds 里的平台会走）。两者共用这一对字段 —— 它们回答的是同一个问题
        // 「这条曲目的封面换上了没有」，任一到位后另一条就不再发起，省掉一次请求也避免来回换图。
        //
        // 刻意不用拼串做键：该判定每次属性刷新都会走到，拼串就是纯 GC 压力；
        //    而且键里不含歌手 —— 歌手在 seek / 换轨瞬间会短暂缺失，带进来会让键对不上，
        //    于是封面被程序图标顶掉，而记账又还「看起来匹配」，再也换不回外部封面。
        private string _externalCoverAppId = "";
        private string _externalCoverTitle = "";

        // 会话自带封面（SMTC 缩略图）的读取节奏。两个常量解决的是同一类问题：别把上一首的图当成本曲目的。
        //
        // settle：属性变化的通知到达时，会话的缩略图往往还是上一首的 —— 网易云音乐与酷狗实测如此
        //         （QQ 音乐是同批更新，所以没有这个现象）。当场读会把上一首的封面记到新曲目头上，
        //         而记账一旦命中，选封面与网络封面两条路都会让位，之后再也不会纠正 ——
        //         表现就是「从第二首起，每首歌显示的都是上一首的封面」。所以读取要错开这一小段。
        // retry ：同一曲目的兜底重试间隔。兜底重试挂在渲染循环上（60 FPS 调用），必须节流；
        //         换歌是新事件，按曲目判定后不受它限制（见 UpdateCover 末尾）。
        private static readonly TimeSpan SessionCoverSettleDelay = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan SessionCoverRetryInterval = TimeSpan.FromSeconds(2);
        // 最近一次发起读取的曲目与时刻。按曲目记账的原因：节流若只看时间，
        // 连续切歌时后一首会被前一首的计时挡住，于是它连读都不读，封面直接停在上一首。
        private DateTime _lastSessionCoverAttempt = DateTime.MinValue;
        private string _sessionCoverAttemptTitle = "";
        private string _sessionCoverAttemptAppId = "";

        // ---- 「标题还没就绪」的兜底重试 ----
        //
        // 现象：PotPlayer 这类播放器播 SMB / 网络文件时，SMTC 的 Title 会先报**自己的应用名**
        //      （"PotPlayerMini64"），要等文件元数据 / 网络加载完才换成真正的文件名；
        //      而这一次替换**多半不发 MediaPropertiesChanged**（实机现象：只有暂停、切歌这类
        //      播放状态变化时才刷新）。宿主只在事件里读属性，于是标题一直卡在应用名上，
        //      直到用户手动暂停一次才「自己好了」。
        //
        // 与封面、取词一样，重试只能挂在渲染循环这个稳定时钟上：RefreshProperties 播放期间
        // 一次都不会来，指望事件自愈是不可能的。
        //
        // 判据必须保守 —— 只有「标题为空」或「标题就是本会话的应用名」才算未就绪，
        // 正常曲目名一次都不会命中，也就不会为它多打一次 COM 调用。
        // 额度用尽就停：真的拿不到标题的播放器（SMTC 永久为空）不该被无限重试。
        private const int TitleRetryMax = 10;
        private static readonly TimeSpan TitleRetryFirstDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan TitleRetryInterval = TimeSpan.FromSeconds(2);
        // 下面三个字段的读写都留在渲染线程上（UpdateMediaMode 只置 _titleRetryResetPending 这个
        // volatile 标志，真正的清零由渲染线程执行）—— 省掉一次跨线程同步。
        private int _titleRetryCount;
        // MinValue = 本轮还没排期（收到未就绪的第一帧只排期、不读，见 RetryTitleIfNeeded）
        private DateTime _titleRetryNextAt = DateTime.MinValue;
        private volatile bool _titleRetryResetPending;

        // 「应用名」推导要每帧做，Substring 不能放在帧路径上 —— 按 AppID 缓存结果，变了才重算
        // （与排版缓存同一套做法：字段逐项比较，不做字符串拼接）。
        private string _appNameCacheKey = "";
        private string _appNameCacheValue = "";

        private static readonly char[] PathSeparators = ['\\', '/'];

        // 当前 Thumbnail 里放的到底是「哪个程序的应用图标」；为其他来源的封面时置空。
        // 少了它，视频模式下每次属性刷新都会新建一张 SKBitmap 再把旧的那张 Dispose 掉 ——
        // 不是泄漏（旧的确实放掉了），但白白在原生堆上反复分配 / 释放，且每帧都要走一遍取图逻辑。
        private string _appIconKey = "";

        // 最近播放过的歌曲及其进度。进度挂在「歌曲槽位」上而不是全局变量，
        // 所以切平台来回（音乐↔浏览器、音乐↔音乐）时能取回进度，歌词不会从头开始。
        // 定长环形缓冲，只存字符串引用与值类型，零分配。
        //
        // AppId 记录这首歌是在哪个会话里播的，换歌时靠它区分两种情况：
        //   同 App 换歌 —— 上一首已经停了，它的进度必须作废；
        //   跨 App 切换 —— 它还在后台放着，进度要接着算。
        // 少了这个区分，「同 App 换歌后又切回来」会把上一轮的进度续用，歌词就从上一首的进度继续播。
        // TickedAt 记录该槽位的进度最后一次被推算的时刻，用来剔除陈年快照（那首歌早播完了，槽里只是残留值）。
        private const int RecentSongSlots = 4;
        private readonly (string Title, string Artist, TimeSpan Position, DateTime TickedAt, string AppId)[] _recentSongs
            = new (string, string, TimeSpan, DateTime, string)[RecentSongSlots];
        private const double FreshSlotSeconds = 3.0; // 后台会话每秒采样一次，留足调度抖动余量
        private int _recentCursor;
        // 当前歌词与时间轴归属的槽位，-1 表示尚未接管。
        // 跨线程：由 FetchLyricsAsync（线程池线程）写入，UpdateLyrics（渲染线程）每帧读取 ——
        // 用 volatile 保证换槽位后渲染线程立刻看到，否则会短暂读到旧槽位的残留进度。
        private volatile int _lyricSlot = -1;

        // 「退到后台但仍在播放」的会话，按槽位登记。
        // 用数组而不是单个变量：音乐↔音乐来回切时会有两首歌同时需要后台推算，
        // 单个变量会被中途路过的平台顶掉，跨平台停留久了进度就被判成陈旧值而清零。
        // 每秒采样一次播放状态，60FPS 下不产生额外开销。
        private readonly GlobalSystemMediaTransportControlsSession?[] _slotSessions
            = new GlobalSystemMediaTransportControlsSession?[RecentSongSlots];
        private readonly DateTime[] _slotSampleAt = new DateTime[RecentSongSlots];

        // 接管新歌后，等下一帧拿到 SMTC 时间轴就强制对齐一次。
        // 不能靠「位置跳变 > 1.5 秒」来兜底：新歌位置往往也是 0，差值判不出来，旧进度就会残留。
        //
        // 跨线程：写入方是 UpdateSession（线程池线程），读取/消费方是 UpdateLyrics（渲染线程）。
        // 必须 volatile —— 否则渲染线程可能看不到这次置位，换歌后的补采被推迟到下一个 200ms 周期，
        // 新歌的进度条与歌词最多慢 200ms 才开始对齐。
        private volatile bool _forceResync;

        // 当前会话 AppID 的镜像。渲染线程每帧都要做一次「歌词归属校验」，
        // 直接读 SourceAppUserModelId 会打 COM 调用，这里由 UpdateSession 同步写一份供它零成本比对。
        //
        // volatile：写方是线程池线程（UpdateSession 开头），读方是渲染线程（每帧的归属校验）。
        // 它必须比 Title / Artist 更早对渲染线程可见 —— UpdateSession 里先写 _currentAppId、
        // 之后才走 RefreshProperties 更新 Title / Artist，volatile 的写屏障保证了同线程后续的
        // Title / Artist 写入不会重排到它前面，渲染线程见到新 AppId 时看到的一定不是上一首的标题。
        private volatile string _currentAppId = "";

        /// <summary>
        /// 当前接管会话的 AUMID（没有会话时为空串）。供渲染线程零成本比对，
        /// 也供「双击媒体控制 → 跳转对应应用」（OpenCurrentApp）取目标。
        /// </summary>
        public string CurrentAppId => _currentAppId;

        // ---- 歌曲时间轴 ----
        // HasTimeline = 「SMTC 给出了曲目总长」（进度条据此决定画不画），不区分平台。
        // 它不是时间轴是否有值的判据：只给位置不给总长的会话照样走真实 SMTC 位置，
        // 「已播放 mm:ss」显示真实值，只是没有进度条。
        // 位置唯一真源是 _timelinePos：歌词槽位与进度条都写它、读它，所以拖动后两者必然精确同步。
        public bool HasTimeline { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string TimelineElapsed { get; private set; } = "0:00";
        public string TimelineTotal { get; private set; } = "0:00";
        private TimeSpan _timelinePos;

        // 状态锁：拖动期间冻结一切上游写入与自动推进，否则每一帧都会被真实值拉回原处（拖动卡顿的根源）
        private volatile bool _isDragging;
        public bool IsDragging => _isDragging;

        // 文本按「整数秒」为键缓存：拖动是 60FPS 路径，绝不允许每帧 ToString
        private int _shownSec = -1, _shownTotal = -1;

        // SMTC 采样快照：歌词 / 进度条 / 总时长三处共用同一份（由后台采样时钟写入，见 SampleSmtcTick）。
        // 渲染线程只读这份快照，一次 COM 都不打 —— 这也意味着下面这些字段是「后台线程写、渲染线程读」，
        // 所以取值时都以整块快照为单位（同一拍采到的 Position / Duration / LastUpdatedTime 一起换掉）。
        private const double SmtcProbeIntervalSec = 0.2;
        private DateTime _smtcProbeAt = DateTime.MinValue;   // 只由后台采样时钟读写
        private TimeSpan _smtcPos = TimeSpan.Zero;
        private TimeSpan _smtcDuration = TimeSpan.Zero;

        // ---- 真实 SMTC 时间轴（Position + LastUpdatedTime 外推） ----
        //
        // 关键事实（Microsoft 文档原文）：TimelineProperties.Position 是
        // 「current as of LastUpdatedTime」—— 它是一份冻结在过去的快照，不是实时值。
        // LastUpdatedTime（DateTimeOffset，UTC）才是这份快照的采样时刻。
        //
        // 也就是说 SMTC 给的是「(时刻 T, 位置 P)」，实时位置要自己外推：
        //     实时位置 = P + (now - T)     （播放中）
        //     实时位置 = P                 （暂停中，位置不会走）
        //
        // 老实现忽略了 LastUpdatedTime，把 P 当成实时值去纠偏，中间那段空隙靠
        // 「自由跑表 + 纠偏」糊过去 —— 表现就是「进度对不上真实 SMTC」，
        // 而且播放器刷新越稀疏、滞后越明显（网易云这类几秒才更新一次的尤其明显）。
        //
        // 现在改为：只要 LastUpdatedTime 可用，就用外推出来的真实 SMTC 位置
        // （见 TryGetSmtcLivePosition）；只有它不可用（默认值 / 播放器不上报）才回退到
        // 自由跑表的虚拟时间轴。
        private bool _smtcHasLastUpdated;   // 本份快照的 LastUpdatedTime 是否可信
        private DateTime _smtcLastUpdatedUtc; // 上一份快照的 LastUpdatedTime（UTC）

        // ---- 后台 SMTC 采样时钟（全类唯一的 COM 调用点） ----
        //
        // 为什么必须单独开一条后台时钟：GetTimelineProperties / GetPlaybackInfo 这两个调用
        // 在部分播放器上会阻塞几十毫秒到数秒 —— 换歌瞬间的汽水音乐、以及刚被切走、
        // 提供方已经不响应的那个旧会话尤其明显。它们原本跑在渲染线程上（见 UpdateLyrics），
        // 于是整块岛体跟着一起冻住：不解冻就不出帧，解冻后的第一帧位置已经跳到当前进度 ——
        // 表现就是「切歌时整个灵动岛卡死（卡多久不固定）」以及「解冻后歌词直接跳到半句」。
        //
        // 现在采样全部落在后台线程，渲染线程只读这里留下的快照，一次 COM 都不打。
        // 两条节流互不影响：当前会话 200ms 一采（换歌时 _forceResync 置位则立刻补采），
        // 后台会话 1 秒一采。
        private System.Threading.Timer? _smtcSampler;
        private const int SmtcSamplerTickMs = 50;   // 采样节拍：50ms 一跳，真正的采样仍按上面两条节流
        private int _smtcSampleVersion;             // 后台写 / 渲染线程读 → 一律 Volatile / Interlocked
        private int _consumedSampleVersion;         // 渲染线程上次消费到的版本号
        private int _sampling;                      // 采样重入闸（见 SampleSmtcTick）
        private DateTime _suspendedProbeAt = DateTime.MinValue;   // 后台线程独占

        /// <summary>后台会话（退到后台仍在放的那首）本拍是否在播放 —— 后台采样器写、渲染线程读。</summary>
        private readonly bool[] _suspendedPlaying = new bool[RecentSongSlots];

        /// <summary>后台会话那几次 COM 调用是否已经失败（播放器退出）—— 后台置位，渲染线程据此注销登记。</summary>
        private readonly bool[] _suspendedDead = new bool[RecentSongSlots];

        public float TimelineProgress => Duration > TimeSpan.Zero
            ? Math.Clamp((float)(_timelinePos.TotalSeconds / Duration.TotalSeconds), 0f, 1f) : 0f;

        public string Title { get; private set; } = "Notch Peninsula";
        public string Artist { get; private set; } = "Waiting for media...";

        // ---- 跨线程共享的标量一律走 volatile 后备字段 ----
        //
        // 本类的写入方与读取方跑在不同线程上：
        //   · 写入方：UpdateSession / RefreshPropertiesCore —— 由 SMTC 事件与 async 续体驱动，
        //     落在线程池线程上；
        //   · 读取方：UpdateLyrics —— 由宿主渲染循环每 16ms 调用，跑在渲染线程上。
        //
        // 普通字段在这里会出「可见性延迟」：渲染线程可能因 CPU 缓存 / 缺少内存屏障，
        // 在异步线程改写后几十到几百毫秒才看到新值。时间轴上这会直接表现为偏差：
        //   · IsPlaying 读成旧值 → 已经暂停了还在按「播放中」外推（位置虚涨），
        //     或已经播起来了却按「暂停」冻结（进度不动），要等下一次属性刷新才纠正；
        //   · _forceResync 读成旧值 → 换歌后那次「立刻补采 SMTC」被推迟到下一个 200ms 周期，
        //     新歌的进度条/歌词最多慢 200ms 才开始对齐。
        // 这些都正是「时间轴因为线程问题出现延迟」的来源，所以用 volatile 保证写后立刻可见。
        private volatile bool _isPlaying;
        public bool IsPlaying => _isPlaying;

        private volatile bool _isActive;
        public bool IsActive => _isActive;

        public SKBitmap? Thumbnail { get; private set; }

        // 封面位图的唯一写入口。三条互不等待的路径都会换图（属性刷新 / 网络封面下载完成 / 会话清空），
        // 而属性刷新那道 _refreshingProperties 闸门管不到网络封面那条异步链 ——
        // 所以「换引用」必须在这里自己串起来，否则两次并发换图会把同一份位图换乱。
        private readonly object _thumbSwap = new();

        // ---- 封面位图的内存上限 ----
        // 封面最终只画在两处：折叠态 22×22、展开面板 50×50（见 Renderer.MediaWidget）。
        //    此前是「源站返回多大就解码多大」——一张 1000×1000 的图解码后是 4MB 原生位图，
        //    而它一辈子只被画进 50×50 的方格里。160 已给到 3 倍 DPI 余量，再大纯属浪费。
        private const int CoverMaxEdge = 160;

        /// <summary>
        /// 单张封面的下载字节上限。挡的是「源站返回一张几十 MB 的原图」这类输入 ——
        /// 封面缺失顶多退回应用图标，把进程撑起来才是真事故。
        /// 同类上限见 ToastIconProvider.MAX_BYTES（那里是 4MB，因为岛上只画 28px）。
        /// </summary>
        private const long CoverMaxBytes = 8L * 1024 * 1024;

        /// <summary>解码后用来的缩放画笔（只在换歌时用到，静态复用）。</summary>
        private static readonly SKPaint _coverScalePaint = new() { FilterQuality = SKFilterQuality.High };

        /// <summary>
        /// 换下的封面位图先在这里排队，不当场 Dispose。
        ///
        /// 不能当场释放：渲染线程每帧 canvas.DrawBitmap(media.Thumbnail, …) 直接读这个属性、
        ///    不持 _thumbSwap，一 Dispose 就是原生 use-after-free（0xC0000005，托管层拦不住）。
        ///
        /// 也不能全交给 GC：SKBitmap 的终结器要等一整轮 GC 才跑，而快速切歌时封面几百毫秒就换一张，
        ///    没缩放的源图更是几 MB 起步，原生内存会在「已无人使用但尚未回收」的状态里堆积。
        ///
        /// 折中：队列超过 RetiredThumbKeep 张时才回收最老的那张。渲染线程每帧只取一次引用、
        ///    当帧用完，排到第 9 张时第 1 张早已不在任何一帧的栈上 —— 既收回了内存，又完全绕开竞态。
        /// </summary>
        private const int RetiredThumbKeep = 8;
        private readonly Queue<SKBitmap> _retiredThumbs = new();

        /// <summary>把一张换下的封面位图挂进待回收队列（只在 _thumbSwap 锁内调用）。</summary>
        private void RetireThumbnail(SKBitmap? bmp)
        {
            if (bmp == null) return;
            _retiredThumbs.Enqueue(bmp);
            while (_retiredThumbs.Count > RetiredThumbKeep)
                _retiredThumbs.Dequeue().Dispose();
        }

        /// <summary>
        /// 换上新的封面位图。
        ///
        /// 换下的那一张不当场 Dispose，而是交给 RetireThumbnail 排队延迟回收 ——
        /// 渲染线程每帧 canvas.DrawBitmap(media.Thumbnail, …) 直接读这个属性且不持本锁，
        /// 当场释放就会释放正在绘制的原生位图。SkiaSharp 的 use-after-free 表现是
        /// 原生访问违例 0xC0000005 直接杀进程，托管层 try/catch 拦不住。回收的时机与理由见
        /// RetiredThumbKeep。
        /// </summary>
        private void SetThumbnail(SKBitmap? next)
        {
            lock (_thumbSwap)
            {
                if (ReferenceEquals(Thumbnail, next)) return;
                RetireThumbnail(Thumbnail);
                Thumbnail = next;
                // 换图即作废「当前放的是哪个程序的图标」这条记账：调用方（SetAppIcon）换完之后自己补上，
                // 其余所有来源（网络封面 / 清空）都天然落到「不是程序图标」，不需要每处都记得清。
                _appIconKey = "";
            }
        }

        /// <summary>
        /// 换上「当前会话那个程序」的应用图标。
        /// 同一个程序的图标已经在位时直接返回 —— 视频模式下每次属性刷新都会走到这里，
        /// 没有这道闸就会反复新建 / 释放原生位图（AppIconProvider 只发副本，那个副本由我们持有）。
        ///
        /// 图标拿不到时（异步解析未完成 / 这个客户端根本解析不出图标，例如 AUMID 是纯中文的汽水音乐）
        /// **什么都不做，绝不用 null 覆盖当前封面** —— 覆盖掉就会露出那张蓝色兜底图。
        /// 宁可继续显示上一张封面，等解析成功（或网络封面）再换。
        /// </summary>
        private void SetAppIcon()
        {
            // 同一个程序的图标已经在位：什么都不做。为 null（解析失败）时 _appIconKey 是空串，
            // 下一次仍会重试 —— 但 AppIconProvider 那边有失败冷却，重试本身几乎不花钱。
            if (_appIconKey.Length > 0 && string.Equals(_appIconKey, _currentAppId, StringComparison.Ordinal)) return;

            var icon = AppIconProvider.Get(_currentAppId);
            if (icon == null) return;      // 没图标：保持当前那张，别清空

            SetThumbnail(icon);            // 内部会把 _appIconKey 清空，所以这一步必须排在下面那行之前
            _appIconKey = _currentAppId;
        }

        /// <summary>
        /// 视频模式：这个会话只能展示「程序图标 + 名称」，没有可用的歌曲信息。
        ///
        /// 判据对所有软件一致，只有一条 —— SMTC 没同时给出歌名与歌手。
        /// 反过来只要有歌名 + 歌手就是音乐模式：取歌词、取网络封面（见 UpdateMediaMode）。
        ///
        /// B站 / 浏览器天然落在视频模式：B站的 Artist 在刷新时就被清空（网页不提供歌手），
        /// 浏览器则要靠网页标题里的「正在播放: 歌名 - 歌手」才能解析出歌手 —— 解析得出来就是音乐模式
        /// （网易云 / QQ音乐网页版这类），解析不出来就是视频模式。
        /// </summary>
        private bool IsVideoMode => !_isMusicMode;

        /// <summary>
        /// 当前会话是否不具备歌词能力 —— 就是视频模式，没有例外。
        ///
        /// 「用户手动锁定某个软件」过去是一条豁免（手动选中的会话即使被判成视频类，也照样去搜歌词）。
        /// 现在取消了：判据收成一条 —— SMTC 没给出歌手就一律按视频模式处理：
        /// 不搜歌词、不取网络封面，只显示标题 + 该程序自己的应用图标。
        /// </summary>
        private bool IsNonLyricSession => IsVideoMode;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // 已挂上播放状态监听的会话（按 AppID 记账）。
        // 自动匹配的接管结果依赖播放状态，而会话表变更事件不会因播放/暂停触发 ——
        // 所以给系统里每个会话都挂一份监听，任何一个开始播放都能立刻重新挑选接管目标。
        private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watchedSessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _watcherLock = new();
        private bool _isBilibiliSession;  // 当前会话是否为 bilibili，用于清空 Artist（视频模式只显示标题）
        // 音乐模式：SMTC 同时给出歌名与歌手即成立（判据对所有软件一致）。
        // 成立 → 取歌词 + 网络封面；不成立 → 视频模式（程序图标 + 标题）。
        private bool _isMusicMode;

        // ---- 当前曲目的「稳定」标题 / 歌手 / 所属会话 ----
        // 这三个字段是「歌词不闪、封面不丢」的关键，动之前先读完：
        //    SMTC 在 seek / 换轨 / 刷新瞬间会间歇性给出空标题或空歌手。如果直接拿原始采样去判模式，
        //    同一首歌里模式会来回翻转，而渲染线程的「非歌词会话」闸门（IsNonLyricSession）会在翻成
        //    视频模式的那一帧把已经显示出来的歌词清掉、封面也会被程序图标顶掉 ——
        //    用户看到的就是「歌词一卡一卡」「翻译没了」「怎么变成 logo 了」。
        //    所以：标题只在拿到非空值时才更新（空标题视为这一拍没上报，沿用上一个），
        //    歌手只在同曲目内拿到非空值时才更新。
        private string _trackAppId = "";
        private string _trackTitle = "";
        private string _trackArtist = "";
        // 同一曲目内连续「没同时拿到歌名+歌手」的采样次数（见 UpdateMediaMode 的宽限）。
        private int _musicModeMisses;
        private const int MusicModeMissGrace = 3;
        private bool _isBrowserSession;   // 当前会话是否为浏览器 (Chrome/Edge)，启用视频标题清理
        private bool _isJustSoloSession;  // 当前会话是否为 Just Solo，启用 LyricServer 直连歌词
        private readonly JustSoloLyricClient _justSoloLyric = new();

        public MediaController()
        {
            Instance = this;
            StartSmtcSampler();
            _ = InitializeAsync();
        }

        /// <summary>启动后台采样时钟。回调是实例方法，退出时必须 Dispose（见 StopSmtcSampler）。</summary>
        private void StartSmtcSampler()
        {
            _smtcSampler ??= new System.Threading.Timer(_ => SampleSmtcTick(), null,
                SmtcSamplerTickMs, SmtcSamplerTickMs);
        }

        /// <summary>
        /// 停掉后台采样时钟（退出路径）。
        /// Timer.Dispose 只是「不再调度」：已经在跑的那一拍会跑完，而它读的都是本对象自己的字段，
        /// 对象还被 Timer 的注册钉着，所以不存在「回调撞上已释放资源」的问题。
        /// </summary>
        private void StopSmtcSampler()
        {
            try { _smtcSampler?.Dispose(); } catch { }
            _smtcSampler = null;
        }

        /// <summary>
        /// 后台采样一拍（跑在线程池线程上，绝不在渲染线程里执行）。
        ///
        /// 当前会话按 200ms 节流；_forceResync（换歌 / 换会话）置位时立刻补采，好让切歌后的
        /// 首次硬对齐不被推迟到下一个周期。后台会话的播放状态按 1 秒节流。
        ///
        /// 整个方法自己兜异常：Timer 回调里漏出去的异常会直接把进程带走。
        /// </summary>
        private void SampleSmtcTick()
        {
            // 重入闸：Timer 不会等上一拍跑完，而某一拍真卡住（不规范的播放器能在 COM 上挂几秒）时
            // 回调会在队列里堆积，解冻后连着打十几次同样的调用。丢弃重叠的那几拍是安全的 ——
            // 下一次采样本来就会把最新状态取回来。
            if (System.Threading.Interlocked.Exchange(ref _sampling, 1) == 1) return;
            try
            {
                var now = DateTime.UtcNow;

                if (_forceResync || (now - _smtcProbeAt).TotalSeconds >= SmtcProbeIntervalSec)
                {
                    _smtcProbeAt = now;
                    SampleCurrentSession();
                    System.Threading.Volatile.Write(ref _smtcSampleVersion, _smtcSampleVersion + 1);
                }

                if ((now - _suspendedProbeAt).TotalSeconds >= 1)
                {
                    _suspendedProbeAt = now;
                    SampleSuspendedSessions();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"SMTC 后台采样异常: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _sampling, 0);
            }
        }

        /// <summary>
        /// 采样当前会话的时间轴与播放状态 —— 全类唯一的 COM 调用点，只允许从这里进来。
        /// 两段各自兜异常：不规范的媒体源会在会话消失的瞬间抛 COM 断开异常，
        /// 那时只是「这一拍没读到」，绝不能把标题 / 模式判定一起打掉。
        /// </summary>
        private void SampleCurrentSession()
        {
            var session = _currentSession;
            if (session == null) return;

            try
            {
                var t = session.GetTimelineProperties();
                _smtcPos = t.Position;

                // 总长取两级兜底，因为各家播放器「把总长填在哪一栏」并不统一：
                //   1. EndTime     —— 规范字段，大多数播放器（QQ 音乐 / Spotify / 浏览器）填这里
                //   2. MaxSeekTime —— 少数播放器只填可 seek 上界，EndTime 恒为 0（有总长但不写规范栏）
                //   3. 都没有 → 0    —— 进度条画不出来，但「已播放 mm:ss」照样能走真实 SMTC 值
                // 判据是 > 0 而不是 >= 0：TimeSpan.Zero 与「没上报」在 API 上无法区分，一律当没给。
                // MaxSeekTime 只是「能拖到哪」的上界，理论上可能略大于实际总长 ——
                // 宁可比例略不准（进度条短一点点），也不要总时长整个缺失（进度条直接消失）。
                _smtcDuration = t.EndTime > TimeSpan.Zero ? t.EndTime
                    : t.MaxSeekTime > TimeSpan.Zero ? t.MaxSeekTime
                    : TimeSpan.Zero;

                // LastUpdatedTime 一并采下来（见字段声明处：Position 是「截至 LastUpdatedTime」的快照，
                // 实时位置要自己用 now - LastUpdatedTime 外推）。
                // 判据：DateTimeOffset 的默认值（MinValue / 0001-01-01）说明播放器根本不上报这个字段，
                // 那种情况下外推没有基准，必须退回虚拟跑表。
                var lu = t.LastUpdatedTime;
                _smtcHasLastUpdated = lu > DateTimeOffset.UnixEpoch;
                _smtcLastUpdatedUtc = _smtcHasLastUpdated ? lu.UtcDateTime : DateTime.MinValue;
            }
            catch
            {
                _smtcPos = TimeSpan.Zero;
                _smtcDuration = TimeSpan.Zero;
                _smtcHasLastUpdated = false;
                _smtcLastUpdatedUtc = DateTime.MinValue;
                // 故意不动 _isPlaying：这次采样失败只说明「没读到」，不代表「暂停了」。
                // 把它按暂停处理会让进度条凭空冻住一帧；保留旧值更接近真实状态。
            }
            // 没有端到端时长（网易云 / 酷狗等）：强制对齐标记留着也没用，就地消费掉，
            // 让采样稳定回到 200ms 节流 —— 否则它会每拍都触发一次补采，等于没节流。
            // （判据读的是刚采下来的 _smtcDuration，所以必须放在上面那段之后。）
            if (_smtcDuration <= TimeSpan.Zero) _forceResync = false;

            // 顺带校准播放状态。为什么必须在采样点做、不能只靠 PlaybackInfoChanged 事件：
            //   时间轴外推要乘上「现在是不是在播放」（暂停时外推量必须为 0），所以 _isPlaying
            //   一旦过期，外推方向就错 —— 暂停了还按播放涨，或播着却冻住。
            //   而事件并不可靠：部分播放器暂停/续播根本不发 PlaybackInfoChanged；
            //   通用媒体模式下这条事件还会先绕一圈 UpdateSession（内部有 await + COM），
            //   等它落到 _isPlaying 已是几百毫秒之后。
            //   这里每 200ms 用一次轻量 GetPlaybackInfo 就地校准，迟到问题从根上消失。
            try
            {
                var info = session.GetPlaybackInfo();
                if (info != null)
                    _isPlaying = info.PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { }
        }

        /// <summary>
        /// 采样后台会话（退到后台仍在放的那首）的播放状态。
        ///
        /// 这一步原来也在渲染线程上（AdvanceSuspendedTimeline 里直接 GetPlaybackInfo）——
        /// 而被切走的那个会话恰恰是最容易阻塞的：切歌瞬间旧会话正在退出，
        /// 它的提供方可能已经不响应了，一次调用就能把岛体冻住几百毫秒。
        /// 现在只在这里取状态，渲染线程读 _suspendedPlaying / _suspendedDead 两个缓存。
        /// </summary>
        private void SampleSuspendedSessions()
        {
            for (int i = 0; i < RecentSongSlots; i++)
            {
                var session = _slotSessions[i];
                if (session == null)
                {
                    _suspendedPlaying[i] = false;
                    _suspendedDead[i] = false;
                    continue;
                }

                try
                {
                    _suspendedPlaying[i] = session.GetPlaybackInfo()?.PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    _suspendedDead[i] = false;
                }
                catch
                {
                    _suspendedPlaying[i] = false;
                    _suspendedDead[i] = true;   // 播放器已退出，交给渲染线程注销登记
                }
            }
        }

        private async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager != null)
            {
                _manager.SessionsChanged += async (s, e) => await UpdateSession(_manager);
                await UpdateSession(_manager);
            }
        }

        // 供 UI 更改配置后主动拉取刷新
        public async Task ForceRefresh()
        {
            if (_manager != null) await UpdateSession(_manager);
        }

        /// <summary>
        /// 退出前释放媒体侧持有的后台资源：
        ///   · Just Solo LyricServer 的 WebSocket 连接与重连循环（JustSoloLyricClient.Stop）；
        ///   · 当前封面位图（原生 Skia 位图，Dispose 前先切断属性引用）。
        ///
        /// 刻意不处理的两样：SMTC 会话归系统管；_http 是进程级静态复用的 HttpClient，
        /// 单例生命周期内复用是正确的，提前 Dispose 反而会导致退出前的请求抛异常。
        /// </summary>
        public void Shutdown()
        {
            _shuttingDownMedia = true;
            StopSmtcSampler();
            try { _justSoloLyric.Stop(); } catch { }
            try { SetThumbnail(null); } catch { }
        }

        /// <summary>
        /// 当前所有可接管的 SMTC 会话 AppID（去重、保持系统顺序，全局屏蔽的软件不列出），
        /// 供设置界面「手动选择软件」下拉直接展示原始 AppID。只在用户展开下拉时调用一次，不做任何后台轮询。
        /// </summary>
        public string[] GetAvailableAppIds()
        {
            if (_manager == null) return Array.Empty<string>();
            try
            {
                var sessions = _manager.GetSessions();
                var ids = new List<string>(sessions.Count);
                for (int i = 0; i < sessions.Count; i++)
                {
                    string id = sessions[i].SourceAppUserModelId ?? "";
                    if (id.Length > 0 && !IsGloballyBlockedApp(id) && !ids.Contains(id)) ids.Add(id);
                }
                return ids.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // 会话当前是否处于播放中：严格等于 SMTC 的 Playing，暂停 / 停止都算「未播放」。
        // 手动模式下不关心，自动匹配时用来把未播放的会话排除在接管之外。
        private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return session.GetPlaybackInfo()?.PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        // 是否处于「用户手动锁定某个软件」的接管模式：通用媒体 + 手动匹配 + 已选定 AppID。
        // 会话挑选与「越过后台自动判定」两处共用它，避免规则漂移。
        private static bool IsManualLockActive =>
            IsMediaControlEnabled && TargetPlatform == "other" && IsManualSessionMatch && ManualSessionAppId.Length > 0;

        // 接管目标重挑的「合并闸」（见 UpdateSession 的注释）：
        //   0 → 闲；1 → 正在跑。期间到达的事件只把 _sessionUpdatePending 置位，不并发第二份。
        private int _updatingSession;
        private volatile bool _sessionUpdatePending;

        /// <summary>退出路径置位：让合并闸里的「再补一遍」循环不再空转。</summary>
        private volatile bool _shuttingDownMedia;

        /// <summary>
        /// 重挑接管目标 + 刷新属性。
        ///
        /// ⚠️ 必须走这道「合并闸」，不能让事件直接调 UpdateSessionCore：
        /// 系统里每一个会话的 PlaybackInfoChanged 都挂在 OnPlaybackInfoChanged 上（见 SyncPlaybackWatchers），
        /// 而切歌 / 弹会员窗这类时刻，汽水音乐会连着甩出一串播放状态与元数据事件 —— 每个事件都来一遍
        /// 全量刷新，而每次刷新内部要**逐个会话**打一次阻塞的 GetPlaybackInfo（选「谁在放」用）。
        /// 结果是：N 个事件 × M 个会话 的阻塞 COM 调用在**同一时刻**铺开，把线程池占满；
        /// 渲染节拍那时还在线程池上（见 NotchWindow._renderThread 的注释），于是岛体整块冻住 ——
        /// 这正是「别的软件弹个窗，我的灵动岛就卡死」的机制。
        ///
        /// 现在同时只允许一个在跑；期间到达的事件只记一个「还要再刷一次」，跑完再补一遍就够
        /// （大家要的都是最终状态，中间的每一拍都没有独立价值）。
        /// </summary>
        private async Task UpdateSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            if (Interlocked.Exchange(ref _updatingSession, 1) == 1)
            {
                _sessionUpdatePending = true;
                return;
            }

            try
            {
                do
                {
                    _sessionUpdatePending = false;
                    await UpdateSessionCore(manager);
                }
                while (_sessionUpdatePending && !_shuttingDownMedia);
            }
            finally
            {
                Interlocked.Exchange(ref _updatingSession, 0);
            }
        }

        /// <summary>UpdateSession 的执行体（原样搬来，只改了名字）。</summary>
        private async Task UpdateSessionCore(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? newSession = null;

            // 单遍扫描会话表：统计「是否存在可接管的 SMTC 会话」（全局屏蔽的软件不算），
            // 设置界面据此决定「手动选择软件」选项框是否要清空；列表同时也供下面挑选复用。
            var sessions = manager.GetSessions();
            bool hasActiveSessions = false;
            for (int i = 0; i < sessions.Count; i++)
            {
                string id = sessions[i].SourceAppUserModelId ?? "";
                if (id.Length == 0 || IsGloballyBlockedApp(id)) continue;
                hasActiveSessions = true;
            }
            HasActiveSessions = hasActiveSessions;

            // 让每个会话的播放状态变化都能触发重新挑选（接管目标依赖「谁在放」）
            SyncPlaybackWatchers(sessions);

            // 如果总开关打开，执行精确的平台过滤
            if (IsMediaControlEnabled)
            {
                // 通用媒体 + 手动模式：直接锁定指定 AppID 的会话，不受平台规则与播放状态影响
                // （全局屏蔽的软件除外，手动也不允许锁定它）
                if (IsManualLockActive)
                {
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        string id = sessions[i].SourceAppUserModelId ?? "";
                        if (IsGloballyBlockedApp(id)) continue;
                        if (string.Equals(id, ManualSessionAppId, StringComparison.OrdinalIgnoreCase))
                        {
                            newSession = sessions[i];
                            break;
                        }
                    }
                }
                else
                {
                    // 单遍扫描直接选出目标会话：索引遍历避免枚举器分配，
                    // 全程 OrdinalIgnoreCase 比较，不再 ToLower 出临时字符串。
                    // 通用媒体的自动匹配（下文「播放/未播放」均指 SMTC 的 Playing / Paused）：
                    //   1) 正在播放的 Just Solo 直接锁定，压过同时播放的其它会话；
                    //   2) Just Solo 未播放（暂停）时，谁在播放就显示谁；
                    //   3) 全都没在播放时兜底显示 Just Solo（系统里没有 justsolo 会话才退而求其次）。
                    bool skipPaused = TargetPlatform == "other";
                    GlobalSystemMediaTransportControlsSession? pausedFallback = null;
                    int pausedFallbackRank = 0;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        string id = s.SourceAppUserModelId ?? "";
                        if (id.Length == 0) continue;

                        int rank = SessionRank(id);
                        if (rank == 0) continue;                  // 不看好的会话，零成本跳过
                        if (skipPaused && !IsSessionPlaying(s))
                        {
                            // 未播放（暂停）的会话不参与接管，只作为「全都没在播放」时的兜底。
                            // 兜底按 rank 取最高，justsolo 的 rank 最高，所以全暂停时会回到 justsolo。
                            if (rank > pausedFallbackRank) { pausedFallbackRank = rank; pausedFallback = s; }
                            continue;
                        }

                        if (rank == 3) { newSession = s; break; }  // 正在播放的 Just Solo 压过一切，立即锁定
                        if (newSession == null) newSession = s;    // 备选，继续往后扫，遇到 Just Solo 会被顶掉
                    }
                    if (newSession == null) newSession = pausedFallback;
                }
            }

            // 命中 bilibili / 浏览器 会话时打标记，供刷新时应用文本显示策略
            bool wasNonLyric = IsNonLyricSession;
            _currentAppId = newSession?.SourceAppUserModelId ?? "";

            _isBilibiliSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Bilibili");
            // 浏览器标记只驱动网页标题清理（CleanBrowserTitle）。
            // 会不会出歌词跟它无关 —— 判据只有「SMTC 有没有给出歌手」这一条（见 IsVideoMode）。
            _isBrowserSession = MediaLogoProvider.IsBrowser(newSession?.SourceAppUserModelId);
            _isJustSoloSession = newSession?.SourceAppUserModelId?.Contains("justsolo", StringComparison.OrdinalIgnoreCase) == true;

            // 跳转应用的定位采样：会话刚被接管时，前台窗口极可能就是它的主窗口 —— 顺手把句柄记下来。
            //    放在这里（会话挑选之后、属性刷新之前）是因为每次接管 / 刷新都会路过；
            //    「要不要真的重采」由 MediaAppLauncher 自己按「接管目标是否变了」裁决
            //    （只在换应用 / 换平台那一刻采一次，避免用户正在别的程序里时把无关窗口记成媒体窗口）。
            if (IsAppLaunchEnabled) MediaAppLauncher.CaptureSession(newSession);

            // Just Solo 专属歌词通道：只有「当前接管的会话就是 justsolo」时才连接 LyricServer
            UpdateJustSoloConnection();

            // 如果目标会话没变，只需刷新属性，避免重复订阅事件浪费内存
            if (_currentSession != null && newSession != null && _currentSession.SourceAppUserModelId == newSession.SourceAppUserModelId)
            {
                await RefreshProperties();
                _isActive = true;
                return;
            }

            // 会话换走：把正在追踪的那首歌登记成「后台推算」，切回来时进度就是连续的。
            // 只在会话级切换时登记 —— 同会话换歌说明这首歌已经停了（清理见 FetchLyricsAsync），
            // 登记它反而会让它的进度被一直推进，下一首就背上残留进度。
            if (!wasNonLyric && _currentSession != null && _lyricSlot >= 0 && _slotSessions[_lyricSlot] == null)
            {
                _slotSessions[_lyricSlot] = _currentSession;
                _slotSampleAt[_lyricSlot] = DateTime.UtcNow; // 以切换时刻为起点，避免首次采样吃进一段巨大的时间差
            }

            // 切换到了新的会话（或者置空）
            if (_currentSession != null)
            {
                // 切换前，必须先解绑旧会话的事件，防止幽灵对象吃内存
                // （播放状态监听由 SyncPlaybackWatchers 统一挂载到所有会话，这里只管媒体属性）
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }

            _currentSession = newSession;

            if (_currentSession != null)
            {
                // 绑定新会话事件（播放状态监听见 SyncPlaybackWatchers）
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;

                await RefreshProperties();
                _isActive = true;
            }
            else
            {
                // 只有会话真的没了（系统里再没有可接管的会话）才清这本会话自己的标记与封面位图。
                // 刻意不在「切到别的会话」时清：切走只是不再展示，歌词与封面缓存都留着，
                //    切回来能立刻复用 —— 宁可多占一点内存，也不要在切换途中把已经拿到的东西丢掉。
                _isActive = false;
                Title = "No Media";
                Artist = "";
                _isPlaying = false;
                _externalCoverAppId = "";
                _externalCoverTitle = "";
                SetThumbnail(null);
            }
        }

        // 把播放状态监听同步到系统里现存的每一个会话：新增的挂上，消失的解绑。
        // 全程同步执行（无 await），只用一个对象锁挡住并发刷新导致的重复订阅。
        private void SyncPlaybackWatchers(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
        {
            lock (_watcherLock)
            {
                // 已经消失的会话：解绑，否则系统会把事件发给幽灵对象
                if (_watchedSessions.Count > 0)
                {
                    List<string>? stale = null;
                    foreach (var kv in _watchedSessions)
                    {
                        bool alive = false;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            if (string.Equals(sessions[i].SourceAppUserModelId, kv.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                alive = true;
                                break;
                            }
                        }
                        if (!alive) (stale ??= new List<string>()).Add(kv.Key);
                    }

                    if (stale != null)
                    {
                        foreach (string key in stale)
                        {
                            _watchedSessions[key].PlaybackInfoChanged -= OnPlaybackInfoChanged;
                            _watchedSessions.Remove(key);
                        }
                    }
                }

                // 新出现的会话：挂上监听。
                // 判据不能只看 AppID —— 同一个 App 的会话可能被系统换成新的 COM 对象
                // （AppID 不变、引用不同）。只看 AppID 会误判「已订阅」，既不摘旧也不挂新，
                // 结果是该 App 的 PlaybackInfoChanged 永久丢失（表现为切歌状态不刷新）。
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    string id = session.SourceAppUserModelId ?? "";
                    if (id.Length == 0) continue;

                    if (_watchedSessions.TryGetValue(id, out var watched))
                    {
                        if (ReferenceEquals(watched, session)) continue; // 就是同一个对象，已订阅
                        // 对象被替换：先摘掉旧对象的订阅（对已消失的会话是安全空操作）
                        try { watched.PlaybackInfoChanged -= OnPlaybackInfoChanged; } catch { }
                    }

                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _watchedSessions[id] = session;
                }
            }
        }

        // 会话优先级：3 = 立即锁定，2 = 备选（仅通用模式），0 = 忽略。
        // 抽成纯函数既让扫描循环极简，也让优先级规则能脱离 WinRT 做无头验证。
        private static int SessionRank(string id)
        {
            // 全局屏蔽名单（抖音、微信视频号）：任何平台模式、任何匹配方式下都不接管它（justsolo 例外，见 IsGloballyBlockedApp）
            if (IsGloballyBlockedApp(id)) return 0;

            // 浏览器媒体：只认浏览器 SMTC 会话，其余进程一律不接管
            if (TargetPlatform == "browser")
                return MediaLogoProvider.IsBrowser(id) ? 3 : 0;

            // 通用模式：Just Solo 最高优先 —— 它在播放时直接压过同时播放的其它会话，其余会话只作备选
            if (TargetPlatform == "other")
                return id.Contains("justsolo", StringComparison.OrdinalIgnoreCase) ? 3 : 2;

            return MatchesTargetPlatform(id) ? 3 : 0;
        }

        // 全局屏蔽的软件：所有模式（含手动指定）下都不接管，也不出现在手动选择列表里。
        // 抖音、微信视频号的 SMTC 会话都会长期挂着干扰接管，所以直接拉黑而不是靠优先级规避。
        // justsolo 单独放行：优先级判断上它排在抖音屏蔽之前，不能被连带屏蔽掉。
        private static bool IsGloballyBlockedApp(string id) =>
            (id.Contains("douyin", StringComparison.OrdinalIgnoreCase)
             || id.Contains("wechatappex", StringComparison.OrdinalIgnoreCase))
            && !id.Contains("justsolo", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 这个 AUMID 是不是「网易云音乐」。
        ///
        /// 单一数据源：既供 MatchesTargetPlatform 判目标平台，也供取词链判
        /// 「要不要走网易优先那两档」（见 FetchLyricsAsync）—— 两处必须同源，
        /// 否则会出现「接管的是网易云、取词却按 QQ 优先」这种半吊子状态。
        /// </summary>
        private static bool IsNeteaseAppId(string? id)
            => id != null
               && (id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("netease", StringComparison.OrdinalIgnoreCase));

        // 目标平台与会话 AppID 的匹配规则（单一数据源）。
        // browser 模式在上游已单独分流，这里只管具体应用；未列出的平台走 ID 直配。
        private static bool MatchesTargetPlatform(string id) => TargetPlatform switch
        {
            "netease" => IsNeteaseAppId(id),
            "qqmusic" => id.Contains("qqmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("tencent", StringComparison.OrdinalIgnoreCase),
            "applemusic" => id.Contains("apple", StringComparison.OrdinalIgnoreCase) && id.Contains("music", StringComparison.OrdinalIgnoreCase),
            "lxmusic" => id.Contains("cn.toside.music.desktop", StringComparison.OrdinalIgnoreCase) || id.Contains("lxmusic", StringComparison.OrdinalIgnoreCase),
            _ => id.Contains(TargetPlatform, StringComparison.OrdinalIgnoreCase),
        };

        // 「会话自带的封面优先」的播放器（AUMID 关键字，包含匹配，中英文都收 ——
        // 部分国产客户端用中文 AUMID）。
        //
        // ⚠️ 现在已不再参与判定：本地 SMTC 缩略图对所有会话一律优先（见 UpdateCover 的 preferSessionCover）。
        // 名单连同 IsSmtcCoverPreferredAppId 一起留着，是为了万一要收回去「只对名单里的播放器优先」时
        // 能直接拿回来 —— 现在没有任何调用点。
        private static readonly string[] SmtcCoverPreferredIds =
        [
            "cloudmusic", "netease",   // 网易云音乐
            "kugou", "酷狗",            // 酷狗音乐
            "qqmusic", "tencent",      // QQ 音乐
            "applemusic",              // Apple Music（UWP 的 AUMID 形如 AppleInc.AppleMusicWin_…）
            "spotify",                 // Spotify
            "qishui", "汽水",           // 汽水音乐
            "migu", "咪咕",             // 咪咕音乐
            "justsolo",                // Just Solo：本地播放器，封面就是它自己通过 SMTC 给出的那张原图
        ];

        /// <summary>
        /// 这个会话是不是「优先用自带封面」的播放器（见 SmtcCoverPreferredIds）。
        /// 只在选定封面时用到，判定失败（未知客户端）走原有封面链即可，不影响播放与取词。
        /// </summary>
        private static bool IsSmtcCoverPreferredAppId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            foreach (var key in SmtcCoverPreferredIds)
                if (id.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        // 依据当前接管的会话，维护 Just Solo LyricServer 的连接：
        //   只有「当前显示的就是 justsolo」才连；切到别的会话、justsolo 会话消失、关掉媒体控制或换平台都断开。
        private void UpdateJustSoloConnection()
        {
            bool shouldConnect = IsMediaControlEnabled && TargetPlatform == "other" && _isJustSoloSession;

            if (shouldConnect)
            {
                if (!_justSoloLyric.IsRunning) _justSoloLyric.Start();
            }
            else if (_justSoloLyric.IsRunning)
            {
                _justSoloLyric.Stop();
            }
        }

        /// <summary>
        /// RefreshProperties 是否正在执行。
        ///
        /// 为什么要这道闸：本方法有三个互不等待的 async 入口，内部又有两处 await（取属性、开封面流）。
        /// 真并发时两边会读到同一个 Thumbnail 引用，各自 Dispose 再各自赋值 —— 同一份位图被释放两次，
        /// 或被换掉之后仍被渲染线程读。SKBitmap.Dispose() 二次调用本身安全，但 Dispose 之后再访问
        /// 就是 0xC0000005 原生访问违例（渲染线程每帧都在读 media.Thumbnail），是一条真实的进程级崩溃路径。
        ///
        /// 丢掉这次刷新是安全的：下一次属性变化 / 轮询会重新拉一遍。
        /// </summary>
        private int _refreshingProperties;

        private async Task RefreshProperties()
        {
            if (_currentSession == null) return;

            // 已有一次刷新在跑：直接放弃本次（排队只会在换歌瞬间堆起一串过期刷新）
            if (Interlocked.Exchange(ref _refreshingProperties, 1) == 1) return;
            try
            {
                await RefreshPropertiesCore();
            }
            finally
            {
                // 闸门必须在 finally 里放开：中间任一处异常都不能让刷新永久锁死
                Interlocked.Exchange(ref _refreshingProperties, 0);
            }
        }

        /// <summary>RefreshProperties 的主体（拿属性 / 换封面 / 同步播放状态与时长 / 触发取词）。</summary>
        private async Task RefreshPropertiesCore()
        {
            try
            {
                var props = await _currentSession!.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    // 标题 / 歌手只取 SMTC 的原始值，不再按平台特判 ——
                    // 「视频模式只显示标题、不显示歌手」统一由 UpdateMediaMode 收口。
                    // 唯一的预处理是浏览器：网页标题里的「正在播放: 歌名 - 歌手」要拆成歌名 + 歌手。
                    string smtcTitle = props.Title ?? "";
                    string smtcArtist = props.Artist ?? "";
                    if (_isBrowserSession)
                    {
                        smtcTitle = CleanBrowserTitle(smtcTitle, out smtcArtist);
                    }
                    else if (_isBilibiliSession)
                    {
                        smtcArtist = ""; // 网页不提供歌手，别让标题尾部被当成歌手
                    }

                    UpdateMediaMode(smtcTitle, smtcArtist);
                    // hasSessionCover：本会话到底带没带封面。只有自带封面优先的平台会据此改走 SMTC 缩略图。
                    UpdateCover(props.Thumbnail != null);
                }
            }
            catch (Exception ex)
            {
                // 属性读失败：只记日志，一个显示状态都不动。
                // 不规范的媒体源会间歇性抛异常（COM 断开、会话正好在消失…）。这里若把标题改成 Unknown、
                // 歌手清空、模式判定作废，下一次成功刷新再全部复原 —— 用户看到的就是标题 / 歌词 / 封面
                // 一起闪一下，然后歌词被清空重取。真会话消失由 UpdateSession / SessionsChanged 负责，
                // 这里保持「最后已知的良好状态」才是对的（与 props == null 的处理保持一致）。
                Logger.Error("读取媒体属性失败，可能遇到不规范的媒体源", ex);
            }

            try
            {
                var playbackInfo = _currentSession!.GetPlaybackInfo();
                _isPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                _isPlaying = false;
            }

            long durationSec = 0;
            try { if (_currentSession!.GetTimelineProperties() is { } t) durationSec = (long)t.EndTime.TotalSeconds; } catch { }

            if (Title != _lastFetchedTitle || Artist != _lastFetchedArtist)
            {
                _lastFetchedTitle = Title;
                _lastFetchedArtist = Artist;
                _ = FetchMediaAsync(Title, Artist, durationSec);
            }
        }

        // ---- 曲目判定 / 音乐模式 / 封面选择 ----

        /// <summary>
        /// 判定「音乐模式」并落定最终显示的标题 / 歌手。
        ///
        /// 判据对所有软件一致：SMTC 同时给出歌名与歌手 → 音乐模式（取歌词 + 网络封面）；
        /// 否则视频模式，只显示标题、不显示歌手。
        ///
        /// 为什么要缓存「稳定」标题 / 歌手而不是直接用这一拍采样：SMTC 在 seek / 换轨 / 刷新瞬间
        /// 会间歇性给出空标题或空歌手，直接采信会让同一首歌的模式来回翻转，而渲染线程的
        /// 「非歌词会话」闸门会在翻成视频模式那一帧清空已显示的歌词 —— 表现就是歌词一闪一闪、
        /// 翻译消失、封面被程序图标顶掉。规则：
        ///   换了会话            → 整条曲目信息重置；
        ///   同会话内标题变了    → 视为换曲，歌手跟着换成这一拍的值（新曲目的歌手可能还没上报）；
        ///   同会话内标题没变    → 空歌手不改动已记住的值，只在拿到非空值时补齐 / 纠正。
        ///
        /// 另外给降级留了宽限（MusicModeMissGrace 次）：真实视频会一直缺歌手，几次之后照样降级；
        /// 而换曲瞬间「歌手晚一拍才到」不会把模式打回去。
        /// </summary>
        /// <param name="smtcTitle">SMTC 原始标题（浏览器已按网页标题规则清理过）</param>
        /// <param name="smtcArtist">SMTC 原始歌手（B站恒为空串）</param>
        private void UpdateMediaMode(string smtcTitle, string smtcArtist)
        {
            bool appChanged = !string.Equals(_trackAppId, _currentAppId, StringComparison.Ordinal);
            bool titleChanged = smtcTitle.Length > 0
                && !string.Equals(_trackTitle, smtcTitle, StringComparison.Ordinal);

            if (appChanged)
            {
                _trackAppId = _currentAppId;
                _trackTitle = smtcTitle;
                _trackArtist = smtcArtist;
                _musicModeMisses = 0;
                // 换会话是模式判定里最强的一次事件：当场定模式，不走宽限。
                //    否则从「有歌手的会话」切到「没歌手的会话」时，_isMusicMode 会挂着旧值继续为 true，
                //    宽限那几拍里歌词不清空、封面不重选 —— 屏上就会出现
                //    「上一个会话的歌词 + 这个会话的图标」这种错配。
                _isMusicMode = _trackTitle.Length > 0 && _trackArtist.Length > 0;
            }
            else if (titleChanged)
            {
                _trackTitle = smtcTitle;
                _trackArtist = smtcArtist;
                _musicModeMisses = 0;
            }
            else if (smtcArtist.Length > 0)
            {
                _trackArtist = smtcArtist; // 同曲目内歌手补齐 / 纠正
            }

            if (_trackTitle.Length > 0 && _trackArtist.Length > 0)
            {
                _isMusicMode = true;
                _musicModeMisses = 0;
            }
            else if (_isMusicMode && ++_musicModeMisses < MusicModeMissGrace)
            {
                // 已经在音乐模式、这一拍只是没拿到歌手/歌名：先忍着，别把歌词与封面打掉
            }
            else
            {
                _isMusicMode = false;
            }

            // 换了会话 / 换了曲目：通知渲染线程把标题重试的额度清零 ——
            // 上一首用剩的额度不该带到下一首（下一首可能同样是「先报应用名」的播放器）。
            if (appChanged || titleChanged) _titleRetryResetPending = true;

            Title = _trackTitle.Length > 0 ? _trackTitle : "Unknown";
            Artist = _isMusicMode ? _trackArtist : ""; // 视频模式：只要标题，歌手不要
        }

        /// <summary>
        /// 选封面。视频模式 → 该程序自己的应用图标；音乐模式 → 外部封面 → 应用图标兜底。
        ///
        /// 外部封面有两条来源，都是异步补上，在它到达之前先用应用图标顶着：
        ///   1. 会话自带封面（FetchSmtcCoverAsync）：**对所有会话优先**（本地 SMTC 缩略图，不走网络，
        ///      比按歌名 + 歌手去曲库搜又快又准），由 allowSessionCover 放行 ——
        ///      属性刷新时传「本会话确实带了图」，兜底重试时传 true（未知，试一次）；
        ///   2. 网络搜索封面（FetchCoverAsync）：会话没给图（或读取 / 解码失败）时的既有通路。
        ///
        /// 读取节奏：每一首曲目至少发起一次；同一曲目的重试按 SessionCoverRetryInterval 节流
        /// （兜底重试每帧都会走到这里）。
        ///
        /// 本曲目的外部封面一旦就位就保持（「就位」的完整判据见 coverInPlace）—— 判据里刻意不带
        /// 「当前是不是视频模式」：模式判定抖动或属性读取失败都不该把一张已经到手的专辑封面
        /// 换成程序图标（换掉就再也回不来了）。
        ///
        /// 不再引用 data\image 下的平台站标（资源保留，只是不再被任何代码路径读到）。
        /// </summary>
        private void UpdateCover(bool allowSessionCover)
        {
            // 「屏上这张封面确实是本曲目从外部取到的那张」的完整判据：
            //   记账命中（曲目标题 + 会话一致）且当前不是应用图标（_appIconKey 非空即说明屏上放的是图标）。
            //
            // 为什么必须带上「不是图标」这一条：记账是按曲目写的，切到无歌手的会话时封面会被换成
            //   应用图标，而记账还停在上一曲。切回该曲目时若不再选一次，记账恰好命中就永远不换 ——
            //   屏上于是留下「这个会话的歌词 + 上一个会话的封面 / 图标」这种错配。
            bool coverInPlace = _appIconKey.Length == 0
                && string.Equals(_externalCoverTitle, _trackTitle, StringComparison.Ordinal)
                && string.Equals(_externalCoverAppId, _trackAppId, StringComparison.Ordinal);
            if (coverInPlace) return;

            // 会话自带封面（本地 SMTC 缩略图）一律优先于网络搜索封面 —— 会话已经把图递到手上了，
            // 直接解码就能上屏，不必等「按歌名 + 歌手去曲库搜」的那一次网络请求（这条正是加载慢的来源）。
            // 拿不到（会话没给图 / 解码失败）时不记账，随后的网络封面链路照常接管（见 FetchSmtcCoverAsync），
            // 所以最差也只是回到原来的行为。名单 SmtcCoverPreferredIds 已不再参与判定（保留以备回退）。
            bool preferSessionCover = true;

            // 即将去取会话自带封面（本地 SMTC 缩略图，马上就到）时**不**抢先顶应用图标：
            //    顶上去会把屏上还在的上一张封面换掉、新封面到了再换回来 —— 用户看到的就是切换瞬间封面闪一下；
            //    图标还没解析出来时更糟（异步返回 null），直接就是那张蓝色兜底。
            //    宁可让上一张封面多留一会儿：这就是「下一张没到就保持上一张」。
            // 于是只有两种情况顶图标：① 本会话没带图（视频模式 / 只给标题的播放器，不会有新封面来了）；
            //    ② 手上什么都没有（首次刷新、上一个会话的图已被清），免得一直空着。
            bool willTrySessionCover = allowSessionCover && preferSessionCover;
            if (!willTrySessionCover || Thumbnail == null) SetAppIcon();

            if (!willTrySessionCover) return;

            // 节流按曲目判：换歌是新事件，必须立刻能再试一次 —— 只看时间的话，连续切歌时
            // 后一首会被前一首的计时挡住，于是它连读都不读，封面直接停在上一首。
            var now = DateTime.UtcNow;
            bool sameTrack = string.Equals(_sessionCoverAttemptTitle, _trackTitle, StringComparison.Ordinal)
                             && string.Equals(_sessionCoverAttemptAppId, _trackAppId, StringComparison.Ordinal);
            if (sameTrack && now - _lastSessionCoverAttempt < SessionCoverRetryInterval) return;

            _sessionCoverAttemptTitle = _trackTitle;
            _sessionCoverAttemptAppId = _trackAppId;
            _lastSessionCoverAttempt = now;

            _ = FetchSmtcCoverAsync(_trackTitle, _trackAppId);
        }

        /// <summary>
        /// 从会话 AppID 推出「应用名」：取进程名（去掉路径与扩展名）后小写。结果按 AppID 缓存。
        /// 例：PotPlayerMini64.exe → potplayermini64，AppleInc.AppleMusicWin_8wekyb3d8bbwe → 原样。
        /// </summary>
        private string AppNameFromAppId()
        {
            string appId = _currentAppId;
            if (string.Equals(appId, _appNameCacheKey, StringComparison.Ordinal)) return _appNameCacheValue;

            string name = appId;
            int slash = name.LastIndexOfAny(PathSeparators);
            if (slash >= 0) name = name[(slash + 1)..];
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];

            _appNameCacheKey = appId;
            _appNameCacheValue = name.ToLowerInvariant();
            return _appNameCacheValue;
        }

        /// <summary>
        /// 标题是不是「还没就绪」—— 空串，或者当前显示的就是本会话的应用名而不是曲目名。
        ///
        /// 后者是 PotPlayer 播网络文件时的典型表现：文件没加载完之前，SMTC 的 Title 填的是
        /// 播放器自己的名字，所以「标题 == 应用名」是可靠的未就绪信号。
        /// 双向包含是为了兼容 AppID 与显示名对不齐的写法（PotPlayerMini64.exe ↔ PotPlayer）。
        /// </summary>
        private bool IsTitleNotReady()
        {
            // 浏览器会话不参与：它的标题是网页标题，撞上 AppID 关键字（chrome / msedge）的概率
            // 比播放器高得多，而浏览器也不会「先报应用名、加载完再换真名」。
            if (_isBrowserSession) return false;

            string title = _trackTitle;
            if (title.Length == 0) return true;
            // 太短的标题两边都容易撞上（歌名 "Pot" 之类），宁可不判定：漏判只是少几次重试，
            // 误判却会让一首正常的歌被反复重读属性
            if (title.Length < 4) return false;

            string appName = AppNameFromAppId();
            if (appName.Length < 3) return false;

            return title.Contains(appName, StringComparison.OrdinalIgnoreCase)
                || appName.Contains(title, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 渲染循环里的标题兜底重试（原因见 TitleRetryMax 上方的注释）。
        /// 未就绪 → 先等首延迟再读第一次（此刻读多半还是同一个旧值），此后按固定间隔重读，
        /// 直到标题不再像应用名、或额度用尽。
        /// </summary>
        private void RetryTitleIfNeeded(DateTime now)
        {
            // 换会话 / 换曲目：额度清零重来（标志由属性刷新线程置位，见 UpdateMediaMode）
            if (_titleRetryResetPending)
            {
                _titleRetryResetPending = false;
                _titleRetryCount = 0;
                _titleRetryNextAt = DateTime.MinValue;
            }

            if (!IsTitleNotReady())
            {
                // 就绪：撤掉本轮排期，下一首遇到未就绪时重新从首延迟开始
                _titleRetryCount = 0;
                _titleRetryNextAt = DateTime.MinValue;
                return;
            }

            if (_titleRetryCount >= TitleRetryMax) return;

            if (_titleRetryNextAt == DateTime.MinValue)
            {
                _titleRetryNextAt = now + TitleRetryFirstDelay;
                return;
            }
            if (now < _titleRetryNextAt) return;

            _titleRetryCount++;
            _titleRetryNextAt = now + TitleRetryInterval;
            // 必须挪到线程池上执行：RefreshPropertiesCore 在第一个 await 之前是同步跑的，
            // 里面那次 TryGetMediaPropertiesAsync 的「发起」动作会落在调用线程上 ——
            // 从渲染线程直接调，就是在渲染线程上打一次 COM（卡顿的来源之一）。
            _ = Task.Run(RefreshProperties);
        }

        private static readonly string[] BrowserVideoSuffixes =
        [
            "_哔哩哔哩_bilibili",
            "-电视剧-高清完整正版视频在线观看-优酷",
            "-电影-高清完整正版视频在线观看-优酷",
            "-综艺-高清完整正版视频在线观看-优酷",
            "-最新热门短剧大全-免费短剧在线观看",
            "-动漫-高清完整正版视频在线观看-优酷",
            "-少儿-高清完整正版视频在线观看-优酷",
            "-纪录片-高清完整正版视频在线观看-优酷",
            "-体育-高清完整正版视频在线观看-优酷",
            "-文化-高清完整正版视频在线观看-优酷",
            "-游戏-高清完整正版视频在线观看-优酷",
            "-音乐-高清完整正版视频在线观看-优酷",
        ];

        [GeneratedRegex(@"^正在播放[:：]\s*(.*?)\s*-\s*(.*)$")]
        private static partial Regex PlayingTitleRegex();

        private static string CleanBrowserTitle(string title, out string artist)
        {
            artist = "";
            var trimmed = title.TrimEnd();
            var playingMatch = PlayingTitleRegex().Match(trimmed);
            if (playingMatch.Success)
            {
                artist = playingMatch.Groups[2].Value.Trim();
                trimmed = playingMatch.Groups[1].Value.Trim();
            }
            else if (trimmed.StartsWith("正在播放", StringComparison.Ordinal)
                     && trimmed.Length > 4 && (trimmed[4] == ':' || trimmed[4] == '：'))
            {
                trimmed = trimmed[5..].Trim();
            }

            foreach (var suffix in BrowserVideoSuffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return trimmed[..^suffix.Length];
            }
            return trimmed;
        }

        public async void TogglePlayPause()
        {
            if (_currentSession == null) return;
            if (IsPlaying) await _currentSession.TryPauseAsync();
            else await _currentSession.TryPlayAsync();
        }

        public async void Next() => await _currentSession?.TrySkipNextAsync();
        public async void Previous() => await _currentSession?.TrySkipPreviousAsync();

        /// <summary>
        /// 双击封面（折叠态是左端缩略图、展开态是那块封面，两种形态同一条判据）时调用：
        /// 跳回正在放媒体的那个应用。
        ///
        /// 能做的：把已开着的应用窗口激活到前台；应用没开时按注册信息把它拉起来。
        /// 不能做的：跳到那首歌 / 那个视频的具体播放页 —— SMTC 不提供任何深链接参数，
        /// 这是协议本身的限制，只能到应用本体。
        ///
        /// 定位（前台窗口采样 / 进程内枚举窗口 / 注册表核验）都在
        /// MediaAppLauncher 里，UI 线程上只做几次极廉价的 API 调用。
        /// </summary>
        public void OpenCurrentApp()
        {
            if (!IsAppLaunchEnabled) return;
            MediaAppLauncher.OpenCurrentSessionApp();
        }

        /// <summary>
        /// 获取 Just Solo LyricServer 推送的实时频谱（12 频段，低频→高频）。
        /// 返回 false 表示不可用（未连接 / 服务端版本过低 / 已暂停），调用方应回退到本地音频采集。
        /// </summary>
        public bool TryGetSoloSpectrum(out float[] bands) => _justSoloLyric.TryGetSpectrum(out bands);

        /// <summary>
        /// 把内置音量下发给 Just Solo 播放器（协议 v1.3.0 的 volume 指令，level: 0.0~1.0）。
        /// 返回 true 表示已由 WS 接走（此时不该再去改系统音量）；未连接 Just Solo 时返回 false。
        /// </summary>
        public bool TrySyncVolumeToJustSolo(float level)
        {
            if (!_justSoloLyric.IsConnected) return false;
            _justSoloLyric.SendVolume(level);
            return true;
        }

        /// <summary>
        /// Just Solo 播放器当前音量（0.0 ~ 1.0）—— 与系统音量互不覆盖的独立变量，
        /// 由 WS 上的音量操作（本机下发 / 服务端回推 / 连接补推）镜像维护。
        /// 返回 false 表示还没从 WS 拿到过音量（没连上 / 服务端还没推）。
        /// </summary>
        public bool TryGetJustSoloVolume(out float volume) => _justSoloLyric.TryGetVolume(out volume);

        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshProperties();
        }

        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            // 通用媒体的自动匹配完全跟随「谁在播放」，所以来自任何会话的播放/暂停都要重挑一次：
            // 尤其是接管中的那个会话自己暂停时（「都未播放就回到 justsolo」），必须重新选，
            // 只刷属性会让界面停在那个已经暂停的会话上。
            if (_manager != null && IsMediaControlEnabled && TargetPlatform == "other" && !IsManualSessionMatch)
            {
                await UpdateSession(_manager);
                return;
            }

            // 手动模式与具体平台模式的接管目标不受播放状态影响，事件来自接管会话时刷新属性即可
            if (string.Equals(sender.SourceAppUserModelId, _currentAppId, StringComparison.OrdinalIgnoreCase))
                await RefreshProperties();
        }

        // ---- 媒体资源获取（歌词 + 封面） ----
        // 「取词」与「取封面」是两件独立的事，各自成一个函数：
        //   · FetchLyricsAsync —— 四个歌词引擎依次兜底，命中即写 _lyrics；
        //   · FetchCoverAsync  —— 只负责把网络封面下载下来换上，失败就保持程序图标兜底。
        //
        // 唯一的调用点是 FetchMediaAsync：排队、换歌清理、以及「这首歌还该不该被取」的校验都在那里，
        // 两条链因此共用同一张网络通行证（_fetchLock），不会各打一遍搜索 ——
        // 封面地址就是取词时的搜索顺带带回来的（见 FetchLyricsAsync 的返回值）。

        /// <summary>
        /// 媒体资源获取的唯一入口：先取歌词，再取封面。由 RefreshPropertiesCore
        /// 在「歌名或歌手变化」时触发一次。
        /// </summary>
        private async Task FetchMediaAsync(string title, string artist, long durationSec)
        {
            // 无歌词能力的会话（浏览器 / 视频类 / PotPlayer）：完全不动已有歌词与时间轴，
            // 让上一首歌的状态原样冻结，切回来时可以立即续播
            if (IsNonLyricSession) return;

            if (string.IsNullOrEmpty(title))
            {
                _lyrics = Array.Empty<(TimeSpan, string, string)>();
                _lyricWordTimings = null;
                _lyricsHasTranslation = false;
                CurrentLyric = "";
                CurrentLyricTranslation = "";
                HasLyricTranslation = false;
                return;
            }

            // 切回来的是同一首歌（如音乐→浏览器→音乐）：沿用已抓到的歌词与已推进的时间轴，
            // 既不重新计时，也不重复请求网络。重置 SMTC 基准是为了重新做一次跳变对齐，
            // 让 Spotify 这类有真实时间轴的播放器能自动校正挂起期间累积的误差。
            if (IsLyricOwner(title, artist))
            {
                ReleaseSuspension();
                _forceResync = true;
                return;
            }

            // ---- 换歌：强制重载歌词与时间轴。----
            // 时间轴基准（0 点）就是此刻 —— SMTC 会话把「这首歌开始播放」告诉我们的第一时间。
            // 它必须落在下面那次取词 await 之前：异步方法在第一个未完成的 await 之前是同步执行的，
            // 所以「位置归零 / 绑定槽位 / 清空旧歌词」三步必然先完成，取词花多久都不会被算进歌词进度
            // （取词链是秒级的：单档约 0.3~0.6 秒，多档兜底约 3 秒）。
            // 取到歌词只是往这条已经走起来的时间轴上贴文本（见 FetchLyricsAsync 末尾），不重置位置。
            string appId = _currentAppId;
            int newSlot = SlotFor(title, artist);

            // 清理后台登记：当前 App 已经回到台前，它名下所有登记一律作废（那些歌不可能还在后台放）。
            // 少了这一步，上一轮退到后台的歌会被每秒 +1 地推进，下次切回去就是「残留进度」。
            for (int i = 0; i < RecentSongSlots; i++)
            {
                if (i == newSlot || _slotSessions[i] == null) continue;
                if (_recentSongs[i].AppId == appId) _slotSessions[i] = null;
            }

            if (newSlot >= 0)
            {
                // 进度续用判定：只有「退到后台、仍在播放」的那首歌才允许续用进度。
                // 其余一律归零 —— 缓冲里翻出来的陈年快照、以及同 App 换歌后又切回来的那首歌，
                // 都已经从 0 重新开始，续用就会让歌词从上一首的进度继续播。
                //
                // 整槽先快照再整槽写回，不逐字段读改写：本方法跑在线程池线程，
                // 而渲染线程可能正在读写同一槽位（AdvanceTimeline / AdvanceSuspendedTimeline）。
                // 逐字段写虽然落在不相交的内存范围上，但「读旧槽 → 算 resumable → 写回」
                // 这个序列会让两边各丢掉对方刚写的那一笔（丢失更新）。
                // 本地快照算完之后只做一次整槽赋值，把窗口压到最小。
                var slotState = _recentSongs[newSlot];
                bool resumable = slotState.AppId == appId
                                 && _slotSessions[newSlot] != null
                                 && IsFreshSlot(slotState);
                _recentSongs[newSlot] = (
                    slotState.Title,
                    slotState.Artist,
                    resumable ? slotState.Position : TimeSpan.Zero,
                    slotState.TickedAt,
                    appId);
                _slotSessions[newSlot] = null; // 这首歌已回到台前，交回当前会话推进
            }

            _lyricSlot = newSlot;
            _forceResync = true;
            // 新曲目：清空上一首的重试记账，并把「下次可重试时刻」推到此刻之后 ——
            // 否则首次取词还在路上（槽位已绑定、_lyrics 尚空）时，渲染循环会立刻判定
            // 「没歌词」而并排再打一次同样的请求。
            _lyricRetryCount = 0;
            _lastLyricRetryAt = DateTime.UtcNow;
            _lyrics = Array.Empty<(TimeSpan, string, string)>();
            _lyricWordTimings = null;
            _lyricsHasTranslation = false;
            CurrentLyric = "";
            CurrentLyricTranslation = "";
            HasLyricTranslation = false;
            CurrentLyricProgress = 0f;

            // ---- 取词 → 取封面：两条链各自成一个函数，各自排队、各自校验归属 ----
            try
            {
                string coverUrl = await FetchLyricsAsync(title, artist, durationSec);
                await FetchCoverAsync(title, artist, coverUrl);
            }
            catch (Exception ex)
            {
                // 必须自己兜住：本方法是 fire-and-forget 调用的（见 RefreshPropertiesCore），
                // 异常逃逸后只会被全局钩子记成一条没有曲目信息的「未观察的 Task 异常」——
                // 本项目曾据此排查很久却无法定位（真正的原因藏在取词链深处）。
                // 落在这里至少带上了歌名 / 歌手，下一次一眼就能对上。
                Logger.Error($"取词/封面链异常（{title} / {artist}）", ex);
            }
        }

        /// <summary>
        /// 渲染循环里的取词补偿，每帧调用（真正的判定很轻：几个字符串比较 + 一次时间差）。
        ///
        /// 触发条件：当前歌已归属槽位、却一条歌词都没有。此时才申请重试额度，并重新读一次
        /// SMTC 总长再取词 —— 这一条同时治两种病：首次取词时时长还是上一首的（被候选的
        /// 时长门槛整条挡掉），以及纯粹的网络偶发失败。重试间隔 2 秒，那时 timeline 早已跟上。
        ///
        /// 刻意不重置时间轴：重试只是往已经在走的那条时间轴上补文本，位置必须保持连续。
        /// </summary>
        private void RetryLyricsIfNeeded()
        {
            // 视频模式（无歌手）本来就不取歌词，重试没有意义
            if (IsNonLyricSession) return;
            if (_lyrics.Length > 0) return;

            // 槽位是异步线程写的 volatile：先快照到局部再判定，否则「判 <0」与「拿来索引」
            // 之间可能被换成 -1，那一瞬就是 IndexOutOfRangeException（渲染线程上等于崩进程）。
            int slot = _lyricSlot;
            if (slot < 0 || slot >= RecentSongSlots) return;

            // 暂停时不刷网络请求，等真正播放起来再说
            if (!_isPlaying) return;

            var owner = _recentSongs[slot];
            // 无标题 / 无歌手的槽位没有可搜的对象（视频模式兜底；槽位元组默认值是 null）
            if (string.IsNullOrEmpty(owner.Title) || string.IsNullOrEmpty(owner.Artist)) return;
            // 屏上已经不是这首歌了（槽位还没被改写，但会话已切走）
            if (!string.Equals(owner.Title, _trackTitle, StringComparison.Ordinal)) return;

            if (!TryBeginLyricRetry()) return;

            Logger.Debug($"取词失败，第 {_lyricRetryCount} 次重试（{owner.Title} / {owner.Artist}）");
            // Task.Run：RetryFetchAsync 的第一个动作是 CurrentTimelineSeconds()（打 COM 读总长），
            // 它在第一个 await 之前同步执行 —— 直接调就是在渲染线程上打 COM。
            _ = Task.Run(() => RetryFetchAsync(owner.Title, owner.Artist));
        }

        /// <summary>重试取词链：只补歌词与封面，不碰时间轴、不重绑槽位。</summary>
        private async Task RetryFetchAsync(string title, string artist)
        {
            try
            {
                string coverUrl = await FetchLyricsAsync(title, artist, CurrentTimelineSeconds());
                await FetchCoverAsync(title, artist, coverUrl);
            }
            catch (Exception ex)
            {
                // 自行兜住并落日志：这两条链是 fire-and-forget 调用的，异常一旦逃出去就只剩
                // 全局钩子那条「未观察的 Task 异常」—— 没有曲目信息、定位极难（本项目踩过）。
                Logger.Error($"取词重试链异常（{title} / {artist}）", ex);
            }
        }

        /// <summary>
        /// 取歌词。译文与封面都随主歌词一起回来，不额外单开接口。按逐字歌词开关分两条路：
        ///
        /// 开关打开（IsLyricScanEnabled，默认开）：逐字优先 —— 先走两个能给出
        /// 逐字时间轴的落月源；其余三档（QQ 音乐官方歌词 / 网易云官方 / LRCLIB）不带逐字数据，不参与
        /// 「优先」，但两个落月源都没给出可用歌词时，由网易云官方 → LRCLIB 依次兜底 ——
        /// 宁可给一份没有逐字的歌词（扫光自动回退整行），也不能整首歌没歌词。
        ///   · 网易云音乐播放时：歌词固定取落月 API(网易云) —— 同一曲库、版本天然一致，
        ///     并且它是先请求的那一档；落月 API(QQ音乐) 退居补料渠道（网易云没给出可用歌词时
        ///     由它顶上 / 借逐字 / 封面兜底），三样都由网易云带齐时不再请求它。逐字先看网易云自己有没有，
        ///     没有才向落月 QQ 借（借之前校验两边歌词是同一版本）。
        ///   · 其他播放器：落月 API(QQ音乐) → 落月 API(网易云) 依次兜底，两档都拿不到就没有歌词。
        ///
        /// 开关关闭：五个引擎依次兜底（落月 API(QQ音乐) → QQ 音乐官方歌词 →
        /// 落月 API(网易云) → 网易云官方 → LRCLIB）。会话是网易云音乐时另走「网易优先」：
        /// 网易系两档（落月 API(网易云) → 网易云官方）整体提到最前。
        ///
        /// 逐字缺失不等于没有歌词：落到落月网易云时若它没带逐字数据，歌词照常采纳、
        /// 扫光自动回退整行 —— 不会因此变成「没歌词」。
        ///
        /// 版本正确性：落月 QQ 的候选要过「歌名 + 歌手 + 时长」三道门槛（见 MatchSong）；
        /// 借逐字时另要两份歌词通过一致性校验（见 LyricsAreSameSong）。两道关卡都是为了不让
        /// 同名不同版本的条目（如《海屿你2.0》）把歌词或逐字带错。
        ///
        /// 封面不受这条分流影响：两个落月源各自带回封面，QQ 档内部还有 smartbox 直连兜底，
        /// 方法末尾另有一层不依赖任何取词档的 QQ 直连封面兜底。
        /// </summary>
        /// <returns>网络封面地址；没有则空串（交给 FetchCoverAsync 消费）。</returns>
        private async Task<string> FetchLyricsAsync(string title, string artist, long durationSec)
        {
            // 等待获取通行证（防止多首歌同时修改 HttpClient 导致程序崩溃）
            await _fetchLock.WaitAsync();
            try
            {
                // 极速拦截：排队轮到自己时，这首歌若已不是时间轴归属者（换歌了）就直接丢弃任务，0 性能浪费。
                // 判据是槽位而不是 Title —— 切到浏览器期间 Title 会变成网页标题，
                // 但那首歌并没有被换掉，此时不该丢弃请求。
                if (!IsLyricOwner(title, artist)) return "";

                string lrcText = "";
                // 译文 LRC：与原文同一套时间戳，解析后按时间对齐成「原文行 → 译文」的映射
                string transText = "";
                // 逐字歌词（落月两个源歌词接口的 data.yrc）。与 lrcText 同生共死 —— 原文没被采纳时
                // 逐字也不采纳，免得出现「逐字时间轴属于另一首歌」。
                string yrcText = "";
                // 两个落月源的 yrc 片段语法正好相反：网易云是「时间在前」`(21680,370,0)你`，
                // QQ 是「文字在前」`游(66,168)`。由采纳它的那一档置位，供下面建表时选解析方向。
                bool yrcTimingFirst = false;
                // 网络封面地址（网易系优先段 / 落月搜索的 cover / 网易云 song/detail 的 picUrl）。
                // 仅音乐模式会用；都没拿到就保持兜底封面（程序图标）。
                string coverUrl = "";

                // 逐字歌词开关打开时只走两个带逐字数据的落月源（见方法说明），其余档位包括
                // 「网易优先」与 LRCLIB 一律不参与 —— 顺序固定为落月 QQ 在前、落月网易云兜底。
                bool luoYueOnly = IsLyricScanEnabled;

                // 正在放歌的是不是网易云音乐。是的话，网易系两档会被提到整条链的最前面（见下面「网易优先」段），
                // 歌词与封面都优先网易云的源；其余播放器一切照旧。
                // 「只走落月两档」时不存在这个前置。
                bool isNetease = IsNeteaseAppId(_currentAppId);
                bool preferNetease = !luoYueOnly && isNetease;

                // 逐字开关打开 + 网易云会话：歌词固定取「落月 API - 网易云」—— 它与播放器同一曲库，
                // 版本天然一致（QQ 曲库会被同名的其它版本顶掉，如《海屿你2.0》）；逐字先看网易云自己有没有，
                // 没有才向「落月 API - QQ音乐」借，且借之前要确认两边歌词确实是同一版本。
                bool neteaseWordSource = luoYueOnly && isNetease;

                // ---- 网易优先：会话是网易云音乐时，把网易系两档提到最前 ----
                // 正在放歌的就是网易云，用网易云曲库最贴：同一曲库来源，版本能对上、译文更全、专辑图也更对版。
                // 歌词与封面一起前置，顺序钉死 —— 落月 API - 网易云 在前、网易云官方在后。
                // 其他播放器不走这一段，网易系两档在引擎 3 / 引擎 4 的位置上充当兜底。
                // 逐字开关打开时整段跳过：那时顺序固定为落月 QQ 在前（见方法说明）。
                if (preferNetease)
                {
                    // ① 落月 API - 网易云：歌词 + 网易云 CDN 专辑图（同一次搜索顺带给出）
                    var neteaseFirst = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    if (!string.IsNullOrEmpty(neteaseFirst.Lrc))
                    {
                        lrcText = neteaseFirst.Lrc;
                        yrcText = neteaseFirst.Yrc ?? "";
                        yrcTimingFirst = true; // 落月网易云的 yrc 是「时间在前」
                    }
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(neteaseFirst.Trans))
                        transText = neteaseFirst.Trans;
                    if (!string.IsNullOrEmpty(neteaseFirst.Cover)) coverUrl = neteaseFirst.Cover;

                    // ② 网易云官方：歌词兜底 + 封面兜底（它给出的同样是网易云的专辑图）。
                    //    条件是「没歌词或没封面」—— 也就是这一档可能只为封面而跑。
                    if (!HasTimedLyric(lrcText) || coverUrl.Length == 0)
                    {
                        var neteaseOfficialFirst = await FetchFromNeteaseOfficialAsync(title, artist, durationSec, allowCover: true);
                        // 已经有可用歌词时不覆盖 —— 这一趟可能只是为了补封面
                        if (!HasTimedLyric(lrcText) && !string.IsNullOrEmpty(neteaseOfficialFirst.Lrc))
                            lrcText = neteaseOfficialFirst.Lrc;
                        if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(neteaseOfficialFirst.Trans))
                            transText = neteaseOfficialFirst.Trans;
                        if (coverUrl.Length == 0 && !string.IsNullOrEmpty(neteaseOfficialFirst.Cover))
                            coverUrl = neteaseOfficialFirst.Cover;
                    }
                }

                // ---- 引擎 1：落月 API - QQ音乐（主源：原文 + 译文 + 封面 + songmid 一次到位） ----
                // QQ 官方的搜索接口（c.y.qq.com/soso/fcgi-bin/client_search_cp）恒返回 HTTP 500 空响应，
                //    拿不到 songmid，它后面那次取词也就无从谈起。所以主源用落月：
                //    同为 QQ 曲库，一次响应把四样东西给齐：
                //      · data.lrc 与 data.trans 同源，时间戳严格对齐 ⇒ 译文不会缺句；
                //      · 搜索响应里的 cover 就是 QQ 专辑图地址；
                //      · 搜索响应里的 mid 就是 QQ 的 songmid（交给引擎 2）。
                //    放在最前面还有个好处：命中就不必再问后面的引擎，总请求数反而更少。
                //
                // 唯一的例外是「网易云会话 + 逐字开关打开」（见 neteaseWordSource）：那一支的歌词主来源
                // 是落月网易云 —— 与播放器同一曲库、版本天然一致，落月 QQ 退居补料渠道，
                // 于是网易云先请、QQ 只在还缺料时才请（见下）。
                LuoYueResult luoYue = default;
                bool needQq = true;

                if (neteaseWordSource)
                {
                    // 歌词：落月网易云（同一曲库、版本最贴），逐字先用它自己的。
                    var neWord = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    if (!string.IsNullOrEmpty(neWord.Lrc))
                    {
                        lrcText = neWord.Lrc;
                        yrcText = neWord.Yrc ?? "";
                        yrcTimingFirst = true;              // 落月网易云的 yrc 是「时间在前」
                    }
                    if (!string.IsNullOrEmpty(neWord.Trans)) transText = neWord.Trans;
                    if (!string.IsNullOrEmpty(neWord.Cover)) coverUrl = neWord.Cover;

                    // 落月 QQ 只在还缺东西时才请。它一次给三样（歌词兜底 / 逐字 / 封面），
                    // 三样都由网易云带齐时这两次请求（搜索 + 取词，实测约 0.9 秒）就没有产出 ——
                    // 逐字开关打开时引擎 2 不参与，它顺带的 songmid 也无人消费。
                    // 顺带也让歌词更早到位：网易云那一趟排在前面，命中即显示，不必先等 QQ 跑完。
                    needQq = !HasTimedLyric(lrcText)
                             || yrcText.Length == 0
                             || coverUrl.Length == 0;
                }

                if (needQq)
                {
                    luoYue = await FetchFromLuoYueAsync(title, artist, durationSec, HttpUserAgent);

                    if (neteaseWordSource)
                    {
                        // 网易云没给出可用歌词（没搜到 / 空壳响应）⇒ 由落月 QQ 顶上。
                        // 它在这一支里是备选档，与「其他播放器」下网易云替 QQ 兜底是同一个道理，
                        // 只是方向相反。歌词与逐字出自同一份响应，一起采纳，不必做一致性校验。
                        if (!HasTimedLyric(lrcText) && !string.IsNullOrEmpty(luoYue.Lrc))
                        {
                            lrcText = luoYue.Lrc;
                            yrcText = luoYue.Yrc ?? "";
                            yrcTimingFirst = false;             // 落月 QQ 的 yrc 是「文字在前」
                            if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(luoYue.Trans))
                                transText = luoYue.Trans;
                        }
                        // 网易云给了歌词、只是没给逐字 ⇒ 向落月 QQ 借逐字。前提是两份歌词确实是同一版本 ——
                        // 逐字时间轴按行、按文本贴在歌词上，版本不同就会整段错位。
                        else if (yrcText.Length == 0
                            && luoYue.Yrc is { Length: > 0 } borrowed
                            && !string.IsNullOrEmpty(luoYue.Lrc)
                            && !string.IsNullOrEmpty(lrcText)
                            && LyricsAreSameSong(luoYue.Lrc, lrcText))
                        {
                            yrcText = borrowed;
                            yrcTimingFirst = false;             // 落月 QQ 的 yrc 是「文字在前」
                        }
                    }
                    // 其余会话：歌词与译文只在还没有可用时间轴时采纳。本档同时是 songmid 的来源，
                    // 所以即使歌词已被前面的档先取到，下面这一次搜索照常进行（封面与 songmid 仍由它提供）。
                    else if (!HasTimedLyric(lrcText))
                    {
                        if (!string.IsNullOrEmpty(luoYue.Lrc))
                        {
                            lrcText = luoYue.Lrc;
                            yrcText = luoYue.Yrc ?? "";
                            yrcTimingFirst = false; // 落月 QQ 的 yrc 是「文字在前」
                        }
                        if (!string.IsNullOrEmpty(luoYue.Trans)) transText = luoYue.Trans;
                    }

                    // 封面：填封面链的第一档，仅在还没有封面时采纳
                    //（网易云会话下上面的分支可能已给出网易云的图，此时不覆盖）。
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(luoYue.Cover)) coverUrl = luoYue.Cover;
                }

                // ---- 引擎 2：QQ 音乐官方歌词接口 ----
                // 落月搜索给出的 mid 就是 QQ 的 songmid（实测可直接喂给本接口取回同一份歌词），
                // 所以这里不需要搜索，只多花一次 GET 就多出一条歌词兜底链路 ——
                // 落月的歌词接口偶发失败 / 限流时由它顶上。
                // QQ 官方接口的 trans 经常是空的（实测同一首歌落月有 1986 字译文、QQ 是 0 字），
                //    所以译文只在落月完全没给时才采纳它，免得把一份好译文覆盖成空。
                if (!luoYueOnly && !HasTimedLyric(lrcText) && !string.IsNullOrEmpty(luoYue.Mid))
                {
                    try
                    {
                        _http.DefaultRequestHeaders.Clear();
                        _http.DefaultRequestHeaders.Add("User-Agent", HttpUserAgent);
                        _http.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");

                        using var qqStream = await _http.GetStreamAsync($"{QQMusicLyricApi}?songmid={luoYue.Mid}&format=json&nobase64=1");
                        using var qqDoc = await JsonDocument.ParseAsync(qqStream);

                        if (qqDoc.RootElement.TryGetProperty("lyric", out var qqLrcEl))
                            lrcText = UnescapeQqText(qqLrcEl.GetString());

                        if (string.IsNullOrEmpty(transText)
                            && qqDoc.RootElement.TryGetProperty("trans", out var qqTransEl))
                            transText = UnescapeQqText(qqTransEl.GetString());
                    }
                    catch (Exception ex) { Logger.Warn($"QQ音乐引擎失败: {ex.Message}"); }
                }

                // ---- 引擎 3：落月 API - 网易云（网易云曲库：原文 + 译文，可能带逐字） ----
                // 位置在 QQ 系两档之后：
                //   · 从 QQ 音乐或别家播放器放歌时，前两档（同为 QQ 曲库）基本已经命中，压根走不到这里
                //     —— 也就是「其他软件照旧走 QQ 音乐」；
                //   · 真落到这一档的，多半是「只在网易云上架 / 版本与 QQ 曲库对不上」的歌，
                //     正好由网易云曲库补上。
                // 它与引擎 4 的网易云官方接口是同一个曲库、两套实现，互为兜底。
                // 网易云会话下这一档已经在方法开头的「网易优先」段跑过了，这里直接跳过，不重复请求。
                //
                // 逐字开关打开时它是两个落月源里的最后一档：落月 QQ 没匹配上（只在网易云有版权的歌）、
                // 或匹配上却没带逐字数据，都由它顶上；它自己也取不到时，再由引擎 4 / 5 兜底（见下）。
                // neteaseWordSource 时本档已在引擎 1 段跑过（那里它是「先请」的一档、歌词的主来源），不重复请求。
                bool needNetease = !neteaseWordSource && (luoYueOnly
                    ? !(HasTimedLyric(lrcText) && yrcText.Length > 0)   // 还没有「带逐字的可用歌词」
                    : !HasTimedLyric(lrcText));
                if (!preferNetease && needNetease)
                {
                    var netease = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    // 采纳条件：本档带来了逐字而现有歌词没有（这正是继续往下走的原因），
                    // 或者现有压根没有可用歌词。两档都没带逐字时保留先命中的落月 QQ ——
                    // 那时扫光回退整行，歌词本身照常显示。
                    if (!string.IsNullOrEmpty(netease.Lrc)
                        && ((luoYueOnly && !string.IsNullOrEmpty(netease.Yrc) && yrcText.Length == 0)
                            || !HasTimedLyric(lrcText)))
                    {
                        lrcText = netease.Lrc;
                        yrcText = netease.Yrc ?? "";
                        yrcTimingFirst = true; // 落月网易云的 yrc 是「时间在前」
                    }
                    // 译文只在前面一个都没给到时才采纳，免得把一份好译文覆盖成空（与引擎 2 同一套保护）
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(netease.Trans))
                        transText = netease.Trans;
                    // 封面：本档搜索顺带给出的就是网易云专辑图（p3/p4.music.126.net），
                    //    顶的是封面链的第二档「网易云」—— 引擎 4 只在「还没拿到歌词」时才跑，
                    //    本档一旦命中歌词，那一档就不会执行，它的 album.picUrl 也就没人补。
                    //    采纳条件与引擎 1 同一条：落月 QQ 搜索没给出封面时才用。
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(netease.Cover)) coverUrl = netease.Cover;
                }

                // ---- 引擎 4：网易云官方 API ----
                // 判据是「有没有可用时间轴」而不是「字符串空不空」：前面的引擎可能返回非空但一行时间轴都没有的
                // 结果（版权提示 / 空壳响应），只判空的话网易云与 LRCLIB 会被整段跳过，最终就是「没歌词」。
                // 网易云会话下本档已在方法开头的「网易优先」段跑过，这里跳过，不重复请求。
                //
                // 逐字开关打开时它仍参与 —— 但只在两个落月源都没给出可用歌词之后（`!HasTimedLyric` 守卫）。
                // 落月网易云的搜索接口固定只返回 10 条，排位靠后的冷门 / 翻唱版本拿不到
                // （实测《游京》/林知微 用「游京 林知微」搜要到前 60 条的靠后位置才出现，
                // 用「游京」搜更要翻到第 180 位之后），而本档的搜索 limit=60 正好能召回它。
                // 此时宁可给一份没有逐字的歌词（扫光自动回退整行），也不能整首歌没歌词 ——
                // 这正是「逐字缺失 ≠ 没有歌词」的落实。
                if (!preferNetease && !HasTimedLyric(lrcText))
                {
                    var neteaseOfficial = await FetchFromNeteaseOfficialAsync(title, artist, durationSec, allowCover: true);
                    if (!string.IsNullOrEmpty(neteaseOfficial.Lrc)) lrcText = neteaseOfficial.Lrc;
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(neteaseOfficial.Trans))
                        transText = neteaseOfficial.Trans;
                    // 封面兜底第二档：落月（引擎 1）没给过封面时才用网易云这张。
                    // 必须判空：NeteaseOfficialResult 各字段是 string，这一档失败时若返回 default，
                    // 取出来的就是 null 而不是空串 —— 直接赋值会把 coverUrl 变成 null，
                    // 下面「coverUrl.Length == 0」那次封面兜底判定立刻抛 NullReferenceException，
                    // 整个取词任务以异常收场（既没有歌词也没有封面，日志里只剩一条未观察的 Task 异常）。
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(neteaseOfficial.Cover))
                        coverUrl = neteaseOfficial.Cover;
                }

                // ---- 引擎 5：LRCLIB ----
                // /api/get 要求 track_name 与 artist_name 都非空，缺任一个直接回 400（实测：
                //    artist_name 为空 → 400、track_name 为空 → 400），而不是「这首歌它没有」的 404。
                //    歌名为空在上游已提前返回，所以这里只需挡住歌手为空 —— 否则就是白花一次往返，
                //    还往日志里刷一条看不懂的 400 WARN，而它其实是最后一个兜底引擎。
                // 与引擎 4 同理：逐字开关打开时它也是「两个落月源都没给出歌词」之后的最后一道兜底。
                if (!HasTimedLyric(lrcText) && artist.Length > 0)
                {
                    try
                    {
                        _http.DefaultRequestHeaders.Clear();
                        _http.DefaultRequestHeaders.Add("User-Agent", HttpUserAgent);
                        string lrclibUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                        if (durationSec > 0) lrclibUrl += $"&duration={durationSec}";

                        // 同样优化为 Stream 流解析
                        using var lrclibStream = await _http.GetStreamAsync(lrclibUrl);
                        using var lrclibDoc = await JsonDocument.ParseAsync(lrclibStream);

                        if (lrclibDoc.RootElement.TryGetProperty("syncedLyrics", out var syn))
                        {
                            lrcText = syn.GetString() ?? "";
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"LRCLIB引擎失败: {ex.Message}"); }
                }

                // ---- 极速解析时间轴 ----
                if (!string.IsNullOrEmpty(lrcText))
                {
                    // 译文先按时间戳建表，随后在解析原文时按时间对齐贴上第二行
                    var transTable = BuildTransTable(transText);
                    int transCursor = 0;
                    // 逐字表同样先建好，随后按行首时间戳 / 文本取用（与译文一个套路）。
                    // 只在扫光总闸开着时才解析：关掉时这份表压根不建，与只有整行时间轴时逐字不差。
                    var wordTable = IsLyricScanEnabled && yrcText.Length > 0
                        ? BuildYrcTable(yrcText, yrcTimingFirst)
                        : Array.Empty<(int StartMs, string Key, LyricWordTiming Timing)>();
                    int wordCursor = 0;
                    var lines = new List<(TimeSpan, string, string)>();
                    // 与 lines 同增同减，所以 wordTimings[i] 天然对应该行
                    List<LyricWordTiming?>? wordTimings = wordTable.Length > 0 ? new List<LyricWordTiming?>() : null;
                    foreach (var line in lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith('[') && line.IndexOf(']') is int idx && idx > 5)
                        {
                            if (TimeSpan.TryParseExact(line.Substring(1, idx - 1), LyricTimeFormats, null, out var ts))
                            {
                                string text = line.Substring(idx + 1).Trim();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    string trans = LookupTrans(transTable, ts.Ticks, ref transCursor);
                                    lines.Add((ts, text, trans));
                                    wordTimings?.Add(LookupWordTiming(wordTable, ref wordCursor, (int)ts.TotalMilliseconds, text));
                                }
                            }
                        }
                    }
                    // 状态锁：请求期间只要这首歌仍是时间轴的归属者就允许写入。
                    // 判据是槽位而不是 Title，这样「切到浏览器又切回来」时
                    // 半路返回的歌词不会被丢掉，正好赶上续播。
                    if (IsLyricOwner(title, artist))
                    {
                        _lyrics = lines.ToArray();
                        _lyricWordTimings = wordTimings?.ToArray();
                        _lyricsHasTranslation = lines.Exists(l => !string.IsNullOrEmpty(l.Item3));
                    }
                }

                // ---- 封面兜底：整条链一个地址都没给出时，走 QQ 直连再试一次 ----
                // 落月的搜索一旦没匹配上，songmid 与封面会一起拿不到（封面地址同样出自那次搜索），
                // 而封面恰恰是最显眼的一项 —— 这里给出一个完全不依赖落月的来源。
                // 视频模式不取网络封面（FetchCoverAsync 会直接返回），所以这里也不白花请求。
                if (string.IsNullOrEmpty(coverUrl) && !IsVideoMode)
                    coverUrl = await FetchQqCoverAsync(title, artist);

                // 统一归一成小尺寸变体（QQ 替换尺寸段 + 网易云补 ?param=）：无论封面最终来自哪一档，
                // 下载量与解码内存都降到约 1/5，把 4 秒超时预算留给真正慢的网络。
                if (!string.IsNullOrEmpty(coverUrl)) coverUrl = NormalizeCoverUrl(coverUrl);

                return coverUrl;
            }
            finally
            {
                // 必须释放锁，让下一首歌可以正常获取
                _fetchLock.Release();
            }
        }

        /// <summary>
        /// 取封面：下载  并换上。地址来自 FetchLyricsAsync
        /// 搜索时顺带命中的专辑图（QQ 给 albummid、网易云给 album.picUrl）。
        ///
        /// 任何一步失败都什么都不做 —— 保持 UpdateCover 已经选好的兜底封面
        /// （该程序的应用图标），绝不清空。
        /// </summary>
        private async Task FetchCoverAsync(string title, string artist, string coverUrl)
        {
            // 视频模式不取网络封面；搜索没命中封面地址时也没什么可取的
            if (IsVideoMode || coverUrl.Length == 0) return;

            // 取词时这首歌是不是已经归属本会话（封面也要挂在同一首歌上）
            if (!IsLyricOwner(title, artist)) return;

            // 会话自带封面已经就位（见 FetchSmtcCoverAsync）⇒ 不再请求网络封面：
            // 那张图就是播放器为本曲目给的原图，比按歌名歌手搜出来的更准，没必要再花一次请求去覆盖它。
            if (string.Equals(_externalCoverTitle, title, StringComparison.Ordinal)
                && string.Equals(_externalCoverAppId, _currentAppId, StringComparison.Ordinal)) return;

            // 刻意不占 _fetchLock：那把锁保护的是四个歌词引擎共用的 DefaultRequestHeaders
            //    （进程级静态字段，改了全局可见）。封面下载是纯 GET，用 HttpRequestMessage 带自己的头，
            //    既不碰共享头、也不需要排队 —— 否则一张 4 秒超时的封面会把下一首歌的取词整整卡住 4 秒。
            string? coverAppId = null;
            SKBitmap? cover = null;
            try
            {
                // 会话在下载前就记下来：下载期间可能换歌，账单必须挂在发起时的那首歌上
                coverAppId = _currentAppId;

                using var request = new HttpRequestMessage(HttpMethod.Get, coverUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", HttpUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var buffer = await ReadLimitedAsync(stream, CoverMaxBytes);
                    if (buffer != null) cover = DecodeCover(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"网络封面获取失败: {ex.Message}");
            }

            // 下载期间可能已经换歌：当场丢掉，别把上一首的封面贴到新歌上
            if (cover == null || !IsLyricOwner(title, artist))
            {
                cover?.Dispose();
                return;
            }

            // 记账必须与「这张图属于哪首曲目」一致：用发起下载时就记下的会话 + 标题，
            // 不能读当时的 _trackAppId/_trackTitle（下载期间可能已经换歌）。
            _externalCoverAppId = coverAppId ?? "";
            _externalCoverTitle = title;
            SetThumbnail(cover);
        }

        /// <summary>
        /// 取会话自带封面（SMTC 缩略图）并换上。对所有会话都优先尝试（本地就有图，不用等网络）：
        /// 这些客户端会把自己正在播放的那张封面通过 SMTC 一并给出，比按歌名 + 歌手去曲库搜更准。
        ///
        /// 拿不到（会话没给缩略图 / 流读不出来 / 解码失败）就什么都不做 ——
        /// 由随后的 FetchCoverAsync 按原链路接管，所以读取失败时封面仍由既有链路提供。
        ///
        /// 成功时同样记入 _externalCoverTitle / _externalCoverAppId，
        /// 于是这首歌不会再发网络封面请求。
        ///
        /// 先等一小段再读（SessionCoverSettleDelay）：属性变化的通知到达时，
        /// 会话的缩略图往往还是上一首的（网易云音乐 / 酷狗实测如此，QQ 音乐同批更新所以没这个问题）。
        /// 当场读会把上一首的封面记到新曲目头上，而记账一旦命中就再也不会纠正。
        ///
        /// 成功时同样记入 _externalCoverTitle / _externalCoverAppId，
        /// 于是这首歌不会再发网络封面请求。发起频率由调用方（UpdateCover）按曲目节流。
        /// </summary>
        private async Task FetchSmtcCoverAsync(string title, string appId)
        {
            // 等缩略图跟上本曲目 —— 原因见 SessionCoverSettleDelay 的注释
            await Task.Delay(SessionCoverSettleDelay);

            // 等待期间换了歌：直接放弃，新曲目会自己再触发一次
            if (!IsSessionCoverOwner(title, appId)) return;

            var cover = await ReadSessionCoverAsync();
            if (cover == null) return;

            // 读流期间又换了歌：当场丢掉，别把上一首的封面贴到新歌上
            if (!IsSessionCoverOwner(title, appId)) { cover.Dispose(); return; }

            _externalCoverTitle = title;
            _externalCoverAppId = appId;
            SetThumbnail(cover);
        }

        /// <summary>
        /// 这个「曲目 + 会话」是不是仍然值得为会话封面记账。判据与封面记账同源
        /// （_trackTitle / _trackAppId）—— 不能用 IsLyricOwner：
        /// 视频模式（浏览器放视频）没有歌词槽位，那会让封面被整批丢掉。
        /// </summary>
        private bool IsSessionCoverOwner(string title, string appId)
            => string.Equals(_trackTitle, title, StringComparison.Ordinal)
               && string.Equals(_trackAppId, appId, StringComparison.Ordinal);

        /// <summary>
        /// 读当前会话的 SMTC 缩略图并解码。没有缩略图 / 解码失败一律返回 null，绝不抛给调用方 ——
        /// 不规范的媒体源会在会话消失的瞬间让这些 COM 调用失败。
        /// </summary>
        private async Task<SKBitmap?> ReadSessionCoverAsync()
        {
            try
            {
                var props = await _currentSession!.TryGetMediaPropertiesAsync();
                if (props?.Thumbnail is not { } thumbRef)
                {
                    // 有些客户端只在首次加载时给缩略图，站内换内容后就不再提供 —— 记一笔便于排查
                    Logger.Debug("会话自带封面：本会话当前未提供缩略图");
                    return null;
                }

                using var stream = await thumbRef.OpenReadAsync();
                using var buffer = await ReadLimitedAsync(stream.AsStreamForRead(), CoverMaxBytes);
                return buffer == null ? null : DecodeCover(buffer);
            }
            catch (Exception ex)
            {
                Logger.Debug($"会话自带封面读取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 带上限地把流读完，超过上限返回 null。调用方负责 Dispose 返回的 MemoryStream。
        ///
        /// 用分块读而不是 CopyToAsync：后者没有任何长度闸门，源站给多大就收多大。
        /// 这里读到超限就立刻掉头，最多也就多收一个 chunk。
        /// </summary>
        private static async Task<MemoryStream?> ReadLimitedAsync(Stream stream, long maxBytes)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        Logger.Debug($"封面体积超过 {maxBytes / 1024 / 1024}MB 上限，已放弃");
                        buffer.Dispose();
                        return null;
                    }
                    buffer.Write(chunk, 0, read);
                }
            }
            catch
            {
                buffer.Dispose();
                throw;   // 交给调用方原有的 catch 记日志
            }

            buffer.Position = 0;
            return buffer;
        }

        /// <summary>
        /// 解码封面并把最长边压到 CoverMaxEdge 以内。解码失败返回 null。
        ///
        /// 缩放这一步是封面链路省内存的关键：源站给的往往是 640 / 1000 见方的原图，
        /// 而它只会被画进 50×50 的方格。按原分辨率长期持有一张 4MB 的位图，
        /// 快速切歌时新旧几张叠在一起就是十几 MB —— 全都花在了根本看不见的像素上。
        /// </summary>
        private static SKBitmap? DecodeCover(Stream buffer)
        {
            SKBitmap? decoded;
            try
            {
                decoded = SKBitmap.Decode(buffer);
            }
            catch (Exception ex)
            {
                Logger.Debug($"封面解码失败: {ex.Message}");
                return null;
            }

            if (decoded == null) return null;

            int maxEdge = Math.Max(decoded.Width, decoded.Height);
            if (maxEdge <= CoverMaxEdge) return decoded;

            float scale = CoverMaxEdge / (float)maxEdge;
            int w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            int h = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            // 目标格式固定 Bgra8888/Premul：源图的 ColorType 可能是 Index8 之类，
            // 照抄过去会得到一张画不出东西的位图。
            var scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(scaled))
                canvas.DrawBitmap(decoded, new SKRect(0, 0, w, h), _coverScalePaint);

            // 原图只是个局部变量、还没交给任何人，这里当场放掉是安全的
            decoded.Dispose();
            return scaled;
        }

        /// <summary>
        /// 网易云的专辑封面地址。搜索接口返回的 album 里只有 picId（不是可直接下载的链接），
        /// 要拿歌曲 id 再请求一次 /api/song/detail 才会给出 album.picUrl。失败返回空串。
        /// </summary>
        private async Task<string> FetchNeteaseCoverAsync(long songId)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", HttpUserAgent);
                _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com");

                // ids 参数是 JSON 数组，方括号必须转义
                using var stream = await _http.GetStreamAsync($"https://music.163.com/api/song/detail?ids=%5B{songId}%5D");
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("songs", out var songs) && songs.GetArrayLength() > 0
                    && songs[0].TryGetProperty("album", out var album)
                    && album.TryGetProperty("picUrl", out var pic))
                    return pic.GetString() ?? "";
            }
            catch (Exception ex)
            {
                Logger.Debug($"网易云封面获取失败: {ex.Message}");
            }
            return "";
        }

        // 落月 API 域名
        private const string LuoYueHost = "https://api.vkeys.cn";

        // QQ 音乐官方歌词接口。它的搜索接口（client_search_cp）已经恒返回 500 挂了，
        // 但本接口仍然可用 —— 前提是有 songmid，而 songmid 由落月搜索提供，所以不需要搜索这一步。
        private const string QQMusicLyricApi = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";

        // QQ 音乐另外两个仍然可用的直连接口（都不需要登录），只服务于「封面兜底」这一条路。
        // 之所以要它们：落月的搜索一旦没匹配上，songmid 与封面会一起拿不到（封面地址也出自那次搜索），
        // 而封面恰恰是最显眼的一项 —— 这两步给出一个完全不依赖落月的来源。
        private const string QQMusicSmartBoxApi = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg";
        private const string QQMusicSingleSongApi = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg";

        /// <summary>QQ 歌词接口返回的正文是 HTML 实体转义的（换行写成 &#10;），统一还原成普通文本。</summary>
        private static string UnescapeQqText(string? raw)
            => raw?.Replace("&#10;", "\n").Replace("&#13;", "\r")
                    .Replace("&#32;", " ").Replace("&#45;", "-")
                    .Replace("&#40;", "(").Replace("&#41;", ")") ?? "";

        /// <summary>落月 API 一次的产出；没命中的项为 null。</summary>
        /// <param name="Lrc">原文 LRC（data.lrc）</param>
        /// <param name="Trans">译文 LRC（data.trans）</param>
        /// <param name="Yrc">逐字歌词（data.yrc）。实测 QQ 侧基本都非空，格式是「文字在前」：`游(66,168)京(234,76)`</param>
        /// <param name="Cover">专辑封面地址（搜索项的 cover，已换成 300×300 变体）</param>
        /// <param name="Mid">QQ 的 songmid（搜索项的 mid），交给 QQMusicLyricApi 用</param>
        private readonly record struct LuoYueResult(string? Lrc, string? Trans, string? Yrc, string? Cover, string? Mid);

        /// <summary>落月「网易云」一次取词的产出；没命中的项为 null。</summary>
        /// <param name="Lrc">原文 LRC（实测字段是 data.lrc，兼容文档里的 data.rc）</param>
        /// <param name="Trans">译文 LRC（data.trans，实测常为空）</param>
        /// <param name="Yrc">逐字歌词（data.yrc）。实测只有部分歌有（孤勇者 / 勾指起誓有，花がら / 起风了 / 晴天 / Lemon 都是空串），
        /// 格式是「时间在前」：`(21680,370,0)你(22050,340,0)是`</param>
        /// <param name="Cover">网易云 CDN 专辑图地址（搜索项的 cover，已换成 300×300 变体）</param>
        private readonly record struct LuoYueNeteaseResult(string? Lrc, string? Trans, string? Yrc, string? Cover);

        /// <summary>
        /// 落月 API：先 /v2/music/tencent/search/song?word= 搜到曲目，再用 /v2/music/tencent/lyric?id=
        /// 取原文（data.lrc）、译文（data.trans）与逐字歌词（data.yrc）；
        /// 搜索响应里顺带拿到专辑封面（cover）与 QQ songmid（mid）。
        /// </summary>
        private async Task<LuoYueResult> FetchFromLuoYueAsync(string title, string artist, long durationSec, string ua)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", ua);

                // 1. 搜索：按「歌名 歌手」搜，结果里歌名与歌手都全字匹配才认
                string word = Uri.EscapeDataString(string.IsNullOrEmpty(artist) ? title : $"{title} {artist}");
                using var searchStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/tencent/search/song?word={word}");
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                    return default;

                // 非中文曲目允许标题包含匹配：这类歌名在 QQ 曲库里常带中译别名，全字匹配会整条落空。
                // 中文曲目一切照旧（按完整歌名基本都能搜到，不需要放宽）。
                long songId = MatchSong(list, title, artist, durationSec, !IsChineseTitle(title), out string? cover, out string? mid);
                if (songId <= 0)
                {
                    // 「没歌词」排查时唯一能依赖的就是这些日志：候选全被「歌名 + 歌手 + 时长」挡下时留一笔
                    Logger.Debug($"落月QQ 搜索没有匹配到候选（{title} / {artist}）");
                    return default;
                }

                // 2. 取歌词。失败也要把封面 / songmid 带回去 —— 三者互不依赖，能拿到一样算一样
                //    （mid 拿得到就还有引擎 2 那条 QQ 官方接口的路可走）。
                using var lyricStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/tencent/lyric?id={songId}");
                using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                var root = lyricDoc.RootElement;

                if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out int code) && code != 200)
                    return new LuoYueResult(null, null, null, cover, mid);
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return new LuoYueResult(null, null, null, cover, mid);

                string lrc = data.TryGetProperty("lrc", out var lrcEl) ? lrcEl.GetString() ?? "" : "";
                string trans = data.TryGetProperty("trans", out var transEl) ? transEl.GetString() ?? "" : "";
                string yrc = ReadJsonString(data, "yrc");
                return new LuoYueResult(
                    string.IsNullOrEmpty(lrc) ? null : lrc,
                    string.IsNullOrEmpty(trans) ? null : trans,
                    string.IsNullOrEmpty(yrc) ? null : yrc,
                    cover,
                    mid);
            }
            catch (Exception ex)
            {
                Logger.Debug($"落月API歌词获取失败: {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// 落月 API - 网易云：先 /v2/music/netease?word= 搜到曲目拿到 id，
        /// 再用 /v2/music/netease/lyric?id= 取原文 / 译文
        /// —— 落月的歌词接口是独立的，搜索响应里并不含歌词，必须分两步。
        /// 搜索响应里会顺带给出网易云 CDN 的专辑图（cover），一并带回去。
        ///
        /// 与 QQ 版的异同：两条路的搜索响应字段完全同构（song / singer /
        /// id / cover），所以候选匹配直接复用 MatchSong 同一套打分；
        /// 但网易云响应里没有时长（time 是发行日期字符串），因此这一档做不了时长校验，
        /// 只能靠「歌名 + 歌手」判定候选。
        ///
        /// 字段名以实测为准：歌词接口返回 data.lrc（部分文档写作 rc）、
        /// data.trans 与 data.yrc（逐字歌词）。原文两个名字都收、按 lrc 优先。
        /// </summary>
        private async Task<LuoYueNeteaseResult> FetchFromLuoYueNeteaseAsync(string title, string artist, string ua)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", ua);

                // 1. 搜索：按「歌名 歌手」搜，复用同一套「宽容匹配」打分选最像的那条
                string word = Uri.EscapeDataString(string.IsNullOrEmpty(artist) ? title : $"{title} {artist}");
                using var searchStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/netease?word={word}");
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                    return default;

                // 搜索结果里顺带给出网易云 CDN 专辑图：网易云会话下它是封面链的第一档（见「网易优先」段），
                // 其他会话下这个值不参与封面（那时封面由引擎 1 / 引擎 4 提供）。
                // 落月「网易云」的搜索响应里没有时长字段 ⇒ 传 0，时长门槛对这一档不生效
                long songId = MatchSong(list, title, artist, 0, false, out string? cover, out _);
                if (songId <= 0)
                {
                    // 落月网易云固定只返回 10 条候选，排位靠后的版本（如《游京》）不在这 10 条里，
                    // 打分全为 0 ⇒ 走到这里。留一笔日志，后续由引擎 4 / 5 兜底。
                    Logger.Debug($"落月网易云 搜索没有匹配到候选（{title} / {artist}）");
                    return default;
                }

                // 2. 取歌词。任何一步失败都直接放弃这一档交给下一个引擎，绝不抛给调用方
                //    （失败也要静默 —— 它是兜底链中的一环，报错只会刷日志）
                using var lyricStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/netease/lyric?id={songId}");
                using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                var root = lyricDoc.RootElement;

                // 取词失败也要把封面带回去 —— 三者互不依赖，能拿到一样算一样（网易云会话下封面是优先档）
                if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out int code) && code != 200)
                    return new LuoYueNeteaseResult(null, null, null, cover);
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return new LuoYueNeteaseResult(null, null, null, cover);

                string lrc = ReadJsonString(data, "lrc");
                if (lrc.Length == 0) lrc = ReadJsonString(data, "rc"); // 兼容文档里的另一种字段名
                string trans = ReadJsonString(data, "trans");
                string yrc = ReadJsonString(data, "yrc");

                return new LuoYueNeteaseResult(
                    string.IsNullOrEmpty(lrc) ? null : lrc,
                    string.IsNullOrEmpty(trans) ? null : trans,
                    string.IsNullOrEmpty(yrc) ? null : yrc,
                    cover);
            }
            catch (Exception ex)
            {
                Logger.Debug($"落月API-网易云歌词获取失败: {ex.Message}");
                return default;
            }
        }

        /// <summary>从 JSON 对象里取一个字符串字段：字段缺失或类型不对一律返回空串，绝不抛。</summary>
        private static string ReadJsonString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

        /// <summary>网易云官方接口一次的产出；没命中的项为空串（default 下是 null，读取一律走 IsNullOrEmpty）。</summary>
        /// <param name="Lrc">原文 LRC（lrc.lyric）</param>
        /// <param name="Trans">译文 LRC（tlyric.lyric）</param>
        /// <param name="Cover">专辑封面地址（album.picUrl，需 allowCover 才取）</param>
        private readonly record struct NeteaseOfficialResult(string Lrc, string Trans, string Cover);

        /// <summary>
        /// 「这一档什么都没拿到」的空结果。
        ///
        /// 刻意不用 `default`：record struct 的 default 会绕过字段初值，三个字段全是 null。
        /// 调用方存在 `coverUrl = xxx.Cover` 这类直赋写法，null 会顺着传染给 coverUrl，
        /// 之后任何一次 `coverUrl.Length` 都是 NullReferenceException —— 一次网易云搜索失败
        /// 就能把整个取词任务炸掉（连带封面链路一起断）。所有失败出口一律返回这个显式空值。
        /// </summary>
        private static readonly NeteaseOfficialResult EmptyNeteaseOfficial = new("", "", "");

        /// <summary>
        /// 网易云官方接口：搜索 → 取词（原文 + 译文）→ 可选取封面。
        ///
        /// 为什么单独抽成一个方法：它在链上有两个调用位置 ——
        /// 网易云音乐会话下被提到最前（「网易优先」段），其他会话下在引擎 4 的位置充当兜底档；
        /// 抽出来是为了让这两条路共用同一份实现。
        ///
        ///  = false 时只取词不取封面：用在「网易优先」段，
        /// 让封面链的第一档始终留给落月的 QQ 搜索（封面来源与优先级不因会话类型而变）。
        /// </summary>
        private async Task<NeteaseOfficialResult> FetchFromNeteaseOfficialAsync(string title, string artist, long durationSec, bool allowCover)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", HttpUserAgent);
                _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com");
                _http.DefaultRequestHeaders.Add("X-Real-IP", $"114.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}");

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("s", $"{title} {artist}"),
                    new KeyValuePair<string, string>("type", "1"),
                    // limit 取 60：网易云搜索对「歌名 + 歌手」组合词的排序并不敏感，冷门 / 翻唱版本
                    // 常被排到 30 位之后（实测《游京》抖音合唱版落在第 30 位之后），取 5~30 都会漏掉它，
                    // 表现就是歌词与封面一起出不来。候选多是安全的：命中还要过
                    // 「歌名 + 歌手 + 时长（±4 秒）」三重校验，非目标版本会被歌手或时长挡掉。
                    new KeyValuePair<string, string>("limit", "60"),
                    new KeyValuePair<string, string>("offset", "0")
                });

                // using：HttpResponseMessage 本身持有内容流与连接租约，只释放它里面的流是不够的
                using var response = await _http.PostAsync("https://music.163.com/api/search/get/web", content);
                using var searchStream = await response.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                long songId = 0;
                // result 不是对象 ⇒ 返回的是一长串加密文本：网易云的反爬把无 cookie 的高频请求拦下了。
                // 单独标记出来，与「正常响应但没有匹配候选」区分开 —— 两者的对策完全不同。
                bool resultIsObject = searchDoc.RootElement.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object;
                if (resultIsObject
                    && result.TryGetProperty("songs", out var songs)
                    && songs.ValueKind == JsonValueKind.Array)
                {
                    // 与落月那档完全同一套两级匹配（理由见 MatchSong 的注释）：时长门槛依赖
                    // 「传入的 durationSec 确实属于这首歌」，而它来自与媒体属性同一次刷新的
                    // SMTC timeline —— 换歌那一拍往往是上一首的时长，会把正确候选整条列表全挡。
                    // 第一遍按时长严格筛；一条都没过（且确实给过时长）时忽略时长再筛一遍，
                    // 但这一遍要求歌名归一化后全等，把「互相包含」的同名不同版本继续挡住。
                    songId = PickNeteaseSongId(songs, title, artist, durationSec, exactNameOnly: false);
                    if (songId <= 0 && durationSec > 0)
                        songId = PickNeteaseSongId(songs, title, artist, 0, exactNameOnly: true);
                }

                if (songId <= 0)
                {
                    // 静默放弃这一档没问题，但必须留下一笔 —— 「没歌词」排查时唯一能依赖的就是这些日志
                    Logger.Debug(resultIsObject
                        ? "网易云官方搜索没有匹配到候选，本档放弃"
                        : "网易云官方搜索被反爬拦截（result 返回的是加密串），本档放弃");
                    return EmptyNeteaseOfficial;
                }

                string lrc = "", trans = "";
                using (var lyricStream = await _http.GetStreamAsync($"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1"))
                using (var lyricDoc = await JsonDocument.ParseAsync(lyricStream))
                {
                    if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrcEl) &&
                        lrcEl.TryGetProperty("lyric", out var lyricStr))
                        lrc = lyricStr.GetString() ?? "";

                    // 网易云译文：独立字段 tlyric，时间戳与原文一一对应
                    if (lyricDoc.RootElement.TryGetProperty("tlyric", out var tl) &&
                        tl.TryGetProperty("lyric", out var tlStr))
                        trans = tlStr.GetString() ?? "";
                }

                // 封面兜底：搜索响应的 album 里只有 picId（不是地址），要拿歌曲 id
                // 再请求一次 song/detail 才有 album.picUrl。
                string cover = allowCover ? await FetchNeteaseCoverAsync(songId) : "";

                return new NeteaseOfficialResult(lrc, trans, cover);
            }
            catch (Exception ex)
            {
                Logger.Warn($"网易云引擎失败: {ex.Message}");
                return EmptyNeteaseOfficial;
            }
        }

        /// <summary>
        /// 在网易云官方搜索的 songs 数组里挑一条，返回它的 id；没有合格的返回 0。
        ///
        /// 分两级由调用方驱动（与 MatchSong 同构，理由也同）：
        ///   exactNameOnly = false —— 原行为：歌名互相包含即可，但时长要卡在 ±4 秒（屏蔽 Live / 伴奏版）；
        ///   exactNameOnly = true  —— 放宽时长后的一遍：歌名归一化后必须全等，
        ///                            这样《海屿你》不会被《海屿你2.0》顶掉（后者只是包含前者）。
        ///
        /// 字段一律 TryGetProperty + 先验 ValueKind：这些字段在真实响应里会缺、甚至类型不对
        /// （实测到过 album 是字符串）。裸 GetProperty 或在非对象元素上调 TryGetProperty 都会抛，
        /// 而异常会被外层 catch 吞成一行 WARN —— 代价却是整个网易云引擎中断，歌词与封面一起没了。
        /// </summary>
        private static long PickNeteaseSongId(JsonElement songs, string title, string artist, long durationSec, bool exactNameOnly)
        {
            string wantTitle = exactNameOnly ? NormalizeToken(title) : "";
            if (exactNameOnly && wantTitle.Length == 0) return 0;

            foreach (var song in songs.EnumerateArray())
            {
                if (song.ValueKind != JsonValueKind.Object) continue;

                string name = song.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string singer = "";
                if (song.TryGetProperty("artists", out var artists)
                    && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0
                    && artists[0].ValueKind == JsonValueKind.Object
                    && artists[0].TryGetProperty("name", out var singerEl))
                    singer = singerEl.GetString() ?? "";

                // 歌名：严格一遍按「互相包含」，放宽一遍按「归一化全等」
                bool titleOk = exactNameOnly
                    ? NormalizeToken(name) == wantTitle
                    : name.Contains(title, StringComparison.OrdinalIgnoreCase)
                      || title.Contains(name, StringComparison.OrdinalIgnoreCase);
                if (!titleOk) continue;

                // 歌手：两遍同一口径（任一侧为空则不因此淘汰，与落月那档一致）
                if (!(string.IsNullOrEmpty(artist)
                      || singer.Contains(artist, StringComparison.OrdinalIgnoreCase)
                      || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!exactNameOnly)
                {
                    // 时长缺失（0）时不做校验：宁可取回搜索结果里的第一条，也别因为缺字段整首歌没歌词
                    long durationMs = song.TryGetProperty("duration", out var durEl) && durEl.TryGetInt64(out long d) ? d : 0;
                    if (!(durationMs <= 0 || durationSec <= 0 || Math.Abs(durationMs / 1000 - durationSec) <= 4))
                        continue;
                }

                if (song.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long id)) return id;
            }
            return 0;
        }

        /// <summary>
        /// 落月搜索结果里最匹配的一条。 带出专辑封面地址、
        ///  带出 QQ 的 songmid；没匹配上返回 0（此时两者都是 null）。
        ///
        /// 为什么要「宽容匹配」而不是全等：全等在真实曲库里命中率偏低，实测两类情况直接落空 ——
        ///   1. 多歌手：落月给 Daoko/米津玄師，播放器上报的歌手却只是 DAOKO（合作曲的常态）；
        ///   2. 标题带后缀：落月给 夜曲 - A35、晴天 (Live)，播放器给的是 夜曲、晴天。
        /// 而一旦落空，歌词与封面会一起拿不到 —— 封面地址同样出自这次搜索。表现就是「有概率获取不到」。
        ///
        /// 规则：两侧先归一化（只留字母 / 数字 / 汉字假名，转小写，去掉空格括号连字符），
        /// 标题要求归一化后全等（分更高）或互相包含；歌手按分隔符拆成多个名字，任一对得上即算过。
        /// 取分数最高的那一条；歌手完全不沾边的不候用，免得挂到翻唱 / 同名曲上。
        ///
        /// allowLooseTitle = true 时，标题在前两档都落空后再试一次
        /// 包含匹配（IsLooseTitleMatch）。它只给非中文曲目开（见 IsChineseTitle）：
        /// 这类歌名在 QQ 曲库里普遍写成「原名 + 中译别名」——
        /// 实测《花がら(Withered Flower)》在库里的条目是《花がら (枯花)》，归一化后
        /// 花がらwitheredflower 与 花がら枯花 互不包含，全字匹配必然落空；
        /// 换更短的搜索词也没用（实测三种搜索词返回的候选完全相同），卡点在打分。
        /// 歌手校验在任何档位都不放宽。
        ///
        /// 两级匹配（见方法内注释）：时长门槛只是第一级。一条都没过、且这次确实拿到了时长
        /// （durationSec &gt; 0）时，会忽略时长再筛一遍，但那一遍只接受标题归一化后全等的候选 ——
        /// 时长门槛原本要防的「互相包含」型同名不同版本（《海屿你》vs《海屿你2.0》）因此仍被挡住。
        /// 之所以必须有第二级：durationSec 来自与媒体属性同一次刷新的 SMTC timeline，
        /// 而 timeline 更新滞后，换歌那一拍往往是上一首的时长，会把正确候选整条列表全挡。
        /// </summary>
        private static long MatchSong(JsonElement list, string title, string artist, long durationSec, bool allowLooseTitle, out string? cover, out string? mid)
        {
            cover = null;
            mid = null;

            string wantTitle = NormalizeToken(title);
            if (wantTitle.Length == 0) return 0;
            var wantArtists = SplitArtists(artist);

            // ---- 两级匹配：时长门槛会误杀，必须有退路 ----
            //
            // 时长门槛要防的是「同名不同版本」（见上），但它依赖一个前提 —— 传入的 durationSec
            // 确实属于这首歌。而调用链里它是与媒体属性同一次刷新读出来的 SMTC timeline：
            // timeline 的更新滞后于 media properties，换歌那一拍拿到的往往是上一首的时长。
            // 一旦如此，本曲目的正确候选会被整条列表全挡（实测《STAY》141s 与《LOVE SCENARIO》
            // 209s 互相套用对方时长时，10/10 条全被挡、零命中），日志只留一行「没有匹配到候选」，
            // 而在外面用同样的搜索词一搜就中 —— 这就是「换歌后必没歌词」的根源。
            //
            // 所以分两级：先按时长严格筛；一条都没过（且确实给过时长）时，忽略时长再筛一遍。
            // 第二级只接受标题归一化后全等的候选（score 的 2 分档），把「互相包含」的
            // 同名不同版本（《海屿你》vs《海屿你2.0》，titleScore 只有 1）继续挡在外面 ——
            // 那正是时长门槛原本要防的一类，放开时长后由标题全等接手，保护不丢。
            string? foundCover = null, foundMid = null;
            long resultId = 0;

            void PickBest(bool strictDuration)
            {
                resultId = 0;
                foundCover = null;
                foundMid = null;
                long id0 = 0;
                int best = 0;

                foreach (var song in list.EnumerateArray())
                {
                    if (song.ValueKind != JsonValueKind.Object) continue; // 数组里混进非对象元素：跳过而不是抛

                    // 时长门槛：候选的 interval（"4分49秒"）与播放器上报的时长比对（±4 秒）。
                    // 歌名有包含关系不等于同一版本 —— 《海屿你》会被《海屿你2.0》顶掉（后者歌名与歌手都包含前者），
                    // 而两者时长相差 118 秒；这一关把它挡在外面，宁可不命中也不要取错版本的歌词。
                    // 拿不到播放器时长、或候选没给 interval 时不校验：宁可宽松，也不因缺字段误杀。
                    if (strictDuration && !DurationMatches(song, durationSec)) continue;

                    string name = song.TryGetProperty("song", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    string singer = song.TryGetProperty("singer", out var singerEl) ? singerEl.GetString() ?? "" : "";

                    int score = ScoreCandidate(wantTitle, wantArtists, NormalizeToken(name), SplitArtists(singer), allowLooseTitle);
                    // 放宽时长这一遍要求标题全等：ScoreCandidate 里只有「归一化后完全相等」才给
                    // titleScore = 2（score ≥ 20）。歌手校验（+1 / +2）与「一侧没给歌手不淘汰」照旧。
                    if (!strictDuration && score < 20) continue;
                    if (score <= best) continue;
                    if (!song.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) continue;

                    best = score;
                    id0 = id;
                    foundCover = song.TryGetProperty("cover", out var coverEl) && coverEl.GetString() is { Length: > 0 } c
                        ? NormalizeCoverUrl(c) : null;
                    foundMid = song.TryGetProperty("mid", out var midEl) && midEl.GetString() is { Length: > 0 } m ? m : null;
                }
                resultId = id0;
            }

            PickBest(strictDuration: true);
            if (resultId <= 0 && durationSec > 0)
                PickBest(strictDuration: false);

            if (resultId <= 0) { cover = null; mid = null; }
            else { cover = foundCover; mid = foundMid; }
            return resultId;
        }

        // 时长门槛的容差（秒）—— 与网易云官方档那处时长校验同一口径。
        private const int DurationToleranceSec = 4;

        /// <summary>
        /// 候选曲目的时长与播放器上报的是否一致。播放器没给时长、或候选没带 interval 时一律放行 ——
        /// 缺字段不该导致误杀，宁可不校验。
        /// </summary>
        private static bool DurationMatches(JsonElement song, long durationSec)
        {
            if (durationSec <= 0) return true;
            if (!song.TryGetProperty("interval", out var el) || el.ValueKind != JsonValueKind.String) return true;

            long seconds = ParseIntervalSeconds(el.GetString());
            if (seconds <= 0) return true;

            return Math.Abs(seconds - durationSec) <= DurationToleranceSec;
        }

        /// <summary>
        /// 解析 QQ 曲库的 interval（「4分49秒」→ 289）。注意同一响应里的 time 是发行日期，不是时长。
        /// 解析不出来返回 0，调用方据此跳过校验。
        /// </summary>
        private static long ParseIntervalSeconds(string? interval)
        {
            if (string.IsNullOrEmpty(interval)) return 0;

            int fen = interval.IndexOf('分');
            int miao = interval.IndexOf('秒');
            if (fen <= 0 || miao <= fen) return 0;

            if (!int.TryParse(interval.AsSpan(0, fen), out int minutes)) return 0;
            if (!int.TryParse(interval.AsSpan(fen + 1, miao - fen - 1), out int seconds)) return 0;

            return minutes * 60L + seconds;
        }

        // 两份歌词判为「同一首歌同一版本」的相似度下限（百分比）。
        private const int LyricsSameSongPercent = 80;

        /// <summary>
        /// 两份歌词是不是同一首歌（同一版本）—— 用于「把另一个曲库的逐字数据借过来」之前的一致性校验。
        /// 逐字时间轴是按行、按文本贴在歌词上的，两份歌词若不是同一版本，借来的逐字会整段错位。
        ///
        /// 口径是「有序 + 容忍插入」：两边先解析成正文行（丢掉时间戳、空白与制作人员 / 版权
        /// 声明那类元数据行），再算最长公共子序列占较长一份的比例。之所以不用「逐行一一比对」：
        /// 两个曲库的元数据行数量与位置都不同（实测 QQ 会多出一行版权声明），硬比会整体错位、
        /// 一致率直接掉到 0；子序列同样要求顺序一致，但能容忍插入 / 删除行。
        /// </summary>
        private static bool LyricsAreSameSong(string a, string b)
        {
            var la = LyricBodyLines(a);
            var lb = LyricBodyLines(b);
            if (la.Length == 0 || lb.Length == 0) return false;

            int max = Math.Max(la.Length, lb.Length);
            // 行数差一倍以上不可能是同一首歌，先挡掉（也省下一趟大数组计算）
            if (Math.Min(la.Length, lb.Length) * 2 < max) return false;

            var prev = new int[lb.Length + 1];
            var cur = new int[lb.Length + 1];
            for (int i = 1; i <= la.Length; i++)
            {
                for (int j = 1; j <= lb.Length; j++)
                    cur[j] = la[i - 1] == lb[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
                (prev, cur) = (cur, prev);
                Array.Clear(cur);
            }

            return prev[lb.Length] * 100 >= LyricsSameSongPercent * max;
        }

        /// <summary>把一份 LRC 拆成「正文行」（去时间戳、去空白、去元数据行），供歌词一致性比对使用。</summary>
        private static string[] LyricBodyLines(string lrc)
        {
            if (string.IsNullOrEmpty(lrc)) return Array.Empty<string>();

            var list = new List<string>();
            foreach (var raw in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length < 6 || raw[0] != '[') continue;
                int idx = raw.IndexOf(']');
                if (idx <= 5) continue;
                if (!TimeSpan.TryParseExact(raw.Substring(1, idx - 1), LyricTimeFormats, null, out _)) continue;

                string text = NormalizeLyricText(raw.Substring(idx + 1));
                if (text.Length == 0 || IsLyricCreditLine(text)) continue;
                list.Add(text);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 「作词: 米果」「小提琴：须磨和声」「（未经著作人许可…）」「歌名 - 歌手」这类行不是歌词正文。
        /// 判定刻意宽松：它只服务于「两份歌词是不是同一首歌」这个对称比对 ——
        /// 两边都被剔掉不影响结论，漏剔一两行也由子序列比对吸收。
        /// </summary>
        private static bool IsLyricCreditLine(string text)
        {
            int colon = text.IndexOfAny([':', '：']);
            if (colon >= 0 && colon <= 8 && text.Length <= 40) return true;   // 制作人员行

            if (text.Contains("未经") || text.Contains("著作权") || text.Contains("不得翻唱")) return true;

            return text.Length <= 32 && text.Contains(" - ");                 // 「歌名 - 歌手」标题行
        }

        /// <summary>候选打分：标题全等 2 分 / 互相包含 1 分（0 分直接淘汰）；歌手另计，最高 2 分。0 表示不候用。
        ///  为 true 时，全等与互相包含都落空的话再试一次包含匹配
        /// （IsLooseTitleMatch），通过同样记 1 分 —— 排序上仍低于「全等」的候选。</summary>
        private static int ScoreCandidate(string wantTitle, HashSet<string> wantArtists, string candTitle, HashSet<string> candArtists, bool allowLooseTitle)
        {
            int titleScore;
            if (candTitle == wantTitle) titleScore = 2;
            else if (candTitle.Length > 0
                     && (candTitle.Contains(wantTitle, StringComparison.Ordinal)
                         || wantTitle.Contains(candTitle, StringComparison.Ordinal))) titleScore = 1;
            else if (allowLooseTitle && IsLooseTitleMatch(candTitle, wantTitle)) titleScore = 1;
            else return 0;

            if (wantArtists.Count == 0 || candArtists.Count == 0) return titleScore * 10 + 1; // 一侧没给歌手：不因此淘汰

            if (wantArtists.Overlaps(candArtists)) return titleScore * 10 + 2;
            return HasPartialArtistOverlap(wantArtists, candArtists) ? titleScore * 10 + 1 : 0;
        }

        /// <summary>
        /// 曲目算不算「中文歌」—— 决定标题匹配落空后能否放宽到包含匹配
        /// （即 MatchSong 与 PickBestMid 的 allowLooseTitle）。
        ///
        /// 判据：歌名里出现汉字、且不含日文假名或韩文，就按中文歌处理。
        /// 中文歌按完整歌名基本都能在 QQ 曲库里搜到，不需要放宽；真正需要的是非中文曲目 ——
        /// 它们的歌名在库里常被写成「原名 + 中译别名」（实测《花がら(Withered Flower)》对应
        /// 《花がら (枯花)》），两侧归一化后互不包含，全字匹配必然落空。
        /// </summary>
        private static bool IsChineseTitle(string title)
        {
            bool hasHan = false;
            foreach (char c in title)
            {
                if (c >= '\u3040' && c <= '\u30FF') return false;  // 平假名 / 片假名 ⇒ 日文
                if (c >= '\uAC00' && c <= '\uD7AF') return false;  // 谚文 ⇒ 韩文
                if (c >= '\u4E00' && c <= '\u9FFF') hasHan = true; // CJK 统一表意文字
            }
            return hasHan;
        }

        // 包含匹配要求的最短公共子串。取 3 是为了稳稳吃掉「原名 + 别名」这类差异
        // （《花がら(Withered Flower)》与《花がら (枯花)》的公共子串正是开头的「花がら」），
        // 又短到不至于把毫无关系的歌拉进来。
        private const int LooseTitleMinCommon = 3;

        // 公共子串还要覆盖较短那个标题的这一比例，避免「短歌名恰好是长歌名前缀」这种巧合。
        private const double LooseTitleCoverage = 0.6;

        /// <summary>
        /// 标题的「包含匹配」：两侧归一化后若存在足够长的公共子串，就当作标题对得上。
        ///
        /// 门槛受两个条件同时约束 —— 公共子串既要不短于 LooseTitleMinCommon，
        /// 又要覆盖较短标题的 LooseTitleCoverage 以上。这是放宽标题写法差异，
        /// 不是放宽「这是不是同一首歌」：歌手校验照旧，且它只在非中文曲目（见 IsChineseTitle）
        /// 与前两档都落空时才启用。
        /// </summary>
        private static bool IsLooseTitleMatch(string candTitle, string wantTitle)
        {
            if (candTitle.Length < LooseTitleMinCommon || wantTitle.Length < LooseTitleMinCommon) return false;

            int shorter = Math.Min(candTitle.Length, wantTitle.Length);
            int need = Math.Max(LooseTitleMinCommon, (int)Math.Ceiling(shorter * LooseTitleCoverage));

            // 最长公共子串。歌名都很短，滚动数组的 O(n·m) 代价可忽略。
            var prev = new int[wantTitle.Length + 1];
            var cur = new int[wantTitle.Length + 1];
            int best = 0;
            for (int i = 1; i <= candTitle.Length; i++)
            {
                for (int j = 1; j <= wantTitle.Length; j++)
                {
                    cur[j] = candTitle[i - 1] == wantTitle[j - 1] ? prev[j - 1] + 1 : 0;
                    if (cur[j] > best) best = cur[j];
                }
                (prev, cur) = (cur, prev);
                Array.Clear(cur, 0, cur.Length);
            }
            return best >= need;
        }

        /// <summary>
        /// 歌手「简称 ↔ 全称」的兜底：任一名字互为包含且都不短，就算对上。
        /// </summary>
        private static bool HasPartialArtistOverlap(HashSet<string> a, HashSet<string> b)
        {
            foreach (string x in a)
            {
                if (x.Length < 2) continue;
                foreach (string y in b)
                {
                    if (y.Length >= 2 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)))
                        return true;
                }
            }
            return false;
        }

        // ---- QQ 直连封面兜底 ----

        /// <summary>
        /// 只走 QQ 音乐自己的两个接口取专辑封面，完全不依赖落月。
        /// 调用点在整条歌词链跑完之后、且一个封面地址都没拿到时。
        ///
        ///   1. smartbox_new.fcg —— QQ 的搜索建议接口。官方主搜索 client_search_cp 已恒 500，
        ///      但这个仍然可用，且正好给出曲名 / 歌手 / songmid，可以直接复用同一套匹配打分；
        ///   2. fcg_play_single_song.fcg?songmid= —— 用 songmid 换回 album.mid（albummid），
        ///      拼成 QQ 专辑图地址（与落月给的 cover 同一个 CDN 格式）。
        ///
        /// 任何一步失败都返回空串，调用方保持原有的兜底封面（程序图标）。
        /// </summary>
        private async Task<string> FetchQqCoverAsync(string title, string artist)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", HttpUserAgent);
                _http.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");

                // 1) 搜索建议：拿到候选曲目及其 songmid
                string word = Uri.EscapeDataString(string.IsNullOrEmpty(artist) ? title : $"{title} {artist}");
                using var searchStream = await _http.GetStreamAsync($"{QQMusicSmartBoxApi}?key={word}&format=json&utf8=1");
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                if (!searchDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("song", out var songBox) || songBox.ValueKind != JsonValueKind.Object
                    || !songBox.TryGetProperty("itemlist", out var items) || items.ValueKind != JsonValueKind.Array)
                    return "";

                string? songMid = PickBestMid(items, title, artist);
                if (songMid == null) return "";

                // 2) 用 songmid 换 albummid
                using var detailStream = await _http.GetStreamAsync($"{QQMusicSingleSongApi}?songmid={songMid}&platform=yqq&format=json");
                using var detailDoc = await JsonDocument.ParseAsync(detailStream);

                if (!detailDoc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array
                    || list.GetArrayLength() == 0 || list[0].ValueKind != JsonValueKind.Object
                    || !list[0].TryGetProperty("album", out var album) || album.ValueKind != JsonValueKind.Object
                    || !album.TryGetProperty("mid", out var albumMidEl))
                    return "";

                string albumMid = albumMidEl.GetString() ?? "";
                return albumMid.Length == 0 ? "" : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg";
            }
            catch (Exception ex)
            {
                Logger.Debug($"QQ 直连封面获取失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>在 smartbox 的歌曲列表里挑最匹配的一条，返回 songmid；没有合格的返回 null。</summary>
        private static string? PickBestMid(JsonElement items, string title, string artist)
        {
            string wantTitle = NormalizeToken(title);
            if (wantTitle.Length == 0) return null;
            var wantArtists = SplitArtists(artist);
            bool allowLooseTitle = !IsChineseTitle(title); // 与落月 QQ 档同一口径（详见 MatchSong）

            string? bestMid = null;
            int bestScore = 0;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string singer = item.TryGetProperty("singer", out var singerEl) ? singerEl.GetString() ?? "" : "";

                int score = ScoreCandidate(wantTitle, wantArtists, NormalizeToken(name), SplitArtists(singer), allowLooseTitle);
                if (score <= bestScore) continue;
                if (!item.TryGetProperty("mid", out var midEl) || midEl.GetString() is not { Length: > 0 } mid) continue;

                bestScore = score;
                bestMid = mid;
            }
            return bestMid;
        }

        /// <summary>
        /// 归一化：只保留字母 / 数字 / 汉字假名谚文，其余（空格、括号、连字符、全角标点…）全部去掉并转小写。
        /// 于是 晴天 (Live) → 晴天live、夜曲 - A35 → 夜曲a35、DAOKO → daoko。
        /// </summary>
        private static string NormalizeToken(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // 歌手串里的分隔符：中英日常见的并列写法都收进来
        private static readonly char[] ArtistSeparators = ['/', '、', ',', '，', '&', '×', ';', '；', '|', '+'];

        /// <summary>把歌手串拆成名字集合并归一化：Daoko/米津玄師 → {daoko, 米津玄師}。</summary>
        private static HashSet<string> SplitArtists(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in s.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string n = NormalizeToken(part);
                if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        /// <summary>
        /// 当前展示会话的原始标题里是否带 B 站网页标题后缀（用于把封面切到会话自带的那张）。
        ///
        /// 判据刻意收成这一个精确串（而不是原来的「含哔哩哔哩 / bilibili」）：后者太宽，
        /// B站客户端、以及歌名里恰好带这几个字的曲目都会被误判成网页视频。
        ///
        /// ⚠️ 现在已无调用点：B 站网页视频的封面也走「本地 SMTC 缩略图优先」这条通用规则，
        /// 那个粘性标志位删掉了，这里保留备用。
        /// </summary>
        private static bool IsBilibiliTitle(string title)
            => title.Contains("_哔哩哔哩_bilibili", StringComparison.Ordinal);

        /// <summary>
        /// 把封面地址归一成小尺寸变体：岛上最大只画 50px，下原图纯属浪费带宽与解码内存
        /// （还直接吃掉 4 秒的 HttpClient 超时预算）。两类 CDN 的写法不同：
        ///
        ///   1. QQ（y.qq.com / y.gtimg.cn）：尺寸段写在文件名里 —— R800x800M000 → R300x300M000
        ///      （180KB → 33KB）；
        ///   2. 网易云（p*.music.126.net）：URL 不带尺寸段，要用 ?param=NyN 查询参数指定 ——
        ///      同一张图原图 502KB、?param=300y300 为 94KB。
        ///
        /// 幂等：归一过的地址再调用一次不会重复追加（QQ 那档已无 R800 段，
        /// 网易云那档已带 param=）。不符合任何一类的地址原样返回。
        /// </summary>
        private static string NormalizeCoverUrl(string url)
        {
            string normalized = url.Replace("R800x800M000", "R300x300M000", StringComparison.Ordinal);

            if (normalized.Contains("music.126.net", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("param=", StringComparison.Ordinal))
                normalized += normalized.Contains('?') ? "&param=300y300" : "?param=300y300";

            return normalized;
        }

        // LRC 时间标签的几种写法（百分秒 / 毫秒 / 十分之一秒 / 帧格式 / 整秒）。
        // 原文时间轴、译文时间轴、以及「这段 LRC 有没有可用时间轴」三处共用同一份，避免格式集合漂移。
        //
        // `mm:ss:ff` 是帧格式（秒与百分秒之间也用冒号）。标准 LRC 不该出现这种写法，
        // 但部分网易云歌词整份正文都是它（《花がら》正文 52 行全为 `[00:24:88]`，只有开头
        // 「作词 / 作曲 / 制作人」那几行是标准的 `[00:24.88]`）。没有这一项时：
        //   ① 正文整段解析不出时间轴，全部被丢弃；
        //   ② `HasTimedLyric` 仍会被同文件里少数标准格式行判为 true ⇒ 后面的兜底引擎不再执行，
        //      岛上只剩「制作人: xxx」一行一直挂到结束。
        private static readonly string[] LyricTimeFormats =
            [@"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss\:ff", @"mm\:ss"];

        /// <summary>
        /// 这段 LRC 里至少有一行能被解析出时间戳吗。
        ///
        /// 引擎链按这个判据短路，而不是 string.IsNullOrEmpty：QQ 音乐在搜得到歌、
        /// 但歌词接口返回纯文本（版权提示、空壳响应）时会给出「非空却一行时间轴都没有」的结果 ——
        /// 只判空的话网易云与 LRCLIB 会被整段跳过，最终歌词是空的，表现就是「有时候没歌词」。
        /// </summary>
        private static bool HasTimedLyric(string lrc)
        {
            if (string.IsNullOrEmpty(lrc)) return false;

            foreach (var line in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('[') && line.IndexOf(']') is int idx && idx > 5
                    && TimeSpan.TryParseExact(line.Substring(1, idx - 1), LyricTimeFormats, null, out _))
                    return true;
            }
            return false;
        }

        // 译文行与原文行的时间戳允许的最大偏差。两个源（QQ 官方 trans / 落月 API）与歌词正文
        // 偶尔会差个几十毫秒（例：[00:44.48] 橡皮擦… vs [00:44.56] 消しゴムが…），
        // 只认精确相等的话这些行会白白丢掉译文；放到 300ms 又不会串到隔壁句（正常行距都是秒级）。
        private const long TransMatchToleranceTicks = 300L * TimeSpan.TicksPerMillisecond;

        // 译文「沿用」窗口：上一句译文距本行不超过这个跨度时，本行继续沿用它的译文。
        // 兜的是「译文与原文行切分不一致」—— 译文把两句并作一句、或整段只给一句翻译时，
        // 被并掉的那些原文行按容差匹配不上，只认 300ms 就会凭空少掉一两句译文。
        // 跨度限制是防止把间奏前的最后一句一直拖到间奏之后。
        private const long TransCarryTicks = 5L * TimeSpan.TicksPerSecond;

        // 把译文 LRC 解析成按时间戳升序的数组，供原文行做「精确命中 → 邻近命中」两级查找。
        // 同一时间戳出现多行时后者覆盖前者，与「多时间标签展开」的语义保持一致。
        //
        // 时间格式与原文解析共用 `LyricTimeFormats`（单一数据源）：各写一份会让格式集合逐渐漂移，
        // 表现为原文解析得出来、译文整段进不了表，也就是「歌词有、翻译全没了」。
        private static (long Ticks, string Text)[] BuildTransTable(string lrc)
        {
            if (string.IsNullOrEmpty(lrc)) return Array.Empty<(long, string)>();

            var list = new List<(long Ticks, string Text)>();
            foreach (var line in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('[') && line.IndexOf(']') is int idx && idx > 5)
                {
                    if (TimeSpan.TryParseExact(line.Substring(1, idx - 1), LyricTimeFormats, null, out var ts))
                    {
                        string text = line.Substring(idx + 1).Trim();
                        if (IsUsableTranslation(text)) list.Add((ts.Ticks, text));
                    }
                }
            }

            list.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
            return list.ToArray();
        }

        // 取某条原文行对应的译文。 由调用方持有并随歌词推进单调后移 ——
        // 歌词是按时间升序解析的，所以线性扫描摊下来是 O(n)，不需要每次二分。
        private static string LookupTrans((long Ticks, string Text)[] table, long ticks, ref int cursor)
        {
            if (table.Length == 0) return "";

            while (cursor < table.Length && table[cursor].Ticks < ticks - TransMatchToleranceTicks) cursor++;
            if (cursor >= table.Length) return "";

            // 精确 / 邻近命中
            if (Math.Abs(table[cursor].Ticks - ticks) <= TransMatchToleranceTicks) return table[cursor].Text;

            // 下一句译文的起点还离得远，而上一句译文距本行不远 ⇒ 本行沿用上一句译文
            //（译文与原文行切分不一致时，被并掉的那些原文行靠这一步才拿得到译文）
            if (cursor > 0 && ticks - table[cursor - 1].Ticks <= TransCarryTicks) return table[cursor - 1].Text;

            return "";
        }

        // ---- 逐字歌词（yrc） ----
        // 落月的两个歌词接口各带一份 yrc（逐字时间轴），两家的片段语法正好相反：
        //
        //   落月 网易云（时间在前）  [21680,3610](21680,370,0)你(22050,340,0)是
        //   落月 QQ    （文字在前）  [66,599]游(66,168)京(234,76)
        //
        // 共同点：行首都是 [起始ms,时长ms]；括号里的时间戳都是绝对值（与行首同一坐标系，
        // 不是相对偏移）；括号里可能是 2 个或 3 个数字（第 3 位是保留位），解析只认前两位。
        // 元数据行（[ti:] / [kana:] / [offset:]、以及整行没有可计时片段的版权行）会被自然跳过。
        //
        // 对齐用文本、不用时间戳。实测两家表现完全不同：
        //   · QQ 的 lrc 与 yrc 行首时间戳完全相等；
        //   · 网易云两者是系统性错位（同一行差 170~650ms，且越到后面越大），
        //     60ms 容差只能命中 6.8%，就是「有逐字数据却几乎用不上」。
        // 而两者文本来自同一份歌词、归一化后逐行相等（忽略空白），所以拿它当主键：
        // 文本是强约束，不会错配到别的句子；时间戳只留一个宽松兜底，应付个别标点不一致的行。

        /// <summary>
        /// 一行的逐字时间轴。时间片相对本行起始，单位毫秒。
        /// CumChars 是「截至该片段结束时的累积有效字符数」——
        /// 扫光据此把「已经唱到第几个字」折算成 0~1 的比例，渲染层因此零改动。
        /// </summary>
        private readonly struct LyricWordTiming
        {
            /// <summary>第 i 个片段结束的时刻（相对本行起始，毫秒），非递减。</summary>
            public readonly int[] EndMs;
            /// <summary>截至第 i 个片段结束时的累积有效字符数，与 EndMs 等长。</summary>
            public readonly int[] CumChars;
            /// <summary>有时间标注的字的总有效字符数（扫光比例的分子上限）。</summary>
            public readonly int TotalChars;
            /// <summary>
            /// 这份时间轴所属的 yrc 行首时刻（毫秒）。EndMs 是相对它算出来的。
            /// 之所以要留这个原点：lrc 行首与 yrc 行首并不总是相等（网易云两侧系统性错位
            /// 170~650ms，且越往后越大），而扫光用的是「当前播放位置 − lrc 行首」。
            /// 折算时必须把两份坐标系对齐，否则网易云这类源会整首偏快或偏慢。
            /// </summary>
            public readonly int LineStartMs;

            public LyricWordTiming(int[] endMs, int[] cumChars, int totalChars, int lineStartMs)
            {
                EndMs = endMs;
                CumChars = cumChars;
                TotalChars = totalChars;
                LineStartMs = lineStartMs;
            }
        }

        // 时间戳兜底允许的最大偏差。只用于「文本对不上」的行（个别标点差异），
        // 所以给得宽松些 —— 正常行距都在 1.7 秒以上，500ms 不会串到隔壁句。
        private const int YrcTimeFallbackMs = 500;

        /// <summary>
        /// 解析 yrc，产出「行首毫秒 + 归一化文本 → 该行逐字时间轴」的表（按行首升序）。
        ///  选片段语法方向（见本节开头的两组样例）——
        /// 由采纳它的那一档按源给出，比逐行猜更稳（歌词正文里出现括号也不会误判）。
        /// 语法不认识 / 整行没有可计时字符：跳过该行，它自然回落整行扫描。
        /// </summary>
        private static (int StartMs, string Key, LyricWordTiming Timing)[] BuildYrcTable(string yrc, bool timingFirst)
        {
            if (string.IsNullOrEmpty(yrc)) return Array.Empty<(int, string, LyricWordTiming)>();

            var table = new List<(int StartMs, string Key, LyricWordTiming Timing)>();

            foreach (var raw in yrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length < 4 || line[0] != '[') continue;
                int headEnd = line.IndexOf(']');
                if (headEnd <= 1) continue;

                // 行首 [起始ms,时长ms,…]：只认第一个数字
                string head = line.Substring(1, headEnd - 1);
                int comma = head.IndexOf(',');
                if (comma > 0) head = head.Substring(0, comma);
                if (!int.TryParse(head, out int lineStart)) continue;

                var endMs = new List<int>();
                var cumChars = new List<int>();
                var text = new StringBuilder(); // 片段文字拼起来 = 整行文本，用来做文本对齐
                int totalChars = 0;

                string body = line.Substring(headEnd + 1);
                int p = 0;
                while (true)
                {
                    int open = body.IndexOf('(', p);
                    if (open < 0) break;
                    int close = body.IndexOf(')', open);
                    if (close < 0) break;

                    // 两种语法只是「文字」与「括号」的先后关系相反 —— 取文字的那一侧不同
                    string seg;
                    if (timingFirst)
                    {
                        // (时间)文字：文字从本 ')' 之后到下一个 '('（或行尾）
                        int next = body.IndexOf('(', close + 1);
                        seg = next < 0 ? body.Substring(close + 1) : body.Substring(close + 1, next - close - 1);
                    }
                    else
                    {
                        // 文字(时间)：文字从上一个 ')' 之后到本 '('
                        seg = body.Substring(p, open - p);
                    }
                    string stamp = body.Substring(open + 1, close - open - 1);
                    p = close + 1;
                    text.Append(seg);

                    var parts = stamp.Split(',');
                    if (parts.Length < 2
                        || !int.TryParse(parts[0], out int wordStart)
                        || !int.TryParse(parts[1], out int wordDuration))
                        continue;

                    int chars = CountEffectiveChars(seg);
                    if (chars <= 0) continue;

                    // 换算成相对行首的结束时刻。0 时长的片段会让它与上一个相等，
                    // 所以这里只保证「非递减」（二分查找不要求严格递增）。
                    int end = wordStart + wordDuration - lineStart;
                    if (endMs.Count > 0 && end < endMs[^1]) end = endMs[^1];

                    totalChars += chars;
                    endMs.Add(end);
                    cumChars.Add(totalChars);
                }

                if (totalChars == 0 || endMs.Count != cumChars.Count) continue;
                table.Add((lineStart, NormalizeLyricText(text.ToString()),
                    new LyricWordTiming(endMs.ToArray(), cumChars.ToArray(), totalChars, lineStart)));
            }

            table.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            return table.ToArray();
        }

        /// <summary>片段覆盖的「有效字符数」—— 空白不计，否则扫光比例会被空格 / 缩进拖偏。</summary>
        private static int CountEffectiveChars(string s)
        {
            int n = 0;
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) n++;
            return n;
        }

        /// <summary>
        /// 歌词文本的归一化形式：只去掉空白，用于 lrc 行与 yrc 行的对齐。
        /// 两边的文本来自同一份歌词，去掉空白后应当逐行相等（实测《花がら》《勾指起誓》均如此）。
        /// </summary>
        private static string NormalizeLyricText(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// 取某条原文行对应的逐字时间轴。
        ///
        /// 先用文本对（忽略空白）， 由调用方持有并单调后移 ——
        /// 歌词的行序与 yrc 的行序一致，命中就把游标推过该条，于是副歌重复出现时
        /// 第二次自然对上第二份逐字数据。文本相等是强约束，不会错配到别的句子。
        ///
        /// 文本对不上时再退回按行首时间戳找（YrcTimeFallbackMs），
        /// 应付个别标点不一致的行。两条都落空就返回 null，该行回落整行扫描。
        /// </summary>
        private static LyricWordTiming? LookupWordTiming(
            (int StartMs, string Key, LyricWordTiming Timing)[] table, ref int cursor, int lineMs, string lineText)
        {
            if (table.Length == 0) return null;

            string key = NormalizeLyricText(lineText);
            for (int j = cursor; j < table.Length; j++)
            {
                if (table[j].Key == key) { cursor = j + 1; return table[j].Timing; }
            }

            // 时间戳兜底：只用于「文本因个别标点差异对不上」的行。
            // 这里额外要求两行的有效字符数一致 —— 扫光比例是「已唱字数 / 逐字表总字数」，
            // 而渲染层拿整行文本宽度去乘它。字数不等（对错了句 / 一方少了标点字数）时，
            // 比例会整体缩放，扫光要么永远到不了头、要么提前走完。
            // 宁可这一行回落整行扫光，也不要拿一份字数对不上的逐字表去驱动。
            // key 已去掉空白，所以它的长度就是有效字符数。
            int lineChars = key.Length;
            for (int j = cursor; j < table.Length && table[j].StartMs <= lineMs + YrcTimeFallbackMs; j++)
            {
                if (Math.Abs(table[j].StartMs - lineMs) <= YrcTimeFallbackMs
                    && table[j].Timing.TotalChars == lineChars)
                {
                    cursor = j + 1;
                    return table[j].Timing;
                }
            }
            return null;
        }

        /// <summary>
        /// 某一行扫光进度（0~1）的唯一出口 —— 逐字歌词与卡拉 OK 合并后的同一条链。
        ///
        ///   1. 逐字优先：本行有字级时间轴时按每个字自己的时值推进（长的音就慢慢走、
        ///      快念段就快速掠过），与 ComputeWordAlignedProgress 的口径一致；
        ///   2. 自动回退整行扫光：逐字效果不可用时，按「本行起点 → 下一行起点」线性插值。
        ///      所谓「逐字不可用」有两种情形 —— 本行没对上字级数据、整首歌拿不到逐字数据
        ///      （只有落月的两个源会带 yrc，网易云侧还只有部分歌有）。
        ///
        /// 为什么是一条链：两条路径产出的是同一个 0~1 标量，
        /// 渲染层的扫光依旧是「整行总宽 × 进度」，不需要知道自己拿到的是哪一种驱动。
        /// 于是「这首歌没有逐字数据」不会退化成不扫光，而只是自动降级成整行均匀扫光。
        ///
        /// position 传的是原始播放位置。本行「从哪一刻开始算它已经上台」由 LineStartRaw 给出，
        /// 而选行那边用的正是同一个值 —— 两处同源，本行上台那一刻进度必然正好是 0，
        /// 让位那一刻也必然已经走满（见 LineStartRaw / LineYieldRaw 的说明）。
        /// </summary>
        private float ComputeScanProgress(int lineIndex, TimeSpan position)
        {
            double window = LineWindowSeconds(lineIndex);
            float progress;

            // ① 逐字优先
            if (HasWordTiming(lineIndex) && _lyricWordTimings![lineIndex] is { } wordTiming)
            {
                // 逐字时间轴的 EndMs 是相对 **yrc 行首** 记的，而 position 是绝对播放位置，
                // 所以折算一律用「position + 用户补偿 − yrc 行首」。
                // 选行用的 LineStartRaw 也是按 yrc 行首 + 同一份补偿推出来的（见那个方法的注释），
                // 于是本行上台那一刻这个表达式正好是 0 —— 前半句不会被跳过、尾巴也不会被截断。
                progress = ComputeWordAlignedProgress(
                    wordTiming,
                    position.TotalMilliseconds + LyricDelayOffset * 1000.0 - wordTiming.LineStartMs);
            }
            else if (window > 0)
            {
                // ② 逐字不可用 → 回退整行均匀扫光（本行已经上台多久 / 本行窗口）
                progress = (float)((position - LineStartRaw(lineIndex)).TotalSeconds / window);
            }
            else
            {
                return 0f;
            }

            // 行尾收口：距「本行让位的那一刻」不足 LyricLineFinishLead 时，把进度线性推向 1。
            //
            // 逐字数据未必覆盖到整行末尾 —— yrc 行尾与 lrc 的下一句行首并不总是对齐，部分源还会
            // 系统性错位上百毫秒。少了这道收口，每句扫光都会在换行那一刻被切掉一截（「吞词」）。
            // 收口只在最后这一小段生效，整行的扫光节奏完全不变，只是保证「换行前一定走满」。
            //
            // 判据用 LineYieldRaw（本行真正会显示到哪一刻）而不是「下一行的 lrc 时间戳」：
            // 下一行带着自己的提前量提前上台，那个提前量会把本行的尾巴再吃掉一截，
            // 用 lrc 时间戳算就会出现「逐字行后面跟着整行 lrc 时，逐字行只扫到 76%」。
            double remain = (LineYieldRaw(lineIndex) - position).TotalSeconds;
            if (remain <= LyricLineFinishLead)
            {
                // 收口不是「走到换行那一刻刚好 1」—— 渲染是 16ms 一帧，最后一帧落在哪儿不确定，
                // 那样常常会停在 95%~99% 就被换行顶掉，看着仍然是「没扫完」。
                // 所以提前 LyricLineFinishEarly 走满，换行前留一段实打实的 100%（约 7 帧）。
                double span = LyricLineFinishLead - LyricLineFinishEarly;
                float forced = (float)((LyricLineFinishLead - remain) / span);
                progress = Math.Max(progress, Math.Clamp(forced, 0f, 1f));
            }

            return Math.Clamp(progress, 0f, 1f);
        }

        /// <summary>
        /// 本行上台的「真实播放位置」—— 选行与扫光共用的唯一时钟原点。两者必须同源：
        /// 不同源就会出现「本行上台时扫光已经走掉半句」（前半句被跳过）或
        /// 「本行还没走满就被下一行顶掉」（尾巴被吞）。
        ///
        /// 提前量的来源分两种，**都不是拍脑袋的常数**：
        ///   · 逐字行（yrc）：直接用数据本身的差 —— yrc 行首就是这一行第一个字真正开口的时刻，
        ///     它普遍比 lrc 行首早（网易云实测 170~650ms，lrc 那一栏填得偏晚）。
        ///     拿这个差当提前量，本行就会在「该开口的那一刻」上台，扫光时钟正好归零。
        ///     ⚠️ 这里曾经写死成 0（理由「字级时间戳就是真实起唱时刻，不该再叠补偿」）——
        ///     那等于把逐字行推迟到 lrc 时刻才上台，而扫光时钟早已走过半个字表，
        ///     表现就是网易云这类 yrc 行首偏早的源「前半句直接跳过、从中段往后扫」。
        ///   · 整行 lrc：那一栏没有字级数据，只能用固定提前量 0.6s 近似。
        ///
        /// 用户自己的「歌词延迟补偿」两类行都照常叠加（显式旋钮，不该被自动提前量连坐）。
        /// </summary>
        private TimeSpan LineStartRaw(int lineIndex)
            => _lyrics[lineIndex].Time - TimeSpan.FromSeconds(LineLeadSeconds(lineIndex));

        /// <summary>本行被下一行顶掉的「真实播放位置」（末行没有下一句可依，按 LyricLastLineSeconds 估）。</summary>
        private TimeSpan LineYieldRaw(int lineIndex)
            => lineIndex < _lyrics.Length - 1
                ? LineStartRaw(lineIndex + 1)
                : LineStartRaw(lineIndex) + TimeSpan.FromSeconds(LyricLastLineSeconds);

        /// <summary>本行的提前量（秒，正 = 提前上台）：逐字行走数据差，整行走固定 0.6s，再叠用户补偿并封顶。</summary>
        private double LineLeadSeconds(int lineIndex)
        {
            double lead = 0.6;   // 整行 lrc：那一栏没有字级数据，只能用固定提前量近似
            if (HasWordTiming(lineIndex) && _lyricWordTimings![lineIndex] is { } wordTiming)
            {
                // yrc 行首比 lrc 行首早多少，本行就该早多少上台。
                // 反过来（yrc 比 lrc 晚）不反着推迟 —— 那只会让这一行比 lrc 时间还晚出现，没有意义。
                lead = Math.Max(0,
                    (_lyrics[lineIndex].Time - TimeSpan.FromMilliseconds(wordTiming.LineStartMs)).TotalSeconds);
            }

            return ClampLead(lead + LyricDelayOffset, lineIndex);
        }

        /// <summary>
        /// 提前量的统一封顶 —— 只用两条**天然边界**，不用含糊的经验常数：
        ///
        ///   1. lead_i ≤ t_i：上台时刻不可能早于曲目开头（StartRaw 不能是负数）。
        ///   2. lead_i ≤ t_i − t_{i−1}：上台时刻不可能早于上一行的上台时刻。
        ///      少了这一条，副歌前的短促垫句 / 说唱快句（相邻两句只隔零点几秒）里，
        ///      后一行会把前一行整个盖掉 —— 那一句一帧都不会出现。
        ///
        /// 为什么不额外压一个小上限（比如 0.6s）来「求稳」：提前量本身就是「lrc 行首比
        /// 真实起唱晚多少」的度量，网易云这类源会晚到 1 秒以上。按下限压掉它，
        /// 就等于把这一行推迟到 lrc 时刻才上台，而扫光时钟早已走过半句 ——
        /// 前半句直接跳过。宁可让这一行早一点上台（它本来就是从这一刻开始唱的），
        /// 也不能让扫光从中段开始。
        /// </summary>
        private double ClampLead(double lead, int lineIndex)
        {
            double t = _lyrics[lineIndex].Time.TotalSeconds;
            double prevWindow = lineIndex > 0
                ? t - _lyrics[lineIndex - 1].Time.TotalSeconds
                : double.MaxValue;

            double cap = Math.Min(t, prevWindow);
            if (cap < 0) cap = 0;
            if (Math.Abs(lead) > cap) lead = Math.Sign(lead) * cap;
            return lead;
        }

        /// <summary>本行的窗口长度（本行起点 → 下一行起点；末行没有下一句可依，按 LyricLastLineSeconds 估）。</summary>
        private double LineWindowSeconds(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _lyrics.Length) return 0;
            TimeSpan start = _lyrics[lineIndex].Time;
            TimeSpan end = lineIndex < _lyrics.Length - 1
                ? _lyrics[lineIndex + 1].Time
                : start + TimeSpan.FromSeconds(LyricLastLineSeconds);
            return (end - start).TotalSeconds;
        }

        /// <summary>本行有没有可用的逐字（yrc）时间轴。</summary>
        private bool HasWordTiming(int lineIndex)
            => _lyricWordTimings is { Length: > 0 } timings
               && lineIndex >= 0 && lineIndex < timings.Length
               && timings[lineIndex] is { TotalChars: > 0 };

        /// <summary>末行没有下一句可依，按这个时长估算它的窗口。</summary>
        private const double LyricLastLineSeconds = 4.0;

        /// <summary>行尾收口时长：距「本行让位」不足它时开始把扫光推向 1（见 ComputeScanProgress）。</summary>
        private const double LyricLineFinishLead = 0.35;

        /// <summary>行尾收口的提前量：扫光在这一小段之前就走满，换行前保证有一段 100% 的实拍。</summary>
        private const double LyricLineFinishEarly = 0.12;

        /// <summary>
        /// 按逐字时间轴把「本行已经唱到哪」折算成 0~1 的扫光比例。仅在逐字数据可用时被
        /// ComputeScanProgress 调用；不可用时由那条链回退到整行扫光。
        ///
        /// 口径是「字符数」而不是像素宽度：中文与日文基本等宽，折算误差肉眼不可见；
        /// 好处是渲染层零改动 —— 它拿到的仍然只是一个 0~1 的标量，扫光依旧是「整行总宽 × 比例」。
        /// 含大量拉丁字母 / 空格的行会有几像素偏差（总字数按非空白字符计，渲染宽度却含空格），
        /// 这是已知取舍；中日文歌不受影响。
        /// </summary>
        private static float ComputeWordAlignedProgress(LyricWordTiming timing, double elapsedMs)
        {
            int[] ends = timing.EndMs;
            int n = ends.Length;
            if (n == 0 || timing.TotalChars <= 0) return 0f;
            // 最后一段唱完到本行结束之间通常是间奏：进度钉在 1（整行已唱完），
            // 与主流卡拉 OK 一致；等到下一行时间戳到了自然换行。
            if (elapsedMs >= ends[n - 1]) return 1f;
            if (elapsedMs <= 0) return 0f;

            // 二分：最后一个 ends[i] <= elapsed 的下标 + 1 = 已完成的片段数
            int lo = 0, hi = n - 1, done = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (ends[mid] <= elapsedMs) { done = mid + 1; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (done >= n) return 1f;

            int doneChars = done > 0 ? timing.CumChars[done - 1] : 0;
            int spanStart = done > 0 ? ends[done - 1] : 0;
            int spanEnd = ends[done];
            float frac = spanEnd > spanStart ? (float)((elapsedMs - spanStart) / (spanEnd - spanStart)) : 0f;
            int spanChars = timing.CumChars[done] - doneChars;

            return Math.Clamp((doneChars + frac * spanChars) / timing.TotalChars, 0f, 1f);
        }

        // 译文里的占位符与版权声明不该被当成歌词显示：
        // `//`、`/`、`…` 这类整行只有符号的占位，以及 QQ 音乐那句「享有本翻译作品的著作权」，
        // 统一按「这句没有译文」处理 —— 少了这层过滤，间奏行会变成一堆「//」。
        private static bool IsUsableTranslation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Contains("翻译作品") || text.Contains("本译文")) return false;

            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) return true; // 中日韩文字 / 拉丁字母 / 数字都算有效内容

            return false;
        }

        // 被底层渲染循环以 60FPS 极速调用，彻底无视流氓播放器的限制
        public void UpdateLyrics()
        {
            var now = DateTime.UtcNow;
            var dt = now - _lastUpdateTime;
            _lastUpdateTime = now; // 无论是否在播放，每一帧都更新绝对时间差

            // 封面兜底重试。
            // 图标解析在「刚接管会话」那一刻很容易瞬时失败（播放器进程还没就绪 / 时序），
            // 而会驱动 UpdateCover 的 MediaPropertiesChanged 只在元数据变化时触发 ——
            // 播放期间通常一次都不会再来。结果就是：播放时一直没有图标，一按暂停（播放状态变化
            // 触发一次属性刷新）反而冒出来了 —— 用户实测到的正是这个现象。
            // 所以借渲染循环这个稳定时钟补一次重试；真正的解析由 AppIconProvider 自带退避节流，
            // 会话自带封面的读取由 UpdateCover 按曲目节流，未命中时这里只是几次字符串比较。
            //
            // 触发条件除了「没有任何封面」，还有「封面还挂在别的曲目上」—— 换歌到新封面到位之间
            // 就是这种过渡态（会话自带封面优先的平台上必然出现），此时也需要继续把接力棒往下传。
            // 触发条件除了「没有任何封面」，还有两条：
            //   · 封面还挂在别的曲目上 —— 换歌到新封面到位之间就是这种过渡态，要把接力棒往下传；
            //   · 屏上当前是应用图标（_appIconKey 非空）—— 例如切到无歌手的会话时被顶成了图标，
            //     切回来要把本会话的外部封面重新接力回来，否则记账命中就永远停在图标上了。
            if (Thumbnail == null
                || _appIconKey.Length > 0
                || !string.Equals(_externalCoverTitle, _trackTitle, StringComparison.Ordinal)
                || !string.Equals(_externalCoverAppId, _trackAppId, StringComparison.Ordinal))
                UpdateCover(true);

            // 标题未就绪的兜底重试（同上，共用渲染循环这个稳定时钟）：
            // PotPlayer 播 SMB / 网络文件时 SMTC 先报自己的应用名，加载完才换成文件名，
            // 而那一次替换多半不发属性变更事件 —— 不补重试就会一直卡在应用名上（详见 TitleRetryMax）。
            RetryTitleIfNeeded(now);

            // 取词失败的补偿重试（与上面封面兜底共用渲染循环这个稳定时钟）。
            // 为什么非挂这里不可：RefreshPropertiesCore 只在「标题/歌手变了」时触发取词，
            // 取词失败不会改变标题 —— 没有这条补偿，一次失败就是这首歌永久没歌词。
            RetryLyricsIfNeeded();

            // 被切走的那首歌若还在后台播放，继续替它推算进度（内部按 1 秒节流，无挂起时立即返回）
            AdvanceSuspendedTimeline(now);

            // 无会话：不显示歌词，也不保留时间轴
            if (_currentSession == null)
            {
                SetLyric("", "", 0f, false);
                if (!_isDragging) { HasTimeline = false; Duration = TimeSpan.Zero; _timelinePos = TimeSpan.Zero; }
                return;
            }

            // Just Solo LyricServer 直连歌词优先（只有当前显示 justsolo 时才会处于连接状态）
            // 译文来自协议里的 translation 字段，由客户端负责上下两行分开画
            if (_justSoloLyric.TryGetCurrentLyric(LyricDelayOffset, out string soloText, out string soloTrans, out float soloProgress))
            {
                SetLyric(soloText, soloTrans, soloProgress, _justSoloLyric.HasTranslation);
                return;
            }

            // 时间轴快照已由后台采样时钟取好（见 SampleSmtcTick）：渲染线程一次 COM 都不打，
            // 否则换歌瞬间那些会阻塞的 SMTC 调用会把整块岛体一起冻住。
            // 这里只判断「后台是否刚采到一份新快照」—— 纠偏与跳变判定只在那一帧做，
            // 因为采样是 200ms 一次、渲染是 16ms 一次，每帧都按同一份快照纠偏会把纠偏量放大十几倍。
            int sampleVersion = System.Threading.Volatile.Read(ref _smtcSampleVersion);
            bool sampledNow = sampleVersion != _consumedSampleVersion;
            _consumedSampleVersion = sampleVersion;

            // 「检测到 SMTC 提供歌曲进度」= 端到端时长有效，不区分具体平台
            HasTimeline = _smtcDuration > TimeSpan.Zero;
            Duration = _smtcDuration;
            // 时间文本在推进之前刷新：「已播放 mm:ss」按整秒缓存，早一帧算只会让秒数最多晚 16ms
            // 跳变（肉眼不可见），却能让 Duration 本帧就被进度条读到。放在 AdvanceTimeline 之后
            // 反而要重排这一行的调用位置，得不偿失。
            UpdateTimelineTexts();

            // 浏览器视频 / PotPlayer 这类无歌词会话、以及尚未接管歌词的歌：不显示歌词，
            // 但时间轴照常走 —— 只要 SMTC 给出进度就显示。
            if (IsNonLyricSession || _lyricSlot < 0)
            {
                SetLyric("", "", 0f, false);
                AdvanceFreeTimeline(_smtcPos, dt, now);
                _forceResync = false; // 无歌词槽位：强制对齐标记不适用，就地消费，避免每帧重复采样
                return;
            }

            // 歌词归属校验：槽位里的歌必须与当前会话正在放的歌完全一致。
            // UpdateSession / RefreshProperties 都跑在异步线程上，渲染线程完全可能正好夹在
            // 「会话已换、歌词还没换」的缝隙里 —— 那一瞬间上一首的歌词会被新会话的时间轴推着继续走，
            // 这就是切歌残留的根源。这里每帧做一次本地比对（多数情况下是同一实例的短路比较），
            // 只要对不上就当场清空：宁可空一帧，也不让上一首的歌词多留一帧。
            //
            // 三个比较项各取一次本地快照：三者在异步线程上是分几次写入的（_currentAppId 最先，
            // Title / Artist 随后），逐项直接读字段可能让这一帧混用「新旧两首歌」的值
            // ——那会得出「匹配」的错误结论，反而是残留歌词唯一的漏网窗口。先快照再比，至少保证
            // 同一帧内三项来自同一份采样。
            string ownerTitle = _recentSongs[_lyricSlot].Title;
            string ownerArtist = _recentSongs[_lyricSlot].Artist;
            string ownerAppId = _recentSongs[_lyricSlot].AppId;
            if (ownerTitle != Title
                || ownerArtist != Artist
                || ownerAppId != _currentAppId)
            {
                SetLyric("", "", 0f, false);
                return;
            }

            // 自己接管进度！不管有没有拿到歌词，底层的时间轴必须一直跟着播放状态往前走！
            AdvanceTimeline(HasTimeline, _smtcPos, sampledNow, dt, now);

            // 只有等时间轴正确走完后，如果还没歌词，我们再退出渲染拦截
            if (_lyrics.Length == 0) { SetLyric("", "", 0f, false); return; }

            string found = "";
            string foundTrans = "";
            float progress = 0f;
            // 选行用的时刻必须与本行扫光用的时钟完全一致 —— 都走 LineStartRaw。
            //
            // 原实现是两套时钟：选行用「位置 + 0.6s + 用户补偿」，逐字扫光却用原始位置，
            // 于是每一句的扫光都在走满之前就被下一行顶掉了 0.6 秒的尾巴（「吞词」）。
            // 而把逐字行的提前量改成写死 0 又是另一个极端：网易云这类源的 yrc 行首比 lrc 行首
            // 早约 0.5s，等到 lrc 时刻才上台时扫光时钟早已走过半个字表 ——
            // 表现就是「前半句直接跳过、从中段往后扫」。所以提前量只能由数据本身给出。
            TimeSpan rawPosition = _recentSongs[_lyricSlot].Position;
            for (int i = _lyrics.Length - 1; i >= 0; i--)
            {
                if (rawPosition >= LineStartRaw(i))
                {
                    found = _lyrics[i].Text;
                    foundTrans = _lyrics[i].Translation;
                    // 本句的扫光进度：逐字优先，逐字不可用则自动回退整行扫光（见 ComputeScanProgress）。
                    progress = ComputeScanProgress(i, rawPosition);
                    break;
                }
            }

            // 输出结果，供渲染层使用
            SetLyric(found, foundTrans, progress, _lyricsHasTranslation);
        }

        /// <summary>
        /// 原文 / 译文 / 进度三件套的唯一出口。原文与译文必须同生同灭 ——
        /// 只清原文不清译文的话，渲染层会把上一句的译文贴到「歌手 - 歌名」下方接着显示。
        /// </summary>
        private void SetLyric(string text, string translation, float progress, bool hasTranslation)
        {
            if (!IsLyricsEnabled)
            {
                CurrentLyric = "";
                CurrentLyricTranslation = "";
                CurrentLyricProgress = 0f;
                HasLyricTranslation = false;
                return;
            }

            CurrentLyric = text;
            CurrentLyricTranslation = translation;
            CurrentLyricProgress = progress;
            HasLyricTranslation = hasTranslation;
        }

        // 取一首歌的槽位（缓冲里没有就新建一个）。优先选空闲槽位，跳过当前歌词槽与后台仍在推算的槽位，
        // 保证「当前歌」与「后台还在放的歌」不会被环形覆盖挤掉；四个槽全满时退化为强占游标槽，
        // 保证换歌永远有槽可用（否则歌词会直接消失）。
        private int SlotFor(string title, string artist)
        {
            for (int i = 0; i < RecentSongSlots; i++)
                if (_recentSongs[i].Title == title && _recentSongs[i].Artist == artist) return i;

            int victim = -1;
            for (int n = 0; n < RecentSongSlots; n++)
            {
                int i = _recentCursor;
                _recentCursor = (_recentCursor + 1) % RecentSongSlots;
                if (i == _lyricSlot) continue;
                if (_slotSessions[i] == null) { victim = i; break; }
                if (victim < 0) victim = i;
            }
            if (victim < 0) return -1;

            _recentSongs[victim] = (title, artist, TimeSpan.Zero, DateTime.UtcNow, "");
            _slotSessions[victim] = null;
            return victim;
        }

        // 这个槽位的进度是不是「刚刚还在被推算」——只有这种进度才可信、可以拿来续播
        private static bool IsFreshSlot((string Title, string Artist, TimeSpan Position, DateTime TickedAt, string AppId) slot)
            => (DateTime.UtcNow - slot.TickedAt).TotalSeconds < FreshSlotSeconds;

        // 这首歌是否仍是当前歌词时间轴的归属者
        private bool IsLyricOwner(string title, string artist)
            => _lyricSlot >= 0 && _recentSongs[_lyricSlot].Title == title && _recentSongs[_lyricSlot].Artist == artist;

        // 这首歌回到台前时撤销后台登记：时间轴交回当前会话推进，
        // 否则同一首歌会被「当前会话」和「后台会话」各累加一次。
        private void ReleaseSuspension()
        {
            if (_lyricSlot >= 0) _slotSessions[_lyricSlot] = null;
        }

        // ---- 位置推进/纠偏常数（见 AdvanceTimeline 的说明） ----
        private const double TimelineJumpSeconds = 1.5;        // 与本地位置的差超过它 → 当作真实跳变，直接对齐
        private const double TimelineNudgeDeadZoneSec = 0.02;  // 落后小于它就不动，省掉无意义的微调
        private const double TimelineNudgeRatio = 0.25;        // 每次采样吃掉 25% 的落后量
        private const double TimelineNudgeMaxStepSec = 0.08;   // 单次追赶上限
        private const double TimelineSeekBackSeconds = 0.5;    // SMTC 自己往回走超过它 → 判定为用户往回 seek
        private const double TimelineAheadDeadZoneSec = 0.08;  // 领先超过它 → 接下来走慢一点把偏差追平
        private const double TimelineSlowRate = 0.8;           // 领先时每帧只推进 80% 的时间
        // 降速安全阀阈值：领先量已经超过它、且还在继续扩大 ⇒ 播放器上报的位置根本没在推进，
        // 放弃降速、恢复实时推进（否则会无限累积落后，见 AdvanceTimeline 里的说明）。
        private const double TimelineStallAheadSeconds = 1.2;

        // 「领先降速」的适用上限：领先量超过它就认为「不是本地跑快了，而是播放器上报得晚」，
        // 不再降速 —— 否则本地时钟（歌词与进度条的唯一时基）会一直落在人声后面。
        private const double TimelineAheadMaxLeadSeconds = 0.6;

        /// <summary>
        /// 把本帧的 SMTC 快照外推成「此刻的真实 SMTC 位置」。
        ///
        /// 原理见字段声明处：SMTC 给的是 (LastUpdatedTime, Position) 这样一对「某时刻的位置」，
        /// 实时值要用 `Position + (now - LastUpdatedTime)` 算出来。播放中才加这段外推量 ——
        /// 暂停时位置本来就不走，直接返回 Position 才对，否则会算出「暂停了进度还在涨」。
        ///
        /// 返回 false 表示无法外推（播放器不上报 LastUpdatedTime），调用方应回退到虚拟跑表。
        /// 时钟回拨 / 跨时区等异常（外推量为负）一律判为不可用 —— 宁可退回虚拟跑表，
        /// 也不要拿一个倒退的位置去纠偏（那会把进度条往回拽）。
        /// </summary>
        private bool TryGetSmtcLivePosition(DateTime now, out TimeSpan live)
        {
            live = _smtcPos;
            if (!_smtcHasLastUpdated) return false;

            if (!IsPlaying) return true;   // 暂停：位置冻结在快照那一刻，直接用 Position

            double elapsed = (now - _smtcLastUpdatedUtc).TotalSeconds;
            if (elapsed < 0) return false;                  // 时钟回拨：不可信
            if (elapsed > SmtcMaxExtrapolationSec) return false; // 快照太旧：外推会被放大成跳变

            live = _smtcPos + TimeSpan.FromSeconds(elapsed);

            // 外推不得超过曲目总长（避免片尾把进度条推出界）
            if (_smtcDuration > TimeSpan.Zero && live > _smtcDuration) live = _smtcDuration;
            return true;
        }

        /// <summary>SMTC 快照最大外推时长：超过它就认为这份快照已经失效，退回虚拟跑表。</summary>
        private const double SmtcMaxExtrapolationSec = 10.0;

        /// <summary>
        /// 推进当前歌词歌的时间轴。
        ///
        /// 两级真源，优先级从高到低：
        /// 1. 真实 SMTC 时间轴（LastUpdatedTime 可用）→ 直接用外推出的 SMTC 位置，
        ///    本地不做任何自由跑表 / 纠偏（见 TryGetSmtcLivePosition）。
        /// 2. 虚拟时间轴（SMTC 只给了进度没给时间戳）→ 按播放状态自行累加，有 Position 时顺带纠偏。
        ///
        /// SMTC 采样已由 UpdateLyrics 统一完成（每帧最多一次），这里只做纯计算。
        /// </summary>
        /// <param name="newSample">
        /// 本帧是否刚采到一份新的 SMTC 快照。纠偏与跳变判定只在拿到新快照的那一帧做 ——
        /// 采样是 200ms 一次、渲染是 16ms 一次，若每帧都按同一份快照纠偏，纠偏量会被放大十几倍。
        /// </param>
        private void AdvanceTimeline(bool hasTimeline, TimeSpan smtcPos, bool newSample, TimeSpan dt, DateTime now)
        {
            // 快照槽位：异步线程可能在本方法执行期间换掉 _lyricSlot，逐次读取会写串槽位。
            int slot = _lyricSlot;
            if (slot < 0 || _isDragging) return; // 状态锁：拖动期间禁止上游写入与自动推进

            // 拖动静默期内同样不能外推：播放器执行 seek 的几十~几百毫秒里，
            // LastUpdatedTime 与 Position 都还是旧值。照算的话这段窗口里位置会自己往上涨，
            // 刚拖到的落点会被拽走一段（拖动后进度条「弹一下」）。静默期让 _timelinePos 停在
            // 落点上不动，等播放器把新位置报上来（外推量归零）再自然接上。
            bool settling = now < _seekSettleUntil;

            // ---- 第一级：真实 SMTC 时间轴（外推） ----
            // 只要 SMTC 能给出 (LastUpdatedTime, Position)，位置就完全由它决定 ——
            // 本地既不自由跑表也不纠偏，那两套机制正是「进度和真实 SMTC 对不上」的来源。
            //
            // 闸门是 TryGetSmtcLivePosition 本身（它内部检查 _smtcHasLastUpdated），
            // 而不是 hasTimeline（那个要求 EndTime > 0）。
            // 单向时间轴 = 有 Position + LastUpdatedTime，不要求有总时长：
            // 有播放器只给位置不给总长（进度条画不出来，但「已播放 mm:ss」照样能显示真实值），
            // 这种情况也必须走真实 SMTC，不能因为 hasTimeline=false 就退回虚拟跑表。
            if (!settling && TryGetSmtcLivePosition(now, out TimeSpan live))
            {
                _recentSongs[slot].Position = live;
                _timelineAhead = false;   // 不再需要「领先降速」那套补偿
                _recentSongs[slot].TickedAt = now;
                _timelinePos = live;
                _forceResync = false;
                return;
            }

            // ---- 第二级：虚拟时间轴（SMTC 时间戳不可用） ----
            // 播放器没给端到端时间轴（网易云 / 酷狗等 EndTime 恒为 0）⇒ 纠偏块整段不执行，
            // `_timelineAhead` 就没有任何机会被复位。此时必须主动清掉：它是「上一首 / 上一个播放器」
            // 留下的状态，背着它会让本首的时间轴全程按 0.8 倍速走（每秒落后 0.2s，见下面的安全阀说明）。
            if (!hasTimeline) _timelineAhead = false;

            // 推进：正常按实时走；一旦发现本地领先 SMTC，就按慢速走。
            //
            // 纠偏只能靠「少走一点」，不能靠「往回退一点」 —— 这是卡拉 OK 不再来回滚的关键：
            //    往回退是在单帧里退掉几十毫秒，比这一帧本来要前进的 16ms 还多，所以那一帧
            //    看起来就是「退回去了」；下几帧再靠累加追回来，于是永远在「滚一点退一点」。
            //    改成降速后位置单调不减，偏差由播放器自己追上，肉眼完全看不出来。
            if (IsPlaying)
            {
                double rate = _timelineAhead ? TimelineSlowRate : 1.0;
                _recentSongs[slot].Position += TimeSpan.FromSeconds(dt.TotalSeconds * rate);
            }

            if (hasTimeline && newSample)
            {
                if (_forceResync) _hasPrevSmtcPos = false; // 换歌：不拿上一首的位置当基准

                // 误差和本地当前位置比，不能和「上一次对齐点」比 —— 对齐点只在跳变时才更新，
                // 拿它当基准时偏差会一直攒着，攒过阈值就一次性跳过去（能到秒级）。
                double delta = (smtcPos - _recentSongs[slot].Position).TotalSeconds;

                // 只有 SMTC 自己往回走了，才是用户把进度往回拖了。
                bool smtcWentBack = _hasPrevSmtcPos
                    && smtcPos < _prevSmtcPos - TimeSpan.FromSeconds(TimelineSeekBackSeconds);
                _prevSmtcPos = smtcPos;
                _hasPrevSmtcPos = true;
                // settling 已在方法开头算好，本处沿用（拖动刚松手时播放器还没执行完 seek）

                if (_forceResync || delta > TimelineJumpSeconds || (!settling && smtcWentBack))
                {
                    // 换歌 / 播放器往前跳 / 用户往回拖 —— 这三种才是该硬对齐的
                    _recentSongs[slot].Position = smtcPos;
                    _timelineAhead = false;
                }
                else if (delta > TimelineNudgeDeadZoneSec)
                {
                    // 落后 → 往前追（单次封顶，免得一次追太多看着像跳）
                    _recentSongs[slot].Position += TimeSpan.FromSeconds(
                        Math.Min(delta * TimelineNudgeRatio, TimelineNudgeMaxStepSec));
                    _timelineAhead = false;
                }
                else
                {
                    // 领先 → 只记标记，交给上面那趟「慢速推进」慢慢追平，绝不往回退
                    _timelineAhead = delta < -TimelineAheadDeadZoneSec;

                    // 降速安全阀（修「歌词越来越慢」）：降速只有在播放器上报的位置
                    //    确实在往上追时才有意义 —— 领先量必须被一口口吃掉。
                    //    若领先量不但没缩小、反而继续扩大，说明它根本没在推进（位置卡住 /
                    //    只在特定事件才刷新），它永远追不上来，降速就成了无底洞：
                    //    每秒只走 0.8×dt ⇒ 每分钟落后 12 秒，单调累积，且块内没有任何分支能把它复位
                    //    （硬对齐要求 delta > +1.5，往回退又被刻意禁止）。
                    //    这时放弃降速、恢复按实时推进；位置仍单调不减，不会出现卡拉 OK 回退。
                    if (_timelineAhead && delta < _prevDelta && delta < -TimelineStallAheadSeconds)
                        _timelineAhead = false;

                    // 降速只用来吃掉「本地采样间隙里多跑出去的那一点点」——
                    // 200ms 一采 ⇒ 量级在零点几秒以内，这才是本地时钟自己的误差。
                    //
                    // 领先量已经超过 TimelineAheadMaxLead 时不再降速：那不是本地跑快了，
                    // 而是播放器上报的位置本身就滞后于真实播放（部分播放器几秒才刷一次位置，
                    // 上报的一定是「过去某一刻」的值）。此时继续按 0.8 倍推进，
                    // 本地时钟就会一路落到实际音频后面，而整个歌词显示读的正是这个时钟 ——
                    // 表现就是「这句歌词显示完了，下一句其实已经唱到一半」（迟钝），
                    // 等偏差攒过 1.5 秒触发硬对齐时，就是「切到下句歌词，扫光直接跳到一半」。
                    //
                    // 为什么只有「提供时间轴进度」的播放器会中招：整段纠偏都挂在 hasTimeline 上，
                    // 不上报总时长的播放器根本不进这里（位置纯靠本地跑表），所以它们一直是准的。
                    //
                    // 取舍：进度条可能比播放器自报的值早那么一点点（至多 TimelineAheadMaxLead）。
                    // 宁可进度条略偏，也不能让歌词整体落在人声后面 —— 歌词是每一秒都在看的东西。
                    if (_timelineAhead && delta < -TimelineAheadMaxLeadSeconds) _timelineAhead = false;
                }

                _prevDelta = delta;
                _forceResync = false;
            }

            _recentSongs[slot].TickedAt = now; // 标记这个进度是刚推算过的，换歌时据此判断能否续用
            _timelinePos = _recentSongs[slot].Position; // 进度条与歌词同源：永远读同一份位置
        }

        /// <summary>
        /// 无歌词槽位的时间轴（浏览器视频 / PotPlayer / 尚未接管歌词）：位置只服务进度条。
        ///
        /// 与 AdvanceTimeline 同一套两级真源：
        /// 1. SMTC 能给出 (LastUpdatedTime, Position) → 直接用外推值，本地不跑表；
        /// 2. 否则退回「位置为 0 就自走、否则按 1.5 秒阈值对齐」的老口径。
        ///
        /// 老的「跳变对齐 + 自走」在每个 16ms 帧都在跑，而 SMTC 只 200ms 才刷新一次 ——
        /// 两次快照之间进度条完全靠自走估，这就是无歌词会话进度同样对不上的原因。
        /// </summary>
        private void AdvanceFreeTimeline(TimeSpan smtcPos, TimeSpan dt, DateTime now)
        {
            if (_isDragging) return;

            // 拖动静默期内不做外推（理由同 AdvanceTimeline：seek 未生效时快照仍是旧值，
            // 外推会把刚落下的位置继续往前拽）；也不纠偏，就停在当前值上。
            bool settling = now < _seekSettleUntil;

            // 第一级：真实 SMTC 时间轴外推
            if (!settling && TryGetSmtcLivePosition(now, out TimeSpan live) && live >= TimeSpan.Zero)
            {
                _timelinePos = live;
                return;
            }
            if (settling) return;

            // 第二级：虚拟跑表
            if (smtcPos > TimeSpan.Zero && Math.Abs((smtcPos - _timelinePos).TotalSeconds) > 1.5) _timelinePos = smtcPos;
            if (IsPlaying) _timelinePos += dt;
        }

        // ---- 进度条拖动（状态锁 + 拖动缓存） ----
        // 拖动期间只改本地缓存，不打任何 COM / IO；松手才提交一次 seek —— 这是「频繁拖动不卡顿」的全部秘密。

        /// <summary>开始拖动。命中时返回 true，调用方据此 SetCapture 并消费这次点击。</summary>
        public bool BeginDrag(float ratio)
        {
            if (!HasTimeline || Duration <= TimeSpan.Zero) return false;
            _isDragging = true;
            DragTo(ratio);
            return true;
        }

        /// <summary>拖动中：落点写进缓存，并同步写回歌词槽位（歌词与进度条读同一份位置，天然精确同步）。</summary>
        public void DragTo(float ratio)
        {
            if (!_isDragging) return;
            var pos = TimeSpan.FromSeconds(Math.Clamp(ratio, 0f, 1f) * Duration.TotalSeconds);
            _timelinePos = pos;
            if (_lyricSlot >= 0) _recentSongs[_lyricSlot].Position = pos;
            UpdateTimelineTexts();
        }

        /// <summary>松手：解除状态锁，给一段静默期，再异步提交一次 seek。</summary>
        public void EndDrag()
        {
            if (!_isDragging) return;
            _isDragging = false;
            // 播放器执行 seek 的几十~几百毫秒里它上报的仍是旧位置，静默期内不按跳变处理，
            // 否则落点会被判成跳变而把进度条与歌词弹回原处。
            _seekSettleUntil = DateTime.UtcNow.AddSeconds(SeekSettleSeconds);
            if (_currentSession != null) CommitSeek(_currentSession, _timelinePos.Ticks);
        }

        /// <summary>
        /// 全局快捷键的进度步进：在当前位置上前后挪 seconds 秒，夹在 [0, 总长] 内。
        ///
        /// 与拖动同一条路：只改本地位置（进度条与歌词立刻跟上），再异步提交一次 seek ——
        /// 连按十几下也不会在 UI 线程上等播放器。返回 false 表示当前没有可用时间轴
        /// （没有会话 / SMTC 没给总长），调用方据此静默跳过。
        /// </summary>
        public bool SeekBy(double seconds)
        {
            if (!HasTimeline || Duration <= TimeSpan.Zero || _currentSession == null) return false;

            double target = Math.Clamp(_timelinePos.TotalSeconds + seconds, 0, Duration.TotalSeconds);
            var pos = TimeSpan.FromSeconds(target);
            _timelinePos = pos;
            if (_lyricSlot >= 0) _recentSongs[_lyricSlot].Position = pos;
            UpdateTimelineTexts();

            // 与松手拖动同一套静默期：播放器执行 seek 的几十~几百毫秒里上报的仍是旧位置，
            //    不设静默期会被判成跳变，把进度条与歌词弹回原处。
            _seekSettleUntil = DateTime.UtcNow.AddSeconds(SeekSettleSeconds);
            CommitSeek(_currentSession, pos.Ticks);
            return true;
        }

        // 提交一次 seek（位置单位是 100ns tick）。异步丢弃：UI 线程绝不等待播放器，异常也不会冒泡打断交互。
        private static async void CommitSeek(GlobalSystemMediaTransportControlsSession session, long ticks)
        {
            try { await session.TryChangePlaybackPositionAsync(ticks); } catch { }
        }

        // 时钟文本（秒 → m:ss / h:mm:ss）。只在整数秒变化时调用，不产生持续分配。
        private static string FormatClock(int sec)
        {
            if (sec < 0) sec = 0;
            int h = sec / 3600;
            return h > 0 ? $"{h}:{sec / 60 % 60:00}:{sec % 60:00}" : $"{sec / 60}:{sec % 60:00}";
        }

        // 按「整数秒」为键缓存文本：60FPS 拖动路径上，秒数没变就一行都不重排、一个字符串都不新建。
        private void UpdateTimelineTexts()
        {
            int sec = (int)_timelinePos.TotalSeconds;
            if (sec != _shownSec) { _shownSec = sec; TimelineElapsed = FormatClock(sec); }
            int total = (int)Duration.TotalSeconds;
            if (total != _shownTotal) { _shownTotal = total; TimelineTotal = FormatClock(total); }
        }

        // 退到后台的歌词会话：只要它还在放，就继续替它把进度写回自己的槽位，
        // 切回来时歌词位置就是连续的。每秒结算一次；播放状态由后台采样器取好
        // （见 SampleSuspendedSessions），这里只读 _suspendedPlaying / _suspendedDead 两个缓存。
        //
        // 为什么不能在这里直接 GetPlaybackInfo：这一步原本就跑在渲染线程上，而被切走的那个
        // 会话恰恰是最容易阻塞的（切歌瞬间旧会话正在退出，它的提供方可能已经不响应了），
        // 一次调用就能把整块岛体冻住几百毫秒 —— 正是「切歌就卡死」的直接来源。
        private void AdvanceSuspendedTimeline(DateTime now)
        {
            for (int i = 0; i < RecentSongSlots; i++)
            {
                var session = _slotSessions[i];
                if (session == null)
                {
                    _suspendedPlaying[i] = false;
                    _suspendedDead[i] = false;
                    continue;
                }

                // 只在这个槽位登记的后台会话「就是台前正在播的那个软件」时才撤销登记：
                // 它已经回到台前，交回 AdvanceTimeline 推进，不能再替它累加，否则会被加两次。
                //
                // 必须同时比对 AppID，不能只看槽位下标：手动匹配切换软件时，UpdateSession 先把
                // 旧歌登记成后台会话，而 _lyricSlot 要等 FetchLyricsAsync 拿到新歌的槽位才更新，
                // 中间隔着一次媒体属性读取。这段窗口里 _lyricSlot 仍停在旧歌的槽位上、当前会话却
                // 已是新软件，只看下标会把刚登记好的后台会话当场清掉 —— 旧歌进度不再推算、时间戳
                // 也停更，切回来时续播判定失效而被清零，歌词就从头上重播了。
                if (i == _lyricSlot && _recentSongs[i].AppId == _currentAppId)
                {
                    _slotSessions[i] = null;
                    _suspendedPlaying[i] = false;
                    _suspendedDead[i] = false;
                    continue;
                }

                var dt = now - _slotSampleAt[i];
                if (dt.TotalSeconds < 1) continue;
                _slotSampleAt[i] = now;

                // 先打时间戳：即使这首歌处于暂停，也说明这个槽位「还在被跟踪」，进度依然可信
                _recentSongs[i].TickedAt = now;

                // 播放器已退出（后台那次采样抛了）→ 注销登记，放弃推算
                if (_suspendedDead[i])
                {
                    _slotSessions[i] = null;
                    _suspendedPlaying[i] = false;
                    _suspendedDead[i] = false;
                    continue;
                }

                if (!_suspendedPlaying[i]) continue;
                _recentSongs[i].Position += dt;
            }
        }
    }
}