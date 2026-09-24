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
        internal static bool IsLyricsEnabled = true;
        internal static bool IsKaraokeEnabled = true;
        // 翻译歌词：开启后把当前句的译文作为第二行画在原文下方（仅在有译文时生效）
        internal static bool IsTranslationEnabled = true;
        internal static float LyricDelayOffset = 0f;
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

        // 当前会话是否不具备歌词能力（浏览器 / 视频类 / PotPlayer）：它们没有可用的歌词时间轴，
        // 既不该显示歌词，也不该污染歌词进度。三处判断共用一份定义，避免规则漂移。
        //
        // 例外：用户在设置里手动锁定的软件越过全部自动判定（含浏览器判定），一律按普通媒体源
        // 走歌词校验 —— 用户明确指定了它，就不该再被「进程名带 edge / chrome」这种猜测否掉。
        // 否则 msedgewebview2（Pake / Tauri 等 WebView2 套壳播放器）会被当成浏览器直接掐掉歌词，
        // 手动选择等于白选。手动锁定的判据见 _isManualLockedSession。
        private bool IsNonLyricSession =>
            !_isManualLockedSession && (_isBilibiliSession || _isBrowserSession || _isPotPlayerSession);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // 已挂上播放状态监听的会话（按 AppID 记账）。
        // 自动匹配的接管结果依赖播放状态，而会话表变更事件不会因播放/暂停触发 ——
        // 所以给系统里每个会话都挂一份监听，任何一个开始播放都能立刻重新挑选接管目标。
        private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watchedSessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _watcherLock = new();
        private bool _isBilibiliSession;  // 通用模式下当前会话是否为 bilibili，用于隐藏 Artist
        private bool _isPotPlayerSession; // 当前会话是否为 PotPlayer，无歌名/歌手时隐藏文本
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
            try { Thumbnail?.Dispose(); Thumbnail = null; } catch { }
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

            // 命中 bilibili / PotPlayer / 浏览器 会话时打标记，供刷新时应用文本显示策略
            bool wasNonLyric = IsNonLyricSession;
            _currentAppId = newSession?.SourceAppUserModelId ?? "";

            // 手动锁定的会话必须真的是用户选中的那个 AppID 才算数 ——
            // 否则「手动选了 A、系统里只有 B」时会错误地放行 B 的自动判定。
            // 判定只做一次字符串比较，不落在 60FPS 路径上。
            _isManualLockedSession = IsManualLockActive && newSession != null
                && string.Equals(_currentAppId, ManualSessionAppId, StringComparison.OrdinalIgnoreCase);

            _isBilibiliSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Bilibili");
            _isPotPlayerSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "PotPlayer");
            // 浏览器标记照常保留：它同时还驱动网页标题清理（CleanBrowserTitle）。
            // 手动锁定会话的歌词拦截已由 IsNonLyricSession 单独豁免，不受这里影响。
            _isBrowserSession = MediaLogoProvider.IsBrowser(newSession?.SourceAppUserModelId);
            _isJustSoloSession = newSession?.SourceAppUserModelId?.Contains("justsolo", StringComparison.OrdinalIgnoreCase) == true;

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
                Thumbnail?.Dispose();
                Thumbnail = null;
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

                // 新出现的会话：挂上监听
                for (int i = 0; i < sessions.Count; i++)
                {
                    string id = sessions[i].SourceAppUserModelId ?? "";
                    if (id.Length == 0 || _watchedSessions.ContainsKey(id)) continue;
                    sessions[i].PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _watchedSessions[id] = sessions[i];
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

        private async Task RefreshProperties()
        {
            if (_currentSession == null) return;

            try
            {
                var props = await _currentSession.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    Title = string.IsNullOrEmpty(props.Title) ? (_isPotPlayerSession ? "" : "Unknown") : props.Title;

                    string browserArtist = "";
                    if (_isBrowserSession)
                        Title = CleanBrowserTitle(Title, out browserArtist);

                    Artist = _isBilibiliSession ? "" : (!string.IsNullOrEmpty(browserArtist) ? browserArtist
                            : (string.IsNullOrEmpty(props.Artist) ? "" : props.Artist));

                    var platformLogo = MediaLogoProvider.GetLogo(_currentSession.SourceAppUserModelId, props.Thumbnail != null);
                    if (platformLogo != null)
                    {
                        var oldThumb = Thumbnail;
                        Thumbnail = platformLogo;
                        oldThumb?.Dispose();
                    }
                    else if (props.Thumbnail != null)
                    {
                        try
                        {
                            using var stream = await props.Thumbnail.OpenReadAsync();
                            using var dotNetStream = stream.AsStreamForRead();

                            var oldThumb = Thumbnail;
                            Thumbnail = SKBitmap.Decode(dotNetStream);
                            oldThumb?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("封面解析失败", ex);
                            Thumbnail = null;
                        }
                    }
                    else
                    {
                        // 与上面两条分支保持一致的释放纪律：丢引用前先放掉原生位图
                        Thumbnail?.Dispose();
                        Thumbnail = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取媒体属性失败，可能遇到不规范的媒体源", ex);
                Title = _isPotPlayerSession ? "" : "Unknown";
                Artist = (_isBilibiliSession || _isPotPlayerSession) ? "" : "Unknown";
                Thumbnail?.Dispose();
                Thumbnail = null;
            }

            try
            {
                var playbackInfo = _currentSession.GetPlaybackInfo();
                IsPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                IsPlaying = false;
            }

            long durationSec = 0;
            try { if (_currentSession.GetTimelineProperties() is { } t) durationSec = (long)t.EndTime.TotalSeconds; } catch { }

            if (Title != _lastFetchedTitle || Artist != _lastFetchedArtist)
            {
                _lastFetchedTitle = Title;
                _lastFetchedArtist = Artist;
                _ = FetchLyricsAsync(Title, Artist, durationSec);
            }
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

        private async Task FetchLyricsAsync(string title, string artist, long durationSec)
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

            // 等待获取通行证（防止多首歌同时修改 HttpClient 导致程序崩溃）
            await _fetchLock.WaitAsync();
            try
            {
                // 极速拦截：排队轮到自己时，这首歌若已不是时间轴归属者（换歌了）就直接丢弃任务，0 性能浪费。
                // 判据是槽位而不是 Title —— 切到浏览器期间 Title 会变成网页标题，
                // 但那首歌并没有被换掉，此时不该丢弃请求。
                if (!IsLyricOwner(title, artist)) return;

                string query = Uri.EscapeDataString($"{title} {artist}");
                string lrcText = "";
                // 译文 LRC：与原文同一套时间戳，解析后按时间对齐成「原文行 → 译文」的映射
                string transText = "";
                // QQ 搜索命中的 songmid，留着给译文的兜底渠道用（拿到就说明这首歌在 QQ 曲库里有对应记录）
                string qqSongmid = "";
                string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

                // ====== 引擎 1：QQ音乐 (优先) ======
                try
                {
                    _http.DefaultRequestHeaders.Clear();
                    _http.DefaultRequestHeaders.Add("User-Agent", ua);

                    // 内存优化：使用 Stream 流直接解析 JSON，避免生成大字符串吃内存
                    using var searchStream = await _http.GetStreamAsync($"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&n=5&format=json");
                    using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                    if (searchDoc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("song", out var songData) &&
                        songData.TryGetProperty("list", out var list))
                    {
                        foreach (var song in list.EnumerateArray())
                        {
                            string name = song.GetProperty("songname").GetString() ?? "";
                            string singer = "";
                            if (song.TryGetProperty("singer", out var singers) && singers.GetArrayLength() > 0)
                                singer = singers[0].GetProperty("name").GetString() ?? "";

                            // 精度优化：同时验证歌名和歌手名，避免同名歌曲乱串
                            if ((name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                                (string.IsNullOrEmpty(artist) || singer.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                            {
                                qqSongmid = song.GetProperty("songmid").GetString() ?? "";
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(qqSongmid))
                    {
                        _http.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");
                        using var lyricStream = await _http.GetStreamAsync($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={qqSongmid}&format=json&nobase64=1");
                        using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);

                        if (lyricDoc.RootElement.TryGetProperty("lyric", out var lrcEl))
                        {
                            lrcText = lrcEl.GetString()?
                                .Replace("&#10;", "\n").Replace("&#13;", "\r")
                                .Replace("&#32;", " ").Replace("&#45;", "-")
                                .Replace("&#40;", "(").Replace("&#41;", ")") ?? "";
                        }

                        // QQ 音乐把译文放在同一次响应的 trans 字段里（与 lyric 同格式、同时间戳）
                        if (lyricDoc.RootElement.TryGetProperty("trans", out var transEl))
                        {
                            transText = transEl.GetString()?
                                .Replace("&#10;", "\n").Replace("&#13;", "\r")
                                .Replace("&#32;", " ").Replace("&#45;", "-")
                                .Replace("&#40;", "(").Replace("&#41;", ")") ?? "";
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn($"QQ音乐引擎失败: {ex.Message}"); }

                // ====== 引擎 2：网易云 API ======
                if (string.IsNullOrEmpty(lrcText))
                {
                    try
                    {
                        _http.DefaultRequestHeaders.Clear();
                        _http.DefaultRequestHeaders.Add("User-Agent", ua);
                        _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com");
                        _http.DefaultRequestHeaders.Add("X-Real-IP", $"114.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}");

                        var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("s", $"{title} {artist}"),
                            new KeyValuePair<string, string>("type", "1"),
                            new KeyValuePair<string, string>("limit", "5"),
                            new KeyValuePair<string, string>("offset", "0")
                        });

                        var response = await _http.PostAsync("https://music.163.com/api/search/get/web", content);
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
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"网易云引擎失败: {ex.Message}"); }
                }

                // ====== 引擎 3：LRCLIB ======
                if (string.IsNullOrEmpty(lrcText))
                {
                    try
                    {
                        _http.DefaultRequestHeaders.Clear();
                        _http.DefaultRequestHeaders.Add("User-Agent", ua);
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

                // ====== 译文补抓：落月 API ======
                // 前面的引擎都没给出可用的译文时（要么整段为空，要么拿到的全是 `//` 这类占位），
                // 用 QQ 搜索命中的 songmid 去落月 API 再要一份。只补译文，不参与歌词正文的获取 ——
                // 它返回的 trans 与 QQ 官方歌词同源同时间戳，解析后能按时间戳精确贴到原文行上。
                if (!string.IsNullOrEmpty(qqSongmid) && BuildTransTable(transText).Length == 0)
                {
                    string? fallbackTrans = await FetchTransFromLuoYueAsync(qqSongmid, ua);
                    if (!string.IsNullOrEmpty(fallbackTrans)) transText = fallbackTrans;
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
                            if (TimeSpan.TryParseExact(line.Substring(1, idx - 1), new[] { @"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss" }, null, out var ts))
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
            }
            finally
            {
                // 必须释放锁，让下一首歌可以正常获取
                _fetchLock.Release();
            }
        }

        // 落月 API 译文源。主域名不通时自动退回备用域名（官方文档给出的容灾域名）。
        private static readonly string[] LuoYueHosts = { "https://api.vkeys.cn", "https://api.epdd.cn" };

        /// <summary>
        /// 按 QQ songmid 向落月 API 要一份译文 LRC。**只用于译文**，不参与歌词正文获取：
        /// 它返回的 trans 与 QQ 官方歌词同源同时间戳，解析后能按时间戳精确贴到原文行上。
        /// 拿不到（网络异常 / 曲库无此曲 / code 非 200）一律返回 null，调用方保持「无译文」的单行显示。
        /// </summary>
        private async Task<string?> FetchTransFromLuoYueAsync(string mid, string ua)
        {
            foreach (var host in LuoYueHosts)
            {
                try
                {
                    _http.DefaultRequestHeaders.Clear();
                    _http.DefaultRequestHeaders.Add("User-Agent", ua);

                    using var stream = await _http.GetStreamAsync($"{host}/v2/music/tencent/lyric?mid={Uri.EscapeDataString(mid)}");
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var root = doc.RootElement;

                    // code != 200 视为这个域名没戏，但换个域名还有救，所以继续循环
                    if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out int code) && code != 200) continue;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                    if (!data.TryGetProperty("trans", out var transEl)) continue;

                    string trans = transEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(trans)) return trans;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"落月API译文获取失败({host}): {ex.Message}");
                }
            }
            return null;
        }

        // 译文行与原文行的时间戳允许的最大偏差。两个源（QQ 官方 trans / 落月 API）与歌词正文
        // 偶尔会差个几十毫秒（例：[00:44.48] 橡皮擦… vs [00:44.56] 消しゴムが…），
        // 只认精确相等的话这些行会白白丢掉译文；放到 300ms 又不会串到隔壁句（正常行距都是秒级）。
        private const long TransMatchToleranceTicks = 300L * TimeSpan.TicksPerMillisecond;

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

            return Math.Abs(table[cursor].Ticks - ticks) <= TransMatchToleranceTicks ? table[cursor].Text : "";
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