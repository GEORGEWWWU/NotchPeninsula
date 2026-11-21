using SkiaSharp;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ---- 媒体控制模块（唯一入口） ----

        // 文字区右界都改用【真裁切】（见下），不再需要「渐隐幕布 + 纯色片」那套常量
        private const float BUTTON_BLOCK_LEFT = 79f;    // 组件右端 − 79 = 第一个图标左缘

        // 展开态封面在媒体面板里的几何（相对面板左缘）。岛内展开面板与实况窗详情页共用：
        // 双击封面跳转既要画得对，也要点得中，不能再各写一套 20 / 50。
        internal const float MediaPanelCoverLeft = 20f;
        internal const float MediaPanelCoverTop = 20f;
        internal const float MediaPanelCoverSize = 50f;

        internal static SKRect MediaPanelCoverRect(float panelLeft)
            => new(panelLeft + MediaPanelCoverLeft, MediaPanelCoverTop,
                panelLeft + MediaPanelCoverLeft + MediaPanelCoverSize, MediaPanelCoverTop + MediaPanelCoverSize);

        /// <summary>
        /// 实况窗展开面板里的封面双击命中（面板局部坐标）。
        /// 横向沿用主岛 <see cref="HitMediaLaunchZone"/> 的 ±5px 容差，别让用户觉得「明明点到封面却没反应」。
        /// </summary>
        internal static bool HitMediaPanelCover(float x, float y)
        {
            var r = MediaPanelCoverRect(0f);
            return x >= r.Left - 5f && x <= r.Right + 5f && y >= r.Top && y <= r.Bottom;
        }

        // 播放三角的光学补偿（逻辑像素，scale=1.6 下量出来的）：三角面积重心在包围盒左侧
        // 1/6 宽处，几何居中看着偏左，右移这一档才「看着」居中。暂停的两根竖条对称，不需要。
        private const float PlayOpticalDx = 2f;

        // ---- 展开态标题超长自动滚动 ----
        // 岛内展开面板和实况窗详情页共用这一段绘制：两处共用同一条时间轴，
        // 不会出现「主岛滚到一半、浮层从头发车」。速度取偏慢的 34px/s，长标题读起来才不晕。
        private const float TITLE_MARQUEE_SPEED = 34f;
        // 滚完一整条标题后空出来的间隔，按「几个空格」的宽度算：换字体时比例也不会突然变窄。
        private const string TITLE_MARQUEE_GAP = "     ";

        private static string _titleMarqueeText = "";
        private static DateTime _titleMarqueeTick;
        private static float _titleMarqueeOffset;

        private static string _titleMeasureText = "";
        private static SKTypeface? _titleMeasureFace;
        private static float _titleMeasureWidth;

        private static float MeasureMediaTitleWidth(string text)
        {
            var face = _titlePaint.Typeface;
            if (_titleMeasureText == text && ReferenceEquals(_titleMeasureFace, face)) return _titleMeasureWidth;
            BuildTextRuns(text, _titlePaint, face, _measureRuns, out _titleMeasureWidth);
            _titleMeasureText = text;
            _titleMeasureFace = face;
            return _titleMeasureWidth;
        }

        /// <summary>标题超出可视宽度后的横向偏移（逻辑像素）；放得下就是 0。</summary>
        private static float MediaTitleScrollOffset(string title, float titleWidth, float viewWidth)
        {
            if (string.IsNullOrEmpty(title) || titleWidth <= viewWidth)
            {
                _titleMarqueeText = title;
                _titleMarqueeOffset = 0f;
                return 0f;
            }

            var now = DateTime.Now;
            if (_titleMarqueeText != title)
            {
                // 换曲 / 换标题从 0 开始，别接着上一首的位置滚。
                _titleMarqueeText = title;
                _titleMarqueeOffset = 0f;
                _titleMarqueeTick = now;
                return 0f;
            }

            double dt = (now - _titleMarqueeTick).TotalSeconds;
            _titleMarqueeTick = now;
            if (dt > 0d) _titleMarqueeOffset += (float)(dt * TITLE_MARQUEE_SPEED);

            float gap = Math.Max(_titlePaint.MeasureText(TITLE_MARQUEE_GAP), 16f);
            float period = titleWidth + gap;
            if (period <= 0f) return 0f;
            _titleMarqueeOffset %= period;
            if (_titleMarqueeOffset < 0f) _titleMarqueeOffset = 0f;
            return _titleMarqueeOffset;
        }

        private readonly record struct MediaBlockGeometry(float ZoneLeft, float ContentLeft, float AnchorRight);

        private static bool IsMediaPanelShowing(MediaController media, float currentHeight)
            => media.IsActive && IsMediaExpanded && currentHeight > 60f;

        private static void DrawMediaControl(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, float textOffsetY, byte alpha)
        {
            if (IsMediaPanelShowing(media, currentHeight))
                DrawMediaPanel(canvas, media, bars, geometry, currentHeight, alpha);
            else
                DrawMediaInline(canvas, media, isHovered, bars, geometry, currentHeight, textOffsetY, alpha);
        }

        // ---- 折叠态内联行 ----

        private static void DrawMediaInline(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, float textOffsetY, byte alpha)
        {
            _compositeMediaRight = geometry.AnchorRight;
            // 本模块的右键命中区 = 文字 + 频谱 / 按钮锚点
            _hitDraft.MediaL = geometry.ZoneLeft;
            _hitDraft.MediaR = geometry.AnchorRight;
            RegisterMediaBlock(geometry.ZoneLeft, geometry.AnchorRight);

            _textPaint.Color = _currentTextColor.WithAlpha(alpha);

            float textY = (currentHeight - _cachedMediaTextHeight) / 2 - _cachedMediaTextTop + 0.3f + textOffsetY;
            float textX = geometry.ContentLeft;

            // 双击跳转的命中区：不管有没有封面位图都要登记。
            float thumbSize = 22f; float thumbRadius = 4f; float thumbY = (currentHeight - thumbSize) / 2f;
            RegisterMediaCover(new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize));

            //    RenderLoop，直接把进程带走。
            var thumb = media.Thumbnail;
            if (thumb != null)
            {
                var thumbRect = new SKRect(textX, thumbY, textX + thumbSize, thumbY + thumbSize);
                canvas.DrawRoundRect(thumbRect, thumbRadius, thumbRadius, _shadowPaint);
                canvas.Save();
                _clipPath.Rewind(); _clipPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(thumb, thumbRect, _highQualitySampling);
                canvas.Restore();
                textX += thumbSize + 10;
            }

            // 文字右界：悬停出按钮时让到按钮块左侧、常态让到频谱左侧。
            // ⚠️ 用【真裁切】而不是「盖一层渐变 + 纯色」—— 那层在亚克力 / 低透明度下会变成一块突兀的亮片。
            float textClipRight = (isHovered && MediaInteractionMode == 0)
                ? geometry.AnchorRight - BUTTON_BLOCK_LEFT - 4f
                : geometry.AnchorRight - 41f;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, textClipRight, currentHeight), SKClipOperation.Intersect, true);

            // 折叠态下的歌词叠化与位移动画 (带卡拉OK)
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

            canvas.Restore();   // 结束文字裁切

            if (isHovered && MediaInteractionMode == 0)
            {
                int btnPrevX = (int)geometry.AnchorRight - 90;
                int btnPlayX = (int)geometry.AnchorRight - 60;
                int btnNextX = (int)geometry.AnchorRight - 30;

                float prevNextY = (currentHeight - 10f) / 2f; float playPauseY = (currentHeight - 12f) / 2f;
                _mediaIconPaint.Color = ToneColor(new SKRect(btnPrevX, 0, btnPrevX + 22, currentHeight), alpha);
                DrawSvgPath(canvas, _mediaIconPaint, btnPrevX + 11, prevNextY, _prevPath);
                _mediaIconPaint.Color = ToneColor(new SKRect(btnPlayX, 0, btnPlayX + 22, currentHeight), alpha);
                DrawSvgPath(canvas, _mediaIconPaint, btnPlayX + (media.IsPlaying ? 10 : 11), playPauseY, media.IsPlaying ? _pausePath : _playPath);
                _mediaIconPaint.Color = ToneColor(new SKRect(btnNextX, 0, btnNextX + 22, currentHeight), alpha);
                DrawSvgPath(canvas, _mediaIconPaint, btnNextX + 11, prevNextY, _nextPath);
                // 复原：下面别处还会用全局图标色
                _mediaIconPaint.Color = _currentTextColor.WithAlpha(alpha);
            }
            else if (bars != null)
            {
                float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f;
                float spectrumX = geometry.AnchorRight - 16f - totalBarWidth;
                _barPaint.Color = ToneColor(new SKRect(spectrumX, currentHeight / 2f - maxH / 2f,
                    spectrumX + totalBarWidth, currentHeight / 2f + maxH / 2f), alpha);
                for (int i = 0; i < 5; i++)
                {
                    float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                    canvas.DrawRoundRect(new SKRect(spectrumX + i * (barWidth + spacing), y, spectrumX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                }
            }
        }

        // ---- 展开态面板 ----

        private static void DrawMediaPanel(SKCanvas canvas, MediaController media, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, byte alpha)
        {
            float left = geometry.ZoneLeft;
            float right = geometry.AnchorRight;

            _hitDraft.MediaL = left;
            _hitDraft.MediaR = right;

            float coverSize = MediaPanelCoverSize; // 1. 封面缩小 10px
            float coverX = left + MediaPanelCoverLeft;
            float coverY = MediaPanelCoverTop;    // 封面微调光学居中

            // 封面
            var coverRect = MediaPanelCoverRect(left);
            RegisterMediaCover(coverRect);
            var cover = media.Thumbnail;
            if (cover != null)
            {
                canvas.DrawRoundRect(coverRect, 8f, 8f, _shadowPaint);
                canvas.Save();
                _clipPath.Rewind(); _clipPath.AddRoundRect(coverRect, 8f, 8f);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(cover, coverRect, _highQualitySampling);
                canvas.Restore();
            }
            else
            {
                canvas.DrawRoundRect(coverRect, 8f, 8f, _fallbackIconPaint);
            }

            // 双行文字：左右都用真裁切收口（右侧让给频谱，左侧挡住跑马灯，别让标题滚到封面上）。
            float textStartX = coverX + coverSize + 12f;
            float titleViewRight = right - 55f;
            canvas.Save();
            canvas.ClipRect(new SKRect(textStartX, 0, titleViewRight, currentHeight), SKClipOperation.Intersect, true);

            _titlePaint.Color = _currentTextColor.WithAlpha(alpha);
            _titlePaint.TextSize = 14.5f;

            // 标题超长：向左匀速滚，滚完整条 + 几个空格后接下一轮。
            // 可视宽度与上面的裁切线同源，避免文字滚到频谱底下才被切掉的观感。
            string title = _lastMediaTitle;
            float titleWidth = MeasureMediaTitleWidth(title);
            float titleViewWidth = Math.Max(0f, titleViewRight - textStartX);
            float titleScroll = MediaTitleScrollOffset(title, titleWidth, titleViewWidth);
            if (titleScroll <= 0f)
            {
                DrawKaraoke(canvas, title, textStartX, coverY + 18f, _titlePaint, alpha, 0f, false); // 歌名也做 emoji/多语言回退
            }
            else
            {
                // 两段就够：标题宽 + 空格间隔 = period ≥ 可视宽度，第二段正好在滚完时接上。
                float gap = Math.Max(_titlePaint.MeasureText(TITLE_MARQUEE_GAP), 16f);
                float period = titleWidth + gap;
                float x = textStartX - titleScroll;
                DrawKaraoke(canvas, title, x, coverY + 18f, _titlePaint, alpha, 0f, false);
                DrawKaraoke(canvas, title, x + period, coverY + 18f, _titlePaint, alpha, 0f, false);
            }

            _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            _bodyPaint.TextSize = 12.5f;
            string displaySub = string.IsNullOrEmpty(_lastLyric) ? _lastMediaArtist : _lastLyric;
            string displaySubTrans = string.IsNullOrEmpty(_lastLyric) ? "" : _lastLyricTrans;

            // 展开模式下的平滑叠化渲染 (带卡拉OK)
            // 展开态面板宽度固定、歌词可能比可视区宽：把「正在唱的位置」滚到左侧 40% 处，
            // 翻译按同一进度、按自身宽度跟着滚；滚到底就停（逐字没走完也不再滚）。
            // 没有逐字歌词时 CurrentLyricProgress 本身就是「按本行时长」的线性进度（见 ComputeScanProgress）。
            float lyricViewWidth = Math.Max(0f, titleViewRight - textStartX);
            bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
            // 连续进度：250ms 的采样值只做基准，帧间由本地时钟补间 —— 否则滚动是一跳一跳的。
            float lyricProgress = SmoothLyricProgress(media, media.CurrentLyricProgress);
            if (_lyricAnimProgress < 1f && isLyricDisplay)
            {
                float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);
                if (!string.IsNullOrEmpty(_prevLyric))
                {
                    // 旧歌词淡出时进度直接锁定 100% (1f)
                    DrawLyricLine(canvas, _prevLyric, _prevLyricTrans, textStartX, coverY + 46f - (8f * easeOut), _bodyPaint, (byte)(alpha * (1f - easeOut)), 1f, true, lyricViewWidth);
                }
                // 新歌词套用连续进度
                DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, coverY + 46f + (8f * (1f - easeOut)), _bodyPaint, (byte)(alpha * easeOut), lyricProgress, true, lyricViewWidth);
                _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            }
            else
            {
                DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, coverY + 46f, _bodyPaint, alpha, lyricProgress, isLyricDisplay, lyricViewWidth);
            }

            canvas.Restore();   // 结束文字裁切

            // 复用律动频谱 (放右上角)
            if (bars != null)
            {
                float barWidth = 2.5f, spacing = 3.5f, maxH = 18f, totalBarWidth = 26.5f;
                float startX = right - 20f - totalBarWidth;
                _barPaint.Color = ToneColor(new SKRect(startX, coverY + 22f - maxH / 2f,
                    startX + totalBarWidth, coverY + 22f + maxH / 2f), alpha);
                for (int i = 0; i < 5; i++)
                {
                    float h = Math.Max(2f, bars[i] * maxH);
                    float barY = coverY + 22f + (maxH - h) / 2f;
                    canvas.DrawRoundRect(new SKRect(startX + i * (barWidth + spacing), barY, startX + i * (barWidth + spacing) + barWidth, barY + h), 1.5f, 1.5f, _barPaint);
                }
            }

            // 3. 底部放大媒体控件
            float centerX = (left + right) / 2f;
            float scale = 1.6f;
            // 底圆中心 / 图标中心 / 命中中心必须是同一个点，统一取自「播放控件命中几何」那条真源
            //（Renderer.Media.cs 的 MediaButtonCenterY + MediaButtonSpacing）。
            // 之前图标按左上角画、底圆却拿同一个 x 当圆心 → 底圆比图标偏左半个图标宽（约 8px），
            // 悬停时还会再比光标偏左 6~9px（画的位置是 centerX-60/-7/+45，命中是 centerX±54）。
            float centerY = MediaButtonCenterY(currentHeight);

            void DrawControl(int index, SKPath path, float opticalDx = 0f)
            {
                float cx = centerX + (index - 1) * MediaButtonSpacing;
                var hit = new SKRect(cx - MediaButtonRadius, centerY - MediaButtonRadius,
                    cx + MediaButtonRadius, centerY + MediaButtonRadius);
                if (HoveredExpandedButton == index)
                {
                    _hoverCirclePaint.Color = ToneColor(hit, 25);
                    canvas.DrawCircle(cx, centerY, MediaButtonRadius, _hoverCirclePaint);
                }
                _mediaIconPaint.Color = ToneColor(hit, alpha);
                DrawSvgPathCentered(canvas, _mediaIconPaint, cx + opticalDx, centerY, path, scale);
            }

            DrawControl(0, _prevPath);
            // 播放三角的包围盒中心在底圆正中，但它的面积重心在包围盒左侧 1/6 处（重心 x=w/3，盒心 w/2），
            // 所以几何居中看着仍然「偏左」；暂停的两根竖条左右对称，才显得居中。
            // 右移一点做光学补偿 —— 只挪图标，底圆和命中区仍压在中心上。
            DrawControl(1, media.IsPlaying ? _pausePath : _playPath, media.IsPlaying ? 0f : PlayOpticalDx);
            DrawControl(2, _nextPath);
            _mediaIconPaint.Color = _currentTextColor.WithAlpha(alpha);

            if (TimelineVisible(media) && currentHeight > TL_MIN_HEIGHT)
                DrawTimeline(canvas, media, left, right, currentHeight, alpha);
        }
    }
}
