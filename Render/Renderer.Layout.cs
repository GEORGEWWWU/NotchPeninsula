using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {

        // ================= 岛体内容布局：两条互斥分支 =================
        // 组合模式：原生模块与插件组件按「内容顺序表」混排（各模块是下面的局部函数）。
        // 非组合模式：原生内容居中，插件行按顺序表贴在它的左右两侧。
        //
        // 参数都是 Draw() 里算好的几何量，直接透传，不要在这里重算 ——
        // 左右边界 / 按钮位置 / 插件预留都只有 Draw() 一个真源。
        //
        // 🎵 媒体控制不在这里实现：它整块搬到了 Renderer.MediaWidget.cs，由统一入口
        //    DrawMediaControl 自行分流「折叠内联行 / 展开面板」—— 本文件只负责把几何量喂给它，
        //    组合与非组合因此不再各写一套媒体绘制。

        private static void DrawCompositeLayout(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            float left, float currentHeight, float topY, float textOffsetY, byte alpha)
        {
            float currentX = left + 16f;
            float centerY = currentHeight / 2f + textOffsetY;
            const float moduleGap = 16f;

            // ---- 原生模块绘制（局部函数，由下面的「内容顺序表」按位置调用）----
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
                // 本模块的右边界：不再假设自己一定贴着岛体最右 —— 插件可能被排到它右边
                float mediaRight = currentX + MeasureMediaBlockWidth(media);
                // 整块交给媒体控制模块（折叠 / 展开由它自己分流）；
                // 锚点比模块右缘多 18px，是给频谱 / 播放按钮让出的固定位置。
                DrawMediaControl(canvas, media, isHovered, bars,
                    new MediaBlockGeometry(currentX, currentX, mediaRight + 18f),
                    currentHeight, textOffsetY, alpha);
                currentX = mediaRight + moduleGap;
            }

            // ---- 🧩 按「内容顺序表」混排：原生模块与插件组件共用同一套左右顺序 ----
            // 顺序表由「显示设置 → 显示内容」调整并持久化，默认 = [时钟, 硬件, 媒体, 插件...]，
            // 与引入顺序表之前的表现完全一致；插件之间的先后也在同一张表里独立调整。
            var contentOrder = Plugins.PluginManager.Instance.Host.ContentOrder;
            bool clockHandled = false, hardwareHandled = false, mediaHandled = false;

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

            // 兜底：顺序表里尚未登记的内容按默认次序补在末尾（例如刚装入、还没进表的插件）
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
                // 🎵 非组合模式下媒体控制器独占「原生内容区」：内容区左右边界就是它的几何量。
                //    内容起点比左边界多 16px（岛体内边距），锚点就是右边界 —— 与组合模式喂进去的
                //    只是几何量不同，绘制走的是同一个模块，所以「组合 / 非组合」不会再有两套媒体逻辑。
                //    展开面板由模块自己判定接管（IsMediaPanelShowing），此处无需分支。
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
                // 🖱️ 待机时整条原生内容区（时间 + 日期）都算时钟区域
                _clockZoneL = left + 16f;
                _clockZoneR = right - 16f;
                // 注：插件组件行不参与本段原生布局，统一在下面「插件组件行」处渲染在岛体最右侧
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
                // 居中以「原生内容区」为准（扣除两侧插件组），插件行不参与居中计算
                float centerX = (left + right) / 2f;
                float startX = centerX - totalContentW / 2f;
                float cpuBarW = cpuGroupW;
                float ramBarW = ramGroupW;
                // 🖱️ 硬件占用模块的右键命中区 = CPU 标签到 RAM 进度条右端
                _hardwareZoneL = startX;
                _hardwareZoneR = startX + totalContentW;

                // ================= [ CPU ] =================
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

                // ================= [ RAM ] =================
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
        // 核心逻辑：2 倍速急速消失 —— 只要本体浮现到一半（Alpha>0.5），按钮立刻彻底消失，绝不拖泥带水。
        private static void DrawWakeButton(SKCanvas canvas, float currentHeight)
        {
            // 核心逻辑：2倍速急速消失。只要本体浮现到一半（Alpha>0.5），按钮立刻彻底消失，绝不拖泥带水
            // 取「哪条通道把岛体压得更暗」那一条：穿透睡眠走 PassthroughAlpha，完全隐藏走 FullHideAlpha
            float islandAlpha = Math.Min(PassthroughAlpha, FullHideAlpha);
            byte wakeAlpha = (byte)(Math.Max(0f, 1f - islandAlpha * 2f) * 255);

            float wakeBtnY = (currentHeight - WAKE_BTN_SIZE) / 2f; // 对齐内部垂直居中
            // 水平居中：唤醒按钮落在整个岛体的正中心，不再贴左边缘。
            // X 走 Renderer.WakeButtonX（唯一真源），NotchWindow 的命中判定与手型指针共用它。
            float wakeBtnX = WakeButtonX;

            // 垫底一块 Alpha=1 的隐形纯黑热区！肉眼完全不可见，但足以 100% 截断 Windows 物理穿透事件
            _wakeHitPaint.Color = SKColors.Black.WithAlpha(1);
            canvas.DrawRect(wakeBtnX, wakeBtnY, WAKE_BTN_SIZE, WAKE_BTN_SIZE, _wakeHitPaint);

            if (wakeAlpha > 0)
            {
                // 芯片先铺底、白色箭头压在上面。芯片不透明度跟随同一个 wakeAlpha 等比缩放，
                // 保证它与本体淡出节奏完全同步，不会出现「岛已透明、芯片还实心」的割裂感。
                _wakeChipPaint.Color = SKColors.Black.WithAlpha((byte)(WakeChipAlpha * wakeAlpha / 255));
                DrawSvgPath(canvas, _wakeChipPaint, wakeBtnX, wakeBtnY, _wakeChipPath);

                _wakePaint.Color = SKColors.White.WithAlpha(wakeAlpha);
                DrawSvgPath(canvas, _wakePaint, wakeBtnX, wakeBtnY, _wakePath);
            }
        }

        // ================= 🖱️ 岛内右键「按区域直达设置页签」命中区 =================
        // 规则（用户 2026-09-19 定下、2026-09-23 细分）：原生媒体控制器区域的右键一律不消费、
        // 依然只打开设置窗口，只是**按右键落在哪块原生内容上直达对应页签**：
        //   · 媒体控制器（标题 / 歌词 / 频谱 / 播放按钮 / 空白）→ 媒体设置
        //   · 时间 / 日期、CPU / RAM                            → 显示设置
        //   · 其他（空白待机、插件行、插件详情页等）            → 设置窗口的当前页签，保持原行为
        //
        // 命中区与插件命中区同一套思路：**本帧绘制时登记，帧首作废**（见 InvalidateNativeHitZones）。
        // 于是通知 / 剪贴板面板 / 插件详情页接管岛体时，这几块区域自动不存在，右键不会误命中。
        // ⚠️ 只登记「本模块真正画出来的 x 区间」，不登记覆盖整岛的隐形大热区 ——
        //    2026-09-19 的回归就是这么来的（大热区把设置窗口的入口整片吃掉）。

        private static float _clockZoneL = -1f, _clockZoneR = -1f;
        private static float _hardwareZoneL = -1f, _hardwareZoneR = -1f;
        private static float _mediaZoneL = -1f, _mediaZoneR = -1f;

        /// <summary>帧首作废三块原生模块的右键命中区；本帧没画就等于命中区不存在。</summary>
        private static void InvalidateNativeHitZones()
        {
            _clockZoneL = _clockZoneR = -1f;
            _hardwareZoneL = _hardwareZoneR = -1f;
            _mediaZoneL = _mediaZoneR = -1f;
        }

        /// <summary>
        /// 岛内右键落在哪块原生内容上，返回设置窗口应直达的页签下标；未命中任何原生模块返回 -1。
        /// 页签下标与 <see cref="ConsoleWindow"/> 的侧边栏一致：1 = 显示设置、2 = 媒体设置。
        /// 只按 x 判定 —— 三块区域在岛内是互不重叠的横向切片，y 由调用方（岛体悬停）保证。
        /// </summary>
        public static int NativeRightClickTab(float x)
        {
            if (InZone(x, _mediaZoneL, _mediaZoneR)) return 2;
            if (InZone(x, _clockZoneL, _clockZoneR) || InZone(x, _hardwareZoneL, _hardwareZoneR)) return 1;
            return -1;
        }

        /// <summary>
        /// 岛内逻辑坐标 x 是否落在本帧绘制的媒体模块上（折叠内联行或展开面板都算）。
        ///
        /// 宿主用它判定「点击展开媒体面板」与「悬停给小手」：组合模式下媒体只是岛体里的一段
        /// （左右还挨着时钟 / 硬件 / 插件），所以不能用「整岛命中」——那会让点时钟也把媒体展开。
        /// 区间由绘制时登记、帧首作废，与本帧实际画出来的东西严格一致。
        /// </summary>
        public static bool HitMediaZone(float x) => InZone(x, _mediaZoneL, _mediaZoneR);

        private static bool InZone(float x, float l, float r) => l >= 0f && x >= l && x <= r;

    }
}
