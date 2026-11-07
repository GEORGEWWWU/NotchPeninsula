using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NotchPeninsula;

/// <summary>
/// 内置「媒体控制」实况窗：把正在播放的媒体收进岛体左侧的小气泡，封面跟着播放状态旋转。
///
/// <para>
/// 由 <see cref="MediaLiveWindowController"/> 在检测到媒体 3 秒后注册；本类的
/// <see cref="GetVisual"/> / <see cref="DrawInner"/> 只在渲染线程被调用，其余状态全部从
/// <see cref="MediaController"/> 只读，不加锁。
/// </para>
/// </summary>
internal sealed class MediaLiveWindow : ILiveWindow
{
    private const string LiveId = "builtin.media";

    private readonly MediaController _media;
    private readonly MediaPanelDetailPage _detail;

    public MediaLiveWindow(MediaController media)
    {
        _media = media;
        _detail = new MediaPanelDetailPage(media);
    }

    public string Id => LiveId;

    public IDetailPage? DetailPage => _detail;

    /// <summary>封面 + 播放/暂停按钮都在 <see cref="MediaPanelDetailPage"/> 里按原有媒体面板绘制。</summary>
    public LiveWindowVisual GetVisual()
    {
        var media = _media;
        return new LiveWindowVisual
        {
            // 没有时间轴（直播 / 部分网页）就不假装知道进度：环走不确定态。
            Progress = media.HasTimeline ? Math.Clamp(media.TimelineProgress, 0f, 1f) : -1f,
            // Title/Status 是退出动画期间的兜底文字（插件对象断开后宿主只画缓存文字）。
            Status = media.Artist,
            Title = media.Title,
            // 只有播放中才让宿主转；暂停时角度保持不动，就是「冻结」。
            SpinInner = media.IsPlaying
        };
    }

    public void DrawInner(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        // 用户要求封面大一点：直接铺满内圈（宿主已裁到内圈圆内），不再缩成圆角小方块。
        // 旋转的是整块封面内容，圆形裁切下依然能看出专辑图在转。
        float size = MathF.Min(rect.Width, rect.Height) * 1.18f;
        float cx = rect.MidX;
        float cy = rect.MidY;
        var cover = new SKRect(cx - size / 2f, cy - size / 2f, cx + size / 2f, cy + size / 2f);

        var thumb = _media.Thumbnail;
        if (thumb != null)
        {
            canvas.DrawBitmap(thumb, cover, CoverPaint);
            return;
        }

        // 没有封面（纯音频 / 会话还没回图）：画音符占位，同样能看出在转。
        FallbackFill.Color = frame.Theme.TextColor.WithAlpha(26);
        canvas.DrawCircle(cx, cy, size * 0.5f, FallbackFill);
        NotePaint.Typeface = FontConfig.Bold;
        NotePaint.Color = frame.Theme.TextColor.WithAlpha(230);
        NotePaint.TextAlign = SKTextAlign.Center;
        NotePaint.TextSize = size * 0.62f;
        canvas.DrawText("♪", cx, cy + size * 0.20f, NotePaint);
    }

    private static readonly SKPaint CoverPaint = new() { FilterQuality = SKFilterQuality.High };
    private static readonly SKPaint FallbackFill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint NotePaint = new() { IsAntialias = true };

    /// <summary>右键展开的浮层面板：复用岛内「原有媒体展开态」，只换位置和宿主。</summary>
    private sealed class MediaPanelDetailPage : IDetailPage
    {
        private readonly MediaController _media;

        public MediaPanelDetailPage(MediaController media) => _media = media;

        // 浮层宽度按当前歌词 / 译文 / 歌名自适应：长歌词不再被右边裁掉（原先固定 320 会切字）。
        public float MeasureWidth()
        {
            float textW = Renderer.MeasureMediaPanelTextWidth(_media);
            // 布局：左边距 20 + 封面 50 + 12；右侧留给频谱 / 留白 55 + 12。
            return Math.Clamp(82f + textW + 67f, 320f, 620f);
        }

        public float MeasureHeight() => _media.HasTimeline && Renderer.MediaInteractionMode == 1 ? 158f : 130f;

        // 和岛内媒体面板同一口径：3 秒没有鼠标动作就自动收起（宿主鼠标停在面板上会取消计时）。
        public TimeSpan AutoCollapseDelay => TimeSpan.FromSeconds(3);

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
            // alpha 传满：淡入淡出已由 LiveWindowRenderer 的合成层统一处理，这里再乘一次会变成双重衰减。
            => Renderer.DrawMediaFloatingPanel(canvas, rect, _media, 255);

        public WidgetHit HitTest(float x, float y, SKRect rect)
        {
            int button = Renderer.HitMediaFloatingButton(x, y, rect.Width, rect.Height);
            if (button >= 0)
            {
                string action = button == 0 ? "prev" : button == 1 ? "toggle" : "next";
                return new WidgetHit(action);
            }
            // _tlBar* 是上一帧浮层绘制时登记的局部坐标（桥里已把 left/right 设成面板坐标）。
            // 这里不能调 TimelineVisible()：桥画完就把 IsMediaExpanded 复原了，命中侧读到的是复原后的值。
            if (_media.HasTimeline && Renderer.MediaInteractionMode == 1 && Renderer.HitTimeline(x, y))
                return new WidgetHit("seek");
            return WidgetHit.None;
        }

