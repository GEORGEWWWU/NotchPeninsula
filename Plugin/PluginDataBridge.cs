using System.IO;
using SkiaSharp;

namespace NotchPeninsula.Plugins;

// 插件只读数据桥（宿主 → 插件，单向）
// 设计取舍（写给以后的维护者）：

/// <summary>
/// 一行歌词的逐字时间轴（宿主 yrc 表的只读视图）。
///
/// 每行一个实例、**行变了才换引用**，插件可以放心缓存；<see cref="EndMs"/> / <see cref="CumChars"/>
/// 直接指向宿主的数组（构建后不再改动），插件不要写它。
/// </summary>
public sealed class LyricWordTimeline
{
    /// <summary>每一段的结束时刻（毫秒，相对本行起点，单调递增）。</summary>
    public int[] EndMs { get; init; } = System.Array.Empty<int>();

    /// <summary>到这一段结束为止累计唱到的字数（与 <see cref="EndMs"/> 一一对应）。</summary>
    public int[] CumChars { get; init; } = System.Array.Empty<int>();

    /// <summary>本行有效字数（不含空白）。</summary>
    public int TotalChars { get; init; }

    /// <summary>
    /// 按「本行已播放毫秒」算逐字扫光进度 0~1。
    ///
    /// 与宿主灵动岛用的是**同一套算法**（MediaController.ComputeWordAlignedProgress）：
    /// 逐字边界精确对齐、字内线性插值。插件在 60fps 上反复调用它，扫光就与岛上完全同步，
    /// 不用再靠 250ms 一次的 <see cref="MediaSnapshot.LyricProgress"/> 猜速率。
    /// </summary>
    public float ProgressAt(double elapsedMs)
    {
        int n = EndMs.Length;
        if (n == 0 || TotalChars <= 0 || CumChars.Length != n) return 0f;
        if (elapsedMs >= EndMs[n - 1]) return 1f;   // 唱完这一句；换行交给宿主
        if (elapsedMs <= 0) return 0f;

        int lo = 0, hi = n - 1, done = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (EndMs[mid] <= elapsedMs) { done = mid + 1; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (done >= n) return 1f;

        int doneChars = done > 0 ? CumChars[done - 1] : 0;
        int spanStart = done > 0 ? EndMs[done - 1] : 0;
        int spanEnd = EndMs[done];
        float frac = spanEnd > spanStart ? (float)((elapsedMs - spanStart) / (spanEnd - spanStart)) : 0f;
        int spanChars = CumChars[done] - doneChars;

        return System.Math.Clamp((doneChars + frac * spanChars) / TotalChars, 0f, 1f);
    }
}

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

    /// <summary>本行逐字时间轴；没有 yrc 或这一行没有逐字时间戳时为 null。</summary>
    public LyricWordTimeline? LyricTimeline { get; init; }

    /// <summary>宿主报的「本行已播放毫秒」（含歌词延迟，暂停不涨）。配 <see cref="LyricTimeline"/> 用。</summary>
    public double LyricLineElapsedMs { get; init; }

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
    internal WeakReference<ToastData>? VisualSource { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>用于观察宿主异步补齐图标或头像，无需复制或读取像素。</summary>
    public int IconRevision => VisualSource?.TryGetTarget(out var toast) == true
        && !toast.IconsReleased && Renderer.ResolveToastVisual(toast, out _, includeDefaultApp: true) is { } bitmap
        ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bitmap) : 0;

    /// <summary>在调用方的画布绘制宿主已有的图标。位图的所有权留在宿主。</summary>
    public void DrawIcon(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        if (VisualSource?.TryGetTarget(out var toast) != true || toast == null
            || !System.Threading.Monitor.TryEnter(toast)) return;
        try
        {
            if (!ToastIconProvider.TryEnterBitmapRead()) return;
            try { Renderer.DrawSharedToastIcon(canvas, toast, rect, paint); }
            finally { ToastIconProvider.ExitBitmapRead(); }
        }
        finally { System.Threading.Monitor.Exit(toast); }
    }

    public long NoticeId { get; init; }

    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Aumid { get; init; } = "";
    public byte[]? IconPng { get; init; }

    /// <summary>消息头像（如 QQ 好友 / 群头像）。**有值就优先于 <see cref="IconPng"/>**：
    /// 插件 / HTTP / MSP 通道的通知可能带，Windows 系统通知拿不到头像，恒为 null。</summary>
    public byte[]? AvatarPng { get; init; }

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
    private static int _ticking;            // Tick 重入闸（见 Tick 注释）

