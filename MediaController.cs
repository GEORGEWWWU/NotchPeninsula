using System.IO;
using Windows.Media.Control;
using SkiaSharp;

namespace NotchPeninsula
{
    public class MediaController
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
        private bool _isBilibiliSession; // 通用模式下当前会话是否为 bilibili，用于隐藏 Artist

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

            // 命中 bilibili 会话时打标记，供刷新时隐藏 Artist
            _isBilibiliSession = newSession != null
                                 && newSession.SourceAppUserModelId.Contains("bilibili", StringComparison.OrdinalIgnoreCase);

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
                    Title = string.IsNullOrEmpty(props.Title) ? "Unknown" : props.Title;
                    // 浏览器视频没有艺术家概念，隐藏 Artist
                    Artist = _isBilibiliSession ? "" : (string.IsNullOrEmpty(props.Artist) ? "" : props.Artist);

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
                Title = "Unknown";
                Artist = _isBilibiliSession ? "" : "Unknown";
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