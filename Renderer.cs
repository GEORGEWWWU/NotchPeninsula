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
                Color = SKColors.White, //[cite: 1]
                TextSize = 13, //[cite: 1]
                IsAntialias = true, //[cite: 1]
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) //[cite: 1]
            };

            var textBounds = new SKRect(); //[cite: 1]
            textPaint.MeasureText(displayTitle, ref textBounds); //[cite: 1]
            float textY = (HEIGHT - textBounds.Height) / 2 - textBounds.Top; //[cite: 1]

            float textX = media.IsActive ? left + 16 : left + (currentWidth - textBounds.Width) / 2f; //[cite: 1]

            // 绘制 SMTC 封面（带圆角）
            if (media.IsActive && media.Thumbnail != null)
            {
                float thumbSize = 22f; // 大小适中不喧宾夺主
                float thumbRadius = 4f; // 4px 小圆角裁切
                float thumbY = (HEIGHT - thumbSize) / 2f; // 垂直居中
                var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);

                canvas.Save();
                using var thumbPath = new SKPath();
                thumbPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                canvas.ClipPath(thumbPath, SKClipOperation.Intersect, true); // 裁切封面的圆角
                canvas.DrawBitmap(media.Thumbnail, thumbRect); // 画封面
                canvas.Restore();

                textX += thumbSize + 8; // 让出封面和间距，将文字往右推
            }

            canvas.DrawText(displayTitle, textX, textY, textPaint); //[cite: 1]

            if (isHovered && media.IsActive) //[cite: 1]
            {
                canvas.Save(); // ★ 必须加上 Save
                // 直接把绘制区域裁切在整个刘海的轮廓内，从此告别右下角的直角！
                canvas.ClipPath(path, SKClipOperation.Intersect, true);

                var gradientStart = new SKPoint(right - 120, 0); //[cite: 1]
                var gradientEnd = new SKPoint(right - 90, 0); //[cite: 1]

                // 确保 RGB 通道一致，只改变 Alpha 透明度
                var colors = new[] { bgPaint.Color.WithAlpha(0), bgPaint.Color };

                using var gradientShader = SKShader.CreateLinearGradient(gradientStart, gradientEnd, colors, null, SKShaderTileMode.Clamp); //[cite: 1]
                using var gradientPaint = new SKPaint { Shader = gradientShader }; //[cite: 1]

                canvas.DrawRect(right - 120, 0, 30, HEIGHT, gradientPaint); //[cite: 1]
                canvas.DrawRect(right - 90, 0, 90, HEIGHT, bgPaint); //[cite: 1]

                using var iconPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill }; //[cite: 1]

                DrawSvgPath(canvas, iconPaint, btnPrevX + 10, 11, CreatePrevPath()); //[cite: 1]
                if (media.IsPlaying) DrawSvgPath(canvas, iconPaint, btnPlayX + 10, 11, CreatePausePath()); //[cite: 1]
                else DrawSvgPath(canvas, iconPaint, btnPlayX + 11, 11, CreatePlayPath()); //[cite: 1]
                DrawSvgPath(canvas, iconPaint, btnNextX + 10, 11, CreateNextPath()); //[cite: 1]

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
            path.AddRect(new SKRect(0, 0, 2, 12)); path.MoveTo(10, 0); path.LineTo(2, 6); path.LineTo(10, 12); path.Close();
            return path;
        }

        private static SKPath CreateNextPath()
        {
            var path = new SKPath();
            path.MoveTo(0, 0); path.LineTo(8, 6); path.LineTo(0, 12); path.Close(); path.AddRect(new SKRect(8, 0, 10, 12));
            return path;
        }
    }
}