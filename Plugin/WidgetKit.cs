using SkiaSharp;

namespace NotchPeninsula.Plugins;

// ---- AutoWidth 协议的布局盒子 ----
//
// 插件只描述「我由哪几块组成」，宽度由宿主量、由宿主加留白、由宿主向上取整。
// 因此这里没有 PadX、没有余量、没有上限夹取 —— 那些口径由宿主独占，插件一旦自己再留一份，
// 就会量出来和画出来对不上（这也是老协议下每个插件都要 +2f / +5f 的根因）。

/// <summary>文字字重。与宿主自己的 <c>FontConfig.Normal / SemiBold / Bold</c> 一一对应。</summary>
public enum TextWeight
{
    Normal = 0,
    SemiBold = 1,
    Bold = 2,
}

/// <summary>布局盒子的基类。插件只使用 <see cref="WidgetKit"/> 提供的工厂方法构造，不要自己派生。</summary>
public abstract record Box;

/// <summary>
/// 一段文字。宽度按宿主当前字体实测 —— 与插件 <c>Draw</c> 里用的是同一个字体面，
/// 所以「量出来」和「画出来」必然一致（老协议下这条只能靠插件自觉）。
/// </summary>
public sealed record TextBox(string Text, float Size, TextWeight Weight) : Box;

/// <summary>固定宽度块：图标、进度点、分隔线之类。</summary>
public sealed record FixedBox(float Width) : Box;

/// <summary>纯间距。</summary>
public sealed record GapBox(float Width) : Box;

/// <summary>横向排列：宽度 = 各块之和 + 块间距。</summary>
public sealed record RowBox(Box[] Parts, float Gap) : Box;

/// <summary>纵向叠放（同一格里的正文与小字）：宽度取最宽的一行，不累加。</summary>
public sealed record StackBox(Box[] Rows) : Box;

/// <summary>给里面的盒子一个内容宽度下限（极短的内容也别缩成一条）。</summary>
public sealed record MinBox(float MinWidth, Box Inner) : Box;

/// <summary>
/// <b>AutoWidth 协议</b>：宽度完全交给宿主，插件不实现 <see cref="IWidget.MeasureWidth"/>。
///
/// <para>
/// 宿主拿到 <see cref="IAutoWidthWidget.Layout"/> 返回的盒子后自己测量，再把「内容宽 + 两侧留白」
/// 作为折叠态槽位宽度；插件在 <c>Draw</c> 里直接用 <c>rect</c> 排版即可
/// （要内容区就用 <see cref="WidgetKit.ContentRect"/>）。
/// </para>
///
/// <para>
/// <b>内容是动态的怎么办：</b>内容变了调一次 <c>host.InvalidateWidgetLayout()</c>，宿主会重新调
/// <see cref="IAutoWidthWidget.Layout"/> 并平滑过渡宽度 —— 老协议插件也是同一条路径，没有额外成本。
/// </para>
///
/// <para>
/// <b>为什么是独立接口而不是 IWidget 上的开关：</b>旧插件程序集不实现本接口，宿主用
/// <c>is IAutoWidthWidget</c> 判一下就知道走哪条路，双方都不需要改一行代码。
/// </para>
/// </summary>
public interface IAutoWidthWidget : IWidget
{
    /// <summary>
    /// 本组件的宽度会不会随内容变化。默认 <c>false</c>。
    ///
    /// <para>
    /// 置 <c>true</c> 后宿主会做两件事：宽度吸到 4px 栅格；变宽立即生效、变窄要连续 600ms
    /// 确认才收。前者避免浮点让岛体抖半像素，后者避免「长句 → 短句 → 长句」时岛体来回抽。
    /// 宽度固定的组件保持默认值，宿主一分钱额外开销都不花。
    /// </para>
    /// </summary>
    bool WidthVaries => false;

    /// <summary>
    /// 描述当前内容。返回 null 或空盒子时宿主按 0 宽处理（该组件本帧不显示）。
    /// <b>不要在这里做 IO、加锁或耗时计算</b> —— 宿主只在组件注册表版本变化 / 你主动通知重测时调用。
    /// </summary>
    Box? Layout(float availableHeight);
}

/// <summary>
/// AutoWidth 协议的测量工具。所有数字口径都在这里，插件侧不该再出现 <c>+ 2f</c> 这类余量。
/// </summary>
public static class WidgetKit
{
    /// <summary>宿主两个组件槽位之间的间距。老协议插件过去只能硬编码 16，宿主一改就全错。</summary>
    public const float SlotGap = 16f;

    /// <summary>
    /// 宿主给 AutoWidth 槽位统一加的左右留白（单侧）。插件不需要自己留，也不需要知道具体值。
    /// 1px 足够避免字形贴着岛体边缘，再多就是白送宽度（还容易把插件行顶出预算）。
    /// </summary>
    public const float AutoPadX = 1f;

