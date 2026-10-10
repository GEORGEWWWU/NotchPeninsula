using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        private const float TOAST_TEXT_MARGIN = 8f;

        private const float TOAST_FULL_MODE_RIGHT_RESERVE = 36f;

        public static float GetToastAutoWidth()
        {
            float maxTextW = IsToastFullMode
                ? Math.Max(_cachedToastTitleWidth, Math.Max(_cachedToastBodyWidth, _cachedToastAppNameWidth))
                : Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            float w = maxTextW + 68f;
            if (IsToastFullMode) w += TOAST_FULL_MODE_RIGHT_RESERVE;
            if (IsToastCompactMode) w += Math.Max(COMPACT_RIGHT_WIDTH, _cachedToastCompactRightWidth);
            w += TOAST_TEXT_MARGIN;
            return Math.Min(Math.Max(TOAST_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        private static uint _lastToastId = 0;

        private static string _cachedToastSender = "";

        private static string _cachedToastBody = "";

        private static string _cachedToastAppName = "";

        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastSenderRuns = new();

        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastBodyRuns = new();

        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastAppNameRuns = new();

        private static float _cachedToastTitleWidth = 0f;

        private static float _cachedToastBodyWidth = 0f;

        private static float _cachedToastAppNameWidth = 0f;

        private static float _cachedToastCompactRightWidth = 0f;

        private static void DrawToastLayer(SKCanvas canvas, ToastData toast, float left, float right, float currentHeight)
        {
            if (_lastToastId != toast.NotificationId)
            {
                _lastToastId = toast.NotificationId;
                _cachedToastSender = !string.IsNullOrEmpty(toast.Title) ? toast.Title : (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");
                _cachedToastBody = toast.Body ?? "";
                _cachedToastAppName = string.IsNullOrEmpty(toast.AppName) ? (string.IsNullOrEmpty(toast.ProcessName) ? "系统通知" : toast.ProcessName) : toast.AppName;

                // 只在接收到新消息时分配一次内存
                BuildTextRuns(_cachedToastSender, _titlePaint, _boldTypeface, _cachedToastSenderRuns, out _cachedToastTitleWidth);
                BuildTextRuns(_cachedToastBody, _bodyPaint, _normalTypeface, _cachedToastBodyRuns, out _cachedToastBodyWidth);
                BuildTextRuns(_cachedToastAppName, _bodyPaint, _normalTypeface, _cachedToastAppNameRuns, out _cachedToastAppNameWidth);

                _cachedToastCompactRightWidth = Math.Max(
                    _compactTimePaint.MeasureText("现在"),
                    _compactAppPaint.MeasureText(string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName));
            }

            float iconSize = 28f;
            float toastIconX = left + 14f;
            float toastIconY = (currentHeight - iconSize) / 2f;
            var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

            EnsureIconsLoaded();

            SKBitmap? targetIcon = toast.CustomIcon;

            if (targetIcon == null &&
                (toast.ProcessName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ||
                 toast.AppName.Contains("QQ", StringComparison.OrdinalIgnoreCase)))
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
            if (IsToastFullMode) toastMaxTextRight -= 36f; // 完整模式右上角需预留“现在”的空间

            float compactNowWidth = 0f, compactAppW = 0f, compactRightLeft = 0f;
            if (IsToastCompactMode)
            {
                compactNowWidth = _compactTimePaint.MeasureText("现在");
                string compactAppName0 = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                compactAppW = _compactAppPaint.MeasureText(compactAppName0);
                compactRightLeft = (right - 16f) - Math.Max(compactNowWidth, compactAppW);
            }

            float textSpacing = 5f;

            // 三行文本参数（完整模式）或两行文本参数（缩略模式）
            float totalTextHeight, line1Y, line2Y, line3Y;
            if (IsToastFullMode)
            {
                totalTextHeight = 12f + 13.5f + 11.5f + textSpacing * 2;
                float toastTextY = (currentHeight - totalTextHeight) / 2f;
                line1Y = toastTextY + 10f;            // 应用名（小字灰度）
                line2Y = line1Y + 12f + textSpacing;  // 发送者（粗体）
                line3Y = line2Y + 13.5f + textSpacing;// 消息主体
            }
            else
            {
                totalTextHeight = 13.5f + 11.5f + textSpacing;
                float toastTextY = (currentHeight - totalTextHeight) / 2f;
                line1Y = toastTextY + 11.5f;
                line2Y = line1Y + 13.5f + textSpacing;
                line3Y = 0f;
            }

            if (IsToastFullMode)
            {
                // 渲染应用名（小字），右上角同排悬浮“现在”
                // 应用名用【真裁切】收口（不再盖遮罩层）
                canvas.Save();
                canvas.ClipRect(new SKRect(0, 0, toastMaxTextRight, currentHeight), SKClipOperation.Intersect, true);
                foreach (var run in _cachedToastAppNameRuns)
                {
                    _bodyPaint.Typeface = run.Type;
                    canvas.DrawText(run.Text, toastTextX + run.X, line1Y, _bodyPaint);
                }
                canvas.Restore();
                _bodyPaint.Typeface = _normalTypeface; // 重置

                float nowWidth = _bodyPaint.MeasureText("现在");
                canvas.DrawText("现在", right - 16f - nowWidth, line1Y, _bodyPaint);
            }

            // 发送者 / 正文同样用真裁切收口 —— 不再叠渐变或纯色层（那层在亚克力 / 低透明度下会露出来）
            float textClipRight = IsToastCompactMode ? compactRightLeft : toastMaxTextRight;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, textClipRight, currentHeight), SKClipOperation.Intersect, true);

            // 完整模式在第2行，缩略模式在第1行
            float senderY = IsToastFullMode ? line2Y : line1Y;
            foreach (var run in _cachedToastSenderRuns)
            {
                _titlePaint.Typeface = run.Type;
                canvas.DrawText(run.Text, toastTextX + run.X, senderY, _titlePaint);
            }
            _titlePaint.Typeface = _boldTypeface; // 重置

            // 渲染内容主体（缩略模式在第2行，完整模式在第3行）
            float bodyY = IsToastFullMode ? line3Y : line2Y;
            foreach (var run in _cachedToastBodyRuns)
            {
                _bodyPaint.Typeface = run.Type;
                canvas.DrawText(run.Text, toastTextX + run.X, bodyY, _bodyPaint);
            }
            _bodyPaint.Typeface = _normalTypeface; // 重置

            canvas.Restore();   // 结束文字裁切

            if (IsToastCompactMode)
            {
                string compactAppName = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                canvas.DrawText("现在", right - 16f - compactNowWidth, line1Y, _compactTimePaint);
                canvas.DrawText(compactAppName, right - 16f - compactAppW, line2Y, _compactAppPaint);
            }
        }

    }
}
