using System;
using SkiaSharp;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // 🛠️ 1. 布局核心参数 
        // 物理窗口设为最大宽度，足以容纳媒体展开时的尺寸 (260 + 两侧外圆角空间)
        public const int WINDOW_WIDTH = 320;
        public const int BASE_HEIGHT = 34;
        public const int TOAST_HEIGHT = 55;
        public const int MAX_WINDOW_HEIGHT = 51; // 用于锁定系统底层窗口和画布缓冲区大小，极大提升性能
        private static SKBitmap? _defaultIcon;

        // 极速单例缓存加载，只在第一次弹出通知时读取一次，之后全走内存
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

        // 状态目标宽度
        public const int STANDBY_WIDTH = 130; // 待机时的短短形态
        public const int MEDIA_WIDTH = 260;   // 媒体活跃时的长形态

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

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

            // 用动态的 currentHeight 替换原有的 HEIGHT
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
                float iconSize = 22f;
                float toastIconX = left + 14f;
                float toastIconY = (currentHeight - iconSize) / 2f;
                var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

                // 绘制项目默认 Icon，带 4px 小圆角裁切
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
                    // 极端情况加载失败的兜底
                    using var iconPaint = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };
                    canvas.DrawRoundRect(iconRect, 4, 4, iconPaint);
                }

                float toastTextX = toastIconX + iconSize + 10f;
                float toastMaxTextRight = right - 16f;

                using var titlePaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
                using var bodyPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

                // 极限性能：复用同一个渐变 Shader 处理长文本溢出防越界
                using var textShader = SKShader.CreateLinearGradient(
                    new SKPoint(toastMaxTextRight - 15f, 0), new SKPoint(toastMaxTextRight, 0),
                    new[] { SKColors.White, SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);

                titlePaint.Shader = textShader;
                bodyPaint.Shader = textShader;

                // 动态垂直居中计算
                float totalTextHeight = 13.5f + 11.5f + 2f; // 加上间距
                float toastTextY = (currentHeight - totalTextHeight) / 2f;

                // 优先显示 Title（通常是发送者），如果没有则降级显示应用名或"通知"
                string senderName = !string.IsNullOrEmpty(toast.Title) ? toast.Title :
                                   (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");

                // 画双行文字
                canvas.DrawText(senderName, toastTextX, toastTextY + 11.5f, titlePaint);
                canvas.DrawText(toast.Body ?? "", toastTextX, toastTextY + 26f, bodyPaint);

                return; // 阻断后续渲染
            }

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

            // 计算启动动画的 Y 轴偏移和透明度
            float textOffsetY = 0f;
            if (!media.IsActive && startupProgress < 1f)
            {
                textOffsetY = (1f - startupProgress) * 15f; // 从下方 15 像素处升起
                textPaint.Color = textPaint.Color.WithAlpha((byte)(255 * startupProgress)); // 透明度从 0 到 255 渐变
            }

            // 利用亚像素渲染进行极其细腻的微调
            float textY = (currentHeight - textBounds.Height) / 2 - textBounds.Top + 0.3f + textOffsetY;
            float textX = media.IsActive ? left + 16 : left + (currentWidth - textBounds.Width) / 2f;

            // 绘制 SMTC 封面（带圆角）
            if (media.IsActive && media.Thumbnail != null)
            {
                float thumbSize = 22f; // 大小适中不喧宾夺主
                float thumbRadius = 4f; // 4px 小圆角裁切
                float thumbY = (currentHeight - thumbSize) / 2f; // 垂直居中
                var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);

                // 在裁切和绘制封面之前，先画一层浅灰色的外阴影轮廓
                using var shadowPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.White.WithAlpha(50),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f)
                };
                // 画出这层带有模糊属性的圆角矩形底底
                canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, shadowPaint);

                // 下面是原本的裁切和画封面的逻辑
                canvas.Save();
                using var thumbPath = new SKPath();
                thumbPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                canvas.ClipPath(thumbPath, SKClipOperation.Intersect, true); // 裁切封面的圆角
                canvas.DrawBitmap(media.Thumbnail, thumbRect); // 画封面
                canvas.Restore();

                textX += thumbSize + 10; // 让出封面和间距，将文字往右推
            }

            // 封面绘制逻辑执行完毕后，textX 已经确定
            // ==========================================
            // 文本防溢出与尾部渐变遮罩逻辑

            // 动态释放空间
            float rightOccupiedWidth = 16f;
            if (media.IsActive)
            {
                if (isHovered)
                    rightOccupiedWidth = 95f;  // 留给 Prev, Play, Next 控件的空间
                else
                    rightOccupiedWidth = 45f;
            }

            // 动态计算文本最大右边界
            float maxTextRight = right - rightOccupiedWidth;
            float currentTextRight = textX + textBounds.Width;

            if (currentTextRight > maxTextRight)
            {
                // 遮罩渐变的长度 15px
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

                // 黑色背景遮罩层也动态缩放，绝不多遮盖 1 像素的文字
                float maskEnd = right - rightOccupiedWidth + 5f; // 向右多延展5px，防止文字边缘隐约漏出
                float maskStart = maskEnd - 15f;
                var gradientStart = new SKPoint(maskStart, 0);
                var gradientEnd = new SKPoint(maskEnd, 0);
                var colors = new[] { bgPaint.Color.WithAlpha(0), bgPaint.Color };

                using var gradientShader = SKShader.CreateLinearGradient(gradientStart, gradientEnd, colors, null, SKShaderTileMode.Clamp);
                using var gradientPaint = new SKPaint { Shader = gradientShader };

                // 画 15px 渐变区 + 纯黑底色区
                canvas.DrawRect(maskStart, 0, maskEnd - maskStart, currentHeight, gradientPaint);
                canvas.DrawRect(maskEnd, 0, right - maskEnd, currentHeight, bgPaint);

                if (isHovered)
                {
                    // 1. 鼠标悬浮时：渲染完整的媒体控制按钮
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
                    // 正常播放且未悬浮时：渲染 5 根极简律动柱子
                    using var barPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

                    float barWidth = 2f;
                    float spacing = 2.8f;
                    float maxH = 16f;

                    // 绝对靠右对齐
                    // 5根柱子总宽 = (5个柱子 * 2px) + (4个间距 * 2.8px) = 21.2px
                    // 固定将其锚定在距离右边界 16px 的位置
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