    // 逐字时间轴缓存：宿主每行只构建一份 EndMs 数组，引用变了才重建（见 Tick）
    private static LyricWordTimeline? _wordTimeline;
    private static int[]? _wordTimelineEnds;
    private static int _wordTimelineChars;

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
        _wordTimeline = null;
        _wordTimelineEnds = null;
        _wordTimelineChars = 0;
    }

    private static void Tick()
    {
        // 定时器回调允许重入（250ms 一拍，编码封面偶尔超时就会重叠）：重叠会让两份快照
        // 乱序投递，插件按版本号缓存的封面就可能被旧的那份盖回去。跳过这一拍，下一拍补上。
        if (System.Threading.Interlocked.Exchange(ref _ticking, 1) == 1) return;
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

            // 逐字时间轴（新传输内容）：本行的段结束时刻 / 累计字数 / 总字数 + 本行已播放毫秒。
            // 数组直接引用宿主的 yrc 表（只读共享），这里只负责「行变了就重建一个时间轴对象」。
            int[] wordEnds = SafeArr(() => media.CurrentLyricWordEndMs);
            int[] wordCums = SafeArr(() => media.CurrentLyricWordCumChars);
            int totalChars = SafeInt(() => media.CurrentLyricTotalChars);
            double lineElapsed = SafeDouble(() => media.CurrentLyricLineElapsedMs);

            string signature = string.Concat(
                isActive ? "1" : "0", isPlaying ? "1" : "0",
                "|", title, "|", artist, "|", appId,
                "|", lyric, "|", (int)(lyricProgress * 200f),
                "|", hasTimeline ? "1" : "0", "|", (int)(timelineProgress * 1000f),
                "|", elapsed, "|", total,
                "|", (int)(lineElapsed / 40.0));   // 逐字扫光在走 → 40ms 粒度就够，别把 250ms 采样顶爆

            bool changed = false;
            MediaSnapshot? snapshot = null;

            // 封面：引用没换就复用上次编码出来的字节，换了才重新编码。
            // 必须在锁外先读一次引用 —— 下面要用它判断「这一拍是不是只有封面变了」。
            SKBitmap? thumb = null;
            try { thumb = media.Thumbnail; } catch { }

            lock (_gate)
            {
                if (!_samplerStarted) return;

                // ⚠ 封面变化必须单独算一种「要发快照」的信号。
                //   封面是异步解析的（先贴应用图标兜底，真封面后到），往往正好落在
                //   歌名 / 进度都没变的那一拍。只比 signature 会把这一拍整条丢掉 ——
                //   宿主自己每帧直接读 Thumbnail 所以看着正常，插件却永远停在兜底图标上。
                bool coverChanged = !ReferenceEquals(thumb, _lastThumb);

                if (signature == _signature && !coverChanged) return;

                if (coverChanged)
                {
                    _lastThumb = thumb;
                    _coverPng = EncodePng(thumb);
                    _coverVersion++;
                }

                // 逐字时间轴：宿主每行只构建一份 EndMs 数组，引用变了才重建对象 ——
                // 250ms 一拍不会重复分配，插件拿到的引用也是稳定的，能安心缓存。
                if (!ReferenceEquals(wordEnds, _wordTimelineEnds) || totalChars != _wordTimelineChars)
                {
                    _wordTimelineEnds = wordEnds;
                    _wordTimelineChars = totalChars;
                    _wordTimeline = wordEnds.Length > 0 && wordCums.Length == wordEnds.Length && totalChars > 0
                        ? new LyricWordTimeline { EndMs = wordEnds, CumChars = wordCums, TotalChars = totalChars }
                        : null;
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
                    LyricTimeline = _wordTimeline,
                    LyricLineElapsedMs = lineElapsed,
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
        finally
        {
            System.Threading.Interlocked.Exchange(ref _ticking, 0);
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
            // 新插件直接画宿主位图。保留旧订阅者使用的头像 PNG。
            Publish(new ToastNotice
            {
                NoticeId = noticeId,
                VisualSource = new WeakReference<ToastData>(toast),
                Duration = toast.Duration,
                AppName = toast.AppName,
                Title = toast.Title,
                Body = toast.Body,
                Aumid = toast.Aumid,
                IconPng = null,
                AvatarPng = EncodePng(Safe(() => toast.Avatar)),
                Version = 1,
                ReceivedUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            try { Logger.Warn("[PluginDataBridge] 发布系统通知失败: " + ex.Message); } catch { }
            return;
        }


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

    // 读应用显示名下的 64×64 logo。宿主 toast 层（Toast.ExtractToastData）在快照新鲜时
    // 也会调它给 CustomIcon 兜底 —— internal：只给宿主内部复用，不进插件契约。
    // 每个失败出口都落一条 Debug：哪些应用取不到、卡在哪一步，没有日志就只能瞎猜。
    internal static SKBitmap? TryGetAppLogo(ToastData toast)
    {
        try
        {
            var info = toast.InternalNotification?.AppInfo?.DisplayInfo;
            if (info == null)
            {
                Logger.Debug($"[Toast图标] AppInfo/DisplayInfo 为空，logo 取不到（{toast.AppName} / {toast.Aumid}）");
                return null;
            }

            var logoRef = info.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logoRef == null)
            {
                Logger.Debug($"[Toast图标] GetLogo 返回空（{info.DisplayName} / {toast.Aumid}）");
                return null;
            }

            using var winrt = logoRef.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using var net = winrt.AsStreamForRead();
            var bmp = SKBitmap.Decode(net);
            if (bmp == null)
                Logger.Debug($"[Toast图标] logo 流解码失败（{info.DisplayName}，{winrt.Size} 字节）");
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Toast图标] logo 读取异常（{toast.AppName} / {toast.Aumid}）: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

    private static int SafeInt(Func<int> read)
    {
        try { return read(); } catch { return 0; }
    }

    private static double SafeDouble(Func<double> read)
    {
        try { return read(); } catch { return 0d; }
    }

    private static int[] SafeArr(Func<int[]> read)
    {
        try { return read() ?? System.Array.Empty<int>(); } catch { return System.Array.Empty<int>(); }
    }

    private static TimeSpan SafeTime(Func<TimeSpan> read)
    {
        try { return read(); } catch { return TimeSpan.Zero; }
    }
}
