using System.IO;
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
        // 🖱 双击封面（折叠态与展开态都算）是否跳回正在放媒体的那个应用。
        // 关掉后双击完全不消费、不做事，折叠态媒体区的展开入口同时变成**左键单击**
        // （见 Renderer.MediaExpandByLeftClick / MediaExpandByRightClick）。
        //
        // ⚠️ **默认关闭**（2026-10-03 用户要求）：这是「跳回媒体软件」这种会抢走前台焦点的动作，
        //    不该在老用户升级后不告而开 —— 注册表里没有 MediaAppLaunchEnabled 这个键时一律按关闭处理
        //    （见 Program.LoadSettings）。顺带的好处是：升级用户默认拿到的就是他们熟悉的
        //    「左键单击展开媒体面板」（2026-10-02 之前的老口径），而双击跳转变成显式开启的功能。
        //    用户手动开过 / 关过的，注册表里的值照旧优先，不会被这次改默认值影响。
        internal static bool IsAppLaunchEnabled = false;

        internal static bool IsLyricsEnabled = true;
        internal static bool IsKaraokeEnabled = true;
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
        // 当前时间轴里是否存在译文行（随 _lyrics 一起更新，避免每帧遍历数组）
        private bool _lyricsHasTranslation;
        public string CurrentLyric { get; private set; } = "";
        // 当前歌词行的译文（无译文时为空串）。渲染层据此在原文下方再画一行。
        public string CurrentLyricTranslation { get; private set; } = "";
        // 当前这首歌的时间轴里是否有译文：高度补偿只认它，避免逐句有无译文导致岛体忽高忽低
        public bool HasLyricTranslation { get; private set; }
        public float CurrentLyricProgress { get; private set; } = 0f;
        private TimeSpan _lastSmtcPosition = TimeSpan.Zero;
        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private string _lastFetchedTitle = "";
        private string _lastFetchedArtist = "";

        // 「本曲目的网络封面已经就位」的记账（会话 + 标题）。命中时属性刷新一律不再碰封面。
        //
        // ⚠️ 刻意**不用拼串做键**：该判定每次属性刷新都会走到，拼串就是纯 GC 压力；
        //    而且键里**不含歌手** —— 歌手在 seek / 换轨瞬间会短暂缺失，带进来会让键对不上，
        //    于是封面被程序图标顶掉，而记账又还「看起来匹配」，再也换不回网络封面。
        private string _networkCoverAppId = "";
        private string _networkCoverTitle = "";

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
        /// 也供「双击媒体控制 → 跳转对应应用」（<see cref="OpenCurrentApp"/>）取目标。
        /// </summary>
        public string CurrentAppId => _currentAppId;

        // ==================== 🎵 歌曲时间轴 ====================
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

        // 封面位图的**唯一写入口**。三处互不等待的路径都会换图 —— 属性刷新、网络封面下载完成、
        // 会话被清空 —— 而属性刷新那道 _refreshingProperties 闸门管不到网络封面那条异步链，
        // 所以「换引用 + 释放旧图」必须在这里自己串起来：否则两次并发换图会把同一张位图 Dispose 两次，
        // 而渲染线程每帧都在读 Thumbnail —— Dispose 之后再访问就是原生访问违例（0xC0000005，直接杀进程）。
        private readonly object _thumbSwap = new();

        private void SetThumbnail(SKBitmap? next)
        {
            lock (_thumbSwap)
            {
                var old = Thumbnail;
                if (ReferenceEquals(old, next)) return;
                Thumbnail = next;
                old?.Dispose();
                // 换图即作废「当前放的是哪个程序的图标」这条记账：调用方（SetAppIcon）换完之后自己补上，
                // 其余所有来源（网络封面 / 清空）都天然落到「不是程序图标」，不需要每处都记得清。
                _appIconKey = "";
            }
        }

        /// <summary>
        /// 换上「当前会话那个程序」的应用图标。
        /// 同一个程序的图标已经在位时直接返回 —— 视频模式下每次属性刷新都会走到这里，
        /// 没有这道闸就会反复新建 / 释放原生位图（<see cref="AppIconProvider"/> 只发副本，那个副本由我们持有）。
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
        /// <para>判据对**所有软件一致**，只有一条 —— SMTC 没同时给出歌名与歌手。
        /// 反过来只要有歌名 + 歌手就是音乐模式：取歌词、取网络封面（见 <see cref="UpdateMediaMode"/>）。</para>
        ///
        /// <para>B站 / 浏览器天然落在视频模式：B站的 Artist 在刷新时就被清空（网页不提供歌手），
        /// 浏览器则要靠网页标题里的「正在播放: 歌名 - 歌手」才能解析出歌手 —— 解析得出来就是音乐模式
        /// （网易云 / QQ音乐网页版这类），解析不出来就是视频模式。</para>
        /// </summary>
        private bool IsVideoMode => !_isMusicMode;

        /// <summary>
        /// 当前会话是否不具备歌词能力 —— 视频模式，外加「用户手动锁定」这一条豁免。
        ///
        /// <para>豁免的理由：用户明确指定了某个软件，就不该再被「进程名带 edge / chrome」这种猜测否掉，
        /// 否则 msedgewebview2（Pake / Tauri 等 WebView2 套壳播放器）会被当成浏览器直接掐掉歌词，
        /// 手动选择等于白选。手动锁定的判据见 <see cref="_isManualLockedSession"/>。</para>
        /// </summary>
        private bool IsNonLyricSession => !_isManualLockedSession && IsVideoMode;

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
        // ⚠️ 这三个字段是「歌词不闪、封面不丢」的关键，动之前先读完：
        //    SMTC 在 seek / 换轨 / 刷新瞬间会**间歇性**给出空标题或空歌手。如果直接拿原始采样去判模式，
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
        // 当前接管的会话是不是「用户在设置里手动锁定」的那一个。
        // 手动锁定的会话不参与任何自动分类的歌词拦截（见 IsNonLyricSession）：
        // 用户手动选了它，就必须按普通媒体源走歌词校验，校验命中就正常加载歌词。
        private bool _isManualLockedSession;
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
        ///   · Just Solo LyricServer 的 WebSocket 连接与重连循环（<see cref="JustSoloLyricClient.Stop"/>）；
        ///   · 当前封面位图（原生 Skia 位图，Dispose 前先切断属性引用）。
        ///
        /// 刻意不处理的两样：SMTC 会话归系统管；<c>_http</c> 是进程级静态复用的 HttpClient，
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

            // 手动锁定的会话必须真的是用户选中的那个 AppID 才算数 ——
            // 否则「手动选了 A、系统里只有 B」时会错误地放行 B 的自动判定。
            // 判定只做一次字符串比较，不落在 60FPS 路径上。
            _isManualLockedSession = IsManualLockActive && newSession != null
                && string.Equals(_currentAppId, ManualSessionAppId, StringComparison.OrdinalIgnoreCase);

            _isBilibiliSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Bilibili");
            // 浏览器标记照常保留：它同时还驱动网页标题清理（CleanBrowserTitle）。
            // 手动锁定会话的歌词拦截已由 IsNonLyricSession 单独豁免，不受这里影响。
            _isBrowserSession = MediaLogoProvider.IsBrowser(newSession?.SourceAppUserModelId);
            _isJustSoloSession = newSession?.SourceAppUserModelId?.Contains("justsolo", StringComparison.OrdinalIgnoreCase) == true;

            // 🖱 跳转应用的定位采样：会话刚被接管时，前台窗口极可能就是它的主窗口 —— 顺手把句柄记下来。
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
                _networkCoverAppId = "";
                _networkCoverTitle = "";
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
                // 判据**不能只看 AppID** —— 同一个 App 的会话可能被系统换成**新的 COM 对象**
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

        // 目标平台与会话 AppID 的匹配规则（单一数据源）。
        // browser 模式在上游已单独分流，这里只管具体应用；未列出的平台走 ID 直配。
        private static bool MatchesTargetPlatform(string id) => TargetPlatform switch
        {
            "netease" => id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("netease", StringComparison.OrdinalIgnoreCase),
            "qqmusic" => id.Contains("qqmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("tencent", StringComparison.OrdinalIgnoreCase),
            "applemusic" => id.Contains("apple", StringComparison.OrdinalIgnoreCase) && id.Contains("music", StringComparison.OrdinalIgnoreCase),
            "lxmusic" => id.Contains("cn.toside.music.desktop", StringComparison.OrdinalIgnoreCase) || id.Contains("lxmusic", StringComparison.OrdinalIgnoreCase),
            _ => id.Contains(TargetPlatform, StringComparison.OrdinalIgnoreCase),
        };

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
        /// <see cref="RefreshProperties"/> 是否正在执行。
        ///
        /// <para><b>为什么需要这道闸（2026-10-02 修）</b>：本方法有**三个互不等待的 async 入口**
        /// （<c>OnMediaPropertiesChanged</c> / <c>OnPlaybackInfoChanged</c> / <c>UpdateSession</c>），
        /// 而它内部有两处 await（取属性、开封面流）。两次调用真并发时，两边会各自读到**同一个**
        /// <c>Thumbnail</c> 引用，然后各自 <c>oldThumb?.Dispose()</c> + 各自赋值 ——
        /// 结果是一份位图被释放两次、或者被换掉之后仍被渲染线程读。
        /// 实测 SkiaSharp 的 <c>SKBitmap.Dispose()</c> 二次调用是安全的，但**Dispose 之后再访问就是原生
        /// 访问违例（0xC0000005，直接杀进程）** —— 也就是说这不只是"丢一份封面"，
        /// 而是一条真实的进程级崩溃路径（渲染线程每帧都在读 <c>media.Thumbnail</c>）。
        /// 丢掉这次刷新是安全的：下一次属性变化 / 轮询会重新拉一遍。</para>
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

        /// <summary><see cref="RefreshProperties"/> 的主体（拿属性 / 换封面 / 同步播放状态与时长 / 触发取词）。</summary>
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
                    if (_isBrowserSession) smtcTitle = CleanBrowserTitle(smtcTitle, out smtcArtist);
                    else if (_isBilibiliSession) smtcArtist = ""; // 网页不提供歌手，别让标题尾部被当成歌手

                    UpdateMediaMode(smtcTitle, smtcArtist);
                    UpdateCover();
                }
            }
            catch (Exception ex)
            {
                // 属性读失败：**只记日志，一个显示状态都不动**。
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

        // ==================== 🎵 曲目判定 / 音乐模式 / 封面选择 ====================

        /// <summary>
        /// 判定「音乐模式」并落定最终显示的标题 / 歌手。
        ///
        /// <para><b>判据（对所有软件一致）</b>：SMTC 同时给出歌名与歌手 → 音乐模式（取歌词 + 网络封面）；
        /// 否则视频模式 —— 只显示标题，歌手一律不显示。</para>
        ///
        /// <para><b>为什么要缓存「稳定」标题 / 歌手，而不是直接用这一拍的采样</b>：SMTC 在 seek /
        /// 换轨 / 刷新瞬间会间歇性给出空标题或空歌手。直接采信就会让同一首歌的模式来回翻转，
        /// 而渲染线程的「非歌词会话」闸门会在翻成视频模式的那一帧把已显示的歌词清空 ——
        /// 表现就是歌词一闪一闪、翻译消失、封面被程序图标顶掉。规则：</para>
        /// <list type="bullet">
        /// <item>换了会话 → 整条曲目信息重置；</item>
        /// <item>同一会话内标题变了 → 视为换曲，歌手跟着换成这一拍的值（新曲目的歌手可能还没上报）；</item>
        /// <item>同一会话内标题没变 → 空的歌手**不改动**已记住的歌手，只在拿到非空值时补齐 / 纠正。</item>
        /// </list>
        ///
        /// <para>另外给降级留了宽限（<see cref="MusicModeMissGrace"/> 次）：真实视频会一直缺歌手，
        /// 几次之后照样降级；而换曲瞬间「歌手晚一拍才到」不会把模式打回去。</para>
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
        /// 选封面。**视频模式** → 该程序自己的应用图标；**音乐模式** → 网络封面 → 应用图标兜底。
        ///
        /// <para>网络封面由 <see cref="FetchCoverAsync"/> 拿到搜索结果后异步补上，在它到达之前先用应用图标顶着。
        /// 本曲目的网络封面一旦就位就**无条件保持** —— 判据里刻意不带「当前是不是视频模式」：
        /// 模式判定抖动或属性读取失败都不该把一张已经下好的专辑封面换成程序图标（换掉就再也回不来了）。</para>
        ///
        /// <para><b>不再引用 data\image 下的平台站标</b>（资源保留，只是不再被任何代码路径读到）。</para>
        /// </summary>
        private void UpdateCover()
        {
            if (string.Equals(_networkCoverTitle, _trackTitle, StringComparison.Ordinal)
                && string.Equals(_networkCoverAppId, _trackAppId, StringComparison.Ordinal))
                return;

            // 视频模式的唯一来源 / 音乐模式的兜底：该程序自己的应用图标
            SetAppIcon();
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
        /// 📺 双击封面（折叠态是左端缩略图、展开态是那块封面，两种形态同一条判据）时调用：
        /// 跳回正在放媒体的那个应用。
        ///
        /// <para>能做的：把已开着的应用窗口激活到前台；应用没开时按注册信息把它拉起来。
        /// 不能做的：跳到那首歌 / 那个视频的具体播放页 —— SMTC 不提供任何深链接参数，
        /// 这是协议本身的限制，只能到应用本体。</para>
        ///
        /// <para>定位（前台窗口采样 / 进程内枚举窗口 / 注册表核验）都在
        /// <see cref="MediaAppLauncher"/> 里，UI 线程上只做几次极廉价的 API 调用。</para>
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

        // ==================== 🎵 媒体资源获取（歌词 + 封面）====================
        // 「取词」与「取封面」是两件独立的事，各自成一个函数：
        //   · FetchLyricsAsync —— 四个歌词引擎依次兜底，命中即写 _lyrics；
        //   · FetchCoverAsync  —— 只负责把网络封面下载下来换上，失败就保持程序图标兜底。
        //
        // 唯一的调用点是 FetchMediaAsync：排队、换歌清理、以及「这首歌还该不该被取」的校验都在那里，
        // 两条链因此共用同一张网络通行证（_fetchLock），不会各打一遍搜索 ——
        // 封面地址就是取词时的搜索顺带带回来的（见 FetchLyricsAsync 的返回值）。

        /// <summary>
        /// 媒体资源获取的**唯一入口**：先取歌词，再取封面。由 <see cref="RefreshPropertiesCore"/>
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
        /// 取歌词：四个引擎依次兜底（落月 API → QQ 音乐官方歌词 → 网易云 → LRCLIB），
        /// 命中即解析时间轴并写入。译文与封面都随主歌词一起回来，不额外单开接口。
        /// </summary>
        /// <returns>网络封面地址；没有则空串（交给 <see cref="FetchCoverAsync"/> 消费）。</returns>
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
                // 网络封面地址（落月搜索的 cover / 网易云 song/detail 的 picUrl）。
                // 仅音乐模式会用；都没拿到就保持兜底封面（程序图标）。
                string coverUrl = "";

                // ====== 引擎 1：落月 API（主源：原文 + 译文 + 封面 + songmid 一次到位）======
                // ⚠️ 这里原本是 QQ 官方搜索接口 c.y.qq.com/soso/fcgi-bin/client_search_cp，
                //    该接口现已**恒返回 HTTP 500**（空响应）—— 拿不到 songmid，它后面那次取词也永远走不到，
                //    整条链等于全废还白花一次请求。所以换成落月：同样是 QQ 曲库，一次响应把四样东西给齐：
                //      · data.lrc 与 data.trans 同源，时间戳严格对齐 ⇒ 译文不会缺句；
                //      · 搜索响应里的 cover 就是 QQ 专辑图地址；
                //      · 搜索响应里的 mid 就是 QQ 的 songmid（交给引擎 2）。
                //    放在最前面还有个好处：命中就不必再问后面的引擎，总请求数反而更少。
                var luoYue = await FetchFromLuoYueAsync(title, artist, HttpUserAgent);
                if (!string.IsNullOrEmpty(luoYue.Cover)) coverUrl = luoYue.Cover;
                if (!string.IsNullOrEmpty(luoYue.Lrc)) lrcText = luoYue.Lrc;
                if (!string.IsNullOrEmpty(luoYue.Trans)) transText = luoYue.Trans;

                // ====== 引擎 2：QQ 音乐官方歌词接口 ======
                // 落月搜索给出的 mid 就是 QQ 的 songmid（实测可直接喂给本接口取回同一份歌词），
                // 所以这里**不需要搜索**，只多花一次 GET 就多出一条歌词兜底链路 ——
                // 落月的歌词接口偶发失败 / 限流时由它顶上。
                // ⚠️ QQ 官方接口的 trans 经常是空的（实测同一首歌落月有 1986 字译文、QQ 是 0 字），
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

                // ====== 引擎 3：网易云 API ======
                // 判据是「有没有可用时间轴」而不是「字符串空不空」：前面的引擎可能返回非空但一行时间轴都没有的
                // 结果（版权提示 / 空壳响应），只判空的话网易云与 LRCLIB 会被整段跳过，最终就是「没歌词」。
                if (!HasTimedLyric(lrcText))
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
                            new KeyValuePair<string, string>("limit", "5"),
                            new KeyValuePair<string, string>("offset", "0")
                        });

                        // using：HttpResponseMessage 本身持有内容流与连接租约，只释放它里面的流是不够的
                        using var response = await _http.PostAsync("https://music.163.com/api/search/get/web", content);
                        using var searchStream = await response.Content.ReadAsStreamAsync();
                        using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                        long songId = 0;
                        if (searchDoc.RootElement.TryGetProperty("result", out var result) &&
                            result.TryGetProperty("songs", out var songs))
                        {
                            foreach (var song in songs.EnumerateArray())
                            {
                                string name = song.GetProperty("name").GetString() ?? "";
                                string singer = "";
                                if (song.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
                                    singer = artists[0].GetProperty("name").GetString() ?? "";

                                // 精度优化：匹配歌名+歌手，并引入时长校验（误差4秒内）屏蔽 Live/伴奏 版
                                if ((name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                                    (string.IsNullOrEmpty(artist) || singer.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                                {
                                    long durationMs = song.GetProperty("duration").GetInt64();
                                    if (durationSec <= 0 || Math.Abs(durationMs / 1000 - durationSec) <= 4)
                                    {
                                        songId = song.GetProperty("id").GetInt64();
                                        break;
                                    }
                                }
                            }
                        }

                        if (songId > 0)
                        {
                            using var lyricStream = await _http.GetStreamAsync($"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1");
                            using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                            if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) &&
                                lrc.TryGetProperty("lyric", out var lyricStr))
                            {
                                lrcText = lyricStr.GetString() ?? "";
                            }

                            // 网易云译文：独立字段 tlyric，时间戳与原文一一对应
                            if (lyricDoc.RootElement.TryGetProperty("tlyric", out var tl) &&
                                tl.TryGetProperty("lyric", out var tlStr))
                            {
                                transText = tlStr.GetString() ?? "";
                            }

                            // 封面兜底：搜索响应的 album 里只有 picId（不是地址），要拿歌曲 id
                            // 再请求一次 song/detail 才有 album.picUrl。落月已经给过封面就跳过。
                            if (coverUrl.Length == 0) coverUrl = await FetchNeteaseCoverAsync(songId);
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"网易云引擎失败: {ex.Message}"); }
                }

                // ====== 引擎 4：LRCLIB ======
                if (!HasTimedLyric(lrcText))
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

                // ====== 极速解析时间轴 ======
                if (!string.IsNullOrEmpty(lrcText))
                {
                    // 译文先按时间戳建表，随后在解析原文时按时间对齐贴上第二行
                    var transTable = BuildTransTable(transText);
                    int transCursor = 0;
                    var lines = new List<(TimeSpan, string, string)>();
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
                        _lyricsHasTranslation = lines.Exists(l => !string.IsNullOrEmpty(l.Item3));
                    }
                }

                // 搜索顺带命中的封面地址带回给调用方，由取封面那条链消费
                return coverUrl;
            }
            finally
            {
                // 必须释放锁，让下一首歌可以正常获取
                _fetchLock.Release();
            }
        }

        /// <summary>
        /// 取封面：下载 <paramref name="coverUrl"/> 并换上。地址来自 <see cref="FetchLyricsAsync"/>
        /// 搜索时顺带命中的专辑图（QQ 给 albummid、网易云给 album.picUrl）。
        ///
        /// <para>任何一步失败都**什么都不做** —— 保持 <see cref="UpdateCover"/> 已经选好的兜底封面
        /// （该程序的应用图标），绝不清空。</para>
        /// </summary>
        private async Task FetchCoverAsync(string title, string artist, string coverUrl)
        {
            // 视频模式不取网络封面；搜索没命中封面地址时也没什么可取的
            if (IsVideoMode || coverUrl.Length == 0) return;

            // 取词时这首歌是不是已经归属本会话（封面也要挂在同一首歌上）
            if (!IsLyricOwner(title, artist)) return;

            // ⚠️ 刻意**不占 _fetchLock**：那把锁保护的是四个歌词引擎共用的 DefaultRequestHeaders
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
            _networkCoverAppId = coverAppId ?? "";
            _networkCoverTitle = title;
            SetThumbnail(cover);
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

        // QQ 音乐官方歌词接口。⚠️ 它的**搜索**接口（client_search_cp）已经恒返回 500 挂了，
        // 但本接口仍然可用 —— 前提是有 songmid，而 songmid 由落月搜索提供，所以不需要搜索这一步。
        private const string QQMusicLyricApi = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";

        /// <summary>QQ 歌词接口返回的正文是 HTML 实体转义的（换行写成 <c>&#10;</c>），统一还原成普通文本。</summary>
        private static string UnescapeQqText(string? raw)
            => raw?.Replace("&#10;", "\n").Replace("&#13;", "\r")
                    .Replace("&#32;", " ").Replace("&#45;", "-")
                    .Replace("&#40;", "(").Replace("&#41;", ")") ?? "";

        /// <summary>落月 API 一次的产出；没命中的项为 null。</summary>
        /// <param name="Lrc">原文 LRC（data.lrc）</param>
        /// <param name="Trans">译文 LRC（data.trans）</param>
        /// <param name="Cover">专辑封面地址（搜索项的 cover，已换成 300×300 变体）</param>
        /// <param name="Mid">QQ 的 songmid（搜索项的 mid），交给 <see cref="QQMusicLyricApi"/> 用</param>
        private readonly record struct LuoYueResult(string? Lrc, string? Trans, string? Cover, string? Mid);

        /// <summary>
        /// 落月 API：先 /v2/music/tencent/search/song?word= 搜到曲目，再用 /v2/music/tencent/lyric?id=
        /// 取原文（data.lrc）与译文（data.trans）；搜索响应里顺带拿到专辑封面（cover）与 QQ songmid（mid）。
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

                long songId = MatchSong(list, title, artist, out string? cover, out string? mid);
                if (songId <= 0) return default;

                // 2. 取歌词。失败也要把封面 / songmid 带回去 —— 三者互不依赖，能拿到一样算一样
                //    （mid 拿得到就还有引擎 2 那条 QQ 官方接口的路可走）。
                using var lyricStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/tencent/lyric?id={songId}");
                using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                var root = lyricDoc.RootElement;

                if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out int code) && code != 200)
                    return new LuoYueResult(null, null, cover, mid);
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return new LuoYueResult(null, null, cover, mid);

                string lrc = data.TryGetProperty("lrc", out var lrcEl) ? lrcEl.GetString() ?? "" : "";
                string trans = data.TryGetProperty("trans", out var transEl) ? transEl.GetString() ?? "" : "";
                return new LuoYueResult(
                    string.IsNullOrEmpty(lrc) ? null : lrc,
                    string.IsNullOrEmpty(trans) ? null : trans,
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
        /// 落月搜索结果里歌名与歌手全字匹配的第一条。
        /// <paramref name="cover"/> 带出专辑封面地址，<paramref name="mid"/> 带出 QQ 的 songmid；
        /// 匹配不上返回 0（此时两者都是 null）。
        /// </summary>
        private static long MatchSong(JsonElement list, string title, string artist, out string? cover, out string? mid)
        {
            cover = null;
            mid = null;

            foreach (var song in list.EnumerateArray())
            {
                string name = song.TryGetProperty("song", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string singer = song.TryGetProperty("singer", out var singerEl) ? singerEl.GetString() ?? "" : "";

                if (!string.Equals(name, title, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(artist) && !string.Equals(singer, artist, StringComparison.OrdinalIgnoreCase)) continue;

                if (!song.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) continue;

                if (song.TryGetProperty("cover", out var coverEl) && coverEl.GetString() is { Length: > 0 } c)
                    cover = NormalizeCoverUrl(c);
                if (song.TryGetProperty("mid", out var midEl) && midEl.GetString() is { Length: > 0 } m)
                    mid = m;

                return id;
            }
            return 0;
        }

        /// <summary>
        /// 落月给的封面是 800×800（约 180KB），而岛上最大只画 50px —— 换成同一 CDN 的 300×300 变体
        /// （约 33KB），下载量与解码后的原生内存都降到 1/5。地址不符合该格式时原样返回。
        /// </summary>
        private static string NormalizeCoverUrl(string url)
            => url.Replace("R800x800M000", "R300x300M000", StringComparison.Ordinal);

        // LRC 时间标签的几种写法（百分秒 / 毫秒 / 十分之一秒 / 整秒）。
        // 解析时间轴与「这段 LRC 有没有可用时间轴」两处共用同一份，避免规则漂移。
        private static readonly string[] LyricTimeFormats =
            [@"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss"];

        /// <summary>
        /// 这段 LRC 里**至少有一行能被解析出时间戳**吗。
        ///
        /// <para>引擎链按这个判据短路，而不是 <c>string.IsNullOrEmpty</c>：QQ 音乐在搜得到歌、
        /// 但歌词接口返回纯文本（版权提示、空壳响应）时会给出「非空却一行时间轴都没有」的结果 ——
        /// 只判空的话网易云与 LRCLIB 会被整段跳过，最终歌词是空的，表现就是「有时候没歌词」。</para>
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
        private static (long Ticks, string Text)[] BuildTransTable(string lrc)
        {
            if (string.IsNullOrEmpty(lrc)) return Array.Empty<(long, string)>();

            var list = new List<(long Ticks, string Text)>();
            foreach (var line in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('[') && line.IndexOf(']') is int idx && idx > 5)
                {
                    if (TimeSpan.TryParseExact(line.Substring(1, idx - 1), new[] { @"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss" }, null, out var ts))
                    {
                        string text = line.Substring(idx + 1).Trim();
                        if (IsUsableTranslation(text)) list.Add((ts.Ticks, text));
                    }
                }
            }

            list.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
            return list.ToArray();
        }

        // 取某条原文行对应的译文。<paramref name="cursor"/> 由调用方持有并随歌词推进单调后移 ——
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

            // ★ 全帧唯一一次 SMTC 时间轴采样（200ms 节流，换歌后立即补采）：
            //   歌词推进、进度条、总时长三处共用这份快照，杜绝每帧重复打 COM。
            if (_forceResync || (now - _smtcProbeAt).TotalSeconds >= SmtcProbeIntervalSec)
            {
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

            // ★ 歌词归属校验：槽位里的歌必须与当前会话正在放的歌完全一致。
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
            AdvanceTimeline(_currentSession, HasTimeline, _smtcPos, dt, now);

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
                    // 算出当前这句歌词的停留时长，并转换成 0.0 ~ 1.0 的进度
                    TimeSpan endTime = (i < _lyrics.Length - 1) ? _lyrics[i + 1].Time : _lyrics[i].Time + TimeSpan.FromSeconds(4);
                    double duration = (endTime - _lyrics[i].Time).TotalSeconds;
                    if (duration > 0)
                    {
                        progress = (float)((compensatedPosition - _lyrics[i].Time).TotalSeconds / duration);
                        progress = Math.Clamp(progress, 0f, 1f); // 锁定在 0~1 之间
                    }
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

        // 推进当前歌词歌的时间轴。SMTC 采样已由 UpdateLyrics 统一完成（每帧最多一次），这里只做纯计算。
        // 提供真实时间轴的播放器（Apple Music / QQ音乐 / Echo Music 等，EndTime 有效）以 SMTC 为准：
        // 接管新歌后第一次采样强制对齐，之后位置跳变超过 1.5 秒也直接对齐（播放器内拖动 / 主动上报）。
        // 不提供时间轴的播放器（网易云、酷狗等，EndTime 恒为 0）才按播放状态自行累加。
        private void AdvanceTimeline(GlobalSystemMediaTransportControlsSession? session, bool hasTimeline, TimeSpan smtcPos, TimeSpan dt, DateTime now)
        {
            // 快照槽位：异步线程可能在本方法执行期间换掉 _lyricSlot，逐次读取会写串槽位。
            int slot = _lyricSlot;
            if (slot < 0 || _isDragging) return; // 状态锁：拖动期间禁止上游写入与自动推进

            if (hasTimeline)
            {
                if (_forceResync || Math.Abs((smtcPos - _lastSmtcPosition).TotalSeconds) > 1.5)
                {
                    _recentSongs[slot].Position = smtcPos;
                    _lastSmtcPosition = smtcPos;
                }
                _forceResync = false;
            }

            if (IsPlaying) _recentSongs[slot].Position += dt;

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

        // ==================== 🎵 进度条拖动（状态锁 + 拖动缓存） ====================
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

        /// <summary>松手：解除状态锁，把落点登记为 SMTC 对齐基准，再异步提交一次 seek。</summary>
        public void EndDrag()
        {
            if (!_isDragging) return;
            _isDragging = false;
            // 播放器执行 seek 的几十~几百毫秒里，落点会被判成「跳变」而把进度弹回原处，所以先把它写成对齐基准
            _lastSmtcPosition = _timelinePos;
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