using SkiaSharp;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        /// <summary>
        /// 浮层要用的文字宽度（浮层按内容自适应宽度，避免长歌词被右边裁掉）。
        /// 只被 UI 线程的 MeasureWidth 调用，用自己的画笔，不去碰渲染线程的共享画笔。
        /// </summary>
        private static readonly SKPaint _panelMeasurePaint = new()
        {
            IsAntialias = true,
            Typeface = FontConfig.Normal,
            TextSize = 12.5f
        };

        internal static float MeasureMediaPanelTextWidth(MediaController media)
        {
            float w = 0f;
            if (!string.IsNullOrEmpty(media.Title))
                w = MathF.Max(w, _panelMeasurePaint.MeasureText(media.Title) * 1.16f);   // 标题 14.5 粗体
            if (MediaController.IsLyricsEnabled && !string.IsNullOrEmpty(media.CurrentLyric))
                w = MathF.Max(w, _panelMeasurePaint.MeasureText(media.CurrentLyric));
            if (IsTranslationLineVisible(media))
                w = MathF.Max(w, _panelMeasurePaint.MeasureText(media.CurrentLyricTranslation) * 0.92f);
            return w;
        }

        // ---- 实况窗的媒体浮层 ----
        // 右键实况窗 = 原有的媒体展开面板，但画在气泡长出来的浮动面板里、主岛不变形。
        // 这里不重画一套：直接复用岛内那套 DrawMediaPanel，视觉与命中几何才不会有第二份真相。

        /// <summary>
        /// 在实况窗展开面板里画一遍「原有媒体展开态」。面板底色 / 圆角 / 裁剪由
        /// <see cref="LiveWindowRenderer"/> 负责，本方法只负责媒体内容（局部坐标）。
        /// </summary>
        internal static void DrawMediaFloatingPanel(SKCanvas canvas, SKRect panel, MediaController media, byte alpha)
        {
            bool oldExpanded = IsMediaExpanded;
            IsMediaExpanded = true;   // TimelineVisible() 按展开态判断：浮层要画时间轴和更大的媒体布局
            int depth = canvas.SaveCount;
            canvas.Save();
            try
            {
                canvas.Translate(panel.Left, panel.Top);
                DrawMediaPanel(canvas, media, null, new MediaBlockGeometry(0f, 0f, panel.Width), panel.Height, alpha);
            }
            catch (Exception ex)
            {
                Logger.Error("[实况窗] 媒体浮层绘制异常", ex);
            }
            finally
            {
                canvas.RestoreToCount(depth);
                IsMediaExpanded = oldExpanded;
                // 面板登记的是局部坐标命中区：清掉，别污染主岛自己的媒体右键 / 封面拖放判定。
                _mediaZoneL = _mediaZoneR = _mediaBlockL = _mediaBlockR = -1f;
                _mediaCoverRect = default;
            }
        }

        /// <summary>
        /// 浮层面板的播放控件命中（面板局部坐标）：与岛内同一套中心距 / 间距 / 半径，
        /// 只是中心从窗口中心换成面板中心。
        /// </summary>
        internal static int HitMediaFloatingButton(float x, float y, float width, float height)
        {
            float centerX = width / 2f;
            float centerY = height - MediaButtonCenterOffsetY;

            for (int i = 0; i < 3; i++)
            {
                float cx = centerX + (i - 1) * MediaButtonSpacing;
                float dx = x - cx, dy = y - centerY;
                if (dx * dx + dy * dy <= MediaButtonRadius * MediaButtonRadius) return i;
            }
            return -1;
        }
    }
}
