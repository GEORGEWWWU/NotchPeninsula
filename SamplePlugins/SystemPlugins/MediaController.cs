using System.IO;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using Windows.Storage.Streams;
using SkiaSharp;
using System.Net.Http;
using System.Text.Json;

using NotchPeninsula;
using NotchPeninsula.Plugins;

namespace SystemPlugins
{
    public partial class MediaController
    {
        // 暴露给 UI 的单例，方便极速调用
        public static MediaController? Instance { get; private set; }
        // 平台 ID 列表（与 MediaSettingsPage 的 ChoiceSetting 选项顺序一致）
        private static readonly string[] PlatformIds = ["other", "netease", "qqmusic", "kugou", "spotify", "applemusic", "echomusic", "lxmusic"];
        private static string TargetPlatform = "other";
        private static bool IsMediaControlEnabled = true;
        private static bool IsLyricsEnabled = true;
        private static bool IsKaraokeEnabled = true;
        private static float LyricDelayOffset = 0f;
        private static readonly HttpClient _http = new(new HttpClientHandler // 注入无条件放行的证书校验回调，彻底解决 SSL 报错，同时增加超时容错
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        })
        { Timeout = TimeSpan.FromSeconds(4) };

        // 统一的请求 UA，按请求消息设置，避免并发修改静态 HttpClient 的默认头部
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

        // 声明一个容量为 1 的异步锁，控制网络请求只能单线进行
        private static readonly System.Threading.SemaphoreSlim _fetchLock = new(1, 1);

        // 构造带独立头部的请求消息：头部挂在消息上而非静态 HttpClient 上，天然线程安全
        private static HttpRequestMessage CreateRequest(
            HttpMethod method, string url, string userAgent,
            string? referer = null, string? xRealIp = null, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.UserAgent.ParseAdd(userAgent);
            if (referer != null) request.Headers.Referrer = new Uri(referer);
            if (xRealIp != null) request.Headers.TryAddWithoutValidation("X-Real-IP", xRealIp);
            return request;
        }

        private (TimeSpan Time, string Text)[] _lyrics = Array.Empty<(TimeSpan, string)>();
        public string CurrentLyric { get; private set; } = "";
        public float CurrentLyricProgress { get; private set; } = 0f;
        private TimeSpan _currentSimulatedPosition = TimeSpan.Zero;
        private TimeSpan _lastSmtcPosition = TimeSpan.Zero;
        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private string _lastFetchedTitle = "";
        private string _lastFetchedArtist = "";

        public string Title { get; private set; } = "Notch Peninsula";
        public string Artist { get; private set; } = "Waiting for media...";
        public bool IsPlaying { get; private set; } = false;
        public bool IsActive { get; private set; } = false;
        public SKBitmap? Thumbnail { get; private set; }

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private bool _isBilibiliSession;  // 通用模式下当前会话是否为 bilibili，用于隐藏 Artist
        private bool _isPotPlayerSession; // 当前会话是否为 PotPlayer，无歌名/歌手时隐藏文本
        private bool _isPotPlayerMusic;   // PotPlayer 是否处于音乐模式（SMTC 带歌手）：决定封面来源与是否取歌词
        private string _appliedCoverKey = ""; // 已应用网络封面的曲目标识("歌名|歌手")，避免重复访问网络
        private bool _isBrowserSession;   // 当前会话是否为浏览器 (Chrome/Edge)，启用视频标题清理
        private bool _isJustSoloSession;  // 当前会话是否为 Just Solo，启用 LyricServer 直连歌词
        private readonly JustSoloLyricClient _justSoloLyric = new();
        private readonly AudioAnalyzer _audioAnalyzer = new();
        private readonly float[] _bars = new float[5];
        private readonly float[] _soloMapBuf = new float[5];

        /// <summary>平滑后的 5 根频谱柱（0~1），供媒体组件绘制。</summary>
        public float[] Bars => _bars;

        /// <summary>刷新频谱柱：优先 Just Solo LyricServer，回退 WASAPI 采集。</summary>
        public void UpdateBars()
        {
            float[] source = _justSoloLyric.TryGetSpectrum(out var bands) && bands.Length >= 12
                ? MapSoloSpectrum(bands)
                : _audioAnalyzer.GetBars();
            for (int i = 0; i < 5; i++)
            {
                float target = source[i];
                _bars[i] += (target - _bars[i]) * (target > _bars[i] ? 0.75f : 0.12f);
            }
        }

