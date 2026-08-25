using System;
using System.IO; // ★ 新增：用于 AsStreamForRead()
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using SkiaSharp; // ★ 新增：用于 SKBitmap

namespace NotchPeninsula
{
    public class MediaController
    {
        public string Title { get; private set; } = "Notch Peninsula"; //[cite: 3]
        public string Artist { get; private set; } = "Waiting for media..."; //[cite: 3]
        public bool IsPlaying { get; private set; } = false; //[cite: 3]
        public bool IsActive { get; private set; } = false; //[cite: 3]

        // ★ 新增：保存当前封面
        public SKBitmap? Thumbnail { get; private set; }

        private GlobalSystemMediaTransportControlsSession? _currentSession; //[cite: 3]

        public MediaController()
        {
            _ = InitializeAsync(); //[cite: 3]
        }

        private async Task InitializeAsync()
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager != null)
            {
                manager.SessionsChanged += async (s, e) => await UpdateSession(manager);
                await UpdateSession(manager);
            }
        }

        private async Task UpdateSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            var sessions = manager.GetSessions();
            // 修正：使用 GetPlaybackInfo()?.PlaybackStatus
            _currentSession = sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? sessions.FirstOrDefault();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += async (s, e) => await RefreshProperties();
                // 修正：事件名叫 PlaybackInfoChanged
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
            }
        }

        private async Task RefreshProperties()
        {
            if (_currentSession == null) return; //[cite: 3]

            var props = await _currentSession.TryGetMediaPropertiesAsync(); //[cite: 3]
            if (props != null)
            {
                Title = string.IsNullOrEmpty(props.Title) ? "Unknown" : props.Title; //[cite: 3]
                Artist = string.IsNullOrEmpty(props.Artist) ? "Unknown" : props.Artist; //[cite: 3]

                // ★ 新增：获取并解析封面
                if (props.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await props.Thumbnail.OpenReadAsync();
                        using var dotNetStream = stream.AsStreamForRead();

                        var oldThumb = Thumbnail; // 保存旧封面以便释放内存
                        Thumbnail = SKBitmap.Decode(dotNetStream);
                        oldThumb?.Dispose(); // 避免 Skia 内存泄漏
                    }
                    catch(Exception ex)
                    {
                        Logger.Error("封面解析失败，可能是格式不支持或流读取错误", ex);
                        Thumbnail = null;
                    }
                }
                else
                {
                    Thumbnail = null;
                }
            }

            var playbackInfo = _currentSession.GetPlaybackInfo(); //[cite: 3]
            IsPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; //[cite: 3]
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