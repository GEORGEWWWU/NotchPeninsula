using SkiaSharp;

namespace NotchPeninsula.Plugins;

/// <summary>布局引擎：测量 → 排列 → 产出 rect 快照，供绘制与命中检测共用。</summary>
public static class WidgetLayout
{
    public readonly record struct Slot(IWidget Widget, SKRect Rect);

    /// <summary>
    /// 把一组 widget 沿水平方向排列，返回每个 widget 的 rect。
    /// 从左到右，垂直方向在 [topY, topY + height] 内顶部对齐。
    /// </summary>
    public static List<Slot> ArrangeRow(
        IReadOnlyList<IWidget> widgets,
        float left, float topY, float height, float gap)
    {
        var slots = new List<Slot>(widgets.Count);
        float x = left;
        foreach (var w in widgets)
        {
            float width = w.MeasureWidth(height);
            slots.Add(new Slot(w, new SKRect(x, topY, x + width, topY + height)));
            x += width + gap;
        }
        return slots;
    }

    /// <summary>整行总宽度（含间距），供主机宽度决策。</summary>
    public static float MeasureRowWidth(IReadOnlyList<IWidget> widgets, float height, float gap)
    {
        float w = 0f;
        for (int i = 0; i < widgets.Count; i++)
        {
            w += widgets[i].MeasureWidth(height);
            if (i < widgets.Count - 1) w += gap;
        }
        return w;
    }
}
