using System.Diagnostics;
using SkiaSharp;

namespace NotchPeninsula;

public static partial class Renderer
{
    internal const string MediaRhythmEnabledKey = "MediaRhythmEnabled";
    internal const string MediaRhythmColorKey = "MediaRhythmColor";
    internal enum MediaRhythmColorMode { Artwork, White, Black, Pink }

    private static int MediaBackgroundOpacityPercent => ImmersiveCoverOpacityPercents[ImmersiveCoverOpacity];

    private static volatile bool _mediaRhythmEnabled;
    private static volatile MediaRhythmColorMode _mediaRhythmColor;
    internal static bool MediaRhythmEnabled
    {
        get => _mediaRhythmEnabled;
        set
        {
            _mediaRhythmEnabled = value;
            if (IslandLiquidGlass) ThemeMode = 2;
        }
    }

    internal static MediaRhythmColorMode MediaRhythmColor
    {
        get => _mediaRhythmColor;
        set => _mediaRhythmColor = (MediaRhythmColorMode)Math.Clamp((int)value, 0, (int)MediaRhythmColorMode.Pink);
    }

    private static readonly SKColor MediaSpectrumPink = new(130, 88, 97);
    private static readonly SKColor[] RhythmPinkPalette = [MediaSpectrumPink];
    private static readonly SKColor[] RhythmWhitePalette = [SKColors.White];
    private static readonly SKColor[] RhythmBlackPalette = [SKColors.Black];
    private static readonly SKPaint RhythmPaint = new()
    {
        IsAntialias = true,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.12f)
    };
    private static readonly SKPaint RhythmFadePaint = new();
    private static readonly SKPath RhythmBlob = new();
    private static SKBitmap? _rhythmSampledArtwork;
    private static SKColor[] _rhythmArtworkPalette = RhythmPinkPalette;
    private static SKColor[]? _rhythmShaderPalette;
    private static SKShader[] _rhythmShaders = [];
    private static long _rhythmLastFrame;
    private static float _rhythmPhase;
    private static float _rhythmBass;
    private static MediaBackgroundFade _mediaRhythmFade;

    private static float UpdateMediaRhythmFade(bool enabled, bool mediaVisible)
    {
        if (!mediaVisible)
        {
            _mediaRhythmFade = default;
            _rhythmBass = 0f;
            return 0f;
        }

        long now = Stopwatch.GetTimestamp();
        _mediaRhythmFade = _mediaRhythmFade.Retarget(enabled, now);
        return _mediaRhythmFade.OpacityAt(now);
    }

    private static void DrawMediaRhythm(SKCanvas canvas, SKBitmap? artwork, bool playing,
        float lowestBand, int opacity, float layerOpacity, float blobTransmission = 1f)
    {
        if (opacity == 0) return;
        var palette = MediaRhythmColor switch
        {
            MediaRhythmColorMode.White => RhythmWhitePalette,
            MediaRhythmColorMode.Black => RhythmBlackPalette,
            MediaRhythmColorMode.Pink => RhythmPinkPalette,
            _ => GetRhythmArtworkPalette(artwork)
        };
        if (!ReferenceEquals(palette, _rhythmShaderPalette))
        {
            RhythmPaint.Shader = null;
            foreach (var shader in _rhythmShaders) shader.Dispose();
            _rhythmShaders = palette.Select(color => SKShader.CreateRadialGradient(
                SKPoint.Empty, 1.15f, [color, color.WithAlpha(198), color.WithAlpha(0)],
                [0f, 0.42f, 1f], SKShaderTileMode.Clamp)).ToArray();
            _rhythmShaderPalette = palette;
        }

        long now = Stopwatch.GetTimestamp();
        float dt = Math.Min(0.05f, (float)Stopwatch.GetElapsedTime(_rhythmLastFrame, now).TotalSeconds);
        _rhythmLastFrame = now;
        // 最左柱的原始目标单独缓动。起跳逐帧跟上，回落仍比频谱柱的 12% 快。
        float bass = playing ? Math.Clamp(lowestBand, 0f, 1f) : 0f;
        float response = bass > _rhythmBass ? 0.40f : 0.22f;
        _rhythmBass += (bass - _rhythmBass) * response;
        if (bass == 0f && _rhythmBass < 0.005f) _rhythmBass = 0f;
        _rhythmPhase = (_rhythmPhase + dt * _rhythmBass * 3f) % MathF.Tau;

        // 低档保留柔和的叠色，高档给流体留足强度，避免再被上层的亚克力色调压暗。
        float strength = opacity / 100f;
        float blobAlpha = (150f + _rhythmBass * 78f) * strength * (0.45f + 0.55f * strength) * blobTransmission;

        var bounds = _bgPath.Bounds;
        canvas.Save();
        canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);
        // 淡入淡出作用于整个律动层，避免重叠色团随独立 alpha 改变叠加亮度。
        bool fadeLayer = layerOpacity < 1f;
        if (fadeLayer)
        {
            RhythmFadePaint.Color = SKColors.White.WithAlpha((byte)MathF.Round(255f * layerOpacity));
            canvas.SaveLayer(bounds, RhythmFadePaint);
        }
        const int blobCount = 5;
        Span<SKPoint> points = stackalloc SKPoint[12];
        for (int i = 0; i < blobCount; i++)
        {
            float phase = _rhythmPhase + i * 1.7f;
            // 两端各有一个色团，按轮廓完整边界布置，覆盖刘海向外伸出的倒角。
            float x = bounds.Left + bounds.Width * (i / (blobCount - 1f) + MathF.Sin(phase) * 0.055f);
            float y = bounds.MidY + bounds.Height * (MathF.Cos(phase) * 0.17f
                - (i == 0 || i == blobCount - 1 ? 0.24f : 0f));
            float scale = 1f + _rhythmBass * 0.32f;
            float radiusX = bounds.Width * (0.31f + MathF.Sin(phase) * 0.04f) * scale;
            float radiusY = Math.Max(16f, bounds.Height * 0.78f) * scale;

            for (int j = 0; j < points.Length; j++)
            {
                float angle = j * MathF.Tau / points.Length;
                float radius = 1f + 0.16f * MathF.Sin(angle * 3f + phase)
                    + 0.12f * MathF.Cos(angle * 2f - phase * 2f) * (1f + _rhythmBass);
                points[j] = new SKPoint(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            }

            RhythmBlob.Rewind();
            RhythmBlob.MoveTo((points[^1].X + points[0].X) / 2f, (points[^1].Y + points[0].Y) / 2f);
            for (int j = 0; j < points.Length; j++)
            {
                var p = points[j];
                var q = points[(j + 1) % points.Length];
                RhythmBlob.QuadTo(p.X, p.Y, (p.X + q.X) / 2f, (p.Y + q.Y) / 2f);
            }
            RhythmBlob.Close();
            RhythmPaint.Shader = _rhythmShaders[i % _rhythmShaders.Length];
            RhythmPaint.Color = SKColors.White.WithAlpha((byte)MathF.Round(blobAlpha));
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(radiusX, radiusY);
            canvas.DrawPath(RhythmBlob, RhythmPaint);
            canvas.Restore();
        }
        if (fadeLayer) canvas.Restore();
        canvas.Restore();
    }

    private static SKColor[] GetRhythmArtworkPalette(SKBitmap? artwork)
    {
        if (ReferenceEquals(artwork, _rhythmSampledArtwork)) return _rhythmArtworkPalette;
        _rhythmSampledArtwork = artwork;
        _rhythmArtworkPalette = RhythmPinkPalette;
        if (artwork == null || artwork.Width == 0 || artwork.Height == 0)
            return _rhythmArtworkPalette;

        using var sample = artwork.Resize(new SKImageInfo(24, 24), SKFilterQuality.Low);
        if (sample == null) return _rhythmArtworkPalette;

        // 统计相近颜色并按占比和饱和度排序，避免同一色系的几个灰阶挤掉其他主色。
        var bins = new Dictionary<int, (int Count, int Red, int Green, int Blue)>();
        foreach (var pixel in sample.Pixels)
        {
            if (pixel.Alpha < 128) continue;
            int key = (pixel.Red >> 5) << 6 | (pixel.Green >> 5) << 3 | (pixel.Blue >> 5);
            var bin = bins.GetValueOrDefault(key);
            bins[key] = (bin.Count + 1, bin.Red + pixel.Red, bin.Green + pixel.Green, bin.Blue + pixel.Blue);
        }
        var candidates = bins.Values.Select(bin => (
            Color: new SKColor((byte)(bin.Red / bin.Count), (byte)(bin.Green / bin.Count), (byte)(bin.Blue / bin.Count)),
            bin.Count))
            .OrderByDescending(bin => bin.Count * (96 + Math.Max(bin.Color.Red, Math.Max(bin.Color.Green, bin.Color.Blue))
                - Math.Min(bin.Color.Red, Math.Min(bin.Color.Green, bin.Color.Blue))));
        var palette = new List<SKColor>(4);
        foreach (var candidate in candidates)
        {
            var color = candidate.Color;
            if (palette.Any(other => ColorDistanceSquared(color, other) < 42 * 42)) continue;
            palette.Add(color);
            if (palette.Count == 4) break;
        }

        // 纯色封面没有足够主色时，从原色生成明暗变化。
        if (palette.Count == 0) return _rhythmArtworkPalette;
        var primary = palette[0];
        if (palette.Count < 3)
            palette.Add(new SKColor((byte)((primary.Red + 255) / 2), (byte)((primary.Green + 255) / 2), (byte)((primary.Blue + 255) / 2)));
        if (palette.Count < 3)
            palette.Add(new SKColor((byte)(primary.Red / 2), (byte)(primary.Green / 2), (byte)(primary.Blue / 2)));
        return _rhythmArtworkPalette = palette.ToArray();
    }

    private static int ColorDistanceSquared(SKColor a, SKColor b)
    {
        int r = a.Red - b.Red, g = a.Green - b.Green, bDistance = a.Blue - b.Blue;
        return r * r + g * g + bDistance * bDistance;
    }
}
