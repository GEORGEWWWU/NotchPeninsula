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
            float left, float currentWidth, float currentHeight, float topY, float textOffsetY, byte alpha)
        {
            float currentX = left + 16f;
            float centerY = currentHeight / 2f + textOffsetY;
            const float moduleGap = 16f;

            void DrawClockModule()
            {
                _timePaint.Color = _currentTextColor.WithAlpha(alpha);
                _datePaint.Color = _currentSubTextColor.WithAlpha(alpha);
                float timeBaselineY = centerY + 5f;
                DrawToneText(canvas, _cachedTimeStr, currentX, timeBaselineY, _timePaint, alpha);
                float dateX = currentX + _cachedTimeWidth + 12f;
                DrawToneText(canvas, _cachedDateStr, dateX, timeBaselineY, _datePaint, alpha, sub: true);
                _hitDraft.ClockL = currentX;
                _hitDraft.ClockR = dateX + _cachedDateWidth;
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
                DrawToneText(canvas, cpuLabel, cpuX + tagPadX, textBaseline, _tagTextPaint, alpha);
                DrawToneText(canvas, cpuPct, cpuTagRect.Right + gapLabelPct, textBaseline, _textPaint, alpha);
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
                DrawToneText(canvas, ramLabel, ramX + tagPadX, textBaseline, _tagTextPaint, alpha);
                DrawToneText(canvas, ramPct, ramTagRect.Right + gapLabelPct, textBaseline, _textPaint, alpha);
                canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramGroupW, barTop + barH),
                    barH / 2f, barH / 2f, _barBgPaint);
                float ramFillW = ramGroupW * (_smoothRamUsage / 100f);
                if (ramFillW > 0.5f)
                    canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramFillW, barTop + barH),
                        barH / 2f, barH / 2f, _barPaint);

                currentX = ramX + ramGroupW + moduleGap;
                _hitDraft.HardwareL = cpuX;
                _hitDraft.HardwareR = ramX + ramGroupW;
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
                // 待机只显示「待机显示内容」下拉里选中的那一项；
                // 空串 = 空白，内置项走各自的原生模块，其余当作插件 Key 交给插件行。
                string scene = StandbySceneKey;

                // 岛宽有 60px 下限：窄内容（比如 22px 的插件图标）若只靠左右各 16px 边距，
                // 多出来的宽度会全堆在右侧、看起来整体偏左。这里按「岛中心 - 内容宽/2」摆，
                // 宽度没被下限撑开时（currentWidth == own + 32）结果与 left + 16 完全一致。
                float CenteredContentX(float ownW)
                    => left + Math.Max(16f, (currentWidth - ownW) / 2f);

                if (string.Equals(scene, Plugins.BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase))
                {
                    currentX = CenteredContentX(_cachedTimeWidth + 12f + _cachedDateWidth);
                    DrawClockModule();
                }
                else if (string.Equals(scene, Plugins.BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase))
                {
                    currentX = CenteredContentX(MeasureHardwareBlockWidth());
                    DrawHardwareModule();
                }
                else if (string.Equals(scene, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase))
                {
                    if (media.IsActive && !SuppressMediaInIsland)
                    {
                        currentX = CenteredContentX(MeasureMediaBlockWidth(media));
                        DrawMediaModule();
                    }
                }
                else if (!string.IsNullOrEmpty(scene))
                {
                    // 设置页存的是插件 Key（DLL 文件名），组件反查表用的是插件 Id，这里先换一次。
                    string? pluginId = ResolveStandbyPluginId();
                    if (pluginId != null)
                    {
                        currentX = CenteredContentX(SumPluginRowWidth(pluginId));
                        DrawPluginWidgets(canvas, pluginId, currentX, currentHeight, alpha, textOffsetY, bars,
                            _pluginMouseX, _pluginMouseY - topY);
                    }
                }
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
                    if (CompShowMedia && media.IsActive && !SuppressMediaInIsland) DrawMediaModule();
                }
                else
                {
                    // 插件：整组组件摆在这个位置
                    currentX = DrawPluginWidgets(canvas, item, currentX, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                }
            }

            if (!clockHandled && CompShowDateTime) DrawClockModule();
            if (!hardwareHandled && CompShowHardware) DrawHardwareModule();
            if (!mediaHandled && CompShowMedia && media.IsActive && !SuppressMediaInIsland) DrawMediaModule();
            DrawPluginWidgets(canvas, null, currentX, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
        }

        private static void DrawNativeLayout(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            float left, float right, float currentHeight, float textOffsetY, byte alpha)
        {
            // 拆分绘制逻辑
            if (media.IsActive && !SuppressMediaInIsland)
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
                DrawToneText(canvas, _cachedTimeStr, left + 16f, baselineY, _timePaint, alpha);
                DrawToneText(canvas, _cachedDateStr, right - 16f - _cachedDateWidth, baselineY, _datePaint, alpha, sub: true);
                // 待机时整条原生内容区（时间 + 日期）都算时钟区域
                _hitDraft.ClockL = left + 16f;
                _hitDraft.ClockR = right - 16f;
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
                _hitDraft.HardwareL = startX;
                _hitDraft.HardwareR = startX + totalContentW;

                // ---- [ CPU ] ----
                float cpuX = startX;
                var cpuTagRect = new SKRect(
                    cpuX,
                    textBaseline - 10f - tagPadY,
                    cpuX + cpuTagW,
                    textBaseline + 2.5f + tagPadY
                );
                canvas.DrawRoundRect(cpuTagRect, tagRadius, tagRadius, _tagBgPaint);
                DrawToneText(canvas, cpuLabel, cpuX + tagPadX, textBaseline, _tagTextPaint, alpha);
                DrawToneText(canvas, cpuPct, cpuTagRect.Right + gapBetweenLabelAndPct, textBaseline, _textPaint, alpha);
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
                DrawToneText(canvas, ramLabel, ramX + tagPadX, textBaseline, _tagTextPaint, alpha);
                DrawToneText(canvas, ramPct, ramTagRect.Right + gapBetweenLabelAndPct, textBaseline, _textPaint, alpha);
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

        /// <summary>
        /// 岛内原生内容的命中几何（时钟 / 硬件 / 媒体区 + 遮罩 + 时间轴）。
        ///
        /// 关键约定：**渲染线程写 <see cref="_hitDraft"/>，画完一帧才整体发布到 <see cref="_hitPublished"/>；
        /// 输入线程（点击、指针、拖放、岛外判定）只读 <see cref="HitZones"/>。**
        /// 早先是同一批字段被两边共用，帧首 InvalidateNativeHitZones() 会先把它们清成 -1，
        /// 再在绘制过程中逐个填回来 —— 绘制期间（144fps 下每帧都有几毫秒）落进来的点击和
        /// WM_MOUSEMOVE 读到的是「所有热区都不存在」，表现就是**点岛体偶尔点不上**、
        /// **指针在手指和箭头之间来回闪**。发布制之后输入侧永远拿到上一条完整帧的几何。
        /// </summary>
        internal struct NativeHitZones
        {
            internal float ClockL, ClockR, HardwareL, HardwareR;
            internal float MediaL, MediaR;
            internal float MediaBlockL, MediaBlockR;
            internal SKRect MediaCover;
            internal float TlX1, TlX2, TlY;

            internal static NativeHitZones None => new NativeHitZones
            {
                ClockL = -1f, ClockR = -1f, HardwareL = -1f, HardwareR = -1f,
                MediaL = -1f, MediaR = -1f, MediaBlockL = -1f, MediaBlockR = -1f,
                MediaCover = default, TlX1 = 0f, TlX2 = 0f, TlY = 0f
            };
        }

        // 渲染线程独占的草稿；发布用的快照只在上一条完整帧之后才更新。
        private static NativeHitZones _hitDraft = NativeHitZones.None;
        private static NativeHitZones _hitPublished = NativeHitZones.None;
        private static readonly object _hitZoneLock = new object();

        /// <summary>输入侧读到的命中几何：上一条完整帧。永远不会是「本帧还没画到」的半成品。</summary>
        internal static NativeHitZones HitZones
        {
            get { lock (_hitZoneLock) return _hitPublished; }
        }

        /// <summary>渲染线程：本帧绘制结束（含提前返回与异常路径）时整体发布。</summary>
        internal static void PublishHitZones()
        {
            lock (_hitZoneLock) _hitPublished = _hitDraft;
            // 插件行热区同样是「帧末交换」：输入侧读到的永远是上一条完整帧的那份。
            lock (_pluginSlotLock)
            {
                var published = _pluginSlots;
                _pluginSlots = _pluginSlotsDraft;
                _pluginSlotsDraft = published;
            }
        }

        private static void InvalidateNativeHitZones() => _hitDraft = NativeHitZones.None;

        private static void RegisterMediaCover(SKRect rect)
        {
            _hitDraft.MediaCover = rect;
            if (_hitDraft.MediaL < 0f || rect.Left < _hitDraft.MediaL) _hitDraft.MediaL = rect.Left;
        }

        private static void RegisterMediaBlock(float left, float right)
        {
            _hitDraft.MediaBlockL = left;
            _hitDraft.MediaBlockR = right;
        }

        public static int NativeRightClickTab(float x)
        {
            var z = HitZones;
            if (InZone(x, z.MediaL, z.MediaR)) return 2;
            if (InZone(x, z.ClockL, z.ClockR) || InZone(x, z.HardwareL, z.HardwareR)) return 1;
            return -1;
        }

        public static bool HitMediaZone(float x)
        {
            var z = HitZones;
            return InZone(x, z.MediaL, z.MediaR);
        }

        public static bool HitMediaLaunchZone(float x, float y) => HitMediaLaunchZone(HitZones, x, y);

        private static bool HitMediaLaunchZone(NativeHitZones z, float x, float y)
        {
            // 展开态：封面那一块
            if (IsMediaPanelShowing(MediaController.Instance))
            {
                SKRect cover = z.MediaCover;
                if (cover.Width <= 0f || cover.Height <= 0f) return false;
                return x >= cover.Left - 5f && x <= cover.Right + 5f
                    && y >= cover.Top && y <= cover.Bottom;
            }

            // 折叠态：整个媒体模块的左半边
            float blockL = z.MediaBlockL, blockR = z.MediaBlockR;
            if (blockL < 0f || blockR <= blockL) return false;
            return x >= blockL && x <= (blockL + blockR) / 2f;
        }

        private static bool InZone(float x, float l, float r) => l >= 0f && x >= l && x <= r;

        public static bool HitMediaSpectrumZone(float x) => HitMediaSpectrumZone(HitZones, x);

        private static bool HitMediaSpectrumZone(NativeHitZones z, float x)
        {
            float blockL = z.MediaBlockL, blockR = z.MediaBlockR;
            if (blockL < 0f || blockR <= blockL) return false;
            return x > (blockL + blockR) / 2f && x <= blockR;
        }

        public static bool IsBlankAt(float x, float y, float currentHeight)
        {
            // 一次取快照给下面所有判定共用：中途若发生发布，也不会出现「这一条用上帧、
            // 下一条用新帧」的混合结果（点击判定里那会表现成「明明点在媒体区却说空白」）。
            var z = HitZones;
            if (HitPluginZone(x, y)) return false;
            if (InZone(x, z.MediaL, z.MediaR)) return false;
            if (HitMediaLaunchZone(z, x, y)) return false;
            if (HitMediaSpectrumZone(z, x)) return false;
            if (HitTimeline(z, x, y)) return false;
            // 不判掉就会把待机胶囊的中央一大片误判成「非空白」——
            if (IsMediaPanelShowing(MediaController.Instance) && HitExpandedButton(x, y, currentHeight) >= 0) return false;
            return true;
        }

    }
}
