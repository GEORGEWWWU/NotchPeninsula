using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static class Renderer
    {
        // 1. 布局核心参数 (改为无锁动态变量)
        private static volatile float _standbyWidth = 130f;
        private static volatile float _baseHeight = 34f;
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
        private static readonly SKPaint _wakePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true }; // 极简线条画笔
        private static readonly SKPaint _wakeHitPaint = new SKPaint { Style = SKPaintStyle.Fill }; // 隐形物理热区底板
        private static readonly SKPath _wakePath = CreateWakePath();

        private static SKPath CreateWakePath()
        {
            var path = new SKPath();
            path.AddCircle(18f, 18f, 8f); // 外圈
            path.AddCircle(18f, 18f, 3f); // 核心唤醒点
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
        /// 岛体总长度上限：与消息弹窗（Toast）的最大长度保持一致。
        /// Toast / 剪贴板面板的自适应宽度、组合模式总宽、以及插件行的取舍都以它封顶。
        /// </summary>
        public const float MAX_ISLAND_WIDTH = 800f;

        // 计算Toast消息自适应宽度，限制最大宽度（与岛体总长上限一致）
        public static float GetToastAutoWidth()
        {
            float maxTextW = IsToastFullMode
                ? Math.Max(_cachedToastTitleWidth, Math.Max(_cachedToastBodyWidth, _cachedToastAppNameWidth))
                : Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            float w = maxTextW + 68f;
            // 紧凑模式：左侧文本之外还需为右侧双行信息（现在 + 应用名）预留空间
            if (IsToastCompactMode) w += COMPACT_RIGHT_WIDTH;
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

        // 📋 剪贴板链接面板：左「icon」+ 中间链接 + 右「打开」按钮（尺寸与媒体控制器同款）
        private static void DrawClipboard(SKCanvas canvas, string url, float left, float right, float currentHeight, float textOffsetY)
        {
            EnsureClipboardIconsLoaded();
            EnsureClipboardTextCache(url);

            // 左侧 icon（与 Toast 图标同款圆角裁切）
            float iconSize = 20f;
            float iconX = left + 14f;
            float iconY = (currentHeight - iconSize) / 2f + textOffsetY;
            if (_clipboardIcon != null)
            {
                var iconRect = new SKRect(iconX, iconY, iconX + iconSize, iconY + iconSize);
                canvas.Save();
                _clipPath.Rewind();
                _clipPath.AddRoundRect(iconRect, 4, 4);
                canvas.ClipPath(_clipPath, SKClipOperation.Intersect, true);
                canvas.DrawBitmap(_clipboardIcon, iconRect, _highQualitySampling);
                canvas.Restore();
            }

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
            if (_openLinkIcon != null)
            {
                var btnRect = new SKRect(btnLeft, btnTop, btnRight, btnTop + btnSize);
                canvas.DrawBitmap(_openLinkIcon, btnRect, _highQualitySampling);
            }
        }

        private static void EnsureClipboardIconsLoaded()
        {
            if (_clipboardIconsLoaded) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string clipPath = Path.Combine(baseDir, "data", "image", "clipboard.png");
                string openPath = Path.Combine(baseDir, "data", "image", "open_the_link.png");
                _clipboardIcon ??= TryDecode(clipPath);
                _openLinkIcon ??= TryDecode(openPath);
                // 两张都就绪才标记完成；若文件缺失/解码失败，下一帧继续重试，避免一次失败后永久空白
                _clipboardIconsLoaded = _clipboardIcon != null && _openLinkIcon != null;
            }
            catch (Exception ex) { Logger.Error("加载剪贴板图标失败", ex); }
        }

        private static SKBitmap? TryDecode(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var s = File.OpenRead(path);
                return SKBitmap.Decode(s);
            }
            catch (Exception ex)
            {
                Logger.Error($"加载图标失败: {path}", ex);
                return null;
            }
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

        // 展开态高度：有时间轴时加高 28px，保证进度条与底部按钮互不侵占
        public static float GetExpandedHeight(MediaController media) => media.HasTimeline ? 158f : 130f;

        public static bool HitTimeline(float x, float y)
            => _tlBarX2 > _tlBarX1 && x >= _tlBarX1 - 8f && x <= _tlBarX2 + 8f && Math.Abs(y - _tlBarY) <= 13f;

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

        // 🧩 插件行本帧是否显示：由 NotchWindow 每帧按「岛体总长上限」判定后写入。
        //    媒体控制器开着且歌词很长时，原生内容会吃掉大半个岛，剩余宽度放不下插件行；
        //    这种帧就整行隐藏（对所有插件一视同仁），插件既不绘制、也不留位、命中区同样为空。
        //    默认 true —— 宿主还没跑到判定逻辑时（启动首帧等）插件照常显示。
        private static volatile bool _pluginRowVisible = true;

        /// <summary>
        /// 设置本帧插件行是否显示。
        ///
        /// 判定权在 NotchWindow：它知道原生内容（媒体控制器 / 长歌词自适应 / 硬件占用）本帧要多宽，
        /// 当「原生宽度 + 插件行宽度 &gt; <see cref="MAX_ISLAND_WIDTH"/>」时，就说明剩余长度放不下插件，
        /// 此时整行隐藏所有插件，原生内容照常显示，岛体也不会被撑过上限。
        ///
        /// 只影响「插件行独立贴在原生内容右侧」的非组合模式；组合模式下插件已并入内容顺序表混排，
        /// 宽度统一由 <c>GetCompositeWidth</c> 计算，不受本开关影响。
        /// </summary>
        public static void SetPluginRowVisible(bool visible) => _pluginRowVisible = visible;
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
        /// 返回 0 表示当前没有可显示的插件组件。
        /// 原生内容据此内收右边界，因此插件显示与否、排序如何，都完全不影响任何原生功能；
        /// 对组合模式同样适用：时间日期/硬件占用/媒体控制器先排完，插件一律跟在最后。
        /// </summary>
        public static float GetPluginRowReserve()
        {
            if (!RefreshPluginWidgets()) return 0f;
            float rowW = SumPluginRowWidth(null);
            return rowW > 0f ? rowW + 16f : 0f;
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
        /// 媒体模块右边界：组合模式用它渲染时的真实位置（插件可能被排到媒体右边），
        /// 其他模式按「岛体右边界 - 插件预留区」推算，与原有命中逻辑保持一致。
        /// </summary>
        public static float GetMediaRight(float windowWidth, float currentWidth, bool toastActive)
        {
            if (CompositeModeEnabled && _compositeMediaRight > 0f) return _compositeMediaRight;
            return (windowWidth + currentWidth) / 2f - (toastActive ? 0f : GetPluginRowReserve());
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

                // 宽度与快照同版本一起算好：稳态 60FPS 下不再触碰插件代码，保持零额外开销
                for (int i = 0; i < _pluginWidgets.Length; i++)
                {
                    float w = 0f;
                    try { w = Math.Max(_pluginWidgets[i].MeasureWidth(BASE_HEIGHT), 0f); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    _pluginWidths[i] = w;
                }
                return _pluginWidgets.Length > 0;
            }
        }

        /// <summary>
        /// 按缓存宽度求和（组件间 16px 间距）。pluginIdFilter 不为 null 时只统计该插件的组件。
        /// 返回 0 表示该范围内没有可显示的组件。
        /// </summary>
        private static float SumPluginRowWidth(string? pluginIdFilter)
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
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
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
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
            // 在同一把锁内取齐快照，避免 UI 线程正好重建快照时读到长度不一致的数组
            lock (_pluginSnapshotLock)
            {
                if (_pluginWidgets == null || _pluginWidths == null || _pluginBroken == null) return startX;
                widgets = _pluginWidgets;
                widths = _pluginWidths;
                broken = _pluginBroken;
                drawn = _pluginDrawn;
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
        // 将透明原生窗口的基础画布拓宽至 1200f，给极长歌词预留充足的物理空间，防止被系统窗口边缘裁切
        // 🧩 插件详情页展开时，底层缓冲必须容得下详情页尺寸（+80 / +45 是原有的四周留白）
        public static float WINDOW_WIDTH => Math.Max(1200f, Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))) + 80f);
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

        // 📋 剪贴板链接面板专用缓存（只在链接变化 / 换字体时重建一次，稳态零重算）
        private static readonly List<(string Text, SKTypeface Type, float X)> _cachedClipboardRuns = new();
        private static string _lastClipboardUrl = "";
        private static float _cachedClipboardTextWidth = 0f;
        private static SKRect _clipboardOpenHit;   // 本帧「打开」按钮命中区，帧首作废
        private static SKBitmap? _clipboardIcon;
        private static SKBitmap? _openLinkIcon;
        private static bool _clipboardIconsLoaded;

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

                float left = (WINDOW_WIDTH - currentWidth) / 2f;
                // 岛体物理右边界（背景形状 / 裁剪范围以它为准）
                float islandRight = left + currentWidth;
                // 🧩 插件行独立占据岛体最右侧：为它预留宽度，原生内容右边界相应内收。
                //    这样无论待机显示什么内容、媒体是否激活、是否组合模式，原生布局都保持原样不受影响，
                //    插件也不受原生功能影响，始终稳定显示在岛体最右侧。
                // 🧩 组合模式下插件已被并入「内容顺序表」，与原生模块一起混排（宽度计在 GetCompositeWidth 内），
                //    因此不再单独占用右侧预留区；其他模式仍是整行贴在原生内容右侧。
                float pluginReserve = toast == null && !CompositeModeEnabled ? GetPluginRowReserve() : 0f;
                // 原生内容的右边界（插件预留区之前）；pluginReserve == 0 时与岛体右边界相同
                float right = islandRight - pluginReserve;
                // 组合模式媒体模块右边界（每帧由媒体模块绘制时刷新）；非组合模式置 -1 表示不适用
                _compositeMediaRight = CompositeModeEnabled ? right : -1f;
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
                _bgPath.MoveTo(left + rTopX, 0);
                _bgPath.ConicTo(left, 0, left, rTopY, w);
                _bgPath.LineTo(left, currentHeight - rBottom);
                _bgPath.ConicTo(left, currentHeight, left + rBottom, currentHeight, w);
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
                    if (_lastMediaTitle != media.Title || _lastMediaArtist != media.Artist || _lastLyric != media.CurrentLyric)
                    {
                        // 换歌：整块歌词状态强制重载。_prevLyric 是叠化动画的「淡出层」，
                        // 不清掉的话上一首的最后一句会被带到新歌的第一帧上 —— 切歌残留的视觉来源。
                        bool songChanged = _lastMediaTitle != media.Title || _lastMediaArtist != media.Artist;
                        if (songChanged) _prevLyric = "";

                        _lastMediaTitle = media.Title ?? "";
                        _lastMediaArtist = media.Artist ?? "";

                        // 触发叠化动画
                        if (_lastLyric != media.CurrentLyric)
                        {
                            _prevLyric = songChanged ? "" : _lastLyric;
                            _lastLyric = media.CurrentLyric ?? "";
                            _lyricAnimProgress = 0f;
                            _lyricChangeTime = DateTime.Now;
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
                                DrawKaraoke(canvas, _prevLyric, textX, textY - (10f * easeOut), _textPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                            DrawKaraoke(canvas, _cachedMediaDisplay, textX, textY + (10f * (1f - easeOut)), _textPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                            _textPaint.Color = _currentTextColor.WithAlpha(alpha);
                        }
                        else
                        {
                            DrawKaraoke(canvas, _cachedMediaDisplay, textX, textY, _textPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
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

                            // 展开模式下的平滑叠化渲染 (带卡拉OK)
                            bool isLyricDisplay = !string.IsNullOrEmpty(_lastLyric);
                            if (_lyricAnimProgress < 1f && isLyricDisplay)
                            {
                                float easeOut = 1f - (float)Math.Pow(1f - _lyricAnimProgress, 3);
                                if (!string.IsNullOrEmpty(_prevLyric))
                                {
                                    // 旧歌词淡出时进度直接锁定 100% (1f)
                                    DrawKaraoke(canvas, _prevLyric, textStartX, coverY + 42f - (8f * easeOut), _bodyPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                                }
                                // 新歌词套用当前进度
                                DrawKaraoke(canvas, displaySub, textStartX, coverY + 42f + (8f * (1f - easeOut)), _bodyPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                                _bodyPaint.Color = _currentSubTextColor.WithAlpha(alpha);
                            }
                            else
                            {
                                DrawKaraoke(canvas, displaySub, textStartX, coverY + 42f, _bodyPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
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
                            float centerX = left + currentWidth / 2f;
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
                                    DrawKaraoke(canvas, _prevLyric, textX, textY - (10f * easeOut), _textPaint, (byte)(alpha * (1f - easeOut)), 1f, true);
                                }

                                DrawKaraoke(canvas, _cachedMediaDisplay, textX, textY + (10f * (1f - easeOut)), _textPaint, (byte)(alpha * easeOut), media.CurrentLyricProgress, true);
                                _textPaint.Color = _currentTextColor.WithAlpha(alpha);
                            }
                            else
                            {
                                DrawKaraoke(canvas, _cachedMediaDisplay, textX, textY, _textPaint, alpha, media.CurrentLyricProgress, isLyricDisplay);
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
                        // 居中以「原生内容区」为准（扣除插件预留），插件行不参与居中计算
                        float centerX = left + (currentWidth - pluginReserve) / 2f;
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

                // ================= 🧩 插件组件行（非组合模式：整行贴在原生内容右侧） =================
                // 组合模式下插件已并入「内容顺序表」跟原生模块混排（见上方组合模式分支），这里只处理其余模式：
                // 待机(时间日期/空白/硬件)、媒体激活、媒体展开……原生内容一律不感知插件，插件也不影响原生布局。
                if (!CompositeModeEnabled && pluginReserve > 0f)
                {
                    DrawPluginWidgets(canvas, null, right + 16f, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                }

                // === 下方原本旧版残留的 _wakePath 绘制代码已被彻底删除 ===

                canvas.Restore(); // 1. 恢复 ClipPath 裁切
                canvas.Restore(); // 2. 闭合 SaveLayer 透明层，本体内部渲染彻底完结！任何阴影、遮罩全部随之消失。

                // 独立于本体之外，绘制隐形物理热区与极速渐变唤醒按钮
                if (PassthroughModeEnabled && PassthroughAlpha < 0.99f)
                {
                    // 核心逻辑：2倍速急速消失。只要本体浮现到一半（Alpha>0.5），按钮立刻彻底消失，绝不拖泥带水
                    byte wakeAlpha = (byte)(Math.Max(0f, 1f - PassthroughAlpha * 2f) * 255);

                    float wakeBtnY = (currentHeight - 36f) / 2f; // 对齐内部垂直居中

                    // 垫底一块 Alpha=1 的隐形纯黑热区！肉眼完全不可见，但足以 100% 截断 Windows 物理穿透事件
                    _wakeHitPaint.Color = SKColors.Black.WithAlpha(1);
                    canvas.DrawRect(left, wakeBtnY, 36f, 36f, _wakeHitPaint);

                    if (wakeAlpha > 0)
                    {
                        _wakePaint.Color = SKColors.White.WithAlpha(wakeAlpha);
                        DrawSvgPath(canvas, _wakePaint, left, wakeBtnY, _wakePath);
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

        /// <summary>媒体模块在组合模式下的占宽（缩略图 + 文本 + 间距 + 频谱）。</summary>
        private static float MeasureMediaBlockWidth(MediaController? media)
        {
            float textWidth = (!string.IsNullOrEmpty(media?.CurrentLyric) && MediaController.IsLyricsEnabled)
                ? _textPaint.MeasureText(media!.CurrentLyric)
                : (string.IsNullOrEmpty(media?.Artist)
                    ? _textPaint.MeasureText(media?.Title)
                    : _textPaint.MeasureText(media!.Artist) + _textPaint.MeasureText(media.Title) + 15f);

            float thumbW = media?.Thumbnail != null ? 32f : 0f;
            return thumbW + textWidth + 12f + 21.2f;
        }

        /// <summary>
        /// 组合模式总宽：按「内容顺序表」把原生模块与插件组件依次累加，与 Renderer.Draw 的混排保持一致。
        /// </summary>
        public static float GetCompositeWidth(MediaController media)
        {
            if (!CompositeModeEnabled) return STANDBY_WIDTH;

            RefreshPluginWidgets(); // 插件宽度需与快照同版本，才能算准总宽

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
            // 顺序表的插件 ID 集合，供结尾兜底去重（只补「没进表」的插件，已入表的绝不重复计宽）
            var orderSet = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
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
                else
                {
                    AddModule(SumPluginRowWidth(item)); // 插件组件组
                }
            }

            // 兜底：顺序表里尚未登记的内容按默认次序补上
            if (!clockHandled && CompShowDateTime) AddModule(_cachedTimeWidth + 12f + _cachedDateWidth);
            if (!hardwareHandled && CompShowHardware) AddModule(MeasureHardwareBlockWidth());
            if (!mediaHandled && CompShowMedia && media != null && media.IsActive) AddModule(MeasureMediaBlockWidth(media));
            // 插件兜底：只补「不在顺序表里」的插件宽度（与 Draw 的未绘制兜底一致），已入表的已被主循环累计，绝不重复
            AddModule(SumPluginRowWidthNotIn(orderSet));

            width += 16f; // 右侧边距与 Draw 中每模块尾距(16px)对齐，避免最后一个模块被裁切 6px

            return Math.Clamp(width, 60f, 900f);
        }
    }
}