using SkiaSharp;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ---- 实况窗的媒体浮层 ----
        // 右键实况窗 = 原有的媒体展开面板，但画在气泡长出来的浮动面板里、主岛不变形。
        // 这里不重画一套：直接复用岛内那套 DrawMediaPanel，视觉与命中几何才不会有第二份真相。
        // 本帧的频谱：岛内展开面板画了右边那组律动条，浮层也必须用同一份数据，
        // 否则两个入口的「主岛媒体面板」看起来就是两套东西。
        private static float[]? _lastBars;

        /// <summary>
        /// 在实况窗展开面板里画一遍「原有媒体展开态」。面板底色 / 圆角 / 裁剪由
        /// <see cref="LiveWindowRenderer"/> 负责，本方法只负责媒体内容（局部坐标）。
        /// </summary>
        internal static void DrawMediaFloatingPanel(SKCanvas canvas, SKRect panel, MediaController media, byte alpha)
        {
            int depth = canvas.SaveCount;
            canvas.Save();
            try
            {
                canvas.Translate(panel.Left, panel.Top);
                DrawMediaPanel(canvas, media, _lastBars, new MediaBlockGeometry(0f, 0f, panel.Width), panel.Height, alpha, floating: true);
            }
            catch (Exception ex)
            {
                Logger.Error("[实况窗] 媒体浮层绘制异常", ex);
            }
            finally
            {
                canvas.RestoreToCount(depth);
            }
        }

        internal static int HitMediaFloatingButton(float x, float y, float width, float height)
            => HitMediaPanelButton(x, y, 0f, width, height);

        internal static bool HitMediaFloatingTimeline(MediaController media, float x, float y, SKRect panel)
        {
            if (panel.Height <= TimelineRevealHeight || !MediaPanelTimelineVisible(media, forceExpanded: true))
                return false;

            var track = MediaTimelineTrack(0f, panel.Width, panel.Height, media.TimelineTotal);
            return track.Width >= 20f && HitTimeline(x, y, track.Left, track.Right, track.MidY);
        }

        internal static float MediaFloatingTimelineRatio(MediaController media, float x, float width, float height)
        {
            var track = MediaTimelineTrack(0f, width, height, media.TimelineTotal);
            return track.Width >= 20f ? Math.Clamp((x - track.Left) / track.Width, 0f, 1f) : 0f;
        }
    }
}
