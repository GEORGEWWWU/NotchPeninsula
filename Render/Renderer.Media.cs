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
            // 复用卡拉OK run 缓存：组合模式 / 折叠模式每帧都会用同一句歌词调这里，
            // 命中缓存时连 MeasureText 都不做（稳定期零重算、零分配，也不打断渲染节奏）。
            if (ReuseCachedRuns(text, _semiBoldTypeface, out float cachedWidth)) return cachedWidth;
            return _textPaint.MeasureText(text);
        }

        public static bool IsMediaExpanded = false;

        public static int HoveredExpandedButton = -1; // -1:无, 0:上一首, 1:播放/暂停, 2:下一首

        // ==================== 🎵 展开态歌曲时间轴 ====================
        // 几何登记表：Draw 里「真的画了」才登记，帧首统一作废 —— 画与点因此共用同一套坐标，
        // 且 HitTimeline 返回 false 天然等价于「本帧没画」，收起 / 切状态时不会在空处误触发。
        private static float _tlBarX1, _tlBarX2, _tlBarY;

        private const float TL_SIDE_PAD = 22f;    // 时间文本距岛体左右边缘的留白

        private const float TL_TEXT_GAP = 8f;     // 时间文本与进度条之间的间距

        private const float TL_BOTTOM_GAP = 68f;  // 进度条距岛体底边的距离（加高 28px 后正好落在封面与按钮之间的空档）

        private const float TL_MIN_HEIGHT = 140f; // 高度涨到这条线之前不画，避免与底部按钮叠字

        // 显示门控：仅「纯媒体控制器（可点击展开）+ 灵动岛已展开 + 非组合模式 + SMTC 提供进度」时出现

        public static bool TimelineVisible(MediaController media)
            => IsMediaExpanded && MediaInteractionMode == 1 && !CompositeModeEnabled && media.HasTimeline;

        // 展开态高度：只有在「本帧真的会画时间轴」时才为它加高 28px 留位。
        // 直接交互模式下时间轴不画（见 TimelineVisible），右键展开出的媒体面板因此回到 130，
        // 不会在封面与底部按钮之间多出一段空档。调用方仅在 IsMediaExpanded 时取值。

        public static float GetExpandedHeight(MediaController media) => TimelineVisible(media) ? 158f : 130f;

        public static bool HitTimeline(float x, float y)
            => _tlBarX2 > _tlBarX1 && x >= _tlBarX1 - 8f && x <= _tlBarX2 + 8f && Math.Abs(y - _tlBarY) <= 13f;

        // ==================== 🎵 歌词翻译（上下两行） ====================
        // 译文画在原文正下方，视觉上「上下分开」：译文沿用原文那支画笔，只把颜色调成次级灰、
        // 再用画布缩放做小一号 —— 共用同一套字体 run 缓存，不额外占缓存槽，也不动 TextSize。
        // 注意：两行**不改变岛体高度**，是在原高度里把两条线各自上下让开半格挤出来的
        // （见 DrawLyricLine）：岛体尺寸恒定，歌词有没有译文都不会弹高弹低。
        // 间距按默认媒体高度 40px 调过：两行基线相距 15px 时，整块占用约 y=4.4→36，
        // 中文大字的上下都不打架，也不贴边；高度调小时它还是居中的，只是余量变小。

        private const float LYRIC_TRANS_LINE_STEP = 15f;  // 原文与译文两条基线的间距

        private const float LYRIC_TRANS_SCALE = 0.92f;    // 译文视觉缩放：12.5px → 约 11.5px（略小于原文，保持主次）

        /// <summary>
        /// 本帧是否要把译文作为第二行画出来：开关开启 + 正在显示歌词 + 这句确实有译文。
        /// 三者缺一不可 —— 否则会把译文贴到「歌手 - 歌名」下面。
        /// </summary>

        public static bool IsTranslationLineVisible(MediaController? media)
            => media != null
               && MediaController.IsTranslationEnabled
               && !string.IsNullOrEmpty(media.CurrentLyric)
               && !string.IsNullOrEmpty(media.CurrentLyricTranslation);

        /// <summary>译文行的排版宽度（已经折算过视觉缩放），供岛体自适应宽度使用。</summary>

        public static float MeasureLyricTranslationWidth(string text)
            => string.IsNullOrEmpty(text) ? 0f : MeasureCurrentLyricWidth(text) * LYRIC_TRANS_SCALE;

        // ==================== 🎵 折叠态媒体标题区：**故意没有右键热区** ====================
        // 这里曾经有一套「本帧真的画了标题文本才登记命中区、帧首统一作废」的机制（`_mediaTitleHit` +
        // `HitMediaTitle`），给「右键媒体标题展开媒体面板」用。但那个热区高度 = 整个岛体高、宽度 = 文字宽度，
        // 媒体控制器铺满岛体时几乎吃掉整片右键：用户想打开设置窗口得精确点到岛体最右侧那条窄边。
        // 用户 2026-09-19 要求「整个媒体控制器的右键都只打开设置窗口」，故整套机制已删除 ——
        // 原生媒体区域（标题 / 歌词 / 频谱 / 播放按钮 / 空白）的右键一律**不消费**（照旧打开设置窗口）。
        // 2026-09-23 追加：这里的右键只按区域决定「直达设置窗口的哪个页签」，
        // 命中区是渲染时登记的、贴着模块真实边界的 x 区间（见 Renderer.Layout.cs 的 NativeRightClickTab），
        // 依然不消费右键、也不覆盖整岛高度。以后要再加媒体区域右键行为，**不要**退回「覆盖整岛高度的大热区」。

        // 鼠标 x → 0~1 落点比例（与 HitTimeline 共用同一套坐标）

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

        // 硬件监控零 GC 缓存池 (预热101个字符串，避免每帧 ToString 分配内存)
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

        // 🎵 展开态歌曲时间轴：左「当前时间」+ 中间进度条 + 右「总时长」。
        // 纵向从岛体底边反推（currentHeight - TL_BOTTOM_GAP），随展开动画一起生长，天然落在封面与按钮之间。
        // 全程只用静态画笔与 SKRect 值类型，零分配；画完登记几何，供命中判定与落点换算共用。
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

        // 卡拉OK渲染引擎
        private static void DrawKaraoke(SKCanvas canvas, string text, float x, float y, SKPaint paint, byte targetAlpha, float progress, bool isLyric)
        {
            SKTypeface baseTypeface = paint.Typeface;

            // 缓存 runs：播放时段文本不变则直接复用，不重建，避免每帧 BuildTextRuns 拖慢渲染帧率导致时间刷新滞后
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

            // 如果没开启卡拉OK，直接短路渲染普通的实体文字，瞬间返回，0 性能开销
            if (!isLyric || progress <= 0f || !MediaController.IsKaraokeEnabled)
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

        /// <summary>
        /// 画一句歌词（含可选的译文第二行）。
        ///
        /// <paramref name="y"/> 传的是「整块文字（原文 + 译文）的竖向中心基线」：没有译文时就是原文基线，
        /// 与改造前的行为完全一致；有译文时原文上移半格、译文下移半格，两行以原来的基线为轴心上下分开。
        /// 译文复用调用方那支画笔（字体 run 缓存与原文共用，不额外占缓存槽），
        /// 颜色改成次级灰、字号交给画布缩放 —— 直接改 TextSize 会让缓存里的宽度度量失效。
        /// </summary>

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

        /// <summary>硬件占用模块在组合模式下的占宽（CPU 组 + 16px + RAM 组）。</summary>

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

        /// <summary>
        /// 媒体模块在组合模式下的占宽（缩略图 + 文本 + 间距 + 频谱）。
        ///
        /// <b>不封顶</b>（2026-09-20 删掉了原先的 <c>CompositeMediaMaxWidth</c>）：用户要求媒体控制器长度全放开，
        /// 文本按真实内容计宽，超出部分只受 <see cref="MAX_ISLAND_WIDTH"/> 约束（而它已放宽到 1920）。
        /// 组合模式总宽仍由 <see cref="GetCompositeWidth"/> 收口，且插件行预算是「总长上限 − 原生总宽」，
        /// 所以本值变大只会让岛体变长、不会把插件挤没（前提是原生总宽还没吃满总长上限）。
        /// </summary>

        private static float MeasureMediaBlockWidth(MediaController? media)
        {
            float textWidth = (!string.IsNullOrEmpty(media?.CurrentLyric) && MediaController.IsLyricsEnabled)
                ? _textPaint.MeasureText(media!.CurrentLyric)
                : (string.IsNullOrEmpty(media?.Artist)
                    ? _textPaint.MeasureText(media?.Title)
                    : _textPaint.MeasureText(media!.Artist) + _textPaint.MeasureText(media.Title) + 15f);

            // 译文第二行若更宽，按它计宽（与折叠态的自适应宽度口径一致）
            if (IsTranslationLineVisible(media))
                textWidth = Math.Max(textWidth, _textPaint.MeasureText(media!.CurrentLyricTranslation) * LYRIC_TRANS_SCALE);

            float thumbW = media?.Thumbnail != null ? 32f : 0f;
            return thumbW + textWidth + 12f + 21.2f;
        }

        /// <summary>
        /// 组合模式总宽：按「内容顺序表」把原生模块与插件组件依次累加，与 Renderer.Draw 的混排保持一致。
        ///
        /// 插件组件不是无条件计入的：先单独量出一整行「原生模块」的总宽，据此定出插件行的宽度预算
        /// （岛体总长上限 − 原生总宽），再按同一预算规则决定哪些组件能完整显示。
        /// 放不下的组件既不计宽也不绘制 —— 与「内容显示不全就不显示」保持一致。
        /// </summary>

        public static float GetCompositeWidth(MediaController media)
        {
            if (!CompositeModeEnabled) return STANDBY_WIDTH;

            RefreshPluginWidgets(); // 插件宽度需与快照同版本，才能算准总宽

            // 第一趟：只量原生模块，定出插件行还能用多少宽度
            SetPluginRowBudget(MAX_ISLAND_WIDTH - MeasureCompositeWidth(media, includePlugins: false));

            // 第二趟：含插件的总宽（放行结果已写进 _pluginVisible，SumPluginRowWidth 会按它过滤）
            return Math.Clamp(MeasureCompositeWidth(media, includePlugins: true), 60f, MAX_ISLAND_WIDTH);
        }

        /// <summary>
        /// 组合模式下「一整行原生模块」的总宽（不含任何插件组件）。
        /// 宿主用它给插件行定宽度预算：岛体总长上限 − 这个值 = 插件行可用的空间。
        /// 只是一串宽度相加，没有副作用，可以安全地在定预算时先调一次。
        /// </summary>

        public static float GetCompositeNativeWidth(MediaController media)
            => CompositeModeEnabled ? MeasureCompositeWidth(media, includePlugins: false) : 0f;

        /// <summary>
        /// 组合模式宽度累加本体。<paramref name="includePlugins"/> 为 false 时跳过插件组件组，
        /// 专门用来量「原生模块总宽」，好给插件行定预算。
        /// </summary>

        private static float MeasureCompositeWidth(MediaController media, bool includePlugins)
        {
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
            // 顺序表的插件 ID 集合，供结尾兜底去重（只补「没进表」的插件，已入表的绝不重复计宽）；
            // 第一趟（只量原生）用不到它，直接跳过分配
            var orderSet = includePlugins ? new HashSet<string>(order, StringComparer.OrdinalIgnoreCase) : null;
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
            // 插件兜底：只补「不在顺序表里」的插件宽度（与 Draw 的未绘制兜底一致），已入表的已被主循环累计，绝不重复
            if (includePlugins) AddModule(SumPluginRowWidthNotIn(orderSet!));

            width += 16f; // 右侧边距与 Draw 中每模块尾距(16px)对齐，避免最后一个模块被裁切 6px

            return width;
        }

        // 媒体激活时刷新「当前显示文本 / 歌词 / 叠化动画」的缓存。
        // 只在歌曲、歌词或译文真的变了的时候重算，渲染路径每帧调用也不会产生额外开销。
        private static void UpdateMediaState(MediaController media)
        {
            string liveTrans = media.CurrentLyricTranslation ?? "";
            if (_lastMediaTitle != media.Title || _lastMediaArtist != media.Artist || _lastLyric != media.CurrentLyric || _lastLyricTrans != liveTrans)
            {
                // 换歌：整块歌词状态强制重载。_prevLyric 是叠化动画的「淡出层」，
                // 不清掉的话上一首的最后一句会被带到新歌的第一帧上 —— 切歌残留的视觉来源。
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
                    // 原文没变、只有译文姗姗来迟（异步抓到的翻译 LRC）：直接换上，不触发叠化，
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

            // 纯数学计算动画插值 (0.0 -> 1.0，周期约 350ms)，零 GC 分配
            if (_lyricAnimProgress < 1f)
            {
                _lyricAnimProgress = (float)(DateTime.Now - _lyricChangeTime).TotalSeconds / 0.35f;
                if (_lyricAnimProgress > 1f) _lyricAnimProgress = 1f;
            }
        }

        // 时间日期缓存：与媒体是否激活无关，组合模式 / 待机每帧都保证就绪。
        // 启动即播放音乐或媒体全程激活时，之前时钟会因缓存一直为空而「消失」，现改为始终照常跳分钟。
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
