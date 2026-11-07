using SkiaSharp;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 实况窗一帧的可视状态。渲染线程每帧调用一次 <see cref="ILiveWindow.GetVisual"/> 取它，
/// 所以实现方要自己保证线程安全（字段用 volatile / Interlocked，别在 getter 里做 I/O 或锁等待）。
/// </summary>
public readonly record struct LiveWindowVisual
{
    /// <summary>0..1 的确定进度；小于 0 表示不确定进度（进度弧改为持续旋转的扫掠段）。</summary>
    public float Progress { get; init; }

    /// <summary>内圈状态行（如 "downloading"）。</summary>
    public string? Status { get; init; }

    /// <summary>内圈标题（如 "Rayburst"）。</summary>
    public string? Title { get; init; }

    /// <summary>内圈百分比文字覆盖；null 表示由宿主按 <see cref="Progress"/> 生成 "40%"。</summary>
    public string? PercentText { get; init; }

    /// <summary>进度弧颜色；null 表示宿主默认绿。</summary>
    public SKColor? ProgressColor { get; init; }

    /// <summary>内圈内容是否由宿主持续旋转（手稿里的「可选：一直旋转（宿主控制）」）。</summary>
    public bool SpinInner { get; init; }

    /// <summary>
    /// 内圈旋转速度（圈/秒）。不传（0）= 宿主默认慢速 0.12 圈/秒（约 8 秒转一圈）；
    /// 想转快就自己传，宿主只负责按这个速度累加角度。
    /// </summary>
    public float SpinSpeed { get; init; }

    /// <summary>完成态：进度弧亮一下并保持满圈。</summary>
    public bool Completed { get; init; }
}

/// <summary>
/// 实况窗（Live Window）注册契约。
///
/// <para>
/// 它独立于右侧主岛：只在主岛左边显示，**不会**出现在岛内组件行（手稿：左边显示右边不重复显示该组件）。
/// 目前只允许插件与宿主内置注册，不提供 HTTP / MSP 注册入口。
/// </para>
/// </summary>
public interface ILiveWindow
{
    /// <summary>同一插件内唯一。重复注册同 Id 会替换旧窗（不闪烁）。</summary>
    string Id { get; }

    /// <summary>渲染线程每帧调用，取视觉快照。</summary>
    LiveWindowVisual GetVisual();

    /// <summary>
    /// 内圈全量自定义绘制。矩形是内圈内的正方形（逻辑像素，约 39×39），
    /// 宿主已裁剪到内圈圆内、并按 <see cref="LiveWindowVisual.SpinInner"/> 旋转过画布。
    /// 想用宿主那套三行文字（download 示例）直接调 <see cref="LiveWindowDefaults.DrawInnerText"/>。
    /// <b>只在渲染线程调用，必须快速返回；不能让异常逃出去（宿主会熔断该窗）。</b>
    /// </summary>
    void DrawInner(SKCanvas canvas, SKRect rect, WidgetFrame frame);

    /// <summary>
    /// 「原有的展开态」：**一般不用实现（返回 null 即可）** —— 宿主会自动复用该插件组件原有的
    /// <see cref="IDetailPage"/>；只有内置消费者 / 想给实况窗单独做页面的特殊场景才返回自定义页。
    /// 宿主把它从气泡位置展开成浮动面板，主岛不参与变形。null 且找不到可复用页时改走 <see cref="OnExpand"/>。
    /// <para>左右键都由宿主直接展开，插件不需要自己管「展开按钮」。</para>
    /// </summary>
    IDetailPage? DetailPage => null;

    /// <summary>
    /// 左键点击气泡。**保留做二进制兼容：宿主已统一接管左右键展开，不再调用本回调**。
    /// 想给实况窗做点击动作请用 <see cref="DetailPage"/>（打开面板）或 <see cref="OnExpand"/>（打开自己的界面）。
    /// </summary>
    void OnLeftClick() { }

    /// <summary>没有可复用 <see cref="DetailPage"/> 时，左右键点击气泡的回调（比如打开自己的设置页 / 市场页）。</summary>
    void OnExpand() { }

    /// <summary>宿主注销这个窗时回调一次（插件卸载 / 主动 Dispose）。之后宿主不再回调本对象的任何方法。</summary>
    void OnRemoved() { }

    /// <summary>
    /// 「设置 → 实况窗管理」里的插件级开关变化：<c>true</c> = 重新启用，<c>false</c> = 已被禁用。
    /// <para>
    /// 禁用时宿主立刻挂起该插件的全部气泡：不再绘制、不再命中，也不再调用 <see cref="GetVisual"/> /
    /// <see cref="DrawInner"/>；插件应把内容按既有方式放回主岛组件行（重新启用时气泡恢复，内容可以再交给实况窗）。
    /// 窗口对象仍然有效，只是被暂时挂起 —— 不要在这个回调里注销或重新注册。
    /// 由 UI 线程调用，回调里别做耗时操作。
    /// </para>
    /// </summary>
    void OnHostEnabledChanged(bool enabled) { }
}

