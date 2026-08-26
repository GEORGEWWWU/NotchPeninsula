using System;
using System.IO;
using SkiaSharp;
using Svg.Skia;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // 🛠️ 1. 布局核心参数 
        public const int WINDOW_WIDTH = 320;
        public const int BASE_HEIGHT = 34;
        public const int TOAST_HEIGHT = 55;
        public const int MAX_WINDOW_HEIGHT = 51;

        // 状态目标宽度
        public const int STANDBY_WIDTH = 130;
        public const int MEDIA_WIDTH = 260;

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

        // --- 图标缓存 ---
        private static SKBitmap? _defaultIcon;
        private static SKSvg? _qqSvg;
        private static SKSvg? _defaultToastSvg;
        private static bool _svgLoaded = false;

        private static SKBitmap? GetDefaultIcon()
        {
            if (_defaultIcon == null)
            {
                try
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);
                    if (icon != null)
                    {
                        using var bmp = icon.ToBitmap();
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        _defaultIcon = SKBitmap.Decode(ms);
                    }
                }
                catch { }
            }
            return _defaultIcon;
        }

        // 极速单例加载 SVG
        private static void EnsureSvgLoaded()
        {
            if (_svgLoaded) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string qqPath = Path.Combine(baseDir, "data", "image", "qq-icon.svg");
                string defaultPath = Path.Combine(baseDir, "data", "image", "wintoast-icon.svg");

                if (File.Exists(qqPath))
                {
                    _qqSvg = new SKSvg();
                    _qqSvg.Load(qqPath);
                }

                if (File.Exists(defaultPath))
                {
                    _defaultToastSvg = new SKSvg();
                    _defaultToastSvg.Load(defaultPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载 SVG 图标失败", ex);
            }
            finally
            {
                _svgLoaded = true;
            }
        }


        // 接收动态宽度 currentWidth，渲染 Q 弹动画每一帧
        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null)
        {
            canvas.Clear(SKColors.Transparent);

            float left = (WINDOW_WIDTH - currentWidth) / 2f;
            float right = left + currentWidth;
            int btnPrevX = (int)right - 90;
            int btnPlayX = (int)right - 60;
            int btnNextX = (int)right - 30;

            using var bgPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            var path = new SKPath();
            path.MoveTo(left - OUTER_R, 0);
            path.QuadTo(left, 0, left, OUTER_R);
            path.LineTo(left, currentHeight - INNER_R);
            path.QuadTo(left, currentHeight, left + INNER_R, currentHeight);
            path.LineTo(right - INNER_R, currentHeight);
            path.QuadTo(right, currentHeight, right, currentHeight - INNER_R);
            path.LineTo(right, OUTER_R);
            path.QuadTo(right, 0, right + OUTER_R, 0);
            path.Close();

            canvas.DrawPath(path, bgPaint);

            // ==========================================
            // 最高优先级的 Toast 渲染拦截逻辑
            // ==========================================
            if (toast != null)
            {
                float iconSize = 22f; // 改回适合的尺寸
                float toastIconX = left + 14f;
                float toastIconY = (currentHeight - iconSize) / 2f;
                var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

                // 判断并绘制 SVG 图标
                EnsureSvgLoaded();
                SKSvg? targetSvg = null;

                // 简单的判定逻辑，如果是 QQ 通知就用 QQ 图标
                if (toast.ProcessName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ||
                    toast.AppName.Contains("QQ", StringComparison.OrdinalIgnoreCase))
                {
                    targetSvg = _qqSvg;
                }

                if (targetSvg == null)
                {
                    targetSvg = _defaultToastSvg;
                }

                if (targetSvg != null && targetSvg.Picture != null)
                {
                    canvas.Save();
                    // 裁剪圆角
                    using var iconClip = new SKPath();
                    iconClip.AddRoundRect(iconRect, 4, 4);
                    canvas.ClipPath(iconClip, SKClipOperation.Intersect, true);

                    // 利用 Canvas 原生矩阵栈进行平移和缩放，彻底避免手工计算矩阵偏移的 Bug
                    float scaleX = iconSize / targetSvg.Picture.CullRect.Width;
                    float scaleY = iconSize / targetSvg.Picture.CullRect.Height;

                    canvas.Translate(toastIconX, toastIconY);
                    canvas.Scale(scaleX, scaleY);

                    // 极速重绘内存中的矢量指令，不产生任何位图内存
                    canvas.DrawPicture(targetSvg.Picture);

                    canvas.Restore();
                }
                else
                {
                    // SVG 没加载出来的兜底：画原有的程序图标
                    var defaultIcon = GetDefaultIcon();
                    if (defaultIcon != null)
                    {
                        canvas.Save();
                        using var iconClip = new SKPath();
                        iconClip.AddRoundRect(iconRect, 4, 4);
                        canvas.ClipPath(iconClip, SKClipOperation.Intersect, true);
                        using var samplingOpts = new SKPaint { FilterQuality = SKFilterQuality.High };
                        canvas.DrawBitmap(defaultIcon, iconRect, samplingOpts);
                        canvas.Restore();
                    }
                    else
                    {
                        using var iconPaint = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };
                        canvas.DrawRoundRect(iconRect, 4, 4, iconPaint);
                    }
                }


                float toastTextX = toastIconX + iconSize + 10f;
                float toastMaxTextRight = right - 16f;

                using var titlePaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
                using var bodyPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

                using var textShader = SKShader.CreateLinearGradient(
                    new SKPoint(toastMaxTextRight - 15f, 0), new SKPoint(toastMaxTextRight, 0),
                    new[] { SKColors.White, SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);

                titlePaint.Shader = textShader;
                bodyPaint.Shader = textShader;

                float totalTextHeight = 13.5f + 11.5f + 2f;
                float toastTextY = (currentHeight - totalTextHeight) / 2f;

                string senderName = !string.IsNullOrEmpty(toast.Title) ? toast.Title :
                                   (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");

                canvas.DrawText(senderName, toastTextX, toastTextY + 11.5f, titlePaint);
                canvas.DrawText(toast.Body ?? "", toastTextX, toastTextY + 26f, bodyPaint);

                return;
            }

            // ... 下面的媒体绘制逻辑保持不变 ...
            string displayTitle = media.IsActive
                ? (string.IsNullOrEmpty(media.Artist) ? media.Title : $"{media.Artist} - {media.Title}")
                : "Code By Ryen";
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 13,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };

            var textBounds = new SKRect();
            textPaint.MeasureText(displayTitle, ref textBounds);

            float textOffsetY = 0f;
            if (!media.IsActive && startupProgress < 1f)
            {
                textOffsetY = (1f - startupProgress) * 15f;
                textPaint.Color = textPaint.Color.WithAlpha((byte)(255 * startupProgress));
            }

            float textY = (currentHeight - textBounds.Height) / 2 - textBounds.Top + 0.3f + textOffsetY;
            float textX = media.IsActive ? left + 16 : left + (currentWidth - textBounds.Width) / 2f;

            if (media.IsActive && media.Thumbnail != null)
            {
                float thumbSize = 22f;
                float thumbRadius = 4f;
                float thumbY = (currentHeight - thumbSize) / 2f;
                var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);

                using var shadowPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.White.WithAlpha(50),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f)
                };
                canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, shadowPaint);

                canvas.Save();
                using var thumbPath = new SKPath();
                thumbPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                canvas.ClipPath(thumbPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(media.Thumbnail, thumbRect);
                canvas.Restore();

                textX += thumbSize + 10;
            }

            float rightOccupiedWidth = 16f;
            if (media.IsActive)
            {
                if (isHovered)
                    rightOccupiedWidth = 95f;
                else
                    rightOccupiedWidth = 45f;
            }

            float maxTextRight = right - rightOccupiedWidth;
            float currentTextRight = textX + textBounds.Width;

            if (currentTextRight > maxTextRight)
            {
                float fadeWidth = 15f;
                float fadeStart = maxTextRight - fadeWidth;
                float fadeEnd = maxTextRight;

                textPaint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(fadeStart, 0),
                    new SKPoint(fadeEnd, 0),
                    new[] { SKColors.White, SKColors.White.WithAlpha(0) },
                    null,
                    SKShaderTileMode.Clamp
                );
            }
            else
            {
                textPaint.Shader = null;
            }

            canvas.DrawText(displayTitle, textX, textY, textPaint);

            if (media.IsActive)
            {
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, true);

                float maskEnd = right - rightOccupiedWidth + 5f;
                float maskStart = maskEnd - 15f;
                var gradientStart = new SKPoint(maskStart, 0);
                var gradientEnd = new SKPoint(maskEnd, 0);
                var colors = new[] { bgPaint.Color.WithAlpha(0), bgPaint.Color };

                using var gradientShader = SKShader.CreateLinearGradient(gradientStart, gradientEnd, colors, null, SKShaderTileMode.Clamp);
                using var gradientPaint = new SKPaint { Shader = gradientShader };

                canvas.DrawRect(maskStart, 0, maskEnd - maskStart, currentHeight, gradientPaint);
                canvas.DrawRect(maskEnd, 0, right - maskEnd, currentHeight, bgPaint);

                if (isHovered)
                {
                    using var iconPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
                    DrawSvgPath(canvas, iconPaint, btnPrevX + 11, 12, CreatePrevPath());

                    if (media.IsPlaying)
                        DrawSvgPath(canvas, iconPaint, btnPlayX + 10, 11, CreatePausePath());
                    else
                        DrawSvgPath(canvas, iconPaint, btnPlayX + 11, 11, CreatePlayPath());

                    DrawSvgPath(canvas, iconPaint, btnNextX + 11, 12, CreateNextPath());
                }
                else if (bars != null)
                {
                    using var barPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

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
                        canvas.DrawRoundRect(rect, 1.5f, 1.5f, barPaint);
                    }
                }

                canvas.Restore();
            }
        }

        // ... 原有的 SVG Path 方法 (CreatePlayPath 等) 保持不变 ...
        private static void DrawSvgPath(SKCanvas canvas, SKPaint paint, float x, float y, SKPath path)
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        private static SKPath CreatePlayPath()
        {
            var path = new SKPath();
            path.MoveTo(0, 0); path.LineTo(10, 6); path.LineTo(0, 12); path.Close();
            return path;
        }

        private static SKPath CreatePausePath()
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 3, 12)); path.AddRect(new SKRect(6, 0, 9, 12));
            return path;
        }

        private static SKPath CreatePrevPath()
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 2, 10));
            path.MoveTo(8, 0);
            path.LineTo(2, 5);
            path.LineTo(8, 10);
            path.Close();
            return path;
        }

        private static SKPath CreateNextPath()
        {
            var path = new SKPath();
            path.MoveTo(0, 0);
            path.LineTo(6, 5);
            path.LineTo(0, 10);
            path.Close();
            path.AddRect(new SKRect(6, 0, 8, 10));
            return path;
        }
    }
}