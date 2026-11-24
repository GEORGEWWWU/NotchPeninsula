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
        private const float MediaPanelInset = 22f;
        private const float MediaPanelCoverTop = 18f;
        private const float MediaPanelCoverSize = 54f;

        internal static SKRect MediaPanelCoverRect(float panelLeft)
            => SKRect.Create(panelLeft + MediaPanelInset, MediaPanelCoverTop, MediaPanelCoverSize, MediaPanelCoverSize);

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

        // 展开态使用独立的 SVG 路径，按 1 DIP 为一个路径单位绘制。
        private static readonly SKPath _expandedPlayPath = SKPath.ParseSvgPathData(
            "M0 2 Q0 0 2 1 L18 11 Q20 12 18 13 L2 23 Q0 24 0 22 Z");
        private static readonly SKPath _expandedPausePath = SKPath.ParseSvgPathData(
            "M2 0 H6 Q8 0 8 2 V23 Q8 25 6 25 H2 Q0 25 0 23 V2 Q0 0 2 0 Z " +
            "M13 0 H17 Q19 0 19 2 V23 Q19 25 17 25 H13 Q11 25 11 23 V2 Q11 0 13 0 Z");
        private static readonly SKPath _expandedNextPath = SKPath.ParseSvgPathData(
            "M0 2 Q0 0 2 1 L14 8 Q15 8.5 14 9 L2 16 Q0 17 0 15 Z " +
            "M14 2 Q14 0 16 1 L28 8 Q29 8.5 28 9 L16 16 Q14 17 14 15 Z");
        private static readonly SKPath _expandedPrevPath = SKPath.ParseSvgPathData(
            "M28 2 Q28 0 26 1 L14 8 Q13 8.5 14 9 L26 16 Q28 17 28 15 Z " +
            "M14 2 Q14 0 12 1 L0 8 Q-1 8.5 0 9 L12 16 Q14 17 14 15 Z");

        // ---- 展开态标题超长自动滚动 ----
        // 岛内展开面板和实况窗详情页共用这一段绘制：两处共用同一条时间轴，
        // 不会出现「主岛滚到一半、浮层从头发车」。速度取 22px/s：慢一点更好读，
        // 但慢速对亚像素抖更敏感 —— 配合 MediaTitleScrollOffset 的整像素量化 + 滚动期间关 SubpixelText。
        private const float TITLE_MARQUEE_SPEED = 22f;
        // 滚完一整条标题后空出来的间隔，按「几个空格」的宽度算：换字体时比例也不会突然变窄。
        private const string TITLE_MARQUEE_GAP = "     ";

        private static string _titleMarqueeText = "";
        private static DateTime _titleMarqueeTick;
        private static float _titleMarqueeOffset;

        private static string _titleMeasureText = "";
        private static SKTypeface? _titleMeasureFace;
        private static float _titleMeasureSize;
        private static float _titleMeasureWidth;

        private static float MeasureMediaTitleWidth(string text)
        {
            var face = _titlePaint.Typeface;
            if (_titleMeasureText == text && ReferenceEquals(_titleMeasureFace, face) && _titleMeasureSize == _titlePaint.TextSize)
                return _titleMeasureWidth;
            BuildTextRuns(text, _titlePaint, face, _measureRuns, out _titleMeasureWidth);
            _titleMeasureText = text;
            _titleMeasureFace = face;
            _titleMeasureSize = _titlePaint.TextSize;
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
            // 整像素量化：亚像素平移会导致字形边缘每帧重新抗锯齿，慢速滚动时就是肉眼可见的「闪」，
            // 中文笔画密尤其明显。落到整像素后每 1~2 帧跳一次，但完全不闪。
            return MathF.Round(_titleMarqueeOffset);
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

        private static float MediaInlineCoverSize(float height) => Math.Clamp(height - 4f, 0f, 22f);

        private static float MediaInlineLeadingInset(float height)
        {
            const float originalInset = 16f;
            float coverInset = (height - MediaInlineCoverSize(height)) / 2f;
            return (originalInset + Math.Min(originalInset, coverInset)) / 2f;
        }

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

            float thumbSize = MediaInlineCoverSize(currentHeight);
            float thumbY = (currentHeight - thumbSize) / 2f;
            var thumbRect = SKRect.Create(textX, thumbY, thumbSize, thumbSize);
            RegisterMediaCover(thumbRect);

            var thumb = media.Thumbnail;
            if (thumb != null)
            {
                const float thumbRadius = 7f;
                canvas.Save();
                _clipPath.Rewind(); _clipPath.AddRoundRect(thumbRect, thumbRadius, thumbRadius);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(thumb, thumbRect, _highQualitySampling);
                canvas.Restore();
                textX = thumbRect.Right + 10f;
            }

            // 文字右界：悬停出按钮时让到按钮块左侧、常态让到频谱左侧。
            // ⚠️ 用【真裁切】而不是「盖一层渐变 + 纯色」—— 那层在亚克力 / 低透明度下会变成一块突兀的亮片。
            float textClipRight = (isHovered && MediaInteractionMode == 0)
                ? geometry.AnchorRight - BUTTON_BLOCK_LEFT - 4f
                : geometry.AnchorRight - 41f;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, textClipRight, currentHeight), SKClipOperation.Intersect, true);

            // 折叠态下的歌词叠化与位移动画 (带卡拉OK)
            // ⚠️ 这里刻意用原始 CurrentLyricProgress，不接 SmoothLyricProgress：
            //    折叠态不做滚动、且宽度随内容自适应，而 SmoothLyricProgress 的计时基准
            //    是展开态共享的静态字段，两边同时调会互相把基准刷掉（折叠态宽度会跟着抖）。
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

        // 原生亚克力的模糊位于前景窗口之下，前景封面需单独做柔化。
        private const float ImmersiveBlurSigma = 12f;
        private static readonly SKImageFilter _immersiveArtworkBlur = SKImageFilter.CreateBlur(ImmersiveBlurSigma, ImmersiveBlurSigma);
        private static readonly SKPaint _immersiveArtworkPaint = new()
        {
            FilterQuality = SKFilterQuality.Medium,
            ImageFilter = _immersiveArtworkBlur
        };

        private static SKBitmap? _immersiveShaderBitmap;
        private static SKRect _immersiveShaderBounds;

        private static void DrawImmersiveArtwork(SKCanvas canvas, MediaController media, SKRect island)
        {
            if (!media.IsActive || SuppressMediaInIsland) return;
            bool mediaVisible = SilentActive ? SilentMode.MediaImportantEnabled
                : StandbyActive ? StandbySceneKey == Plugins.BuiltinWidgets.Media
                : CompShowMedia;
            if (!mediaVisible || Plugins.PluginManager.Instance.Host.ActiveDetailPage != null) return;

            var artwork = media.AlbumArtwork;
            if (artwork == null || artwork.Width <= 0 || artwork.Height <= 0) return;

            // 按岛体宽度等比例缩放，垂直居中，所有溢出交给岛体路径裁切。
            float imageHeight = island.Width * artwork.Height / artwork.Width;
            var image = SKRect.Create(island.Left, island.MidY - imageHeight / 2f, island.Width, imageHeight);
            if (!ReferenceEquals(artwork, _immersiveShaderBitmap) || image != _immersiveShaderBounds)
            {
                var matrix = SKMatrix.CreateScaleTranslation(image.Width / artwork.Width, image.Height / artwork.Height,
                    image.Left, image.Top);
                using var shader = artwork.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, matrix);
                _immersiveArtworkPaint.Shader = shader;
                _immersiveShaderBitmap = artwork;
                _immersiveShaderBounds = image;
            }

            // 为卷积补上延伸的边缘像素，防止模糊在封面两侧混入透明像素。
            var blurInput = island;
            blurInput.Inflate(ImmersiveBlurSigma * 4f, ImmersiveBlurSigma * 4f);
            canvas.Save();
            canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);
            canvas.ClipRect(image);
            canvas.DrawRect(blurInput, _immersiveArtworkPaint);
            canvas.Restore();
        }

        // ---- 展开态面板 ----

        private static void DrawMediaPanel(SKCanvas canvas, MediaController media, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, byte alpha, bool floating = false)
        {
            float left = geometry.ZoneLeft;
            float right = geometry.AnchorRight;

            var coverRect = MediaPanelCoverRect(left);
            if (!floating)
            {
                _hitDraft.MediaL = left;
                _hitDraft.MediaR = right;
                RegisterMediaCover(coverRect);
            }

            const float coverRadius = 14f;
            var cover = media.Thumbnail;
            if (cover != null)
            {
                canvas.Save();
                _clipPath.Rewind(); _clipPath.AddRoundRect(coverRect, coverRadius, coverRadius);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(cover, coverRect, _highQualitySampling);
                canvas.Restore();
            }
            else
            {
                var accent = ToneColor(coverRect, 255).Red < 128
                    ? new SKColor(29, 85, 146, alpha)
                    : new SKColor(152, 202, 255, alpha);
                _barBgPaint.Color = accent.WithAlpha((byte)(alpha * 0.18f));
                canvas.DrawRoundRect(coverRect, coverRadius, coverRadius, _barBgPaint);

                _mediaIconPaint.Color = accent;
                float x = coverRect.MidX, y = coverRect.MidY;
                canvas.DrawOval(SKRect.Create(x - 8f, y + 3f, 10f, 6f), _mediaIconPaint);
                canvas.DrawRect(SKRect.Create(x, y - 9f, 2f, 15f), _mediaIconPaint);
                canvas.DrawRoundRect(SKRect.Create(x, y - 9f, 8f, 3f), 1.5f, 1.5f, _mediaIconPaint);
            }

            // 双行文字：左右都用真裁切收口（右侧让给频谱，左侧挡住跑马灯，别让标题滚到封面上）。
            var spectrum = SKRect.Create(right - MediaPanelInset - 34f, coverRect.Top + 2f, 34f, 28f);
            float textStartX = coverRect.Right + 14f;
            float titleViewRight = spectrum.Left - 12f;
            float titleBaseline = coverRect.MidY - 6f;
            bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
            float subBaseline = coverRect.MidY + (isLyricDisplay ? 15f : 13f);

            canvas.Save();
            canvas.ClipRect(new SKRect(textStartX, 0f, titleViewRight, coverRect.Bottom + 5f), SKClipOperation.Intersect, true);

            _titlePaint.Color = _currentTextColor.WithAlpha(alpha);
            _titlePaint.TextSize = 16f;

            // 标题超长：向左匀速滚，滚完整条 + 几个空格后接下一轮。
            // 可视宽度与上面的裁切线同源，避免文字滚到频谱底下才被切掉的观感。
            string title = _lastMediaTitle;
            float titleWidth = MeasureMediaTitleWidth(title);
            float titleViewWidth = Math.Max(0f, titleViewRight - textStartX);
            float titleScroll = MediaTitleScrollOffset(title, titleWidth, titleViewWidth);
            if (titleScroll <= 0f)
            {
                DrawKaraoke(canvas, title, textStartX, titleBaseline, _titlePaint, alpha, 0f, false); // 歌名也做 emoji/多语言回退
            }
            else
            {
                // 滚动期间关掉亚像素文本定位：慢速平移下字形边缘每帧重新抗锯齿，肉眼看就是「闪」，
                // 中文笔画密比英文明显得多。静止文字仍然保留亚像素（更平滑）。
                bool prevSubpixel = _titlePaint.SubpixelText;
                _titlePaint.SubpixelText = false;
                try
                {
                    // 两段就够：标题宽 + 空格间隔 = period ≥ 可视宽度，第二段正好在滚完时接上。
                    float gap = Math.Max(_titlePaint.MeasureText(TITLE_MARQUEE_GAP), 16f);
                    float period = titleWidth + gap;
                    float x = textStartX - titleScroll;
                    DrawKaraoke(canvas, title, x, titleBaseline, _titlePaint, alpha, 0f, false);
                    DrawKaraoke(canvas, title, x + period, titleBaseline, _titlePaint, alpha, 0f, false);
                }
                finally { _titlePaint.SubpixelText = prevSubpixel; }
            }

            // 歌词切换时的位移限制在副标题行，避免两行歌词滑过歌名。
            canvas.ClipRect(new SKRect(textStartX, titleBaseline + 3f, titleViewRight, coverRect.Bottom + 5f));
            _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            _bodyPaint.TextSize = 12.5f;
            string displaySub = isLyricDisplay ? _lastLyric : _lastMediaArtist;
            string displaySubTrans = isLyricDisplay ? _lastLyricTrans : "";

            // 展开模式下的平滑叠化渲染 (带卡拉OK)
            // 展开态面板宽度固定、歌词可能比可视区宽：把「正在唱的位置」滚到左侧 40% 处，
            // 翻译按同一进度、按自身宽度跟着滚；滚到底就停（逐字没走完也不再滚）。
            // 没有逐字歌词时 CurrentLyricProgress 本身就是「按本行时长」的线性进度（见 ComputeScanProgress）。
            // 连续进度：250ms 的采样值只做基准，帧间由本地时钟补间 —— 否则滚动是一跳一跳的。
            float lyricProgress = SmoothLyricProgress(media, media.CurrentLyricProgress);
            if (_lyricAnimProgress < 1f && isLyricDisplay)
            {
                float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);
                if (!string.IsNullOrEmpty(_prevLyric))
                {
                    // 旧歌词淡出时进度直接锁定 100% (1f)
                    DrawLyricLine(canvas, _prevLyric, _prevLyricTrans, textStartX, subBaseline - (8f * easeOut), _bodyPaint, (byte)(alpha * (1f - easeOut)), 1f, true, titleViewWidth);
                }
                // 新歌词套用连续进度
                DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, subBaseline + (8f * (1f - easeOut)), _bodyPaint, (byte)(alpha * easeOut), lyricProgress, true, titleViewWidth);
                _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            }
            else
            {
                DrawLyricLine(canvas, displaySub, displaySubTrans, textStartX, subBaseline, _bodyPaint, alpha, lyricProgress, isLyricDisplay, titleViewWidth);
            }

            canvas.Restore();   // 结束文字裁切

            if (bars is { Length: >= 5 })
            {
                _barPaint.Color = ToneColor(spectrum, 255).Red < 128
                    ? new SKColor(117, 68, 85, alpha)
                    : new SKColor(130, 88, 97, alpha);
                for (int i = 0; i < 5; i++)
                {
                    float h = Math.Clamp(bars[i] * 16f, 2f, 16f);
                    float x = spectrum.MidX + (i - 2) * 4.5f - 1f;
                    canvas.DrawRoundRect(SKRect.Create(x, spectrum.MidY - h / 2f, 2f, h), 1f, 1f, _barPaint);
                }
            }

            if (MediaPanelTimelineVisible(media, forceExpanded: floating) && currentHeight > TimelineRevealHeight)
            {
                float reveal = Math.Clamp((currentHeight - TimelineRevealHeight) /
                    (MEDIA_EXPANDED_HEIGHT_TIMELINE - TimelineRevealHeight), 0f, 1f);
                DrawTimeline(canvas, media, left, right, currentHeight, (byte)(alpha * reveal), floating);
            }

            if (currentHeight > MediaControlsRevealHeight)
            {
                float reveal = Math.Clamp((currentHeight - MediaControlsRevealHeight) /
                    (MEDIA_EXPANDED_HEIGHT_PLAIN - MediaControlsRevealHeight), 0f, 1f);
                byte controlsAlpha = (byte)(alpha * reveal);
                float centerX = (left + right) / 2f;
                DrawExpandedControl(canvas, centerX, currentHeight, 0, _expandedPrevPath, controlsAlpha);
                DrawExpandedControl(canvas, centerX, currentHeight, 1, media.IsPlaying ? _expandedPausePath : _expandedPlayPath,
                    controlsAlpha, media.IsPlaying ? 0f : PlayOpticalDx);
                DrawExpandedControl(canvas, centerX, currentHeight, 2, _expandedNextPath, controlsAlpha);
                _mediaIconPaint.Color = _currentTextColor.WithAlpha(alpha);
            }
        }

        private static void DrawExpandedControl(SKCanvas canvas, float centerX, float height, int index, SKPath icon,
            byte alpha, float opticalDx = 0f)
        {
            var bounds = MediaButtonBounds(centerX, height, index);
            _mediaIconPaint.Color = ToneColor(bounds, alpha);
            float scale = HoveredExpandedButton == index ? 1.07f : 1f;
            DrawSvgPathCentered(canvas, _mediaIconPaint, bounds.MidX + opticalDx, bounds.MidY, icon, scale);
        }
    }
}
