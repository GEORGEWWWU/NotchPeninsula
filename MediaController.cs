using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace NotchPeninsula
{
    public class MediaController
    {
        public string Title { get; private set; } = "Notch Peninsula";
        public string Artist { get; private set; } = "Waiting for media...";
        public bool IsPlaying { get; private set; } = false;
        public bool IsActive { get; private set; } = false; // 是否有活跃媒体

        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public MediaController()
        {
            _ = InitializeAsync();
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
            if (_currentSession == null) return;

            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (props != null)
            {
                Title = string.IsNullOrEmpty(props.Title) ? "Unknown" : props.Title;
                Artist = string.IsNullOrEmpty(props.Artist) ? "Unknown" : props.Artist;
            }

            // 修正：正确获取播放状态
            var playbackInfo = _currentSession.GetPlaybackInfo();
            IsPlaying = playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
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