using SkiaSharp;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // ==========================================
        // 🛠️ 1. 布局核心参数 
        // ==========================================
        // 物理窗口设为最大宽度，足以容纳媒体展开时的尺寸 (260 + 两侧外圆角空间)
        public const int WINDOW_WIDTH = 320;
        public const int HEIGHT = 34;

        // 状态目标宽度
        public const int STANDBY_WIDTH = 120; // 待机时的短短形态
        public const int MEDIA_WIDTH = 260;   // 媒体活跃时的长形态

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

        // 接收动态宽度 currentWidth，渲染 Q 弹动画每一帧
        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth)
        {
            canvas.Clear(SKColors.Transparent); //[cite: 1]

            float left = (WINDOW_WIDTH - currentWidth) / 2f; //[cite: 1]
            float right = left + currentWidth; //[cite: 1]
            int btnPrevX = (int)right - 90; //[cite: 1]
            int btnPlayX = (int)right - 60; //[cite: 1]
            int btnNextX = (int)right - 30; //[cite: 1]

            using var bgPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true }; //[cite: 1]

            // 获取刘海的路径
            var path = new SKPath(); //[cite: 1]
            path.MoveTo(left - OUTER_R, 0); //[cite: 1]
            path.QuadTo(left, 0, left, OUTER_R); //[cite: 1]
            path.LineTo(left, HEIGHT - INNER_R); //[cite: 1]
            path.QuadTo(left, HEIGHT, left + INNER_R, HEIGHT); //[cite: 1]
            path.LineTo(right - INNER_R, HEIGHT); //[cite: 1]
            path.QuadTo(right, HEIGHT, right, HEIGHT - INNER_R); //[cite: 1]
            path.LineTo(right, OUTER_R); //[cite: 1]
            path.QuadTo(right, 0, right + OUTER_R, 0); //[cite: 1]
            path.Close(); //[cite: 1]

            canvas.DrawPath(path, bgPaint); //[cite: 1]

            string displayTitle = media.IsActive ? $"{media.Artist} - {media.Title}" : "Code By Ryen"; //[cite: 1]
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 13,
                IsAntialias = true,
                // 将 "Segoe UI" 替换为 "Microsoft YaHei UI"
                Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };

            var textBounds = new SKRect();
            textPaint.MeasureText(displayTitle, ref textBounds);
            // 利用亚像素渲染进行极其细腻的微调
            float textY = (HEIGHT - textBounds.Height) / 2 - textBounds.Top + 0.3f;

            float textX = media.IsActive ? left + 16 : left + (currentWidth - textBounds.Width) / 2f; //[cite: 1]

            // 绘制 SMTC 封面（带圆角）
            if (media.IsActive && media.Thumbnail != null) //[cite: 20]
            {
                float thumbSize = 22f; // 大小适中不喧宾夺主 //[cite: 20]
                float thumbRadius = 4f; // 4px 小圆角裁切 //[cite: 20]
                float thumbY = (HEIGHT - thumbSize) / 2f; // 垂直居中 //[cite: 20]
                var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize); //[cite: 20]

                // 在裁切和绘制封面之前，先画一层浅灰色的外阴影轮廓
                using var shadowPaint = new SKPaint
                {
                    IsAntialias = true,
                    // 使用半透明白色，在黑底上会自然过渡成浅灰色
                    Color = SKColors.White.WithAlpha(50),
                    // SKBlurStyle.Outer 确保发光只出现在矩形外部，不会干扰到封面本体的颜色
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f)
                };
                // 画出这层带有模糊属性的圆角矩形底底
                canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, shadowPaint);

                // 下面是原本的裁切和画封面的逻辑
                canvas.Save(); //[cite: 20]
                using var thumbPath = new SKPath(); //[cite: 20]
                thumbPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius); //[cite: 20]
                canvas.ClipPath(thumbPath, SKClipOperation.Intersect, true); // 裁切封面的圆角 //[cite: 20]
                canvas.DrawBitmap(media.Thumbnail, thumbRect); // 画封面 //[cite: 20]
                canvas.Restore(); //[cite: 20]

                textX += thumbSize + 10; // 让出封面和间距，将文字往右推 //[cite: 20]
            }

            // 封面绘制逻辑执行完毕后，textX 已经确定
            // ==========================================
            // 文本防溢出与尾部渐变遮罩逻辑

            // 1. 动态计算文本允许的最大右侧边界
            // 悬浮时，将文本右侧允许的边界放宽到 right - 90（刚好是第一个按钮的命中区边缘）
            float maxTextRight = (isHovered && media.IsActive) ? right - 90f : right - 16f;

            float currentTextRight = textX + textBounds.Width;

            if (currentTextRight > maxTextRight)
            {
                // 遮罩渐变的长度缩短到 15px，一点点过渡就行
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

            canvas.DrawText(displayTitle, textX, textY, textPaint); //[cite: 11]

            if (isHovered && media.IsActive) //[cite: 11]
            {
                canvas.Save(); //[cite: 11]
                canvas.ClipPath(path, SKClipOperation.Intersect, true); //[cite: 11]

                // 起点推迟到 105，终点在 90
                var gradientStart = new SKPoint(right - 105, 0);
                var gradientEnd = new SKPoint(right - 90, 0);

                var colors = new[] { bgPaint.Color.WithAlpha(0), bgPaint.Color }; //[cite: 11]

                using var gradientShader = SKShader.CreateLinearGradient(gradientStart, gradientEnd, colors, null, SKShaderTileMode.Clamp); //[cite: 11]
                using var gradientPaint = new SKPaint { Shader = gradientShader }; //[cite: 11]

                // 由于起点变成了 105 到 90，这个渐变矩形的宽度就是 15px
                canvas.DrawRect(right - 105, 0, 15, HEIGHT, gradientPaint);
                canvas.DrawRect(right - 90, 0, 90, HEIGHT, bgPaint); //[cite: 11]

                using var iconPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill }; //[cite: 1]

                // 💡 Prev 和 Next 的 X 偏移量由 +10 改为 +11，Y 由 11 改为 12 
                DrawSvgPath(canvas, iconPaint, btnPrevX + 11, 12, CreatePrevPath());

                // 播放/暂停图标保持原始大小不变，视觉比例更协调
                if (media.IsPlaying)
                    DrawSvgPath(canvas, iconPaint, btnPlayX + 10, 11, CreatePausePath());
                else
                    DrawSvgPath(canvas, iconPaint, btnPlayX + 11, 11, CreatePlayPath());

                DrawSvgPath(canvas, iconPaint, btnNextX + 11, 12, CreateNextPath());

                canvas.Restore(); // 画完悬浮控件后恢复画布
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
            // 宽度由10缩小至8，高度由12缩小至10
            // 左侧竖线：宽 2，高 10
            path.AddRect(new SKRect(0, 0, 2, 10));
            // 右侧三角形：顶点依次为 (8,0), (2,5), (8,10)
            path.MoveTo(8, 0);
            path.LineTo(2, 5);
            path.LineTo(8, 10);
            path.Close();
            return path;
        }

        private static SKPath CreateNextPath()
        {
            var path = new SKPath();
            // 左侧三角形：顶点依次为 (0,0), (6,5), (0,10)
            path.MoveTo(0, 0);
            path.LineTo(6, 5);
            path.LineTo(0, 10);
            path.Close();
            // 右侧竖线：起点 X 为 6，宽 2，高 10
            path.AddRect(new SKRect(6, 0, 8, 10));
            return path;
        }
    }
}