        public void OnAction(string? action, float x, float y)
        {
            switch (action)
            {
                case "prev": _media.Previous(); break;
                case "toggle": _media.TogglePlayPause(); break;
                case "next": _media.Next(); break;
                case "seek":
                    // 单击时间轴 = 跳到该位置并提交；拖动由岛内面板负责，浮层只做点击跳转。
                    if (_media.BeginDrag(Renderer.TimelineRatio(x))) _media.EndDrag();
                    break;
            }
        }

        public void OnMouseMove(float x, float y)
            => Renderer.HoveredExpandedButton = Renderer.HitMediaFloatingButton(x, y, MeasureWidth(), MeasureHeight());

        public void OnMouseLeave() => Renderer.HoveredExpandedButton = -1;

        public void OnMouseUp(float x, float y)
        {
            // 浮层没有鼠标捕获，拖动收不到 move；这里兜底收尾，别让媒体停留在拖动状态。
            if (_media.IsDragging) _media.EndDrag();
        }
    }
}

/// <summary>
/// 媒体 → 实况窗的时序控制：检测到媒体先让主岛显示 3 秒，之后注册气泡并抑制岛内媒体；
/// 媒体结束 / 开关关闭 / 进入待机时收回气泡并恢复岛内媒体。
///
/// <para>
/// <see cref="Tick"/> 在 NotchWindow 渲染线程每帧调一次；<see cref="SetEnabled"/> 在 UI 线程调。
/// 注册 / 注销都通过 <see cref="LiveWindowHost"/>，注销即走「回到右边」的退出动画。
/// </para>
/// </summary>
internal static class MediaLiveWindowController
{
    private const string Owner = "builtin.media";
    private const long IslandPreviewMs = 3000;

    private static readonly object _gate = new();

    private static bool _enabled;        // 「使用实况窗展示媒体」开关（注册表持久化）
    private static bool _active;         // 气泡当前已注册
    private static IDisposable? _handle;
    private static long _mediaDetectedAt;   // 首次检测到媒体的时刻；0 = 还没检测到

    public static bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
    }

    public static bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>UI 线程：设置页开关 / 启动读注册表。关闭时立即注销，不等下一次 Tick。</summary>
    public static void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            _mediaDetectedAt = 0;   // 重新开启时重新计时 3 秒预览
        }

        if (!enabled)
        {
            Deactivate();
            Renderer.SuppressMediaInIsland = false;
        }
    }

    /// <summary>渲染线程每帧一次：管理 3 秒预览 → 注册 → 抑制岛内媒体 的时序。</summary>
    public static void Tick(MediaController media)
    {
        bool enabled;
        lock (_gate) enabled = _enabled;

        // 待机时岛体只显示待机内容：媒体先留在岛内，退出待机后再重新走一遍 3 秒预览。
        bool wantLive = enabled && media.IsActive && !Renderer.StandbyActive;
        if (!wantLive)
        {
            Deactivate();
            Renderer.SuppressMediaInIsland = false;
            lock (_gate) _mediaDetectedAt = 0;
            return;
        }

        if (IsActive)
        {
            Renderer.SuppressMediaInIsland = true;
            return;
        }

        long now = Environment.TickCount64;
        long detected;
        lock (_gate)
        {
            if (_mediaDetectedAt == 0) _mediaDetectedAt = now;
            detected = _mediaDetectedAt;
        }

        if (now - detected < IslandPreviewMs)
        {
            Renderer.SuppressMediaInIsland = false;   // 这 3 秒里主岛照常显示媒体
            return;
        }

        Activate(media);
        // 注册可能被宿主拒绝（极少）：此时媒体还在岛内，绝不能把岛内内容藏掉。
        Renderer.SuppressMediaInIsland = IsActive;
    }

    private static void Activate(MediaController media)
    {
        lock (_gate)
        {
            if (_active || !_enabled) return;

            var window = new MediaLiveWindow(media);
            var handle = LiveWindowHost.Register(Owner, window);
            if (handle == null) return;   // 宿主拒绝注册：保持岛内显示，别把媒体藏没了

            _handle = handle;
            _active = true;
        }

        // 用户可能在这 3 秒里点开过主岛的媒体面板：媒体已经挪到左边，主岛不该继续展开。
        Renderer.IsMediaExpanded = false;
        Logger.Info("[实况窗] 媒体已收进左侧实况窗（岛内不再重复显示）");
    }

    private static void Deactivate()
    {
        IDisposable? handle;
        lock (_gate)
        {
            if (!_active) return;
            handle = _handle;
            _handle = null;
            _active = false;
            _mediaDetectedAt = 0;
        }

        try { handle?.Dispose(); }
        catch (Exception ex) { Logger.Error("[实况窗] 注销媒体实况窗失败", ex); }
        Logger.Info("[实况窗] 媒体实况窗已收起");
    }
}