        private float[] MapSoloSpectrum(float[] bands)
        {
            _soloMapBuf[0] = Math.Max(bands[0], bands[1]);
            _soloMapBuf[1] = Math.Max(bands[2], Math.Max(bands[3], bands[4]));
            _soloMapBuf[2] = Math.Max(bands[5], bands[6]);
            _soloMapBuf[3] = Math.Max(bands[7], Math.Max(bands[8], bands[9]));
            _soloMapBuf[4] = Math.Max(bands[10], bands[11]);
            return _soloMapBuf;
        }

        public MediaController(IPluginHost host)
        {
            Instance = this;
            SyncSettings(host);
            host.SettingsChanged += () => { SyncSettings(host); _ = ForceRefresh(); };
            _ = InitializeAsync();
        }

        private static void SyncSettings(IPluginHost host)
        {
            IsMediaControlEnabled = host.GetSetting("MediaControlEnabled", "1") != "0";
            IsLyricsEnabled = host.GetSetting("LyricsEnabled", "1") != "0";
            IsKaraokeEnabled = host.GetSetting("KaraokeEnabled", "1") != "0";
            LyricDelayOffset = float.TryParse(host.GetSetting("LyricDelayOffset", "0"), out var d) ? d : 0f;
            int plat = int.TryParse(host.GetSetting("TargetPlatform", "0"), out var p) ? p : 0;
            TargetPlatform = plat >= 0 && plat < PlatformIds.Length ? PlatformIds[plat] : "other";
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

        private async Task UpdateSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? newSession = null;

            // 如果总开关打开，执行精确的平台过滤
            if (IsMediaControlEnabled)
            {
                var sessions = manager.GetSessions();
                Logger.Info("会话列表: " + string.Join(" | ", sessions.Select(s => s.SourceAppUserModelId))); // 临时调试

                if (TargetPlatform == "other")
                {
                    // 通用模式屏蔽抖音
                    newSession = sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("justsolo", StringComparison.OrdinalIgnoreCase))
                              ?? sessions.FirstOrDefault(s => !s.SourceAppUserModelId.Contains("douyin", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    foreach (var s in sessions)
                    {
                        var id = s.SourceAppUserModelId.ToLower();
                        if (id.Contains("douyin")) continue; // 全局拉黑抖音

                        // 网易云音乐 (包名常为 cloudmusic 或 netease)
                        if (TargetPlatform == "netease" && (id.Contains("cloudmusic") || id.Contains("netease")))
                        { newSession = s; break; }

                        // QQ音乐 (包名常为 qqmusic 或 tencent)
                        else if (TargetPlatform == "qqmusic" && (id.Contains("qqmusic") || id.Contains("tencent")))
                        { newSession = s; break; }

                        // Apple Music (包名通常包含 apple 和 music)
                        else if (TargetPlatform == "applemusic" && id.Contains("apple") && id.Contains("music"))
                        { newSession = s; break; }

                        // 酷狗、Spotify、Echomusic 直接匹配 TargetPlatform ID
                        else if (TargetPlatform != "netease" && TargetPlatform != "qqmusic" && TargetPlatform != "applemusic"
                                 && id.Contains(TargetPlatform))
                        { newSession = s; break; }

                        // LX Music (包名通常包含 cn.toside.music.desktop 或 lxmusic)
                        else if (TargetPlatform == "lxmusic" && (id.Contains("cn.toside.music.desktop") || id.Contains("lxmusic")))
                        { newSession = s; break; }
                    }
                }
            }

            // 命中 bilibili / PotPlayer / 浏览器 会话时打标记，供刷新时应用文本显示策略
            _isBilibiliSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Bilibili");
            _isPotPlayerSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "PotPlayer");
            _isPotPlayerMusic = false; // 音乐/视频模式由 RefreshProperties 按 SMTC 歌手字段判定
            _isBrowserSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Chrome")
                             || MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Edge");
            _isJustSoloSession = newSession?.SourceAppUserModelId?.Contains("justsolo", StringComparison.OrdinalIgnoreCase) == true;

            // Just Solo 专属歌词通道：仅通用模式下检测到 justsolo 会话时才连接 LyricServer
            UpdateJustSoloConnection();

            // 如果目标会话没变，只需刷新属性，避免重复订阅事件浪费内存
            if (_currentSession != null && newSession != null && _currentSession.SourceAppUserModelId == newSession.SourceAppUserModelId)
            {
                await RefreshProperties();
                IsActive = true;
                return;
            }

            // 切换到了新的会话（或者置空）
            if (_currentSession != null)
            {
                // 切换前，必须先解绑旧会话的事件，防止幽灵对象吃内存
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            _currentSession = newSession;

            if (_currentSession != null)
            {
                // 绑定新会话事件
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;

                await RefreshProperties();
                IsActive = true;
            }
            else
            {
                IsActive = false;
                Title = "No Media";
                Artist = "";
                IsPlaying = false;
                await UpdateThumbnailAsync(null, null);
            }
        }

        // 依据当前会话与平台模式，维护 Just Solo LyricServer 的连接
        private void UpdateJustSoloConnection()
        {
            bool shouldConnect = TargetPlatform == "other" && _isJustSoloSession;

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

                    // PotPlayer 用 SMTC 是否带歌手区分：有歌手=音乐（走封面+歌词），无歌手=视频（用站标）
                    _isPotPlayerMusic = _isPotPlayerSession && !string.IsNullOrEmpty(props.Artist);

                    await UpdateThumbnailAsync(_currentSession, props.Thumbnail);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取媒体属性失败，可能遇到不规范的媒体源", ex);
                Title = _isPotPlayerSession ? "" : "Unknown";
                Artist = (_isBilibiliSession || _isPotPlayerSession) ? "" : "Unknown";
                _isPotPlayerMusic = false;
                await UpdateThumbnailAsync(null, null);
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

        // 统一封面处理入口：
        // PotPlayer 音乐模式 → SMTC 封面优先，无封面时网络获取（不用站标）
        // 其余情况 → 平台站标优先（Always 始终使用；Fallback 无 SMTC 封面时兜底），其次 SMTC 封面
        private async Task UpdateThumbnailAsync(
            GlobalSystemMediaTransportControlsSession? session,
            IRandomAccessStreamReference? smtcThumbnail)
        {
            if (session == null)
            {
                _appliedCoverKey = "";
                SetThumbnail(null);
                return;
            }

            if (_isPotPlayerMusic)
            {
                if (smtcThumbnail != null)
                {
                    _appliedCoverKey = "";
                    SetThumbnail(await DecodeSmtcThumbnailAsync(smtcThumbnail));
                    return;
                }

                // 同一首歌只请求一次，避免属性/播放状态变化时反复访问网络
                string coverKey = $"{Title}|{Artist}";
                if (coverKey == _appliedCoverKey) return;
                _appliedCoverKey = coverKey;

                var cover = await SearchCoverAsync(Title, Artist);
                if ($"{Title}|{Artist}" != coverKey) return; // 期间已切歌，丢弃过期封面
                SetThumbnail(cover);
                return;
            }

            _appliedCoverKey = "";
            var platformLogo = MediaLogoProvider.GetLogo(session.SourceAppUserModelId, smtcThumbnail != null);
            SetThumbnail(platformLogo ?? (smtcThumbnail != null ? await DecodeSmtcThumbnailAsync(smtcThumbnail) : null));
        }

        // 替换封面并释放旧封面
        private void SetThumbnail(SKBitmap? thumbnail)
        {
            var oldThumbnail = Thumbnail;
            Thumbnail = thumbnail;
            oldThumbnail?.Dispose();
        }

        // 解码 SMTC 原始封面，失败返回 null
        private static async Task<SKBitmap?> DecodeSmtcThumbnailAsync(IRandomAccessStreamReference smtcThumbnail)
        {
            try
            {
                using var stream = await smtcThumbnail.OpenReadAsync();
                using var dotNetStream = stream.AsStreamForRead();
                return SKBitmap.Decode(dotNetStream);
            }
            catch (Exception ex)
            {
                Logger.Error("封面解析失败", ex);
                return null;
            }
        }

        // 封面搜索：QQ音乐搜索匹配歌曲并下载专辑封面，返回解码后的封面（失败返回 null）
        private async Task<SKBitmap?> SearchCoverAsync(string title, string artist)
        {
            if (string.IsNullOrEmpty(title)) return null;

            await _fetchLock.WaitAsync();
            try
            {
                string query = Uri.EscapeDataString($"{title} {artist}");

                using var searchRequest = CreateRequest(HttpMethod.Get, $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&n=5&format=json", BrowserUserAgent);
                using var searchResponse = await _http.SendAsync(searchRequest, HttpCompletionOption.ResponseHeadersRead);
                using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                string albumMid = "";
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

                        // 与歌词引擎一致：同时校验歌名与歌手，避免同名歌曲串封面
                        if ((name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(artist) || singer.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(singer, StringComparison.OrdinalIgnoreCase)))
                        {
                            albumMid = song.TryGetProperty("albummid", out var am) ? am.GetString() ?? "" : "";
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(albumMid)) return null;

                using var coverRequest = CreateRequest(HttpMethod.Get, $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg", BrowserUserAgent);
                using var coverResponse = await _http.SendAsync(coverRequest, HttpCompletionOption.ResponseHeadersRead);
                using var coverStream = await coverResponse.Content.ReadAsStreamAsync();
                return SKBitmap.Decode(coverStream);
            }
            catch (Exception ex)
            {
                Logger.Warn($"网络封面获取失败: {ex.Message}");
                return null;
            }
            finally
            {
                _fetchLock.Release();
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
        /// 返回 false 表示不可用（未连接 / 服务端版本过低 / 无数据），调用方应回退到本地音频采集。
        /// </summary>
        public bool TryGetSoloSpectrum(out float[] bands) => _justSoloLyric.TryGetSpectrum(out bands);

        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshProperties();
        }

        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            await RefreshProperties();
        }

        private async Task FetchLyricsAsync(string title, string artist, long durationSec)
        {
            _lyrics = Array.Empty<(TimeSpan, string)>();
            CurrentLyric = "";
            _currentSimulatedPosition = TimeSpan.Zero;
            _lastSmtcPosition = TimeSpan.Zero;
            // PotPlayer 视频模式（SMTC 无歌手）不显示歌词；音乐模式正常获取
            if (string.IsNullOrEmpty(title) || _isBilibiliSession || _isBrowserSession || (_isPotPlayerSession && !_isPotPlayerMusic)) return;

            string lrcText = await SearchLyricsAsync(title, artist, durationSec);
            if (string.IsNullOrEmpty(lrcText)) return;

            // ====== 极速解析时间轴 ======
            var lines = new List<(TimeSpan, string)>();
            foreach (var line in lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('[') && line.IndexOf(']') is int idx && idx > 5)
                {
                    if (TimeSpan.TryParseExact(line.Substring(1, idx - 1), new[] { @"mm\:ss\.ff", @"mm\:ss\.fff", @"mm\:ss\.f", @"mm\:ss" }, null, out var ts))
                    {
                        string text = line.Substring(idx + 1).Trim();
                        if (!string.IsNullOrEmpty(text)) lines.Add((ts, text));
                    }
                }
            }
            // 状态锁：只有当网络请求结束，且当前播放的歌曲没被切走时，才允许写入
            if (this.Title == title && this.Artist == artist)
            {
                _lyrics = lines.ToArray();
            }
        }

        // 歌词搜索：依次尝试 QQ音乐 → 网易云 → LRCLIB，返回 LRC 文本（全部失败返回空串）
        private async Task<string> SearchLyricsAsync(string title, string artist, long durationSec)
        {
            // 等待获取通行证，让封面/歌词的网络请求单线进行，避免同一时刻并发拉取
            await _fetchLock.WaitAsync();
            try
            {
                // 极速拦截：如果排队轮到自己时，发现系统已经播放别的歌了，直接丢弃任务，0 性能浪费
                if (this.Title != title || this.Artist != artist) return "";

                string query = Uri.EscapeDataString($"{title} {artist}");
                string lrcText = "";

                // ====== 引擎 1：QQ音乐 (优先) ======
                try
                {
                    // 内存优化：使用 Stream 流直接解析 JSON，避免生成大字符串吃内存
                    using var searchRequest = CreateRequest(HttpMethod.Get, $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&n=5&format=json", BrowserUserAgent);
                    using var searchResponse = await _http.SendAsync(searchRequest, HttpCompletionOption.ResponseHeadersRead);
                    using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
                    using var searchDoc = await JsonDocument.ParseAsync(searchStream);

                    string songmid = "";
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
                                songmid = song.GetProperty("songmid").GetString() ?? "";
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(songmid))
                    {
                        using var lyricRequest = CreateRequest(HttpMethod.Get, $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songmid}&format=json&nobase64=1", BrowserUserAgent, referer: "https://y.qq.com/");
                        using var lyricResponse = await _http.SendAsync(lyricRequest, HttpCompletionOption.ResponseHeadersRead);
                        using var lyricStream = await lyricResponse.Content.ReadAsStreamAsync();
                        using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);

                        if (lyricDoc.RootElement.TryGetProperty("lyric", out var lrcEl))
                        {
                            lrcText = lrcEl.GetString()?
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
                        string xRealIp = $"114.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}";

                        using var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("s", $"{title} {artist}"),
                            new KeyValuePair<string, string>("type", "1"),
                            new KeyValuePair<string, string>("limit", "5"),
                            new KeyValuePair<string, string>("offset", "0")
                        });

                        using var searchRequest = CreateRequest(HttpMethod.Post, "https://music.163.com/api/search/get/web", BrowserUserAgent, referer: "https://music.163.com", xRealIp: xRealIp, content: content);
                        using var searchResponse = await _http.SendAsync(searchRequest, HttpCompletionOption.ResponseHeadersRead);
                        using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
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
                            using var lyricRequest = CreateRequest(HttpMethod.Get, $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1", BrowserUserAgent, referer: "https://music.163.com", xRealIp: xRealIp);
                            using var lyricResponse = await _http.SendAsync(lyricRequest, HttpCompletionOption.ResponseHeadersRead);
                            using var lyricStream = await lyricResponse.Content.ReadAsStreamAsync();
                            using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                            if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) &&
                                lrc.TryGetProperty("lyric", out var lyricStr))
                            {
                                lrcText = lyricStr.GetString() ?? "";
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
                        string lrclibUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                        if (durationSec > 0) lrclibUrl += $"&duration={durationSec}";

                        // 同样优化为 Stream 流解析
                        using var lrclibRequest = CreateRequest(HttpMethod.Get, lrclibUrl, BrowserUserAgent);
                        using var lrclibResponse = await _http.SendAsync(lrclibRequest, HttpCompletionOption.ResponseHeadersRead);
                        using var lrclibStream = await lrclibResponse.Content.ReadAsStreamAsync();
                        using var lrclibDoc = await JsonDocument.ParseAsync(lrclibStream);

                        if (lrclibDoc.RootElement.TryGetProperty("syncedLyrics", out var syn))
                        {
                            lrcText = syn.GetString() ?? "";
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"LRCLIB引擎失败: {ex.Message}"); }
                }

                return lrcText;
            }
            finally
            {
                // 必须释放锁，让下一首歌可以正常获取
                _fetchLock.Release();
            }
        }

        // 被底层渲染循环以 60FPS 极速调用，彻底无视流氓播放器的限制
        public void UpdateLyrics()
        {
            var now = DateTime.UtcNow;
            var dt = now - _lastUpdateTime;
            _lastUpdateTime = now; // 无论是否在播放，每一帧都更新绝对时间差

            // 拦截无效会话，但不再在这里拦截空歌词
            if (_currentSession == null) { CurrentLyric = ""; return; }

            // Just Solo LyricServer 直连歌词优先（仅通用模式下检测到 justsolo 会话时才会处于连接状态）
            if (_justSoloLyric.TryGetCurrentLyric(LyricDelayOffset, out string soloText, out float soloProgress))
            {
                CurrentLyric = IsLyricsEnabled ? soloText : "";
                CurrentLyricProgress = IsLyricsEnabled ? soloProgress : 0f;
                return;
            }

            try
            {
                var props = _currentSession.GetTimelineProperties();
                var smtcPos = props.Position;

                // SMTC 数据发生跳变 > 1.5秒（例如用户手动拖动了进度条，或者遇到了良心播放器主动更新了）
                if (Math.Abs((smtcPos - _lastSmtcPosition).TotalSeconds) > 1.5)
                {
                    _currentSimulatedPosition = smtcPos;
                    _lastSmtcPosition = smtcPos;
                }

                // 自己接管进度！不管有没有拿到歌词，底层的时间轴必须一直跟着播放状态往前走！
                if (IsPlaying)
                {
                    _currentSimulatedPosition += dt;
                }

                // 只有等时间轴正确走完后，如果还没歌词，我们再退出渲染拦截
                if (_lyrics.Length == 0) { CurrentLyric = ""; return; }

                string found = "";
                float progress = 0f;
                TimeSpan compensatedPosition = _currentSimulatedPosition + TimeSpan.FromSeconds(0.6 + LyricDelayOffset);
                for (int i = _lyrics.Length - 1; i >= 0; i--)
                {
                    if (compensatedPosition >= _lyrics[i].Time)
                    {
                        found = _lyrics[i].Text;
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
                CurrentLyric = IsLyricsEnabled ? found : "";
                CurrentLyricProgress = IsLyricsEnabled ? progress : 0f;
            }
            catch { }
        }
    }
}