using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        private static readonly SKPaint _layerPaint = new SKPaint(); // 零GC硬件级透明图层

        private static readonly SKPaint _wakePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true }; // 折角箭头（线条）

        private static readonly SKPaint _wakeChipPaint = new SKPaint { Color = SKColors.Black.WithAlpha(WakeChipAlpha), Style = SKPaintStyle.Fill, IsAntialias = true }; // 芯片底色

        private static readonly SKPaint _wakeHitPaint = new SKPaint { Style = SKPaintStyle.Fill }; // 隐形物理热区底板

        private static readonly SKPath _wakePath = CreateWakePath();

        private static readonly SKPath _wakeChipPath = CreateWakeChipPath();

        private const byte WakeChipAlpha = 158;

        // 穿透唤醒按钮的边长（逻辑坐标）。

        public const float WAKE_BTN_SIZE = 36f;

        public static float WakeButtonX => (WINDOW_WIDTH - WAKE_BTN_SIZE) / 2f;

        private static SKPath CreateWakeChipPath()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(5f, 5f, 31f, 31f), 8f, 8f);
            return path;
        }

        // 也避免两段斜线连成一根粗斜杠（那样就认不出是箭头了）。

        private static SKPath CreateWakePath()
        {
            var path = new SKPath();

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

        private static bool _acrylicLogged;

        public static void ApplyThemeColors() // 刷新颜色的方法
        {
            bool isLight = ThemeMode == 1;
            if (ThemeMode == 2) // 跟随系统
            {
                isLight = SystemIsLightTheme;
            }

            // 预计算颜色，避免在渲染树中生成新对象
            // 亚克力 = 模糊背板 + 上层的浅色/深色涂层（真实亚克力的"奶感"就来自这层）。
            // 上限 140/255 ≈ 55%，与设置窗口那套亚克力色调（0x8C）同口径；档位滑到最左 = 纯模糊。
            byte bgAlpha = IslandAcrylic
                ? (byte)Math.Clamp(BgOpacityLevel * 140 / 4, 0, 255)
                : (byte)Math.Clamp(BgOpacityLevel * 255 / 4, 0, 255);

            // 一次性诊断：材质开关一变就记一行 —— 排查「亚克力看不见」时第一时间看它
            if (IslandAcrylic != _acrylicLogged)
            {
                _acrylicLogged = IslandAcrylic;
                Logger.Info($"[亚克力] 材质={(IslandAcrylic ? "开" : "关")}  涂层alpha={bgAlpha}"
                    + $"  档位={BgOpacityLevel}  主题={(isLight ? "浅" : "深")}  底色={(isLight ? "白" : "黑")}");
            }
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
            _tlTextPaint.Color = _currentSubTextColor; // 时间轴时间文本
            _shadowPaint.Color = _currentTextColor.WithAlpha(50);
            _hoverCirclePaint.Color = _currentTextColor.WithAlpha(25);

            _tagTextPaint.Color = _currentTextColor;
            _tagBgPaint.Color = _currentTextColor.WithAlpha(25);  // 浅色半透明背景标签
            _barBgPaint.Color = _currentTextColor.WithAlpha(30);   // 未填充进度条的半透明纯色底槽
        }

        public static Plugins.RenderTheme GetCurrentTheme()
            => new Plugins.RenderTheme(_currentTextColor, _currentSubTextColor, _bgPaint.Color, GLOBAL_DPI, NOTCH_BOTTOM_RADIUS);

        private static readonly object _renderLock = new();

        // 全局复用池 (彻底实现 60FPS 零 GC 分配)

        private static readonly SKPaint _bgPaint = new() { Color = SKColors.Black, IsAntialias = true };

        // 亚克力背板画笔。
        // 这里**故意不带 ImageFilter** —— 每帧带高斯画一次会让 Skia 每帧开临时缓冲，帧率和内存都会崩。
        // 模糊已经在抓到背板时一次性做完了（缩 1/12 + 降半升回的盒式模糊），这里只需平滑放大。
        // ⚠️ 必须用 Low（纯双线性）：Medium 会走 mipmap，Skia 按图像 uniqueID 缓存 mip 层级，
        // 而背板每 250ms 就是一张新图 → 同样只增不减。
        private static readonly SKPaint _acrylicPaint = new()
        { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

        private static readonly SKPaint _fallbackIconPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };

        private static SKTypeface _boldTypeface = FontConfig.Bold;

        private static SKTypeface _normalTypeface = FontConfig.Normal;

        private static SKTypeface _semiBoldTypeface = FontConfig.SemiBold;

        static Renderer()
        {
            FontConfig.Changed += ApplyFont;
            ApplyFont();
        }

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
            _tlTextPaint.Typeface = _semiBoldTypeface; // 时间轴时间文本

            _lastMinute = -1;                 // 时间/日期文本与宽度缓存
            _lastToastId = uint.MaxValue;     // Toast 分段缓存（下一帧强制重建）
            _lastMediaTitle = "";             // 媒体文本度量缓存
            _lastMediaArtist = "";            // 不动 _lastLyric，避免误触发歌词叠化动画

            _cachedToastSenderRuns.Clear();
            _cachedToastBodyRuns.Clear();
            _cachedToastAppNameRuns.Clear();
            _karaokeRuns.Clear();
            _karaokeRuns1.Clear();
            _krKey0 = default;
            _krKey1 = default;
            _fallbackWidths.Clear();
        }

        private static readonly SKPaint _titlePaint = new() { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _bodyPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _textPaint = new() { Color = SKColors.White, TextSize = 12.5f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _shadowPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f) };

        private static readonly SKPaint _mediaIconPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPaint _barPaint = new() { Color = SKColors.White, IsAntialias = true };

        private static readonly SKPaint _tlTextPaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPath _bgPath = new();

        private static readonly SKPath _clipPath = new();

        private static readonly SKPath _playPath = CreatePlayPath();

        private static readonly SKPath _pausePath = CreatePausePath();

        private static readonly SKPath _prevPath = CreatePrevPath();

        private static readonly SKPath _nextPath = CreateNextPath();

        // PNG 图标缓存替换 SVG

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
                    // 每次调用都会重进本分支，所以更必须 Dispose。
                    using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
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
                // 直接极速解码为位图
                using (var stream = DataResources.OpenRead("data/image/qq-icon.png"))
                    if (stream != null) _qqIcon = SKBitmap.Decode(stream);

                using (var stream = DataResources.OpenRead("data/image/wintoast-icon.png"))
                    if (stream != null) _defaultToastIcon = SKBitmap.Decode(stream);
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

        private static readonly SKPaint _compactTimePaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _compactAppPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 10f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _tagTextPaint = new() { Color = SKColors.White, TextSize = 10.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _tagBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPaint _barBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

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

                bool missingInBase = baseTypeface.GetGlyph(cp) == 0;
                bool forcedEmoji = false;

                if (!missingInBase && i + charLen < text.Length)
                {
                    char nextChar = text[i + charLen];
                    if (nextChar == '\uFE0F' || nextChar == '\u200D' || nextChar == '\u20E3')
                    {
                        forcedEmoji = true;
                    }
                }

                if (cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3)
                {
                    forcedEmoji = true;
                }

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

        private static SKTypeface ResolveTypeface(int cp, SKTypeface baseTypeface, bool missingInBase, bool forcedEmoji)
        {
            if (!missingInBase && !forcedEmoji) return baseTypeface;
            if (forcedEmoji) return _emojiTypeface;
            if (IsEmojiCodePoint(cp) && _emojiTypeface.GetGlyph(cp) != 0) return _emojiTypeface;

            if (!FontConfig.HasCustomFont && missingInBase)
            {
                SKTypeface? multi = LyricsFont.Resolve(cp, baseTypeface);
                if (multi != null) return multi;
            }

            if (FontConfig.Fallback.GetGlyph(cp) != 0) return FontConfig.Fallback;
            return _emojiTypeface; // 兜底字体也没有：维持改动前「交给 Emoji 字体」的旧行为
        }

        private static bool IsEmojiCodePoint(int cp)
            => (cp >= 0x1F000 && cp <= 0x1FAFF)   // Emoji 主体区（表情、交通、补充符号等）
            || (cp >= 0x2600 && cp <= 0x27BF)     // 杂项符号与装饰符号
            || (cp >= 0x2B00 && cp <= 0x2BFF)     // 杂项符号与箭头
            || (cp >= 0xFE00 && cp <= 0xFE0F)     // 变体选择符
            || cp == 0x200D || cp == 0x20E3;

        private static readonly List<(string Text, SKTypeface Type, float X)> _measureRuns = new(8);
        private static readonly Dictionary<string, float> _fallbackWidths = new(64);

        public static float MeasureTextWithFallback(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            if (_fallbackWidths.TryGetValue(text, out float cached)) return cached;

            BuildTextRuns(text, _textPaint, _textPaint.Typeface, _measureRuns, out float width);
            if (_fallbackWidths.Count >= 128) _fallbackWidths.Clear();
            _fallbackWidths[text] = width;
            return width;
        }

        private static bool ReuseCachedRuns(string text, SKTypeface baseTypeface, out float totalWidth)
        {
            if (_krKey0.Text == text && ReferenceEquals(_krKey0.Type, baseTypeface)) { totalWidth = _krKey0.Width; return true; }
            if (_krKey1.Text == text && ReferenceEquals(_krKey1.Type, baseTypeface)) { totalWidth = _krKey1.Width; return true; }
            totalWidth = 0f;
            return false;
        }

    }
}
