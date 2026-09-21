using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        /// <summary>
        /// Toast 文本右边界与渐隐遮罩起点之间的兜底余量（逻辑像素）。
        ///
        /// 宽度公式与 <c>Draw</c> 里的排版是两套独立计算：文字宽由 <c>BuildTextRuns</c> 分 run 累加，
        /// 绘制时又按 run 逐段画（跨 run 的 kerning 会丢一点），再加上岛体宽度是弹簧动画、
        /// 可能稳定在目标值下方零点几像素 —— 余量取 0 时就会出现「刚好卡在遮罩边缘」：
        /// 文字既显示不全（末尾被渐隐吃掉），又因为缓存宽度认为放得下而不触发加宽。
        /// </summary>
        private const float TOAST_TEXT_MARGIN = 8f;

        /// <summary>完整模式右上角「现在」的预留宽度，必须与 <c>Draw</c> 里 <c>toastMaxTextRight -= 36f</c> 一致。</summary>

        private const float TOAST_FULL_MODE_RIGHT_RESERVE = 36f;

        // 计算Toast消息自适应宽度，限制最大宽度（与岛体总长上限一致）

        public static float GetToastAutoWidth()
        {
            float maxTextW = IsToastFullMode
                ? Math.Max(_cachedToastTitleWidth, Math.Max(_cachedToastBodyWidth, _cachedToastAppNameWidth))
                : Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            // 68 = 左侧 chrome（14 左边距 + 28 图标 + 10 间距）+ 右侧 16 内边距，
            // 与 Draw 里 toastTextX / toastMaxTextRight 的取值严格对应，改一处必须同步另一处。
            float w = maxTextW + 68f;
            // 🩹 完整模式右上角要放「现在」：Draw 里让了 36px，这里必须一起让，
            //    否则文本右边界永远比遮罩起点多出 36px —— 每行末尾都会被渐隐截掉一截。
            if (IsToastFullMode) w += TOAST_FULL_MODE_RIGHT_RESERVE;
            // 紧凑模式：左侧文本之外还需为右侧双行信息（现在 + 应用名）预留空间。
            // 取「固定预留」与「实测占宽」的较大者：应用名较长时按实测值预留，
            // 否则右侧信息会实际压进左侧消息文字里，把消息尾巴挤到遮罩下面。
            if (IsToastCompactMode) w += Math.Max(COMPACT_RIGHT_WIDTH, _cachedToastCompactRightWidth);
            // 🩹 兜底余量：保证文本右边界不会正好落在遮罩起点上（见 TOAST_TEXT_MARGIN 注释）
            w += TOAST_TEXT_MARGIN;
            return Math.Min(Math.Max(TOAST_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        // 📋 剪贴板链接面板自适应宽度：媒体控制器同款基准尺寸，链接过长时按文本加宽，
        //    封顶宽度与消息通知弹窗的最大长度保持一致

        private const float CLIPBOARD_EXTRA_WIDTH = 10f; // 计算宽度之外的视觉呼吸量，避免文本贴边

        public static float GetClipboardAutoWidth(string url)
        {
            EnsureClipboardTextCache(url);
            float w = 14f + 20f + 10f + _cachedClipboardTextWidth + 10f + 22f + 14f + CLIPBOARD_EXTRA_WIDTH;
            return Math.Min(Math.Max(MEDIA_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        // 📋 「打开」按钮命中判定：本帧未绘制则热区为空，天然不会在收起后误触发

        public static bool HitClipboardOpen(float x, float y)
            => _clipboardOpenHit.Width > 0f && _clipboardOpenHit.Contains(x, y);

        private static void EnsureClipboardTextCache(string url)
        {
            if (_lastClipboardUrl == url) return;
            _lastClipboardUrl = url ?? "";
            BuildTextRuns(_lastClipboardUrl, _textPaint, _semiBoldTypeface, _cachedClipboardRuns, out _cachedClipboardTextWidth);
        }

        // 📋 剪贴板链接面板：左「链接图标」+ 中间链接 + 右「打开」按钮（尺寸与媒体控制器同款）

        private static void DrawClipboard(SKCanvas canvas, string url, float left, float right, float currentHeight, float textOffsetY)
        {
            EnsureClipboardTextCache(url);

            // 左侧「链接」图标：细线条矢量路径，颜色跟随主题的纯黑 / 纯白。
            // （原先是 data/image/clipboard.png 位图 + 圆角裁切，现改为矢量直绘：任意 DPI 都锐利、
            //   不再有位图缩放的毛边，也不再需要裁切路径。）
            float iconSize = 20f;
            float iconX = left + 14f;
            float iconY = (currentHeight - iconSize) / 2f + textOffsetY;
            DrawSvgPath(canvas, _clipboardLinkPaint, iconX, iconY, _clipboardLinkPath, iconSize / ClipboardIconCanvas);

            // 右侧「打开」按钮布局（先算坐标，按钮本体在文本之后绘制，保证永远压在最上层不被遮挡）
            float btnSize = 22f;
            float btnRight = right - 14f;
            float btnLeft = btnRight - btnSize;
            float btnTop = (currentHeight - btnSize) / 2f + textOffsetY;
            _clipboardOpenHit = new SKRect(btnLeft - 4f, btnTop - 3f, btnRight + 4f, btnTop + btnSize + 3f);

            // 中间链接文本（单行垂直居中）。
            // 关键保护：把文本严格裁剪在 [textX, btnLeft-10] 区域内，超长只渐隐截断文字，
            // 绝不绘制到按钮热区上 —— 任何岛体宽度（含弹簧动画过程中）都不会遮挡「打开」按钮。
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

            // 按钮最后绘制：即使动画中途岛体宽度暂时不足，按钮也完整可见可点
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
        // 🩹 紧凑模式右侧双行信息（“现在”+ 应用名，小号字体）的真实占宽，随 toast 一起缓存。
        //    GetToastAutoWidth 用它来预留右侧空间 —— 只写死 COMPACT_RIGHT_WIDTH 的话，
        //    应用名一长（如“Windows 安全中心”）右侧就会实际吃进左侧消息文字，尾巴被遮罩截掉。

        private static float _cachedToastCompactRightWidth = 0f;

        // 📋 剪贴板链接面板专用缓存（只在链接变化 / 换字体时重建一次，稳态零重算）

        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedClipboardRuns = new();

        private static string _lastClipboardUrl = "";

        private static float _cachedClipboardTextWidth = 0f;

        private static SKRect _clipboardOpenHit;   // 本帧「打开」按钮命中区，帧首作废

        // Toast 通知整层绘制（图标 / 标题 / 正文 / 紧凑与完整模式的双行信息）。
        // 由 Draw() 在主流程中调用；调用方负责随后的 3 次 Restore 与提前 return。
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

                // 🩹 紧凑模式右侧那块双行信息的真实占宽（与下面绘制用的画笔、文案、字号完全一致），
                //    供 GetToastAutoWidth 预留宽度使用，保证左侧消息文字永远放得下。
                _cachedToastCompactRightWidth = Math.Max(
                    _compactTimePaint.MeasureText("现在"),
                    _compactAppPaint.MeasureText(string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName));
            }

            float iconSize = 28f;
            float toastIconX = left + 14f;
            float toastIconY = (currentHeight - iconSize) / 2f;
            var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

            EnsureIconsLoaded();

            // 发送端自带的自定义图标优先（HTTP 消息 / 插件提醒，见 ToastIconProvider）；
            // 没带或还没解析完（异步）就退回原有的 QQ → 默认图标判定。
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

            // 紧凑模式：右侧文本块的真实左边缘（“现在”与应用名两行的最大宽者），遮罩与溢出判断都紧贴它
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

            // 渲染发送者标题：自动在常规字体与 Emoji 字体间热切换
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
                // 紧凑模式下遮罩紧贴右侧文本真实左边缘，其余模式按统一文本右边界
                float fadeStart = IsToastCompactMode ? compactRightLeft - fadeWidth : toastMaxTextRight - fadeWidth;

                canvas.Save();
                canvas.Translate(fadeStart, 0);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();

                // 紧凑模式在左侧文本与右侧信息接触处用遮罩过渡，其余模式则覆盖超出右边界的文字
                if (!IsToastCompactMode)
                    canvas.DrawRect(toastMaxTextRight, 0, WINDOW_WIDTH, currentHeight, _bgPaint);
            }

            // 紧凑模式：右侧靠边显示双行信息（右上“现在”纯色、右下应用名灰色，均小号右对齐）
            if (IsToastCompactMode)
            {
                string compactAppName = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                // 先铺与窗口背景同色的实心色块，从右侧文本真实左边缘延伸到右缘，与渐变遮罩衔接，保证左侧长内容不会透到这两行信息上
                canvas.DrawRect(compactRightLeft, 0f, right - 16f, currentHeight, _bgPaint);
                canvas.DrawText("现在", right - 16f - compactNowWidth, line1Y, _compactTimePaint);
                canvas.DrawText(compactAppName, right - 16f - compactAppW, line2Y, _compactAppPaint);
            }
        }

    }
}
