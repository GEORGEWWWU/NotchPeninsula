using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
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

        // 待机时间显示专用画笔
        private static readonly SKPaint _timePaint = new() { Color = SKColors.White, TextSize = 14.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _datePaint = new() { Color = new SKColor(200, 200, 200), TextSize = 14.5f, IsAntialias = true, Typeface = _normalTypeface };
        // 紧凑模式右侧小号信息画笔：“现在”用纯色(跟随主题明暗)，应用名用灰色(小号)

        private static readonly SKPaint _compactTimePaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _compactAppPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 10f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _tagTextPaint = new() { Color = SKColors.White, TextSize = 10.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _tagBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPaint _barBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

        // 预加载 Windows 自带 Emoji 彩色字体与零 GC 渲染缓存列表
        private static readonly SKTypeface _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");

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

    }
}
