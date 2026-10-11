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
        internal static bool IsManualSessionMatch = false;
        internal static string ManualSessionAppId = "";
        internal static bool HasActiveSessions { get; private set; }
        // 注册表里有值的照旧优先，不会被这次改默认值影响。
        internal static bool IsAppLaunchEnabled = false;

        internal static bool IsLyricsEnabled = true;
        internal static bool IsLyricScanEnabled = true;

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

        private LyricWordTiming?[]? _lyricWordTimings;

        private bool _lyricsHasTranslation;
        public string CurrentLyric { get; private set; } = "";
        public string CurrentLyricTranslation { get; private set; } = "";
        public bool HasLyricTranslation { get; private set; }
        public float CurrentLyricProgress { get; private set; } = 0f;
        private DateTime _seekSettleUntil = DateTime.MinValue;
        private const double SeekSettleSeconds = 1.5;

        private TimeSpan _prevSmtcPos;
        private bool _hasPrevSmtcPos;
        private bool _timelineAhead;
        // 初值取负无穷 ⇒ 首次采样永不触发安全阀。
        private double _prevDelta = double.NegativeInfinity;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private string _lastFetchedTitle = "";
        private string _lastFetchedArtist = "";

        // ---- 取词失败的重试记账 ----
        // 这几类瞬时原因，又不会在真的没有歌词源时无限刷请求。
        private const int LyricRetryMax = 3;
        private static readonly TimeSpan LyricRetryInterval = TimeSpan.FromSeconds(2);
        private int _lyricRetryCount;
        private DateTime _lastLyricRetryAt = DateTime.MinValue;

        private bool TryBeginLyricRetry()
        {
            if (_lyricRetryCount >= LyricRetryMax) return false;

            var now = DateTime.UtcNow;
            if (now - _lastLyricRetryAt < LyricRetryInterval) return false;

            _lyricRetryCount++;
            _lastLyricRetryAt = now;
            return true;
        }

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

        private string _externalCoverAppId = "";
        private string _externalCoverTitle = "";

        // ---- 封面：按「曲目世代」管理；网络封面 > SMTC ----
        // 换曲时 _coverGen +1；所有异步封面回写都带上世代号，回来对不上就丢弃，
        // 从根上杜绝「上一首的封面贴到新歌上」，也不再依赖歌名字符串比较。
        private int _coverGen;
        private int _coverPendingGen = -1;            // 当前仍在找封面的世代（两侧只写同一个值，无竞争）
        private volatile bool _coverPending;          // 该世代是否还没定局
        private int _coverFetching;                   // 同一时刻只允许一条 SMTC 封面链路
        private int _networkCoverGen = -1;            // 网络封面已经贴上的世代（它比 SMTC 权威）

        // SMTC 封面探测节拍：先密后疏。很多播放器是「标题先变、封面后到」，
        // 密档负责秒贴，疏档负责接住迟到的封面（顺便纠正上一首的残留图）。
        private static readonly int[] SessionCoverProbeMs = [0, 250, 250, 250, 250, 250, 250, 1000, 2000, 2000];

        // 读到同一张图至少要稳住这么久才敢收工。⚠️ 别用「连续两轮相同」当判据（只要 250ms）：
        // 播放器换曲瞬间给的往往还是上一首的封面，250ms 后它才更新 —— 早收工就把残留图钉死了。
        private const int SessionCoverSettleMs = 1500;

        private DateTime _lastCoverLogAt = DateTime.MinValue;
        private void CoverLog(string message)
        {
            var now = DateTime.UtcNow;
            if (now - _lastCoverLogAt < TimeSpan.FromSeconds(3)) return;   // 采样式日志，别刷屏
            _lastCoverLogAt = now;
            Logger.Debug(message);
        }

        // ---- 「标题还没就绪」的兜底重试 ----
        //      直到用户手动暂停一次才「自己好了」。
        // 一次都不会来，指望事件自愈是不可能的。
        private const int TitleRetryMax = 10;
        private static readonly TimeSpan TitleRetryFirstDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan TitleRetryInterval = TimeSpan.FromSeconds(2);
        private int _titleRetryCount;
        private DateTime _titleRetryNextAt = DateTime.MinValue;
        private volatile bool _titleRetryResetPending;

        private string _appNameCacheKey = "";
        private string _appNameCacheValue = "";

        private static readonly char[] PathSeparators = ['\\', '/'];

        private string _appIconKey = "";

        // 定长环形缓冲，只存字符串引用与值类型，零分配。
        private const int RecentSongSlots = 4;
        private readonly (string Title, string Artist, TimeSpan Position, DateTime TickedAt, string AppId)[] _recentSongs
            = new (string, string, TimeSpan, DateTime, string)[RecentSongSlots];
        private const double FreshSlotSeconds = 3.0; // 后台会话每秒采样一次，留足调度抖动余量
        private int _recentCursor;
        // 当前歌词与时间轴归属的槽位，-1 表示尚未接管。
        private volatile int _lyricSlot = -1;

        // 「退到后台但仍在播放」的会话，按槽位登记。
        // 每秒采样一次播放状态，60FPS 下不产生额外开销。
        private readonly GlobalSystemMediaTransportControlsSession?[] _slotSessions
            = new GlobalSystemMediaTransportControlsSession?[RecentSongSlots];
        private readonly DateTime[] _slotSampleAt = new DateTime[RecentSongSlots];

        // 新歌的进度条与歌词最多慢 200ms 才开始对齐。
        private volatile bool _forceResync;

        private volatile string _currentAppId = "";

        public string CurrentAppId => _currentAppId;

        // ---- 歌曲时间轴 ----
        // 「已播放 mm:ss」显示真实值，只是没有进度条。
        public bool HasTimeline { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string TimelineElapsed { get; private set; } = "0:00";
        public string TimelineTotal { get; private set; } = "0:00";
        private TimeSpan _timelinePos;

        private volatile bool _isDragging;
        public bool IsDragging => _isDragging;

        private int _shownSec = -1, _shownTotal = -1;

        private const double SmtcProbeIntervalSec = 0.2;
        private DateTime _smtcProbeAt = DateTime.MinValue;   // 只由后台采样时钟读写
        private TimeSpan _smtcPos = TimeSpan.Zero;
        private TimeSpan _smtcDuration = TimeSpan.Zero;

        // 自由跑表的虚拟时间轴。
        private bool _smtcHasLastUpdated;   // 本份快照的 LastUpdatedTime 是否可信
        private DateTime _smtcLastUpdatedUtc; // 上一份快照的 LastUpdatedTime（UTC）

        // 后台会话 1 秒一采。
        private System.Threading.Timer? _smtcSampler;
        private const int SmtcSamplerTickMs = 50;   // 采样节拍：50ms 一跳，真正的采样仍按上面两条节流
        private int _smtcSampleVersion;             // 后台写 / 渲染线程读 → 一律 Volatile / Interlocked
        private int _consumedSampleVersion;         // 渲染线程上次消费到的版本号
        private int _sampling;                      // 采样重入闸（见 SampleSmtcTick）
        private DateTime _suspendedProbeAt = DateTime.MinValue;   // 后台线程独占

        // 「僵尸会话」判定（见 Media/SessionValidity）：被判残留的 App 在会话选择里整体跳过，
        // 直到它重新开始播放、或视频窗口回来。后台采样线程写、其余线程读 → volatile。
        private volatile string _staleAppId = "";

        private string _staleTitle = "";                       // 判残留时的标题，恢复检测拿它比对（后台线程独占）

        private DateTime _staleProbeAt = DateTime.MinValue;    // 后台线程独占

        // 残留判定的采样间隔：250ms × 迟滞 2 次 ≈ 0.5 秒出结果。
        // 这段就是「关掉视频 → 岛体清空」的观感延迟；判定只在非播放态才跑，播放中零成本。
        private const int StaleProbeIntervalMs = 250;

        // 已判残留之后的「恢复」轮询间隔：降到 1 秒。
        // 恢复不靠它顶着 —— 新会话一播放就会触发 OnPlaybackInfoChanged 立刻补一刀，
        // 这里只是保底，没必要 250ms 一轮地读会话属性。
        private const int StaleRecoverIntervalMs = 1000;

        private readonly bool[] _suspendedPlaying = new bool[RecentSongSlots];

        private readonly bool[] _suspendedDead = new bool[RecentSongSlots];

        public float TimelineProgress => Duration > TimeSpan.Zero
            ? Math.Clamp((float)(_timelinePos.TotalSeconds / Duration.TotalSeconds), 0f, 1f) : 0f;

        public string Title { get; private set; } = "Notch Peninsula";
        public string Artist { get; private set; } = "Waiting for media...";

        // 本类的写入方与读取方跑在不同线程上：
        //     落在线程池线程上；
        private volatile bool _isPlaying;
        public bool IsPlaying => _isPlaying;

        private volatile bool _isActive;
        public bool IsActive => _isActive;

        private volatile SKBitmap? _thumbnail;
        public SKBitmap? Thumbnail => _thumbnail;

        private readonly object _thumbSwap = new();

        // ---- 封面位图的内存上限 ----
        private const int CoverMaxEdge = 160;

        private const long CoverMaxBytes = 8L * 1024 * 1024;

        private static readonly SKPaint _coverScalePaint = new() { FilterQuality = SKFilterQuality.High };

        private const int RetiredThumbKeep = 8;
        private readonly Queue<SKBitmap> _retiredThumbs = new();

        private void RetireThumbnail(SKBitmap? bmp)
        {
            if (bmp == null) return;
            _retiredThumbs.Enqueue(bmp);
            while (_retiredThumbs.Count > RetiredThumbKeep)
                _retiredThumbs.Dequeue().Dispose();
        }

        private void SetThumbnail(SKBitmap? next, string tag)
        {
            lock (_thumbSwap)
            {
                if (ReferenceEquals(_thumbnail, next)) return;
                RetireThumbnail(_thumbnail);
                _thumbnail = next;
                _appIconKey = "";
            }
            Logger.Debug($"[封面] {tag} → {(next == null ? "null" : $"{next.Width}x{next.Height}")}  曲目='{_trackTitle}'");
        }

        private void SetAppIcon()
        {
            if (_appIconKey.Length > 0 && string.Equals(_appIconKey, _currentAppId, StringComparison.Ordinal)) return;

            var icon = AppIconProvider.Get(_currentAppId);
            if (icon == null) return;      // 没图标：保持当前那张，别清空

            SetThumbnail(icon, "图标");    // 内部会把 _appIconKey 清空，所以这一步必须排在下面那行之前
            _appIconKey = _currentAppId;
        }

        private bool IsVideoMode => !_isMusicMode;

        private bool IsNonLyricSession => IsVideoMode;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // 已挂上播放状态监听的会话（按 AppID 记账）。
        private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watchedSessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _watcherLock = new();
        private bool _isBilibiliSession;  // 当前会话是否为 bilibili，用于清空 Artist（视频模式只显示标题）
        private bool _isMusicMode;

        //    歌手只在同曲目内拿到非空值时才更新。
        private string _trackAppId = "";
        private string _trackTitle = "";
        private string _trackArtist = "";
        private int _musicModeMisses;
        private const int MusicModeMissGrace = 3;
        private bool _isBrowserSession;   // 当前会话是否为浏览器 (Chrome/Edge)，启用视频标题清理
        private bool _isJustSoloSession;  // 当前会话是否为 Just Solo，启用 LyricServer 直连歌词
        private bool _isVideoIconOnlySession; // 视频模式下只用应用图标（会话缩略图是视频截图，不是封面）
        private readonly JustSoloLyricClient _justSoloLyric = new();

        public MediaController()
        {
            Instance = this;
            StartSmtcSampler();
            _ = InitializeAsync();
        }

        private void StartSmtcSampler()
        {
            _smtcSampler ??= new System.Threading.Timer(_ => SampleSmtcTick(), null,
                SmtcSamplerTickMs, SmtcSamplerTickMs);
        }

        private void StopSmtcSampler()
        {
            try { _smtcSampler?.Dispose(); } catch { }
            _smtcSampler = null;
        }

        private void SampleSmtcTick()
        {
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

                // 残留判定必须独立于会话增删事件：客户端关视频时不产生任何 SMTC 通知，
                // 光靠 SessionsChanged 永远发现不了这个假会话。
                // 频率比暂停采样高一档：这段延迟直接等于「关掉视频到岛体清空」的观感延迟。
                // 未判残留（清除路径）保持 250ms；已判残留（恢复路径）降到 1 秒 ——
                // 恢复有事件驱动兜底，轮询只是保底，不必 250ms 一轮地读会话属性。
                // 注意 _staleProbeAt 要无条件推进，否则非目标场景每 50ms 都会重算一次门槛。
                int staleInterval = _staleAppId.Length > 0 ? StaleRecoverIntervalMs : StaleProbeIntervalMs;
                if ((now - _staleProbeAt).TotalMilliseconds >= staleInterval)
                {
                    _staleProbeAt = now;
                    // 到点才查入口：非白名单会话（绝大多数时候）连判定都不进，也不碰 WinRT 属性。
                    bool staleRelevant = _staleAppId.Length > 0
                        || (_currentSession != null && SessionValidity.IsTrackedApp(_currentSession.SourceAppUserModelId));
                    if (staleRelevant) _ = CheckStaleSessionAsync();
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

        // 判定并忽略「僵尸会话」：客户端关了视频却不注销 SMTC 会话，系统媒体控件里就留着一个
        // 点不动的假媒体。判据与实测数据见 Media/SessionValidity.cs。
        // 这里只做「宿主内部忽略」，不去清系统会话（消费侧没有那个能力）。
        //
        // 异步 + 防重入：恢复判定要读会话标题来确认「内容真的换了」，由后台采样线程调用。
        private int _staleChecking;

        private async Task CheckStaleSessionAsync()
        {
            if (Interlocked.Exchange(ref _staleChecking, 1) == 1) return;
            try
            {
                await CheckStaleSessionCore();
            }
            catch (Exception ex)
            {
                Logger.Debug($"残留会话判定异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _staleChecking, 0);
            }
        }

        private async Task CheckStaleSessionCore()
        {
            if (!IsMediaControlEnabled)
            {
                // 媒体接管关掉后残留判定没有意义，顺手把状态清干净，免得下次开启时带着旧判定回来
                if (_staleAppId.Length > 0)
                {
                    _staleAppId = "";
                    _staleTitle = "";
                    SessionValidity.Reset();
                }
                return;
            }

            var manager = _manager;
            if (manager == null) return;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = manager.GetSessions(); }
            catch { return; }

            string staleId = _staleAppId;

            if (staleId.Length > 0)
            {
                // 已判残留：只找「恢复」的证据。关键是必须确认「内容真的换了」——
                // B站换视频的时序是「旧会话先抢报 Playing / 或整段消失，窗口标题立即变新，
                // 新会话 2~3 秒后才带着新标题回来」。只凭 Playing 或会话在不在都放行得太早，
                // 结果就是先把上一个视频的标题闪出来（实测持续 2.1 秒）。
                bool found = false;
                bool alive = false;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (!string.Equals(s.SourceAppUserModelId, staleId, StringComparison.OrdinalIgnoreCase)) continue;

                    found = true;
                    try
                    {
                        if (_staleTitle.Length > 0
                            && SessionValidity.HasVisibleWindowWithTitle(staleId, _staleTitle))
                        {
                            // 便宜的一刀：窗口还挂着残留时的标题 → 内容确实在场
                            //（重播同一视频 / 暂停 + 最小化），直接算恢复，省掉一次读属性。
                            alive = true;
                        }
                        else
                        {
                            // 否则才去读标题，确认是换了新内容、还是旧会话抢报 Playing。
                            string cur = (await s.TryGetMediaPropertiesAsync())?.Title ?? "";
                            bool newContent = cur.Length > 0
                                && !string.Equals(cur, _staleTitle, StringComparison.Ordinal);

                            if (newContent)
                            {
                                // 换了内容：在播，或新内容的窗口已经可见 → 算恢复。
                                //（后者兜住「点开视频但没自动播」。）
                                alive = IsSessionPlaying(s)
                                    || SessionValidity.HasVisibleWindowWithTitle(staleId, cur);
                            }
                            // 标题仍是残留时那个 → 继续忽略
                        }
                    }
                    catch { return; }   // 读不出来就维持现状，下一轮再判
                    break;
                }

                if (found && !alive) return;   // 会话还在、内容也没换 → 继续忽略

                // 会话整段消失：B站换视频会先销毁旧会话、新会话稍后才带新标题回来。
                // 该进程窗口还在就说明不是真退出 —— 继续忽略，免得用旧标题闪一下。
                if (!found && SessionValidity.HasVisibleWindow(staleId)) return;

                Logger.Debug($"[媒体] 残留会话恢复（{staleId}）");
                _staleAppId = "";
                _staleTitle = "";
                SessionValidity.Reset();
                if (_manager != null) _ = UpdateSession(_manager);
                return;
            }

            // 未判残留：只看当前会话，播放中一律不判
            var current = _currentSession;
            if (current == null) return;

            string appId = current.SourceAppUserModelId ?? "";
            if (!SessionValidity.IsTrackedApp(appId)) return;
            if (_trackTitle.Length == 0) return;

            bool stale;
            try
            {
                if (IsSessionPlaying(current))
                {
                    SessionValidity.NoteAlive(appId);
                    return;
                }
                stale = SessionValidity.IsStale(appId, _trackTitle);
            }
            catch { return; }

            if (!stale) return;

            _staleAppId = appId;
            _staleTitle = _trackTitle;
            Logger.Debug($"[媒体] 判定会话残留（{appId}）→ 忽略，标题='{_trackTitle}'");
            if (_manager != null) _ = UpdateSession(_manager);
        }

        private void SampleCurrentSession()
        {
            var session = _currentSession;
            if (session == null) return;

            try
            {
                var t = session.GetTimelineProperties();
                _smtcPos = t.Position;

                _smtcDuration = t.EndTime > TimeSpan.Zero ? t.EndTime
                    : t.MaxSeekTime > TimeSpan.Zero ? t.MaxSeekTime
                    : TimeSpan.Zero;

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
            }
            if (_smtcDuration <= TimeSpan.Zero) _forceResync = false;

            //   等它落到 _isPlaying 已是几百毫秒之后。
            try
            {
                var info = session.GetPlaybackInfo();
                if (info != null)
                    _isPlaying = info.PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { }
        }

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

        public void Shutdown()
        {
            _shuttingDownMedia = true;
            StopSmtcSampler();
            try { _justSoloLyric.Stop(); } catch { }
            try { SetThumbnail(null, "退出"); } catch { }
        }

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

        private static bool IsManualLockActive =>
            IsMediaControlEnabled && TargetPlatform == "other" && IsManualSessionMatch && ManualSessionAppId.Length > 0;

        private int _updatingSession;
        private volatile bool _sessionUpdatePending;

        private volatile bool _shuttingDownMedia;

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

        private async Task UpdateSessionCore(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? newSession = null;

            var sessions = manager.GetSessions();
            // 判为残留的会话整体不参与：既不进候选，也不算「有媒体」——否则岛体仍会挂在假会话上
            string staleId = _staleAppId;
            bool hasActiveSessions = false;
            for (int i = 0; i < sessions.Count; i++)
            {
                string id = sessions[i].SourceAppUserModelId ?? "";
                if (id.Length == 0 || IsGloballyBlockedApp(id)) continue;
                if (staleId.Length > 0 && string.Equals(id, staleId, StringComparison.OrdinalIgnoreCase)) continue;
                hasActiveSessions = true;
            }
            HasActiveSessions = hasActiveSessions;

            SyncPlaybackWatchers(sessions);

            // 如果总开关打开，执行精确的平台过滤
            if (IsMediaControlEnabled)
            {
                // （全局屏蔽的软件除外，手动也不允许锁定它）
                if (IsManualLockActive)
                {
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        string id = sessions[i].SourceAppUserModelId ?? "";
                        if (IsGloballyBlockedApp(id)) continue;
                        // 手动锁定也要过这道闸，否则用户手选了 B站就再也没法摆脱那个假会话
                        if (staleId.Length > 0 && string.Equals(id, staleId, StringComparison.OrdinalIgnoreCase)) continue;
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
                    bool skipPaused = TargetPlatform == "other";
                    GlobalSystemMediaTransportControlsSession? pausedFallback = null;
                    int pausedFallbackRank = 0;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        string id = s.SourceAppUserModelId ?? "";
                        if (id.Length == 0) continue;
                        if (staleId.Length > 0 && string.Equals(id, staleId, StringComparison.OrdinalIgnoreCase)) continue;

                        int rank = SessionRank(id);
                        if (rank == 0) continue;                  // 不看好的会话，零成本跳过
                        if (skipPaused && !IsSessionPlaying(s))
                        {
                            if (rank > pausedFallbackRank) { pausedFallbackRank = rank; pausedFallback = s; }
                            continue;
                        }

                        if (rank == 3) { newSession = s; break; }  // 正在播放的 Just Solo 压过一切，立即锁定
                        if (newSession == null) newSession = s;    // 备选，继续往后扫，遇到 Just Solo 会被顶掉
                    }
                    if (newSession == null) newSession = pausedFallback;
                }
            }

            bool wasNonLyric = IsNonLyricSession;
            _currentAppId = newSession?.SourceAppUserModelId ?? "";

            _isBilibiliSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Bilibili");
            _isBrowserSession = MediaLogoProvider.IsBrowser(newSession?.SourceAppUserModelId);
            _isJustSoloSession = newSession?.SourceAppUserModelId?.Contains("justsolo", StringComparison.OrdinalIgnoreCase) == true;
            _isVideoIconOnlySession = MediaLogoProvider.IsVideoIconOnly(newSession?.SourceAppUserModelId);

            if (IsAppLaunchEnabled) MediaAppLauncher.CaptureSession(newSession);

            UpdateJustSoloConnection();

            if (_currentSession != null && newSession != null && _currentSession.SourceAppUserModelId == newSession.SourceAppUserModelId)
            {
                await RefreshProperties();
                _isActive = true;
                return;
            }

            if (!wasNonLyric && _currentSession != null && _lyricSlot >= 0 && _slotSessions[_lyricSlot] == null)
            {
                _slotSessions[_lyricSlot] = _currentSession;
                _slotSampleAt[_lyricSlot] = DateTime.UtcNow; // 以切换时刻为起点，避免首次采样吃进一段巨大的时间差
            }

            // 切换到了新的会话（或者置空）
            if (_currentSession != null)
            {
                // 切换前，必须先解绑旧会话的事件，防止幽灵对象吃内存
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }

            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;

                await RefreshProperties();
                _isActive = true;
            }
            else
            {
                _isActive = false;
                Title = "No Media";
                Artist = "";
                _isPlaying = false;
                InvalidateCover();
            }
        }

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
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    string id = session.SourceAppUserModelId ?? "";
                    if (id.Length == 0) continue;

                    if (_watchedSessions.TryGetValue(id, out var watched))
                    {
                        if (ReferenceEquals(watched, session)) continue; // 就是同一个对象，已订阅
                        try { watched.PlaybackInfoChanged -= OnPlaybackInfoChanged; } catch { }
                    }

                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _watchedSessions[id] = session;
                }
            }
        }

        private static int SessionRank(string id)
        {
            if (IsGloballyBlockedApp(id)) return 0;

            if (TargetPlatform == "browser")
                return MediaLogoProvider.IsBrowser(id) ? 3 : 0;

            if (TargetPlatform == "other")
                return id.Contains("justsolo", StringComparison.OrdinalIgnoreCase) ? 3 : 2;

            return MatchesTargetPlatform(id) ? 3 : 0;
        }

        private static bool IsGloballyBlockedApp(string id) =>
            (id.Contains("douyin", StringComparison.OrdinalIgnoreCase)
             || id.Contains("wechatappex", StringComparison.OrdinalIgnoreCase))
            && !id.Contains("justsolo", StringComparison.OrdinalIgnoreCase);

        private static bool IsNeteaseAppId(string? id)
            => id != null
               && (id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("netease", StringComparison.OrdinalIgnoreCase));

        // 目标平台与会话 AppID 的匹配规则（单一数据源）。
        private static bool MatchesTargetPlatform(string id) => TargetPlatform switch
        {
            "netease" => IsNeteaseAppId(id),
            "qqmusic" => id.Contains("qqmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("tencent", StringComparison.OrdinalIgnoreCase),
            "applemusic" => id.Contains("apple", StringComparison.OrdinalIgnoreCase) && id.Contains("music", StringComparison.OrdinalIgnoreCase),
            "lxmusic" => id.Contains("cn.toside.music.desktop", StringComparison.OrdinalIgnoreCase) || id.Contains("lxmusic", StringComparison.OrdinalIgnoreCase),
            _ => id.Contains(TargetPlatform, StringComparison.OrdinalIgnoreCase),
        };

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

        private int _refreshingProperties;

        private async Task RefreshProperties()
        {
            if (_currentSession == null) return;

            if (Interlocked.Exchange(ref _refreshingProperties, 1) == 1) return;
            try
            {
                await RefreshPropertiesCore();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshingProperties, 0);
            }
        }

        private async Task RefreshPropertiesCore()
        {
            try
            {
                var props = await _currentSession!.TryGetMediaPropertiesAsync();
                if (props != null)
                {
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

                    // 标题纠偏（只对白名单客户端）：换视频后 SMTC 标题会滞后 2~3 秒，
                    // 而视频窗口标题是即时更新的 —— 滞后这段就是「岛体先闪一下上一个视频」的来源。
                    // SMTC 标题在可见窗口里找不到落地佐证、而内容窗口有明确标题时，以内容窗口为准。
                    if (SessionValidity.InspectLiveWindows(_currentSession!.SourceAppUserModelId, smtcTitle,
                            out bool titleConfirmed, out string liveTitle)
                        && !titleConfirmed && liveTitle.Length > 0)
                    {
                        smtcTitle = liveTitle;
                    }

                    UpdateMediaMode(smtcTitle, smtcArtist);
                    UpdateCover();
                }
            }
            catch (Exception ex)
            {
                // 属性读失败：只记日志，一个显示状态都不动。
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

        private void UpdateMediaMode(string smtcTitle, string smtcArtist)
        {
            // 外部媒体源（网易云 / 酷狗）接管标题：这两个参数就是 SMTC 那点脏数据的来源，
            // 而插件从页面里读到的歌名 / 歌手要准得多。接管只在「SMTC 给不出时间轴」时成立，
            // 否则 smtcTitle / smtcArtist 原样不动，本方法行为与从前完全一致。
            if (TryGetExternalTrack(_currentAppId, _smtcDuration, out string extTitle, out string extArtist))
            {
                smtcTitle = extTitle;
                smtcArtist = extArtist;
            }

            bool appChanged = !string.Equals(_trackAppId, _currentAppId, StringComparison.Ordinal);
            bool titleChanged = smtcTitle.Length > 0
                && !string.Equals(_trackTitle, smtcTitle, StringComparison.Ordinal);

            if (appChanged)
            {
                _trackAppId = _currentAppId;
                _trackTitle = smtcTitle;
                _trackArtist = smtcArtist;
                _musicModeMisses = 0;
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
            }
            else
            {
                _isMusicMode = false;
            }

            if (appChanged || titleChanged)
            {
                _titleRetryResetPending = true;
                InvalidateCover();   // 换曲：立刻丢掉旧封面
            }

            Title = _trackTitle.Length > 0 ? _trackTitle : "Unknown";
            Artist = _isMusicMode ? _trackArtist : ""; // 视频模式：只要标题，歌手不要
        }

        // 换曲：当场作废旧封面。宁可空一格 / 先出应用图标，也绝不把上一首的图留在屏上。
        private void InvalidateCover()
        {
            int gen = Interlocked.Increment(ref _coverGen);
            _coverPending = false;
            _externalCoverTitle = "";
            _externalCoverAppId = "";
            Logger.Debug($"[封面] 换曲作废 gen={gen} 原图={(Thumbnail == null ? "null" : "有")} 新曲目='{_trackTitle}'");
            SetThumbnail(null, "换曲清空");
        }

        private DateTime _lastCoverDiagAt = DateTime.MinValue;

        private void UpdateCover()
        {
            int gen = Volatile.Read(ref _coverGen);
            if (gen != _coverPendingGen)
            {
                _coverPendingGen = gen;
                _coverPending = true;
            }

            // 兜底图：只在完全没有封面时补位（也负责把异步解析到的图标最终贴上）。
            // SetAppIcon 自带「同一 AUMID 只贴一次」的闸，不会每帧复制位图；
            // ⚠️ 条件必须是 Thumbnail == null —— 用 _appIconKey 判定会在封面刚贴上时把它盖成应用图标。
            if (Thumbnail == null) SetAppIcon();

            if (!_coverPending) return;                  // 本世代已定局：不再重试
            if (Volatile.Read(ref _networkCoverGen) == gen) { _coverPending = false; return; }

            // 视频模式下 PotPlayer 这类播放器：会话缩略图是视频截图而不是封面，直接用应用图标。
            // 放在启动探测之前，SMTC 图根本没有机会贴上来；上面那一档若已贴过图标，这里的 SetAppIcon 会因
            // _appIconKey 命中而直接返回，不产生多余的位图拷贝。
            if (IsVideoMode && _isVideoIconOnlySession)
            {
                SetAppIcon();
                _coverPending = false;
                return;
            }

            if (_currentSession == null || _trackTitle.Length == 0) return;
            if (Volatile.Read(ref _coverFetching) == 1) return;

            _ = FetchSmtcCoverAsync(_trackTitle, gen, _currentSession);
        }

        // 诊断：只在「本世代迟迟没有封面」时每秒打一行，说明卡在哪一环。
        private void DiagCoverIfStuck(DateTime now)
        {
            if (Thumbnail != null || !_coverPending) return;
            if (now - _lastCoverDiagAt < TimeSpan.FromSeconds(1)) return;
            _lastCoverDiagAt = now;
            Logger.Debug($"[封面] 未就位 gen={_coverGen} pendingGen={_coverPendingGen} fetching={Volatile.Read(ref _coverFetching)} session={(_currentSession == null ? "null" : "ok")} 曲目='{_trackTitle}'");
        }

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

        private bool IsTitleNotReady()
        {
            if (_isBrowserSession) return false;

            string title = _trackTitle;
            if (title.Length == 0) return true;
            // 误判却会让一首正常的歌被反复重读属性
            if (title.Length < 4) return false;

            string appName = AppNameFromAppId();
            if (appName.Length < 3) return false;

            return title.Contains(appName, StringComparison.OrdinalIgnoreCase)
                || appName.Contains(title, StringComparison.OrdinalIgnoreCase);
        }

        private void RetryTitleIfNeeded(DateTime now)
        {
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

        public void OpenCurrentApp()
        {
            if (!IsAppLaunchEnabled) return;
            MediaAppLauncher.OpenCurrentSessionApp();
        }

        public bool TryGetSoloSpectrum(out float[] bands) => _justSoloLyric.TryGetSpectrum(out bands);

        public bool TrySyncVolumeToJustSolo(float level)
        {
            if (!_justSoloLyric.IsConnected) return false;
            _justSoloLyric.SendVolume(level);
            return true;
        }

        public bool TryGetJustSoloVolume(out float volume) => _justSoloLyric.TryGetVolume(out volume);

        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshProperties();
        }

        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            // 残留状态下播放器只要有播放动作就得立刻响应，不必等 1 秒的轮询档
            if (_staleAppId.Length > 0) _ = CheckStaleSessionAsync();

            // 只刷属性会让界面停在那个已经暂停的会话上。
            if (_manager != null && IsMediaControlEnabled && TargetPlatform == "other" && !IsManualSessionMatch)
            {
                await UpdateSession(_manager);
                return;
            }

            if (string.Equals(sender.SourceAppUserModelId, _currentAppId, StringComparison.OrdinalIgnoreCase))
                await RefreshProperties();
        }

        // ---- 媒体资源获取（歌词 + 封面） ----
        // 「取词」与「取封面」是两件独立的事，各自成一个函数：

        private async Task FetchMediaAsync(string title, string artist, long durationSec)
        {
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

            if (IsLyricOwner(title, artist))
            {
                ReleaseSuspension();
                _forceResync = true;
                return;
            }

            // ---- 换歌：强制重载歌词与时间轴。----
            string appId = _currentAppId;
            int newSlot = SlotFor(title, artist);

            for (int i = 0; i < RecentSongSlots; i++)
            {
                if (i == newSlot || _slotSessions[i] == null) continue;
                if (_recentSongs[i].AppId == appId) _slotSessions[i] = null;
            }

            if (newSlot >= 0)
            {
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

            try
            {
                string coverUrl = await FetchLyricsAsync(title, artist, durationSec);
                await FetchCoverAsync(title, artist, coverUrl);
            }
            catch (Exception ex)
            {
                // 落在这里至少带上了歌名 / 歌手，下一次一眼就能对上。
                Logger.Error($"取词/封面链异常（{title} / {artist}）", ex);
            }
        }

        private void RetryLyricsIfNeeded()
        {
            // 视频模式（无歌手）本来就不取歌词，重试没有意义
            if (IsNonLyricSession) return;
            if (_lyrics.Length > 0) return;

            int slot = _lyricSlot;
            if (slot < 0 || slot >= RecentSongSlots) return;

            // 暂停时不刷网络请求，等真正播放起来再说
            if (!_isPlaying) return;

            var owner = _recentSongs[slot];
            if (string.IsNullOrEmpty(owner.Title) || string.IsNullOrEmpty(owner.Artist)) return;
            // 屏上已经不是这首歌了（槽位还没被改写，但会话已切走）
            if (!string.Equals(owner.Title, _trackTitle, StringComparison.Ordinal)) return;

            if (!TryBeginLyricRetry()) return;

            Logger.Debug($"取词失败，第 {_lyricRetryCount} 次重试（{owner.Title} / {owner.Artist}）");
            _ = Task.Run(() => RetryFetchAsync(owner.Title, owner.Artist));
        }

        private async Task RetryFetchAsync(string title, string artist)
        {
            try
            {
                string coverUrl = await FetchLyricsAsync(title, artist, CurrentTimelineSeconds());
                await FetchCoverAsync(title, artist, coverUrl);
            }
            catch (Exception ex)
            {
                Logger.Error($"取词重试链异常（{title} / {artist}）", ex);
            }
        }

        private async Task<string> FetchLyricsAsync(string title, string artist, long durationSec)
        {
            await _fetchLock.WaitAsync();
            try
            {
                // 但那首歌并没有被换掉，此时不该丢弃请求。
                if (!IsLyricOwner(title, artist)) return "";

                string lrcText = "";
                string transText = "";
                // 逐字也不采纳，免得出现「逐字时间轴属于另一首歌」。
                string yrcText = "";
                bool yrcTimingFirst = false;
                // 仅音乐模式会用；都没拿到就保持兜底封面（程序图标）。
                string coverUrl = "";

                bool luoYueOnly = IsLyricScanEnabled;

                // 歌词与封面都优先网易云的源；其余播放器一切照旧。
                // 「只走落月两档」时不存在这个前置。
                bool isNetease = IsNeteaseAppId(_currentAppId);
                bool preferNetease = !luoYueOnly && isNetease;

                bool neteaseWordSource = luoYueOnly && isNetease;

                if (preferNetease)
                {
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

                //    同为 QQ 曲库，一次响应把四样东西给齐：
                // 于是网易云先请、QQ 只在还缺料时才请（见下）。
                LuoYueResult luoYue = default;
                bool needQq = true;

                if (neteaseWordSource)
                {
                    var neWord = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    if (!string.IsNullOrEmpty(neWord.Lrc))
                    {
                        lrcText = neWord.Lrc;
                        yrcText = neWord.Yrc ?? "";
                        yrcTimingFirst = true;              // 落月网易云的 yrc 是「时间在前」
                    }
                    if (!string.IsNullOrEmpty(neWord.Trans)) transText = neWord.Trans;
                    if (!string.IsNullOrEmpty(neWord.Cover)) coverUrl = neWord.Cover;

                    needQq = !HasTimedLyric(lrcText)
                             || yrcText.Length == 0
                             || coverUrl.Length == 0;
                }

                if (needQq)
                {
                    luoYue = await FetchFromLuoYueAsync(title, artist, durationSec, HttpUserAgent);

                    if (neteaseWordSource)
                    {
                        if (!HasTimedLyric(lrcText) && !string.IsNullOrEmpty(luoYue.Lrc))
                        {
                            lrcText = luoYue.Lrc;
                            yrcText = luoYue.Yrc ?? "";
                            yrcTimingFirst = false;             // 落月 QQ 的 yrc 是「文字在前」
                            if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(luoYue.Trans))
                                transText = luoYue.Trans;
                        }
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
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(luoYue.Cover)) coverUrl = luoYue.Cover;
                }

                // ---- 引擎 2：QQ 音乐官方歌词接口 ----
                // 落月的歌词接口偶发失败 / 限流时由它顶上。
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

                // 位置在 QQ 系两档之后：
                //     —— 也就是「其他软件照旧走 QQ 音乐」；
                //     正好由网易云曲库补上。
                bool needNetease = !neteaseWordSource && (luoYueOnly
                    ? !(HasTimedLyric(lrcText) && yrcText.Length > 0)   // 还没有「带逐字的可用歌词」
                    : !HasTimedLyric(lrcText));
                if (!preferNetease && needNetease)
                {
                    var netease = await FetchFromLuoYueNeteaseAsync(title, artist, HttpUserAgent);
                    // 那时扫光回退整行，歌词本身照常显示。
                    if (!string.IsNullOrEmpty(netease.Lrc)
                        && ((luoYueOnly && !string.IsNullOrEmpty(netease.Yrc) && yrcText.Length == 0)
                            || !HasTimedLyric(lrcText)))
                    {
                        lrcText = netease.Lrc;
                        yrcText = netease.Yrc ?? "";
                        yrcTimingFirst = true; // 落月网易云的 yrc 是「时间在前」
                    }
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(netease.Trans))
                        transText = netease.Trans;
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(netease.Cover)) coverUrl = netease.Cover;
                }

                // ---- 引擎 4：网易云官方 API ----
                // 这正是「逐字缺失 ≠ 没有歌词」的落实。
                if (!preferNetease && !HasTimedLyric(lrcText))
                {
                    var neteaseOfficial = await FetchFromNeteaseOfficialAsync(title, artist, durationSec, allowCover: true);
                    if (!string.IsNullOrEmpty(neteaseOfficial.Lrc)) lrcText = neteaseOfficial.Lrc;
                    if (string.IsNullOrEmpty(transText) && !string.IsNullOrEmpty(neteaseOfficial.Trans))
                        transText = neteaseOfficial.Trans;
                    if (coverUrl.Length == 0 && !string.IsNullOrEmpty(neteaseOfficial.Cover))
                        coverUrl = neteaseOfficial.Cover;
                }

                // ---- 引擎 5：LRCLIB ----
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
                    var transTable = BuildTransTable(transText);
                    int transCursor = 0;
                    int transClaimed = -1;   // 已被某一行用作译文的条目下标，不允许再被别的行认领
                    var wordTable = IsLyricScanEnabled && yrcText.Length > 0
                        ? BuildYrcTable(yrcText, yrcTimingFirst)
                        : Array.Empty<(int StartMs, string Key, LyricWordTiming Timing)>();
                    int wordCursor = 0;
                    var lines = new List<(TimeSpan, string, string)>();
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
                                    string trans = LookupTrans(transTable, ts.Ticks, ref transCursor, ref transClaimed);
                                    lines.Add((ts, text, trans));
                                    wordTimings?.Add(LookupWordTiming(wordTable, ref wordCursor, (int)ts.TotalMilliseconds, text));
                                }
                            }
                        }
                    }
                    // 半路返回的歌词不会被丢掉，正好赶上续播。
                    if (IsLyricOwner(title, artist))
                    {
                        _lyrics = lines.ToArray();
                        _lyricWordTimings = wordTimings?.ToArray();
                        _lyricsHasTranslation = lines.Exists(l => !string.IsNullOrEmpty(l.Item3));
                    }
                }

                if (string.IsNullOrEmpty(coverUrl) && !IsVideoMode)
                    coverUrl = await FetchQqCoverAsync(title, artist);

                if (!string.IsNullOrEmpty(coverUrl)) coverUrl = NormalizeCoverUrl(coverUrl);

                return coverUrl;
            }
            finally
            {
                // 必须释放锁，让下一首歌可以正常获取
                _fetchLock.Release();
            }
        }

        private async Task FetchCoverAsync(string title, string artist, string coverUrl)
        {
            if (IsVideoMode || coverUrl.Length == 0) return;

            if (!IsLyricOwner(title, artist)) return;

            if (string.Equals(_externalCoverTitle, title, StringComparison.Ordinal)
                && string.Equals(_externalCoverAppId, _currentAppId, StringComparison.Ordinal)) return;

            string? coverAppId = null;
            SKBitmap? cover = null;
            int gen = Volatile.Read(ref _coverGen);
            try
            {
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

            // 世代号 + 归属双重校验：下载期间换了歌就当场丢掉，别把上一首的图贴到新歌上
            if (cover == null || gen != Volatile.Read(ref _coverGen) || !IsLyricOwner(title, artist))
            {
                cover?.Dispose();
                return;
            }

            _externalCoverAppId = coverAppId ?? "";
            _externalCoverTitle = title;
            Volatile.Write(ref _networkCoverGen, gen);   // 网络封面按歌名/歌手精确匹配 → 比 SMTC 权威
            SetThumbnail(cover, "网络");
        }

        private const int SessionCoverReadTimeoutMs = 2500;

        private async Task FetchSmtcCoverAsync(string title, int gen,
            GlobalSystemMediaTransportControlsSession session)
        {
            if (Interlocked.CompareExchange(ref _coverFetching, 1, 0) == 1) return;
            try
            {
                long appliedHash = -1;                  // 本链路已贴上的封面内容指纹
                DateTime appliedAt = DateTime.MinValue; // 上次换图的时间（用来判断「稳住了」）

                for (int i = 0; i < SessionCoverProbeMs.Length; i++)
                {
                    if (i > 0) await Task.Delay(SessionCoverProbeMs[i]);

                    // 换歌了：整条链路作废，新世代的链路会自己再起来。
                    // ⚠️ 判据只用世代号 —— 别用 ReferenceEquals(session, _currentSession)：
                    //    WinRT 每次 GetSessions() 给的 session 对象不保证是同一个实例，
                    //    那样会让链路刚起来就自杀，封面永远贴不上。
                    if (gen != Volatile.Read(ref _coverGen)) return;

                    // 本世代已经有网络封面了：它更准，别再让 SMTC 把它盖掉
                    if (Volatile.Read(ref _networkCoverGen) == gen) { _coverPending = false; return; }

                    var read = ReadSessionCoverAsync(session);
                    // 汽水音乐这类源的 WinRT 读流会「既不返回也不抛」—— 超时就当这一档失败，
                    // 否则 _coverFetching 被永久占住，整首曲子都不会再找封面。
                    if (await Task.WhenAny(read, Task.Delay(SessionCoverReadTimeoutMs)) != read)
                    {
                        CoverLog("会话自带封面：读取超时，本轮跳过");
                        continue;
                    }

                    var (bmp, hash) = read.Result;

                    if (gen != Volatile.Read(ref _coverGen))
                    {
                        bmp?.Dispose();
                        return;
                    }

                    Logger.Debug($"[封面] SMTC#{i} [{(bmp == null ? "null" : $"{bmp.Width}x{bmp.Height}")}] h={hash:X8} 曲目='{title}'");

                    if (bmp == null) { bmp?.Dispose(); continue; }   // 还没就绪 / 读失败 → 下一档再看

                    if (hash == appliedHash)
                    {
                        bmp.Dispose();
                        // 同一张图稳定够久了才收工；否则继续等播放器把封面换过来
                        if (DateTime.UtcNow - appliedAt >= TimeSpan.FromMilliseconds(SessionCoverSettleMs)) break;
                    }
                    else
                    {
                        appliedHash = hash;
                        appliedAt = DateTime.UtcNow;
                        SetThumbnail(bmp, "SMTC");   // 内容变了才换：贴第一张，也纠正「迟到」的残留图
                    }
                }

                if (gen == Volatile.Read(ref _coverGen)) _coverPending = false;
            }
            finally
            {
                Interlocked.Exchange(ref _coverFetching, 0);
            }
        }

        // 必须用调用方捕获的 session：_currentSession 随时会被换成别的会话，
        // 老代码用 `_currentSession!` 直接取属性，会话消失时就抛 NRE（那段 2 秒一次的刷屏日志）。
        private async Task<(SKBitmap? Bitmap, long Hash)> ReadSessionCoverAsync(
            GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props?.Thumbnail is not { } thumbRef)
                {
                    CoverLog("会话自带封面：本会话当前未提供缩略图");
                    return (null, 0);
                }

                var raw = await thumbRef.OpenReadAsync();
                if (raw == null)
                {
                    CoverLog("会话自带封面：打开缩略图流返回 null");
                    return (null, 0);
                }

                using (raw)
                using (var stream = raw.AsStreamForRead())
                using (var buffer = await ReadLimitedAsync(stream, CoverMaxBytes))
                {
                    if (buffer == null) return (null, 0);
                    long hash = Fingerprint(buffer);
                    return (DecodeCover(buffer), hash);
                }
            }
            catch (Exception ex)
            {
                CoverLog($"会话自带封面读取失败: {ex.GetType().Name} {ex.Message}");
                return (null, 0);
            }
        }

        // 抽稀采样哈希：封面换没换一眼就能认出来，比解一次码便宜得多。
        private static long Fingerprint(MemoryStream buffer)
        {
            byte[] bytes = buffer.GetBuffer();
            int n = (int)buffer.Length;
            int step = Math.Max(1, n / 512);
            long hash = unchecked((long)1469598103934665603UL);
            for (int i = 0; i < n; i += step)
                hash = unchecked((hash ^ bytes[i]) * 1099511628211L);
            return hash;
        }

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

            // 照抄过去会得到一张画不出东西的位图。
            var scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(scaled))
                canvas.DrawBitmap(decoded, new SKRect(0, 0, w, h), _coverScalePaint);

            decoded.Dispose();
            return scaled;
        }

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

        private const string QQMusicLyricApi = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";

        private const string QQMusicSmartBoxApi = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg";
        private const string QQMusicSingleSongApi = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg";

        private static string UnescapeQqText(string? raw)
            => raw?.Replace("&#10;", "\n").Replace("&#13;", "\r")
                    .Replace("&#32;", " ").Replace("&#45;", "-")
                    .Replace("&#40;", "(").Replace("&#41;", ")") ?? "";

        private readonly record struct LuoYueResult(string? Lrc, string? Trans, string? Yrc, string? Cover, string? Mid);

        private readonly record struct LuoYueNeteaseResult(string? Lrc, string? Trans, string? Yrc, string? Cover);

        private async Task<LuoYueResult> FetchFromLuoYueAsync(string title, string artist, long durationSec, string ua)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", ua);

                string word = Uri.EscapeDataString(string.IsNullOrEmpty(artist) ? title : $"{title} {artist}");
                using var searchStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/tencent/search/song?word={word}");
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                    return default;

                long songId = MatchSong(list, title, artist, durationSec, !IsChineseTitle(title), out string? cover, out string? mid);
                if (songId <= 0)
                {
                    Logger.Debug($"落月QQ 搜索没有匹配到候选（{title} / {artist}）");
                    return default;
                }

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

        private async Task<LuoYueNeteaseResult> FetchFromLuoYueNeteaseAsync(string title, string artist, string ua)
        {
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", ua);

                string word = Uri.EscapeDataString(string.IsNullOrEmpty(artist) ? title : $"{title} {artist}");
                using var searchStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/netease?word={word}");
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                    return default;

                long songId = MatchSong(list, title, artist, 0, false, out string? cover, out _);
                if (songId <= 0)
                {
                    Logger.Debug($"落月网易云 搜索没有匹配到候选（{title} / {artist}）");
                    return default;
                }

                using var lyricStream = await _http.GetStreamAsync($"{LuoYueHost}/v2/music/netease/lyric?id={songId}");
                using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                var root = lyricDoc.RootElement;

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

        private static string ReadJsonString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

        private readonly record struct NeteaseOfficialResult(string Lrc, string Trans, string Cover);

        private static readonly NeteaseOfficialResult EmptyNeteaseOfficial = new("", "", "");

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
                    new KeyValuePair<string, string>("limit", "60"),
                    new KeyValuePair<string, string>("offset", "0")
                });

                using var response = await _http.PostAsync("https://music.163.com/api/search/get/web", content);
                using var searchStream = await response.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                long songId = 0;
                bool resultIsObject = searchDoc.RootElement.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object;
                if (resultIsObject
                    && result.TryGetProperty("songs", out var songs)
                    && songs.ValueKind == JsonValueKind.Array)
                {
                    songId = PickNeteaseSongId(songs, title, artist, durationSec, exactNameOnly: false);
                    if (songId <= 0 && durationSec > 0)
                        songId = PickNeteaseSongId(songs, title, artist, 0, exactNameOnly: true);
                }

                if (songId <= 0)
                {
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

                    if (lyricDoc.RootElement.TryGetProperty("tlyric", out var tl) &&
                        tl.TryGetProperty("lyric", out var tlStr))
                        trans = tlStr.GetString() ?? "";
                }

                string cover = allowCover ? await FetchNeteaseCoverAsync(songId) : "";

                return new NeteaseOfficialResult(lrc, trans, cover);
            }
            catch (Exception ex)
            {
                Logger.Warn($"网易云引擎失败: {ex.Message}");
                return EmptyNeteaseOfficial;
            }
        }

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

                if (!(string.IsNullOrEmpty(artist)
                      || singer.Contains(artist, StringComparison.OrdinalIgnoreCase)
                      || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!exactNameOnly)
                {
                    long durationMs = song.TryGetProperty("duration", out var durEl) && durEl.TryGetInt64(out long d) ? d : 0;
                    if (!(durationMs <= 0 || durationSec <= 0 || Math.Abs(durationMs / 1000 - durationSec) <= 4))
                        continue;
                }

                if (song.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long id)) return id;
            }
            return 0;
        }

        private static long MatchSong(JsonElement list, string title, string artist, long durationSec, bool allowLooseTitle, out string? cover, out string? mid)
        {
            cover = null;
            mid = null;

            string wantTitle = NormalizeToken(title);
            if (wantTitle.Length == 0) return 0;
            var wantArtists = SplitArtists(artist);

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

                    if (strictDuration && !DurationMatches(song, durationSec)) continue;

                    string name = song.TryGetProperty("song", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    string singer = song.TryGetProperty("singer", out var singerEl) ? singerEl.GetString() ?? "" : "";

                    int score = ScoreCandidate(wantTitle, wantArtists, NormalizeToken(name), SplitArtists(singer), allowLooseTitle);
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

        private const int DurationToleranceSec = 4;

        private static bool DurationMatches(JsonElement song, long durationSec)
        {
            if (durationSec <= 0) return true;
            if (!song.TryGetProperty("interval", out var el) || el.ValueKind != JsonValueKind.String) return true;

            long seconds = ParseIntervalSeconds(el.GetString());
            if (seconds <= 0) return true;

            return Math.Abs(seconds - durationSec) <= DurationToleranceSec;
        }

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

        private const int LyricsSameSongPercent = 80;

        private static bool LyricsAreSameSong(string a, string b)
        {
            var la = LyricBodyLines(a);
            var lb = LyricBodyLines(b);
            if (la.Length == 0 || lb.Length == 0) return false;

            int max = Math.Max(la.Length, lb.Length);
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

        private static bool IsLyricCreditLine(string text)
        {
            int colon = text.IndexOfAny([':', '：']);
            if (colon >= 0 && colon <= 8 && text.Length <= 40) return true;   // 制作人员行

            if (text.Contains("未经") || text.Contains("著作权") || text.Contains("不得翻唱")) return true;

            return text.Length <= 32 && text.Contains(" - ");                 // 「歌名 - 歌手」标题行
        }

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

        // 又短到不至于把毫无关系的歌拉进来。
        private const int LooseTitleMinCommon = 3;

        private const double LooseTitleCoverage = 0.6;

        private static bool IsLooseTitleMatch(string candTitle, string wantTitle)
        {
            if (candTitle.Length < LooseTitleMinCommon || wantTitle.Length < LooseTitleMinCommon) return false;

            int shorter = Math.Min(candTitle.Length, wantTitle.Length);
            int need = Math.Max(LooseTitleMinCommon, (int)Math.Ceiling(shorter * LooseTitleCoverage));

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

        private static string NormalizeToken(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // 歌手串里的分隔符：中英日常见的并列写法都收进来
        private static readonly char[] ArtistSeparators = ['/', '、', ',', '，', '&', '×', ';', '；', '|', '+'];

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

        private static string NormalizeCoverUrl(string url)
        {
            string normalized = url.Replace("R800x800M000", "R300x300M000", StringComparison.Ordinal);

            if (normalized.Contains("music.126.net", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("param=", StringComparison.Ordinal))
                normalized += normalized.Contains('?') ? "&param=300y300" : "?param=300y300";

            return normalized;
        }

        //   ① 正文整段解析不出时间轴，全部被丢弃；
        private static readonly string[] LyricTimeFormats =
            [@"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss\:ff", @"mm\:ss"];

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

        private const long TransMatchToleranceTicks = 300L * TimeSpan.TicksPerMillisecond;

        // 跨度限制是防止把间奏前的最后一句一直拖到间奏之后。
        private const long TransCarryTicks = 5L * TimeSpan.TicksPerSecond;

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

        // 一条译文只能被一行认领：认领过（精确命中）的条目不再向后携带。
        // 否则中文原句（上游译文表里没有它的条目）会把上一句外文歌词的译文一直挂在第二行，
        // 直到下一句真正需要翻译时才被替换掉 —— 表现为「第二行残留」。
        private static string LookupTrans((long Ticks, string Text)[] table, long ticks, ref int cursor, ref int claimed)
        {
            if (table.Length == 0) return "";

            while (cursor < table.Length && table[cursor].Ticks < ticks - TransMatchToleranceTicks) cursor++;
            if (cursor >= table.Length) return "";

            // 精确 / 邻近命中 → 认领
            if (Math.Abs(table[cursor].Ticks - ticks) <= TransMatchToleranceTicks)
            {
                claimed = cursor;
                return table[cursor].Text;
            }

            if (cursor > 0 && cursor - 1 != claimed && ticks - table[cursor - 1].Ticks <= TransCarryTicks)
                return table[cursor - 1].Text;

            return "";
        }

        // ---- 逐字歌词（yrc） ----
        // 对齐用文本、不用时间戳。实测两家表现完全不同：

        private readonly struct LyricWordTiming
        {
            public readonly int[] EndMs;
            public readonly int[] CumChars;
            public readonly int TotalChars;
            public readonly int LineStartMs;

            public LyricWordTiming(int[] endMs, int[] cumChars, int totalChars, int lineStartMs)
            {
                EndMs = endMs;
                CumChars = cumChars;
                TotalChars = totalChars;
                LineStartMs = lineStartMs;
            }
        }

        private const int YrcTimeFallbackMs = 500;

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

                    string seg;
                    if (timingFirst)
                    {
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

        private static int CountEffectiveChars(string s)
        {
            int n = 0;
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) n++;
            return n;
        }

        private static string NormalizeLyricText(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

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
            // 比例会整体缩放，扫光要么永远到不了头、要么提前走完。
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

        private float ComputeScanProgress(int lineIndex, TimeSpan position)
        {
            double window = LineWindowSeconds(lineIndex);
            float progress;

            // ① 逐字优先
            if (HasWordTiming(lineIndex) && _lyricWordTimings![lineIndex] is { } wordTiming)
            {
                progress = ComputeWordAlignedProgress(
                    wordTiming,
                    position.TotalMilliseconds + LyricDelayOffset * 1000.0 - wordTiming.LineStartMs);
            }
            else if (window > 0)
            {
                progress = (float)((position - LineStartRaw(lineIndex)).TotalSeconds / window);
            }
            else
            {
                return 0f;
            }

            double remain = (LineYieldRaw(lineIndex) - position).TotalSeconds;
            if (remain <= LyricLineFinishLead)
            {
                double span = LyricLineFinishLead - LyricLineFinishEarly;
                float forced = (float)((LyricLineFinishLead - remain) / span);
                progress = Math.Max(progress, Math.Clamp(forced, 0f, 1f));
            }

            return Math.Clamp(progress, 0f, 1f);
        }

        private TimeSpan LineStartRaw(int lineIndex)
            => _lyrics[lineIndex].Time - TimeSpan.FromSeconds(LineLeadSeconds(lineIndex));

        private TimeSpan LineYieldRaw(int lineIndex)
            => lineIndex < _lyrics.Length - 1
                ? LineStartRaw(lineIndex + 1)
                : LineStartRaw(lineIndex) + TimeSpan.FromSeconds(LyricLastLineSeconds);

        private double LineLeadSeconds(int lineIndex)
        {
            double lead = 0.6;   // 整行 lrc：那一栏没有字级数据，只能用固定提前量近似
            if (HasWordTiming(lineIndex) && _lyricWordTimings![lineIndex] is { } wordTiming)
            {
                lead = Math.Max(0,
                    (_lyrics[lineIndex].Time - TimeSpan.FromMilliseconds(wordTiming.LineStartMs)).TotalSeconds);
            }

            return ClampLead(lead + LyricDelayOffset, lineIndex);
        }

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

        private double LineWindowSeconds(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _lyrics.Length) return 0;
            TimeSpan start = _lyrics[lineIndex].Time;
            TimeSpan end = lineIndex < _lyrics.Length - 1
                ? _lyrics[lineIndex + 1].Time
                : start + TimeSpan.FromSeconds(LyricLastLineSeconds);
            return (end - start).TotalSeconds;
        }

        private bool HasWordTiming(int lineIndex)
            => _lyricWordTimings is { Length: > 0 } timings
               && lineIndex >= 0 && lineIndex < timings.Length
               && timings[lineIndex] is { TotalChars: > 0 };

        private const double LyricLastLineSeconds = 4.0;

        private const double LyricLineFinishLead = 0.35;

        private const double LyricLineFinishEarly = 0.12;

        private static float ComputeWordAlignedProgress(LyricWordTiming timing, double elapsedMs)
        {
            int[] ends = timing.EndMs;
            int n = ends.Length;
            if (n == 0 || timing.TotalChars <= 0) return 0f;
            // 与主流卡拉 OK 一致；等到下一行时间戳到了自然换行。
            if (elapsedMs >= ends[n - 1]) return 1f;
            if (elapsedMs <= 0) return 0f;

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
        private static bool IsUsableTranslation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Contains("翻译作品") || text.Contains("本译文")) return false;

            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) return true; // 中日韩文字 / 拉丁字母 / 数字都算有效内容

            return false;
        }

        public void UpdateLyrics()
        {
            var now = DateTime.UtcNow;
            var dt = now - _lastUpdateTime;
            _lastUpdateTime = now; // 无论是否在播放，每一帧都更新绝对时间差

            // 封面兜底推进：换代即重开一轮；本世代定局后这里基本零成本。
            UpdateCover();
            DiagCoverIfStuck(now);

            RetryTitleIfNeeded(now);

            RetryLyricsIfNeeded();

            AdvanceSuspendedTimeline(now);

            // 无会话：不显示歌词，也不保留时间轴
            if (_currentSession == null)
            {
                SetLyric("", "", 0f, false);
                if (!_isDragging) { HasTimeline = false; Duration = TimeSpan.Zero; _timelinePos = TimeSpan.Zero; }
                return;
            }

            if (_justSoloLyric.TryGetCurrentLyric(LyricDelayOffset, out string soloText, out string soloTrans, out float soloProgress))
            {
                SetLyric(soloText, soloTrans, soloProgress, _justSoloLyric.HasTranslation);
                return;
            }

            int sampleVersion = System.Threading.Volatile.Read(ref _smtcSampleVersion);
            bool sampledNow = sampleVersion != _consumedSampleVersion;
            _consumedSampleVersion = sampleVersion;

            // 时间轴：默认照旧用 SMTC；只有「当前会话是网易云 / 酷狗，且 SMTC 给不出时间轴」时，
            // 才换成插件从 CDP 读到的时长与位置（位置在 MediaController.ExternalSource.cs 里按墙钟补足）。
            TimeSpan drivePos = _smtcPos;
            _externalDrive = TryGetExternalTimeline(_currentAppId, _smtcDuration, now, out TimeSpan extPos, out TimeSpan extDur);
            if (_externalDrive)
            {
                drivePos = extPos;
                HasTimeline = true;
                Duration = extDur;
            }
            else
            {
                HasTimeline = _smtcDuration > TimeSpan.Zero;
                Duration = _smtcDuration;
            }
            // 反而要重排这一行的调用位置，得不偿失。
            UpdateTimelineTexts();

            // 但时间轴照常走 —— 只要 SMTC 给出进度就显示。
            if (IsNonLyricSession || _lyricSlot < 0)
            {
                SetLyric("", "", 0f, false);
                AdvanceFreeTimeline(drivePos, dt, now);
                _forceResync = false; // 无歌词槽位：强制对齐标记不适用，就地消费，避免每帧重复采样
                return;
            }

            // 时间轴先推进：它跟「这一帧有没有歌词可显示」是两件事。
            // 必须放在下面那几个 return 之前 —— 否则刚换歌、歌词还在拉的这段时间里，
            // 位置一次都不推进，看上去就跟宿主自己的虚拟跑表一样（歌名时长都对、就是不往前走）。
            AdvanceTimeline(HasTimeline, drivePos, sampledNow, dt, now);

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

            if (_lyrics.Length == 0) { SetLyric("", "", 0f, false); return; }

            string found = "";
            string foundTrans = "";
            float progress = 0f;
            TimeSpan rawPosition = _recentSongs[_lyricSlot].Position;
            for (int i = _lyrics.Length - 1; i >= 0; i--)
            {
                if (rawPosition >= LineStartRaw(i))
                {
                    found = _lyrics[i].Text;
                    foundTrans = _lyrics[i].Translation;
                    progress = ComputeScanProgress(i, rawPosition);
                    break;
                }
            }

            // 输出结果，供渲染层使用
            SetLyric(found, foundTrans, progress, _lyricsHasTranslation);
        }

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

        private const double TimelineJumpSeconds = 1.5;        // 与本地位置的差超过它 → 当作真实跳变，直接对齐
        private const double TimelineNudgeDeadZoneSec = 0.02;  // 落后小于它就不动，省掉无意义的微调
        private const double TimelineNudgeRatio = 0.25;        // 每次采样吃掉 25% 的落后量
        private const double TimelineNudgeMaxStepSec = 0.08;   // 单次追赶上限
        private const double TimelineSeekBackSeconds = 0.5;    // SMTC 自己往回走超过它 → 判定为用户往回 seek
        private const double TimelineAheadDeadZoneSec = 0.08;  // 领先超过它 → 接下来走慢一点把偏差追平
        private const double TimelineSlowRate = 0.8;           // 领先时每帧只推进 80% 的时间
        private const double TimelineStallAheadSeconds = 1.2;

        private const double TimelineAheadMaxLeadSeconds = 0.6;

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

        private const double SmtcMaxExtrapolationSec = 10.0;

        private void AdvanceTimeline(bool hasTimeline, TimeSpan smtcPos, bool newSample, TimeSpan dt, DateTime now)
        {
            int slot = _lyricSlot;
            if (slot < 0 || _isDragging) return; // 状态锁：拖动期间禁止上游写入与自动推进

            bool settling = now < _seekSettleUntil;

            // 外部源（插件）接管时，位置就是「插件快照 + 墙钟」，上面已经算好并作为 smtcPos 传进来了。
            // 必须短路掉下面那个 TryGetSmtcLivePosition —— 它读的是 _smtcPos，
            // 而网易云/酷狗在 SMTC 里给出的进度就是 0：不短路的话歌名、时长都对，
            // 位置却被按回原点，歌词自然一行都不往前走。
            if (_externalDrive)
            {
                _recentSongs[slot].Position = smtcPos;
                _timelineAhead = false;
                _recentSongs[slot].TickedAt = now;
                _timelinePos = smtcPos;
                _forceResync = false;
                return;
            }

            if (!settling && TryGetSmtcLivePosition(now, out TimeSpan live))
            {
                _recentSongs[slot].Position = live;
                _timelineAhead = false;   // 不再需要「领先降速」那套补偿
                _recentSongs[slot].TickedAt = now;
                _timelinePos = live;
                _forceResync = false;
                return;
            }

            if (!hasTimeline) _timelineAhead = false;

            if (IsPlaying)
            {
                double rate = _timelineAhead ? TimelineSlowRate : 1.0;
                _recentSongs[slot].Position += TimeSpan.FromSeconds(dt.TotalSeconds * rate);
            }

            if (hasTimeline && newSample)
            {
                if (_forceResync) _hasPrevSmtcPos = false; // 换歌：不拿上一首的位置当基准

                double delta = (smtcPos - _recentSongs[slot].Position).TotalSeconds;

                // 只有 SMTC 自己往回走了，才是用户把进度往回拖了。
                bool smtcWentBack = _hasPrevSmtcPos
                    && smtcPos < _prevSmtcPos - TimeSpan.FromSeconds(TimelineSeekBackSeconds);
                _prevSmtcPos = smtcPos;
                _hasPrevSmtcPos = true;

                if (_forceResync || delta > TimelineJumpSeconds || (!settling && smtcWentBack))
                {
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
                    _timelineAhead = delta < -TimelineAheadDeadZoneSec;

                    if (_timelineAhead && delta < _prevDelta && delta < -TimelineStallAheadSeconds)
                        _timelineAhead = false;

                    // 降速只用来吃掉「本地采样间隙里多跑出去的那一点点」——
                    if (_timelineAhead && delta < -TimelineAheadMaxLeadSeconds) _timelineAhead = false;
                }

                _prevDelta = delta;
                _forceResync = false;
            }

            _recentSongs[slot].TickedAt = now; // 标记这个进度是刚推算过的，换歌时据此判断能否续用
            _timelinePos = _recentSongs[slot].Position; // 进度条与歌词同源：永远读同一份位置
        }

        private void AdvanceFreeTimeline(TimeSpan smtcPos, TimeSpan dt, DateTime now)
        {
            if (_isDragging) return;

            bool settling = now < _seekSettleUntil;

            // 外部源接管时位置由插件驱动（同 AdvanceTimeline 的理由），别再走 SMTC 外推
            if (_externalDrive)
            {
                _timelinePos = smtcPos;
                return;
            }

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

        public bool BeginDrag(float ratio)
        {
            if (!HasTimeline || Duration <= TimeSpan.Zero) return false;
            _isDragging = true;
            DragTo(ratio);
            return true;
        }

        public void DragTo(float ratio)
        {
            if (!_isDragging) return;
            var pos = TimeSpan.FromSeconds(Math.Clamp(ratio, 0f, 1f) * Duration.TotalSeconds);
            _timelinePos = pos;
            if (_lyricSlot >= 0) _recentSongs[_lyricSlot].Position = pos;
            UpdateTimelineTexts();
        }

        public void EndDrag()
        {
            if (!_isDragging) return;
            _isDragging = false;
            // 否则落点会被判成跳变而把进度条与歌词弹回原处。
            _seekSettleUntil = DateTime.UtcNow.AddSeconds(SeekSettleSeconds);
            if (_currentSession != null) CommitSeek(_currentSession, _timelinePos.Ticks);
        }

        public bool SeekBy(double seconds)
        {
            if (!HasTimeline || Duration <= TimeSpan.Zero || _currentSession == null) return false;

            double target = Math.Clamp(_timelinePos.TotalSeconds + seconds, 0, Duration.TotalSeconds);
            var pos = TimeSpan.FromSeconds(target);
            _timelinePos = pos;
            if (_lyricSlot >= 0) _recentSongs[_lyricSlot].Position = pos;
            UpdateTimelineTexts();

            //    不设静默期会被判成跳变，把进度条与歌词弹回原处。
            _seekSettleUntil = DateTime.UtcNow.AddSeconds(SeekSettleSeconds);
            CommitSeek(_currentSession, pos.Ticks);
            return true;
        }

        private static async void CommitSeek(GlobalSystemMediaTransportControlsSession session, long ticks)
        {
            try { await session.TryChangePlaybackPositionAsync(ticks); } catch { }
        }

        private static string FormatClock(int sec)
        {
            if (sec < 0) sec = 0;
            int h = sec / 3600;
            return h > 0 ? $"{h}:{sec / 60 % 60:00}:{sec % 60:00}" : $"{sec / 60}:{sec % 60:00}";
        }

        private void UpdateTimelineTexts()
        {
            int sec = (int)_timelinePos.TotalSeconds;
            if (sec != _shownSec) { _shownSec = sec; TimelineElapsed = FormatClock(sec); }
            int total = (int)Duration.TotalSeconds;
            if (total != _shownTotal) { _shownTotal = total; TimelineTotal = FormatClock(total); }
        }

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