using SkiaSharp;

namespace NotchPeninsula;

internal static class GlassMaterial
{
    internal const int Refraction = 0, Rim = 1, Thickness = 2, Tint = 3, FrameRate = 4;
    internal static readonly string[] Keys =
        ["LiquidGlassRefraction", "LiquidGlassRim", "LiquidGlassThickness", "LiquidGlassFrost", "LiquidGlassFps"];
    internal static readonly string[] Labels = ["折射", "高光", "厚度", "色调", "帧率"];
    internal static readonly string[] Levels = ["最低", "偏低", "标准", "偏高", "最高"];
    internal static readonly string[] FpsLabels = ["30", "60", "90", "120", "144", "不限"];
    private static readonly int[] DefaultLevels = [2, 2, 2, 1, 2];
    private static readonly int[] Settings = (int[])DefaultLevels.Clone();
    private static readonly int[] FrameRates = [30, 60, 90, 120, 144, 0];

    internal static int DefaultLevel(int index) => DefaultLevels[index];
    internal static int MaxLevel(int index) => index == FrameRate ? FpsLabels.Length - 1 : Levels.Length - 1;
    internal static int GetLevel(int index) => Volatile.Read(ref Settings[index]);
    internal static void SetLevel(int index, int level) =>
        Volatile.Write(ref Settings[index], Math.Clamp(level, 0, MaxLevel(index)));

    // Composition refreshes the backing independently of the app's foreground frame limiter.
    internal static int ForegroundFps => FrameRates[GetLevel(FrameRate)];
    internal const float BackdropOpacity = 0.46f;
    internal static float RefractionPixels => GetLevel(Refraction) * 1.75f;
    internal static float EdgeWidth => 2f + GetLevel(Thickness) * 1.25f;
    internal static volatile bool BackdropReady;
    internal static bool LiveBackdropReady { get; set; }

    // All drawing and region collection takes place on the render thread.
    internal readonly record struct Region(SKRect Rect, float Radius, float InnerRadius, float Opacity);
    private static readonly Region[] Regions = new Region[LiveWindowHost.MaxItems + 1];
    private static int _regionCount;
    internal static ReadOnlySpan<Region> LiveRegions => Regions.AsSpan(0, _regionCount);
    internal static void BeginFrame() => _regionCount = 0;
    internal static void AddRegion(SKRect rect, float radius, float innerRadius, float opacity)
    {
        if (_regionCount < Regions.Length && rect.Width >= 1f && rect.Height >= 1f)
            Regions[_regionCount++] = new(rect, radius, innerRadius, opacity);
    }

    private static readonly SKPaint TintPaint = new() { IsAntialias = true };
    private static readonly SKPaint RimPaint = new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round
    };

    internal static SKColor TintColor(bool light, bool hasBackdrop)
    {
        var color = light ? new SKColor(248, 251, 255) : new SKColor(18, 21, 29);
        int tint = GetLevel(Tint);
        return color.WithAlpha((byte)(hasBackdrop ? 12 + tint * 8 : 54 + tint * 24));
    }

    internal static void DrawCoating(SKCanvas canvas, SKPath shape, bool light, float opacity, bool hasBackdrop)
    {
        var tint = TintColor(light, hasBackdrop);
        TintPaint.Color = tint.WithAlpha((byte)MathF.Round(tint.Alpha * opacity));
        canvas.DrawPath(shape, TintPaint);

        int rim = GetLevel(Rim);
        if (rim == 0) return;

        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        // Offset the contour slightly to put the reflection and shadow on opposing inner edges.
        canvas.Translate(0f, 0.7f);
        RimPaint.Color = new SKColor(239, 247, 255, (byte)((28 + rim * 18) * opacity));
        canvas.DrawPath(shape, RimPaint);
        canvas.Translate(0f, -1.4f);
        RimPaint.Color = new SKColor(9, 15, 27, (byte)((12 + rim * 9) * opacity));
        canvas.DrawPath(shape, RimPaint);
        canvas.Restore();
    }
}
