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
    private static readonly SKPaint _alphaLayerPaint = new();
    /// <summary>气泡的隐形命中底（alpha≈2，肉眼看不见但吃鼠标），见 DrawBubble 里的说明。</summary>
    private static readonly SKPaint _hitFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPath _clipPath = new();
    // 气泡进度环带（外圆 - 内圆，EvenOdd）：材质只铺在这一圈，环内内容区保持透明。
    private static readonly SKPath _ringPath = new();
    // 液体颈：_neckPath = 闭合区域（填色 + 气泡差集裁切），_lipPath = 两条水腰线（描边）。
    private static readonly SKPath _neckPath = new();
    private static readonly SKPath _lipPath = new();
    private static readonly SKPath _maskPath = new();

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

        // 液体颈先画：它是「连在一起的那部分液体」，气泡是浮在它上面的那半颗。
        // 退出 = 被岛体吸进去，出生 = 从岛体挤出来，两个方向共用同一条几何，只是 t 走向相反。
        for (int i = 0; i < frame.Count; i++)
        {
            var item = frame.Items[i];
            bool absorbing = item.Exiting;
            float t = absorbing ? item.ExitProgress : item.SpawnProgress;
            if (t >= 1f || t <= 0.0005f) continue;

            int depth = canvas.SaveCount;
            try { DrawLiquidBridge(canvas, in item, in theme, global, t, absorbing); }
            catch (Exception ex) { Logger.Error("[实况窗] 液体转移绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }

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

        if (frame.PanelVisible && frame.PanelAlpha > 0)
        {
            int depth = canvas.SaveCount;
            try { DrawPanel(canvas, frame, in theme, lightBubble, global); }
            catch (Exception ex) { Logger.Error("[实况窗] 展开面板绘制异常", ex); }
            finally { RestoreTo(canvas, depth); }
        }
    }

    /// <summary>
    /// 液体颈：液滴和岛体之间那段「水桥」。
    ///
    /// <para>几何就是两圆之间的经典水桥：在液滴朝岛体那一侧、以及岛体左缘各取一对对称切点，
    /// 中间用两条向内收的贝塞尔连起来，围出一只沙漏。颈的粗细只由 <paramref name="t"/> 推：
    /// 吸附时先长后掐断，出芽时从连着到拉断 —— 掐断那一下才是吸附感，所以它必须是一条
    /// 非线性曲线（钟形），不能跟着位置线性缩放。</para>
    ///
    /// <para>填色跟随岛体材质的色调，描边只画水桥的两条腰线。</para>
    /// </summary>
    private static void DrawLiquidBridge(SKCanvas canvas, in LiveWindowDrawItem item, in RenderTheme theme,
        float global, float t, bool absorbing)
    {
        float alpha = (item.Alpha / 255f) * global;
        if (alpha <= 0.01f) return;

        float rBase = item.Rect.Width * 0.5f;
        if (rBase < 3f) return;
        float r = rBase * TransferRadiusFactor(t, absorbing);

        float girth = BuildLiquidNeck(in item, t, absorbing, r, _neckPath, _lipPath);
        if (girth <= 0f) return;

        _fillPaint.Color = WithOpacity(theme.BackgroundColor, alpha);
        canvas.DrawPath(_neckPath, _fillPaint);

        int depth = canvas.SaveCount;
        try
        {
            // 腰线只描在「水膜外侧」：液滴圆内部不画，否则那两条线会穿过气泡的透明内容区，
            // 看着像两根浮在气泡里的黑钩子（低半径、大接触角时最明显）。
            _maskPath.Rewind();
            _maskPath.AddCircle(item.Rect.MidX, item.Rect.MidY, r);
            canvas.ClipPath(_maskPath, SKClipOperation.Difference, true);

            // 两条腰线：亮边给水膜高光，暗边给内缘阴影，配方与岛体玻璃的描边一致，接缝才不突兀。
            _strokePaint.StrokeCap = SKStrokeCap.Round;
            _strokePaint.StrokeWidth = 1f + 1.6f * girth;
            canvas.Translate(0f, 0.6f);
            _strokePaint.Color = new SKColor(239, 247, 255, AlphaByte(alpha * (0.10f + 0.26f * girth)));
            canvas.DrawPath(_lipPath, _strokePaint);
            canvas.Translate(0f, -1.2f);
            _strokePaint.Color = new SKColor(9, 15, 27, AlphaByte(alpha * (0.06f + 0.14f * girth)));
            canvas.DrawPath(_lipPath, _strokePaint);
        }
        finally
        {
            RestoreTo(canvas, depth);
            _strokePaint.StrokeCap = SKStrokeCap.Butt;
        }
    }

    /// <summary>
    /// 生成液体颈的几何（画布坐标，未经缩放）。返回颈部腰身的粗细 0..1（0 = 这一段没有颈）。
    /// 同时填 <paramref name="body"/>（闭合区域 = 液滴被吃掉的那块 + 颈）与 <paramref name="lip"/>（两条腰线）。
    /// </summary>
    private static float BuildLiquidNeck(in LiveWindowDrawItem item, float t, bool absorbing,
        float r, SKPath body, SKPath lip)
    {
        body.Rewind();
        lip.Rewind();

        float R = item.Rect.Width * 0.5f;
        if (R < 3f || r < 2f) return 0f;

        float cx = item.Rect.MidX;
        float cy = item.Rect.MidY;

        // edgeX = 岛体左边线。下面只用一颗「虚拟圆」来量液滴和岛体的间距（决定颈部粗细），
        // 真正的切点接在岛体左端圆帽的表面上（见 capCx）。
        float edgeX = item.MergeTargetX;
        float anchorX = edgeX + R * 0.75f;
        float anchorR = R * 0.75f;

        float d = anchorX - cx;                      // 液滴永远在岛体左侧
        if (d <= 0.25f) return 0f;

        // 两件事分开算，才不会一上来就变成一只大漏斗：
        //   phi  = 接触角（液体啃掉了液滴多少），前期很小、后段猛地扫过去；
        //   girth = 颈部腰身（那根「水丝」有多粗），中途最粗、掐断时归零。
        float eat = absorbing
            ? SmoothStep01((t - 0.10f) / 0.85f)
            : 1f - SmoothStep01((t - 0.02f) / 0.55f);
        float phi = 0.22f + 1.05f * eat;

        float girth = absorbing
            ? MathF.Sin(MathF.PI * Math.Clamp(t / 0.95f, 0f, 1f))
            : 1f - SmoothStep01(t / 0.60f);

        // 距离越远脖子越细：两颗水滴离得远时只能是「一丝」，不能是一条粗管子。
        float gap = d - r - anchorR;                 // 负 = 两圆已经重叠
        girth *= Math.Clamp(1f - MathF.Max(0f, gap) / (r * 1.9f), 0.22f, 1f);
        girth = Math.Clamp(girth, 0f, 1f);
        if (girth <= 0.05f) return 0f;

        float sinPhi = MathF.Sin(phi), cosPhi = MathF.Cos(phi);

        float pdUpY = cy - r * sinPhi, pdDnY = cy + r * sinPhi;
        float pdX = cx + r * cosPhi;

        // 把水桥接在圆帽表面，避免描边进入岛体。
        float capCx = edgeX + R * 0.96f;             // 圆帽圆心（edgeX 就是岛体左边线）
        float piX = capCx - R * cosPhi;
        float piUpY = cy - R * sinPhi, piDnY = cy + R * sinPhi;

        // 水桥的腰部比两端的连接处窄。
        float neckHalf = MathF.Min(R * 0.30f * girth, R * sinPhi * 0.92f);
        float midUpY = cy - neckHalf, midDnY = cy + neckHalf;
        float cAx = pdX + (piX - pdX) * 0.42f;
        float cBx = piX - (piX - pdX) * 0.42f;

        body.MoveTo(piX, piUpY);
        body.CubicTo(cBx, midUpY, cAx, midUpY, pdX, pdUpY);
        // 沿液滴朝岛体那一侧绕过去：上切点 → 最右点 → 下切点。这一段就是「被吃掉」的边界。
        float deg = phi * 180f / MathF.PI;
        body.ArcTo(new SKRect(cx - r, cy - r, cx + r, cy + r), -deg, deg * 2f, false);
        body.CubicTo(cAx, midDnY, cBx, midDnY, piX, piDnY);
        body.Close();

        lip.MoveTo(piX, piUpY);
        lip.CubicTo(cBx, midUpY, cAx, midUpY, pdX, pdUpY);
        lip.MoveTo(piX, piDnY);
        lip.CubicTo(cBx, midDnY, cAx, midDnY, pdX, pdDnY);
        return girth;
    }

    /// <summary>转移期间的半径系数：吸附时被抽走，出芽时由小长到正常（末端轻微过冲）。</summary>
    private static float TransferRadiusFactor(float t, bool absorbing)
    {
        if (absorbing)
            return 1f - 0.42f * SmoothStep01((t - 0.30f) / 0.70f);

        float u = Math.Clamp(t, 0f, 1f) - 1f;
        float back = 1f + 1.9f * u * u * u + 0.9f * u * u;   // easeOutBack(overshoot 0.9)
        return 0.52f + 0.48f * back;
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

        // 液体转移（出生 / 吞入）期间，半径、压扁、以及「被吃掉」的裁切都从同一个 t 推出来；
        // 位置曲线在宿主那边用同一段时间轴 —— 三样必须同步，否则又会出现「圆在抖但脖子没动」。
        bool absorbing = item.Exiting;
        float transfer = absorbing
            ? Math.Clamp(item.ExitProgress, 0f, 1f)
            : (item.SpawnProgress < 1f ? Math.Clamp(item.SpawnProgress, 0f, 1f) : 1f);
        bool transferring = transfer < 1f;
        float r = rBase * (transferring ? TransferRadiusFactor(transfer, absorbing) : 1f);

        float cx = rect.MidX;
        float cy = rect.MidY;

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
        // 被液体吃掉的那一块：用颈的闭合区域做差集裁切（颈本身由 DrawLiquidBridge 画在气泡下层）。
        // 于是圆环是被「吃掉」的，而不是整体淡出的 —— 这是吸附感最关键的一步。
        if (transferring && r > 1.5f)
        {
            if (BuildLiquidNeck(in item, transfer, absorbing, r, _neckPath, _lipPath) > 0f)
                canvas.ClipPath(_neckPath, SKClipOperation.Difference, true);

            // 岛体那一侧一律切掉：岛体画在气泡之前，重叠出来的像素就是一颗贴在边上的鼓包。
            // 颈掐断之后（girth = 0）也仍然靠这条兜住，不会突然「弹回整圆」再淡出。
            _maskPath.Rewind();
            _maskPath.AddRect(new SKRect(item.MergeTargetX, cy - rBase * 3f,
                item.MergeTargetX + rBase * 6f, cy + rBase * 3f));
            canvas.ClipPath(_maskPath, SKClipOperation.Difference, true);
        }

        // 液体形变：沿运动方向（x）拉伸、另一轴收缩，才像水而不是气球。
        float squash = transferring ? MathF.Sin(MathF.PI * transfer) * (absorbing ? 0.85f : 0.55f) : 0f;
        float sx = scale * (1f + 0.20f * squash);
        float sy = scale * (1f - 0.16f * squash);
        if (MathF.Abs(sx - 1f) > 0.0005f || MathF.Abs(sy - 1f) > 0.0005f)
        {
            canvas.Translate(cx, cy);
            canvas.Scale(sx, sy);
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
                    float scaledRadius = r * scale;
                    var sampleRect = new SKRect(cx - scaledRadius, cy - scaledRadius, cx + scaledRadius, cy + scaledRadius);
                    var drawRect = new SKRect(cx - r, cy - r, cx + r, cy + r);
                    bool drawn = DrawGlassBackdrop(canvas, sampleRect, drawRect, alpha);
                    GlassMaterial.DrawCoating(canvas, _ringPath, lightBubble, alpha, drawn);
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

        // 旧的「一条粗线从气泡连到面板」已经删掉：那根 110/255 的圆头直线是实况窗里最像卡通的一笔。
        // 面板本来就是从气泡的圆角直接长出来的（下面的 radius 插值），不需要再补一根管子。

        // 圆角从气泡的半径一路收到面板的 20：展开动画看起来是「气泡被拉成面板」。
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
                Renderer.BeginPanelToneGrid(hasBackdrop ? Renderer.AcrylicBackdrop : null, toneDst, toneSrc);

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
            int depth = canvas.SaveCount;
            canvas.Save();
            try
            {
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                bool drawn = DrawGlassBackdrop(canvas, rect, rect, alpha);
                GlassMaterial.DrawCoating(canvas, _clipPath, lightBubble, alpha, drawn);
            }
            finally { RestoreTo(canvas, depth); }
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

    private static bool DrawGlassBackdrop(SKCanvas canvas, SKRect sampleRect, SKRect drawRect, float alpha)
    {
        if (Renderer.AcrylicBackdrop is not { } backdrop
            || !Renderer.TryCanvasToBackdrop(sampleRect, out SKRect src)) return false;
        _panelImagePaint.Color = SKColors.White.WithAlpha(AlphaByte(alpha));
        canvas.DrawImage(backdrop, src, drawRect, _panelImagePaint);
        return true;
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
