using SkiaSharp;
using NotchPeninsula.Plugins;
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
        public static IReadOnlyList<IWidget>? WidgetRow = null; // 组件行（内置 + 插件），由 NotchWindow 每帧注入
        public static IReadOnlyList<IWidget>? PluginWidgets = null; // 仅插件组件（组合模式追加用）
        public static IWidget? ActiveDetailWidget = null; // 当前展开详情的插件组件
        public static SKRect ActiveDetailRect; // 详情页 rect（命中检测用）
        public static IReadOnlyList<WidgetLayout.Slot>? WidgetRowSlots = null; // 命中检测 rect 快照
        public static float WidgetRowTopY = 0f; // 排列时的顶部偏移（命中检测用）
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
            return _textPaint.MeasureText(text);
        }
        // 计算Toast消息自适应宽度，限制最大500px
        public static float GetToastAutoWidth()
        {
            float maxTextW = Math.Max(_cachedToastTitleWidth, _cachedToastBodyWidth);
            return Math.Min(Math.Max(TOAST_WIDTH, maxTextW + 68f), 800f);
        }
        public static bool IsMediaExpanded = false;
        public static bool MediaActive = false; // 是否有正在播放的媒体（由媒体插件写入，供自动隐藏/启动动画判断）
        public static int HoveredExpandedButton = -1; // -1:无, 0:上一首, 1:播放/暂停, 2:下一首
        // ===== 剪贴板链接岛 =====
        public const float CLIPBOARD_HEIGHT = 44f;   // 链接岛高度
        public const float CLIPBOARD_ICON = 26f;     // 左侧剪贴板图标边长
        public const float CLIPBOARD_BTN = 26f;      // 右侧跳转按钮边长
        public const float CLIPBOARD_PAD = 12f;      // 左右外边距
        public static bool ClipboardButtonHovered = false; // 跳转按钮是否处于悬停
        private static SKBitmap? _clipboardIcon;
        private static SKBitmap? _openLinkIcon;

        // 链接岛自适应宽度：左图标 + 间距 + 文本 + 间距 + 跳转按钮，并限制最大宽度防止岛体过长
        public static float GetClipboardAutoWidth(string? link)
        {
            float textW = string.IsNullOrEmpty(link) ? 0f : _textPaint.MeasureText(link);
            float width = CLIPBOARD_PAD + CLIPBOARD_ICON + 10f + textW + 10f + CLIPBOARD_BTN + CLIPBOARD_PAD;
            return Math.Min(Math.Max(width, 176f), 520f);
        }
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

            // 渐变着色器需要重新生成一次，但必须先手动释放旧的，防止非托管内存泄漏
            _fadePaint.Shader?.Dispose();
            _fadePaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(1, 0),
                [bg.WithAlpha(0), bg],
                null, SKShaderTileMode.Clamp);

        }

        // 暴露当前主题快照给插件层（Phase 1 桥接）
        public static Plugins.RenderTheme GetCurrentTheme() => new(
            _currentTextColor,
            _currentSubTextColor,
            _bgPaint.Color,
            GLOBAL_DPI,
            NOTCH_BOTTOM_RADIUS);

        // 动态计算最大边界，防止因刘海变大导致出界
        // 将透明原生窗口的基础画布拓宽至 1200f，给极长歌词预留充足的物理空间，防止被系统窗口边缘裁切
        public static float WINDOW_WIDTH => Math.Max(1200f, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH)) + 80f);
        public static float MAX_WINDOW_HEIGHT => Math.Max(220f, Math.Max(BASE_HEIGHT, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT)) + 45f);

        public const int OUTER_R = 14;
        public const int INNER_R = 12;

        private static readonly object _renderLock = new();

        // 🚀 全局复用池 (彻底实现 60FPS 零 GC 分配)
        private static readonly SKPaint _bgPaint = new() { Color = SKColors.Black, IsAntialias = true };
        private static readonly SKPaint _fallbackIconPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };

        private static readonly SKTypeface _boldTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        private static readonly SKTypeface _normalTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        private static readonly SKTypeface _semiBoldTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        private static readonly SKPaint _titlePaint = new() { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = _boldTypeface };
        private static readonly SKPaint _bodyPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = _normalTypeface };
        private static readonly SKPaint _textPaint = new() { Color = SKColors.White, TextSize = 12.5f, IsAntialias = true, Typeface = _semiBoldTypeface };


        private static readonly SKShader _fadeShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(1, 0),
            [SKColors.Black.WithAlpha(0), SKColors.Black],
            null, SKShaderTileMode.Clamp);
        private static readonly SKPaint _fadePaint = new() { Shader = _fadeShader };

        private static readonly SKPath _bgPath = new();
        private static readonly SKPath _clipPath = new();

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
                string clipboardPath = Path.Combine(baseDir, "data", "image", "Clipboard.png");
                string openLinkPath = Path.Combine(baseDir, "data", "image", "open_the_link.png");

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

                if (File.Exists(clipboardPath))
                {
                    using var stream = File.OpenRead(clipboardPath);
                    _clipboardIcon = SKBitmap.Decode(stream);
                }

                if (File.Exists(openLinkPath))
                {
                    using var stream = File.OpenRead(openLinkPath);
                    _openLinkIcon = SKBitmap.Decode(stream);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载 PNG 图标失败", ex);
            }
            finally { _iconsLoaded = true; }
        }

        // 高频字符串与排版宽度缓存

        private static uint _lastToastId = 0;
        // 歌词动画专属独立变量
        // 待机时间显示专用画笔
        // 硬件监控零 GC 缓存池 (预热101个字符串，避免每帧 ToString 分配内存)
        // 硬件监控平滑过渡与标签零 GC 缓存

        private static string _cachedToastSender = "";
        private static string _cachedToastBody = "";
        // 预加载 Windows 自带 Emoji 彩色字体与零 GC 渲染缓存列表
        private static readonly SKTypeface _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");
        private static readonly List<(string Text, bool IsEmoji, float X)> _cachedToastSenderRuns = new();
        private static readonly List<(string Text, bool IsEmoji, float X)> _cachedToastBodyRuns = new();
        private static float _cachedToastTitleWidth = 0f;
        private static float _cachedToastBodyWidth = 0f;

        public static void Draw(SKCanvas canvas, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f, string? clipboardLink = null)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                float left = (WINDOW_WIDTH - currentWidth) / 2f;
                float right = left + currentWidth;

                // 灵动岛悬浮距离顶部的 Y 轴高度 (随过渡进度平滑变化)
                float topY = 12f * styleProgress;
                WidgetRowTopY = topY;

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
                _bgPath.LineTo(right - rBottom, currentHeight);
                _bgPath.ConicTo(right, currentHeight, right, currentHeight - rBottom, w);
                _bgPath.LineTo(right, rTopY);
                _bgPath.ConicTo(right, 0, right - rTopX, 0, w);
                _bgPath.Close();

                canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                byte alpha = (byte)(255 * startupProgress * transitionAlpha);
                float textOffsetY = 0f;

                // 仅恢复原版代码中软件刚启动时的位移，不影响状态切换
                if (!MediaActive && startupProgress < 1f)
                {
                    textOffsetY = (1f - startupProgress) * 15f;
                }

                SKColor currentA = _currentTextColor.WithAlpha(alpha);
                SKColor subA = _currentSubTextColor.WithAlpha(alpha);

                _titlePaint.Color = currentA;
                _bodyPaint.Color = subA;
                _textPaint.Color = currentA;
                _highQualitySampling.Color = SKColors.White.WithAlpha(alpha); // 同步作用于图片图标

                // ---------------- [ 剪贴板链接 ] ----------------
                if (!string.IsNullOrEmpty(clipboardLink))
                {
                    DrawClipboardLink(canvas, clipboardLink!, left, right, currentHeight, alpha);

                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ Toast 消息通知 ] ----------------
                if (toast != null)
                {
                    DrawToast(canvas, toast, left, right, currentHeight);
                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ 插件详情页（优先级最高） ] ----------------
                if (ActiveDetailWidget?.DetailPage != null)
                {
                    DrawPluginDetail(canvas, left, right, currentWidth, currentHeight, alpha, textOffsetY, isHovered, bars);
                }
                // ---------------- [ 统一组件行 ] ----------------
                else if (WidgetRow is { Count: > 0 })
                {
                    DrawWidgetRow(canvas, left, right, currentHeight, alpha, textOffsetY, isHovered, bars);
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

        // 组合模式：时间日期 + 硬件 + 媒体 一行横排

        // 独立媒体（折叠 / 展开）

        // Toast 消息通知（图标 + 标题 + 正文 + 淡出遮罩）
        private static void DrawToast(SKCanvas canvas, ToastData toast, float left, float right, float currentHeight)
        {
            if (_lastToastId != toast.NotificationId)
            {
                _lastToastId = toast.NotificationId;
                _cachedToastSender = !string.IsNullOrEmpty(toast.Title) ? toast.Title : (!string.IsNullOrEmpty(toast.AppName) ? toast.AppName : "通知");
                _cachedToastBody = toast.Body ?? "";

                // 只在接收到新消息时分配一次内存
                BuildTextRuns(_cachedToastSender, _titlePaint, _boldTypeface, _cachedToastSenderRuns, out _cachedToastTitleWidth);
                BuildTextRuns(_cachedToastBody, _bodyPaint, _normalTypeface, _cachedToastBodyRuns, out _cachedToastBodyWidth);
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

            float textSpacing = 5f;
            float totalTextHeight = 13.5f + 11.5f + textSpacing;
            float toastTextY = (currentHeight - totalTextHeight) / 2f;

            float line1Y = toastTextY + 11.5f;
            float line2Y = line1Y + 13.5f + textSpacing;

            // 渲染标题：自动在常规字体与 Emoji 字体间热切换
            foreach (var run in _cachedToastSenderRuns)
            {
                _titlePaint.Typeface = run.IsEmoji ? _emojiTypeface : _boldTypeface;
                canvas.DrawText(run.Text, toastTextX + run.X, line1Y, _titlePaint);
            }
            _titlePaint.Typeface = _boldTypeface; // 重置

            // 渲染内容主体
            foreach (var run in _cachedToastBodyRuns)
            {
                _bodyPaint.Typeface = run.IsEmoji ? _emojiTypeface : _normalTypeface;
                canvas.DrawText(run.Text, toastTextX + run.X, line2Y, _bodyPaint);
            }
            _bodyPaint.Typeface = _normalTypeface; // 重置

            if ((toastTextX + _cachedToastTitleWidth > toastMaxTextRight) || (toastTextX + _cachedToastBodyWidth > toastMaxTextRight))
            {
                float fadeWidth = 15f;
                float fadeStart = toastMaxTextRight - fadeWidth;

                canvas.Save();
                canvas.Translate(fadeStart, 0);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0, 0, 1, 1, _fadePaint);
                canvas.Restore();

                canvas.DrawRect(toastMaxTextRight, 0, WINDOW_WIDTH, currentHeight, _bgPaint);
            }
        }

        // 插件详情页（展开态）
        private static void DrawPluginDetail(SKCanvas canvas, float left, float right, float currentWidth, float currentHeight, byte alpha, float textOffsetY, bool isHovered, float[]? bars)
        {
            var detail = ActiveDetailWidget?.DetailPage;
            if (detail == null) return;
            ActiveDetailRect = new SKRect(left, 0, right, currentHeight);
            var frame = new WidgetFrame(GetCurrentTheme(), alpha, textOffsetY, bars, isHovered);
            detail.Draw(canvas, ActiveDetailRect, frame);
        }

        // 待机：组件行（内置 + 插件，横排）
        private static void DrawWidgetRow(SKCanvas canvas, float left, float right, float currentHeight, byte alpha, float textOffsetY, bool isHovered, float[]? bars)
        {
            if (WidgetRow is not { Count: > 0 }) return;
            var frame = new WidgetFrame(GetCurrentTheme(), alpha, textOffsetY, bars, isHovered);
            var slots = WidgetLayout.ArrangeRow(WidgetRow, left + 16f, 0, currentHeight, 12f);
            WidgetRowSlots = slots; // 存快照供命中检测
            foreach (var slot in slots)
            {
                slot.Widget.Draw(canvas, slot.Rect, frame);
            }
        }

        // 时钟紧凑宽度（行内）


        // 文本拆分引擎，实现emoji显示
        // 剪贴板链接岛布局： [剪贴板 logo] 链接文本 [跳转按钮]
        private static void DrawClipboardLink(SKCanvas canvas, string link, float left, float right, float currentHeight, byte alpha)
        {
            EnsureIconsLoaded();

            float centerY = currentHeight / 2f;

            // 左侧：剪贴板 logo
            float iconX = left + CLIPBOARD_PAD;
            float iconY = centerY - CLIPBOARD_ICON / 2f;
            var iconRect = new SKRect(iconX, iconY, iconX + CLIPBOARD_ICON, iconY + CLIPBOARD_ICON);
            if (_clipboardIcon != null) canvas.DrawBitmap(_clipboardIcon, iconRect, _highQualitySampling);
            else canvas.DrawRoundRect(iconRect, 4f, 4f, _fallbackIconPaint);

            // 右侧：跳转按钮
            float btnX = right - CLIPBOARD_PAD - CLIPBOARD_BTN;
            float btnY = centerY - CLIPBOARD_BTN / 2f;
            var btnRect = new SKRect(btnX, btnY, btnX + CLIPBOARD_BTN, btnY + CLIPBOARD_BTN);

            float textStartX = iconRect.Right + 10f;
            float textEndX = btnRect.Left - 10f;

            // 中间：链接文本（单行左对齐，超出可用宽度时在按钮前渐隐收尾）
            _textPaint.Color = _currentTextColor.WithAlpha(alpha);
            var fm = _textPaint.FontMetrics;
            float baselineY = centerY - (fm.Ascent + fm.Descent) / 2f;

            canvas.Save();
            canvas.ClipRect(new SKRect(textStartX, 0f, textEndX, currentHeight));
            canvas.DrawText(link, textStartX, baselineY, _textPaint);

            if (_textPaint.MeasureText(link) > textEndX - textStartX)
            {
                float fadeWidth = 15f;
                float fadeStart = textEndX - fadeWidth;
                canvas.Save();
                canvas.Translate(fadeStart, 0f);
                canvas.Scale(fadeWidth, currentHeight);
                canvas.DrawRect(0f, 0f, 1f, 1f, _fadePaint);
                canvas.Restore();
                canvas.DrawRect(fadeStart, 0f, WINDOW_WIDTH, currentHeight, _bgPaint);
            }
            canvas.Restore();

            // 跳转按钮：悬停高亮 + 图标
            if (ClipboardButtonHovered)
                canvas.DrawCircle(btnRect.MidX, btnRect.MidY, CLIPBOARD_BTN / 2f + 4f, _hoverCirclePaint);

            if (_openLinkIcon != null) canvas.DrawBitmap(_openLinkIcon, btnRect, _highQualitySampling);
            else
            {
                // 图标加载失败的兜底：画一个简单的外链箭头
                using var arrow = new SKPath();
                arrow.MoveTo(btnX + 7f, btnY + 19f);
                arrow.LineTo(btnX + 19f, btnY + 7f);
                arrow.MoveTo(btnX + 11f, btnY + 7f);
                arrow.LineTo(btnX + 19f, btnY + 7f);
                arrow.LineTo(btnX + 19f, btnY + 15f);
                var arrowPaint = new SKPaint
                {
                    Color = _currentTextColor.WithAlpha(alpha),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2f,
                    StrokeJoin = SKStrokeJoin.Round,
                    StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawPath(arrow, arrowPaint);
            }
        }

        private static void DrawSvgPath(SKCanvas canvas, SKPaint paint, float x, float y, SKPath path, float scale = 1f)
        {
            canvas.Save();
            canvas.Translate(x, y);
            if (scale != 1f) canvas.Scale(scale);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        private static void BuildTextRuns(string text, SKPaint paint, SKTypeface baseTypeface, List<(string Text, bool IsEmoji, float X)> runs, out float totalWidth)
        {
            runs.Clear();
            totalWidth = 0;
            if (string.IsNullOrEmpty(text)) return;

            int start = 0;
            bool currentIsEmoji = false;

            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                int charLen = 1;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                {
                    cp = char.ConvertToUtf32(text, i);
                    charLen = 2;
                }

                // 基础判定：默认字体里没有这个字，那就是 Emoji
                bool isEmoji = baseTypeface.GetGlyph(cp) == 0;

                // 1. 向前探测：如果当前字符（比如 # 或 ⛸）后面紧跟了 Emoji 变体选择器(FE0F)或零宽连字(200D)，
                // 说明它是 Emoji 组合的开头，强制视为 Emoji，防止被默认字体抢走。
                if (!isEmoji && i + charLen < text.Length)
                {
                    char nextChar = text[i + charLen];
                    if (nextChar == '\uFE0F' || nextChar == '\u200D' || nextChar == '\u20E3')
                    {
                        isEmoji = true;
                    }
                }

                // 2. 修饰符绑定：这些不可见字符本身必须作为 Emoji 处理，不能断开
                if (cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3)
                {
                    isEmoji = true;
                }

                // 3. 肤色修饰符 (U+1F3FB ~ U+1F3FF)，强制绑定为 Emoji
                if (cp >= 0x1F3FB && cp <= 0x1F3FF)
                {
                    isEmoji = true;
                }

                if (i == 0) currentIsEmoji = isEmoji; // 初始化第一个状态

                // 只有当字体类型发生真正的改变时，才进行安全切割
                if (isEmoji != currentIsEmoji)
                {
                    string sub = text.Substring(start, i - start);
                    runs.Add((sub, currentIsEmoji, totalWidth));
                    paint.Typeface = currentIsEmoji ? _emojiTypeface : baseTypeface;
                    totalWidth += paint.MeasureText(sub);

                    currentIsEmoji = isEmoji;
                    start = i;
                }

                if (charLen == 2) i++; // 跳过代理对的后半段
            }

            // 处理收尾文本
            if (start < text.Length)
            {
                string sub = text.Substring(start);
                runs.Add((sub, currentIsEmoji, totalWidth));
                paint.Typeface = currentIsEmoji ? _emojiTypeface : baseTypeface;
                totalWidth += paint.MeasureText(sub);
            }

            paint.Typeface = baseTypeface; // 重置画笔
        }

        // 卡拉OK渲染引擎
    }
}
