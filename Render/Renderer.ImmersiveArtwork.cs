using System.Buffers;
using SkiaSharp;

namespace NotchPeninsula;

public static partial class Renderer
{
    private const int ImmersiveArtworkMaxEdge = 160;
    private const float ImmersiveArtworkReferenceWidth = 320f;
    private const float ImmersiveBlurSigma = 12f;
    private static readonly SKPaint ImmersiveArtworkPaint = new() { FilterQuality = SKFilterQuality.Low };
    private static SKBitmap? _immersiveArtworkSource;
    private static SKBitmap? _immersiveBlurredArtwork;

    private static void ClearImmersiveArtwork()
    {
        _immersiveBlurredArtwork?.Dispose();
        _immersiveBlurredArtwork = null;
        _immersiveArtworkSource = null;
    }

    private static void DrawImmersiveArtwork(SKCanvas canvas, SKBitmap artwork, byte opacity)
    {
        if (!ReferenceEquals(artwork, _immersiveArtworkSource))
        {
            ClearImmersiveArtwork();
            _immersiveBlurredArtwork = CreateImmersiveArtwork(artwork);
            _immersiveArtworkSource = artwork;
        }
        if (_immersiveBlurredArtwork is not { } blurred) return;

        // 倒角包含在轮廓边界内。收展和淡入淡出只更新目标矩形与 alpha。
        var island = _bgPath.Bounds;
        float scale = Math.Max(island.Width / blurred.Width, island.Height / blurred.Height);
        float width = blurred.Width * scale, height = blurred.Height * scale;
        var image = SKRect.Create(island.MidX - width / 2f, island.MidY - height / 2f, width, height);
        canvas.Save();
        canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);
        ImmersiveArtworkPaint.Color = SKColors.White.WithAlpha(opacity);
        canvas.DrawBitmap(blurred, image, ImmersiveArtworkPaint);
        canvas.Restore();
    }

    private static unsafe SKBitmap? CreateImmersiveArtwork(SKBitmap artwork)
    {
        int sourceWidth = artwork.Width, sourceHeight = artwork.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        float scale = Math.Min(1f, (float)ImmersiveArtworkMaxEdge / Math.Max(sourceWidth, sourceHeight));
        int width = Math.Max(1, (int)MathF.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)MathF.Round(sourceHeight * scale));
        var blurred = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using (var canvas = new SKCanvas(blurred))
            {
                ImmersiveArtworkPaint.Color = SKColors.White;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(artwork, SKRect.Create(width, height), ImmersiveArtworkPaint);
            }

            // 三轮可分离盒式模糊近似高斯。始终在小图上处理，避开 Skia 的逐帧滤镜缓存。
            // 模糊尺度按代表性岛宽计算，窗口收展时只缩放这一张图。
            float sigma = ImmersiveBlurSigma * width / ImmersiveArtworkReferenceWidth;
            int radius = (int)MathF.Round((MathF.Sqrt(1f + 4f * sigma * sigma) - 1f) / 2f);
            if (radius > 0)
            {
                int count = width * height;
                uint[] buffer = ArrayPool<uint>.Shared.Rent(count);
                try
                {
                    Span<uint> pixels = new((void*)blurred.GetPixels(), count);
                    Span<uint> scratch = buffer.AsSpan(0, count);
                    for (int pass = 0; pass < 3; pass++)
                    {
                        BlurArtworkLines(pixels, scratch, height, width, width, 1, radius);
                        BlurArtworkLines(scratch, pixels, width, height, 1, width, radius);
                    }
                    blurred.NotifyPixelsChanged();
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(buffer);
                }
            }

            blurred.SetImmutable();
            return blurred;
        }
        catch
        {
            blurred.Dispose();
            throw;
        }
    }

    private static void BlurArtworkLines(ReadOnlySpan<uint> source, Span<uint> target,
        int lineCount, int length, int lineStep, int pixelStep, int radius)
    {
        int windowSize = radius * 2 + 1;
        int last = length - 1;
        for (int line = 0; line < lineCount; line++)
        {
            int start = line * lineStep;
            ulong sum = 0;
            for (int offset = -radius; offset <= radius; offset++)
                sum += PackArtworkChannels(source[start + Math.Clamp(offset, 0, last) * pixelStep]);

            for (int x = 0; x < length; x++)
            {
                uint blue = (ushort)sum / (uint)windowSize;
                uint green = (ushort)(sum >> 16) / (uint)windowSize;
                uint red = (ushort)(sum >> 32) / (uint)windowSize;
                uint alpha = (ushort)(sum >> 48) / (uint)windowSize;
                target[start + x * pixelStep] = blue | (green << 8) | (red << 16) | (alpha << 24);
                int leaving = Math.Max(0, x - radius);
                int entering = Math.Min(last, x + radius + 1);
                sum -= PackArtworkChannels(source[start + leaving * pixelStep]);
                sum += PackArtworkChannels(source[start + entering * pixelStep]);
            }
        }
    }

    // 四个 16 位通道分别累加，最大 13 个像素不会产生跨通道进位。
    private static ulong PackArtworkChannels(uint pixel) =>
        (pixel & 0xffUL) | ((pixel & 0xff00UL) << 8) | ((pixel & 0xff0000UL) << 16) | ((pixel & 0xff000000UL) << 24);
}
