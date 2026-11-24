using SkiaSharp;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        public static float MeasureCurrentLyricWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (ReuseCachedRuns(text, _semiBoldTypeface, out float cachedWidth)) return cachedWidth;
            return MeasureTextWithFallback(text);
        }

        public static bool IsMediaExpanded = false;

        public static bool IsMediaPanelShowing(MediaController? media)
            => media is { IsActive: true } && Renderer.IsMediaExpanded && _currentHeightForHit > 60f;

        private static float _currentHeightForHit;

        public static void SetHitTestHeight(float height) => _currentHeightForHit = height;

        public static int HoveredExpandedButton = -1; // -1:无, 0:上一首, 1:播放/暂停, 2:下一首

        private const float MediaControlsRevealHeight = 122f;

        public static int HitExpandedButton(float x, float y, float currentHeight)
        {
            var zones = HitZones;
            return HitMediaPanelButton(x, y, zones.MediaL, zones.MediaR, currentHeight);
        }

        private static SKRect MediaButtonBounds(float centerX, float height, int index)
        {
            float x = centerX + (index - 1) * 62f;
            float y = height - 30f;
            float halfWidth = index == 1 ? 27f : 22f;
            return new SKRect(x - halfWidth, y - 18f, x + halfWidth, y + 18f);
        }

        private static int HitMediaPanelButton(float x, float y, float left, float right, float height)
        {
            if (right <= left || height <= MediaControlsRevealHeight) return -1;
            float centerX = (left + right) / 2f;
            for (int i = 0; i < 3; i++)
            {
                if (MediaButtonBounds(centerX, height, i).Contains(x, y)) return i;
            }
            return -1;
        }

        public static int HitInlineButton(float x, float y, float right, float currentHeight)
        {
            float centerY = currentHeight / 2f;
            float radius = 12f;

            for (int i = 0; i < 3; i++)
            {
                float cx = right - 79f + i * 30f;
                float dx = x - cx, dy = y - centerY;
                if (dx * dx + dy * dy <= radius * radius) return i;
            }
            return -1;
        }

        private const float TimelineBottomGap = 68f;
        private const float TimelineRevealHeight = 152f;

        // 主岛和媒体实况窗使用同一尺寸。
        public const float MEDIA_EXPANDED_WIDTH = 350f;
        public const float MEDIA_EXPANDED_HEIGHT_TIMELINE = 164f;
        public const float MEDIA_EXPANDED_HEIGHT_PLAIN = 130f;

        /// <summary>展开态要不要带时间轴（进度条）：主岛展开面板 / 实况窗详情页共用同一判据。</summary>
        public static bool MediaPanelTimelineVisible(MediaController media, bool forceExpanded = false)
            => (forceExpanded || MediaInteractionMode == 1) && media.HasTimeline;

        public static bool TimelineVisible(MediaController media)
            => IsMediaExpanded && MediaPanelTimelineVisible(media);

        public static float GetExpandedHeight(MediaController media, bool forceExpanded = false)
            => MediaPanelTimelineVisible(media, forceExpanded)
                ? MEDIA_EXPANDED_HEIGHT_TIMELINE
                : MEDIA_EXPANDED_HEIGHT_PLAIN;

        public static bool HitTimeline(float x, float y) => HitTimeline(HitZones, x, y);

        private static bool HitTimeline(NativeHitZones z, float x, float y)
            => HitTimeline(x, y, z.TlX1, z.TlX2, z.TlY);

        private static bool HitTimeline(float x, float y, float left, float right, float centerY)
            => right > left && x >= left - 8f && x <= right + 8f && Math.Abs(y - centerY) <= 13f;

        // ---- 歌词翻译（上下两行） ----

        private const float LYRIC_TRANS_LINE_STEP = 15f;  // 原文与译文两条基线的间距

        private const float LYRIC_TRANS_SCALE = 0.92f;    // 译文视觉缩放：12.5px → 约 11.5px（略小于原文，保持主次）

        public static bool IsTranslationLineVisible(MediaController? media)
            => media != null
               && MediaController.IsTranslationEnabled
               && !string.IsNullOrEmpty(media.CurrentLyric)
               && !string.IsNullOrEmpty(media.CurrentLyricTranslation);

        public static float MeasureLyricTranslationWidth(string text)
            => string.IsNullOrEmpty(text) ? 0f : MeasureCurrentLyricWidth(text) * LYRIC_TRANS_SCALE;

        // ---- 折叠态媒体标题区：故意没有右键热区 ----
        // 这里的右键只按区域决定「直达设置窗口的哪个页签」，

        public static float TimelineRatio(float x)
        {
            var z = HitZones;
            return z.TlX2 > z.TlX1 ? Math.Clamp((x - z.TlX1) / (z.TlX2 - z.TlX1), 0f, 1f) : 0f;
        }

        // 高频字符串与排版宽度缓存
        private static string _lastMediaTitle = "";

        private static string _lastMediaArtist = "";

        private static string _cachedMediaDisplay = "Code By Ryen";

        private static float _cachedMediaTextTop = 0f;

        private static float _cachedMediaTextHeight = 0f;

        // 歌词动画专属独立变量
        private static string _lastLyric = "";

        private static string _prevLyric = ""; // 保存上一句歌词

        private static string _lastLyricTrans = ""; // 当前句的译文（第二行），无译文时为空

        private static string _prevLyricTrans = ""; // 叠化淡出层的译文，与 _prevLyric 同生同灭

        private static float _lyricAnimProgress = 1f; // 动画进度 0~1

        private static DateTime _lyricChangeTime; // 动画起始时间

        private static string[]? _cpuStrs;

        private static string[]? _ramStrs;

        private static int _cpuUsage = 0;

        private static int _ramUsage = 0;

        private static ulong _lastIdleTime = 0, _lastSystemTime = 0;

        private static int _lastHardwareTick = 0;
        // 硬件监控平滑过渡与标签零 GC 缓存

        private static float _smoothCpuUsage = 0f;

        private static float _smoothRamUsage = 0f;

        private static string[]? _pctStrs;

        private static void UpdateHardwareStats()
        {
            if (_cpuStrs == null)
            {
                _cpuStrs = new string[101];
                _ramStrs = new string[101];
                _pctStrs = new string[101];
                for (int i = 0; i <= 100; i++)
                {
                    _cpuStrs[i] = $"CPU {i}%";
                    _ramStrs[i] = $"RAM {i}%";
                    _pctStrs[i] = $"{i}%";
                }
            }

            int now = Environment.TickCount;
            if (now - _lastHardwareTick >= 1000)
            {
                _lastHardwareTick = now;

                var memInfo = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Win32.MEMORYSTATUSEX)) };
                if (Win32.GlobalMemoryStatusEx(ref memInfo)) _ramUsage = (int)memInfo.dwMemoryLoad;

                if (Win32.GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    ulong currentIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
                    ulong currentSystem = (((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime) + (((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime);

                    if (_lastSystemTime > 0)
                    {
                        ulong idleDiff = currentIdle - _lastIdleTime;
                        ulong sysDiff = currentSystem - _lastSystemTime;
                        if (sysDiff > 0) _cpuUsage = (int)((sysDiff - idleDiff) * 100 / sysDiff);
                    }
                    _lastIdleTime = currentIdle; _lastSystemTime = currentSystem;
                }
            }

            // 帧级线性插值（Lerp），实现丝滑过渡动画
            _smoothCpuUsage += (_cpuUsage - _smoothCpuUsage) * 0.2f;
            _smoothRamUsage += (_ramUsage - _smoothRamUsage) * 0.2f;
        }

        // 时间日期零GC缓存

        private static int _lastMinute = -1;

        private static string _cachedTimeStr = "";

        private static string _cachedDateStr = "";

        private static float _cachedTimeWidth = 0f;

        private static float _cachedDateWidth = 0f;

        public static float CachedTimeWidth => _cachedTimeWidth;

        public static float CachedDateWidth => _cachedDateWidth;

        private static readonly List<(string Text, SKTypeface Type, float X)> _karaokeRuns = new(); // 歌词/歌名/歌手 逐字字体回退用的 runs

        private static readonly List<(string Text, SKTypeface Type, float X)> _karaokeRuns1 = new(); // 第二缓存槽（叠化动画时旧/新两条歌词各占一槽）

        private static (string Text, SKTypeface Type, float Width) _krKey0;

        private static (string Text, SKTypeface Type, float Width) _krKey1;

        private static bool _krSlot;

        private static SKRect MediaTimelineTrack(float left, float right, float height, string duration)
        {
            // 两端按总时长的位数留白，时间更新时轨道和拖动坐标保持稳定。
            float labelWidth = Math.Max(34f, (duration.Length + 1) * 6.5f);
            float inset = MediaPanelInset + labelWidth + 7f;
            float y = height - TimelineBottomGap;
            return new SKRect(left + inset, y - 2.5f, right - inset, y + 2.5f);
        }

        private static SKColor MediaAccent(SKRect area, byte alpha)
            => ToneColor(area, 255).Red < 128
                ? new SKColor(29, 85, 146, alpha)
                : new SKColor(152, 202, 255, alpha);

        private static void DrawTimeline(SKCanvas canvas, MediaController media, float left, float right, float height,
            byte alpha, bool floating)
        {
            var track = MediaTimelineTrack(left, right, height, media.TimelineTotal);
            if (track.Width < 20f) return;

            float baseline = track.MidY + 3.5f;
            string elapsed = media.TimelineElapsed, remaining = media.TimelineRemaining;
            DrawToneText(canvas, elapsed, left + MediaPanelInset, baseline, _tlTextPaint, alpha);
            DrawToneText(canvas, remaining, right - MediaPanelInset - _tlTextPaint.MeasureText(remaining), baseline, _tlTextPaint, alpha);

            const float radius = 2.5f;
            _barBgPaint.Color = ToneColor(track, (byte)(alpha * 0.23f));
            canvas.DrawRoundRect(track, radius, radius, _barBgPaint);

            float head = track.Left + track.Width * media.TimelineProgress;
            _barPaint.Color = MediaAccent(track, alpha);
            if (head > track.Left)
                canvas.DrawRoundRect(new SKRect(track.Left, track.Top, head, track.Bottom), radius, radius, _barPaint);
            if (media.IsDragging)
                canvas.DrawCircle(head, track.MidY, 4.5f, _barPaint);

            if (!floating)
            {
                _hitDraft.TlX1 = track.Left;
                _hitDraft.TlX2 = track.Right;
                _hitDraft.TlY = track.MidY;
            }
        }

        private static void DrawKaraoke(SKCanvas canvas, string text, float x, float y, SKPaint paint, byte targetAlpha, float progress, bool isLyric)
        {
            SKTypeface baseTypeface = paint.Typeface;

            // 元素级明暗：整行文字按它自己那一小块玻璃的底色定黑白。
            // 逐字判会让同一个词一半黑一半白（相邻字落在不同网格格里），整行统一才好看。
            paint.Color = ToneText(text, x, y, paint, 255, false);

            List<(string Text, SKTypeface Type, float X)> runs;
            float totalWidth;
            if (ReuseCachedRuns(text, baseTypeface, out totalWidth))
            {
                runs = ReferenceEquals(_krKey0.Type, baseTypeface) && _krKey0.Text == text ? _karaokeRuns : _karaokeRuns1;
            }
            else
            {
                if (_krSlot)
                {
                    BuildTextRuns(text, paint, baseTypeface, _karaokeRuns1, out totalWidth);
                    _krKey1 = (text, baseTypeface, totalWidth);
                    runs = _karaokeRuns1;
                }
                else
                {
                    BuildTextRuns(text, paint, baseTypeface, _karaokeRuns, out totalWidth);
                    _krKey0 = (text, baseTypeface, totalWidth);
                    runs = _karaokeRuns;
                }
                _krSlot = !_krSlot;
            }

            if (!isLyric || !MediaController.IsLyricScanEnabled)
            {
                foreach (var run in runs)
                {
                    paint.Typeface = run.Type;
                    paint.Color = paint.Color.WithAlpha(targetAlpha);
                    canvas.DrawText(run.Text, x + run.X, y, paint);
                }
                paint.Typeface = baseTypeface;
                paint.Color = paint.Color.WithAlpha(targetAlpha);
                return;
            }

            // 1. 先画完整的半透明底板（未唱到的部分）。40% 在浅底上几乎看不见，
            //    提到 55%：仍明显暗于已唱段，但浅底也能读出歌词。
            foreach (var run in runs)
            {
                paint.Typeface = run.Type;
                paint.Color = paint.Color.WithAlpha((byte)(targetAlpha * 0.55f));
                canvas.DrawText(run.Text, x + run.X, y, paint);
            }

            // 2. 算出现在应该亮起到多宽
            float scanWidth = totalWidth * progress;

            // 3. 硬件级裁剪高亮部分并覆盖上去
            canvas.Save();
            // y-30 到 y+10 足够包裹住字体的上下最高/低点
            canvas.ClipRect(new SKRect(x, y - 30f, x + scanWidth, y + 10f), SKClipOperation.Intersect, true);
            paint.Color = paint.Color.WithAlpha(targetAlpha);
            foreach (var run in runs)
            {
                paint.Typeface = run.Type;
                canvas.DrawText(run.Text, x + run.X, y, paint);
            }
            canvas.Restore();

            paint.Typeface = baseTypeface;
            paint.Color = paint.Color.WithAlpha(targetAlpha);
        }

        private static void DrawLyricLine(SKCanvas canvas, string text, string translation, float x, float y,
            SKPaint paint, byte targetAlpha, float progress, bool isLyric, float viewWidth = 0f)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool twoLines = isLyric && !string.IsNullOrEmpty(translation) && MediaController.IsTranslationEnabled;
            float mainY = twoLines ? y - LYRIC_TRANS_LINE_STEP * 0.5f : y;

            // 展开态歌词横向滚动：把「正在唱的位置」对到可视区左侧 40%，滚到底就停（逐字没走完也不再滚）。
            float mainScroll = LyricScrollOffset(paint.MeasureText(text), viewWidth, progress);

            // 翻译：不跟逐字锚点，就按行进度「匀速」从 0 滚到底（到底即停）——
            // 它比主歌词短，跟着 40% 锚点走会显得乱跳，匀速更稳。
            float transScroll = 0f;
            if (twoLines)
            {
                float transScreenW = paint.MeasureText(translation) * LYRIC_TRANS_SCALE;
                if (viewWidth > 1f && transScreenW > viewWidth + 0.5f)
                    transScroll = MathF.Round((transScreenW - viewWidth) * Math.Clamp(progress, 0f, 1f));
            }

            // 滚动期间关掉亚像素定位：慢速平移下字形边缘每帧重新抗锯齿 = 肉眼看就是「闪」，
            // 中文笔画密尤其明显。静止文字仍保留亚像素（更平滑）。
            bool scrolling = mainScroll > 0.01f || transScroll > 0.01f;
            bool prevSubpixel = paint.SubpixelText;
            if (scrolling) paint.SubpixelText = false;
            try
            {
                if (mainScroll > 0.01f)
                {
                    canvas.Save();
                    try
                    {
                        canvas.Translate(-mainScroll, 0f);
                        DrawKaraoke(canvas, text, x, mainY, paint, targetAlpha, progress, isLyric);
                    }
                    finally { canvas.Restore(); }
                }
                else
                {
                    DrawKaraoke(canvas, text, x, mainY, paint, targetAlpha, progress, isLyric);
                }

                if (!twoLines) return;

                byte transAlpha = (byte)(targetAlpha * 0.88f);
                var savedColor = paint.Color;
                paint.Color = _currentSubTextColor.WithAlpha(transAlpha);

                canvas.Save();
                try
                {
                    canvas.Translate(x - transScroll / LYRIC_TRANS_SCALE, y + LYRIC_TRANS_LINE_STEP * 0.5f);
                    canvas.Scale(LYRIC_TRANS_SCALE, LYRIC_TRANS_SCALE);
                    DrawKaraoke(canvas, translation, 0f, 0f, paint, transAlpha, 1f, false);
                }
                finally
                {
                    canvas.Restore();
                    paint.Color = savedColor;
                }
            }
            finally
            {
                paint.SubpixelText = prevSubpixel;
            }
        }

        /// <summary>
        /// 歌词横向滚动量（屏幕像素）：把「progress 对应的那个字」放在可视区 40% 处，
        /// 并夹在 [0, 文字宽 - 可视宽] —— 滚到底之后即使逐字还没走完也不再滚。
        /// <paramref name="viewWidth"/> ≤ 1 表示不滚（折叠态走这条）。
        /// </summary>
        private static float LyricScrollOffset(float textWidth, float viewWidth, float progress)
        {
            if (viewWidth <= 1f || textWidth <= viewWidth + 0.5f) return 0f;
            float maxScroll = textWidth - viewWidth;
            float want = textWidth * Math.Clamp(progress, 0f, 1f) - viewWidth * LyricScrollAnchor;
            // 整像素量化：半像素档仍会落到亚像素抗锯齿上，中文边缘照样抖（速度越慢越明显）。
            return MathF.Round(Math.Clamp(want, 0f, maxScroll));
        }

        /// <summary>正在唱的位置停在可视区左侧 40% 处。</summary>
        private const float LyricScrollAnchor = 0.4f;

        // ---- 歌词滚动的连续性：媒体位置每 250ms 才更新一次，直接画会一跳一跳 ----
        private static float _lyricProgRaw, _lyricProgOut, _lyricProgRate;
        private static long _lyricProgRawTs, _lyricProgOutTs;

        // ---- 行内连续进度：媒体位置每 250ms 才刷新，靠本地时钟补间才对得平 ----
        private static double _lineElapsedRaw;
        private static long _lineElapsedTs;

        /// <summary>
        /// 展开态歌词的连续进度：用 <see cref="MediaController.CurrentLyricLineElapsedMs"/> 作基准，
        /// 两次刷新之间由本地时钟推进，再除以「本行总时长」。
        /// 总时长取逐字表最后一段的 endMs；没有逐字时用「已播放 / 当前进度」反推。
        /// 这样偏移是单调匀速变化的，不会像用 250ms 采样值那样一跳一跳。
        /// </summary>
        internal static float SmoothLyricProgress(MediaController media, float fallback)
        {
            long now = Environment.TickCount64;
            double rawMs = media.CurrentLyricLineElapsedMs;

            if (Math.Abs(rawMs - _lineElapsedRaw) > 0.5)
            {
                // 新采样：换行 / seek 时 rawMs 会跳，直接以它为准重置基准。
                _lineElapsedRaw = rawMs;
                _lineElapsedTs = now;
            }
            if (_lineElapsedTs == 0) { _lineElapsedTs = now; return fallback; }

            double elapsed = _lineElapsedRaw + (now - _lineElapsedTs);

            double totalMs = 0;
            var ends = media.CurrentLyricWordEndMs;
            if (ends.Length > 0) totalMs = ends[^1];
            if (totalMs <= 1)
            {
                float p = media.CurrentLyricProgress;
                if (p > 0.02f && rawMs > 1) totalMs = rawMs / p;
            }
            if (totalMs <= 1) return fallback;

            return Math.Clamp((float)(elapsed / totalMs), 0f, 1f);
        }

        /// <summary>
        /// 把「阶梯状」的行内进度变成连续值：采样之间按上次速率外推，输出再做一次时间常数约 40ms 的平滑。
        /// 行切换 / 拖动进度条时进度会突降，这时不做外推（速率为 0），直接跟随新值。
        /// </summary>
        internal static float AdvanceLyricProgress(float raw)
        {
            long now = Environment.TickCount64;
            if (_lyricProgRawTs == 0)
            {
                _lyricProgRaw = _lyricProgOut = raw;
                _lyricProgRawTs = _lyricProgOutTs = now;
                return raw;
            }

            if (Math.Abs(raw - _lyricProgRaw) > 0.001f)
            {
                double dt = Math.Max(0.001, (now - _lyricProgRawTs) / 1000.0);
                float rate = (float)((raw - _lyricProgRaw) / dt);
                // 只接受「往前推进」的速率：往回跳说明换行 / seek，直接归零避免反向冲刺。
                _lyricProgRate = rate > 0f ? Math.Min(rate, 2.5f) : 0f;
                _lyricProgRaw = raw;
                _lyricProgRawTs = now;
            }

            double elapsed = (now - _lyricProgRawTs) / 1000.0;
            float predicted = Math.Clamp(_lyricProgRaw + _lyricProgRate * (float)elapsed, 0f, 1f);

            float dtOut = (float)Math.Max(0.001, (now - _lyricProgOutTs) / 1000.0);
            float error = predicted - _lyricProgOut;
            // 落后太多（换行 / 拖进度 / 掉帧）就直接对上，别慢慢追 —— 追的过程看起来就是「滚得慢」。
            _lyricProgOut = Math.Abs(error) > 0.08f
                ? predicted
                : _lyricProgOut + error * Math.Min(1f, 18f * dtOut);
            _lyricProgOutTs = now;
            return Math.Clamp(_lyricProgOut, 0f, 1f);
        }

        private static float MeasureHardwareBlockWidth()
        {
            float cpuLabelW = _tagTextPaint.MeasureText("CPU");
            float ramLabelW = _tagTextPaint.MeasureText("RAM");
            float pctW = _textPaint.MeasureText("100%");
            float cpuTagW = cpuLabelW + 6f;
            float ramTagW = ramLabelW + 6f;
            float cpuGroupW = cpuTagW + 4f + pctW;
            float ramGroupW = ramTagW + 4f + pctW;
            return cpuGroupW + 16f + ramGroupW;
        }

        private static float MeasureMediaBlockWidth(MediaController? media)
        {
            float textWidth = (!string.IsNullOrEmpty(media?.CurrentLyric) && MediaController.IsLyricsEnabled)
                ? MeasureTextWithFallback(media!.CurrentLyric)
                : (string.IsNullOrEmpty(media?.Artist)
                    ? MeasureTextWithFallback(media?.Title)
                    : MeasureTextWithFallback(media!.Artist) + MeasureTextWithFallback(media.Title) + 15f);

            if (IsTranslationLineVisible(media))
                textWidth = Math.Max(textWidth, MeasureTextWithFallback(media!.CurrentLyricTranslation) * LYRIC_TRANS_SCALE);

            float thumbW = media?.Thumbnail != null ? 32f : 0f;
            return thumbW + textWidth + 12f + 21.2f;
        }

        public static float GetCompositeWidth(MediaController media)
        {
            if (!CompositeModeEnabled) return STANDBY_WIDTH;

            RefreshPluginWidgets(); // 插件宽度需与快照同版本，才能算准总宽

            // 第一趟：只量原生模块，定出插件行还能用多少宽度
            SetPluginRowBudget(IslandWidthCap - MeasureCompositeWidth(media, includePlugins: false));

            return Math.Clamp(MeasureCompositeWidth(media, includePlugins: true), 60f, IslandWidthCap);
        }

        public static float GetCompositeNativeWidth(MediaController media)
            => CompositeModeEnabled ? MeasureCompositeWidth(media, includePlugins: false) : 0f;

        private const float STANDBY_BLANK_WIDTH = 96f;

        private static float MeasureCompositeWidth(MediaController media, bool includePlugins)
        {
            if (SilentActive)
            {
                // 静默模式没有原生模块，宽度全留给「勾选了 + 有重要信息」的插件行。
                // 第一趟（只量原生）返回 0：预算整块给插件，否则插件会被自己的预算挡掉。
                if (!includePlugins) return 0f;

                var ids = Plugins.PluginManager.Instance.SilentImportantPluginIds;
                float own = 0f;
                for (int i = 0; i < ids.Count; i++)
                {
                    float w = SumPluginRowWidth(ids[i]);
                    if (w <= 0f) continue;
                    own = own > 0f ? own + 16f + w : w;
                }

                // 没有插件内容但实况窗算重要信息时，岛用空白胶囊兜底（气泡要在屏上，岛得回来陪它）。
                return own > 0f ? 16f + own + 16f : STANDBY_BLANK_WIDTH;
            }

            if (StandbyActive)
            {
                string scene = StandbySceneKey;
                if (string.IsNullOrEmpty(scene)) return STANDBY_BLANK_WIDTH;

                float own;
                if (string.Equals(scene, Plugins.BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase))
                    own = _cachedTimeWidth + 12f + _cachedDateWidth;
                else if (string.Equals(scene, Plugins.BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase))
                    own = MeasureHardwareBlockWidth();
                else if (string.Equals(scene, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase))
                    own = media != null && media.IsActive && !SuppressMediaInIsland ? MeasureMediaBlockWidth(media) : 0f;
                else if (includePlugins)
                {
                    // 同上：Key → 插件 Id 才能命中组件。
                    string? pluginId = ResolveStandbyPluginId();
                    own = pluginId != null ? SumPluginRowWidth(pluginId) : 0f;
                }
                else
                    return 0f;                        // 插件项第一趟不占原生宽度，预算全留给插件行

                return own > 0f ? 16f + own + 16f : STANDBY_BLANK_WIDTH;
            }

            float width = 16f; // 初始只有左边距 16px
            bool hasPrev = false;

            void AddModule(float w)
            {
                if (w <= 0f) return;
                if (hasPrev) width += 16f; // 前面已有内容 → 补 16px 间距
                width += w;
                hasPrev = true;
            }

            var order = Plugins.PluginManager.Instance.Host.ContentOrder;
            // 第一趟（只量原生）用不到它，直接跳过。
            var orderSet = includePlugins ? GetOrderSet(order) : null;
            bool clockHandled = false, hardwareHandled = false, mediaHandled = false;

            for (int i = 0; i < order.Count; i++)
            {
                string item = order[i];
                if (string.Equals(item, Plugins.BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase))
                {
                    clockHandled = true;
                    if (CompShowDateTime) AddModule(_cachedTimeWidth + 12f + _cachedDateWidth);
                }
                else if (string.Equals(item, Plugins.BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase))
                {
                    hardwareHandled = true;
                    if (CompShowHardware) AddModule(MeasureHardwareBlockWidth());
                }
                else if (string.Equals(item, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase))
                {
                    mediaHandled = true;
                    if (CompShowMedia && media != null && media.IsActive && !SuppressMediaInIsland) AddModule(MeasureMediaBlockWidth(media));
                }
                else if (includePlugins)
                {
                    AddModule(SumPluginRowWidth(item)); // 插件组件组（只累计本帧被预算放行的组件）
                }
            }

            // 兜底：顺序表里尚未登记的内容按默认次序补上
            if (!clockHandled && CompShowDateTime) AddModule(_cachedTimeWidth + 12f + _cachedDateWidth);
            if (!hardwareHandled && CompShowHardware) AddModule(MeasureHardwareBlockWidth());
            if (!mediaHandled && CompShowMedia && media != null && media.IsActive && !SuppressMediaInIsland) AddModule(MeasureMediaBlockWidth(media));
            if (includePlugins) AddModule(SumPluginRowWidthNotIn(orderSet!));

            width += 16f; // 右侧边距与 Draw 中每模块尾距(16px)对齐，避免最后一个模块被裁切 6px

            return width;
        }

        private static HashSet<string>? _orderSetCache;
        private static IReadOnlyList<string>? _orderSetSource;

        private static HashSet<string> GetOrderSet(IReadOnlyList<string> order)
        {
            var cached = _orderSetCache;
            if (cached != null && ReferenceEquals(_orderSetSource, order)) return cached;

            cached = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
            _orderSetSource = order;   // 先写源再写缓存：极端竞态下最多多重建一次，不会读到不一致的集合
            _orderSetCache = cached;
            return cached;
        }

        private static void UpdateMediaState(MediaController media)
        {
            string liveTrans = media.CurrentLyricTranslation ?? "";
            if (_lastMediaTitle != media.Title || _lastMediaArtist != media.Artist || _lastLyric != media.CurrentLyric || _lastLyricTrans != liveTrans)
            {
                bool songChanged = _lastMediaTitle != media.Title || _lastMediaArtist != media.Artist;
                if (songChanged) { _prevLyric = ""; _prevLyricTrans = ""; }

                _lastMediaTitle = media.Title ?? "";
                _lastMediaArtist = media.Artist ?? "";

                // 触发叠化动画
                if (_lastLyric != media.CurrentLyric)
                {
                    _prevLyric = songChanged ? "" : _lastLyric;
                    _prevLyricTrans = songChanged ? "" : _lastLyricTrans;
                    _lastLyric = media.CurrentLyric ?? "";
                    _lastLyricTrans = liveTrans;
                    _lyricAnimProgress = 0f;
                    _lyricChangeTime = DateTime.Now;
                }
                else
                {
                    // 否则整行会为了一个「补上的小字」白抖 350ms。
                    _lastLyricTrans = liveTrans;
                }

                if (!string.IsNullOrEmpty(_lastLyric))
                {
                    _cachedMediaDisplay = _lastLyric;
                }
                else
                {
                    _cachedMediaDisplay = string.IsNullOrEmpty(_lastMediaArtist) ? _lastMediaTitle : $"{_lastMediaArtist} - {_lastMediaTitle}";
                }

                var metrics = _textPaint.FontMetrics;
                _cachedMediaTextTop = metrics.Ascent;
                _cachedMediaTextHeight = metrics.Descent - metrics.Ascent;
            }

            if (_lyricAnimProgress < 1f)
            {
                _lyricAnimProgress = (float)(DateTime.Now - _lyricChangeTime).TotalSeconds / 0.35f;
                if (_lyricAnimProgress > 1f) _lyricAnimProgress = 1f;
            }
        }

        private static void UpdateClockCache()
        {
        var now = DateTime.Now;
        if (_lastMinute != now.Minute)
        {
            _lastMinute = now.Minute;
            _cachedTimeStr = now.ToString("HH:mm"); // 00:00 24小时制
            _cachedDateStr = now.ToString("MM/dd"); // 月/日 格式
            _cachedTimeWidth = _timePaint.MeasureText(_cachedTimeStr);
            _cachedDateWidth = _datePaint.MeasureText(_cachedDateStr);
        }
        }

    }
}
