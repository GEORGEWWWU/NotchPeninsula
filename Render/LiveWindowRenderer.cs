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
    private const float SpawnSeconds = 0.5f;      // 与宿主的生成时长同口径，用来把 SpawnProgress 换算成秒
    private const float BridgeSeconds = 0.32f;
    private const float IndeterminatePeriodMs = 1100f;
    private const float PanelRadius = 16f;

    private const float BubbleLightAlpha = 246f / 255f;
    private const float BubbleDarkAlpha = 240f / 255f;
    private const float PanelBaseAlpha = 248f / 255f;

    // 深色气泡 / 浅色面板的固定底色；浅色气泡就是纯白。
    private static readonly SKColor DarkBubbleColor = new(0x1C, 0x1C, 0x1E);
    private static readonly SKColor LightPanelColor = new(0xF2, 0xF2, 0xF7);
    private static readonly SKColor DefaultProgressColor = new(0x32, 0xD7, 0x4B);

    // 画笔 / 路径全部 static 复用：60FPS 下每帧 new SKPaint/SKPath 就是持续 GC。
    private static readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private static readonly SKPaint _arcPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly SKPaint _bridgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly SKPaint _alphaLayerPaint = new();
    private static readonly SKPath _clipPath = new();
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

        // 明暗跟「文字色」而非 ThemeMode：液态玻璃下文字黑白由桌面亮度逐帧决定，
        // 只按 ThemeMode 会出现「玻璃上是黑字、旁边却立着深色气泡」的割裂。
        bool lightBubble = !IsLightColor(theme.TextColor);

        // 分裂桥先画：后画的气泡要能盖住桥，否则接缝处会出现一条硬边。
        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            if (item.Exiting || item.SpawnProgress >= 1f) continue;
            DrawBridge(canvas, in item, in theme, global);
        }

        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            if (item.Alpha < 4) continue;

            int depth = canvas.SaveCount;
            try { DrawBubble(canvas, in item, in theme, lightBubble, global); }
            catch (Exception ex) { Logger.Error("[实况窗] 气泡绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }

        // 「消失 = 两个圆合并」：气泡滑向岛体左缘，与宿主侧的同色圆合并成一个，再一起缩进岛体。
        // 画在气泡之后：融合时宿主圆会盖住气泡的环和封面，看起来就是被吞进去。
        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            if (!item.Exiting || item.ExitProgress >= 1f) continue;

            int depth = canvas.SaveCount;
            try { DrawHostMouth(canvas, in item, in theme, lightBubble, global); }
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

    /// <summary>细胞分裂的桥：只在生成头 0.32s 存在，α 从 45 线性衰到 0。</summary>
    private static void DrawBridge(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme, float global)
    {
        float t = item.SpawnProgress * (SpawnSeconds / BridgeSeconds);
        if (t >= 1f) return;

        // 不乘 item.Alpha：气泡自己的淡入在头 0.12s 才 0→1，乘进来桥等于看不见。
        float alpha = (45f / 255f) * (1f - t) * global;
        if (alpha <= 0.002f) return;

        _bridgePaint.StrokeWidth = item.Rect.Width * 0.55f;
        _bridgePaint.Color = WithOpacity(theme.TextColor, alpha);
        canvas.DrawLine(item.SpawnFromX, item.Rect.MidY, item.Rect.MidX, item.Rect.MidY, _bridgePaint);
    }

    /// <summary>
    /// 「消失 = 宿主张嘴把气泡吞进去」：根扎在岛体左缘的上下两点，两条对称曲线向左伸出，
    /// 围住气泡后合拢（手稿里那种「一个圆 + 两条膜曲线」的吞噬动画），最后整只口缩回岛体。
    /// 画在气泡之后，膜内填岛体底色，合拢时正好盖住气泡 = 被吞掉。
    /// </summary>
    private static void DrawHostMouth(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme, bool lightBubble, float global)
    {
        float t = Math.Clamp(item.ExitProgress, 0f, 1f);
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
        // 之前写死的 -4.5 / -8.5 会把这颗小气泡的环和内容挤没；进度条半径也顺势收到 0.80r。
        float outerR = r - 0.5f;
        float bandW = Math.Clamp(r * 0.15f, 2.5f, 4.5f);
        float trackR = r * 0.80f;
        float innerR = r * 0.69f;
        float contentClipR = r * 0.66f;
        float ringW = Math.Clamp(r * 0.037f, 1f, 1.3f);

        canvas.Save();
        // 围绕气泡中心缩放：生成弹入 / 退出缩小都走这一条，位置仍由宿主给的 Rect 决定。
        if (MathF.Abs(scale - 1f) > 0.0005f)
        {
            canvas.Translate(cx, cy);
            canvas.Scale(scale);
            canvas.Translate(-cx, -cy);
        }

        SKColor baseColor = lightBubble ? SKColors.White : DarkBubbleColor;
        _fillPaint.Color = WithOpacity(baseColor, alpha * (lightBubble ? BubbleLightAlpha : BubbleDarkAlpha));
        canvas.DrawCircle(cx, cy, r, _fillPaint);

        // 外圈：hover 提亮到 1.0，平时 0.85。
        _strokePaint.StrokeWidth = ringW;
        _strokePaint.Color = WithOpacity(theme.TextColor, alpha * (item.Hovered ? 1f : 0.85f));
        canvas.DrawCircle(cx, cy, outerR, _strokePaint);

        _strokePaint.StrokeWidth = bandW;
        _strokePaint.Color = WithOpacity(theme.TextColor, alpha * 0.14f);
        canvas.DrawCircle(cx, cy, trackR, _strokePaint);

        DrawProgressArc(canvas, in item, cx, cy, trackR, bandW, alpha);

        _strokePaint.StrokeWidth = ringW;
        _strokePaint.Color = WithOpacity(theme.TextColor, alpha * 0.85f);
        canvas.DrawCircle(cx, cy, innerR, _strokePaint);

        // 内圈内容矩形 = 0.62r 见方；先裁剪、再旋转，注册方怎么画都不会溢出圆环。
        float innerHalf = r * 0.62f;
        var innerRect = new SKRect(cx - innerHalf, cy - innerHalf, cx + innerHalf, cy + innerHalf);

        canvas.Save();
        _clipPath.Rewind();
        _clipPath.AddCircle(cx, cy, contentClipR);
        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);

        // 宿主只在 SpinInner 时累加角度，所以「角度非 0」本身就等价于「该转」。
        if (item.SpinAngle != 0f)
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

        if (item.Indeterminate)
        {
            // 没有每项时间戳：用系统时钟取模，所有不确定态气泡同相旋转，反而更像一个整体。
            float start = (Environment.TickCount64 % (long)IndeterminatePeriodMs) * (360f / IndeterminatePeriodMs);
            canvas.DrawArc(oval, start, 80f, false, _arcPaint);
            return;
        }

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

        _fillPaint.Color = WithOpacity(lightBubble ? LightPanelColor : DarkBubbleColor, alpha * PanelBaseAlpha);
        canvas.DrawPath(_clipPath, _fillPaint);

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

                // 内容按「目标矩形」排版，靠裁剪窗从气泡位置一路长出来 —— 不跟着 reveal 矩形变形。
                page.Draw(canvas, frame.PanelRect, new WidgetFrame(theme, pageAlpha, 0f, null, false));
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
