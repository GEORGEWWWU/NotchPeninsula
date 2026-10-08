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

        //    封顶宽度与消息通知弹窗的最大长度保持一致

        private const float CLIPBOARD_EXTRA_WIDTH = 10f; // 计算宽度之外的视觉呼吸量，避免文本贴边

        public static float GetClipboardAutoWidth(string url)
        {
            EnsureClipboardTextCache(url);
            float w = 14f + 20f + 10f + _cachedClipboardTextWidth + 10f + 22f + 14f + CLIPBOARD_EXTRA_WIDTH;
            return Math.Min(Math.Max(MEDIA_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        public static bool HitClipboardOpen(float x, float y)
            => _clipboardOpenHit.Width > 0f && _clipboardOpenHit.Contains(x, y);

        private static void EnsureClipboardTextCache(string url)
        {
            if (_lastClipboardUrl == url) return;
            _lastClipboardUrl = url ?? "";
            BuildTextRuns(_lastClipboardUrl, _textPaint, _semiBoldTypeface, _cachedClipboardRuns, out _cachedClipboardTextWidth);
        }

        private static void DrawClipboard(SKCanvas canvas, string url, float left, float right, float currentHeight, float textOffsetY)
        {
            EnsureClipboardTextCache(url);

            //   不再有位图缩放的毛边，也不再需要裁切路径。）
            float iconSize = 20f;
            float iconX = left + 14f;
            float iconY = (currentHeight - iconSize) / 2f + textOffsetY;
            DrawSvgPath(canvas, _clipboardLinkPaint, iconX, iconY, _clipboardLinkPath, iconSize / ClipboardIconCanvas);

            float btnSize = 22f;
            float btnRight = right - 14f;
            float btnLeft = btnRight - btnSize;
            float btnTop = (currentHeight - btnSize) / 2f + textOffsetY;
            _clipboardOpenHit = new SKRect(btnLeft - 4f, btnTop - 3f, btnRight + 4f, btnTop + btnSize + 3f);

            // 中间链接文本（单行垂直居中）。
            float textX = iconX + iconSize + 10f;
            float textRightLimit = btnLeft - 10f;
            float textY = currentHeight / 2f + _textPaint.TextSize * 0.36f + textOffsetY;
            canvas.Save();
            canvas.ClipRect(new SKRect(textX, 0f, textRightLimit, currentHeight), SKClipOperation.Intersect, false);
            foreach (var run in _cachedClipboardRuns)
            {
                _textPaint.Typeface = run.Type;
                canvas.DrawText(run.Text, textX + run.X, textY, _textPaint);
            }
            _textPaint.Typeface = _semiBoldTypeface; // 重置，防污染

            if (textX + _cachedClipboardTextWidth > textRightLimit)
            {
                float fadeWidth = 15f;
                float fadeStart = textRightLimit - fadeWidth;
                canvas.Save();
                canvas.Translate(fadeStart, 0);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();
            }
            canvas.Restore(); // 结束文本裁剪区

            DrawSvgPath(canvas, _clipboardOpenPaint, btnLeft, btnTop, _clipboardOpenPath, btnSize / ClipboardIconCanvas);
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

        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedClipboardRuns = new();

        private static string _lastClipboardUrl = "";

        private static float _cachedClipboardTextWidth = 0f;

        private static SKRect _clipboardOpenHit;   // 本帧「打开」按钮命中区，帧首作废

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
                foreach (var run in _cachedToastAppNameRuns)
                {
                    _bodyPaint.Typeface = run.Type;
                    canvas.DrawText(run.Text, toastTextX + run.X, line1Y, _bodyPaint);
                }
                _bodyPaint.Typeface = _normalTypeface; // 重置

                float nowWidth = _bodyPaint.MeasureText("现在");
                canvas.DrawText("现在", right - 16f - nowWidth, line1Y, _bodyPaint);
            }

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

            bool textOverflow;
            if (IsToastFullMode)
                textOverflow = toastTextX + _cachedToastAppNameWidth > toastMaxTextRight ||
                               toastTextX + _cachedToastTitleWidth > toastMaxTextRight ||
                               toastTextX + _cachedToastBodyWidth > toastMaxTextRight;
            else if (IsToastCompactMode)
                textOverflow = toastTextX + _cachedToastTitleWidth > compactRightLeft ||
                               toastTextX + _cachedToastBodyWidth > compactRightLeft;
            else
                textOverflow = toastTextX + _cachedToastTitleWidth > toastMaxTextRight ||
                               toastTextX + _cachedToastBodyWidth > toastMaxTextRight;

            if (textOverflow)
            {
                float fadeWidth = 15f;
                float fadeStart = IsToastCompactMode ? compactRightLeft - fadeWidth : toastMaxTextRight - fadeWidth;

                canvas.Save();
                canvas.Translate(fadeStart, 0);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();

                if (!IsToastCompactMode)
                    canvas.DrawRect(toastMaxTextRight, 0, WINDOW_WIDTH, currentHeight, _bgPaint);
            }

            if (IsToastCompactMode)
            {
                string compactAppName = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                canvas.DrawRect(compactRightLeft, 0f, right - 16f, currentHeight, _bgPaint);
                canvas.DrawText("现在", right - 16f - compactNowWidth, line1Y, _compactTimePaint);
                canvas.DrawText(compactAppName, right - 16f - compactAppW, line2Y, _compactAppPaint);
            }
        }

    }
}
