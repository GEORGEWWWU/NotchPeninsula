using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SkiaSharp;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // 1. 布局核心参数 
        public const int WINDOW_WIDTH = 320;
        public const int BASE_HEIGHT = 34;
        public const int TOAST_HEIGHT = 55;
        public const int MAX_WINDOW_HEIGHT = 55;

        public const int STANDBY_WIDTH = 130;
        public const int MEDIA_WIDTH = 260;

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

        private static readonly object _renderLock = new();

        // 🚀 全局复用池 (彻底实现 60FPS 零 GC 分配)
        private static readonly SKPaint _bgPaint = new() { Color = SKColors.Black, IsAntialias = true };
        private static readonly SKPaint _fallbackIconPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };

        private static readonly SKTypeface _boldTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        private static readonly SKTypeface _normalTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        private static readonly SKTypeface _semiBoldTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        private static readonly SKPaint _titlePaint = new() { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = _boldTypeface };
        private static readonly SKPaint _bodyPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = _normalTypeface };
        private static readonly SKPaint _textPaint = new() { Color = SKColors.White, TextSize = 13, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _shadowPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f) };
        private static readonly SKPaint _mediaIconPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint _barPaint = new() { Color = SKColors.White, IsAntialias = true };

        private static readonly SKShader _fadeShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(1, 0),
            [SKColors.Black.WithAlpha(0), SKColors.Black],
            null, SKShaderTileMode.Clamp);
        private static readonly SKPaint _fadePaint = new() { Shader = _fadeShader };

        private static readonly SKPath _bgPath = new();
        private static readonly SKPath _clipPath = new();
        private static readonly SKPath _playPath = CreatePlayPath();
        private static readonly SKPath _pausePath = CreatePausePath();
        private static readonly SKPath _prevPath = CreatePrevPath();
        private static readonly SKPath _nextPath = CreateNextPath();

        // 🚀 PNG 图标缓存替换 SVG
        private static SKBitmap? _defaultAppIcon;
        private static SKBitmap? _qqIcon;
        private static SKBitmap? _defaultToastIcon;
        private static bool _iconsLoaded = false;
        private static readonly SKPaint _highQualitySampling = new() { FilterQuality = SKFilterQuality.High };

        private static SKBitmap? GetDefaultAppIcon()
        {
            if (_defaultAppIcon == null)
            {
                try
                {
                    var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (icon != null)
                    {
                        using var bmp = icon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        ms.Position = 0;
                        _defaultAppIcon = SKBitmap.Decode(ms);
                    }
                }
                catch { }
            }
            return _defaultAppIcon;
        }

        private static void EnsureIconsLoaded()
        {
            if (_iconsLoaded) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string qqPath = Path.Combine(baseDir, "data", "image", "qq-icon.png");
                string defaultPath = Path.Combine(baseDir, "data", "image", "wintoast-icon.png");

                // 直接极速解码为位图
                if (File.Exists(qqPath))
                {
                    using var stream = File.OpenRead(qqPath);
                    _qqIcon = SKBitmap.Decode(stream);
                }

                if (File.Exists(defaultPath))
                {
                    using var stream = File.OpenRead(defaultPath);
                    _defaultToastIcon = SKBitmap.Decode(stream);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载 PNG 图标失败", ex);
            }
            finally { _iconsLoaded = true; }
        }

        // 🚀 核心优化：高频字符串与排版宽度缓存
        private static string _lastMediaTitle = "";
        private static string _lastMediaArtist = "";
        private static string _cachedMediaDisplay = "Code By Ryen";
        private static float _cachedMediaTextTop = 0f;
        private static float _cachedMediaTextHeight = 0f;

        private static uint _lastToastId = 0;
        private static string _cachedToastSender = "";
        private static string _cachedToastBody = "";
        private static float _cachedToastTitleWidth = 0f;
        private static float _cachedToastBodyWidth = 0f;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                float left = (WINDOW_WIDTH - currentWidth) / 2f;
                float right = left + currentWidth;
                int btnPrevX = (int)right - 90;
                int btnPlayX = (int)right - 60;
                int btnNextX = (int)right - 30;

                _bgPath.Rewind();
                _bgPath.MoveTo(left - OUTER_R, 0);
                _bgPath.QuadTo(left, 0, left, OUTER_R);
                _bgPath.LineTo(left, currentHeight - INNER_R);
                _bgPath.QuadTo(left, currentHeight, left + INNER_R, currentHeight);
                _bgPath.LineTo(right - INNER_R, currentHeight);
                _bgPath.QuadTo(right, currentHeight, right, currentHeight - INNER_R);
                _bgPath.LineTo(right, OUTER_R);
                _bgPath.QuadTo(right, 0, right + OUTER_R, 0);
                _bgPath.Close();

                canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                // ---------------- [ Toast 消息通知 ] ----------------
                if (toast != null)
                {
                    if (_lastToastId != toast.NotificationId)
                    {
                        _lastToastId = toast.NotificationId;
                        _cachedToastSender = !string.IsNullOrEmpty(toast.Title) ? toast.Title : (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");
                        _cachedToastBody = toast.Body ?? "";
                        _cachedToastTitleWidth = _titlePaint.MeasureText(_cachedToastSender);
                        _cachedToastBodyWidth = _bodyPaint.MeasureText(_cachedToastBody);
                    }

                    float iconSize = 28f;
                    float toastIconX = left + 14f;
                    float toastIconY = (currentHeight - iconSize) / 2f;
                    var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

                    EnsureIconsLoaded();
                    SKBitmap? targetIcon = null;

                    if (toast.ProcessName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ||
                        toast.AppName.Contains("QQ", StringComparison.OrdinalIgnoreCase))
                    {
                        targetIcon = _qqIcon;
                    }
                    targetIcon ??= _defaultToastIcon;

                    // 直接绘制位图，逻辑极其精简
                    if (targetIcon != null)
                    {
                        canvas.Save();
                        _clipPath.Rewind();
                        _clipPath.AddRoundRect(iconRect, 4, 4);
                        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(targetIcon, iconRect, _highQualitySampling);
                        canvas.Restore();
                    }
                    else
                    {
                        var defaultAppIcon = GetDefaultAppIcon();
                        if (defaultAppIcon != null)
                        {
                            canvas.Save();
                            _clipPath.Rewind();
                            _clipPath.AddRoundRect(iconRect, 4, 4);
                            canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                            canvas.DrawBitmap(defaultAppIcon, iconRect, _highQualitySampling);
                            canvas.Restore();
                        }
                        else
                        {
                            canvas.DrawRoundRect(iconRect, 4, 4, _fallbackIconPaint);
                        }
                    }

                    float toastTextX = toastIconX + iconSize + 10f;
                    float toastMaxTextRight = right - 16f;

                    float textSpacing = 5f;
                    float totalTextHeight = 13.5f + 11.5f + textSpacing;
                    float toastTextY = (currentHeight - totalTextHeight) / 2f;

                    float line1Y = toastTextY + 11.5f;
                    float line2Y = line1Y + 13.5f + textSpacing;

                    canvas.DrawText(_cachedToastSender, toastTextX, line1Y, _titlePaint);
                    canvas.DrawText(_cachedToastBody, toastTextX, line2Y, _bodyPaint);

                    if ((toastTextX + _cachedToastTitleWidth > toastMaxTextRight) || (toastTextX + _cachedToastBodyWidth > toastMaxTextRight))
                    {
                        float fadeWidth = 15f;
                        float fadeStart = toastMaxTextRight - fadeWidth;

                        canvas.Save();
                        canvas.Translate(fadeStart, 0);
                        canvas.Scale(fadeWidth, currentHeight);
                        canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                        canvas.Restore();

                        canvas.DrawRect(toastMaxTextRight, 0, WINDOW_WIDTH, currentHeight, _bgPaint);
                    }

                    canvas.Restore();
                    return;
                }

                // ---------------- [ 媒体控制状态 ] ----------------
                if (media.IsActive)
                {
                    if (_lastMediaTitle != media.Title || _lastMediaArtist != media.Artist)
                    {
                        _lastMediaTitle = media.Title ?? "";
                        _lastMediaArtist = media.Artist ?? "";
                        _cachedMediaDisplay = string.IsNullOrEmpty(_lastMediaArtist) ? _lastMediaTitle : $"{_lastMediaArtist} - {_lastMediaTitle}";

                        var tb = new SKRect();
                        _textPaint.MeasureText(_cachedMediaDisplay, ref tb);
                        _cachedMediaTextTop = tb.Top;
                        _cachedMediaTextHeight = tb.Height;
                    }
                }
                else
                {
                    if (_cachedMediaDisplay != "Code By Ryen")
                    {
                        _cachedMediaDisplay = "Code By Ryen";
                        _lastMediaTitle = "";
                        var tb = new SKRect();
                        _textPaint.MeasureText(_cachedMediaDisplay, ref tb);
                        _cachedMediaTextTop = tb.Top;
                        _cachedMediaTextHeight = tb.Height;
                    }
                }

                float textOffsetY = 0f;
                _textPaint.Color = SKColors.White;
                if (!media.IsActive && startupProgress < 1f)
                {
                    textOffsetY = (1f - startupProgress) * 15f;
                    _textPaint.Color = SKColors.White.WithAlpha((byte)(255 * startupProgress));
                }

                float textY = (currentHeight - _cachedMediaTextHeight) / 2 - _cachedMediaTextTop + 0.3f + textOffsetY;
                float textWidth = _textPaint.MeasureText(_cachedMediaDisplay);
                float textX = media.IsActive ? left + 16 : left + (currentWidth - textWidth) / 2f;

                if (media.IsActive && media.Thumbnail != null)
                {
                    float thumbSize = 22f;
                    float thumbRadius = 4f;
                    float thumbY = (currentHeight - thumbSize) / 2f;
                    var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);

                    canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, _shadowPaint);

                    canvas.Save();
                    _clipPath.Rewind();
                    _clipPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                    canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                    canvas.DrawBitmap(media.Thumbnail, thumbRect, _highQualitySampling);
                    canvas.Restore();

                    textX += thumbSize + 10;
                }

                canvas.DrawText(_cachedMediaDisplay, textX, textY, _textPaint);

                if (media.IsActive)
                {
                    float rightOccupiedWidth = isHovered ? 95f : 45f;
                    float maskEnd = right - rightOccupiedWidth + 5f;
                    float maskStart = maskEnd - 15f;

                    canvas.Save();
                    canvas.Translate(maskStart, 0);
                    canvas.Scale(maskEnd - maskStart, currentHeight);
                    canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                    canvas.Restore();

                    canvas.DrawRect(maskEnd, 0, WINDOW_WIDTH, currentHeight, _bgPaint);

                    if (isHovered)
                    {
                        DrawSvgPath(canvas, _mediaIconPaint, btnPrevX + 11, 12, _prevPath);
                        if (media.IsPlaying)
                            DrawSvgPath(canvas, _mediaIconPaint, btnPlayX + 10, 11, _pausePath);
                        else
                            DrawSvgPath(canvas, _mediaIconPaint, btnPlayX + 11, 11, _playPath);
                        DrawSvgPath(canvas, _mediaIconPaint, btnNextX + 11, 12, _nextPath);
                    }
                    else if (bars != null)
                    {
                        float barWidth = 2f;
                        float spacing = 2.8f;
                        float maxH = 16f;
                        float totalBarWidth = 21.2f;
                        float startX = right - 16f - totalBarWidth;

                        for (int i = 0; i < 5; i++)
                        {
                            float h = Math.Max(2f, bars[i] * maxH);
                            float y = (currentHeight - h) / 2f;
                            var rect = new SKRect(startX + i * (barWidth + spacing), y, startX + i * (barWidth + spacing) + barWidth, y + h);
                            canvas.DrawRoundRect(rect, 1.5f, 1.5f, _barPaint);
                        }
                    }
                }

                canvas.Restore();
            }
            finally
            {
                Monitor.Exit(_renderLock);
            }
        }

        private static void DrawSvgPath(SKCanvas canvas, SKPaint paint, float x, float y, SKPath path)
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        private static SKPath CreatePlayPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(10, 6); path.LineTo(0, 12); path.Close(); return path; }
        private static SKPath CreatePausePath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 3, 12)); path.AddRect(new SKRect(6, 0, 9, 12)); return path; }
        private static SKPath CreatePrevPath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 2, 10)); path.MoveTo(8, 0); path.LineTo(2, 5); path.LineTo(8, 10); path.Close(); return path; }
        private static SKPath CreateNextPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(6, 5); path.LineTo(0, 10); path.Close(); path.AddRect(new SKRect(6, 0, 8, 10)); return path; }
    }
}