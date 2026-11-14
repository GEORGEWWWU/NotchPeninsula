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

        // 四档字号的可调接口与默认值在 Renderer.ToastFont.cs；
        // 下面所有行高 / 块高都由那几个属性推导，运行时改字号无需重编译。

        // 头像与图标是两个尺寸档：头像大 5px，且圆角取自身的 50%（正方形头像 → 正圆）。
        private const float TOAST_ICON_SIZE = 28f;

        private const float TOAST_AVATAR_SIZE = TOAST_ICON_SIZE + 5f;

        private const float TOAST_ICON_RADIUS = 4f;

        // ── 右侧操作按钮（如「拒收」）──
        // 几何只在这里定义一份：绘制与 NotchWindow 的命中判定都走 GetToastActionRect。
        private const float TOAST_ACTION_W = 40f;
        private const float TOAST_ACTION_H = 22f;
        private const float TOAST_ACTION_RIGHT_PAD = 12f;
        private const float TOAST_ACTION_GAP = 8f;

        private const float ToastActionReserve = TOAST_ACTION_RIGHT_PAD + TOAST_ACTION_W + TOAST_ACTION_GAP;

        /// <summary>右侧操作按钮当前是否悬停（WndProc 写、渲染线程读）。</summary>
        public static volatile bool ToastActionHovered;

        /// <summary>右侧操作按钮的命中区（逻辑坐标，right = 岛体右缘）。</summary>
        public static SKRect GetToastActionRect(float right, float height)
        {
            float x2 = right - TOAST_ACTION_RIGHT_PAD;
            float x1 = x2 - TOAST_ACTION_W;
            float y2 = height / 2f + TOAST_ACTION_H / 2f;
            return new SKRect(x1, y2 - TOAST_ACTION_H, x2, y2);
        }

        public static float GetToastAutoWidth(ToastData? toast = null)
        {
            float maxTextW = IsToastFullMode
                ? Math.Max(_cachedToastTitleWidth, Math.Max(_cachedToastBodyWidth, _cachedToastAppNameWidth))
                : Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            float w = maxTextW + 68f;
            if (IsToastFullMode) w += TOAST_FULL_MODE_RIGHT_RESERVE;
            if (IsToastCompactMode) w += Math.Max(COMPACT_RIGHT_WIDTH, _cachedToastCompactRightWidth);
            // 有操作按钮时给右侧让出整个按钮区，否则正文会压到按钮底下。
            if (!string.IsNullOrEmpty(toast?.ActionLabel)) w += ToastActionReserve;
            w += TOAST_TEXT_MARGIN;
            return Math.Min(Math.Max(TOAST_WIDTH, w), IslandWidthCap);
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

        /// <summary>头像取短边居中的正方形源区，避免非正方形头像被拉到圆形里变形。</summary>
        private static SKRect SquareSrcOf(SKBitmap bmp)
        {
            float side = Math.Min(bmp.Width, bmp.Height);
            float x = (bmp.Width - side) / 2f;
            float y = (bmp.Height - side) / 2f;
            return new SKRect(x, y, x + side, y + side);
        }

        /// <summary>预设图标：没带 icon / 头像的来源，按名字挑一张内置图（目前只有 QQ）。</summary>
        private static SKBitmap? ResolvePresetIcon(ToastData toast)
        {
            if (toast.ProcessName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ||
                toast.AppName.Contains("QQ", StringComparison.OrdinalIgnoreCase))
                return _qqIcon;

            return null;
        }

        private static void DrawToastLayer(SKCanvas canvas, ToastData toast, float left, float right, float currentHeight)
        {
            // 整帧用一份字号快照：改字号的那一帧也不会出现「行高按新值、字形按旧值」。
            float senderSize = ToastSenderSize;
            float bodySize = ToastBodySize;
            float appSize = ToastAppSize;
            float metaSize = ToastMetaSize;

            // 字号每帧显式落定：Toast 与媒体组件共用这几支画笔，
            // 不显式赋值就会沿用别处（如媒体歌词 14.5 / 12.5）留下的字号。
            _titlePaint.TextSize = senderSize;
            _bodyPaint.TextSize = bodySize;
            _compactTimePaint.TextSize = metaSize;
            _compactAppPaint.TextSize = metaSize;

            if (_lastToastId != toast.NotificationId)
            {
                _lastToastId = toast.NotificationId;
                _cachedToastSender = !string.IsNullOrEmpty(toast.Title) ? toast.Title : (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");
                _cachedToastBody = toast.Body ?? "";
                _cachedToastAppName = string.IsNullOrEmpty(toast.AppName) ? (string.IsNullOrEmpty(toast.ProcessName) ? "系统通知" : toast.ProcessName) : toast.AppName;

                // 只在接收到新消息时分配一次内存
                BuildTextRuns(_cachedToastSender, _titlePaint, _boldTypeface, _cachedToastSenderRuns, out _cachedToastTitleWidth);
                BuildTextRuns(_cachedToastBody, _bodyPaint, _normalTypeface, _cachedToastBodyRuns, out _cachedToastBodyWidth);
                // 应用名是「小字」档，量宽度时临时切到 appSize，量完复原
                _bodyPaint.TextSize = appSize;
                BuildTextRuns(_cachedToastAppName, _bodyPaint, _normalTypeface, _cachedToastAppNameRuns, out _cachedToastAppNameWidth);
                _bodyPaint.TextSize = bodySize;

                _cachedToastCompactRightWidth = Math.Max(
                    _compactTimePaint.MeasureText("现在"),
                    _compactAppPaint.MeasureText(string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName));
            }

            // 头像：大 5px + 50% 圆角（正圆）；图标维持原尺寸与原圆角。
            bool isAvatar = toast.Avatar != null;
            float iconSize = isAvatar ? TOAST_AVATAR_SIZE : TOAST_ICON_SIZE;
            float iconRadius = isAvatar ? iconSize * 0.5f : TOAST_ICON_RADIUS;
            float toastIconX = left + 14f;
            float toastIconY = (currentHeight - iconSize) / 2f;
            var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

            EnsureIconsLoaded();

            // 图标三级：头像 > icon > 兜底。任何一条消息带了头像，预设图标就让位。
            SKBitmap? targetIcon = isAvatar
                ? toast.Avatar
                : toast.CustomIcon ?? ResolvePresetIcon(toast) ?? _defaultToastIcon;

            // 直接绘制位图，逻辑极其精简
            if (targetIcon != null)
            {
                canvas.Save();
                _clipPath.Rewind();
                _clipPath.AddRoundRect(iconRect, iconRadius, iconRadius);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);

                // 头像按短边居中裁成正方形再画，免得非正方形头像被拉变形。
                if (isAvatar) canvas.DrawBitmap(targetIcon, SquareSrcOf(targetIcon), iconRect, _highQualitySampling);
                else canvas.DrawBitmap(targetIcon, iconRect, _highQualitySampling);
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

            bool hasAction = !string.IsNullOrEmpty(toast.ActionLabel);
            // 右侧「现在 / 应用名 / 操作按钮」的统一起点：有按钮时整块右栏都让到按钮左侧。
            float rightTextEdge = hasAction
                ? Math.Min(right - 16f, right - TOAST_ACTION_RIGHT_PAD - TOAST_ACTION_W - TOAST_ACTION_GAP)
                : right - 16f;

            float toastTextX = toastIconX + iconSize + 10f;
            float toastMaxTextRight = rightTextEdge;
            if (IsToastFullMode) toastMaxTextRight -= 36f; // 完整模式右上角需预留“现在”的空间

            float compactNowWidth = 0f, compactAppW = 0f, compactRightLeft = 0f;
            if (IsToastCompactMode)
            {
                compactNowWidth = _compactTimePaint.MeasureText("现在");
                string compactAppName0 = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                compactAppW = _compactAppPaint.MeasureText(compactAppName0);
                compactRightLeft = rightTextEdge - Math.Max(compactNowWidth, compactAppW);
            }

            // 应用名 → 标题用固定行距；标题 → 正文的行距随字号放大而增加，
            // 这样正文调大时不会被标题压住（算法见 Renderer.ToastFont.cs）。
            float baseSpacing = ToastBaseLineSpacing;
            float bodyGap = ToastBodyLineGap(senderSize, bodySize, appSize, IsToastFullMode, TOAST_HEIGHT);
            // 新增的行距不要全靠居中平分：留一部分给正文单独下移，符合「字大了往下让一点」的观感。
            float gapBias = (bodyGap - ToastBaseLineSpacing) * ToastLineGapBodyBias;

            // 三行文本参数（完整模式）或两行文本参数（缩略模式）
            float totalTextHeight, line1Y, line2Y, line3Y;
            if (IsToastFullMode)
            {
                totalTextHeight = (appSize + 0.5f) + senderSize + bodySize + baseSpacing + bodyGap;
                float toastTextY = (currentHeight - totalTextHeight) / 2f + gapBias;
                line1Y = toastTextY + appSize - 1.5f;                        // 应用名（小字灰度）
                line2Y = line1Y + appSize + 0.5f + baseSpacing;              // 发送者（粗体）
                line3Y = line2Y + senderSize + bodyGap;                      // 消息主体
            }
            else
            {
                totalTextHeight = senderSize + bodySize + bodyGap;
                float toastTextY = (currentHeight - totalTextHeight) / 2f + gapBias;
                line1Y = toastTextY + senderSize - 2f;
                line2Y = line1Y + senderSize + bodyGap;
                line3Y = 0f;
            }

            if (IsToastFullMode)
            {
                // 渲染应用名（小字），右上角同排悬浮“现在”
                // 应用名用【真裁切】收口（不再盖遮罩层）
                canvas.Save();
                canvas.ClipRect(new SKRect(0, 0, toastMaxTextRight, currentHeight), SKClipOperation.Intersect, true);
                _bodyPaint.TextSize = appSize;
                // 元素级明暗：整行按自己那一块底色的黑白走
                _bodyPaint.Color = ToneText(_cachedToastAppName, toastTextX, line1Y, _bodyPaint, 255, false);
                foreach (var run in _cachedToastAppNameRuns)
                {
                    _bodyPaint.Typeface = run.Type;
                    canvas.DrawText(run.Text, toastTextX + run.X, line1Y, _bodyPaint);
                }
                canvas.Restore();
                _bodyPaint.Typeface = _normalTypeface; // 重置

                // 「现在」跟正文同色但字号走小字档，量宽/绘制都按 metaSize
                _bodyPaint.TextSize = metaSize;
                float nowWidth = _bodyPaint.MeasureText("现在");
                DrawToneText(canvas, "现在", rightTextEdge - nowWidth, line1Y, _bodyPaint, 255);
                _bodyPaint.TextSize = bodySize; // 复原
            }

            // 发送者 / 正文同样用真裁切收口 —— 不再叠渐变或纯色层（那层在亚克力 / 低透明度下会露出来）
            float textClipRight = IsToastCompactMode ? compactRightLeft : toastMaxTextRight;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, textClipRight, currentHeight), SKClipOperation.Intersect, true);

            // 完整模式在第2行，缩略模式在第1行
            float senderY = IsToastFullMode ? line2Y : line1Y;
            _titlePaint.Color = ToneText(_cachedToastSender, toastTextX, senderY, _titlePaint, 255, false);
            foreach (var run in _cachedToastSenderRuns)
            {
                _titlePaint.Typeface = run.Type;
                canvas.DrawText(run.Text, toastTextX + run.X, senderY, _titlePaint);
            }
            _titlePaint.Typeface = _boldTypeface; // 重置

            // 渲染内容主体（缩略模式在第2行，完整模式在第3行）
            float bodyY = IsToastFullMode ? line3Y : line2Y;
            _bodyPaint.Color = ToneText(_cachedToastBody, toastTextX, bodyY, _bodyPaint, 255, false);
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
                DrawToneText(canvas, "现在", rightTextEdge - compactNowWidth, line1Y, _compactTimePaint, 255);
                DrawToneText(canvas, compactAppName, rightTextEdge - compactAppW, line2Y, _compactAppPaint, 255);
            }

            // 右侧操作按钮：「拒收」这类一次性动作。画在岛体右缘内侧，和文字裁切区完全分开。
            if (hasAction)
            {
                var actionRect = GetToastActionRect(right, currentHeight);
                var originalBg = _tagBgPaint.Color;
                _tagBgPaint.Color = ToneColor(actionRect, ToastActionHovered ? (byte)70 : (byte)28, sub: true);
                canvas.DrawRoundRect(actionRect, actionRect.Height / 2f, actionRect.Height / 2f, _tagBgPaint);
                _tagBgPaint.Color = originalBg;

                _tagTextPaint.Color = _currentTextColor;
                float aw = _tagTextPaint.MeasureText(toast.ActionLabel);
                DrawToneText(canvas, toast.ActionLabel, actionRect.MidX - aw / 2f,
                    actionRect.MidY + _tagTextPaint.TextSize * 0.35f, _tagTextPaint, 255);
                _tagTextPaint.Color = _currentTextColor;   // 与 ApplyThemeColors 的默认值一致
            }
        }

    }
}
