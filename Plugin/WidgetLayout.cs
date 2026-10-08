using SkiaSharp;

namespace NotchPeninsula.Plugins;

public static class WidgetLayout
{
    public readonly record struct Slot(IWidget Widget, SKRect Rect);

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
