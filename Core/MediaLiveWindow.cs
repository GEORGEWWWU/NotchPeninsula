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

    /// <summary>
    /// 直播 / 部分网页没有时间轴 = 没有进度条：这时封面会撑满整颗气泡，
    /// 播放中照旧让它转，用来代替原来那圈「转圈扫描段」。
    /// 走带默认实现的接口成员（不是 LiveWindowVisual 字段），旧插件二进制不受影响。
    /// </summary>
    public bool SpinWhenNoProgress => _media.IsPlaying;

    /// <summary>
    /// 静默模式：媒体实况窗开着就代表用户想看见它 —— 气泡显示时岛要回来陪它
    ///（气泡挂在岛左侧，岛退场它也会跟着出画面）。这个开关本来就在「媒体设置」里默认关闭，是用户的显式选择。
    /// </summary>
    public bool CountsAsImportantInfo => true;

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

        // 和主岛展开媒体面板同一份尺寸，宽度固定（统一 350）、高度看有没有时间轴：
        // 直接复用 Renderer 里那两个口径，不在这里再写一套数字。
        public bool ResizeWithContent => true;   // 时间轴可能晚到 / 消失：高度跟着主岛面板实时变

        public float MeasureWidth() => Renderer.MEDIA_EXPANDED_WIDTH;

        // 力按展开态算：详情页就是展开态媒体面板，交互方式设成「直接交互」也照样要进度条。
        public float MeasureHeight() => Renderer.GetExpandedHeight(_media, forceExpanded: true);

        // 和岛内媒体面板同一口径：3 秒没有鼠标动作就自动收起（宿主鼠标停在面板上会取消计时）。
        public TimeSpan AutoCollapseDelay => TimeSpan.FromSeconds(3);

        // 左键双击封面 → 跳回正在放媒体的那个应用。开关与主岛「双击封面跳转应用」同一个，
        // 关掉时连双击都不声明，宿主照旧把这一下消费掉（不会漏到待机模式切换）。
        public bool AcceptsDoubleClick => MediaController.IsAppLaunchEnabled;

        public void OnLeftDoubleClick(float x, float y)
        {
            if (!Renderer.HitMediaPanelCover(x, y)) return;
            _media.OpenCurrentApp();
        }

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
            if (Renderer.HitMediaFloatingTimeline(_media, x, y, rect))
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
                    if (_media.BeginDrag(Renderer.MediaFloatingTimelineRatio(_media, x, MeasureWidth(), MeasureHeight())))
                        _media.EndDrag();
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
    public const string Owner = "builtin.media";
    private const long IslandPreviewMs = 3000;

    /// <summary>
    /// 静默编排：气泡出生动画（0.46s）站稳后、压岛前的守护时长 ——
    /// 顺序是「主岛展示 3s 媒体 → 从媒体态岛缘生成实况窗 → 气泡站稳 → 岛缩没 → 气泡组右移居中」。
    /// </summary>
    private const long SpawnSettleGuardMs = 800;

    /// <summary>编排 B：岛缩没（0.5s）+ 余量后，气泡才从岛中心放大出生。</summary>
    private const long IslandHideGuardMs = 600;

    private static long _pendingSpawnAtMs;   // 编排 B 的延迟出生计时（0 = 未在等待）
    // 岛体宽高弹簧固定 0.450s；这里留一帧余量让弹簧真正启动起来（否则第一帧读到的还是「没在动」）。
    private const long IslandShrinkGuardMs = 50;
    // 弹簧状态异常时不至于让气泡永远出不来（正常情况下 0.45s 就收完了）。
    private const long IslandShrinkTimeoutMs = 900;
    // 气泡退场动画 0.5s；超时兜底，别让岛体永远不展开。
    private const long BubbleExitTimeoutMs = 900;

    private static readonly object _gate = new();

    private static bool _enabled;        // 「使用实况窗展示媒体」开关（注册表持久化）
    private static bool _active;         // 气泡当前已注册

    /// <summary>媒体气泡恢复出生（挂起解除）时由 LiveWindowHost 调用：重置压岛计时，等新出生站稳。</summary>
    public static void NotifySpawnRestarted()
    {
        lock (_gate) _activatedAtMs = Environment.TickCount64;
    }

    private static bool _delayedSpawn;       // 编排 B（插件恢复触发）：先压岛缩没，气泡后从中心放大
    private static long _delayedSpawnAtMs;

    /// <summary>
    /// 「只有一个东西」的启用：恢复的插件是岛上唯一内容 —— 请求延迟出生编排：
    /// 岛先带内容缩小消失（0.5s），缩完气泡再从岛中心（=居中位）原地放大出生。
    /// </summary>
    public static void RequestDelayedSpawn()
    {
        lock (_gate)
        {
            _delayedSpawn = true;
            _pendingSpawnAtMs = 0;
            _delayedSpawnAtMs = Environment.TickCount64;
        }
    }
    private static long _activatedAtMs;  // 注册成功时刻：静默编排用它等气泡站稳再压岛
    private static IDisposable? _handle;
    private static long _mediaDetectedAt;   // 首次检测到媒体的时刻；0 = 还没检测到
    private static bool _islandShrinking;   // 已请求岛体收缩，正在等弹簧跑完
    private static long _islandShrinkAtMs;
    private static volatile bool _bubbleExiting;   // 气泡已注销，正在等「滑回岛体」退场动画
    private static long _bubbleExitAtMs;

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
            _islandShrinking = false;
            Deactivate();   // 有气泡时进入退场等待，岛体展开交给 Tick 排队
        }
    }

    /// <summary>渲染线程每帧一次：管理 3 秒预览 → 注册 → 抑制岛内媒体 的时序。</summary>
    public static void Tick(MediaController media)
    {
        bool enabled;
        lock (_gate) enabled = _enabled;

        // 待机时岛体只显示待机内容：媒体先留在岛内，退出待机后再重新走一遍 3 秒预览。
        // 静默模式放行 —— 用户既然在「媒体设置」里打开了实况窗，静默模式下气泡就该照常出现
        //（气泡算重要信息，岛会回来陪它，见 MediaLiveWindow.CountsAsImportantInfo）。
        // ⚠️ 静默模式下还要过「媒体是否作为重要信息」开关：关了就不展示（岛内和气泡都不出现）。
        bool wantLive = enabled && media.IsActive && !Renderer.StandbyModeOnly
                        && (!Renderer.SilentActive || SilentMode.MediaImportantEnabled);
        long now = Environment.TickCount64;

        // 气泡退场阶段：动画本身一点没改（还是原来那条「滑回岛体」），只是把它播完之前岛体先不展开。
        if (_bubbleExiting)
        {
            bool stillExiting = LiveWindowHost.HasExiting(Owner);
            if (stillExiting && now - _bubbleExitAtMs < BubbleExitTimeoutMs)
            {
                // 与普通模式顺序一致（用户定稿）：岛体维持收短态，气泡吸回，播完岛再展开。
                Renderer.SuppressMediaInIsland = true;
                return;
            }
            _bubbleExiting = false;
            Renderer.SuppressMediaInIsland = false;      // 气泡退场播完 → 岛体再展开
        }

        if (!wantLive)
        {
            _islandShrinking = false;
            Deactivate();
            if (!_bubbleExiting) Renderer.SuppressMediaInIsland = false;
            lock (_gate) _mediaDetectedAt = 0;
            return;
        }

        if (IsActive)
        {
            Renderer.SuppressMediaInIsland = true;
            return;
        }

        long detected;
        lock (_gate)
        {
            if (_mediaDetectedAt == 0) _mediaDetectedAt = now;
            detected = _mediaDetectedAt;
        }

        if (now - detected < IslandPreviewMs)
        {
            _islandShrinking = false;
            Renderer.SuppressMediaInIsland = false;   // 这 3 秒里主岛照常显示媒体
            return;
        }

        // 用户要求「岛主动画优先」：先让主岛把媒体收回去、宽高弹簧收短，等这次形变彻底跑完，
        // 再让气泡从**收短之后**的岛体左缘分裂出来 —— 两个动画不再叠在一起，气泡也不会
        // 骑着还在移动的岛体边跑。气泡自己的生成 / 分裂动画参数一个都没动。
        //
        // ⚠️ 静默模式走另一条编排（用户定稿，四步串行）：
        //    ① 主岛展示 3s 媒体（IslandPreviewMs，媒体在岛内）
        //    ② 岛**不收短**、保持媒体折叠态当参照，气泡从媒体态岛缘分裂出生（Spawn 0.46s）
        //    ③ 气泡站稳（SpawnSettleGuardMs = 0.8s）后压岛（SuppressMediaInIsland=true
        //       → IsIslandMedia=false → 岛缩没 0.3s）
        //    ④ 岛不可见 → LiveWindowHost 的 islandVisible 跳变沿 → 气泡组一步到居中位。
        //    走普通收短的话，静默下会先闪一个空白折叠胶囊，再从它上面分裂气泡。
        if (Renderer.SilentActive && !Renderer.StandbyModeOnly)
        {
            // 静默编排（用户定稿，全部 0.5s 统一节奏）：
            // A. 岛还有其他内容：气泡立即从岛缘出生（岛保持展示），站稳 0.8s 后压岛 → 岛变形/保持。
            // B. 岛没有其他内容：先压岛（岛带内容缩小消失 0.5s），缩完气泡再从岛中心（=居中位）放大出生。
            //    B 有两个触发源：媒体启用且岛无其他内容（下方兜底）/ 插件恢复且它是唯一内容
            //    （RequestDelayedSpawn，由 LiveWindowHost 恢复时判定）。
            if (_delayedSpawn)
            {
                Renderer.SuppressMediaInIsland = true;   // 岛带内容缩小中（岛内画由「缩没中且未接管」分支维持）
                if (now - _delayedSpawnAtMs >= IslandHideGuardMs)
                {
                    _delayedSpawn = false;
                    Activate(media);                     // 岛已缩没：气泡在居中位（=岛中心）原地放大出生
                }
                return;
            }

            if (SilentMode.HasIslandContent)
            {
                Activate(media);   // 幂等：已激活直接返回；注册被拒绝时 _active=false，岛继续显示媒体
                bool spawnSettled = _activatedAtMs != 0 && now - _activatedAtMs >= SpawnSettleGuardMs;
                Renderer.SuppressMediaInIsland = IsActive && spawnSettled;
                return;
            }

            // B 兜底：媒体启用且岛无其他内容。气泡已接管则维持压制即可。
            if (IsActive)
            {
                Renderer.SuppressMediaInIsland = true;
                return;
            }
            Renderer.SuppressMediaInIsland = true;
            if (_pendingSpawnAtMs == 0) _pendingSpawnAtMs = now;
            if (now - _pendingSpawnAtMs >= IslandHideGuardMs)
            {
                _pendingSpawnAtMs = 0;
                Activate(media);   // 岛已缩没：气泡在居中位（=岛中心）原地放大出生
            }
            return;
        }

        if (!_islandShrinking)
        {
            _islandShrinking = true;
            _islandShrinkAtMs = now;
        }
        Renderer.SuppressMediaInIsland = true;   // 从这一刻起主岛按「没有媒体」处理，开始收短

        long shrinkElapsed = now - _islandShrinkAtMs;
        bool islandSettled = shrinkElapsed >= IslandShrinkGuardMs
                             && (!NotchWindow.IslandResizeAnimating || shrinkElapsed >= IslandShrinkTimeoutMs);
        if (!islandSettled) return;

        _islandShrinking = false;
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
            _bubbleExiting = false;
        }

        // 用户可能在这 3 秒里点开过主岛的媒体面板：媒体已经挪到左边，主岛不该继续展开。
        Renderer.IsMediaExpanded = false;
        lock (_gate) _activatedAtMs = Environment.TickCount64;
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

        // 注销只是让气泡开始播它原来那条退场动画；岛体要等它播完再展开（见 Tick 的退场阶段）。
        // 先写时间戳再置 volatile 标志：渲染线程看到 true 时时间戳一定已经就位。
        _bubbleExitAtMs = Environment.TickCount64;
        _bubbleExiting = true;

        try { handle?.Dispose(); }
        catch (Exception ex) { Logger.Error("[实况窗] 注销媒体实况窗失败", ex); }
        Logger.Info("[实况窗] 媒体实况窗已收起");
    }
}
