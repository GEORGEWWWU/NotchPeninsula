using System.IO;
using SkiaSharp;

namespace NotchPeninsula.Plugins;

// 插件只读数据桥（宿主 → 插件，单向）
// 设计取舍（写给以后的维护者）：

public sealed class MediaSnapshot
{
    public bool IsActive { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AppId { get; init; } = "";

    public string Lyric { get; init; } = "";
    public string LyricTranslation { get; init; } = "";
    public bool HasLyricTranslation { get; init; }
    public float LyricProgress { get; init; }

    public bool HasTimeline { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan Position { get; init; }
    public float TimelineProgress { get; init; }
    public string Elapsed { get; init; } = "0:00";
    public string Total { get; init; } = "0:00";

    public byte[]? CoverPng { get; init; }
    public long CoverVersion { get; init; }

    public long Version { get; init; }
}

public sealed class ToastNotice
{
    public long NoticeId { get; init; }

    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Aumid { get; init; } = "";
    public byte[]? IconPng { get; init; }
    public long Version { get; init; }
    public DateTime ReceivedUtc { get; init; }
}

public static class PluginDataBridge
{
    private static readonly object _gate = new();

    private static Action<MediaSnapshot>? _mediaChanged;
    private static Action<ToastNotice>? _notification;

    private static MediaSnapshot? _currentMedia;
    private static ToastNotice? _lastNotice;

    private static System.Threading.Timer? _sampler;
    private static bool _samplerStarted;

    private static bool _explicitStarted;

    // ---- 采样缓存（只在锁内读写）----
    private static string _signature = "";
    private static SKBitmap? _lastThumb;
    private static byte[]? _coverPng;
    private static long _coverVersion;
    private static long _mediaVersion;
    private static long _noticeIdCounter;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    public static MediaSnapshot? CurrentMedia { get { lock (_gate) return _currentMedia; } }

    public static ToastNotice? LastNotification { get { lock (_gate) return _lastNotice; } }

    public static event Action<MediaSnapshot>? MediaChanged
    {
        add
        {
            lock (_gate)
            {
                _mediaChanged += value;
                EnsureSamplerLocked();
            }
        }
        remove
        {
            lock (_gate)
            {
                _mediaChanged -= value;
                // 订阅者归零、又没有插件显式要求常驻 ⇒ 停表。
                if (_mediaChanged == null && !_explicitStarted) StopSamplerLocked();
            }
        }
    }

    public static event Action<ToastNotice>? SystemNotification
    {
        add { lock (_gate) { _notification += value; } }
        remove { lock (_gate) { _notification -= value; } }
    }

    public static void EnsureStarted()
    {
        lock (_gate)
        {
            _explicitStarted = true;
            EnsureSamplerLocked();
        }
    }

    // ---- 内部：采样 ----

    private static void EnsureSamplerLocked()
    {
        if (_samplerStarted) return;
        _samplerStarted = true;
        _sampler = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, SampleInterval);
    }

    private static void StopSamplerLocked()
    {
        if (!_samplerStarted) return;
        _samplerStarted = false;
        _sampler?.Dispose();
        _sampler = null;
        _currentMedia = null;
        _signature = "";
        _lastThumb = null;
        _coverPng = null;
    }

    private static void Tick()
    {
        try
        {
            var media = MediaController.Instance;
            if (media == null) return;

            string title = SafeStr(() => media.Title);
            string artist = SafeStr(() => media.Artist);
            bool isActive = SafeBool(() => media.IsActive);
            bool isPlaying = SafeBool(() => media.IsPlaying);
            string lyric = SafeStr(() => media.CurrentLyric);
            float lyricProgress = SafeFloat(() => media.CurrentLyricProgress);
            bool hasTimeline = SafeBool(() => media.HasTimeline);
            float timelineProgress = SafeFloat(() => media.TimelineProgress);
            TimeSpan duration = SafeTime(() => media.Duration);
            string elapsed = SafeStr(() => media.TimelineElapsed, "0:00");
            string total = SafeStr(() => media.TimelineTotal, "0:00");
            string appId = SafeStr(() => media.CurrentAppId);

            string signature = string.Concat(
                isActive ? "1" : "0", isPlaying ? "1" : "0",
                "|", title, "|", artist, "|", appId,
                "|", lyric, "|", (int)(lyricProgress * 200f),
                "|", hasTimeline ? "1" : "0", "|", (int)(timelineProgress * 1000f),
                "|", elapsed, "|", total);

            bool changed = false;
            MediaSnapshot? snapshot = null;

            lock (_gate)
            {
                if (!_samplerStarted) return;

                if (signature == _signature) return;

                // 封面：引用没换就复用上次编码出来的字节，换了才重新编码
                SKBitmap? thumb = null;
                try { thumb = media.Thumbnail; } catch { }
                if (!ReferenceEquals(thumb, _lastThumb))
                {
                    _lastThumb = thumb;
                    _coverPng = EncodePng(thumb);
                    _coverVersion++;
                }

                _signature = signature;
                _mediaVersion++;

                TimeSpan position = duration > TimeSpan.Zero
                    ? TimeSpan.FromSeconds(duration.TotalSeconds * Math.Clamp(timelineProgress, 0f, 1f))
                    : TimeSpan.Zero;

                snapshot = new MediaSnapshot
                {
                    IsActive = isActive,
                    IsPlaying = isPlaying,
                    Title = title,
                    Artist = artist,
                    AppId = appId,
                    Lyric = lyric,
                    LyricTranslation = SafeStr(() => media.CurrentLyricTranslation),
                    HasLyricTranslation = SafeBool(() => media.HasLyricTranslation),
                    LyricProgress = lyricProgress,
                    HasTimeline = hasTimeline,
                    Duration = duration,
                    Position = position,
                    TimelineProgress = timelineProgress,
                    Elapsed = elapsed,
                    Total = total,
                    CoverPng = _coverPng,
                    CoverVersion = _coverVersion,
                    Version = _mediaVersion,
                };

                _currentMedia = snapshot;
                changed = _mediaChanged != null;
            }

            if (changed) RaiseMedia(snapshot!);
        }
        catch (Exception ex)
        {
            _ = ex;
            try { Logger.DebugThrottled("[PluginDataBridge] 媒体采样异常（10s 内只记一次）"); } catch { }
        }
    }

    private static void RaiseMedia(MediaSnapshot snapshot)
    {
        Action<MediaSnapshot>? handler;
        lock (_gate) handler = _mediaChanged;
        if (handler == null) return;

        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<MediaSnapshot>)d)(snapshot); }
            catch (Exception ex) { try { Logger.Warn("[PluginDataBridge] 插件媒体回调异常: " + ex.Message); } catch { } }
        }
    }

    // ---- 内部：宿主投递系统通知 ----

    internal static void PublishNotification(ToastData toast)
    {
        if (toast == null) return;

        long noticeId;
        lock (_gate) noticeId = ++_noticeIdCounter;

        try
        {
            // 保证组件立刻有画面；拿不到也不阻塞。
            Publish(new ToastNotice
            {
                NoticeId = noticeId,
                AppName = toast.AppName,
                Title = toast.Title,
                Body = toast.Body,
                Aumid = toast.Aumid,
                IconPng = EncodePng(ResolveFallbackIcon(toast)),
                Version = 1,
                ReceivedUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            try { Logger.Warn("[PluginDataBridge] 发布系统通知失败: " + ex.Message); } catch { }
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var logo = TryGetAppLogo(toast);
                if (logo == null) return;

                var png = EncodePng(logo);
                if (png == null || png.Length == 0) return;

                Publish(new ToastNotice
                {
                    NoticeId = noticeId,
                    AppName = toast.AppName,
                    Title = toast.Title,
                    Body = toast.Body,
                    Aumid = toast.Aumid,
                    IconPng = png,
                    Version = 2,
                    ReceivedUtc = DateTime.UtcNow,
                });
            }
            catch
            {
                try { Logger.DebugThrottled("[PluginDataBridge] 取通知应用图标失败（10s 内只记一次）"); } catch { }
            }
        });
    }

    private static void Publish(ToastNotice notice)
    {
        Action<ToastNotice>? handler;
        lock (_gate)
        {
            _lastNotice = notice;
            handler = _notification;
        }

        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<ToastNotice>)d)(notice); }
            catch (Exception ex) { try { Logger.Warn("[PluginDataBridge] 插件通知回调异常: " + ex.Message); } catch { } }
        }
    }

    private static SKBitmap? ResolveFallbackIcon(ToastData toast)
    {
        try
        {
            var custom = Safe(() => toast.CustomIcon);
            if (custom != null) return custom;
        }
        catch { }

        try
        {
            var bmp = ToastIconProvider.Resolve(toast.ProcessName);
            if (bmp != null) return bmp;
            bmp = ToastIconProvider.Resolve(toast.AppName);
            if (bmp != null) return bmp;
        }
        catch { }

        try { return ToastIconProvider.Resolve("windows"); } catch { return null; }
    }

    private static SKBitmap? TryGetAppLogo(ToastData toast)
    {
        try
        {
            var info = toast.InternalNotification?.AppInfo?.DisplayInfo;
            if (info == null) return null;

            var logoRef = info.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logoRef == null) return null;

            using var winrt = logoRef.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using var net = winrt.AsStreamForRead();
            return SKBitmap.Decode(net);
        }
        catch { return null; }
    }

    // ---- 工具 ----

    private static byte[]? EncodePng(SKBitmap? bitmap)
    {
        if (bitmap == null) return null;
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data?.ToArray();
        }
        catch { return null; }
    }

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try { return read(); } catch { return null; }
    }

    private static string SafeStr(Func<string?> read, string fallback = "")
    {
        try { return read() ?? fallback; } catch { return fallback; }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }

    private static float SafeFloat(Func<float> read)
    {
        try { return read(); } catch { return 0f; }
    }

    private static TimeSpan SafeTime(Func<TimeSpan> read)
    {
        try { return read(); } catch { return TimeSpan.Zero; }
    }
}
