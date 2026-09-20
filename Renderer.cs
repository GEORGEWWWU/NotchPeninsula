using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // 1. 布局核心参数 (改为无锁动态变量)
        private static volatile float _standbyWidth = 125f;
        private static volatile float _baseHeight = 29f;
        private static volatile float _mediaWidth = 250f;
        private static volatile float _mediaHeight = 35f;
        private static volatile float _toastWidth = 260f;
        private static volatile float _toastHeight = 55f;
        private static volatile float _globalDpi = 1.0f;
        private static volatile float _notchBottomRadius = 12f;

        public static float STANDBY_WIDTH { get => _standbyWidth; set => _standbyWidth = value; }
        public static float BASE_HEIGHT { get => _baseHeight; set => _baseHeight = value; }
        public static float MEDIA_WIDTH { get => _mediaWidth; set => _mediaWidth = value; }
        public static float MEDIA_HEIGHT { get => _mediaHeight; set => _mediaHeight = value; }
        public static float TOAST_WIDTH { get => _toastWidth; set => _toastWidth = value; }
        public static float TOAST_HEIGHT { get => _toastHeight; set => _toastHeight = value; }
        // 消息通知内容：false=缩略(默认，仅 icon+发送者+主体)，true=完整(icon+应用名+发送者+主体+右上角“现在”)
        public static bool IsToastFullMode = false;
        // 紧凑模式：尺寸与缩略一致，但右侧靠边显示双行信息（右上“现在”、右下应用名），左侧文本过长时用遮罩过渡
        public static bool IsToastCompactMode = false;
        // 紧凑模式下右侧双行信息预留宽度
        public static readonly float COMPACT_RIGHT_WIDTH = 90f;
        // 完整模式下的消息通知最小尺寸（默认缩略为 260x55，完整需更长更高以容纳应用名）
        public static readonly float FULL_TOAST_MIN_WIDTH = 300f;
        public static readonly float FULL_TOAST_MIN_HEIGHT = 72f;
        public static float GLOBAL_DPI { get => _globalDpi; set => _globalDpi = value; }
        public static float NOTCH_BOTTOM_RADIUS { get => _notchBottomRadius; set => _notchBottomRadius = value; }
        public static int ThemeMode { get; set; } = 0; // 0=黑, 1=白, 2=跟随系统
        public static int NotchStyle { get; set; } = 0; // 0=经典刘海, 1=灵动岛
        public static int StandbyDisplayMode { get; set; } = 0; // 0=时间日期, 1=空白
        public static int TargetMonitorIndex { get; set; } = 0; // 目标显示器索引
        public static int BgOpacityLevel { get; set; } = 4; // 透明度档位：0=0%, 1=25%, 2=50%, 3=75%, 4=100%
        public static bool CompositeModeEnabled { get; set; } = false; // 自定义组合模式总开关
        public static bool CompShowDateTime { get; set; } = true;  // 显示时间日期
        public static bool CompShowHardware { get; set; } = true;  // 显示硬件占用
        public static bool CompShowMedia { get; set; } = true;     // 显示媒体控制器(含频谱)
        public static bool PassthroughModeEnabled = false; // 穿透模式总开关
        public static float PassthroughAlpha = 1.0f; // 穿透动画平滑插值
        private static readonly SKPaint _layerPaint = new SKPaint(); // 零GC硬件级透明图层
        // 唤醒按钮：**面性（实心）底座 + 线条图标**。原先的「两个同心圆」是线条图形，既单薄又看不出可点击；
        // 现在底座换成深色圆角芯片（面性、有实体感），图标换成「两个线条折角箭头指向对角」。
        // 试错记录：实心三角头 + 实心杆太笨重（否）；鼠标指针 —— 屏幕上本来就有真指针，多一个很怪（否）。
        // 芯片在深色桌面上几乎隐形，此时退化成一枚白色折角箭头，同样成立；
        // 浅色桌面上则靠深色芯片立住对比（纯白图形落在白色壁纸上会直接消失）。
        private static readonly SKPaint _wakePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true }; // 折角箭头（线条）
        private static readonly SKPaint _wakeChipPaint = new SKPaint { Color = SKColors.Black.WithAlpha(WakeChipAlpha), Style = SKPaintStyle.Fill, IsAntialias = true }; // 芯片底色
        private static readonly SKPaint _wakeHitPaint = new SKPaint { Style = SKPaintStyle.Fill }; // 隐形物理热区底板
        private static readonly SKPath _wakePath = CreateWakePath();
        private static readonly SKPath _wakeChipPath = CreateWakeChipPath();

        // 芯片基准不透明度（0-255）。再深一点会显得像一块实心补丁，再浅一点在浅色壁纸上就撑不住对比。
        private const byte WakeChipAlpha = 158;

        // 穿透唤醒按钮的边长（逻辑坐标）。
        public const float WAKE_BTN_SIZE = 36f;

        // 穿透唤醒按钮的水平位置 —— **唯一真源**。岛体本身水平居中，所以化简后
        // `(WINDOW_WIDTH - 岛宽)/2 + (岛宽 - 36)/2` 就等于 `(WINDOW_WIDTH - 36)/2`，与岛宽无关。
        // 渲染 / 鼠标命中 / 手型指针三处必须都用它，别再各算一份 ——
        // 之前三处各写了一份，改位置时漏掉 WM_MOUSEMOVE 那处，
        // 结果按钮移到中心了、手型指针还留在左边缘（即「hover 按钮没有小手」）。
        public static float WakeButtonX => (WINDOW_WIDTH - WAKE_BTN_SIZE) / 2f;

        // 芯片：36×36 画布内缩 5px 的 26×26 圆角方块，圆角与岛体语言保持一致
        private static SKPath CreateWakeChipPath()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(5f, 5f, 31f, 31f), 8f, 8f);
            return path;
        }

        // 唤醒按钮图标：**两个线条折角箭头指向对角**（就是 `>` `>` 那种 V 形折角，不带杆）。
        // 每个箭头只画折角两笔：尖角落在对角方向，一臂竖直、一臂水平，各自延伸到画布中线 ——
        // 这样两臂与画布中线对齐，视觉重心稳，不会显得偏。不带杆是刻意的：中间留白让图标变轻，
        // 也避免两段斜线连成一根粗斜杠（那样就认不出是箭头了）。
        // 尺寸：尖角距画布中心 6.5（即 (24.5, 11.5)），臂长也是 6.5，尖角离芯片边缘 6.5 ——
        // 之前用的 8.5 太大、离边缘只有 4.5，缩到 6.5 后四周留白舒服了。
        // 左下折角是右上折角关于画布中心 (18,18) 的点对称，所以两组坐标互为 36-x / 36-y。
        // 线条用 Stroke 画笔绘制（2.8px、圆头圆角），坐标同样烘焙进路径。
        private static SKPath CreateWakePath()
        {
            var path = new SKPath();

            // 右上折角：尖角 (24.5, 11.5)，两臂各 6.5，分别落到横竖中线
            path.MoveTo(24.50f, 18.00f);  // 竖直臂末端（落在水平中线）
            path.LineTo(24.50f, 11.50f);  // 尖角
            path.LineTo(18.00f, 11.50f);  // 水平臂末端（落在垂直中线）

            // 左下折角：上面的点对称
            path.MoveTo(11.50f, 18.00f);
            path.LineTo(11.50f, 24.50f);
            path.LineTo(18.00f, 24.50f);

            return path;
        }

        // 媒体交互状态：0=直接交互，1=展开交互(默认)
        public static int MediaInteractionMode = 1;
        public static float MeasureCurrentLyricWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // 复用卡拉OK run 缓存：组合模式 / 折叠模式每帧都会用同一句歌词调这里，
            // 命中缓存时连 MeasureText 都不做（稳定期零重算、零分配，也不打断渲染节奏）。
            if (ReuseCachedRuns(text, _semiBoldTypeface, out float cachedWidth)) return cachedWidth;
            return _textPaint.MeasureText(text);
        }
        /// <summary>
        /// 岛体总长度上限：Toast / 剪贴板面板的自适应宽度、组合模式总宽、以及插件行的取舍都以它封顶。
        ///
        /// <para>
        /// <b>2026-09-20 由 800 放开到 1920（用户要求）</b>：用户原话「必须放开最大长度，灵动岛本体哪怕
        /// 宽度 max=1920 都无所谓，宁愿灵动岛超长溢出屏幕都不要被裁切」。
        /// 旧的 800 是「怕挤压到右边的插件」而设的，但实际效果是**长歌词被裁切**，
        /// 而且插件行预算（= 本值 − 原生内容宽度）被长歌词吃光后，插件会**直接整帧不显示**
        /// （不是被压缩，是彻底消失），体验很差 —— 这个顾虑被证明完全没必要。
        /// </para>
        ///
        /// <para>
        /// 1920 是**本体**的上限（≈ 106 个汉字，任何真实歌词行都远达不到）。
        /// 它同时也是窗口内容区的下限来源：<see cref="WINDOW_WIDTH"/> 必须 ≥ 本值，
        /// 否则岛体超出窗口的部分会被窗口边缘裁掉（那就又变成裁切了）。
        /// 岛体允许溢出屏幕 —— 窗口比屏幕宽是合法的，透明像素照常鼠标穿透。
        /// </para>
        /// </summary>
        public const float MAX_ISLAND_WIDTH = 1920f;

        // ⛔ 2026-09-20 用户明确要求「媒体控制器的长度也放开，多长都无所谓」，因此删掉了两个上限常量：
        //    · MEDIA_TEXT_MAX_WIDTH（默认 480 ≈ 27 个汉字）—— 非组合模式的媒体文本区上限
        //    · CompositeMediaMaxWidth（默认 460 ≈ 21 个汉字）—— 组合模式媒体模块的占宽上限
        //    这两个才是「歌词一长就被裁切」的真正元凶（它们都比 MAX_ISLAND_WIDTH 小得多，长歌词先撞到它们），
        //    而且把原生内容宽度钉死/压低后，插件行预算（= MAX_ISLAND_WIDTH − 原生宽度）被吃光，
        //    装不下的插件会**整帧不显示**（不是压缩，是彻底消失）。
        //    ⚠️ 不要再以「防止挤压插件」为由把它们加回来 —— 插件该不该显示由插件行预算决定，
        //       而岛体该多长就多长（上限见 MAX_ISLAND_WIDTH）。

        /// <summary>
        /// Toast 文本右边界与渐隐遮罩起点之间的兜底余量（逻辑像素）。
        ///
        /// 宽度公式与 <c>Draw</c> 里的排版是两套独立计算：文字宽由 <c>BuildTextRuns</c> 分 run 累加，
        /// 绘制时又按 run 逐段画（跨 run 的 kerning 会丢一点），再加上岛体宽度是弹簧动画、
        /// 可能稳定在目标值下方零点几像素 —— 余量取 0 时就会出现「刚好卡在遮罩边缘」：
        /// 文字既显示不全（末尾被渐隐吃掉），又因为缓存宽度认为放得下而不触发加宽。
        /// </summary>
        private const float TOAST_TEXT_MARGIN = 8f;

        /// <summary>完整模式右上角「现在」的预留宽度，必须与 <c>Draw</c> 里 <c>toastMaxTextRight -= 36f</c> 一致。</summary>
        private const float TOAST_FULL_MODE_RIGHT_RESERVE = 36f;

        // 计算Toast消息自适应宽度，限制最大宽度（与岛体总长上限一致）
        public static float GetToastAutoWidth()
        {
            float maxTextW = IsToastFullMode
                ? Math.Max(_cachedToastTitleWidth, Math.Max(_cachedToastBodyWidth, _cachedToastAppNameWidth))
                : Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            // 68 = 左侧 chrome（14 左边距 + 28 图标 + 10 间距）+ 右侧 16 内边距，
            // 与 Draw 里 toastTextX / toastMaxTextRight 的取值严格对应，改一处必须同步另一处。
            float w = maxTextW + 68f;
            // 🩹 完整模式右上角要放「现在」：Draw 里让了 36px，这里必须一起让，
            //    否则文本右边界永远比遮罩起点多出 36px —— 每行末尾都会被渐隐截掉一截。
            if (IsToastFullMode) w += TOAST_FULL_MODE_RIGHT_RESERVE;
            // 紧凑模式：左侧文本之外还需为右侧双行信息（现在 + 应用名）预留空间。
            // 取「固定预留」与「实测占宽」的较大者：应用名较长时按实测值预留，
            // 否则右侧信息会实际压进左侧消息文字里，把消息尾巴挤到遮罩下面。
            if (IsToastCompactMode) w += Math.Max(COMPACT_RIGHT_WIDTH, _cachedToastCompactRightWidth);
            // 🩹 兜底余量：保证文本右边界不会正好落在遮罩起点上（见 TOAST_TEXT_MARGIN 注释）
            w += TOAST_TEXT_MARGIN;
            return Math.Min(Math.Max(TOAST_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        // 📋 剪贴板链接面板自适应宽度：媒体控制器同款基准尺寸，链接过长时按文本加宽，
        //    封顶宽度与消息通知弹窗的最大长度保持一致
        private const float CLIPBOARD_EXTRA_WIDTH = 10f; // 计算宽度之外的视觉呼吸量，避免文本贴边
        public static float GetClipboardAutoWidth(string url)
        {
            EnsureClipboardTextCache(url);
            float w = 14f + 20f + 10f + _cachedClipboardTextWidth + 10f + 22f + 14f + CLIPBOARD_EXTRA_WIDTH;
            return Math.Min(Math.Max(MEDIA_WIDTH, w), MAX_ISLAND_WIDTH);
        }

        // 📋 「打开」按钮命中判定：本帧未绘制则热区为空，天然不会在收起后误触发
        public static bool HitClipboardOpen(float x, float y)
            => _clipboardOpenHit.Width > 0f && _clipboardOpenHit.Contains(x, y);

        private static void EnsureClipboardTextCache(string url)
        {
            if (_lastClipboardUrl == url) return;
            _lastClipboardUrl = url ?? "";
            BuildTextRuns(_lastClipboardUrl, _textPaint, _semiBoldTypeface, _cachedClipboardRuns, out _cachedClipboardTextWidth);
        }

        // 📋 剪贴板链接面板：左「链接图标」+ 中间链接 + 右「打开」按钮（尺寸与媒体控制器同款）
        private static void DrawClipboard(SKCanvas canvas, string url, float left, float right, float currentHeight, float textOffsetY)
        {
            EnsureClipboardTextCache(url);

            // 左侧「链接」图标：细线条矢量路径，颜色跟随主题的纯黑 / 纯白。
            // （原先是 data/image/clipboard.png 位图 + 圆角裁切，现改为矢量直绘：任意 DPI 都锐利、
            //   不再有位图缩放的毛边，也不再需要裁切路径。）
            float iconSize = 20f;
            float iconX = left + 14f;
            float iconY = (currentHeight - iconSize) / 2f + textOffsetY;
            DrawSvgPath(canvas, _clipboardLinkPaint, iconX, iconY, _clipboardLinkPath, iconSize / ClipboardIconCanvas);

            // 右侧「打开」按钮布局（先算坐标，按钮本体在文本之后绘制，保证永远压在最上层不被遮挡）
            float btnSize = 22f;
            float btnRight = right - 14f;
            float btnLeft = btnRight - btnSize;
            float btnTop = (currentHeight - btnSize) / 2f + textOffsetY;
            _clipboardOpenHit = new SKRect(btnLeft - 4f, btnTop - 3f, btnRight + 4f, btnTop + btnSize + 3f);

            // 中间链接文本（单行垂直居中）。
            // 关键保护：把文本严格裁剪在 [textX, btnLeft-10] 区域内，超长只渐隐截断文字，
            // 绝不绘制到按钮热区上 —— 任何岛体宽度（含弹簧动画过程中）都不会遮挡「打开」按钮。
            float textX = iconX + iconSize + 10f;
            float textRightLimit = btnLeft - 10f;
            float textY = currentHeight / 2f + _textPaint.TextSize * 0.36f + textOffsetY;
            canvas.Save();
            canvas.ClipRect(new SKRect(textX, 0f, textRightLimit, currentHeight), SKClipOperation.Intersect, false);
            foreach (var run in _cachedClipboardRuns)
            {
                _textPaint.Typeface = run.Type;
                canvas.DrawText(run.Text, textX + run.X, textY, _textPaint);
            }
            _textPaint.Typeface = _semiBoldTypeface; // 重置，防污染

            if (textX + _cachedClipboardTextWidth > textRightLimit)
            {
                float fadeWidth = 15f;
                float fadeStart = textRightLimit - fadeWidth;
                canvas.Save();
                canvas.Translate(fadeStart, 0);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();
            }
            canvas.Restore(); // 结束文本裁剪区

            // 按钮最后绘制：即使动画中途岛体宽度暂时不足，按钮也完整可见可点
            DrawSvgPath(canvas, _clipboardOpenPaint, btnLeft, btnTop, _clipboardOpenPath, btnSize / ClipboardIconCanvas);
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
        // 原生媒体区域（标题 / 歌词 / 频谱 / 播放按钮 / 空白）的右键一律不消费。
        // 以后若要重新加媒体区域的右键行为，**不要**再用「覆盖整岛高度的大热区」，改成明确的小按钮热区。

        // 鼠标 x → 0~1 落点比例（与 HitTimeline 共用同一套坐标）
        public static float TimelineRatio(float x)
            => _tlBarX2 > _tlBarX1 ? Math.Clamp((x - _tlBarX1) / (_tlBarX2 - _tlBarX1), 0f, 1f) : 0f;

        private static readonly SKPaint _hoverCirclePaint = new() { IsAntialias = true }; // 零 GC 纯色画笔
        private static SKColor _currentTextColor = SKColors.White;
        private static SKColor _currentSubTextColor = new SKColor(200, 200, 200);
        public static void ApplyThemeColors() // 刷新颜色的方法
        {
            bool isLight = ThemeMode == 1;
            if (ThemeMode == 2) // 跟随系统
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (key != null && key.GetValue("AppsUseLightTheme") is int val) isLight = val == 1;
                }
                catch { }
            }

            // 预计算颜色，避免在渲染树中生成新对象
            byte bgAlpha = (byte)(BgOpacityLevel * 255 / 4); // 计算5个档位对应的透明度值(0~255)
            var baseBg = isLight ? SKColors.White : SKColors.Black;
            var bg = baseBg.WithAlpha(bgAlpha); // 只改变背景色的透明度，不影响内部元素
            _currentTextColor = isLight ? SKColors.Black : SKColors.White;
            _currentSubTextColor = isLight ? new SKColor(80, 80, 80) : new SKColor(200, 200, 200);

            // 直接复写已存在的静态画笔属性 (极致内存复用)
            _bgPaint.Color = bg;
            _titlePaint.Color = _currentTextColor;
            _bodyPaint.Color = _currentSubTextColor;
            _textPaint.Color = _currentTextColor;
            _timePaint.Color = _currentTextColor;
            _datePaint.Color = _currentSubTextColor;
            _compactTimePaint.Color = _currentTextColor;   // “现在”纯黑纯白
            _compactAppPaint.Color = _currentSubTextColor; // 应用名灰色
            _mediaIconPaint.Color = _currentTextColor;
            _barPaint.Color = _currentTextColor;
            _tlTextPaint.Color = _currentSubTextColor; // 🎵 时间轴时间文本
            _shadowPaint.Color = _currentTextColor.WithAlpha(50);
            // 绑定悬浮圆圈底色为文字颜色的 25% 透明度，实现系统级无缝浅色适配
            _hoverCirclePaint.Color = _currentTextColor.WithAlpha(25);

            // 渐变着色器需要重新生成一次，但必须先手动释放旧的，防止非托管内存泄漏
            _fadePaint.Shader?.Dispose();
            _fadePaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(1, 0),
                [bg.WithAlpha(0), bg],
                null, SKShaderTileMode.Clamp);

            _tagTextPaint.Color = _currentTextColor;
            _tagBgPaint.Color = _currentTextColor.WithAlpha(25);  // 浅色半透明背景标签
            _barBgPaint.Color = _currentTextColor.WithAlpha(30);   // 未填充进度条的半透明纯色底槽
        }

        /// <summary>
        /// 把当前主题/DPI 快照成插件侧可用的只读结构体（供 IPluginHost.CurrentTheme 使用）。
        /// 插件在渲染线程读取，这里只做值拷贝，不含任何共享可变状态。
        /// </summary>
        public static Plugins.RenderTheme GetCurrentTheme()
            => new Plugins.RenderTheme(_currentTextColor, _currentSubTextColor, _bgPaint.Color, GLOBAL_DPI, NOTCH_BOTTOM_RADIUS);

        // ================= 🧩 插件组件渲染接线 =================
        // 设计目标：稳态 60FPS 零 GC 分配。
        //   · 组件数组只在注册表版本变化时拷贝一次（_pluginWidgets）；
        //   · 每帧的宽度写入复用数组（_pluginWidths）；
        //   · 命中矩形复用同一个 List（_pluginSlots），绘制与鼠标分发共用；
        //   · 帧上下文 WidgetFrame / RenderTheme 均为 struct，栈上传递不进堆。
        // 插件 Draw / MeasureWidth 抛异常会被熔断（_pluginBroken），只记一次日志，绝不拖死渲染循环。
        private static Plugins.IWidget[]? _pluginWidgets;
        private static int _pluginWidgetsVersion = -1;
        private static float[]? _pluginWidths;
        private static bool[]? _pluginBroken;
        // 本帧绘制标记：组合模式下每个组件只在「内容顺序表」里它自己的位置画一次
        private static bool[]? _pluginDrawn;
        // 组合模式下媒体模块的右边界（供 UI 线程判定媒体按钮/悬停命中，避免窗口宽度换算误差）
        private static float _compositeMediaRight = -1f;
        private static readonly List<Plugins.WidgetLayout.Slot> _pluginSlots = new(8);

        // 🧩 插件行的宽度预算：本帧插件行最多能用多少宽度（含与原生内容之间的 16px 间距）。
        //    由 NotchWindow 每帧按「岛体总长上限 − 原生内容本帧占用宽度」算出后写入；
        //    组合模式由 GetCompositeWidth 内部按同一规则设置（那里才知道原生模块总宽）。
        //    预算内放不下的组件本帧整体不显示 —— 不压缩、不截断，杜绝文字被省略号砍掉半截。
        //    默认 +∞：宿主还没跑到判定逻辑时（启动首帧等），插件照常按自身所需宽度显示。
        private static float _pluginRowBudget = float.PositiveInfinity;
        // 预算判定结果，与 _pluginWidths 同序、同版本：true = 本帧给了这个组件它要的完整宽度。
        private static bool[]? _pluginVisible;
        // 本帧实际放行的插件行总宽（含与原生内容的 16px 间距）；0 = 一个组件都没放行。
        private static float _pluginRowReserve;

        /// <summary>插件行组件间距，也是插件行与原生内容之间的间距（与 Draw 里 <c>right + 16f</c> 对齐）。</summary>
        private const float PLUGIN_GAP = 16f;

        /// <summary>
        /// 原生内容区在任何情况下都要保住的最小宽度（**常量**，故意不跟原生内容实际所需宽度挂钩）。
        ///
        /// 只在「岛体宽度动画途中装不下两侧插件组」时用来给插件组按比例让位 ——
        /// 不设这个下限的话会算出负的原生内容区宽度，把文字遮罩与播放按钮翻到文字左边。
        ///
        /// ⚠️ 为什么必须是常量、不能换成「本帧原生内容真实所需宽度」：
        ///    原生内容所需宽度（换歌词 / 换标题时）与目标宽度是**同一刻跳变**的，而 currentWidth
        ///    还停在旧目标上。拿它当下限 → 换歌词那一帧立刻判定「装不下」→ 原生内容区从 320
        ///    一步拉到 512、插件整行被裁掉 —— 这就是又一次跳变（实测首帧 18px、全程 24px）。
        ///    常量下限则只在「岛体比插件行还窄」的极端瞬态才介入，换歌词时完全不介入，
        ///    边界只跟着岛体边缘平滑移动。用户 2026-09-20 反馈的「闪现」正是要保证这一点。
        /// </summary>
        private const float MIN_NATIVE_AREA = 80f;

        /// <summary>
        /// 设置本帧插件行的宽度预算（含与原生内容之间的 16px 间距）。
        ///
        /// 判定权在 NotchWindow：只有它知道原生内容（媒体控制器 / 长歌词自适应 / 硬件占用）
        /// 本帧要占多宽，用岛体总长上限减掉之后，剩下的才是插件行能用的空间。
        ///
        /// <para>
        /// 组件宽度是各自声明的（<see cref="Plugins.IWidget.MeasureWidth"/> 的语义 =
        /// <b>完整显示内容所需的宽度</b>）。本方法按注册顺序贪心分配：
        /// 所需宽度能完整落进剩余预算的组件才显示，装不下的组件本帧整体不显示 ——
        /// 宿主绝不替它压缩或截断（那才是「内容显示不全」）。
        /// 于是每个组件要么完整显示、要么完全不显示，不存在半截内容。
        /// </para>
        ///
        /// 传 0 即整行隐藏（通知 / 剪贴板 / 详情页接管岛体时）。
        /// 只影响「插件行贴在原生内容右侧」的非组合模式；组合模式由 <c>GetCompositeWidth</c>
        /// 内部按同一预算规则处理，不需要外部调用。
        /// </summary>
        public static void SetPluginRowBudget(float availableWidth)
        {
            lock (_pluginSnapshotLock)
            {
                _pluginRowBudget = float.IsNaN(availableWidth) || availableWidth < 0f ? 0f : availableWidth;
            }
            RefreshPluginWidgets(); // 版本变化时内部会重算一次；没变化则由下面这行重算
            lock (_pluginSnapshotLock) RecomputePluginVisibility();
        }

        /// <summary>
        /// <b>兼容旧调用</b>：等价于「给整行插件 +∞ 宽度」或「一点宽度都不给」。
        ///
        /// 判定逻辑在 <see cref="SetPluginRowBudget"/> 改成了按「组件所需宽度能否完整落进剩余空间」
        /// 逐个放行，本方法只是给旧的布尔口径留一个等价出口，宿主自身已不再调用。
        /// </summary>
        public static void SetPluginRowVisible(bool visible)
            => SetPluginRowBudget(visible ? float.PositiveInfinity : 0f);

        /// <summary>
        /// 按当前预算重算每个组件的放行情况（调用方须持有 <see cref="_pluginSnapshotLock"/>）。
        ///
        /// 贪心：按注册顺序逐个体判断「已用宽度 + 16px 间距 + 它要的宽度」是否还在预算内；
        /// 放不下就跳过它（不占宽度、不绘制、无命中区），后面的组件仍可继续尝试。
        /// 稳态（预算与内容都不变）下只在「值真的变了」时才写，零分配、零无效写入。
        /// </summary>
        private static void RecomputePluginVisibility()
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null || visible == null) return;
            if (visible.Length != widgets.Length || widths.Length != widgets.Length || broken.Length != widgets.Length) return;

            float budget = _pluginRowBudget;
            float used = 0f;

            for (int i = 0; i < widgets.Length; i++)
            {
                bool ok = !broken[i] && widths[i] > 0f && used + PLUGIN_GAP + widths[i] <= budget;
                if (ok) used += PLUGIN_GAP + widths[i];
                if (visible[i] != ok) visible[i] = ok; // 只在变化时写：bool 写入原子，渲染线程不会看到中间态
            }

            if (_pluginRowReserve != used) _pluginRowReserve = used;
        }
        private static readonly object _pluginSlotLock = new();
        // 组件快照/测量的锁：渲染线程与 UI 线程（鼠标命中路径会查询预留宽度）都可能访问
        private static readonly object _pluginSnapshotLock = new();
        // 鼠标逻辑坐标（相对窗口左上角），-1 表示鼠标不在灵动岛上
        private static float _pluginMouseX = -1f;
        private static float _pluginMouseY = -1f;

        /// <summary>NotchWindow 在 WM_MOUSEMOVE 中记录鼠标逻辑坐标；鼠标离开时传 (-1,-1)。</summary>
        public static void UpdatePluginMouse(float x, float y)
        {
            _pluginMouseX = x;
            _pluginMouseY = y;
        }

        /// <summary>
        /// 插件组件行独立占据岛体最右侧所需的预留宽度（含与原生内容的 16px 间距）。
        /// 返回 0 表示本帧没有任何组件被放行（没有可显示的插件，或预算放不下任何一个）。
        ///
        /// 放行结果由 <see cref="SetPluginRowBudget"/> 按「组件声明的所需宽度能否完整落进剩余空间」判定：
        /// 装不下的组件直接不出现在这一帧，而不是被压缩显示。
        ///
        /// 原生内容据此内收右边界，因此插件显示与否、排序如何，都完全不影响任何原生功能。
        /// </summary>
        public static float GetPluginRowReserve()
        {
            if (!RefreshPluginWidgets()) return 0f;
            return _pluginRowReserve;
        }

        /// <summary>
        /// 本帧插件行的可用宽度（含与原生内容之间的 16px 间距）——即「岛体总长上限 − 原生内容本帧占用宽度」。
        ///
        /// 这是**整行**的总预算。某个插件实际能用多少还要看它排在第几位，
        /// 见 <see cref="GetPluginRowRemaining"/>。
        ///
        /// 返回 <c>0</c> 表示本帧插件行被完全接管（通知 / 剪贴板 / 详情页）；
        /// 返回 <see cref="float.PositiveInfinity"/> 表示宿主尚未算过（启动首帧）。
        /// </summary>
        public static float GetPluginRowBudget() => _pluginRowBudget;

        /// <summary>
        /// 某个插件处的**剩余**可用宽度：按组件从左到右的放行优先级，
        /// 累加排在它前面的插件已经占掉的宽度（含间距），从整行预算里减掉，剩下的就是它的。
        ///
        /// 语义就是「不显示这个插件时，它所在位置还剩多少长度」——插件拿它来判断
        /// 「我这条内容放不放得下」：放不下就别报那个宽度上来（宿主会把整个组件隐藏），
        /// 换一条短的更划算。
        ///
        /// 说明：
        /// · <paramref name="pluginId"/> 为 null（宿主自身无插件上下文）时退回整行预算。
        /// · 本插件自己的组件不参与扣减——「不显示它时」的剩余，自然不该被它自己占掉。
        /// · 只统计**本帧被放行**的组件；没放行的组件本来就不占宽度，不该算在别人头上。
        /// · 不改动任何状态、不触发重测（快照由 NotchWindow 每帧刷新），渲染线程与后台线程都可安全调用。
        /// </summary>
        public static float GetPluginRowRemaining(string? pluginId)
        {
            if (pluginId == null) return _pluginRowBudget;

            lock (_pluginSnapshotLock)
            {
                var widgets = _pluginWidgets;
                var widths = _pluginWidths;
                var broken = _pluginBroken;
                var visible = _pluginVisible;
                if (widgets == null || widths == null || broken == null) return _pluginRowBudget;
                if (widths.Length != widgets.Length || broken.Length != widgets.Length) return _pluginRowBudget;

                float budget = _pluginRowBudget;
                if (float.IsInfinity(budget)) return budget;

                var host = Plugins.PluginManager.Instance.Host;
                float used = 0f;

                for (int i = 0; i < widgets.Length; i++)
                {
                    // 走到本插件的第一个组件：它左边（更高优先级）占掉的就是别人的，剩下的全归它
                    if (host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                        && string.Equals(pid, pluginId, StringComparison.OrdinalIgnoreCase))
                        return Math.Max(0f, budget - used);

                    if (broken[i] || widths[i] <= 0f) continue;
                    if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽
                    used += PLUGIN_GAP + widths[i];
                }

                // 快照里找不到本插件的组件（刚注册还没刷新等）→ 退回整行预算，宁可给宽也别给 0
                return Math.Max(0f, budget - used);
            }
        }

        /// <summary>把岛内逻辑坐标 (x,y) 的左键事件分发给插件组件；命中并处理返回 true。</summary>
        public static bool DispatchPluginLeftClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;

                    Plugins.WidgetHit hit;
                    try { hit = slot.Widget.HitTest(x - r.Left, y - r.Top, r); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件命中检测异常", ex); continue; }
                    if (!hit.IsHit) continue;

                    try { slot.Widget.OnLeftClick(hit.Action, x - r.Left, y - r.Top); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件点击回调异常", ex); }
                    return true;
                }
            }
            return false;
        }

        /// <summary>把岛内逻辑坐标 (x,y) 的右键事件广播给命中的插件组件（具体行为由插件决定）。</summary>
        /// <returns>
        /// 命中且提供详情页的组件 Id（供宿主展开详情页）；没有这种情况返回 null。
        /// 调用方（NotchWindow）拿到非 null 就展开详情页并消费掉这次右键，否则继续走原右键逻辑。
        /// </returns>
        public static string? DispatchPluginRightClick(float x, float y)
        {
            string? detailWidgetId = null;
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;
                    try { slot.Widget.OnRightClick(); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件右键回调异常", ex); }

                    if (detailWidgetId == null)
                    {
                        try { if (slot.Widget.DetailPage != null) detailWidgetId = slot.Widget.Id; }
                        catch (Exception ex) { Logger.Error("[Renderer] 读取组件详情页异常", ex); }
                    }
                }
            }
            return detailWidgetId;
        }

        /// <summary>每帧绘制前清空插件命中区；只有本帧实际绘制了插件行才会重新填充。</summary>
        private static void InvalidatePluginHitAreas()
        {
            lock (_pluginSlotLock)
            {
                _pluginSlots.Clear();
                // 每帧重置「已绘制」标记，让组合模式的顺序表混排能重新按位置分组绘制
                if (_pluginDrawn != null) Array.Clear(_pluginDrawn);
                // 详情页命中区同理：只有本帧真的画了详情页才重新登记
                _detailHitPage = null;
                _detailHitRect = default;
            }
        }

        /// <summary>
        /// 组合模式下媒体模块的右边界（-1 表示当前不在组合模式绘制）。
        /// UI 线程用它来判定媒体按钮 / 悬停区域，保证插件被排到媒体左边或右边时命中依然准确。
        /// </summary>
        public static float CompositeMediaRight => _compositeMediaRight;

        /// <summary>
        /// 媒体模块右边界：优先用渲染时记下的真实值（<c>_compositeMediaRight</c>），
        /// 它同时覆盖「组合模式」与「非组合模式 + 有插件预留」两种情况 —— 插件被排到原生内容左边时，
        /// 媒体右边界就是岛体右边界，下面的换算公式会算出偏左的错误位置。
        /// 还没渲染过（启动首帧 / 整块岛体被通知接管）时才退回按「岛体右边界 − 插件预留」推算。
        /// </summary>
        public static float GetMediaRight(float windowWidth, float currentWidth, bool toastActive)
        {
            if (_compositeMediaRight > 0f) return _compositeMediaRight;
            return (windowWidth + currentWidth) / 2f - (toastActive ? 0f : GetPluginRowReserve());
        }

        // ================= 🧩 非组合模式下的插件左右分组 =================
        // 背景：组合模式靠「内容顺序表」把原生模块与插件混排；非组合模式同一时刻只显示一个原生模块
        //       （媒体激活 → 媒体；否则按待机显示模式 → 时间日期 / 硬件占用 / 空），所以插件相对它
        //       只有「排左边」和「排右边」两种位置，同样是查同一张顺序表得出。

        /// <summary>
        /// 非组合模式下「本帧原生内容」对应的内置模块 Id（同一时刻最多一个）；没有原生内容时返回 null。
        /// </summary>
        private static string? GetNativeBuiltinId(bool mediaActive)
        {
            if (mediaActive) return Plugins.BuiltinWidgets.Media;
            if (StandbyDisplayMode == 0) return Plugins.BuiltinWidgets.Clock;
            if (StandbyDisplayMode == 2) return Plugins.BuiltinWidgets.Hardware;
            return null; // 空白待机：没有原生内容，插件行独占整岛（沿用「整行贴右侧」）
        }

        /// <summary>某个 Id 在「内容显示顺序表」里的位置；-1 表示尚未登记。</summary>
        private static int OrderIndexOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            var order = Plugins.PluginManager.Instance.Host.ContentOrder;
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// 某个组件相对原生模块的左右归属：-1 = 排在原生内容左边，+1 = 右边。
        /// 没有原生内容、或组件所属插件还没进顺序表时一律返回 +1（贴右侧），与引入顺序表之前的行为一致。
        /// </summary>
        private static int GetWidgetSide(string widgetId, int nativeOrderIndex)
        {
            if (nativeOrderIndex < 0) return 1;
            var host = Plugins.PluginManager.Instance.Host;
            if (!host.TryGetWidgetPlugin(widgetId, out var pid) || string.IsNullOrEmpty(pid)) return 1;
            int idx = OrderIndexOf(pid);
            return idx >= 0 && idx < nativeOrderIndex ? -1 : 1;
        }

        /// <summary>
        /// 按「内容显示顺序表」把本帧可见的插件组件分成左右两组，返回两组的**内容宽度**
        /// （各组件宽度之和 + 组内 16px 间距，不含与原生内容之间的间距）。
        /// 只统计被预算放行的组件 —— 与绘制、宽度累加的口径严格一致，宽度才不会与绘制脱节。
        /// </summary>
        private static void MeasurePluginSides(int nativeOrderIndex, out float leftWidth, out float rightWidth)
        {
            leftWidth = 0f;
            rightWidth = 0f;

            Plugins.IWidget[]? widgets;
            float[]? widths;
            bool[]? broken;
            bool[]? visible;
            // 在同一把锁内取齐快照，避免 UI 线程正好重建快照时读到长度不一致的数组
            lock (_pluginSnapshotLock)
            {
                widgets = _pluginWidgets;
                widths = _pluginWidths;
                broken = _pluginBroken;
                visible = _pluginVisible;
            }
            if (widgets == null || widths == null || broken == null) return;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return;

            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽，也不绘制
                if (GetWidgetSide(widgets[i].Id, nativeOrderIndex) < 0)
                    leftWidth = leftWidth > 0f ? leftWidth + PLUGIN_GAP + widths[i] : widths[i];
                else
                    rightWidth = rightWidth > 0f ? rightWidth + PLUGIN_GAP + widths[i] : widths[i];
            }
        }

        /// <summary>刷新插件组件快照（版本变化时才分配 + 测量一次），返回是否存在可渲染组件。</summary>
        private static bool RefreshPluginWidgets()
        {
            lock (_pluginSnapshotLock)
            {
                var host = Plugins.PluginManager.Instance.Host;
                int version = host.WidgetsVersion;
                if (_pluginWidgetsVersion == version && _pluginWidths != null)
                    return _pluginWidgets!.Length > 0;

                _pluginWidgetsVersion = version;
                // Widgets getter 返回的是加锁下的全新数组（已按插件顺序排好），as 转换零拷贝直接持有
                _pluginWidgets = host.Widgets as Plugins.IWidget[] ?? Array.Empty<Plugins.IWidget>();
                _pluginWidths = _pluginWidgets.Length > 0 ? new float[_pluginWidgets.Length] : Array.Empty<float>();
                _pluginBroken = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginDrawn = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginVisible = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();

                // 宽度与快照同版本一起算好：稳态 60FPS 下不再触碰插件代码，保持零额外开销
                for (int i = 0; i < _pluginWidgets.Length; i++)
                {
                    float w = 0f;
                    try { w = Math.Max(_pluginWidgets[i].MeasureWidth(BASE_HEIGHT), 0f); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    _pluginWidths[i] = w;
                }
                // 用新宽度按当前预算重算放行情况（新组件默认不显示，必须立刻算一次）
                RecomputePluginVisibility();
                return _pluginWidgets.Length > 0;
            }
        }

        /// <summary>
        /// 按缓存宽度求和（组件间 16px 间距）。pluginIdFilter 不为 null 时只统计该插件的组件。
        /// 只累计「本帧被预算放行」的组件 —— 与 Draw 的过滤规则严格一致，宽度与绘制才不会脱节。
        /// 返回 0 表示该范围内没有可显示的组件。
        /// </summary>
        private static float SumPluginRowWidth(string? pluginIdFilter)
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 预算没放行 → 与绘制一起缺席
                if (pluginIdFilter != null)
                {
                    if (!host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                        || !string.Equals(pid, pluginIdFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                total = total > 0f ? total + 16f + widths[i] : widths[i];
            }
            return total;
        }

        /// <summary>
        /// 兜底累加「不在内容顺序表里」的插件宽度，与 Draw 底部按 drawn 标记只补未绘制组件的行为一一对应。
        /// 已在顺序表里的插件主循环已累计过一次，这里绝不重复累加 —— 否则组合模式右侧会多一整版插件宽度的死空白。
        /// </summary>
        private static float SumPluginRowWidthNotIn(HashSet<string> orderSet)
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 预算没放行 → 与绘制一起缺席
                bool inOrder = false;
                if (host.TryGetWidgetPlugin(widgets[i].Id, out var pid) && pid != null)
                    inOrder = orderSet.Contains(pid);
                if (inOrder) continue; // 已在顺序表 → 主循环已累计，跳过，防重复计宽
                total = total > 0f ? total + 16f + widths[i] : widths[i];
            }
            return total;
        }

        /// <summary>
        /// 绘制插件组件并缓存命中矩形（供鼠标分发复用）。
        /// pluginIdFilter 为 null 表示绘制「本帧尚未画过」的全部组件（非组合模式的整行绘制）；
        /// 不为 null 时只画属于该插件的组件 —— 组合模式据此把插件摆到顺序表指定的位置。
        /// 两种模式都只画 <see cref="SetPluginRowBudget"/> 放行的组件：没放行的既不绘制也不登记命中区。
        /// 返回推进后的游标 X（下一个内容块的起点，已含 16px 间距）。
        /// mouseX/mouseY 为扣除 topY 平移后的岛内逻辑坐标。
        /// </summary>
        private static float DrawPluginWidgets(SKCanvas canvas, string? pluginIdFilter, float startX, float currentHeight,
            byte alpha, float textOffsetY, float[]? bars, float mouseX, float mouseY)
        {
            Plugins.IWidget[] widgets;
            float[] widths;
            bool[] broken;
            bool[]? drawn;
            bool[]? visible;
            // 在同一把锁内取齐快照，避免 UI 线程正好重建快照时读到长度不一致的数组
            lock (_pluginSnapshotLock)
            {
                if (_pluginWidgets == null || _pluginWidths == null || _pluginBroken == null) return startX;
                widgets = _pluginWidgets;
                widths = _pluginWidths;
                broken = _pluginBroken;
                drawn = _pluginDrawn;
                visible = _pluginVisible;
                if (widths.Length != widgets.Length || broken.Length != widgets.Length) return startX;
            }

            var theme = GetCurrentTheme();
            var host = Plugins.PluginManager.Instance.Host;
            float x = startX;

            lock (_pluginSlotLock)
            {
                for (int i = 0; i < widgets.Length; i++)
                {
                    if (drawn == null || i >= drawn.Length) break;
                    if (drawn[i]) continue;
                    float w = widths[i];
                    if (broken[i] || w <= 0f) continue;
                    // 预算没放行（所需宽度装不进剩余空间）→ 本帧整体不画它，也不登记命中区
                    if (visible == null || i >= visible.Length || !visible[i]) continue;

                    if (pluginIdFilter != null)
                    {
                        if (!host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                            || !string.Equals(pid, pluginIdFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    drawn[i] = true;
                    var r = new SKRect(x, 0f, x + w, currentHeight);
                    _pluginSlots.Add(new Plugins.WidgetLayout.Slot(widgets[i], r));

                    bool hovered = mouseX >= r.Left && mouseX <= r.Right && mouseY >= r.Top && mouseY <= r.Bottom;
                    var frame = new Plugins.WidgetFrame(theme, alpha, textOffsetY, bars, hovered);
                    canvas.Save();
                    try { widgets[i].Draw(canvas, r, frame); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    finally { canvas.Restore(); }

                    x += w + 16f;
                }
            }
            return x;
        }

        /// <summary>熔断持续抛异常的插件组件：停用其绘制/命中，整个生命周期只记一次日志防刷屏。</summary>
        private static void MarkPluginBroken(int index, Exception ex)
        {
            if (index < 0 || _pluginBroken == null || index >= _pluginBroken.Length || _pluginBroken[index]) return;
            _pluginBroken[index] = true;
            Logger.Error($"[Renderer] 插件组件 {(_pluginWidgets != null && index < _pluginWidgets.Length ? _pluginWidgets[index].Id : "?")} 渲染异常，已停用其绘制", ex);
        }

        // ================= 🧩 插件详情页（右键展开） =================
        // 详情页把整个岛体内容整块换掉：尺寸完全由插件通过 MeasureWidth / MeasureHeight 决定，
        // 宿主只做上下限裁剪（防止插件把岛体撑到屏幕外），并负责把岛内左键交给详情页处理。
        // 与组件一致：Measure/Draw 抛异常一律熔断，只记一次日志，绝不拖死渲染循环。
        private const float MIN_DETAIL_WIDTH = 180f;
        private const float MAX_DETAIL_WIDTH = 1000f;
        private const float MIN_DETAIL_HEIGHT = 48f;
        private const float MAX_DETAIL_HEIGHT = 480f;

        private static Plugins.IDetailPage? _detailPage;      // 缓存的详情页实例（与宿主 ActiveDetailPage 同步）
        private static float _detailWidth;                    // 裁剪后的详情页宽度
        private static float _detailHeight;                   // 裁剪后的详情页高度
        private static volatile bool _detailBroken;           // 详情页抛异常 → 熔断（岛体退回原尺寸）
        private static bool _detailCloseRequested;            // 熔断后请求宿主收起（由 NotchWindow 消费）
        private static SKRect _detailHitRect;                 // 本帧详情页命中矩形（岛内逻辑坐标）
        private static Plugins.IDetailPage? _detailHitPage;   // 本帧详情页命中目标

        /// <summary>详情页展开时岛体应采用的宽度（0 = 未展开 / 详情页已熔断）。</summary>
        public static float ActiveDetailWidth
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailWidth : 0f; }
        }

        /// <summary>详情页展开时岛体应采用的高度（0 = 未展开 / 详情页已熔断）。</summary>
        public static float ActiveDetailHeight
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailHeight : 0f; }
        }

        /// <summary>是否正处于详情页展开状态（渲染侧 / NotchWindow 尺寸决策依据）。</summary>
        public static bool HasActiveDetailPage
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken; }
        }

        /// <summary>
        /// 同步宿主详情页状态并测量尺寸。必须在读取 WINDOW_WIDTH / MAX_WINDOW_HEIGHT 之前调用
        /// （NotchWindow 每帧第一件事就是它），因为底层缓冲尺寸依赖详情页大小。
        /// 尺寸只在详情页实例变化时测量一次，稳态 60FPS 下不触碰插件代码。
        /// </summary>
        public static void RefreshDetailPageState()
        {
            var page = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
            lock (_pluginSlotLock)
            {
                if (page == null)
                {
                    _detailPage = null;
                    _detailWidth = _detailHeight = 0f;
                    _detailBroken = false;
                    _detailHitPage = null;
                    _detailHitRect = default;
                    return;
                }
                if (ReferenceEquals(page, _detailPage)) return; // 同一个详情页：尺寸已算好，不重复触碰插件代码

                _detailPage = page;
                _detailBroken = false;
                _detailWidth = _detailHeight = 0f;
                try
                {
                    float w = page.MeasureWidth();
                    float h = page.MeasureHeight();
                    if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f)
                    {
                        Logger.Warn($"[Renderer] 详情页尺寸非法（{w} x {h}），已按最小尺寸兜底");
                        w = Math.Max(w, MIN_DETAIL_WIDTH);
                        h = Math.Max(h, MIN_DETAIL_HEIGHT);
                    }
                    _detailWidth = Math.Clamp(w, MIN_DETAIL_WIDTH, MAX_DETAIL_WIDTH);
                    _detailHeight = Math.Clamp(h, MIN_DETAIL_HEIGHT, MAX_DETAIL_HEIGHT);
                }
                catch (Exception ex)
                {
                    _detailBroken = true;
                    _detailWidth = _detailHeight = 0f;
                    _detailCloseRequested = true;
                    Logger.Error("[Renderer] 详情页尺寸测量异常，已熔断该详情页", ex);
                }
            }
        }

        /// <summary>详情页展开时的岛体尺寸（已裁剪）；未展开或已熔断时返回 false。</summary>
        public static bool TryGetDetailPageSize(out float width, out float height)
        {
            lock (_pluginSlotLock)
            {
                if (_detailPage == null || _detailBroken || _detailWidth <= 0f || _detailHeight <= 0f)
                {
                    width = height = 0f;
                    return false;
                }
                width = _detailWidth;
                height = _detailHeight;
                return true;
            }
        }

        /// <summary>取走「详情页熔断，请宿主收起」的请求（一次性）。NotchWindow 每帧调用。</summary>
        public static bool ConsumeDetailCloseRequest()
        {
            lock (_pluginSlotLock)
            {
                if (!_detailCloseRequested) return false;
                _detailCloseRequested = false;
                return true;
            }
        }

        /// <summary>
        /// 绘制详情页：整块岛体交给插件绘制，并登记命中矩形供左键分发。
        /// mouseX/mouseY 为已扣除 topY 平移后的岛内逻辑坐标（与组件行一致）。
        /// </summary>
        private static void DrawDetailPage(SKCanvas canvas, Plugins.IDetailPage page, float left, float currentHeight,
            float currentWidth, byte alpha, float textOffsetY, float[]? bars, float mouseX, float mouseY)
        {
            var rect = new SKRect(left, 0f, left + currentWidth, currentHeight);
            lock (_pluginSlotLock)
            {
                _detailHitPage = page;
                _detailHitRect = rect;
            }

            bool hovered = mouseX >= rect.Left && mouseX <= rect.Right && mouseY >= rect.Top && mouseY <= rect.Bottom;
            var frame = new Plugins.WidgetFrame(GetCurrentTheme(), alpha, textOffsetY, bars, hovered);
            canvas.Save();
            try { page.Draw(canvas, rect, frame); }
            catch (Exception ex) { MarkDetailBroken(ex); }
            finally { canvas.Restore(); }
        }

        /// <summary>把岛内逻辑坐标 (x,y) 的左键事件交给详情页（HitTest + OnAction）；未展开或无命中返回 false。</summary>
        public static bool DispatchDetailPageClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;
                var r = _detailHitRect;
                if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

                Plugins.WidgetHit hit;
                try { hit = page.HitTest(x - r.Left, y - r.Top, r); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页命中检测异常", ex); return false; }
                if (!hit.IsHit) return false;

                try { page.OnAction(hit.Action, x - r.Left, y - r.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页动作回调异常", ex); }
                return true;
            }
        }

        /// <summary>熔断抛异常的详情页：停用绘制并请求宿主收起，整个生命周期只记一次日志。</summary>
        private static void MarkDetailBroken(Exception ex)
        {
            if (_detailBroken) return;
            _detailBroken = true;
            _detailWidth = _detailHeight = 0f;
            _detailHitPage = null;
            _detailCloseRequested = true;
            Logger.Error("[Renderer] 详情页绘制异常，已熔断并收起", ex);
        }

        // 动态计算最大边界，防止因刘海变大导致出界
        // 将透明原生窗口的基础画布拓宽，给极长歌词预留充足的物理空间，防止被系统窗口边缘裁切
        // 🧩 插件详情页展开时，底层缓冲必须容得下详情页尺寸（+80 / +45 是原有的四周留白）
        // ⚠️ 2026-09-20：岛体总长上限放宽到 MAX_ISLAND_WIDTH(1920) 后，窗口内容区**必须**跟着 ≥ 它 ——
        //    岛体是水平居中画的（islandLeft = (WINDOW_WIDTH - currentWidth) / 2），
        //    只要 currentWidth > WINDOW_WIDTH，islandLeft 就变成负数，超出窗口的那部分会被窗口边缘硬裁，
        //    等于又绕回「被裁切」。所以这里把 MAX_ISLAND_WIDTH 也纳入下限。
        //    窗口比屏幕宽是允许的（岛体可以溢出屏幕，用户明确接受）；透明像素照常鼠标穿透，
        //    位置换算（logX / ptDst.x）都已经带上了「窗口居中于显示器」的偏移量，无需另行处理。
        public static float WINDOW_WIDTH => Math.Max(1200f,
            Math.Max(MAX_ISLAND_WIDTH,
                Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))))) + 80f;
        public static float MAX_WINDOW_HEIGHT => Math.Max(220f, Math.Max(ActiveDetailHeight, Math.Max(BASE_HEIGHT, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT))) + 45f);

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

        private static readonly object _renderLock = new();

        // 🚀 全局复用池 (彻底实现 60FPS 零 GC 分配)
        private static readonly SKPaint _bgPaint = new() { Color = SKColors.Black, IsAntialias = true };
        private static readonly SKPaint _fallbackIconPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };

        // 岛内字体统一取自 FontConfig（公共字体变量），用户切换自定义字体时由 ApplyFont() 热替换
        private static SKTypeface _boldTypeface = FontConfig.Bold;
        private static SKTypeface _normalTypeface = FontConfig.Normal;
        private static SKTypeface _semiBoldTypeface = FontConfig.SemiBold;

        // 订阅字体变更：用户选中自定义字体后立即把新字体重绑到全部文本画笔，无需重启、无需改绘制代码
        static Renderer()
        {
            FontConfig.Changed += ApplyFont;
            ApplyFont();
        }

        /// <summary>
        /// 把 FontConfig 当前的公共字体变量热绑定到岛内所有文本画笔上。
        /// 只在启动和用户切换字体时执行，渲染路径（每帧）不调用，因此不影响零 GC 目标。
        /// </summary>
        public static void ApplyFont()
        {
            _boldTypeface = FontConfig.Bold;
            _normalTypeface = FontConfig.Normal;
            _semiBoldTypeface = FontConfig.SemiBold;

            _titlePaint.Typeface = _boldTypeface;
            _bodyPaint.Typeface = _normalTypeface;
            _textPaint.Typeface = _semiBoldTypeface;
            _timePaint.Typeface = _boldTypeface;
            _datePaint.Typeface = _normalTypeface;
            _compactTimePaint.Typeface = _semiBoldTypeface;
            _compactAppPaint.Typeface = _normalTypeface;
            _tagTextPaint.Typeface = _boldTypeface;
            _tlTextPaint.Typeface = _semiBoldTypeface; // 🎵 时间轴时间文本

            // 下面这些缓存都以「字体」为前提，换字体后必须作废，否则会沿用旧字体的排版宽度导致文字错位
            _lastMinute = -1;                 // 时间/日期文本与宽度缓存
            _lastToastId = uint.MaxValue;     // Toast 分段缓存（下一帧强制重建）
            _lastMediaTitle = "";             // 媒体文本度量缓存
            _lastMediaArtist = "";            // 不动 _lastLyric，避免误触发歌词叠化动画

            // 分段缓存里每条 run 都记着「用哪个 SKTypeface 画的」。旧字体在 FontConfig 发完通知后
            // 就会被 Dispose，这里必须把列表连同 key 一起清掉，绝不能留下指向已释放字体的悬挂引用。
            _cachedToastSenderRuns.Clear();
            _cachedToastBodyRuns.Clear();
            _cachedToastAppNameRuns.Clear();
            _cachedClipboardRuns.Clear();
            _lastClipboardUrl = "";
            _karaokeRuns.Clear();
            _karaokeRuns1.Clear();
            _krKey0 = default;
            _krKey1 = default;
        }

        private static readonly SKPaint _titlePaint = new() { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = _boldTypeface };
        private static readonly SKPaint _bodyPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = _normalTypeface };
        private static readonly SKPaint _textPaint = new() { Color = SKColors.White, TextSize = 12.5f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _shadowPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f) };
        private static readonly SKPaint _mediaIconPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint _barPaint = new() { Color = SKColors.White, IsAntialias = true };
        // 🎵 时间轴左右两侧的时间文本画笔（颜色每帧按透明度刷新，Typeface 由 ApplyFont 热替换）
        private static readonly SKPaint _tlTextPaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKShader _fadeShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(1, 0),
            [SKColors.Black.WithAlpha(0), SKColors.Black],
            null, SKShaderTileMode.Clamp);
        private static readonly SKPaint _fadePaint = new() { Shader = _fadeShader };

        private static readonly SKPath _bgPath = new();
        private static readonly SKPath _clipPath = new();
        private static readonly SKPath _playPath = CreatePlayPath();
        private static readonly SKPath _pausePath = CreatePausePath();
        private static readonly SKPath _prevPath = CreatePrevPath();
        private static readonly SKPath _nextPath = CreateNextPath();

        // ==================== 📋 剪贴板面板图标（纯矢量，无位图） ====================
        // 原先用 data/image/clipboard.png + open_the_link.png 两张位图，现已整体改为矢量直绘：
        // 任意 DPI 都锐利、不再有位图缩放的毛边，也不再需要圆角裁切路径。
        // 颜色一律跟随主题的**纯黑 / 纯白**（每帧由 Draw 写入 _currentTextColor + 透明度），
        // 所以深色主题下是纯白、浅色主题下是纯黑，双色自适应。
        // 统一 24×24 设计画布，绘制时按目标像素尺寸等比缩放。
        private const float ClipboardIconCanvas = 24f;

        // 左图标：细线条（2px / 24 画布 ≈ 目标尺寸下 1.7px），圆头圆角
        private static readonly SKPaint _clipboardLinkPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        // 右图标：实心圆底（箭头是圆底上的镂空，所以整条路径是 Fill 单色）
        private static readonly SKPaint _clipboardOpenPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPath _clipboardLinkPath = CreateClipboardLinkPath();
        private static readonly SKPath _clipboardOpenPath = CreateClipboardOpenPath();

        /// <summary>
        /// 左图标「链接」：两个链环（回形针式胶囊）沿对角线错开咬合。
        /// 这是「链接」最通用的图形语言 —— 两个等长细胶囊交叠，一眼就能读出是链接而不是文字/复制。
        /// </summary>
        private static SKPath CreateClipboardLinkPath()
        {
            var path = new SKPath();
            // 14.6×5.8 的细长胶囊，两个中心沿对角线错开 9.2 —— 这个比例下两环咬合量刚好：
            // 再靠近（如 13.4 宽 / 错开 6.6）中间会糊成一坨，再拉开就断成两个不相干的椭圆。
            path.AddPath(ClipboardCapsule(14.6f, 5.8f, 7.4f, 7.4f));
            path.AddPath(ClipboardCapsule(14.6f, 5.8f, 16.6f, 16.6f));
            return path;
        }

        /// <summary>
        /// 胶囊（体育场形）轮廓：先在原点造形，再整体旋转 45° 后平移到 (cx, cy)。
        /// ⚠️ 矩阵顺序是 `Concat(平移, 旋转)` = 「先旋转、再平移」，写反了链环会被绕原点转到画布外。
        /// </summary>
        private static SKPath ClipboardCapsule(float w, float h, float cx, float cy)
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(-w / 2f, -h / 2f, w / 2f, h / 2f), h / 2f, h / 2f);
            path.Transform(SKMatrix.Concat(SKMatrix.CreateTranslation(cx, cy), SKMatrix.CreateRotationDegrees(45f)));
            return path;
        }

        /// <summary>
        /// 右图标「打开链接」：实心圆底 + 指向右上角的箭头。
        /// 箭头不是叠画上去的第二种颜色，而是**圆底上的镂空**（圆底减去箭头描边轮廓的布尔差集）——
        /// 整条路径只有一种颜色，主题反相时自动跟着变，也不需要知道岛体底色。
        /// </summary>
        private static SKPath CreateClipboardOpenPath()
        {
            var disc = new SKPath();
            disc.AddCircle(12f, 12f, 10.5f);

            // 折角箭头：斜杆 + 右上角的两笔折角（横臂、竖臂）
            var arrow = new SKPath();
            arrow.MoveTo(7f, 17f); arrow.LineTo(17f, 7f);
            arrow.MoveTo(7f, 7f); arrow.LineTo(17f, 7f); arrow.LineTo(17f, 17f);

            // 把描边展开成填充轮廓，再与圆底做差集
            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var arrowFill = new SKPath();
            stroke.GetFillPath(arrow, arrowFill);

            return disc.Op(arrowFill, SKPathOp.Difference) ?? disc;
        }

        // 🚀 PNG 图标缓存替换 SVG
        private static SKBitmap? _defaultAppIcon;
        private static SKBitmap? _qqIcon;
        private static SKBitmap? _defaultToastIcon;
        private static bool _iconsLoaded = false;
        private static readonly SKPaint _highQualitySampling = new() { FilterQuality = SKFilterQuality.High };

        private static SKBitmap? GetDefaultAppIcon()
        {
            if (_defaultAppIcon == null)
            {
                try
                {
                    var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (icon != null)
                    {
                        using var bmp = icon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        ms.Position = 0;
                        _defaultAppIcon = SKBitmap.Decode(ms);
                    }
                }
                catch { }
            }
            return _defaultAppIcon;
        }

        private static void EnsureIconsLoaded()
        {
            if (_iconsLoaded) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string qqPath = Path.Combine(baseDir, "data", "image", "qq-icon.png");
                string defaultPath = Path.Combine(baseDir, "data", "image", "wintoast-icon.png");

                // 直接极速解码为位图
                if (File.Exists(qqPath))
                {
                    using var stream = File.OpenRead(qqPath);
                    _qqIcon = SKBitmap.Decode(stream);
                }

                if (File.Exists(defaultPath))
                {
                    using var stream = File.OpenRead(defaultPath);
                    _defaultToastIcon = SKBitmap.Decode(stream);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载 PNG 图标失败", ex);
            }
            finally { _iconsLoaded = true; }
        }

        // 高频字符串与排版宽度缓存
        private static string _lastMediaTitle = "";
        private static string _lastMediaArtist = "";
        private static string _cachedMediaDisplay = "Code By Ryen";
        private static float _cachedMediaTextTop = 0f;
        private static float _cachedMediaTextHeight = 0f;

        private static uint _lastToastId = 0;
        // 歌词动画专属独立变量
        private static string _lastLyric = "";
        private static string _prevLyric = ""; // 保存上一句歌词
        private static string _lastLyricTrans = ""; // 当前句的译文（第二行），无译文时为空
        private static string _prevLyricTrans = ""; // 叠化淡出层的译文，与 _prevLyric 同生同灭
        private static float _lyricAnimProgress = 1f; // 动画进度 0~1
        private static DateTime _lyricChangeTime; // 动画起始时间
        // 待机时间显示专用画笔
        private static readonly SKPaint _timePaint = new() { Color = SKColors.White, TextSize = 14.5f, IsAntialias = true, Typeface = _boldTypeface };
        private static readonly SKPaint _datePaint = new() { Color = new SKColor(200, 200, 200), TextSize = 14.5f, IsAntialias = true, Typeface = _normalTypeface };
        // 紧凑模式右侧小号信息画笔：“现在”用纯色(跟随主题明暗)，应用名用灰色(小号)
        private static readonly SKPaint _compactTimePaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };
        private static readonly SKPaint _compactAppPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 10f, IsAntialias = true, Typeface = _normalTypeface };
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
        private static readonly SKPaint _tagTextPaint = new() { Color = SKColors.White, TextSize = 10.5f, IsAntialias = true, Typeface = _boldTypeface };
        private static readonly SKPaint _tagBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint _barBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

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
        private static string _cachedToastSender = "";
        private static string _cachedToastBody = "";
        private static string _cachedToastAppName = "";
        // 预加载 Windows 自带 Emoji 彩色字体与零 GC 渲染缓存列表
        private static readonly SKTypeface _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");
        private static readonly List<(string Text, SKTypeface Type, float X)> _karaokeRuns = new(); // 歌词/歌名/歌手 逐字字体回退用的 runs
        private static readonly List<(string Text, SKTypeface Type, float X)> _karaokeRuns1 = new(); // 第二缓存槽（叠化动画时旧/新两条歌词各占一槽）
        private static (string Text, SKTypeface Type, float Width) _krKey0;
        private static (string Text, SKTypeface Type, float Width) _krKey1;
        private static bool _krSlot;
        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastSenderRuns = new();
        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastBodyRuns = new();
        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedToastAppNameRuns = new();
        private static float _cachedToastTitleWidth = 0f;
        private static float _cachedToastBodyWidth = 0f;
        private static float _cachedToastAppNameWidth = 0f;
        // 🩹 紧凑模式右侧双行信息（“现在”+ 应用名，小号字体）的真实占宽，随 toast 一起缓存。
        //    GetToastAutoWidth 用它来预留右侧空间 —— 只写死 COMPACT_RIGHT_WIDTH 的话，
        //    应用名一长（如“Windows 安全中心”）右侧就会实际吃进左侧消息文字，尾巴被遮罩截掉。
        private static float _cachedToastCompactRightWidth = 0f;

        // 📋 剪贴板链接面板专用缓存（只在链接变化 / 换字体时重建一次，稳态零重算）
        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedClipboardRuns = new();
        private static string _lastClipboardUrl = "";
        private static float _cachedClipboardTextWidth = 0f;
        private static SKRect _clipboardOpenHit;   // 本帧「打开」按钮命中区，帧首作废

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f, string? clipboardUrl = null)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                // 🧩 每帧清空插件命中区，仅当本帧实际绘制插件行时才重新填充
                // （防止 Toast / 媒体激活等不绘制插件的状态下残留上一帧的过期命中矩形）
                InvalidatePluginHitAreas();

                // 🎵 时间轴几何登记表帧首作废：本帧不画就等于命中区不存在
                _tlBarX1 = _tlBarX2 = _tlBarY = 0f;

                // 📋 剪贴板「打开」按钮热区帧首作废：本帧不画就等于命中区不存在
                _clipboardOpenHit = default;

                // 岛体物理左边界（背景形状 / 裁剪范围以它为准）
                float islandLeft = (WINDOW_WIDTH - currentWidth) / 2f;
                // 岛体物理右边界（背景形状 / 裁剪范围以它为准）
                float islandRight = islandLeft + currentWidth;
                // 🧩 插件行的位置：
                //   · 组合模式：插件已并入「内容顺序表」，与原生模块一起混排（宽度计在 GetCompositeWidth 内），
                //     不单独占用预留区；
                //   · 非组合模式：同样遵守这张顺序表 —— 排在「本帧原生模块」之前的插件画在原生内容**左边**，
                //     之后的画在右边。原生内容的左右边界据此内收，所以插件显示与否、排在哪一边，
                //     都不会影响原生功能本身。
                //     （2026-09-20 修复：此前非组合模式无条件把整行插件贴在岛体最右侧、完全不读顺序表，
                //       导致「插件中心」的 ← / → 只在组合模式下有效。）
                // ⚠️ 这里必须用**未缩放**的预留（GetPluginRowReserve，而不是 GetScaledPluginReserve）。
                //    缩放版把预留按「当前宽度 / 目标宽度」缩小，而岛体宽度是弹簧动画过来的：
                //    媒体控制器长度一变（换歌词 / 换标题 → 目标宽度变大），缩放系数立刻掉下来，
                //    原生内容边界与插件行就会整体挪一下再挪回去 —— 表现出来就是「闪现一下又闪回来」。
                //    （用户 2026-09-20 反馈；组合模式不走这条路径，所以只有媒体控制器会出现。）
                //    改用未缩放值后，插件行与原生内容的边界只跟着岛体边缘平滑移动，不再有跳变。
                float pluginReserve = toast == null && !CompositeModeEnabled ? GetPluginRowReserve() : 0f;
                // 原生内容本帧对应的内置模块（非组合模式同一时刻最多显示一个原生模块）
                string? nativeBuiltinId = CompositeModeEnabled ? null : GetNativeBuiltinId(media.IsActive);
                int nativeOrderIndex = OrderIndexOf(nativeBuiltinId);
                // 两侧插件组：内容宽度（各组件宽度 + 组内 16px 间距）与占位宽度（再加与原生内容之间的间距）
                float leftGroupW = 0f, rightGroupW = 0f;
                float leftPluginBlock = 0f;
                float rightPluginBlock = pluginReserve;
                if (pluginReserve > 0f)
                {
                    MeasurePluginSides(nativeOrderIndex, out leftGroupW, out rightGroupW);
                    leftPluginBlock = leftGroupW > 0f ? leftGroupW + PLUGIN_GAP : 0f;
                    rightPluginBlock = rightGroupW > 0f ? rightGroupW + PLUGIN_GAP : 0f;
                    // 兜底让位：岛体宽度还在弹簧动画途中时（currentWidth < 目标宽度），
                    // 本帧可能装不下「原生内容 + 两侧插件组」（典型：通知收起后插件行重新出现，
                    // 岛体才 260 而插件行要 400）。此时插件组按比例让位，给原生内容留出 MIN_NATIVE_AREA。
                    //
                    // ⚠️ 判定阈值用**常量** MIN_NATIVE_AREA，不能换成「本帧原生内容所需宽度」——
                    //    后者与目标宽度同一刻跳变，而 currentWidth 还停在旧目标上，
                    //    换歌词那一帧就会误判「装不下」而把原生内容区一步拉宽（又是一次跳变）。
                    //    常量下限下：稳态（currentWidth == nativeWidth + reserve）永远不触发，
                    //    换歌词（reserve 不变、只有目标宽度变大）也不触发 —— 边界只跟着岛体边缘平滑移动。
                    // 让位系数 k 随 currentWidth 连续变化（totalBlock 在预留不变时是常量），
                    // 不会引入新的跳变；收窄后的区间由绘制侧裁剪落实（见下方 ClipRect），
                    // 于是左右两组绝不会在岛体中间叠在一起，插件是随岛体长大从两侧滑入的。
                    // ⚠️ 只在「本帧确有原生内容」时才介入：空白待机（nativeBuiltinId == null）时
                    //    原生内容区本来就是空的，没有东西会被负宽度翻面。
                    if (nativeBuiltinId != null)
                    {
                        float totalBlock = leftPluginBlock + rightPluginBlock;
                        float maxBlock = Math.Max(0f, currentWidth - MIN_NATIVE_AREA);
                        if (totalBlock > maxBlock + 0.5f) // 0.5px 容差：稳态下不会触发，别被浮点误差误判
                        {
                            float k = maxBlock / totalBlock;
                            leftPluginBlock *= k;
                            rightPluginBlock *= k;
                        }
                    }
                }
                // 原生内容的左右边界（扣除两侧插件组）；没有插件时与岛体边界完全相同
                float left = islandLeft + leftPluginBlock;
                float right = islandRight - rightPluginBlock;
                // 媒体模块右边界（每帧刷新，供 UI 线程判定媒体按钮 / 悬停命中）：
                // 组合模式与「非组合 + 有插件预留」都要用渲染时的真实值 —— 插件被排到原生内容左边时，
                // 媒体右边界就是岛体右边界，旧的换算公式会算出偏左的错误位置。
                _compositeMediaRight = CompositeModeEnabled || pluginReserve > 0f ? right : -1f;
                int btnPrevX = (int)right - 90;
                int btnPlayX = (int)right - 60;
                int btnNextX = (int)right - 30;

                // 灵动岛悬浮距离顶部的 Y 轴高度 (随过渡进度平滑变化)
                float topY = 12f * styleProgress;

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中
                canvas.Translate(0, topY);

                // 开启一个硬件级透明图层，包裹本体所有元素，杜绝任何图层/阴影残留
                _layerPaint.Color = SKColors.White.WithAlpha((byte)(255 * PassthroughAlpha));
                canvas.SaveLayer(_layerPaint);

                _bgPath.Rewind();

                // 自动把四个圆角调到最大，动态计算插值半径
                // 限制灵动岛展开后的最大圆角为 20f，防止变成大圆球
                float islandRadius = Math.Min(currentHeight / 2f, 20f);
                float rBottom = NOTCH_BOTTOM_RADIUS * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopY = OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopX = -OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;

                // 纯数学魔法：完美正圆形的 Conic 曲线权重 (Math.Sqrt(2) / 2)
                float w = 0.70710678f;

                // 纯数学变形算法：全部使用 ConicTo 替换 QuadTo 强制生成完美圆形弧度
                _bgPath.MoveTo(islandLeft + rTopX, 0);
                _bgPath.ConicTo(islandLeft, 0, islandLeft, rTopY, w);
                _bgPath.LineTo(islandLeft, currentHeight - rBottom);
                _bgPath.ConicTo(islandLeft, currentHeight, islandLeft + rBottom, currentHeight, w);
                _bgPath.LineTo(islandRight - rBottom, currentHeight);
                _bgPath.ConicTo(islandRight, currentHeight, islandRight, currentHeight - rBottom, w);
                _bgPath.LineTo(islandRight, rTopY);
                _bgPath.ConicTo(islandRight, 0, islandRight - rTopX, 0, w);
                _bgPath.Close();

                canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                byte alpha = (byte)(255 * startupProgress * transitionAlpha);
                float textOffsetY = 0f;

                // 仅恢复原版代码中软件刚启动时的位移，不影响状态切换
                if (!media.IsActive && startupProgress < 1f)
                {
                    textOffsetY = (1f - startupProgress) * 15f;
                }

                SKColor currentA = _currentTextColor.WithAlpha(alpha);
                SKColor subA = _currentSubTextColor.WithAlpha(alpha);

                _titlePaint.Color = currentA;
                _bodyPaint.Color = subA;
                _textPaint.Color = currentA;
                _timePaint.Color = currentA;
                _datePaint.Color = subA;
                _mediaIconPaint.Color = currentA;
                _clipboardLinkPaint.Color = currentA; // 📋 剪贴板图标同为矢量：跟着主题色 + 透明度走
                _clipboardOpenPaint.Color = currentA;
                _barPaint.Color = currentA;
                _highQualitySampling.Color = SKColors.White.WithAlpha(alpha); // 同步作用于图片图标

                // ---------------- [ Toast 消息通知 ] ----------------
                if (toast != null)
                {
                    if (_lastToastId != toast.NotificationId)
                    {
                        _lastToastId = toast.NotificationId;
                        _cachedToastSender = !string.IsNullOrEmpty(toast.Title) ? toast.Title : (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");
                        _cachedToastBody = toast.Body ?? "";
                        _cachedToastAppName = string.IsNullOrEmpty(toast.AppName) ? (string.IsNullOrEmpty(toast.ProcessName) ? "系统通知" : toast.ProcessName) : toast.AppName;

                        // 只在接收到新消息时分配一次内存
                        BuildTextRuns(_cachedToastSender, _titlePaint, _boldTypeface, _cachedToastSenderRuns, out _cachedToastTitleWidth);
                        BuildTextRuns(_cachedToastBody, _bodyPaint, _normalTypeface, _cachedToastBodyRuns, out _cachedToastBodyWidth);
                        BuildTextRuns(_cachedToastAppName, _bodyPaint, _normalTypeface, _cachedToastAppNameRuns, out _cachedToastAppNameWidth);

                        // 🩹 紧凑模式右侧那块双行信息的真实占宽（与下面绘制用的画笔、文案、字号完全一致），
                        //    供 GetToastAutoWidth 预留宽度使用，保证左侧消息文字永远放得下。
                        _cachedToastCompactRightWidth = Math.Max(
                            _compactTimePaint.MeasureText("现在"),
                            _compactAppPaint.MeasureText(string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName));
                    }

                    float iconSize = 28f;
                    float toastIconX = left + 14f;
                    float toastIconY = (currentHeight - iconSize) / 2f;
                    var iconRect = new SKRect(toastIconX, toastIconY, toastIconX + iconSize, toastIconY + iconSize);

                    EnsureIconsLoaded();
                    SKBitmap? targetIcon = null;

                    if (toast.ProcessName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ||
                        toast.AppName.Contains("QQ", StringComparison.OrdinalIgnoreCase))
                    {
                        targetIcon = _qqIcon;
                    }
                    targetIcon ??= _defaultToastIcon;

                    // 直接绘制位图，逻辑极其精简
                    if (targetIcon != null)
                    {
                        canvas.Save();
                        _clipPath.Rewind();
                        _clipPath.AddRoundRect(iconRect, 4, 4);
                        canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(targetIcon, iconRect, _highQualitySampling);
                        canvas.Restore();
                    }
                    else
                    {
                        var defaultAppIcon = GetDefaultAppIcon();
                        if (defaultAppIcon != null)
                        {
                            canvas.Save();
                            _clipPath.Rewind();
                            _clipPath.AddRoundRect(iconRect, 4, 4);
                            canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                            canvas.DrawBitmap(defaultAppIcon, iconRect, _highQualitySampling);
                            canvas.Restore();
                        }
                        else
                        {
                            canvas.DrawRoundRect(iconRect, 4, 4, _fallbackIconPaint);
                        }
                    }

                    float toastTextX = toastIconX + iconSize + 10f;
                    float toastMaxTextRight = right - 16f;
                    if (IsToastFullMode) toastMaxTextRight -= 36f; // 完整模式右上角需预留“现在”的空间

                    // 紧凑模式：右侧文本块的真实左边缘（“现在”与应用名两行的最大宽者），遮罩与溢出判断都紧贴它
                    float compactNowWidth = 0f, compactAppW = 0f, compactRightLeft = 0f;
                    if (IsToastCompactMode)
                    {
                        compactNowWidth = _compactTimePaint.MeasureText("现在");
                        string compactAppName0 = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                        compactAppW = _compactAppPaint.MeasureText(compactAppName0);
                        compactRightLeft = (right - 16f) - Math.Max(compactNowWidth, compactAppW);
                    }

                    float textSpacing = 5f;

                    // 三行文本参数（完整模式）或两行文本参数（缩略模式）
                    float totalTextHeight, line1Y, line2Y, line3Y;
                    if (IsToastFullMode)
                    {
                        totalTextHeight = 12f + 13.5f + 11.5f + textSpacing * 2;
                        float toastTextY = (currentHeight - totalTextHeight) / 2f;
                        line1Y = toastTextY + 10f;            // 应用名（小字灰度）
                        line2Y = line1Y + 12f + textSpacing;  // 发送者（粗体）
                        line3Y = line2Y + 13.5f + textSpacing;// 消息主体
                    }
                    else
                    {
                        totalTextHeight = 13.5f + 11.5f + textSpacing;
                        float toastTextY = (currentHeight - totalTextHeight) / 2f;
                        line1Y = toastTextY + 11.5f;
                        line2Y = line1Y + 13.5f + textSpacing;
                        line3Y = 0f;
                    }

                    if (IsToastFullMode)
                    {
                        // 渲染应用名（小字），右上角同排悬浮“现在”
                        foreach (var run in _cachedToastAppNameRuns)
                        {
                            _bodyPaint.Typeface = run.Type;
                            canvas.DrawText(run.Text, toastTextX + run.X, line1Y, _bodyPaint);
                        }
                        _bodyPaint.Typeface = _normalTypeface; // 重置

                        float nowWidth = _bodyPaint.MeasureText("现在");
                        canvas.DrawText("现在", right - 16f - nowWidth, line1Y, _bodyPaint);
                    }

                    // 渲染发送者标题：自动在常规字体与 Emoji 字体间热切换
                    // 完整模式在第2行，缩略模式在第1行
                    float senderY = IsToastFullMode ? line2Y : line1Y;
                    foreach (var run in _cachedToastSenderRuns)
                    {
                        _titlePaint.Typeface = run.Type;
                        canvas.DrawText(run.Text, toastTextX + run.X, senderY, _titlePaint);
                    }
                    _titlePaint.Typeface = _boldTypeface; // 重置

                    // 渲染内容主体（缩略模式在第2行，完整模式在第3行）
                    float bodyY = IsToastFullMode ? line3Y : line2Y;
                    foreach (var run in _cachedToastBodyRuns)
                    {
                        _bodyPaint.Typeface = run.Type;
                        canvas.DrawText(run.Text, toastTextX + run.X, bodyY, _bodyPaint);
                    }
                    _bodyPaint.Typeface = _normalTypeface; // 重置

                    bool textOverflow;
                    if (IsToastFullMode)
                        textOverflow = toastTextX + _cachedToastAppNameWidth > toastMaxTextRight ||
                                       toastTextX + _cachedToastTitleWidth > toastMaxTextRight ||
                                       toastTextX + _cachedToastBodyWidth > toastMaxTextRight;
                    else if (IsToastCompactMode)
                        textOverflow = toastTextX + _cachedToastTitleWidth > compactRightLeft ||
                                       toastTextX + _cachedToastBodyWidth > compactRightLeft;
                    else
                        textOverflow = toastTextX + _cachedToastTitleWidth > toastMaxTextRight ||
                                       toastTextX + _cachedToastBodyWidth > toastMaxTextRight;

                    if (textOverflow)
                    {
                        float fadeWidth = 15f;
                        // 紧凑模式下遮罩紧贴右侧文本真实左边缘，其余模式按统一文本右边界
                        float fadeStart = IsToastCompactMode ? compactRightLeft - fadeWidth : toastMaxTextRight - fadeWidth;

                        canvas.Save();
                        canvas.Translate(fadeStart, 0);
                        canvas.Scale(fadeWidth, currentHeight);
                        canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                        canvas.Restore();

                        // 紧凑模式在左侧文本与右侧信息接触处用遮罩过渡，其余模式则覆盖超出右边界的文字
                        if (!IsToastCompactMode)
                            canvas.DrawRect(toastMaxTextRight, 0, WINDOW_WIDTH, currentHeight, _bgPaint);
                    }

                    // 紧凑模式：右侧靠边显示双行信息（右上“现在”纯色、右下应用名灰色，均小号右对齐）
                    if (IsToastCompactMode)
                    {
                        string compactAppName = string.IsNullOrEmpty(_cachedToastAppName) ? "通知" : _cachedToastAppName;
                        // 先铺与窗口背景同色的实心色块，从右侧文本真实左边缘延伸到右缘，与渐变遮罩衔接，保证左侧长内容不会透到这两行信息上
                        canvas.DrawRect(compactRightLeft, 0f, right - 16f, currentHeight, _bgPaint);
                        canvas.DrawText("现在", right - 16f - compactNowWidth, line1Y, _compactTimePaint);
                        canvas.DrawText(compactAppName, right - 16f - compactAppW, line2Y, _compactAppPaint);
                    }

                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ 📋 剪贴板链接（已识别到链接） ] ----------------
                // 优先级：系统通知 > 剪贴板链接 > 媒体控制器。通知展示期间上层已把链接拦住排队，
                // 所以这里只要拿到链接，就把整块岛体交给剪贴板面板绘制。
                if (!string.IsNullOrEmpty(clipboardUrl))
                {
                    DrawClipboard(canvas, clipboardUrl, left, right, currentHeight, textOffsetY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // ---------------- [ 🧩 插件详情页（右键展开） ] ----------------
                // 详情页展开时整块岛体交给插件绘制：不再绘制原生内容，也不再绘制插件行。
                // 岛体尺寸由 NotchWindow 依据详情页 MeasureWidth/MeasureHeight 决定（这里同步消费一次状态即可）。
                // Toast 优先级高于详情页：通知到来时先显示通知，通知结束后详情页自动回来。
                var detailPage = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
                if (detailPage != null && !_detailBroken)
                {
                    DrawDetailPage(canvas, detailPage, left, currentHeight, currentWidth, alpha, textOffsetY, bars,
                        _pluginMouseX, _pluginMouseY - topY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // ---------------- [ 媒体控制与待机状态 ] ----------------
                if (media.IsActive)
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
                var now = DateTime.Now;
                if (_lastMinute != now.Minute)
                {
                    _lastMinute = now.Minute;
                    _cachedTimeStr = now.ToString("HH:mm"); // 00:00 24小时制
                    _cachedDateStr = now.ToString("MM/dd"); // 月/日 格式
                    _cachedTimeWidth = _timePaint.MeasureText(_cachedTimeStr);
                    _cachedDateWidth = _datePaint.MeasureText(_cachedDateStr);
                }

                // ---------------- [ 自定义组合模式 ] ----------------
                if (CompositeModeEnabled)
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
                else
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
                } // 硬件占用检测 if 结束的大括号

                // ================= 🧩 插件组件行（非组合模式） =================
                // 组合模式下插件已并入「内容顺序表」跟原生模块混排（见上方组合模式分支），这里只处理其余模式：
                // 待机(时间日期/空白/硬件)、媒体激活、媒体展开……原生内容一律不感知插件，插件也不影响原生布局。
                // 与组合模式一样遵守顺序表：排在原生模块之前的插件画在原生内容左边，之后的画在右边。
                if (!CompositeModeEnabled && pluginReserve > 0f)
                {
                    if (leftPluginBlock > 0f)
                    {
                        // 左侧：从岛体左边缘的内边距起，按顺序表次序逐个插件向右排。
                        // 只画「排在原生模块之前」的插件 —— 原生模块本身在它自己的位置由上面的原生分支绘制。
                        // 起点直接锚在岛体左边缘（而不是从 nativeLeft 倒推），这样岛体宽度做动画时
                        // 插件行只跟着边缘一起平移，不会自己额外挪动。
                        // ✂️ 裁剪到 [islandLeft, left]：稳态下这个区间恰好 == 「左侧插件组 + 与原生内容的间距」，
                        //    裁剪等于没裁（零视觉影响）；只有岛体还在变宽的瞬态（leftPluginBlock 被按比例
                        //    让位收窄）才真的切到 —— 于是左右两组绝不会在岛体中间叠在一起，
                        //    插件是随岛体长大从两侧滑入的。
                        var leftOrder = Plugins.PluginManager.Instance.Host.ContentOrder;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(islandLeft, 0f, left, currentHeight));
                        float lx = islandLeft + PLUGIN_GAP;
                        for (int i = 0; i < nativeOrderIndex && i < leftOrder.Count; i++)
                        {
                            string item = leftOrder[i];
                            if (Plugins.BuiltinWidgets.IsBuiltin(item)) continue;
                            lx = DrawPluginWidgets(canvas, item, lx, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        }
                        canvas.Restore();
                    }
                    // 右侧：整行兜底 —— 左侧已画过的组件带 drawn 标记不会重复，
                    // 还没进顺序表的组件（例如直接 RegisterWidget 注册的测试组件）也在这里补上。
                    // 同样锚在岛体右边缘（rightGroupW == 0 说明一个右侧组件都没有，直接跳过），
                    // 并同样裁剪到 [right, islandRight]（与左侧对称，理由同上）。
                    if (rightGroupW > 0f)
                    {
                        canvas.Save();
                        canvas.ClipRect(new SKRect(right, 0f, islandRight, currentHeight));
                        DrawPluginWidgets(canvas, null, islandRight - rightGroupW, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        canvas.Restore();
                    }
                }

                // === 下方原本旧版残留的 _wakePath 绘制代码已被彻底删除 ===

                canvas.Restore(); // 1. 恢复 ClipPath 裁切
                canvas.Restore(); // 2. 闭合 SaveLayer 透明层，本体内部渲染彻底完结！任何阴影、遮罩全部随之消失。

                // 独立于本体之外，绘制隐形物理热区与极速渐变唤醒按钮
                if (PassthroughModeEnabled && PassthroughAlpha < 0.99f)
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

                canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
            }
            finally
            {
                Monitor.Exit(_renderLock);
            }
        }

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

        private static void DrawSvgPath(SKCanvas canvas, SKPaint paint, float x, float y, SKPath path, float scale = 1f)
        {
            canvas.Save();
            canvas.Translate(x, y);
            if (scale != 1f) canvas.Scale(scale);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        private static SKPath CreatePlayPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(10, 6); path.LineTo(0, 12); path.Close(); return path; }
        private static SKPath CreatePausePath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 3, 12)); path.AddRect(new SKRect(6, 0, 9, 12)); return path; }
        private static SKPath CreatePrevPath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 2, 10)); path.MoveTo(8, 0); path.LineTo(2, 5); path.LineTo(8, 10); path.Close(); return path; }
        private static SKPath CreateNextPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(6, 5); path.LineTo(0, 10); path.Close(); path.AddRect(new SKRect(6, 0, 8, 10)); return path; }

        // 文本拆分引擎：逐码点决定用哪套字体，把连续同字体的片段切成 runs，实现 Emoji 与缺字回退
        private static void BuildTextRuns(string text, SKPaint paint, SKTypeface baseTypeface, List<(string Text, SKTypeface Type, float X)> runs, out float totalWidth)
        {
            runs.Clear();
            totalWidth = 0;
            if (string.IsNullOrEmpty(text)) return;

            int start = 0;
            SKTypeface? currentType = null;

            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                int charLen = 1;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                {
                    cp = char.ConvertToUtf32(text, i);
                    charLen = 2;
                }

                // 基础判定：基础字体里没有这个字（可能是 Emoji，也可能是自定义英文字体缺的中文）
                bool missingInBase = baseTypeface.GetGlyph(cp) == 0;
                bool forcedEmoji = false;

                // 1. 向前探测：如果当前字符（比如 # 或 ⛸）后面紧跟了 Emoji 变体选择器(FE0F)或零宽连字(200D)，
                // 说明它是 Emoji 组合的开头，强制视为 Emoji，防止被默认字体抢走。
                if (!missingInBase && i + charLen < text.Length)
                {
                    char nextChar = text[i + charLen];
                    if (nextChar == '\uFE0F' || nextChar == '\u200D' || nextChar == '\u20E3')
                    {
                        forcedEmoji = true;
                    }
                }

                // 2. 修饰符绑定：这些不可见字符本身必须作为 Emoji 处理，不能断开
                if (cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3)
                {
                    forcedEmoji = true;
                }

                // 3. 肤色修饰符 (U+1F3FB ~ U+1F3FF)，强制绑定为 Emoji
                if (cp >= 0x1F3FB && cp <= 0x1F3FF)
                {
                    forcedEmoji = true;
                }

                SKTypeface type = ResolveTypeface(cp, baseTypeface, missingInBase, forcedEmoji);
                currentType ??= type; // 初始化第一个状态

                // 只有当字体类型发生真正的改变时，才进行安全切割
                if (!ReferenceEquals(type, currentType))
                {
                    string sub = text.Substring(start, i - start);
                    runs.Add((sub, currentType, totalWidth));
                    paint.Typeface = currentType;
                    totalWidth += paint.MeasureText(sub);

                    currentType = type;
                    start = i;
                }

                if (charLen == 2) i++; // 跳过代理对的后半段
            }

            // 处理收尾文本
            if (start < text.Length && currentType != null)
            {
                string sub = text.Substring(start);
                runs.Add((sub, currentType, totalWidth));
                paint.Typeface = currentType;
                totalWidth += paint.MeasureText(sub);
            }

            paint.Typeface = baseTypeface; // 重置画笔
        }

        /// <summary>
        /// 逐码点决定用哪套字体：
        ///   基础字体有这个字 → 基础字体；
        ///   缺字且是 Emoji → 彩色 Emoji 字体；
        ///   缺字的普通文字：
        ///     · 用户选了自定义字体 → 系统兜底字体（<b>自定义字体优先级最高，多语言兜底层绝不插手</b>）；
        ///     · 默认系统字体     → 先问 <see cref="LyricsFont"/> 要一套真正含该字形的系统字体
        ///       （韩文、泰文、阿拉伯文……），拿到就用，拿不到才落回原来的系统字体 / Emoji 兜底。
        /// </summary>
        private static SKTypeface ResolveTypeface(int cp, SKTypeface baseTypeface, bool missingInBase, bool forcedEmoji)
        {
            if (!missingInBase && !forcedEmoji) return baseTypeface;
            if (forcedEmoji) return _emojiTypeface;
            if (IsEmojiCodePoint(cp) && _emojiTypeface.GetGlyph(cp) != 0) return _emojiTypeface;

            // ★ 多语言兜底：只在默认字体下启用。Emoji 区段已在上一步分流，这里只处理"真的缺字的文字"。
            //    LyricsFont 内部按码点缓存决定（含负缓存），同一句歌词每个码点只询问系统一次。
            if (!FontConfig.HasCustomFont && missingInBase)
            {
                SKTypeface? multi = LyricsFont.Resolve(cp, baseTypeface);
                if (multi != null) return multi;
            }

            if (FontConfig.Fallback.GetGlyph(cp) != 0) return FontConfig.Fallback;
            return _emojiTypeface; // 兜底字体也没有：维持改动前「交给 Emoji 字体」的旧行为
        }

        /// <summary>粗略判定码点是否落在 Emoji 区段（仅在基础字体缺字时才用于选择字体）。</summary>
        private static bool IsEmojiCodePoint(int cp)
            => (cp >= 0x1F000 && cp <= 0x1FAFF)   // Emoji 主体区（表情、交通、补充符号等）
            || (cp >= 0x2600 && cp <= 0x27BF)     // 杂项符号与装饰符号
            || (cp >= 0x2B00 && cp <= 0x2BFF)     // 杂项符号与箭头
            || (cp >= 0xFE00 && cp <= 0xFE0F)     // 变体选择符
            || cp == 0x200D || cp == 0x20E3;

        /// <summary>
        /// 折叠态媒体文本的绘制宽度（逐字字体回退后的真实总宽），直接读 <see cref="DrawKaraoke"/> 刚建好的 run 缓存，
        /// 稳态下不产生任何额外测量。缓存未命中（刚换字体 / 刚换歌的那一帧）返回 0，调用方自行兜底。
        /// </summary>
        private static float CachedMediaTextWidth()
        {
            if (_krKey0.Text == _cachedMediaDisplay) return _krKey0.Width;
            if (_krKey1.Text == _cachedMediaDisplay) return _krKey1.Width;
            return 0f;
        }

        /// <summary>
        /// 命中原有的卡拉OK run 缓存则直接复用（不重建、不测量）。命中与否由 <see cref="DrawKaraoke"/> 侧同一套 key 决定。
        /// </summary>
        private static bool ReuseCachedRuns(string text, SKTypeface baseTypeface, out float totalWidth)
        {
            if (_krKey0.Text == text && ReferenceEquals(_krKey0.Type, baseTypeface)) { totalWidth = _krKey0.Width; return true; }
            if (_krKey1.Text == text && ReferenceEquals(_krKey1.Type, baseTypeface)) { totalWidth = _krKey1.Width; return true; }
            totalWidth = 0f;
            return false;
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
    }
}