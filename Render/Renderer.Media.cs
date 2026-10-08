using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
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

        // ---- 播放控件命中几何（唯一真源） ----
        private const float MediaButtonSpacing = 54f;
        private const float MediaButtonRadius = 20f;
        private const float MediaButtonCenterOffsetY = 26f;

        private static float MediaButtonCenterY(float currentHeight) => currentHeight - MediaButtonCenterOffsetY;

        public static int HitExpandedButton(float x, float y, float currentHeight)
        {
            float centerX = WINDOW_WIDTH / 2f;
            float centerY = MediaButtonCenterY(currentHeight);

            for (int i = 0; i < 3; i++)
            {
                float cx = centerX + (i - 1) * MediaButtonSpacing;
                float dx = x - cx, dy = y - centerY;
                if (dx * dx + dy * dy <= MediaButtonRadius * MediaButtonRadius) return i;
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

        // ---- 展开态歌曲时间轴 ----
        private static float _tlBarX1, _tlBarX2, _tlBarY;

        private const float TL_SIDE_PAD = 22f;    // 时间文本距岛体左右边缘的留白

        private const float TL_TEXT_GAP = 8f;     // 时间文本与进度条之间的间距

        private const float TL_BOTTOM_GAP = 68f;  // 进度条距岛体底边的距离（加高 28px 后正好落在封面与按钮之间的空档）

        private const float TL_MIN_HEIGHT = 140f; // 高度涨到这条线之前不画，避免与底部按钮叠字

        // 刻意不再排除组合模式：组合模式现在也能展开媒体面板，

        public static bool TimelineVisible(MediaController media)
            => IsMediaExpanded && MediaInteractionMode == 1 && media.HasTimeline;

        public static float GetExpandedHeight(MediaController media) => TimelineVisible(media) ? 158f : 130f;

        public static bool HitTimeline(float x, float y)
            => _tlBarX2 > _tlBarX1 && x >= _tlBarX1 - 8f && x <= _tlBarX2 + 8f && Math.Abs(y - _tlBarY) <= 13f;

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
            => _tlBarX2 > _tlBarX1 ? Math.Clamp((x - _tlBarX1) / (_tlBarX2 - _tlBarX1), 0f, 1f) : 0f;

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

        private static void DrawTimeline(SKCanvas canvas, MediaController media, float left, float right, float currentHeight, byte alpha)
        {
            float barY = currentHeight - TL_BOTTOM_GAP;
            float baseline = barY + 3.6f;              // 10px 字号的视觉居中基线
            float padL = left + TL_SIDE_PAD, padR = right - TL_SIDE_PAD;

            _tlTextPaint.Color = _currentSubTextColor.WithAlpha(alpha);
            string elapsed = media.TimelineElapsed, total = media.TimelineTotal;
            canvas.DrawText(elapsed, padL, baseline, _tlTextPaint);
            float totalW = _tlTextPaint.MeasureText(total);
            canvas.DrawText(total, padR - totalW, baseline, _tlTextPaint);

            float x1 = padL + _tlTextPaint.MeasureText(elapsed) + TL_TEXT_GAP;
            float x2 = padR - totalW - TL_TEXT_GAP;
            if (x2 - x1 < 20f) return;                 // 岛体太窄：宁可不画，也不画一条糊掉的条

            float h = 3.5f, top = barY - h / 2f, radius = h / 2f;
            _barBgPaint.Color = _currentTextColor.WithAlpha((byte)(alpha * 0.22f));
            canvas.DrawRoundRect(new SKRect(x1, top, x2, top + h), radius, radius, _barBgPaint);

            float head = x1 + (x2 - x1) * media.TimelineProgress;
            _barPaint.Color = _currentTextColor.WithAlpha(alpha);
            if (head > x1) canvas.DrawRoundRect(new SKRect(x1, top, head, top + h), radius, radius, _barPaint);
            if (media.IsDragging) canvas.DrawCircle(head, barY, 4.5f, _barPaint); // 拖动时才亮出圆点：常态极简，操作时给足抓取反馈

            _tlBarX1 = x1; _tlBarX2 = x2; _tlBarY = barY;
        }

        private static void DrawKaraoke(SKCanvas canvas, string text, float x, float y, SKPaint paint, byte targetAlpha, float progress, bool isLyric)
        {
            SKTypeface baseTypeface = paint.Typeface;

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

            // 1. 先画完整的半透明底板 (40% 亮度)
            foreach (var run in runs)
            {
                paint.Typeface = run.Type;
                paint.Color = paint.Color.WithAlpha((byte)(targetAlpha * 0.4f));
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
            SKPaint paint, byte targetAlpha, float progress, bool isLyric)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool twoLines = isLyric && !string.IsNullOrEmpty(translation) && MediaController.IsTranslationEnabled;
            float mainY = twoLines ? y - LYRIC_TRANS_LINE_STEP * 0.5f : y;
            DrawKaraoke(canvas, text, x, mainY, paint, targetAlpha, progress, isLyric);

            if (!twoLines) return;

            byte transAlpha = (byte)(targetAlpha * 0.88f);
            var savedColor = paint.Color;
            paint.Color = _currentSubTextColor.WithAlpha(transAlpha);
            canvas.Save();
            canvas.Translate(x, y + LYRIC_TRANS_LINE_STEP * 0.5f);
            canvas.Scale(LYRIC_TRANS_SCALE, LYRIC_TRANS_SCALE);
            DrawKaraoke(canvas, translation, 0f, 0f, paint, transAlpha, 1f, false);
            canvas.Restore();
            paint.Color = savedColor;
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
            SetPluginRowBudget(MAX_ISLAND_WIDTH - MeasureCompositeWidth(media, includePlugins: false));

            return Math.Clamp(MeasureCompositeWidth(media, includePlugins: true), 60f, MAX_ISLAND_WIDTH);
        }

        public static float GetCompositeNativeWidth(MediaController media)
            => CompositeModeEnabled ? MeasureCompositeWidth(media, includePlugins: false) : 0f;

        private const float STANDBY_BLANK_WIDTH = 96f;

        private static float MeasureCompositeWidth(MediaController media, bool includePlugins)
        {
            if (StandbyActive)
            {
                if (StandbyScene == 2) return STANDBY_BLANK_WIDTH;

                float own = StandbyScene switch
                {
                    1 => _cachedTimeWidth + 12f + _cachedDateWidth,                              // 只显示时间
                    3 => media != null && media.IsActive ? MeasureMediaBlockWidth(media) : 0f,   // 折叠媒体控制
                    _ => 0f,
                };
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
                    if (CompShowMedia && media != null && media.IsActive) AddModule(MeasureMediaBlockWidth(media));
                }
                else if (includePlugins)
                {
                    AddModule(SumPluginRowWidth(item)); // 插件组件组（只累计本帧被预算放行的组件）
                }
            }

            // 兜底：顺序表里尚未登记的内容按默认次序补上
            if (!clockHandled && CompShowDateTime) AddModule(_cachedTimeWidth + 12f + _cachedDateWidth);
            if (!hardwareHandled && CompShowHardware) AddModule(MeasureHardwareBlockWidth());
            if (!mediaHandled && CompShowMedia && media != null && media.IsActive) AddModule(MeasureMediaBlockWidth(media));
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
