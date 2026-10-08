using SkiaSharp;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ---- 媒体控制模块（唯一入口） ----

        private const float MEDIA_MASK_FADE = 15f;      // 按钮块左缘再向左的渐隐宽度（把文字柔和收掉）
        private const float BUTTON_BLOCK_LEFT = 79f;    // 组件右端 − 79 = 第一个图标左缘
        private const float BUTTON_BLOCK_RIGHT = 11f;   // 组件右端 − 11 = 「下一首」图标右缘

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
            _mediaZoneL = geometry.ZoneLeft;
            _mediaZoneR = geometry.AnchorRight;
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

            if (isHovered && MediaInteractionMode == 0)
            {
                int btnPrevX = (int)geometry.AnchorRight - 90;
                int btnPlayX = (int)geometry.AnchorRight - 60;
                int btnNextX = (int)geometry.AnchorRight - 30;

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

        // ---- 展开态面板 ----

        private static void DrawMediaPanel(SKCanvas canvas, MediaController media, float[]? bars,
            MediaBlockGeometry geometry, float currentHeight, byte alpha)
        {
            float left = geometry.ZoneLeft;
            float right = geometry.AnchorRight;

            _mediaZoneL = left;
            _mediaZoneR = right;

            float coverSize = 50f; // 1. 封面缩小 10px
            float coverX = left + 20f;
            float coverY = 20f;    // 封面微调光学居中

            // 封面
            var coverRect = new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize);
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

            // 双行文字
            float textStartX = coverX + coverSize + 12f;
            _titlePaint.Color = _currentTextColor.WithAlpha(alpha);
            _titlePaint.TextSize = 14.5f;
            DrawKaraoke(canvas, _lastMediaTitle, textStartX, coverY + 18f, _titlePaint, alpha, 0f, false); // 歌名也做 emoji/多语言回退

            _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            _bodyPaint.TextSize = 12.5f;
            string displaySub = string.IsNullOrEmpty(_lastLyric) ? _lastMediaArtist : _lastLyric;
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
            float centerX = (left + right) / 2f;
            float scale = 1.6f;
            float playBtnY = btnY - 1.6f;

            if (HoveredExpandedButton == 0) canvas.DrawCircle(centerX - 54f, btnY + 8f, 20f, _hoverCirclePaint);
            if (HoveredExpandedButton == 1) canvas.DrawCircle(centerX + 1f, playBtnY + 9.6f, 20f, _hoverCirclePaint);
            if (HoveredExpandedButton == 2) canvas.DrawCircle(centerX + 52f, btnY + 8f, 20f, _hoverCirclePaint);

            DrawSvgPath(canvas, _mediaIconPaint, centerX - 60f, btnY, _prevPath, scale);
            DrawSvgPath(canvas, _mediaIconPaint, centerX - 7f, playBtnY, media.IsPlaying ? _pausePath : _playPath, scale);
            DrawSvgPath(canvas, _mediaIconPaint, centerX + 45f, btnY, _nextPath, scale);

            if (TimelineVisible(media) && currentHeight > TL_MIN_HEIGHT)
                DrawTimeline(canvas, media, left, right, currentHeight, alpha);
        }
    }
}
