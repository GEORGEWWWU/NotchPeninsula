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
        internal static bool IsLyricsEnabled = true;
        internal static float LyricDelayOffset = 0f;
        private static readonly HttpClient _http = new(new HttpClientHandler // 注入无条件放行的证书校验回调，彻底解决 SSL 报错，同时增加超时容错
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        })
        { Timeout = TimeSpan.FromSeconds(4) };
        private (TimeSpan Time, string Text)[] _lyrics = Array.Empty<(TimeSpan, string)>();
        public string CurrentLyric { get; private set; } = "";
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
        private bool _isBrowserSession;   // 当前会话是否为浏览器 (Chrome/Edge)，启用视频标题清理

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

        private async Task UpdateSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? newSession = null;

            // 1. 如果总开关打开，执行精确的平台过滤
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
            _isBrowserSession = MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Chrome")
                             || MediaLogoProvider.IsPlatform(newSession?.SourceAppUserModelId, "Edge");

            // 2. 如果目标会话没变，只需刷新属性，避免重复订阅事件浪费内存
            if (_currentSession != null && newSession != null && _currentSession.SourceAppUserModelId == newSession.SourceAppUserModelId)
            {
                await RefreshProperties();
                IsActive = true;
                return;
            }

            // 3. 切换到了新的会话（或者置空）
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
                Thumbnail?.Dispose();
                Thumbnail = null;
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
                    // 尝试安全读取，如果底层 COM 对象炸了，外层 try-catch 会兜底
                    // PotPlayer 本地文件通常没有元数据，无歌名时直接隐藏而非显示 "Unknown"
                    Title = string.IsNullOrEmpty(props.Title) ? (_isPotPlayerSession ? "" : "Unknown") : props.Title;

                    // 浏览器模式：统一清理标题后缀 + 提取「正在播放: 歌名 - 歌手」
                    string browserArtist = "";
                    if (_isBrowserSession)
                        Title = CleanBrowserTitle(Title, out browserArtist);

                    // 浏览器视频没有艺术家概念，隐藏 Artist；若从标题提取到歌手则优先使用
                    Artist = _isBilibiliSession ? "" : (!string.IsNullOrEmpty(browserArtist) ? browserArtist
                            : (string.IsNullOrEmpty(props.Artist) ? "" : props.Artist));

                    // 统一封面管理：PotPlayer/bilibili 始终用站标；浏览器无 SMTC 封面时用站标兜底
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
                    else Thumbnail = null;
                }
            }
            catch (Exception ex)
            {
                // 捕获网页视频等非常规媒体源导致的底层 COM 异常
                Logger.Error("读取媒体属性失败，可能遇到不规范的媒体源", ex);
                Title = _isPotPlayerSession ? "" : "Unknown";
                Artist = (_isBilibiliSession || _isPotPlayerSession) ? "" : "Unknown";
                Thumbnail = null;
            }

            // 播放状态的读取也建议加上保护
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

            // 独立歌词进程！只有真正的切歌才允许重新抓取和重置时间轴。
            // 免疫系统通知弹窗导致的音量闪避干扰
            if (Title != _lastFetchedTitle || Artist != _lastFetchedArtist)
            {
                _lastFetchedTitle = Title;
                _lastFetchedArtist = Artist;
                _ = FetchLyricsAsync(Title, Artist, durationSec);
            }
        }

        // 浏览器视频站标题后缀列表：命中任一后缀即判定为浏览器视频模式，并统一删除该后缀
        // 注意：判定要用清理前的原始标题（清理后后缀已被删掉，无法再判）
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

        // 浏览器 SMTC 标题「正在播放: 歌名 - 歌手」提取正则（同时匹配全角/半角冒号）
        [GeneratedRegex(@"^正在播放[:：]\s*(.*?)\s*-\s*(.*)$")]
        private static partial Regex PlayingTitleRegex();

        // 统一标题清理：命中浏览器视频后缀则删除该后缀并返回，否则原样返回
        // 若标题为「正在播放: 歌名 - 歌手」格式，同时提取歌手并带回
        private static string CleanBrowserTitle(string title, out string artist)
        {
            artist = "";

            // 判定基于清理前的原始标题（仅去结尾空白归一化），命中后直接删除后缀
            var trimmed = title.TrimEnd();

            // 1. 提取「正在播放: 歌名 - 歌手」格式（" - "为分隔符，歌名取短、歌手取到结尾）
            var playingMatch = PlayingTitleRegex().Match(trimmed);
            if (playingMatch.Success)
            {
                artist = playingMatch.Groups[2].Value.Trim();
                trimmed = playingMatch.Groups[1].Value.Trim();
            }
            // 2. 仅命中「正在播放: 」前缀但无「 - 」分隔时，去掉前缀
            else if (trimmed.StartsWith("正在播放", StringComparison.Ordinal)
                     && trimmed.Length > 4 && (trimmed[4] == ':' || trimmed[4] == '：'))
            {
                trimmed = trimmed[5..].Trim();
            }

            // 3. 删除浏览器视频站标题后缀
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
            _currentSimulatedPosition = TimeSpan.Zero; // 切歌时彻底清零时间
            _lastSmtcPosition = TimeSpan.Zero;
            if (string.IsNullOrEmpty(title) || _isBilibiliSession || _isBrowserSession || _isPotPlayerSession) return;

            string query = Uri.EscapeDataString($"{title} {artist}");
            string lrcText = "";
            string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

            // ====== 引擎 1：QQ音乐 (优先) ======
            try
            {
                _http.DefaultRequestHeaders.Clear();
                _http.DefaultRequestHeaders.Add("User-Agent", ua);

                string searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&n=5&format=json";
                var searchJson = await _http.GetStringAsync(searchUrl);
                using var searchDoc = JsonDocument.Parse(searchJson);

                string songmid = "";
                if (searchDoc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("song", out var songData) &&
                    songData.TryGetProperty("list", out var list))
                {
                    foreach (var song in list.EnumerateArray())
                    {
                        string name = song.GetProperty("songname").GetString() ?? "";
                        if (name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase))
                        {
                            songmid = song.GetProperty("songmid").GetString() ?? "";
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(songmid))
                {
                    _http.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");
                    string lyricUrl = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songmid}&format=json&nobase64=1";
                    var lyricJson = await _http.GetStringAsync(lyricUrl);
                    using var lyricDoc = JsonDocument.Parse(lyricJson);

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

            // ====== 引擎 2：网易云 API (参考 Rust 源码兜底) ======
            if (string.IsNullOrEmpty(lrcText))
            {
                try
                {
                    _http.DefaultRequestHeaders.Clear();
                    _http.DefaultRequestHeaders.Add("User-Agent", ua);
                    _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com");
                    // 构造随机国内IP绕过风控
                    _http.DefaultRequestHeaders.Add("X-Real-IP", $"114.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}.{new Random().Next(1, 255)}");

                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("s", $"{title} {artist}"),
                        new KeyValuePair<string, string>("type", "1"),
                        new KeyValuePair<string, string>("limit", "5"),
                        new KeyValuePair<string, string>("offset", "0")
                    });

                    var response = await _http.PostAsync("https://music.163.com/api/search/get/web", content);
                    var searchJson = await response.Content.ReadAsStringAsync();
                    using var searchDoc = JsonDocument.Parse(searchJson);

                    long songId = 0;
                    if (searchDoc.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("songs", out var songs))
                    {
                        foreach (var song in songs.EnumerateArray())
                        {
                            string name = song.GetProperty("name").GetString() ?? "";
                            if (name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(name, StringComparison.OrdinalIgnoreCase))
                            {
                                songId = song.GetProperty("id").GetInt64();
                                break;
                            }
                        }
                    }

                    if (songId > 0)
                    {
                        string lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1";
                        var lyricJson = await _http.GetStringAsync(lyricUrl);
                        using var lyricDoc = JsonDocument.Parse(lyricJson);
                        if (lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) &&
                            lrc.TryGetProperty("lyric", out var lyricStr))
                        {
                            lrcText = lyricStr.GetString() ?? "";
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn($"网易云引擎失败: {ex.Message}"); }
            }

            // ====== 引擎 3：LRCLIB (修复 400 报错) ======
            if (string.IsNullOrEmpty(lrcText))
            {
                try
                {
                    _http.DefaultRequestHeaders.Clear();
                    _http.DefaultRequestHeaders.Add("User-Agent", ua);
                    // 核心修复：仅在 durationSec 大于 0 时附加 duration 参数
                    string lrclibUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                    if (durationSec > 0) lrclibUrl += $"&duration={durationSec}";

                    var lrclibJson = await _http.GetStringAsync(lrclibUrl);
                    using var lrclibDoc = JsonDocument.Parse(lrclibJson);

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
        }

        // 被底层渲染循环以 60FPS 极速调用，彻底无视流氓播放器的限制
        public void UpdateLyrics()
        {
            var now = DateTime.UtcNow;
            var dt = now - _lastUpdateTime;
            _lastUpdateTime = now; // 无论是否在播放，每一帧都更新绝对时间差

            if (_lyrics.Length == 0 || _currentSession == null) { CurrentLyric = ""; return; }
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

                // 自己接管进度！不管网易云更不更新，我们在 60FPS 循环里自行加上 DeltaTime
                if (IsPlaying)
                {
                    _currentSimulatedPosition += dt;
                }

                // 从后往前找当前时间对应的歌词，加上 0.6 秒的系统通信延迟补偿
                string found = "";
                TimeSpan compensatedPosition = _currentSimulatedPosition + TimeSpan.FromSeconds(0.6 + LyricDelayOffset);
                for (int i = _lyrics.Length - 1; i >= 0; i--)
                {
                    if (compensatedPosition >= _lyrics[i].Time) { found = _lyrics[i].Text; break; }
                }
                // 后台时间轴引擎照常跑，仅在最后输出给渲染器时做拦截
                // 输出为空时，Renderer.cs 会自动回退显示标题和艺术家
                CurrentLyric = IsLyricsEnabled ? found : "";
            }
            catch { }
        }
    }
}