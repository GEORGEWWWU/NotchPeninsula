using System;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace NotchPeninsula.Plugins;

// 插件只读数据桥（宿主 → 插件，单向）
//
// 用途：把宿主已经掌握的两类数据「发」给插件，插件不必自己去碰 SMTC / 通知监听：
//   1) 媒体（SMTC）状态快照  —— 由宿主按固定节奏采样 MediaController.Instance 得出；
//   2) 系统通知（Toast）      —— 由 NotchWindow 在收到通知时投递进来。
//
// 设计取舍（写给以后的维护者）：
//   * 只读、单向。插件不能通过这里反向影响宿主状态；播放控制请直接调
//     MediaController.Instance 的公开方法（它本来就是公开的）。
//   * 不做跨进程 / 不做内存共享。插件与宿主同进程，这里只交换托管对象，
//     封面这类原生资源一律「编码成 PNG 字节」再交出去 —— 插件拿到的是自己的副本，
//     不会和宿主的 SKBitmap 生命周期纠缠（宿主换图时不 Dispose 旧图，但把引用交出去
//     仍然意味着两个线程在同一个原生对象上读写，编码成字节是最省心的安全边界）。
//   * 有订阅者才采样。没人订阅时这个类零开销（不建定时器、不采样）。

/// <summary>SMTC 状态快照（不可变，可安全跨线程传递）。</summary>
public sealed class MediaSnapshot
{
    /// <summary>宿主当前是否接管着媒体会话。</summary>
    public bool IsActive { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    /// <summary>当前会话的 AUMID（跳转应用用）。</summary>
    public string AppId { get; init; } = "";

    /// <summary>当前歌词行（无歌词为空串）。</summary>
    public string Lyric { get; init; } = "";
    /// <summary>当前歌词行的译文（无译文为空串）。</summary>
    public string LyricTranslation { get; init; } = "";
    /// <summary>这首歌是否有译文轨道。</summary>
    public bool HasLyricTranslation { get; init; }
    /// <summary>当前歌词行的推进进度 0~1（卡拉 OK 扫光用）。</summary>
    public float LyricProgress { get; init; }

    public bool HasTimeline { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan Position { get; init; }
    /// <summary>时间轴进度 0~1。</summary>
    public float TimelineProgress { get; init; }
    public string Elapsed { get; init; } = "0:00";
    public string Total { get; init; } = "0:00";

    /// <summary>
    /// 封面 PNG 字节。无封面为 null。
    /// 只在「封面引用发生变化」时重新编码，其余采样直接复用同一份字节。
    /// </summary>
    public byte[]? CoverPng { get; init; }
    /// <summary>封面版本号：同一张图为同值；变化即说明封面换了。</summary>
    public long CoverVersion { get; init; }

    /// <summary>快照版本号，内容每变化一次自增。</summary>
    public long Version { get; init; }
}

/// <summary>系统通知（Toast）快照。</summary>
public sealed class ToastNotice
{
    /// <summary>
    /// 本条通知的稳定标识。同一条通知的内容补全（例如图标稍后异步解析出来）会复用同一个 Id，
    /// 订阅方据此判断「是新通知」还是「同一条通知的补充」，不要重启展示计时。
    /// </summary>
    public long NoticeId { get; init; }

    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Aumid { get; init; } = "";
    /// <summary>通知图标（PNG 字节，可能为 null）。</summary>
    public byte[]? IconPng { get; init; }
    /// <summary>同一条通知每发布一次自增（1 = 首帧，2 = 图标补全帧…）。</summary>
    public long Version { get; init; }
    public DateTime ReceivedUtc { get; init; }
}

/// <summary>
/// 宿主发布给插件的只读数据通道。
/// 插件订阅 MediaChanged / SystemNotification 即可，
/// 也可以随时用 CurrentMedia / LastNotification 拉一次最新值。
/// </summary>
public static class PluginDataBridge
{
    private static readonly object _gate = new();

    private static Action<MediaSnapshot>? _mediaChanged;
    private static Action<ToastNotice>? _notification;

    private static MediaSnapshot? _currentMedia;
    private static ToastNotice? _lastNotice;

    private static System.Threading.Timer? _sampler;
    private static bool _samplerStarted;

    // 「由 EnsureStarted() 而非订阅启动」的标志：这类采样是插件明确要求常驻的，
    // 不随订阅者归零而停（停表的条件见 MediaChanged.remove）。
    private static bool _explicitStarted;

    // ---- 采样缓存（只在锁内读写）----
    private static string _signature = "";
    private static SKBitmap? _lastThumb;
    private static byte[]? _coverPng;
    private static long _coverVersion;
    private static long _mediaVersion;
    private static long _noticeIdCounter;

    /// <summary>采样间隔。250ms 对歌词 / 进度条已经够顺，且不会给渲染线程添麻烦。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>最近一次媒体快照（无订阅者 / 未采样时为 null）。</summary>
    public static MediaSnapshot? CurrentMedia { get { lock (_gate) return _currentMedia; } }

    /// <summary>最近一条系统通知。</summary>
    public static ToastNotice? LastNotification { get { lock (_gate) return _lastNotice; } }

    /// <summary>媒体状态变化（在采样线程上触发，订阅方自行确保线程安全）。</summary>
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
                //    否则插件被卸载 / 禁用（正常退订）之后，这个 250ms 的采样器会陪着进程跑到底，
                //    一直读媒体属性、拼签名，还顺手把 _lastThumb 那张封面钉在静态字段上。
                if (_mediaChanged == null && !_explicitStarted) StopSamplerLocked();
            }
        }
    }

    /// <summary>系统通知到达（在宿主 UI 线程上触发）。</summary>
    public static event Action<ToastNotice>? SystemNotification
    {
        add { lock (_gate) { _notification += value; } }
        remove { lock (_gate) { _notification -= value; } }
    }

    /// <summary>
    /// 显式启动采样（不订阅事件、只想轮询 CurrentMedia 时用）。幂等。
    ///
    /// 与「订阅启动」的区别：这条路径启动的采样器不会因为订阅者归零而停 ——
    /// 调用方要的就是「一直采样」，停表条件里用 _explicitStarted 把它排除在外。
    /// </summary>
    public static void EnsureStarted()
    {
        lock (_gate)
        {
            _explicitStarted = true;
            EnsureSamplerLocked();
        }
    }

    // ---- 内部：采样 ----

    /// <summary>启动采样定时器（调用方必须已持 _gate）。</summary>
    private static void EnsureSamplerLocked()
    {
        if (_samplerStarted) return;
        _samplerStarted = true;
        _sampler = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, SampleInterval);
    }

    /// <summary>
    /// 停采样，并清掉这一轮留在静态字段上的快照与封面引用（调用方必须已持 _gate）。
    ///
    /// 光 Dispose 定时器不够：_currentMedia 里存着上一条 MediaSnapshot、_lastThumb 直接引用着
    /// 一张封面位图、_coverPng 是编码后的字节。这些静态字段会把一份已经没人要的媒体状态
    /// （含位图）钉到进程结束。订阅都退干净了，它们没有任何存在意义。
    /// </summary>
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

            // 签名：内容变了才发布。进度按千分位取整，避免每 250ms 都发一次「几乎没变」的快照。
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
                // 采样已经被停掉（订阅归零）、而这一次回调还在路上：什么都不做 ——
                // 否则会把 StopSamplerLocked 刚清空的快照又填回去，静态引用继续钉着。
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

        // 逐个 invoke 并各自兜异常：某个插件抛错不能影响其他订阅者。
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<MediaSnapshot>)d)(snapshot); }
            catch (Exception ex) { try { Logger.Warn("[PluginDataBridge] 插件媒体回调异常: " + ex.Message); } catch { } }
        }
    }

    // ---- 内部：宿主投递系统通知 ----

    /// <summary>宿主收到系统通知时调用（NotchWindow.OnToastDetected）。</summary>
    internal static void PublishNotification(ToastData toast)
    {
        if (toast == null) return;

        long noticeId;
        lock (_gate) noticeId = ++_noticeIdCounter;

        try
        {
            // 首帧：先用「手边就能拿到」的图标发出去（自定义图 / data\image 里同名别名 / 内置默认图），
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

        // 第二帧（可选）：去拿这条通知所属应用真正的图标。WinRT 取图是异步的，
        // 放到后台做，绝不拖慢宿主 UI 线程；失败就维持首帧的图标。
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

    /// <summary>
    /// 首帧图标（同步、便宜）：自定义图 → data\image 里以 AppName / ProcessName 命名的别名 →
    /// 宿主内置的默认通知图标。都拿不到才返回 null。
    /// </summary>
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

    /// <summary>
    /// 取这条系统通知所属应用的真实图标（WinRT 应用 logo）。
    /// 只在后台线程调用；任何一步失败都返回 null。
    /// </summary>
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

    /// <summary>把位图编码成 PNG 字节（读原生位图，可能抛异常，一律兜住）。</summary>
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
