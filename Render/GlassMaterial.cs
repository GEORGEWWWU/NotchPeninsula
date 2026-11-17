using SkiaSharp;

namespace NotchPeninsula;

internal static class GlassMaterial
{
    internal const int Blur = 0, Rim = 1, Thickness = 2, Tint = 3, FrameRate = 4, Refraction = 5, CaptureRate = 6;
    internal static readonly string[] Keys =
        ["LiquidGlassBlur", "LiquidGlassRim", "LiquidGlassThickness", "LiquidGlassFrost", "LiquidGlassFps",
         "LiquidGlassRefraction", "LiquidGlassCaptureMs"];
    internal static readonly string[] Labels = ["模糊", "高光", "厚度", "色调", "帧率", "折射", "刷新"];
    internal static readonly string[] Levels = ["最低", "偏低", "标准", "偏高", "最高"];
    internal static readonly string[] FpsLabels = ["30", "60", "90", "120", "144", "自动"];
    private static readonly int[] DefaultLevels = [1, 4, 1, 1, 5, 2, 2];
    private static readonly int[] Settings = (int[])DefaultLevels.Clone();
    internal static readonly string[] BlurLabels = ["关", "轻", "中", "强", "最强"];
    private static readonly byte[] TintAlphas = [2, 6, 14, 26, 44];
    // The layered HWND uses its foreground alpha for mouse hit testing. Leave
    // headroom for antialias coverage and the transition layer's alpha rounding.
    private const byte InteractiveAlpha = 12;
    private const int AutomaticFrameRateLevel = 5; // 与旧「不限」共用档位，升级后自动停止无上限循环。
    private static readonly int[] FrameRates = [30, 60, 90, 120, 144];

    internal static int DefaultLevel(int index) => DefaultLevels[index];
    internal static int MaxLevel(int index) => index == FrameRate ? FpsLabels.Length - 1 : Levels.Length - 1;
    internal static int GetLevel(int index) => Volatile.Read(ref Settings[index]);
    internal static void SetLevel(int index, int level) =>
        Volatile.Write(ref Settings[index], Math.Clamp(level, 0, MaxLevel(index)));

    // The old compositor owns native bitmaps. Only the render thread may
    // change its levels, because doing so invalidates those bitmaps.
    internal static void SyncCaptureSettings()
    {
        LiquidGlass.SetLevel(LiquidGlass.P_Blur, GetLevel(Blur));
        LiquidGlass.SetLevel(LiquidGlass.P_Rim, GetLevel(Rim));
        LiquidGlass.SetLevel(LiquidGlass.P_Thickness, GetLevel(Thickness));
        LiquidGlass.SetLevel(LiquidGlass.P_Frost, GetLevel(Tint));
        LiquidGlass.SetLevel(LiquidGlass.P_Refraction, GetLevel(Refraction));
        LiquidGlass.SetLevel(LiquidGlass.P_CaptureHz, GetLevel(CaptureRate));
    }

    // Composition refreshes the backing independently of the app's foreground frame limiter.
    internal static int GetForegroundFps(bool active, bool hidden)
    {
        if (hidden) return 30;
        int level = GetLevel(FrameRate);
        return level == AutomaticFrameRateLevel ? (active ? 60 : 30) : FrameRates[level];
    }
    internal static volatile bool BackdropReady;
    internal static int BackdropVersion { get; set; }

    private static readonly SKPaint TintPaint = new() { IsAntialias = true };
    private static readonly SKPaint RimPaint = new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round
    };

    internal static SKColor TintColor(bool light, bool hasBackdrop)
    {
        var color = light ? new SKColor(248, 251, 255) : new SKColor(18, 21, 29);
        int tint = GetLevel(Tint);
        return color.WithAlpha(hasBackdrop ? TintAlphas[tint] : (byte)(54 + tint * 24));
    }

    internal static void DrawCoating(SKCanvas canvas, SKPath shape, bool light, float opacity, bool hasBackdrop,
        bool interactiveIsland = false)
    {
        var tint = TintColor(light, hasBackdrop);
        byte alpha = interactiveIsland ? Math.Max(tint.Alpha, InteractiveAlpha) : tint.Alpha;
        TintPaint.Color = tint.WithAlpha((byte)MathF.Round(alpha * opacity));
        canvas.DrawPath(shape, TintPaint);

        int rim = GetLevel(Rim);
        if (rim == 0) return;

        canvas.Save();
        canvas.ClipPath(shape, SKClipOperation.Intersect, true);
        // Offset the contour slightly to put the reflection and shadow on opposing inner edges.
        RimPaint.StrokeWidth = 0.75f + GetLevel(Thickness) * 0.375f;
        canvas.Translate(0f, 0.7f);
        RimPaint.Color = new SKColor(239, 247, 255, (byte)((28 + rim * 18) * opacity));
        canvas.DrawPath(shape, RimPaint);
        canvas.Translate(0f, -1.4f);
        RimPaint.Color = new SKColor(9, 15, 27, (byte)((12 + rim * 9) * opacity));
        canvas.DrawPath(shape, RimPaint);
        canvas.Restore();
    }
}
