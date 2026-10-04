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
        // 默认关闭（2026-10-03 用户要求）：这是「跳回媒体软件」这种会抢走前台焦点的动作，
        //    不该在老用户升级后不告而开 —— 注册表里没有 MediaAppLaunchEnabled 这个键时一律按关闭处理
        //    （见 Program.LoadSettings）。顺带的好处是：升级用户默认拿到的就是他们熟悉的
        //    「左键单击展开媒体面板」（2026-10-02 之前的老口径），而双击跳转变成显式开启的功能。
        //    用户手动开过 / 关过的，注册表里的值照旧优先，不会被这次改默认值影响。
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
        private int _lyricSlot = -1;      // 当前歌词与时间轴归属的槽位，-1 表示尚未接管

        // 「退到后台但仍在播放」的会话，按槽位登记。
        // 用数组而不是单个变量：音乐↔音乐来回切时会有两首歌同时需要后台推算，
        // 单个变量会被中途路过的平台顶掉，跨平台停留久了进度就被判成陈旧值而清零。
        // 每秒采样一次播放状态，60FPS 下不产生额外开销。
        private readonly GlobalSystemMediaTransportControlsSession?[] _slotSessions
            = new GlobalSystemMediaTransportControlsSession?[RecentSongSlots];
        private readonly DateTime[] _slotSampleAt = new DateTime[RecentSongSlots];

        // 接管新歌后，等下一帧拿到 SMTC 时间轴就强制对齐一次。
        // 不能靠「位置跳变 > 1.5 秒」来兜底：新歌位置往往也是 0，差值判不出来，旧进度就会残留。
        private bool _forceResync;

        // 当前会话 AppID 的镜像。渲染线程每帧都要做一次「歌词归属校验」，
        // 直接读 SourceAppUserModelId 会打 COM 调用，这里由 UpdateSession 同步写一份供它零成本比对。
        private string _currentAppId = "";

        /// <summary>
        /// 当前接管会话的 AUMID（没有会话时为空串）。供渲染线程零成本比对，
        /// 也供「双击媒体控制 → 跳转对应应用」（OpenCurrentApp）取目标。
        /// </summary>
        public string CurrentAppId => _currentAppId;

        // ---- 歌曲时间轴 ----
        // 仅当 SMTC 会话提供完整时间轴（EndTime > 0）时启用，不区分平台。
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

        // SMTC 采样快照：每帧最多一次 COM 采样（200ms 节流，换歌后立即补采），歌词 / 进度条 / 总时长共用同一份
        private const double SmtcProbeIntervalSec = 0.2;
        private DateTime _smtcProbeAt = DateTime.MinValue;
        private TimeSpan _smtcPos = TimeSpan.Zero;
        private TimeSpan _smtcDuration = TimeSpan.Zero;

        public float TimelineProgress => Duration > TimeSpan.Zero
            ? Math.Clamp((float)(_timelinePos.TotalSeconds / Duration.TotalSeconds), 0f, 1f) : 0f;

        public string Title { get; private set; } = "Notch Peninsula";
        public string Artist { get; private set; } = "Waiting for media...";
        public bool IsPlaying { get; private set; } = false;
        public bool IsActive { get; private set; } = false;
        public SKBitmap? Thumbnail { get; private set; }

        // 封面位图的唯一写入口。三条互不等待的路径都会换图（属性刷新 / 网络封面下载完成 / 会话清空），
        // 而属性刷新那道 _refreshingProperties 闸门管不到网络封面那条异步链 ——
        // 所以「换引用」必须在这里自己串起来，否则两次并发换图会把同一份位图换乱。
        private readonly object _thumbSwap = new();

        /// <summary>
        /// 换上新的封面位图。
        ///
        /// 刻意不 Dispose 被换下的那一张。渲染线程（NotchWindow.RenderLoop，16ms 线程池定时器）
        /// 每帧都在 canvas.DrawBitmap(media.Thumbnail, …) 里直接读这个属性，而它不持有本锁：
        /// 这里一 Dispose，正在绘制的那张原生位图就被释放 —— 这正是 SkiaSharp 的 use-after-free，
        /// 表现为原生访问违例 0xC0000005，直接杀进程，且托管层的 try/catch 拦不住。
        ///
        /// 放弃 Dispose 是安全的：SKBitmap 有终结器会释放原生内存，丢掉最后一个引用后由 GC 回收，
        /// 代价只是让一张封面多存活一小段时间（300×300 约 300KB）。同一取舍在
        /// ToastIconProvider 的图标缓存里已经用过一次。
        /// </summary>
        private void SetThumbnail(SKBitmap? next)
        {
            lock (_thumbSwap)
            {
                if (ReferenceEquals(Thumbnail, next)) return;
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
        /// </summary>
        private void SetAppIcon()
        {
            // 同一个程序的图标已经在位：什么都不做。为 null（解析失败）时 _appIconKey 是空串，
            // 下一次仍会重试 —— 但 AppIconProvider 那边有失败冷却，重试本身几乎不花钱。
            if (_appIconKey.Length > 0 && string.Equals(_appIconKey, _currentAppId, StringComparison.Ordinal)) return;

            var icon = AppIconProvider.Get(_currentAppId);
            SetThumbnail(icon);            // 内部会把 _appIconKey 清空，所以这一步必须排在下面那行之前
            if (icon != null) _appIconKey = _currentAppId;
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
        // 浏览器会话下，原始标题里带「哔哩哔哩 / bilibili」。
        // 必须在 CleanBrowserTitle 之前判定：清理会抹掉 "_哔哩哔哩_bilibili" 这类后缀，
        //    清完标题里就再也找不到平台名了。用途是把封面切到会话自带的那张（视频封面）。
        private bool _isBilibiliBrowserSession;
        private bool _isJustSoloSession;  // 当前会话是否为 Just Solo，启用 LyricServer 直连歌词
        private readonly JustSoloLyricClient _justSoloLyric = new();

        public MediaController()
        {
            Instance = this;
            _ = InitializeAsync();
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

        private async Task UpdateSession(GlobalSystemMediaTransportControlsSessionManager manager)
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
            // 哔哩哔哩的浏览器判定按会话重置（它依赖标题，见 RefreshPropertiesCore）：换会话必须清掉
            _isBilibiliBrowserSession = false;
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
                IsActive = true;
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
                IsActive = true;
            }
            else
            {
                IsActive = false;
                Title = "No Media";
                Artist = "";
                IsPlaying = false;
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
        // 这些播放器都会通过 SMTC 一并给出当前正在播放的那张封面，它比「按歌名 + 歌手去曲库搜出来的图」
        // 更准：冷门歌、带别名的外文歌、翻唱版本在曲库里容易匹配失败或匹配到别的版本。
        // 其余软件保持既有封面链（网络搜索封面 → 应用图标），一个字节不动。
        private static readonly string[] SmtcCoverPreferredIds =
        [
            "cloudmusic", "netease",   // 网易云音乐
            "kugou", "酷狗",            // 酷狗音乐
            "qqmusic", "tencent",      // QQ 音乐
            "applemusic",              // Apple Music（UWP 的 AUMID 形如 AppleInc.AppleMusicWin_…）
            "spotify",                 // Spotify
            "qishui", "汽水",           // 汽水音乐
            "migu", "咪咕",             // 咪咕音乐
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
        /// 为什么需要这道闸（2026-10-02 修）：本方法有三个互不等待的 async 入口
        /// （OnMediaPropertiesChanged / OnPlaybackInfoChanged / UpdateSession），
        /// 而它内部有两处 await（取属性、开封面流）。两次调用真并发时，两边会各自读到同一个
        /// Thumbnail 引用，然后各自 oldThumb?.Dispose() + 各自赋值 ——
        /// 结果是一份位图被释放两次、或者被换掉之后仍被渲染线程读。
        /// 实测 SkiaSharp 的 SKBitmap.Dispose() 二次调用是安全的，但Dispose 之后再访问就是原生
        /// 访问违例（0xC0000005，直接杀进程） —— 也就是说这不只是"丢一份封面"，
        /// 而是一条真实的进程级崩溃路径（渲染线程每帧都在读 media.Thumbnail）。
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
                        // 平台名要在清理之前判：CleanBrowserTitle 会把 "_哔哩哔哩_bilibili" 这类后缀抹掉。
                        // 粘性：一旦判出过哔哩哔哩，本会话内就一直算 —— 站内切集/切下一条时，
                        // 标题会经历「旧标题 → 中间态 → 新标题」的过渡，中途那次刷新可能抓到一个
                        // 不带平台名的标题；若就此翻回 false，封面会退回浏览器图标且之后未必再有刷新来纠正。
                        if (IsBilibiliTitle(smtcTitle)) _isBilibiliBrowserSession = true;
                        smtcTitle = CleanBrowserTitle(smtcTitle, out smtcArtist);
                    }
                    else
                    {
                        _isBilibiliBrowserSession = false;
                        if (_isBilibiliSession) smtcArtist = ""; // 网页不提供歌手，别让标题尾部被当成歌手
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
                IsPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                IsPlaying = false;
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
        /// 判据（对所有软件一致）：SMTC 同时给出歌名与歌手 → 音乐模式（取歌词 + 网络封面）；
        /// 否则视频模式 —— 只显示标题，歌手一律不显示。
        ///
        /// 为什么要缓存「稳定」标题 / 歌手，而不是直接用这一拍的采样：SMTC 在 seek /
        /// 换轨 / 刷新瞬间会间歇性给出空标题或空歌手。直接采信就会让同一首歌的模式来回翻转，
        /// 而渲染线程的「非歌词会话」闸门会在翻成视频模式的那一帧把已显示的歌词清空 ——
        /// 表现就是歌词一闪一闪、翻译消失、封面被程序图标顶掉。规则：
    /// 换了会话 → 整条曲目信息重置；
    /// 同一会话内标题变了 → 视为换曲，歌手跟着换成这一拍的值（新曲目的歌手可能还没上报）；
    /// 同一会话内标题没变 → 空的歌手不改动已记住的歌手，只在拿到非空值时补齐 / 纠正。
        ///
        /// 另外给降级留了宽限（MusicModeMissGrace 次）：真实视频会一直缺歌手，
        /// 几次之后照样降级；而换曲瞬间「歌手晚一拍才到」不会把模式打回去。
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

            Title = _trackTitle.Length > 0 ? _trackTitle : "Unknown";
            Artist = _isMusicMode ? _trackArtist : ""; // 视频模式：只要标题，歌手不要
        }

        /// <summary>
        /// 选封面。视频模式 → 该程序自己的应用图标；音乐模式 → 外部封面 → 应用图标兜底。
        ///
        /// 外部封面有两条来源，都是异步补上，在它到达之前先用应用图标顶着：
    /// 会话自带封面（FetchSmtcCoverAsync）：只给
        ///       SmtcCoverPreferredIds 里的平台用。由 
        ///       放行 —— 属性刷新时传「本会话确实带了图」，兜底重试时传 true（未知，试一次）。
        ///       它取到的就是正在播放的那张图，比曲库搜索更准；
    /// 网络搜索封面（FetchCoverAsync）：其余情况、以及上面那条取不到时的既有通路。
        ///
        /// 读取节奏：每一首曲目至少发起一次；同一曲目的重试按
        /// SessionCoverRetryInterval 节流（兜底重试每帧都会走到这里）。
        ///
        /// 本曲目的外部封面一旦就位就无条件保持 —— 判据里刻意不带「当前是不是视频模式」：
        /// 模式判定抖动或属性读取失败都不该把一张已经到手的专辑封面换成程序图标（换掉就再也回不来了）。
        ///
        /// 不再引用 data\image 下的平台站标（资源保留，只是不再被任何代码路径读到）。
        /// </summary>
        private void UpdateCover(bool allowSessionCover)
        {
            if (string.Equals(_externalCoverTitle, _trackTitle, StringComparison.Ordinal)
                && string.Equals(_externalCoverAppId, _trackAppId, StringComparison.Ordinal))
                return;

            // 会话自带封面的两类来源（其余情况保持既有链路：网络搜索封面 → 应用图标）：
            //   ① 音乐模式 + SmtcCoverPreferredIds 里的播放器 —— 图就是当前这首歌的专辑封面；
            //   ② 浏览器 + 哔哩哔哩 —— 视频模式也走：SMTC 给的是视频封面，比浏览器图标有信息量。
            bool preferSessionCover = _isMusicMode
                ? IsSmtcCoverPreferredAppId(_currentAppId)
                : (_isBrowserSession && _isBilibiliBrowserSession);

            // 应用图标只是两条来源都还没有时的占位：本会话不做「会话封面优先」，
            // 或者它还没有过任何外部封面。
            //
            // 会话封面优先的会话里，已就位的外部封面不会被应用图标顶掉：站内切集 / 切下一条时，
            //    会话未必重新给出缩略图，一旦换成浏览器图标就再也回不来了（新封面根本不会到达）。
            //    此时保持上一张、继续重试：拿到新封面就换上，拿不到也只是短暂停在旧图上，
            //    不会退化成「只剩一个图标」。
            if (!preferSessionCover || _externalCoverTitle.Length == 0) SetAppIcon();

            if (!allowSessionCover || !preferSessionCover) return;

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
                bool resumable = _recentSongs[newSlot].AppId == appId
                                 && _slotSessions[newSlot] != null
                                 && IsFreshSlot(_recentSongs[newSlot]);
                if (!resumable) _recentSongs[newSlot].Position = TimeSpan.Zero;
                _recentSongs[newSlot].AppId = appId;
                _slotSessions[newSlot] = null; // 这首歌已回到台前，交回当前会话推进
            }

            _lyricSlot = newSlot;
            _forceResync = true;
            _lyrics = Array.Empty<(TimeSpan, string, string)>();
            _lyricWordTimings = null;
            _lyricsHasTranslation = false;
            CurrentLyric = "";
            CurrentLyricTranslation = "";
            HasLyricTranslation = false;
            CurrentLyricProgress = 0f;

            // ---- 取词 → 取封面：两条链各自成一个函数，各自排队、各自校验归属 ----
            string coverUrl = await FetchLyricsAsync(title, artist, durationSec);
            await FetchCoverAsync(title, artist, coverUrl);
        }

        /// <summary>
        /// 取歌词：五个引擎依次兜底（落月 API(QQ音乐) → QQ 音乐官方歌词 → 落月 API(网易云) →
        /// 网易云官方 → LRCLIB），命中即解析时间轴并写入。译文与封面都随主歌词一起回来，不额外单开接口。
        ///
        /// 会话是网易云音乐时走「网易优先」：网易系两档（落月 API(网易云) → 网易云官方）
        /// 整体提到最前，歌词与封面都优先网易云的源，其余档位依次顺延。
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

                // 正在放歌的是不是网易云音乐。是的话，网易系两档会被提到整条链的最前面（见下面「网易优先」段），
                // 歌词与封面都优先网易云的源；其余播放器一切照旧。
                bool preferNetease = IsNeteaseAppId(_currentAppId);

                // ---- 网易优先：会话是网易云音乐时，把网易系两档提到最前 ----
                // 正在放歌的就是网易云，用网易云曲库最贴：同一曲库来源，版本能对上、译文更全、专辑图也更对版。
                // 歌词与封面一起前置，顺序钉死 —— 落月 API - 网易云 在前、网易云官方在后。
                // 其他播放器不走这一段，网易系两档在引擎 3 / 引擎 4 的位置上充当兜底。
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
                        if (coverUrl.Length == 0) coverUrl = neteaseOfficialFirst.Cover;
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
                var luoYue = await FetchFromLuoYueAsync(title, artist, HttpUserAgent);
                // 封面：填封面链的第一档，仅在还没有封面时采纳
                //（网易云会话下「网易优先」段可能已给出网易云的图，此时不覆盖）。
                if (coverUrl.Length == 0 && !string.IsNullOrEmpty(luoYue.Cover)) coverUrl = luoYue.Cover;
                // 歌词与译文：只在还没有可用时间轴时采纳。本档同时是 songmid 的来源，
                // 所以即使歌词已被前面的档先取到，下面这一次搜索照常进行（封面与 songmid 仍由它提供）。
                if (!HasTimedLyric(lrcText))
                {
                    if (!string.IsNullOrEmpty(luoYue.Lrc))
                    {
                        lrcText = luoYue.Lrc;
                        yrcText = luoYue.Yrc ?? "";
                        yrcTimingFirst = false; // 落月 QQ 的 yrc 是「文字在前」
                    }
                    if (!string.IsNullOrEmpty(luoYue.Trans)) transText = luoYue.Trans;
                }

                // ---- 引擎 2：QQ 音乐官方歌词接口 ----
                // 落月搜索给出的 mid 就是 QQ 的 songmid（实测可直接喂给本接口取回同一份歌词），
                // 所以这里不需要搜索，只多花一次 GET 就多出一条歌词兜底链路 ——
                // 落月的歌词接口偶发失败 / 限流时由它顶上。
                // QQ 官方接口的 trans 经常是空的（实测同一首歌落月有 1986 字译文、QQ 是 0 字），
                //    所以译文只在落月完全没给时才采纳它，免得把一份好译文覆盖成空。
                if (!HasTimedLyric(lrcText) && !string.IsNullOrEmpty(luoYue.Mid))
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

                // ---- 引擎 3：落月 API - 网易云（网易云曲库：原文 + 译文） ----
                // 位置在 QQ 系两档之后：
                //   · 从 QQ 音乐或别家播放器放歌时，前两档（同为 QQ 曲库）基本已经命中，压根走不到这里
                //     —— 也就是「其他软件照旧走 QQ 音乐」；
                //   · 真落到这一档的，多半是「只在网易云上架 / 版本与 QQ 曲库对不上」的歌，
                //     正好由网易云曲库补上。
                // 它与引擎 4 的网易云官方接口是同一个曲库、两套实现，互为兜底。
                // 网易云会话下这一档已经在方法开头的「网易优先」段跑过了，这里直接跳过，不重复请求。
                if (!preferNetease && !HasTimedLyric(lrcText))
                {
                    var netease = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    if (!string.IsNullOrEmpty(netease.Lrc))
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
                if (!preferNetease && !HasTimedLyric(lrcText))
                {
                    var neteaseOfficial = await FetchFromNeteaseOfficialAsync(title, artist, durationSec, allowCover: true);
                    if (!string.IsNullOrEmpty(neteaseOfficial.Lrc)) lrcText = neteaseOfficial.Lrc;
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(neteaseOfficial.Trans))
                        transText = neteaseOfficial.Trans;
                    // 封面兜底第二档：落月（引擎 1）没给过封面时才用网易云这张
                    if (coverUrl.Length == 0) coverUrl = neteaseOfficial.Cover;
                }

                // ---- 引擎 5：LRCLIB ----
                // /api/get 要求 track_name 与 artist_name 都非空，缺任一个直接回 400（实测：
                //    artist_name 为空 → 400、track_name 为空 → 400），而不是「这首歌它没有」的 404。
                //    歌名为空在上游已提前返回，所以这里只需挡住歌手为空 —— 否则就是白花一次往返，
                //    还往日志里刷一条看不懂的 400 WARN，而它其实是最后一个兜底引擎。
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
                if (coverUrl.Length == 0 && !IsVideoMode)
                    coverUrl = await FetchQqCoverAsync(title, artist);

                // 统一归一成小尺寸变体（QQ 替换尺寸段 + 网易云补 ?param=）：无论封面最终来自哪一档，
                // 下载量与解码内存都降到约 1/5，把 4 秒超时预算留给真正慢的网络。
                if (coverUrl.Length > 0) coverUrl = NormalizeCoverUrl(coverUrl);

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
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    buffer.Position = 0;
                    cover = SKBitmap.Decode(buffer);
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
        /// 取会话自带封面（SMTC 缩略图）并换上。只给 SmtcCoverPreferredIds 里的播放器用：
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
                using var buffer = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(buffer);
                buffer.Position = 0;
                return SKBitmap.Decode(buffer);
            }
            catch (Exception ex)
            {
                Logger.Debug($"会话自带封面读取失败: {ex.Message}");
                return null;
            }
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
        private async Task<LuoYueResult> FetchFromLuoYueAsync(string title, string artist, string ua)
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
                long songId = MatchSong(list, title, artist, !IsChineseTitle(title), out string? cover, out string? mid);
                if (songId <= 0) return default;

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
                long songId = MatchSong(list, title, artist, false, out string? cover, out _);
                if (songId <= 0) return default;

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
                if (searchDoc.RootElement.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("songs", out var songs)
                    && songs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in songs.EnumerateArray())
                    {
                        // 一律 TryGetProperty + 先验 ValueKind：这些字段在真实响应里会缺、
                        //    甚至类型不对（实测到过 album 是字符串）。裸 GetProperty 或在非对象元素上
                        //    调 TryGetProperty 都会抛异常，而异常会被外层 catch 吞成一行 WARN ——
                        //    代价却是整个网易云引擎中断，歌词与封面一起没了。
                        if (song.ValueKind != JsonValueKind.Object) continue;

                        string name = song.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        string singer = "";
                        if (song.TryGetProperty("artists", out var artists)
                            && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0
                            && artists[0].ValueKind == JsonValueKind.Object
                            && artists[0].TryGetProperty("name", out var singerEl))
                            singer = singerEl.GetString() ?? "";

                        // 精度优化：匹配歌名+歌手，并引入时长校验（误差4秒内）屏蔽 Live/伴奏 版
                        if ((name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(artist) || singer.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                        {
                            // 时长缺失（0）时不做校验：宁可取回搜索结果里的第一条，也别因为缺字段整首歌没歌词
                            long durationMs = song.TryGetProperty("duration", out var durEl) && durEl.TryGetInt64(out long d) ? d : 0;
                            if (durationMs <= 0 || durationSec <= 0 || Math.Abs(durationMs / 1000 - durationSec) <= 4)
                            {
                                if (!song.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out songId)) continue;
                                break;
                            }
                        }
                    }
                }

                if (songId <= 0) return default;

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
                return default;
            }
        }

        /// <summary>
        /// 落月搜索结果里最匹配的一条。 带出专辑封面地址、
        ///  带出 QQ 的 songmid；没匹配上返回 0（此时两者都是 null）。
        ///
        /// 为什么要「宽容匹配」而不是全等：全等在真实曲库里命中率偏低，实测两类情况直接落空 ——
    /// 多歌手：落月给 Daoko/米津玄師，播放器上报的歌手却只是 DAOKO（合作曲的常态）；
    /// 标题带后缀：落月给 夜曲 - A35、晴天 (Live)，播放器给的是 夜曲、晴天。
        /// 而一旦落空，歌词与封面会一起拿不到 —— 封面地址同样出自这次搜索。表现就是「有概率获取不到」。
        ///
        /// 规则：两侧先归一化（只留字母 / 数字 / 汉字假名，转小写，去掉空格括号连字符），
        /// 标题要求归一化后全等（分更高）或互相包含；歌手按分隔符拆成多个名字，任一对得上即算过。
        /// 取分数最高的那一条；歌手完全不沾边的不候用，免得挂到翻唱 / 同名曲上。
        ///
        ///  = true 时，标题在前两档都落空后再试一次
        /// 包含匹配（IsLooseTitleMatch）。它只给非中文曲目开（见
        /// IsChineseTitle）：这类歌名在 QQ 曲库里普遍写成「原名 + 中译别名」——
        /// 实测《花がら(Withered Flower)》在库里的条目是《花がら (枯花)》，归一化后
        /// 花がらwitheredflower 与 花がら枯花 互不包含，全字匹配必然落空；
        /// 换更短的搜索词也没用（实测三种搜索词返回的候选完全相同），卡点在打分。
        /// 歌手校验在任何档位都不放宽。
        /// </summary>
        private static long MatchSong(JsonElement list, string title, string artist, bool allowLooseTitle, out string? cover, out string? mid)
        {
            cover = null;
            mid = null;

            string wantTitle = NormalizeToken(title);
            if (wantTitle.Length == 0) return 0;
            var wantArtists = SplitArtists(artist);

            long bestId = 0;
            int bestScore = 0;

            foreach (var song in list.EnumerateArray())
            {
                if (song.ValueKind != JsonValueKind.Object) continue; // 数组里混进非对象元素：跳过而不是抛

                string name = song.TryGetProperty("song", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string singer = song.TryGetProperty("singer", out var singerEl) ? singerEl.GetString() ?? "" : "";

                int score = ScoreCandidate(wantTitle, wantArtists, NormalizeToken(name), SplitArtists(singer), allowLooseTitle);
                if (score <= bestScore) continue;
                if (!song.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) continue;

                bestScore = score;
                bestId = id;
                cover = song.TryGetProperty("cover", out var coverEl) && coverEl.GetString() is { Length: > 0 } c
                    ? NormalizeCoverUrl(c) : null;
                mid = song.TryGetProperty("mid", out var midEl) && midEl.GetString() is { Length: > 0 } m ? m : null;
            }

            if (bestId <= 0) { cover = null; mid = null; }
            return bestId;
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
        /// 又要覆盖较短标题的 LooseTitleCoverage 以上。是放宽标题写法差异，
        /// 不是放宽「这是不是同一首歌」：歌手校验照旧，且它只在非中文曲目（见
        /// IsChineseTitle）与前两档都落空时才启用。
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
    /// smartbox_new.fcg —— QQ 的搜索建议接口。官方主搜索 client_search_cp 已恒 500，
        ///       但这个仍然可用，且正好给出曲名 / 歌手 / songmid，可以直接复用同一套匹配打分；
    /// fcg_play_single_song.fcg?songmid= —— 用 songmid 换回 album.mid（albummid），
        ///       拼成 QQ 专辑图地址（与落月给的 cover 同一个 CDN 格式）。
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
        /// 浏览器原始标题里是否带哔哩哔哩的平台名（用于把封面切到会话自带的那张）。
        /// 必须在 CleanBrowserTitle 之前调用 —— 清理会把 _哔哩哔哩_bilibili
        /// 这类后缀去掉，清完之后标题里就再也找不到平台名了。
        /// </summary>
        private static bool IsBilibiliTitle(string title)
            => title.Contains("哔哩哔哩", StringComparison.Ordinal)
               || title.Contains("bilibili", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 把封面地址归一成小尺寸变体：岛上最大只画 50px，下原图纯属浪费带宽与解码内存
        /// （还直接吃掉 4 秒的 HttpClient 超时预算）。两类 CDN 的写法不同：
        ///
    /// QQ（y.qq.com / y.gtimg.cn）：尺寸段写在文件名里 —— R800x800M000 → R300x300M000
        ///       （180KB → 33KB）；
    /// 网易云（p*.music.126.net）：URL 不带尺寸段，要用 ?param=NyN 查询参数指定 ——
        ///       同一张图原图 502KB、?param=300y300 为 94KB。
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

            public LyricWordTiming(int[] endMs, int[] cumChars, int totalChars)
            {
                EndMs = endMs;
                CumChars = cumChars;
                TotalChars = totalChars;
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
                    new LyricWordTiming(endMs.ToArray(), cumChars.ToArray(), totalChars)));
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

            for (int j = cursor; j < table.Length && table[j].StartMs <= lineMs + YrcTimeFallbackMs; j++)
            {
                if (Math.Abs(table[j].StartMs - lineMs) <= YrcTimeFallbackMs)
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
    /// 逐字优先：本行有字级时间轴时按每个字自己的时值推进（长的音就慢慢走、
        ///       快念段就快速掠过），与 ComputeWordAlignedProgress 的口径一致；
    /// 自动回退整行扫光：逐字效果不可用时，按「本行起点 → 下一行起点」线性插值。
        ///       所谓「逐字不可用」有两种情形 —— 本行没对上字级数据、整首歌拿不到逐字数据
        ///       （只有落月的两个源会带 yrc，网易云侧还只有部分歌有）。
        ///
        /// 为什么是一条链：两条路径产出的是同一个 0~1 标量，
        /// 渲染层的扫光依旧是「整行总宽 × 进度」，不需要知道自己拿到的是哪一种驱动。
        /// 于是「这首歌没有逐字数据」不会退化成不扫光，而只是自动降级成整行均匀扫光。
        /// </summary>
        private float ComputeScanProgress(int lineIndex, TimeSpan position)
        {
            TimeSpan lineStart = _lyrics[lineIndex].Time;

            // ① 逐字优先
            if (_lyricWordTimings is { Length: > 0 } timings
                && lineIndex < timings.Length
                && timings[lineIndex] is { TotalChars: > 0 } wordTiming)
            {
                return ComputeWordAlignedProgress(
                    wordTiming, (position - lineStart).TotalMilliseconds);
            }

            // ② 逐字不可用 → 回退整行扫光
            TimeSpan endTime = lineIndex < _lyrics.Length - 1
                ? _lyrics[lineIndex + 1].Time
                : lineStart + TimeSpan.FromSeconds(4); // 末行没有下一句可依，按 4 秒估
            double duration = (endTime - lineStart).TotalSeconds;
            if (duration <= 0) return 0f;

            return Math.Clamp((float)((position - lineStart).TotalSeconds / duration), 0f, 1f);
        }

        /// <summary>
        /// 按逐字时间轴把「本行已经唱到哪」折算成 0~1 的扫光比例。仅在逐字数据可用时被
        /// ComputeScanProgress 调用；不可用时由那条链回退到整行扫光。
        ///
        /// 口径是「字符数」而不是像素宽度：中文与日文基本等宽，折算误差肉眼不可见；
        /// 好处是渲染层零改动 —— 它拿到的仍然只是一个 0~1 的标量，扫光依旧是「整行总宽 × 比例」。
        /// 含大量拉丁字母 / 空格的行会有几像素偏差，这是已知取舍。
        /// </summary>
        private static float ComputeWordAlignedProgress(LyricWordTiming timing, double elapsedMs)
        {
            int[] ends = timing.EndMs;
            int n = ends.Length;
            if (n == 0 || timing.TotalChars <= 0) return 0f;
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
            if (Thumbnail == null
                || !string.Equals(_externalCoverTitle, _trackTitle, StringComparison.Ordinal)
                || !string.Equals(_externalCoverAppId, _trackAppId, StringComparison.Ordinal))
                UpdateCover(true);

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

            // 全帧唯一一次 SMTC 时间轴采样（200ms 节流，换歌后立即补采）：
            //   歌词推进、进度条、总时长三处共用这份快照，杜绝每帧重复打 COM。
            bool sampledNow = false;
            if (_forceResync || (now - _smtcProbeAt).TotalSeconds >= SmtcProbeIntervalSec)
            {
                sampledNow = true;
                _smtcProbeAt = now;
                try
                {
                    var t = _currentSession.GetTimelineProperties();
                    _smtcPos = t.Position;
                    _smtcDuration = t.EndTime > TimeSpan.Zero ? t.EndTime : TimeSpan.Zero;
                }
                catch { _smtcPos = TimeSpan.Zero; _smtcDuration = TimeSpan.Zero; }
                // 没有端到端时长（网易云 / 酷狗等）：强制对齐标记留着也没用，就地消费掉，
                // 让采样稳定回到 200ms 节流 —— 否则它会每帧都触发一次补采，等于没节流。
                if (_smtcDuration <= TimeSpan.Zero) _forceResync = false;
            }

            // 「检测到 SMTC 提供歌曲进度」= 端到端时长有效，不区分具体平台
            HasTimeline = _smtcDuration > TimeSpan.Zero;
            Duration = _smtcDuration;
            UpdateTimelineTexts();

            // 浏览器视频 / PotPlayer 这类无歌词会话、以及尚未接管歌词的歌：不显示歌词，
            // 但时间轴照常走 —— 只要 SMTC 给出进度就显示。
            if (IsNonLyricSession || _lyricSlot < 0)
            {
                SetLyric("", "", 0f, false);
                AdvanceFreeTimeline(_smtcPos, dt);
                _forceResync = false; // 无歌词槽位：强制对齐标记不适用，就地消费，避免每帧重复采样
                return;
            }

            // 歌词归属校验：槽位里的歌必须与当前会话正在放的歌完全一致。
            // UpdateSession / RefreshProperties 都跑在异步线程上，渲染线程完全可能正好夹在
            // 「会话已换、歌词还没换」的缝隙里 —— 那一瞬间上一首的歌词会被新会话的时间轴推着继续走，
            // 这就是切歌残留的根源。这里每帧做一次本地比对（多数情况下是同一实例的短路比较），
            // 只要对不上就当场清空：宁可空一帧，也不让上一首的歌词多留一帧。
            if (_recentSongs[_lyricSlot].Title != Title
                || _recentSongs[_lyricSlot].Artist != Artist
                || _recentSongs[_lyricSlot].AppId != _currentAppId)
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
            TimeSpan compensatedPosition = _recentSongs[_lyricSlot].Position + TimeSpan.FromSeconds(0.6 + LyricDelayOffset);
            for (int i = _lyrics.Length - 1; i >= 0; i--)
            {
                if (compensatedPosition >= _lyrics[i].Time)
                {
                    found = _lyrics[i].Text;
                    foundTrans = _lyrics[i].Translation;
                    // 本句的扫光进度：逐字优先，逐字不可用则自动回退整行扫光（见 ComputeScanProgress）
                    progress = ComputeScanProgress(i, compensatedPosition);
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

        /// <summary>
        /// 推进当前歌词歌的时间轴。SMTC 采样已由 UpdateLyrics 统一完成（每帧最多一次），
        /// 这里只做纯计算。提供真实时间轴的播放器（Apple Music / QQ音乐 等，EndTime 有效）以 SMTC 为准；
        /// 不提供时间轴的播放器（网易云、酷狗等，EndTime 恒为 0）才按播放状态自行累加。
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
                bool settling = now < _seekSettleUntil; // 刚松手拖动：播放器还没执行完 seek

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

                    // 降速安全阀（2026-10-03 修「歌词越来越慢」）：降速只有在播放器上报的位置
                    //    确实在往上追时才有意义 —— 领先量必须被一口口吃掉。
                    //    若领先量不但没缩小、反而继续扩大，说明它根本没在推进（位置卡住 /
                    //    只在特定事件才刷新），它永远追不上来，降速就成了无底洞：
                    //    每秒只走 0.8×dt ⇒ 每分钟落后 12 秒，单调累积，且块内没有任何分支能把它复位
                    //    （硬对齐要求 delta > +1.5，往回退又被刻意禁止）。
                    //    这时放弃降速、恢复按实时推进；位置仍单调不减，不会出现卡拉 OK 回退。
                    if (_timelineAhead && delta < _prevDelta && delta < -TimelineStallAheadSeconds)
                        _timelineAhead = false;
                }

                _prevDelta = delta;
                _forceResync = false;
            }

            _recentSongs[slot].TickedAt = now; // 标记这个进度是刚推算过的，换歌时据此判断能否续用
            _timelinePos = _recentSongs[slot].Position; // 进度条与歌词同源：永远读同一份位置
        }

        // 无歌词槽位的时间轴（浏览器视频 / PotPlayer / 尚未接管歌词）：位置只服务进度条。
        // smtcPos 为 0 视为「本帧没有可用时间轴」，只按播放状态自走；否则跳变超过 1.5 秒即对齐。
        private void AdvanceFreeTimeline(TimeSpan smtcPos, TimeSpan dt)
        {
            if (_isDragging) return;
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
        // 切回来时歌词位置就是连续的。每秒采样一次，避免 60FPS 下每帧都打 COM 调用。
        private void AdvanceSuspendedTimeline(DateTime now)
        {
            for (int i = 0; i < RecentSongSlots; i++)
            {
                var session = _slotSessions[i];
                if (session == null) continue;

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
                    continue;
                }

                var dt = now - _slotSampleAt[i];
                if (dt.TotalSeconds < 1) continue;
                _slotSampleAt[i] = now;

                // 先打时间戳：即使这首歌处于暂停，也说明这个槽位「还在被跟踪」，进度依然可信
                _recentSongs[i].TickedAt = now;

                try
                {
                    if (session.GetPlaybackInfo()?.PlaybackStatus
                        != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                }
                catch
                {
                    _slotSessions[i] = null; // 播放器已退出，放弃推算
                    continue;
                }

                _recentSongs[i].Position += dt;
            }
        }
    }
}