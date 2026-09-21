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
            }

            // 2. 媒体控制器模块（含频谱，媒体激活时才显示）
            void DrawMediaModule()
            {
                // 本模块的右边界：不再假设自己一定贴着岛体最右 —— 插件可能被排到它右边
                float mediaRight = currentX + MeasureMediaBlockWidth(media);
                float mediaAnchor = mediaRight + 18f;
                _compositeMediaRight = mediaAnchor;
                int mBtnPrevX = (int)mediaAnchor - 90;
                int mBtnPlayX = (int)mediaAnchor - 60;
                int mBtnNextX = (int)mediaAnchor - 30;

                float textY = (currentHeight - _cachedMediaTextHeight) / 2 - _cachedMediaTextTop + 0.3f;
                float textX = currentX;

                if (media.Thumbnail != null)
                {
                    float thumbSize = 22f; float thumbRadius = 4f; float thumbY = (currentHeight - thumbSize) / 2f;
                    var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);
                    canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, _shadowPaint);
                    canvas.Save();
                    _clipPath.Rewind(); _clipPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                    canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                    canvas.DrawBitmap(media.Thumbnail, thumbRect, _highQualitySampling);
                    canvas.Restore();
                    textX += thumbSize + 10;
                }

                bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
                if (_lyricAnimProgress < 1f && isLyricDisplay)
                {
                    float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);
                    if (!string.IsNullOrEmpty(_prevLyric))
                        DrawLyricLine(canvas, _prevLyric, _prevLyricTrans, textX, textY - (10f * easeOut), _textPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                    DrawLyricLine(canvas, _cachedMediaDisplay, _lastLyricTrans, textX, textY + (10f * (1f - easeOut)), _textPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                    _textPaint.Color = _currentTextColor.WithAlpha(alpha);
                }
                else
                {
                    DrawLyricLine(canvas, _cachedMediaDisplay, _lastLyricTrans, textX, textY, _textPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
                }

                // 组合模式媒体控件：一律以「内容末端 + 10px 边距」为锚点
                float rightOccupiedWidth = isHovered ? 95f : 45f;
                float maskEnd = mediaAnchor - rightOccupiedWidth + 5f;
                float maskStart = maskEnd - 15f;
                canvas.Save(); canvas.Translate(maskStart, 0); canvas.Scale(maskEnd - maskStart, currentHeight); canvas.DrawRect(0, 0, 1, 1, _fadePaint); canvas.Restore();
                // 遮罩只涂到本模块锚点为止：排在媒体右边的插件内容不会被盖掉
                canvas.DrawRect(maskEnd, 0, mediaAnchor, currentHeight, _bgPaint);

                if (isHovered)
                {
                    float prevNextY = (currentHeight - 10f) / 2f; float playPauseY = (currentHeight - 12f) / 2f;
                    DrawSvgPath(canvas, _mediaIconPaint, mBtnPrevX + 11, prevNextY, _prevPath);
                    DrawSvgPath(canvas, _mediaIconPaint, mBtnPlayX + (media.IsPlaying ? 10 : 11), playPauseY, media.IsPlaying ? _pausePath : _playPath);
                    DrawSvgPath(canvas, _mediaIconPaint, mBtnNextX + 11, prevNextY, _nextPath);
                }
                else if (bars != null)
                {
                    float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f;
                    float spectrumX = mediaAnchor - 16f - totalBarWidth;
                    for (int i = 0; i < 5; i++)
                    {
                        float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                        canvas.DrawRoundRect(new SKRect(spectrumX + i * (barWidth + spacing), y, spectrumX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                    }
                }

                currentX = mediaRight + moduleGap;
            }

            // ---- 🧩 按「内容顺序表」混排：原生模块与插件组件共用同一套左右顺序 ----
            // 顺序表由「插件中心」的 ← / → 调整并持久化，默认 = [时钟, 硬件, 媒体, 插件...]，
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
            float left, float right, int btnPrevX, int btnPlayX, int btnNextX, float currentHeight, float textOffsetY, byte alpha)
        {
            // 拆分绘制逻辑
            if (media.IsActive)
            {
                _textPaint.Color = _currentTextColor.WithAlpha(alpha);

                if (IsMediaExpanded && currentHeight > 60f) // 展开模式布局
                {
                    float coverSize = 50f; // 1. 封面缩小 10px
                    float coverX = left + 20f;
                    float coverY = 20f;    // 封面微调光学居中

                    // 封面
                    if (media.Thumbnail != null)
                    {
                        var thumbRect = new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize);
                        canvas.DrawRoundRect(thumbRect, 8f, 8f, _shadowPaint);
                        canvas.Save();
                        _clipPath.Rewind(); _clipPath.AddRoundRect(thumbRect, 8f, 8f);
                        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(media.Thumbnail, thumbRect, _highQualitySampling);
                        canvas.Restore();
                    }
                    else
                    {
                        canvas.DrawRoundRect(new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize), 8f, 8f, _fallbackIconPaint);
                    }

                    // 双行文字
                    float textStartX = coverX + coverSize + 12f;
                    _titlePaint.Color = _currentTextColor.WithAlpha(alpha);
                    _titlePaint.TextSize = 14.5f;
                    DrawKaraoke(canvas, _lastMediaTitle, textStartX, coverY + 18f, _titlePaint, alpha, 0f, false); // 歌名也做 emoji/多语言回退

                    _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
                    _bodyPaint.TextSize = 12.5f;
                    string displaySub = string.IsNullOrEmpty(_lastLyric) ? _lastMediaArtist : _lastLyric;
                    // 译文只在「下方那行确实是歌词」时才跟着画（显示的是歌手名时不能贴译文）
                    string displaySubTrans = string.IsNullOrEmpty(_lastLyric) ? "" : _lastLyricTrans;

                    // 展开模式下的平滑叠化渲染 (带卡拉OK)
                    bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
                    if (_lyricAnimProgress < 1f && isLyricDisplay)
                    {
                        float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);
                        if (!string.IsNullOrEmpty(_prevLyric))
                        {
                            // 旧歌词淡出时进度直接锁定 100% (1f)
                            DrawLyricLine(canvas, _prevLyric, _prevLyricTrans, textStartX, coverY + 42f - (8f * easeOut), _bodyPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                        }
                        // 新歌词套用当前进度
                        DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, coverY + 42f + (8f * (1f - easeOut)), _bodyPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                        _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
                    }
                    else
                    {
                        DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, coverY + 42f, _bodyPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
                    }

                    // 2. 新增遮罩隔断：在渲染右侧律动频谱前，直接截断文字区域 (零内存分配)
                    float maskEnd = right - 55f;
                    float maskStart = maskEnd - 20f;
                    canvas.Save();
                    canvas.Translate(maskStart, 0);
                    canvas.Scale(maskEnd - maskStart, currentHeight);
                    canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                    canvas.Restore();
                    canvas.DrawRect(maskEnd, 0, WINDOW_WIDTH, currentHeight, _bgPaint);

                    // 复用律动频谱 (放右上角)
                    if (bars != null)
                    {
                        float barWidth = 2.5f, spacing = 3.5f, maxH = 18f, totalBarWidth = 26.5f;
                        float startX = right - 20f - totalBarWidth;
                        for (int i = 0; i < 5; i++)
                        {
                            float h = Math.Max(2f, bars[i] * maxH);
                            float barY = coverY + 22f + (maxH - h) / 2f;
                            canvas.DrawRoundRect(new SKRect(startX + i * (barWidth + spacing), barY, startX + i * (barWidth + spacing) + barWidth, barY + h), 1.5f, 1.5f, _barPaint);
                        }
                    }

                    // 3. 底部放大媒体控件
                    float btnY = currentHeight - 34f;
                    // 居中于「原生内容区」而非整岛：插件行被排到左边时内容区整体右移，按钮要跟着走
                    float centerX = (left + right) / 2f;
                    float scale = 1.6f;
                    float playBtnY = btnY - 1.6f;

                    if (HoveredExpandedButton == 0) canvas.DrawCircle(centerX - 54f, btnY + 8f, 20f, _hoverCirclePaint);
                    if (HoveredExpandedButton == 1) canvas.DrawCircle(centerX + 1f, playBtnY + 9.6f, 20f, _hoverCirclePaint);
                    if (HoveredExpandedButton == 2) canvas.DrawCircle(centerX + 52f, btnY + 8f, 20f, _hoverCirclePaint);

                    DrawSvgPath(canvas, _mediaIconPaint, centerX - 60f, btnY, _prevPath, scale);
                    DrawSvgPath(canvas, _mediaIconPaint, centerX - 7f, playBtnY, media.IsPlaying ? _pausePath : _playPath, scale);
                    DrawSvgPath(canvas, _mediaIconPaint, centerX + 45f, btnY, _nextPath, scale);

                    // 🎵 歌曲时间轴：必须画在文字遮罩之后（否则右半边被整块盖掉）。
                    //    高度没涨到 140 之前不画，避免展开动画途中与底部按钮叠字。
                    if (TimelineVisible(media) && currentHeight > TL_MIN_HEIGHT)
                        DrawTimeline(canvas, media, left, right, currentHeight, alpha);
                }
                else // 原版折叠模式布局
                {
                    float textY = (currentHeight - _cachedMediaTextHeight) / 2 - _cachedMediaTextTop + 0.3f + textOffsetY;
                    float textX = left + 16;
                    if (media.Thumbnail != null)
                    {
                        float thumbSize = 22f; float thumbRadius = 4f; float thumbY = (currentHeight - thumbSize) / 2f;
                        var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);
                        canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, _shadowPaint);
                        canvas.Save();
                        _clipPath.Rewind(); _clipPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(media.Thumbnail, thumbRect, _highQualitySampling);
                        canvas.Restore();
                        textX += thumbSize + 10;
                    }

                    // 折叠模式下的歌词叠化与位移动画 (带卡拉OK)
                    bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
                    if (_lyricAnimProgress < 1f && isLyricDisplay)
                    {
                        float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);

                        if (!string.IsNullOrEmpty(_prevLyric))
                        {
                            DrawLyricLine(canvas, _prevLyric, _prevLyricTrans, textX, textY - (10f * easeOut), _textPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                        }

                        DrawLyricLine(canvas, _cachedMediaDisplay, _lastLyricTrans, textX, textY + (10f * (1f - easeOut)), _textPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                        _textPaint.Color = _currentTextColor.WithAlpha(alpha);
                    }
                    else
                    {
                        DrawLyricLine(canvas, _cachedMediaDisplay, _lastLyricTrans, textX, textY, _textPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
                    }

                    if (MediaInteractionMode == 0) // 直接交互模式
                    {
                        float rightOccupiedWidth = isHovered ? 95f : 45f;
                        float maskEnd = right - rightOccupiedWidth + 5f;
                        float maskStart = maskEnd - 15f;
                        canvas.Save(); canvas.Translate(maskStart, 0); canvas.Scale(maskEnd - maskStart, currentHeight); canvas.DrawRect(0, 0, 1, 1, _fadePaint); canvas.Restore();
                        canvas.DrawRect(maskEnd, 0, WINDOW_WIDTH, currentHeight, _bgPaint);

                        if (isHovered)
                        {
                            float prevNextY = (currentHeight - 10f) / 2f; float playPauseY = (currentHeight - 12f) / 2f;
                            DrawSvgPath(canvas, _mediaIconPaint, btnPrevX + 11, prevNextY, _prevPath);
                            DrawSvgPath(canvas, _mediaIconPaint, btnPlayX + (media.IsPlaying ? 10 : 11), playPauseY, media.IsPlaying ? _pausePath : _playPath);
                            DrawSvgPath(canvas, _mediaIconPaint, btnNextX + 11, prevNextY, _nextPath);
                        }
                        else if (bars != null)
                        {
                            float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f, startX = right - 16f - totalBarWidth;
                            for (int i = 0; i < 5; i++)
                            {
                                float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                                canvas.DrawRoundRect(new SKRect(startX + i * (barWidth + spacing), y, startX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                            }
                        }
                    }
                    else // 展开交互模式
                    {
                        float rightOccupiedWidth = bars != null ? 45f : 15f;
                        float maskEnd = right - rightOccupiedWidth + 5f;
                        float maskStart = maskEnd - 15f;

                        canvas.Save(); canvas.Translate(maskStart, 0); canvas.Scale(maskEnd - maskStart, currentHeight); canvas.DrawRect(0, 0, 1, 1, _fadePaint); canvas.Restore();
                        canvas.DrawRect(maskEnd, 0, WINDOW_WIDTH, currentHeight, _bgPaint);

                        if (bars != null)
                        {
                            float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f, startX = right - 16f - totalBarWidth;
                            for (int i = 0; i < 5; i++)
                            {
                                float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                                canvas.DrawRoundRect(new SKRect(startX + i * (barWidth + spacing), y, startX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                            }
                        }
                    }
                }
            }
            else if (StandbyDisplayMode == 0)
            {
                _timePaint.Color = _currentTextColor.WithAlpha(alpha);
                _datePaint.Color = _currentSubTextColor.WithAlpha(alpha);
                float baselineY = currentHeight / 2f + 5f + textOffsetY;
                canvas.DrawText(_cachedTimeStr, left + 16f, baselineY, _timePaint);
                canvas.DrawText(_cachedDateStr, right - 16f - _cachedDateWidth, baselineY, _datePaint);
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
            byte wakeAlpha = (byte)(Math.Max(0f, 1f - PassthroughAlpha * 2f) * 255);

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

    }
}
