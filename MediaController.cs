using System.IO;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using SkiaSharp;

namespace NotchPeninsula
{
    public partial class MediaController
    {
        // 暴露给 UI 的静态配置和单例，方便极速调用
        public static MediaController? Instance { get; private set; }
        internal static string TargetPlatform = "other"; // 默认通用媒体
        internal static bool IsMediaControlEnabled = true; // 媒体开关

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
            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += async (s, e) => await RefreshProperties();
                _currentSession.PlaybackInfoChanged += async (s, e) => await RefreshProperties();

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
    }
}