/// <summary>
/// 宿主提供的实况窗默认绘制。目前只有手稿 `downloading / Rayburst / 40%` 那套三行文字，
/// 内置消费者与「不想自己排文字」的插件都可以直接复用。
/// </summary>
public static class LiveWindowDefaults
{
    // 只在渲染线程被调用；static 复用避免每帧分配画笔。
    private static readonly SKPaint StatusPaint = new()
    {
        IsAntialias = true,
        Typeface = FontConfig.Normal,
        TextAlign = SKTextAlign.Center,
        TextSize = 6.5f
    };

    private static readonly SKPaint TitlePaint = new()
    {
        IsAntialias = true,
        Typeface = FontConfig.Bold,
        TextAlign = SKTextAlign.Center,
        TextSize = 9.5f
    };

    private static readonly SKPaint PercentPaint = new()
    {
        IsAntialias = true,
        Typeface = FontConfig.SemiBold,
        TextAlign = SKTextAlign.Center,
        TextSize = 8f
    };

    private static readonly char[] PercentBuffer = new char[8];

    public static void DrawInnerText(SKCanvas canvas, SKRect rect, WidgetFrame frame, LiveWindowVisual visual)
    {
        if (rect.Width <= 2f || rect.Height <= 2f) return;

        string? status = visual.Status;
        string? title = visual.Title;
        string? percent = visual.PercentText;

        bool indeterminate = visual.Progress < 0f;
        if (percent == null && !indeterminate)
        {
            double pct = Math.Clamp(visual.Progress, 0f, 1f) * 100.0;
            int whole = (int)Math.Round(pct, MidpointRounding.AwayFromZero);
            if (whole < 0) whole = 0;
            else if (whole > 100) whole = 100;
            percent = FormatPercent(whole);
        }

        float cx = rect.MidX;

        // 气泡直径跟随折叠岛高度（约 40px）时，内圈只剩 24px 见方：三行文字会糊成一团，
        // 这里退化成「标题 + 百分比」两行，字号按矩形高度自适应。
        if (rect.Height < 32f)
        {
            TitlePaint.Typeface = FontConfig.Bold;
            TitlePaint.Color = frame.Theme.TextColor;
            TitlePaint.TextSize = MathF.Max(4.5f, rect.Height * 0.30f);
            if (!string.IsNullOrEmpty(title))
            {
                float maxW = rect.Width * 0.96f;
                float w = TitlePaint.MeasureText(title);
                if (w > maxW && w > 0f)
                    TitlePaint.TextSize = MathF.Max(4f, TitlePaint.TextSize * (maxW / w));
                canvas.DrawText(title, cx, rect.Top + rect.Height * 0.44f, TitlePaint);
            }

            PercentPaint.Typeface = FontConfig.SemiBold;
            PercentPaint.Color = frame.Theme.TextColor;
            PercentPaint.TextSize = MathF.Max(4f, rect.Height * 0.22f);
            if (!string.IsNullOrEmpty(percent))
                canvas.DrawText(percent, cx, rect.Top + rect.Height * 0.82f, PercentPaint);
            return;
        }

        StatusPaint.Typeface = FontConfig.Normal;
        StatusPaint.Color = frame.Theme.SubTextColor;
        StatusPaint.TextSize = 6.5f;
        if (!string.IsNullOrEmpty(status))
            canvas.DrawText(status, cx, rect.Top + rect.Height * 0.333f, StatusPaint);

        TitlePaint.Typeface = FontConfig.Bold;
        TitlePaint.Color = frame.Theme.TextColor;
        TitlePaint.TextSize = 9.5f;
        if (!string.IsNullOrEmpty(title))
        {
            // 标题允许比内圈略宽，但不能压到圆环上：按矩形宽度等比缩字号（下限 6）。
            float maxW = rect.Width * 0.98f;
            float w = TitlePaint.MeasureText(title);
            if (w > maxW && w > 0f)
                TitlePaint.TextSize = Math.Max(6f, TitlePaint.TextSize * (maxW / w));
            canvas.DrawText(title, cx, rect.Top + rect.Height * 0.628f, TitlePaint);
        }

        PercentPaint.Typeface = FontConfig.SemiBold;
        PercentPaint.Color = frame.Theme.TextColor;
        PercentPaint.TextSize = 8f;
        if (!string.IsNullOrEmpty(percent))
            canvas.DrawText(percent, cx, rect.Top + rect.Height * 0.897f, PercentPaint);
    }

    private static string FormatPercent(int whole)
    {
        int n = 0;
        if (whole >= 100) { PercentBuffer[n++] = '1'; PercentBuffer[n++] = '0'; PercentBuffer[n++] = '0'; }
        else if (whole >= 10) { PercentBuffer[n++] = (char)('0' + whole / 10); PercentBuffer[n++] = (char)('0' + whole % 10); }
        else { PercentBuffer[n++] = (char)('0' + whole); }
        PercentBuffer[n++] = '%';
        return new string(PercentBuffer, 0, n);
    }
}
