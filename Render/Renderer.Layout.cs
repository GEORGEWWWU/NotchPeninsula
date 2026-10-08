using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {

        // ---- 岛体内容布局：两条互斥分支 ----
        //    组合与非组合因此不再各写一套媒体绘制。

        private static void DrawCompositeLayout(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            float left, float currentHeight, float topY, float textOffsetY, byte alpha)
        {
            float currentX = left + 16f;
            float centerY = currentHeight / 2f + textOffsetY;
            const float moduleGap = 16f;

            void DrawClockModule()
            {
                _timePaint.Color = _currentTextColor.WithAlpha(alpha);
                _datePaint.Color = _currentSubTextColor.WithAlpha(alpha);
                float timeBaselineY = centerY + 5f;
                canvas.DrawText(_cachedTimeStr, currentX, timeBaselineY, _timePaint);
                float dateX = currentX + _cachedTimeWidth + 12f;
                canvas.DrawText(_cachedDateStr, dateX, timeBaselineY, _datePaint);
                _clockZoneL = currentX;
                _clockZoneR = dateX + _cachedDateWidth;
                currentX = dateX + _cachedDateWidth + moduleGap;
            }

            // 1. 硬件占用模块
            void DrawHardwareModule()
            {
                UpdateHardwareStats();
                _tagTextPaint.Color = _currentTextColor.WithAlpha(alpha);
                _tagBgPaint.Color = _currentTextColor.WithAlpha((byte)(alpha * 0.12f));
                _barPaint.Color = _currentTextColor.WithAlpha(alpha);
                _barBgPaint.Color = _currentTextColor.WithAlpha((byte)(alpha * 0.20f));
                _tagTextPaint.Typeface = _boldTypeface;

                float textBaseline = centerY - 1f;
                float barTop = centerY + 9f;
                float barH = 3.5f;
                float tagPadX = 3f;
                float tagPadY = 1.5f;
                float tagRadius = 3.5f;
                float gapLabelPct = 4f;
                float gapCpuRam = 16f;

                string cpuLabel = "CPU";
                string ramLabel = "RAM";
                string cpuPct = _pctStrs![_cpuUsage];
                string ramPct = _pctStrs![_ramUsage];
                // 固定资源占用文本最大宽度，防止右侧元素排版跟着抖动
                float cpuLabelW = _tagTextPaint.MeasureText(cpuLabel);
                float ramLabelW = _tagTextPaint.MeasureText(ramLabel);
                float fixedPctW = _textPaint.MeasureText("90%");
                float cpuPctW = fixedPctW;
                float ramPctW = fixedPctW;
                float cpuTagW = cpuLabelW + tagPadX * 2f;
                float ramTagW = ramLabelW + tagPadX * 2f;
                float cpuGroupW = cpuTagW + gapLabelPct + cpuPctW;
                float ramGroupW = ramTagW + gapLabelPct + ramPctW;

                float cpuX = currentX;
                var cpuTagRect = new SKRect(
                    cpuX, textBaseline - 10f - tagPadY,
                    cpuX + cpuTagW, textBaseline + 2.5f + tagPadY);
                canvas.DrawRoundRect(cpuTagRect, tagRadius, tagRadius, _tagBgPaint);
                canvas.DrawText(cpuLabel, cpuX + tagPadX, textBaseline, _tagTextPaint);
                canvas.DrawText(cpuPct, cpuTagRect.Right + gapLabelPct, textBaseline, _textPaint);
                canvas.DrawRoundRect(new SKRect(cpuX, barTop, cpuX + cpuGroupW, barTop + barH),
                    barH / 2f, barH / 2f, _barBgPaint);
                float cpuFillW = cpuGroupW * (_smoothCpuUsage / 100f);
                if (cpuFillW > 0.5f)
                    canvas.DrawRoundRect(new SKRect(cpuX, barTop, cpuX + cpuFillW, barTop + barH),
                        barH / 2f, barH / 2f, _barPaint);

                float ramX = currentX + cpuGroupW + gapCpuRam;
                var ramTagRect = new SKRect(
                    ramX, textBaseline - 10f - tagPadY,
                    ramX + ramTagW, textBaseline + 2.5f + tagPadY);
                canvas.DrawRoundRect(ramTagRect, tagRadius, tagRadius, _tagBgPaint);
                canvas.DrawText(ramLabel, ramX + tagPadX, textBaseline, _tagTextPaint);
                canvas.DrawText(ramPct, ramTagRect.Right + gapLabelPct, textBaseline, _textPaint);
                canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramGroupW, barTop + barH),
                    barH / 2f, barH / 2f, _barBgPaint);
                float ramFillW = ramGroupW * (_smoothRamUsage / 100f);
                if (ramFillW > 0.5f)
                    canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramFillW, barTop + barH),
                        barH / 2f, barH / 2f, _barPaint);

                currentX = ramX + ramGroupW + moduleGap;
                _hardwareZoneL = cpuX;
                _hardwareZoneR = ramX + ramGroupW;
            }

            // 2. 媒体控制器模块（含频谱，媒体激活时才显示）
            void DrawMediaModule()
            {
                float mediaRight = currentX + MeasureMediaBlockWidth(media);
                // 整块交给媒体控制模块（折叠 / 展开由它自己分流）；
                DrawMediaControl(canvas, media, isHovered, bars,
                    new MediaBlockGeometry(currentX, currentX, mediaRight + 18f),
                    currentHeight, textOffsetY, alpha);
                currentX = mediaRight + moduleGap;
            }

            var contentOrder = Plugins.PluginManager.Instance.Host.ContentOrder;
            bool clockHandled = false, hardwareHandled = false, mediaHandled = false;

            if (StandbyActive)
            {
                if (StandbyScene == 1) DrawClockModule();
                else if (StandbyScene == 3 && media.IsActive) DrawMediaModule();
                return;
            }

            for (int oi = 0; oi < contentOrder.Count; oi++)
            {
                string item = contentOrder[oi];
                if (string.Equals(item, Plugins.BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase))
                {
                    clockHandled = true;
                    if (CompShowDateTime) DrawClockModule();
                }
                else if (string.Equals(item, Plugins.BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase))
                {
                    hardwareHandled = true;
                    if (CompShowHardware) DrawHardwareModule();
                }
                else if (string.Equals(item, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase))
                {
                    mediaHandled = true;
                    if (CompShowMedia && media.IsActive) DrawMediaModule();
                }
                else
                {
                    // 插件：整组组件摆在这个位置
                    currentX = DrawPluginWidgets(canvas, item, currentX, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                }
            }

            if (!clockHandled && CompShowDateTime) DrawClockModule();
            if (!hardwareHandled && CompShowHardware) DrawHardwareModule();
            if (!mediaHandled && CompShowMedia && media.IsActive) DrawMediaModule();
            DrawPluginWidgets(canvas, null, currentX, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
        }

        private static void DrawNativeLayout(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            float left, float right, float currentHeight, float textOffsetY, byte alpha)
        {
            // 拆分绘制逻辑
            if (media.IsActive)
            {
                DrawMediaControl(canvas, media, isHovered, bars,
                    new MediaBlockGeometry(left, left + 16f, right),
                    currentHeight, textOffsetY, alpha);
            }
            else if (StandbyDisplayMode == 0)
            {
                _timePaint.Color = _currentTextColor.WithAlpha(alpha);
                _datePaint.Color = _currentSubTextColor.WithAlpha(alpha);
                float baselineY = currentHeight / 2f + 5f + textOffsetY;
                canvas.DrawText(_cachedTimeStr, left + 16f, baselineY, _timePaint);
                canvas.DrawText(_cachedDateStr, right - 16f - _cachedDateWidth, baselineY, _datePaint);
                // 待机时整条原生内容区（时间 + 日期）都算时钟区域
                _clockZoneL = left + 16f;
                _clockZoneR = right - 16f;
            }
            else if (StandbyDisplayMode == 1)
            {
                // 空白待机：原生不绘制任何内容（插件行独立渲染在最右侧）
            }
            else if (StandbyDisplayMode == 2) // 硬件占用检测渲染
            {
                UpdateHardwareStats();

                // 颜色同步
                _tagTextPaint.Color = _currentTextColor.WithAlpha(alpha);
                _tagBgPaint.Color = _currentTextColor.WithAlpha((byte)(alpha * 0.12f));
                _barPaint.Color = _currentTextColor.WithAlpha(alpha);
                _barBgPaint.Color = _currentTextColor.WithAlpha((byte)(alpha * 0.20f));
                _tagTextPaint.Typeface = _boldTypeface; // 防污染

                // 垂直布局
                float contentCenterY = currentHeight / 2f + textOffsetY;
                float textBaseline = contentCenterY - 1f;
                float barTop = contentCenterY + 9f;
                float barH = 3.5f;
                float tagPadX = 3f;
                float tagPadY = 1.5f;
                float tagRadius = 3.5f;
                float gapBetweenLabelAndPct = 4f;   // 标签与百分比间距
                float gapBetweenCpuAndRam = 16f;     // CPU组与RAM组间距

                // 预测量所有文本宽度（零GC，用预缓存字符串）
                string cpuLabel = "CPU";
                string ramLabel = "RAM";
                string cpuPct = _pctStrs![_cpuUsage];
                string ramPct = _pctStrs![_ramUsage];
                float cpuLabelW = _tagTextPaint.MeasureText(cpuLabel);
                float ramLabelW = _tagTextPaint.MeasureText(ramLabel);
                // 固定资源占用文本最大宽度，防止右侧元素排版跟着抖动
                float fixedPctW = _textPaint.MeasureText("90%");
                float cpuPctW = fixedPctW;
                float ramPctW = fixedPctW;
                float cpuTagW = cpuLabelW + tagPadX * 2f;
                float ramTagW = ramLabelW + tagPadX * 2f;
                float cpuGroupW = cpuTagW + gapBetweenLabelAndPct + cpuPctW;
                float ramGroupW = ramTagW + gapBetweenLabelAndPct + ramPctW;
                float totalContentW = cpuGroupW + gapBetweenCpuAndRam + ramGroupW;
                float centerX = (left + right) / 2f;
                float startX = centerX - totalContentW / 2f;
                float cpuBarW = cpuGroupW;
                float ramBarW = ramGroupW;
                _hardwareZoneL = startX;
                _hardwareZoneR = startX + totalContentW;

                // ---- [ CPU ] ----
                float cpuX = startX;
                var cpuTagRect = new SKRect(
                    cpuX,
                    textBaseline - 10f - tagPadY,
                    cpuX + cpuTagW,
                    textBaseline + 2.5f + tagPadY
                );
                canvas.DrawRoundRect(cpuTagRect, tagRadius, tagRadius, _tagBgPaint);
                canvas.DrawText(cpuLabel, cpuX + tagPadX, textBaseline, _tagTextPaint);
                canvas.DrawText(cpuPct, cpuTagRect.Right + gapBetweenLabelAndPct, textBaseline, _textPaint);
                canvas.DrawRoundRect(
                    new SKRect(cpuX, barTop, cpuX + cpuBarW, barTop + barH),
                    barH / 2f, barH / 2f, _barBgPaint);
                float cpuFillW = cpuBarW * (_smoothCpuUsage / 100f);
                if (cpuFillW > 0.5f)
                    canvas.DrawRoundRect(
                        new SKRect(cpuX, barTop, cpuX + cpuFillW, barTop + barH),
                        barH / 2f, barH / 2f, _barPaint);

                // ---- [ RAM ] ----
                float ramX = startX + cpuGroupW + gapBetweenCpuAndRam;
                var ramTagRect = new SKRect(
                    ramX,
                    textBaseline - 10f - tagPadY,
                    ramX + ramTagW,
                    textBaseline + 2.5f + tagPadY
                );
                canvas.DrawRoundRect(ramTagRect, tagRadius, tagRadius, _tagBgPaint);
                canvas.DrawText(ramLabel, ramX + tagPadX, textBaseline, _tagTextPaint);
                canvas.DrawText(ramPct, ramTagRect.Right + gapBetweenLabelAndPct, textBaseline, _textPaint);
                canvas.DrawRoundRect(
                    new SKRect(ramX, barTop, ramX + ramBarW, barTop + barH),
                    barH / 2f, barH / 2f, _barBgPaint);
                float ramFillW = ramBarW * (_smoothRamUsage / 100f);
                if (ramFillW > 0.5f)
                    canvas.DrawRoundRect(
                        new SKRect(ramX, barTop, ramX + ramFillW, barTop + barH),
                        barH / 2f, barH / 2f, _barPaint);
            }
        }

        // 独立于岛体之外，绘制隐形物理热区与极速渐变唤醒按钮。
        private static void DrawWakeButton(SKCanvas canvas, float currentHeight)
        {
            float islandAlpha = Math.Min(PassthroughAlpha, FullHideAlpha);
            byte wakeAlpha = (byte)(Math.Max(0f, 1f - islandAlpha * 2f) * 255);

            float wakeBtnY = (currentHeight - WAKE_BTN_SIZE) / 2f; // 对齐内部垂直居中
            // 水平居中：唤醒按钮落在整个岛体的正中心，不再贴左边缘。
            float wakeBtnX = WakeButtonX;

            _wakeHitPaint.Color = SKColors.Black.WithAlpha(1);
            canvas.DrawRect(wakeBtnX, wakeBtnY, WAKE_BTN_SIZE, WAKE_BTN_SIZE, _wakeHitPaint);

            if (wakeAlpha > 0)
            {
                _wakeChipPaint.Color = SKColors.Black.WithAlpha((byte)(WakeChipAlpha * wakeAlpha / 255));
                DrawSvgPath(canvas, _wakeChipPaint, wakeBtnX, wakeBtnY, _wakeChipPath);

                _wakePaint.Color = SKColors.White.WithAlpha(wakeAlpha);
                DrawSvgPath(canvas, _wakePaint, wakeBtnX, wakeBtnY, _wakePath);
            }
        }

        // 只是按右键落在哪块原生内容上直达对应页签：

        private static float _clockZoneL = -1f, _clockZoneR = -1f;
        private static float _hardwareZoneL = -1f, _hardwareZoneR = -1f;
        private static float _mediaZoneL = -1f, _mediaZoneR = -1f;

        private static void InvalidateNativeHitZones()
        {
            _clockZoneL = _clockZoneR = -1f;
            _hardwareZoneL = _hardwareZoneR = -1f;
            _mediaZoneL = _mediaZoneR = -1f;
            _mediaBlockL = _mediaBlockR = -1f;
            _mediaCoverRect = default;
        }

        private static SKRect _mediaCoverRect;

        private static float _mediaBlockL = -1f, _mediaBlockR = -1f;

        private static void RegisterMediaCover(SKRect rect)
        {
            _mediaCoverRect = rect;
            if (_mediaZoneL < 0f || rect.Left < _mediaZoneL) _mediaZoneL = rect.Left;
        }

        private static void RegisterMediaBlock(float left, float right)
        {
            _mediaBlockL = left;
            _mediaBlockR = right;
        }

        public static int NativeRightClickTab(float x)
        {
            if (InZone(x, _mediaZoneL, _mediaZoneR)) return 2;
            if (InZone(x, _clockZoneL, _clockZoneR) || InZone(x, _hardwareZoneL, _hardwareZoneR)) return 1;
            return -1;
        }

        public static bool HitMediaZone(float x) => InZone(x, _mediaZoneL, _mediaZoneR);

        public static bool HitMediaLaunchZone(float x, float y)
        {
            // 展开态：封面那一块
            if (IsMediaPanelShowing(MediaController.Instance))
            {
                SKRect cover = _mediaCoverRect;
                if (cover.Width <= 0f || cover.Height <= 0f) return false;
                return x >= cover.Left - 5f && x <= cover.Right + 5f
                    && y >= cover.Top && y <= cover.Bottom;
            }

            // 折叠态：整个媒体模块的左半边
            if (_mediaBlockL < 0f || _mediaBlockR <= _mediaBlockL) return false;
            return x >= _mediaBlockL && x <= (_mediaBlockL + _mediaBlockR) / 2f;
        }

        private static bool InZone(float x, float l, float r) => l >= 0f && x >= l && x <= r;

        public static bool HitMediaSpectrumZone(float x)
        {
            if (_mediaBlockL < 0f || _mediaBlockR <= _mediaBlockL) return false;
            return x > (_mediaBlockL + _mediaBlockR) / 2f && x <= _mediaBlockR;
        }

        public static bool IsBlankAt(float x, float y, float currentHeight)
        {
            if (HitPluginZone(x, y)) return false;
            if (HitMediaZone(x)) return false;
            if (HitMediaLaunchZone(x, y)) return false;
            if (HitMediaSpectrumZone(x)) return false;
            if (HitTimeline(x, y)) return false;
            // 不判掉就会把待机胶囊的中央一大片误判成「非空白」——
            if (IsMediaPanelShowing(MediaController.Instance) && HitExpandedButton(x, y, currentHeight) >= 0) return false;
            return true;
        }

    }
}
