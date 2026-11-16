using SkiaSharp;
using NotchPeninsula.Plugins;

namespace NotchPeninsula;

/// <summary>
/// 实况窗的最终绘制。由 <c>Renderer.Draw</c> 在画布回到基线后调用一次：
/// <see cref="LiveWindowHost.Frame"/> 里的矩形已经是画布逻辑坐标（含 topY），这里不要再平移。
/// 只读 Frame，不碰宿主私有状态；插件绘制抛异常只熔断它自己的那一项，绝不能把渲染线程带崩。
/// </summary>
public static class LiveWindowRenderer
{
    // 与主岛展开面板同一圆角（岛体展开态 = min(高/2, 20)），两边看起来才是同一块面板。
    private const float PanelRadius = 20f;

    private const float BubbleLightAlpha = 246f / 255f;
    private const float BubbleDarkAlpha = 240f / 255f;
    private const float PanelBaseAlpha = 248f / 255f;

    // 深色气泡 / 浅色面板的固定底色；浅色气泡就是纯白。
    private static readonly SKColor DarkBubbleColor = new(0x1C, 0x1C, 0x1E);
    private static readonly SKColor LightPanelColor = new(0xF2, 0xF2, 0xF7);
    private static readonly SKColor DefaultProgressColor = new(0x32, 0xD7, 0x4B);