    // 测量用的画笔：进程级复用（与主渲染的 static readonly 画笔同一条规矩），不进稳态帧路径。
    private static readonly SKPaint _measurePaint = new() { IsAntialias = true };

    // 测量可能从渲染线程和 UI 线程同时进来（SetPluginRowBudget 两条路都会触发重测），SKPaint 不是线程安全的。
    private static readonly object _measureLock = new();

    public static TextBox Text(string? text, float size, TextWeight weight = TextWeight.Normal)
        => new(text ?? "", size, weight);

    public static FixedBox Icon(float width) => new(width);

    public static GapBox Gap(float width) => new(width);

    public static RowBox Row(params Box[] parts) => new(parts, 0f);

    public static RowBox Row(float gap, params Box[] parts) => new(parts, gap);

    public static StackBox Stack(params Box[] rows) => new(rows);

    /// <summary>
    /// 内容宽度下限（<b>不含</b>宿主加的两侧留白 —— 想让最终槽位至少 132，这里传 <c>132 − 2×AutoPadX</c>）。
    /// </summary>
    public static MinBox Min(float minWidth, Box inner) => new(minWidth, inner);

    /// <summary>
    /// 取绘制应使用的字体面 —— 和 <see cref="Measure"/> 用的是同一份，<b>借来的，不要 Dispose</b>。
    /// AutoWidth 插件画文字时用它，别再自己解析字体，否则字重一换就量画不一致。
    /// </summary>
    public static SKTypeface Typeface(TextWeight weight = TextWeight.Normal) => weight switch
    {
        TextWeight.Bold => FontConfig.Bold,
        TextWeight.SemiBold => FontConfig.SemiBold,
        _ => FontConfig.Normal,
    };

    /// <summary>
    /// 盒子 → 折叠态槽位宽度：内容宽向上取整（避免浮点把最后一两个字挤到槽外）+ 两侧留白。
    /// 空盒 / 全零盒返回 0，宿主据此当「本帧不显示」。
    /// </summary>
    public static float Measure(Box? box, float padX = AutoPadX)
    {
        if (box == null) return 0f;

        float content;
        try { content = MeasureCore(box); }
        catch { return 0f; }

        if (!(content > 0f) || float.IsInfinity(content) || float.IsNaN(content)) return 0f;

        float total = MathF.Ceiling(content) + MathF.Max(0f, padX) * 2f;
        return total > 0f ? total : 0f;
    }

    /// <summary>槽位 → 内容区（去掉宿主加的左右留白）。插件在 <c>Draw</c> 里想按内容区排版就用它。</summary>
    public static SKRect ContentRect(SKRect slot)
    {
        float l = slot.Left + AutoPadX;
        float r = slot.Right - AutoPadX;
        if (r < l) r = l;
        return new SKRect(l, slot.Top, r, slot.Bottom);
    }

    private static float MeasureCore(Box box)
    {
        switch (box)
        {
            case TextBox t:
                return MeasureText(t.Text, t.Size, t.Weight);
            case FixedBox f:
                return MathF.Max(0f, f.Width);
            case GapBox g:
                return MathF.Max(0f, g.Width);
            case RowBox row:
            {
                float w = 0f;
                for (int i = 0; i < row.Parts.Length; i++)
                {
                    if (row.Parts[i] is { } part) w += MeasureCore(part);
                    if (i < row.Parts.Length - 1) w += row.Gap;
                }
                return w > 0f ? w : 0f;
            }
            case StackBox stack:
            {
                float w = 0f;
                for (int i = 0; i < stack.Rows.Length; i++)
                {
                    float r = stack.Rows[i] is { } row2 ? MeasureCore(row2) : 0f;
                    if (r > w) w = r;
                }
                return w;
            }
            case MinBox min:
                return min.Inner is { } inner
                    ? MathF.Max(MathF.Max(0f, min.MinWidth), MeasureCore(inner))
                    : MathF.Max(0f, min.MinWidth);
            default:
                // 插件自己派生的盒子不认（见 Box 的注释），返回 0 比抛异常安全：
                // 抛异常会让宿主熔断整个组件，而这里只是「量不出来」。
                return 0f;
        }
    }

    private static float MeasureText(string text, float size, TextWeight weight)
    {
        if (string.IsNullOrEmpty(text) || !(size > 0f)) return 0f;

        lock (_measureLock)
        {
            // 字体每次现取：宿主换字体时会 Dispose 旧字体面，缓存下来就是 use-after-dispose。
            var face = Typeface(weight);
            if (face != null) _measurePaint.Typeface = face;
            _measurePaint.TextSize = size;
            return _measurePaint.MeasureText(text);
        }
    }
}
