using SkiaSharp;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // ==========================================
        // 🛠️ 1. 布局核心参数 (在这里改大小)
        // ==========================================
        public const int WINDOW_WIDTH = 280; // 整个透明画布的宽度 (需要比刘海宽，为了装下外圆角)
        public const int NOTCH_WIDTH = 240;  // 黑色刘海主体的宽度
        public const int HEIGHT = 34;        // 刘海高度

        public const int OUTER_R = 14;       // 灵魂：顶部外圆角的弧度 (越大越平缓)
        public const int INNER_R = 12;       // 底部内圆角的弧度

        // 计算刘海主体的左右边界
        private const float LEFT = (WINDOW_WIDTH - NOTCH_WIDTH) / 2f;
        private const float RIGHT = LEFT + NOTCH_WIDTH;

        // 按钮热区定位 (相对于右边界)
        public const int BTN_PREV_X = (int)RIGHT - 90;
        public const int BTN_PLAY_X = (int)RIGHT - 60;
        public const int BTN_NEXT_X = (int)RIGHT - 30;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered)
        {
            canvas.Clear(SKColors.Transparent);

            // ==========================================
            // 🎨 2. 刘海形状与背景色 (在这里改背景)
            // ==========================================
            using var bgPaint = new SKPaint
            {
                Color = SKColors.Black, // <-- 在这里改刘海颜色，比如 new SKColor(30, 30, 30, 240) 支持半透明
                IsAntialias = true
            };

            // 纯手工绘制带外圆角的苹果式刘海
            var path = new SKPath();
            path.MoveTo(LEFT - OUTER_R, 0); // 从左侧屏幕边缘开始
            path.QuadTo(LEFT, 0, LEFT, OUTER_R); // 左外圆角 (顺滑向下)
            path.LineTo(LEFT, HEIGHT - INNER_R); // 左垂直线
            path.QuadTo(LEFT, HEIGHT, LEFT + INNER_R, HEIGHT); // 左内圆角 (底部兜底)
            path.LineTo(RIGHT - INNER_R, HEIGHT); // 底部水平线
            path.QuadTo(RIGHT, HEIGHT, RIGHT, HEIGHT - INNER_R); // 右内圆角
            path.LineTo(RIGHT, OUTER_R); // 右垂直线
            path.QuadTo(RIGHT, 0, RIGHT + OUTER_R, 0); // 右外圆角 (顺滑平摊回屏幕边缘)
            path.Close();

            canvas.DrawPath(path, bgPaint);

            // ==========================================
            // 📝 3. 文本内容与字体样式 (在这里改字)
            // ==========================================
            string displayTitle = media.IsActive ? $"{media.Artist} - {media.Title}" : "Code By Ryen";
            using var textPaint = new SKPaint
            {
                Color = SKColors.White, // <-- 在这里改文字颜色
                TextSize = 13,          // <-- 在这里改文字大小
                IsAntialias = true,
                // <-- 在这里改字体！可以换成 "Microsoft YaHei" 或你喜欢的像素字体
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };

            var textBounds = new SKRect();
            textPaint.MeasureText(displayTitle, ref textBounds);
            float textY = (HEIGHT - textBounds.Height) / 2 - textBounds.Top;

            // 核心修改：如果是待机状态（未活跃），文本X坐标计算为居中；否则保持靠左（16px边距）
            float textX = media.IsActive
                ? LEFT + 16
                : LEFT + (NOTCH_WIDTH - textBounds.Width) / 2f;

            canvas.DrawText(displayTitle, textX, textY, textPaint);

            // ==========================================
            // 🪄 4. 悬停渐变与按钮 (在这里改动效)
            // ==========================================
            if (isHovered && media.IsActive)
            {
                // A. 绘制渐变透明遮罩
                var gradientStart = new SKPoint(RIGHT - 120, 0);
                var gradientEnd = new SKPoint(RIGHT - 90, 0);
                var colors = new[] { SKColors.Transparent, bgPaint.Color }; // 完美融合背景色

                using var gradientShader = SKShader.CreateLinearGradient(gradientStart, gradientEnd, colors, null, SKShaderTileMode.Clamp);
                using var gradientPaint = new SKPaint { Shader = gradientShader };

                canvas.DrawRect(RIGHT - 120, 0, 30, HEIGHT, gradientPaint); // 渐变区
                canvas.DrawRect(RIGHT - 90, 0, 90, HEIGHT, bgPaint);        // 纯黑覆盖区

                // B. 绘制 SVG 按钮
                using var iconPaint = new SKPaint
                {
                    Color = SKColors.White, // <-- 在这里改按钮颜色
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                DrawSvgPath(canvas, iconPaint, BTN_PREV_X + 10, 11, CreatePrevPath());

                if (media.IsPlaying)
                    DrawSvgPath(canvas, iconPaint, BTN_PLAY_X + 10, 11, CreatePausePath());
                else
                    DrawSvgPath(canvas, iconPaint, BTN_PLAY_X + 11, 11, CreatePlayPath());

                DrawSvgPath(canvas, iconPaint, BTN_NEXT_X + 10, 11, CreateNextPath());
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
            path.MoveTo(0, 0);
            path.LineTo(10, 6);
            path.LineTo(0, 12);
            path.Close();
            return path;
        }

        private static SKPath CreatePausePath()
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 3, 12));
            path.AddRect(new SKRect(6, 0, 9, 12));
            return path;
        }

        private static SKPath CreatePrevPath()
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 2, 12));
            path.MoveTo(10, 0);
            path.LineTo(2, 6);
            path.LineTo(10, 12);
            path.Close();
            return path;
        }

        private static SKPath CreateNextPath()
        {
            var path = new SKPath();
            path.MoveTo(0, 0);
            path.LineTo(8, 6);
            path.LineTo(0, 12);
            path.Close();
            path.AddRect(new SKRect(8, 0, 10, 12));
            return path;
        }
    }
}