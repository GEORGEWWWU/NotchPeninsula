using SkiaSharp;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ================= 🎵 媒体控制模块（唯一入口） =================
        // 媒体控制的全部绘制都在本文件：折叠态内联行、展开态面板、遮罩 / 频谱 / 播放按钮、歌词与译文。
        // 时间轴的调度与命中在 Renderer.Media.cs。
        //
        // 组合模式与非组合模式走同一份代码，只是喂进来的 geometry 不同：
        //   · 组合模式：内容起点 = 顺序表行游标，锚点 = 模块右缘 + 18（给频谱 / 按钮让位）；
        //   · 非组合模式：内容起点 = 内容区左边界 + 16，锚点 = 内容区右边界；
        //   · 展开面板：geometry 的左右端就是面板左右端，整块岛体交给它。
        // 展开与否由 IsMediaPanelShowing 裁决。

        // 悬停时播放按钮的垫底遮罩：只盖按钮块本身。三个图标固定摆在组件右端 −90 / −60 / −30，
        // 各自再右移 11px 起画，所以按钮块的实体范围就是组件右端 −79（第一个图标左缘）~ −11（最后一个图标右缘）。
        private const float MEDIA_MASK_FADE = 15f;      // 按钮块左缘再向左的渐隐宽度（把文字柔和收掉）
        private const float BUTTON_BLOCK_LEFT = 79f;    // 组件右端 − 79 = 第一个图标左缘
        private const float BUTTON_BLOCK_RIGHT = 11f;   // 组件右端 − 11 = 「下一首」图标右缘

        /// <summary>
        /// 媒体模块本帧的几何量，三个 x 值都由调用方算好，模块自己不重算。
        /// </summary>
        /// <param name="ZoneLeft">命中区左端（组合 = 模块游标；非组合 = 内容区左边界）。</param>
        /// <param name="ContentLeft">缩略图与文字的起点。</param>
        /// <param name="AnchorRight">播放按钮与频谱的锚定右端，也是宿主判定按钮命中的右边界。</param>
        private readonly record struct MediaBlockGeometry(float ZoneLeft, float ContentLeft, float AnchorRight);

        /// <summary>
        /// 本帧画展开面板（true）还是折叠内联行（false）：媒体激活 + 已展开 + 岛体高度已涨过 60。
        /// 高度门槛是展开动画的过渡闸门，避免在 35px 高的条里塞 130px 的面板。
        /// </summary>
        private static bool IsMediaPanelShowing(MediaController media, float currentHeight)
            => media.IsActive && IsMediaExpanded && currentHeight > 60f;

        /// <summary>
        /// 媒体控制模块的统一入口：展开面板与折叠内联行在这里分流。
        /// </summary>
        private static void DrawMediaControl(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, float textOffsetY, byte alpha)
        {
            if (IsMediaPanelShowing(media, currentHeight))
                DrawMediaPanel(canvas, media, bars, geometry, currentHeight, alpha);
            else
                DrawMediaInline(canvas, media, isHovered, bars, geometry, currentHeight, textOffsetY, alpha);
        }

        // ================= 折叠态内联行 =================
        // 缩略图 + 一行文字（歌词优先，否则「歌手 - 歌名」）+ 右端的频谱或播放按钮。

        private static void DrawMediaInline(SKCanvas canvas, MediaController media, bool isHovered, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, float textOffsetY, byte alpha)
        {
            // 本模块的锚点同时也是宿主判定媒体按钮 / 悬停命中的右边界（组合模式下插件排在媒体右边时也不越界）
            _compositeMediaRight = geometry.AnchorRight;
            // 🖱️ 本模块的右键命中区 = 文字 + 频谱 / 按钮锚点
            _mediaZoneL = geometry.ZoneLeft;
            _mediaZoneR = geometry.AnchorRight;

            _textPaint.Color = _currentTextColor.WithAlpha(alpha);

            float textY = (currentHeight - _cachedMediaTextHeight) / 2 - _cachedMediaTextTop + 0.3f + textOffsetY;
            float textX = geometry.ContentLeft;

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

            // 右端：只有**直接交互**模式悬停时才把频谱换成播放按钮；展开交互模式悬停不显示控件
            //（否则悬停已能直接控制，那个「点一下展开面板」的开关就没意义了），继续画频谱。
            if (isHovered && MediaInteractionMode == 0)
            {
                int btnPrevX = (int)geometry.AnchorRight - 90;
                int btnPlayX = (int)geometry.AnchorRight - 60;
                int btnNextX = (int)geometry.AnchorRight - 30;

                // 垫底遮罩只盖按钮块这一段，两端都渐隐（低背景透明度档位下不出现硬边）：
                //   渐入 [maskL, maskL+fadeW]  →  实心  →  渐出 [maskR−fadeW, maskR]
                // 左缘 = 组件右端 −79（第一个图标左缘）再向左留出渐隐段，右缘 = 组件右端 −11（「下一首」图标右缘）。
                float maskL = Math.Max(geometry.ZoneLeft, geometry.AnchorRight - BUTTON_BLOCK_LEFT - MEDIA_MASK_FADE);
                float maskR = geometry.AnchorRight - BUTTON_BLOCK_RIGHT;
                // 组件过窄时两段渐隐会打架，按可用宽度对半收窄
                float fadeW = Math.Min(MEDIA_MASK_FADE, (maskR - maskL) / 2f);

                canvas.Save();
                canvas.Translate(maskL, 0);
                canvas.Scale(fadeW, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();

                canvas.Save();
                canvas.Translate(maskR, 0);
                canvas.Scale(-fadeW, currentHeight); // 负缩放 → 渐变镜像，实心在左、透明在右
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();

                // ⚠️ 用 SKRect 而不是 (x, y, 宽, 高) 那个重载：后者第 3 个参数是**宽度**，
                //    写成右边缘会画出一条一直冲到岛体最右的色带。
                if (maskR - maskL > fadeW * 2f)
                    canvas.DrawRect(new SKRect(maskL + fadeW, 0f, maskR - fadeW, currentHeight), _bgPaint);

                float prevNextY = (currentHeight - 10f) / 2f; float playPauseY = (currentHeight - 12f) / 2f;
                DrawSvgPath(canvas, _mediaIconPaint, btnPrevX + 11, prevNextY, _prevPath);
                DrawSvgPath(canvas, _mediaIconPaint, btnPlayX + (media.IsPlaying ? 10 : 11), playPauseY, media.IsPlaying ? _pausePath : _playPath);
                DrawSvgPath(canvas, _mediaIconPaint, btnNextX + 11, prevNextY, _nextPath);
            }
            else if (bars != null)
            {
                float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f;
                float spectrumX = geometry.AnchorRight - 16f - totalBarWidth;
                for (int i = 0; i < 5; i++)
                {
                    float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                    canvas.DrawRoundRect(new SKRect(spectrumX + i * (barWidth + spacing), y, spectrumX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                }
            }
        }

        // ================= 展开态面板 =================
        // 整块岛体就是一块 320 × (130 / 158) 的独立面板：封面 + 双行文字（歌名 + 歌词）+ 右侧律动频谱
        // + 底部放大播放控件（+ 可选时间轴）。几何完全按传入的命中区左右边界推，与「组合 / 非组合」无关。
        // 面板尺寸由宿主锁定（NotchWindow：宽 320、高 GetExpandedHeight），这里只负责画。

        private static void DrawMediaPanel(SKCanvas canvas, MediaController media, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, byte alpha)
        {
            float left = geometry.ZoneLeft;
            float right = geometry.AnchorRight;

            // 🖱️ 展开面板整块就是媒体区域（右键直达媒体设置页签也按它判定）
            _mediaZoneL = left;
            _mediaZoneR = right;

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
            // 居中于「媒体内容区」而非整岛：非组合模式下插件行被排到左边时内容区整体右移，按钮要跟着走
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
    }
}