    private static readonly SKPaint _panelImagePaint = new()
    { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

    // 画笔 / 路径全部 static 复用：60FPS 下每帧 new SKPaint/SKPath 就是持续 GC。
    private static readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private static readonly SKPaint _arcPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly SKPaint _bridgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly SKPaint _alphaLayerPaint = new();
    /// <summary>气泡的隐形命中底（alpha≈2，肉眼看不见但吃鼠标），见 DrawBubble 里的说明。</summary>
    private static readonly SKPaint _hitFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPath _clipPath = new();
    // 气泡进度环带（外圆 - 内圆，EvenOdd）：材质只铺在这一圈，环内内容区保持透明。
    private static readonly SKPath _ringPath = new();
    private static readonly SKPath _mouthPath = new();
    private static readonly SKPath _lipPath = new();

    public static void Draw(SKCanvas canvas)
    {
        var frame = LiveWindowHost.Frame;
        if (frame.Count <= 0 && !frame.PanelVisible) return;

        // 与主岛共用同一份全局透明度：穿透 / 隐藏淡出时实况窗必须一起走，不能剩一圈孤零零的气泡。
        float global = Renderer.PassthroughAlpha * Renderer.FullHideAlpha;
        if (global <= 0.002f) return;
        if (global > 1f) global = 1f;

        var theme = Renderer.GetCurrentTheme();

        // 与主岛的文字配色一致。
        bool lightBubble = !IsLightColor(theme.TextColor);

        // 气泡本体。
        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            if (item.Alpha < 4) continue;

            int depth = canvas.SaveCount;
            try { DrawBubble(canvas, in item, in theme, lightBubble, global); }
            catch (Exception ex) { Logger.Error("[实况窗] 气泡绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }

        // 「宿主吞噬」膜动画：这就是实况窗的出入场动画本体（用户定义）。
        // 退出正向播：岛主张嘴把气泡吞进去；生成把它反放：岛体把气泡吐出来。
        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            float t;
            if (item.Exiting)
            {
                if (item.ExitProgress >= 1f) continue;
                t = item.ExitProgress;
            }
            else
            {
                if (item.SpawnProgress >= 1f) continue;
                t = 1f - item.SpawnProgress;   // 反放
            }

            int depth = canvas.SaveCount;
            try { DrawHostMouth(canvas, in item, in theme, lightBubble, global, t); }
            catch (Exception ex) { Logger.Error("[实况窗] 宿主吞噬动画绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }

        if (frame.PanelVisible && frame.PanelAlpha > 0)
        {
            int depth = canvas.SaveCount;
            try { DrawPanel(canvas, frame, in theme, lightBubble, global); }
            catch (Exception ex) { Logger.Error("[实况窗] 展开面板绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }
    }

    /// <summary>
    /// 「宿主吞噬」膜动画（实况窗出入场动画定义）：根扎在岛体左缘的上下两点，两条对称曲线向左伸出，
    /// 围住气泡后合拢（手稿里那种「一个圆 + 两条膜曲线」），最后整只口缩回岛体。
    /// 画在气泡之后，膜内填岛体底色，合拢时正好盖住气泡。
    /// <para>
    /// <paramref name="t"/> = 出场进度 0→1（吞进去）；入场时调用方传 1 - 生成进度，
    /// 同一段动画反放（岛体把气泡吐出来）。不用另做淡入淡出。
    /// </para>
    /// </summary>
    private static void DrawHostMouth(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme,
        bool lightBubble, float global, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t <= 0.02f) return;

        float R = item.Rect.Width * 0.5f;
        float edgeX = item.MergeTargetX;
        float cy = item.Rect.MidY;
        float bubbleCx = item.Rect.MidX;

        float reach = SmoothStep01(Math.Clamp(t / 0.58f, 0f, 1f));     // 膜向左伸出的比例
        float close = SmoothStep01(Math.Clamp((t - 0.5f) / 0.34f, 0f, 1f)); // 合拢比例
        float retract = Math.Clamp((t - 0.82f) / 0.18f, 0f, 1f);       // 整口缩回岛体

        float tipX = edgeX - R * 0.15f + (bubbleCx - R * 0.95f - (edgeX - R * 0.15f)) * reach + retract * R * 1.5f;
        float tipY = R * (1.02f - 0.74f * close);   // 口径：先张大，再合拢
        float edgeY = R * 1.05f;
        float alpha = global * (1f - retract * 0.92f);
        if (alpha <= 0.01f || tipY < 0.5f) return;

        SKColor membrane = theme.BackgroundColor.Alpha > 8
            ? theme.BackgroundColor
            : (lightBubble ? SKColors.White : DarkBubbleColor);

        // 膜内（吞下去的部分）：岛体底色，盖住气泡。
        _mouthPath.Rewind();
        _mouthPath.MoveTo(edgeX, cy - edgeY);
        _mouthPath.CubicTo(edgeX - R * 1.35f, cy - R * 1.5f, tipX + R * 0.9f, cy - tipY, tipX, cy - tipY);
        _mouthPath.CubicTo(tipX - R * 0.62f, cy - tipY * 0.58f, tipX - R * 0.62f, cy + tipY * 0.58f, tipX, cy + tipY);
        _mouthPath.CubicTo(tipX + R * 0.9f, cy + tipY, edgeX - R * 1.35f, cy + R * 1.5f, edgeX, cy + edgeY);
        _mouthPath.Close();
        _fillPaint.Color = WithOpacity(membrane, alpha * 0.97f);
        canvas.DrawPath(_mouthPath, _fillPaint);

        // 两条膜曲线（手稿里的那两条线），只描边、不画收口线。
        _lipPath.Rewind();
        _lipPath.MoveTo(edgeX, cy - edgeY);
        _lipPath.CubicTo(edgeX - R * 1.35f, cy - R * 1.5f, tipX + R * 0.9f, cy - tipY, tipX, cy - tipY);
        _lipPath.CubicTo(tipX - R * 0.62f, cy - tipY * 0.58f, tipX - R * 0.62f, cy + tipY * 0.58f, tipX, cy + tipY);
        _lipPath.CubicTo(tipX + R * 0.9f, cy + tipY, edgeX - R * 1.35f, cy + R * 1.5f, edgeX, cy + edgeY);

        _strokePaint.StrokeWidth = MathF.Max(1.2f, R * 0.14f);
        _strokePaint.StrokeCap = SKStrokeCap.Round;
        _strokePaint.Color = WithOpacity(theme.TextColor, alpha * 0.72f);
        canvas.DrawPath(_lipPath, _strokePaint);
        _strokePaint.StrokeCap = SKStrokeCap.Butt;
    }

    private static void DrawBubble(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme, bool lightBubble, float global)
    {
        var rect = item.Rect;
        float rBase = rect.Width * 0.5f;
        if (rBase < 2f) return;

        float alpha = (item.Alpha / 255f) * global;
        if (alpha <= 0.002f) return;

        float scale = item.Scale;
        if (item.Hovered) scale *= 1.03f;
        if (scale <= 0.01f) return;

        // 两圆分裂的核心数学：半径从 R 缩到 R/√2 再长回来（面积守恒的观感），
        // 圆心距由宿主的位置动画负责（新气泡从母圆中心滑出）。
        // 退出走「宿主张嘴吞入」的膜动画，不再缩半径。
        float pinching = !item.Exiting && item.SplitProgress < 1f ? Math.Clamp(item.SplitProgress, 0f, 1f) : 0f;
        float radiusFactor = 1f - 0.293f * MathF.Sin(MathF.PI * pinching);
        float r = rBase * radiusFactor;

        float cx = rect.MidX;
        float cy = rect.MidY;

        // 半径全部按气泡尺寸成比例：气泡直径现在跟宿主高度走（默认约 40），
        // 之前写死的 -4.5 / -8.5 会把这颗小气泡的环和内容挤没。
        float bandW = Math.Clamp(r * 0.15f, 2.5f, 4.5f);
        // 圆环只保留「进度条 + 内容区」，不再有整个圆盘的底色：
        //   进度条那一圈（contentClipR..r 的环带）就是气泡唯一的实体，材质直接铺在这圈上；
        //   环带以内是内容区，完全透明 —— 文字类内容直接浮在桌面上，不再垫一层背景。
        // 进度环外缘直接贴到气泡边（= 岛体高度，上下沿和岛体对齐），不再留 1.5px 的内缩。
        float trackR = r - bandW * 0.5f;
        float contentClipR = r - bandW;                                  // 内容圆直接贴住进度环内缘，不留空隙
        // 没有可用进度：进度条整个占位让给内容 —— 内容撑满整颗气泡，气泡总尺寸不变。
        bool noProgress = item.Indeterminate;
        if (noProgress) contentClipR = r;
        float innerHalf = contentClipR * 0.94f;                          // 与去掉边框前 0.62 / 0.66 的比例一致

        canvas.Save();
        // 围绕气泡中心缩放：生成弹入 / 退出缩小都走这一条，位置仍由宿主给的 Rect 决定。
        if (MathF.Abs(scale - 1f) > 0.0005f)
        {
            canvas.Translate(cx, cy);
            canvas.Scale(scale);
            canvas.Translate(-cx, -cy);
        }

        // ---- 「吃鼠标」的隐形底 ----
        // 气泡现在只有「进度环 + 内容」、环带以内完全透明（文字直接浮在桌面上）。
        // 分层窗口的命中判定是**逐像素 alpha**：alpha == 0 的像素对鼠标完全透明，
        // 于是点在圆里没有元素的位置（内圈空白、文字之间的缝）会直接透传到下面的窗口 ——
        // 用户就会觉得「明明点在这颗气泡上，却没反应 / 点到别的程序去了」。
        // 这里铺一层 alpha 极低的圆把整颗气泡圈进命中区：肉眼看不见（0.8%），
        // 但 alpha > 0 就吃鼠标，宿主随后用 HitBubble（整圆 + 2px 余量）统一分发。
        // 取 2 而不是 1：个别 DWM 路径 / 放大镜会把 alpha == 1 当成全透明，多留一格。
        // alpha < 0.5 时气泡正在淡出 / 吞入，别让一颗快看不见的气泡还拦鼠标。
        if (r >= 2f && alpha >= 0.5f)
        {
            _hitFillPaint.Color = SKColors.White.WithAlpha(2);
            canvas.DrawCircle(cx, cy, r, _hitFillPaint);
        }

        if (!noProgress)
        {
            // 材质只铺在「进度条那一圈」的环带上（外圆 r，内圆 contentClipR）：气泡不再有整块底。
            // 实体材质 = 原来的纯色环；亚克力 / 玻璃 = 真实背景材质直接接进这道进度环。
            _ringPath.Rewind();
            _ringPath.FillType = SKPathFillType.EvenOdd;
            _ringPath.AddCircle(cx, cy, r);
            _ringPath.AddCircle(cx, cy, contentClipR);
            int ringDepth = canvas.SaveCount;
            try
            {
                canvas.Save();
                canvas.ClipPath(_ringPath, SKClipOperation.Intersect, true);
                if (Renderer.IslandLiquidGlass)
                {
                    GlassMaterial.DrawCoating(canvas, _ringPath, lightBubble, alpha, GlassMaterial.LiveBackdropReady);
                    float scaledRadius = r * scale;
                    GlassMaterial.AddRegion(new SKRect(cx - scaledRadius, cy - scaledRadius,
                        cx + scaledRadius, cy + scaledRadius), scaledRadius, contentClipR * scale, alpha);
                }
                else if (!DrawBubbleMaterial(canvas, rect, cx, cy, r, alpha))
                {
                    SKColor baseColor = lightBubble ? SKColors.White : DarkBubbleColor;
                    _fillPaint.Color = WithOpacity(baseColor, alpha * (lightBubble ? BubbleLightAlpha : BubbleDarkAlpha));
                    canvas.DrawCircle(cx, cy, r, _fillPaint);
                }
            }
            finally
            {
                RestoreTo(canvas, ringDepth);
            }

            // 进度条：只画进度弧本身，不再有整圈淡轨道（那是进度条的「底」，用户要求去掉背景）。
            DrawProgressArc(canvas, in item, cx, cy, trackR, bandW, alpha);
        }

        // 内容矩形跟着裁剪圆走：先裁剪、再旋转，注册方怎么画都不会溢出。
        var innerRect = new SKRect(cx - innerHalf, cy - innerHalf, cx + innerHalf, cy + innerHalf);

        canvas.Save();
        _clipPath.Rewind();
        _clipPath.AddCircle(cx, cy, contentClipR);
        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);

        // 宿主只在「该转」时累加角度；没有进度时默认静止，除非注册方开了 SpinWhenNoProgress。
        if (item.SpinAngle != 0f && (!noProgress || item.SpinWhenNoProgress))
        {
            canvas.Translate(cx, cy);
            canvas.RotateRadians(item.SpinAngle);
            canvas.Translate(-cx, -cy);
        }

        byte contentAlpha = AlphaByte(alpha);
        if (item.Window != null)
            DrawWindowInner(canvas, in item, in theme, innerRect, contentAlpha);
        else
            DrawCachedInner(canvas, in item, in theme, innerRect, contentAlpha);

        canvas.Restore(); // 内圈裁剪 + 旋转
        canvas.Restore(); // 气泡缩放
    }

    private static void DrawProgressArc(SKCanvas canvas, in LiveWindowDrawItem item, float cx, float cy, float radius, float width, float alpha)
    {
        SKColor color = item.ProgressColor.Alpha != 0 ? item.ProgressColor : DefaultProgressColor;

        // 完成态只亮一档、不做 0.35s 脉冲定时器：宿主这一档本来只留约 0.8s 就退出，
        // 为它给每一项维护一个时钟不值得。
        if (item.Completed) color = BlendTowardWhite(color, 0.25f);

        _arcPaint.Color = WithOpacity(color, alpha);
        _arcPaint.StrokeWidth = width;
        var oval = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        // 没有可用进度时不再画「转圈扫描段」：那种状态下气泡根本不走这条路径（内容会撑满整颗气泡）。
        float sweep = 360f * Math.Clamp(item.Progress, 0f, 1f);
        if (sweep < 0.5f) return;
        if (sweep >= 359.5f) canvas.DrawCircle(cx, cy, radius, _arcPaint);
        else canvas.DrawArc(oval, -90f, sweep, false, _arcPaint);
    }

    private static void DrawWindowInner(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme, SKRect innerRect, byte alpha)
    {
        var frame = new WidgetFrame(theme, alpha, 0f, null, item.Hovered);
        int depth = canvas.SaveCount;
        try
        {
            // 插件不一定会遵守 frame.Alpha；用合成层兜一次整体透明度，淡入淡出时才不会露出半截实心内容。
            if (alpha < 250)
            {
                _alphaLayerPaint.Color = SKColors.White.WithAlpha(alpha);
                canvas.SaveLayer(_alphaLayerPaint);
            }
            item.Window!.DrawInner(canvas, innerRect, frame);
        }
        finally
        {
            RestoreTo(canvas, depth);
        }
    }

    private static void DrawCachedInner(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme, SKRect innerRect, byte alpha)
    {
        // 退出动画期间插件对象已断开（Window == null）：只画宿主的缓存文字，绝不回调可能已卸载的插件。
        var fadedTheme = new RenderTheme(
            theme.TextColor.WithAlpha(alpha),
            theme.SubTextColor.WithAlpha(alpha),
            theme.BackgroundColor,
            theme.GlobalDpi,
            theme.NotchBottomRadius);

        var visual = new LiveWindowVisual
        {
            Progress = item.Progress,
            Status = item.Status,
            Title = item.Title,
            PercentText = item.PercentText,
            ProgressColor = item.ProgressColor,
            Completed = item.Completed,
            SpinInner = false
        };

        LiveWindowDefaults.DrawInnerText(canvas, innerRect,
            new WidgetFrame(fadedTheme, alpha, 0f, null, item.Hovered), visual);
    }

    private static void DrawPanel(SKCanvas canvas, LiveWindowFrame frame, in RenderTheme theme, bool lightBubble, float global)
    {
        var page = frame.PanelPage;
        var revealRect = frame.PanelRevealRect;
        if (page == null || revealRect.Width < 1f || revealRect.Height < 1f) return;

        float alpha = (frame.PanelAlpha / 255f) * global;
        if (alpha <= 0.002f) return;

        float reveal = Math.Clamp(frame.PanelReveal, 0f, 1f);

        // 细胞分裂的「收口桥」：面板从气泡长出来时，先画一条逐渐变细的颈把母细胞和子细胞连起来，
        // 只在前 60% 出现；比单纯放大一个圆角矩形更接近分裂的观感。
        if (reveal < 0.6f)
        {
            var src = frame.PanelSourceRect;
            float neckAlpha = (1f - reveal / 0.6f);
            neckAlpha *= neckAlpha * (110f / 255f) * global;
            if (neckAlpha > 0.003f && src.Width > 0.5f)
            {
                _bridgePaint.StrokeWidth = MathF.Max(4f, src.Width * (0.55f - 0.25f * reveal));
                _bridgePaint.Color = WithOpacity(theme.TextColor, neckAlpha);
                canvas.DrawLine(src.MidX, src.MidY, revealRect.MidX, revealRect.MidY, _bridgePaint);
            }
        }

        // 圆角从气泡的半径一路收到面板的 16：展开动画看起来是「气泡被拉成面板」。
        float radius = frame.BubbleDiameter * 0.5f * (1f - reveal) + PanelRadius * reveal;

        _clipPath.Rewind();
        _clipPath.AddRoundRect(revealRect, radius, radius);

        DrawPanelMaterial(canvas, revealRect, radius, alpha, lightBubble);

        // 内容比外框晚一点淡入：外框先长出来，内容再浮出来，避免「一行字从小窗口里滑出来」的突兀。
        // 收起反过来：内容尽量留到最后 35% 再淡出，先收外框后收字，收回才不显得「啪一下没了」。
        float contentT = frame.PanelClosing
            ? Math.Clamp(reveal / 0.65f, 0f, 1f)
            : Math.Clamp((reveal - 0.18f) / 0.82f, 0f, 1f);
        byte pageAlpha = AlphaByte(alpha * (frame.PanelClosing ? contentT : contentT * contentT));

        int depth = canvas.SaveCount;
        try
        {
            if (pageAlpha >= 2)
            {
                canvas.Save();
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);

                if (pageAlpha < 250)
                {
                    _alphaLayerPaint.Color = SKColors.White.WithAlpha(pageAlpha);
                    canvas.SaveLayer(_alphaLayerPaint);
                }

                // 面板在岛外、背后是另一块桌面：文字黑白用面板自己那块底的亮度判（和主岛同款逐元素）。
                // ⚠️ 网格 dst 必须用**面板局部坐标** (0,0,w,h)：详情页绘制时会先 translate 到面板左上角，
                //    文字在局部坐标里排版；用画布绝对坐标取网格会全部落在网格外 → 永远回落到主题色（截图里那种白字压亮底）。
                var toneDst = new SKRect(0f, 0f, frame.PanelRect.Width, frame.PanelRect.Height);
                SKRect toneSrc = default;
                bool hasBackdrop = Renderer.AcrylicBackdrop != null
                    && Renderer.TryCanvasToBackdrop(frame.PanelRect, out toneSrc);
                Renderer.BeginPanelToneGrid(canvas, hasBackdrop ? Renderer.AcrylicBackdrop : null, toneDst, toneSrc);

                try
                {
                    // 内容按「目标矩形」排版，靠裁剪窗从气泡位置一路长出来 —— 不跟着 reveal 矩形变形。
                    page.Draw(canvas, frame.PanelRect, new WidgetFrame(theme, pageAlpha, 0f, null, false));
                }
                finally
                {
                    Renderer.EndPanelToneGrid();
                }
            }
        }
        finally
        {
            // 插件可能打乱 save/restore，按深度回卷到本面板开始前，后续气泡绘制才不会被带歪。
            RestoreTo(canvas, depth);
        }

        // 描边最后画：插件详情页可能自带整块底色，先画的边框会被它压掉。
        _strokePaint.StrokeWidth = 1f;
        _strokePaint.Color = WithOpacity(theme.TextColor, alpha * 0.18f);
        canvas.DrawPath(_clipPath, _strokePaint);
    }

    private static void DrawPanelMaterial(SKCanvas canvas, SKRect rect, float radius, float alpha, bool lightBubble)
    {
        if (Renderer.IslandLiquidGlass)
        {
            GlassMaterial.DrawCoating(canvas, _clipPath, lightBubble, alpha, GlassMaterial.LiveBackdropReady);
            GlassMaterial.AddRegion(rect, radius, 0f, alpha);
            return;
        }

        if (Renderer.IslandAcrylic && Renderer.AcrylicBackdrop is { } backdrop
            && Renderer.TryCanvasToBackdrop(rect, out SKRect src))
        {
            int depth = canvas.SaveCount;
            canvas.Save();
            try
            {
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                _panelImagePaint.Color = SKColors.White.WithAlpha(AlphaByte(alpha));
                canvas.DrawImage(backdrop, src, rect, _panelImagePaint);

                _fillPaint.Color = WithOpacity(Renderer.MaterialCoatColor, alpha);
                canvas.DrawRoundRect(rect, radius, radius, _fillPaint);
            }
            finally
            {
                RestoreTo(canvas, depth);
            }
            return;
        }

        _fillPaint.Color = WithOpacity(lightBubble ? LightPanelColor : DarkBubbleColor, alpha * PanelBaseAlpha);
        canvas.DrawRoundRect(rect, radius, radius, _fillPaint);
    }

    private static bool DrawBubbleMaterial(SKCanvas canvas, SKRect rect, float cx, float cy, float r, float alpha)
    {
        if (!Renderer.IslandAcrylic || Renderer.AcrylicBackdrop is not { } backdrop
            || !Renderer.TryCanvasToBackdrop(rect, out SKRect src)) return false;

        // 调用方已经用进度环的外圆和内圆裁切画布。
        _panelImagePaint.Color = SKColors.White.WithAlpha(AlphaByte(alpha));
        canvas.DrawImage(backdrop, src, rect, _panelImagePaint);
        _fillPaint.Color = WithOpacity(Renderer.MaterialCoatColor, alpha);
        canvas.DrawCircle(cx, cy, r, _fillPaint);
        return true;
    }

    private static SKColor WithOpacity(SKColor color, float opacity)
    {
        if (opacity <= 0f) return SKColors.Transparent;
        if (opacity >= 1f) return color;

        int a = (int)(color.Alpha * opacity + 0.5f);
        if (a <= 0) return SKColors.Transparent;
        if (a > 255) a = 255;
        return new SKColor(color.Red, color.Green, color.Blue, (byte)a);
    }

    private static byte AlphaByte(float opacity)
    {
        if (opacity <= 0f) return 0;
        if (opacity >= 1f) return 255;
        return (byte)(opacity * 255f + 0.5f);
    }

    private static bool IsLightColor(SKColor color)
        => color.Red + color.Green + color.Blue > 382;

    private static float SmoothStep01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private static SKColor BlendTowardWhite(SKColor color, float amount)
        => new(
            (byte)(color.Red + (255 - color.Red) * amount),
            (byte)(color.Green + (255 - color.Green) * amount),
            (byte)(color.Blue + (255 - color.Blue) * amount),
            color.Alpha);

    private static void RestoreTo(SKCanvas canvas, int depth)
    {
        // 用深度回卷而不是固定次数 Restore：插件多 Restore / 少 Restore 都不会把后续绘制带歪。
        int guard = 0;
        while (canvas.SaveCount > depth && guard++ < 32) canvas.Restore();
    }
}
