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
        public static int HoveredExpandedButton = -1; // -1:无, 0:上一首, 1:播放/暂停, 2:下一首
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
            _mediaIconPaint.Color = _currentTextColor;
            _barPaint.Color = _currentTextColor;
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

        private static readonly SKPaint _shadowPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f) };
        private static readonly SKPaint _mediaIconPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint _barPaint = new() { Color = SKColors.White, IsAntialias = true };

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
        // 预加载 Windows 自带 Emoji 彩色字体与零 GC 渲染缓存列表
        private static readonly SKTypeface _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");
        private static readonly List<(string Text, bool IsEmoji, float X)> _cachedToastSenderRuns = new();
        private static readonly List<(string Text, bool IsEmoji, float X)> _cachedToastBodyRuns = new();
        private static float _cachedToastTitleWidth = 0f;
        private static float _cachedToastBodyWidth = 0f;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                float left = (WINDOW_WIDTH - currentWidth) / 2f;
                float right = left + currentWidth;
                int btnPrevX = (int)right - 90;
                int btnPlayX = (int)right - 60;
                int btnNextX = (int)right - 30;

                // 灵动岛悬浮距离顶部的 Y 轴高度 (随过渡进度平滑变化)
                float topY = 12f * styleProgress;

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中，零开销！
                canvas.Translate(0, topY);

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

                // 保留纯粹的透明度叠化，去除多余上浮
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

                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ 媒体控制与待机状态 ] ----------------
                if (media.IsActive)
                {
                    if (_lastMediaTitle != media.Title || _lastMediaArtist != media.Artist || _lastLyric != media.CurrentLyric)
                    {
                        _lastMediaTitle = media.Title ?? "";
                        _lastMediaArtist = media.Artist ?? "";

                        // 触发叠化动画
                        if (_lastLyric != media.CurrentLyric)
                        {
                            _prevLyric = _lastLyric;
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
                else
                {
                    // 零 GC 性能优化：每帧只读取值类型结构体，仅当分钟变化时分配字符串
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

                // ---------------- [ 自定义组合模式 ] ----------------
                if (CompositeModeEnabled)
                {
                    float currentX = left + 16f;
                    float centerY = currentHeight / 2f + textOffsetY;

                    // 1. 时间日期模块
                    if (CompShowDateTime)
                    {
                        _timePaint.Color = _currentTextColor.WithAlpha(alpha);
                        _datePaint.Color = _currentSubTextColor.WithAlpha(alpha);
                        float timeBaselineY = centerY + 5f;
                        canvas.DrawText(_cachedTimeStr, currentX, timeBaselineY, _timePaint);
                        float dateX = currentX + _cachedTimeWidth + 12f;
                        canvas.DrawText(_cachedDateStr, dateX, timeBaselineY, _datePaint);
                        currentX = dateX + _cachedDateWidth + 16f;
                    }

                    // 2. 硬件占用模块
                    if (CompShowHardware)
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

                        currentX = ramX + ramGroupW + 16f;
                    }

                    // 3. 媒体控制器模块（含频谱，媒体激活时才显示）
                    if (CompShowMedia && media.IsActive)
                    {
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

                        // 组合模式媒体控件
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
                            float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f;
                            float spectrumX = right - 16f - totalBarWidth;
                            for (int i = 0; i < 5; i++)
                            {
                                float h = Math.Max(2f, bars[i] * maxH); float y = (currentHeight - h) / 2f;
                                canvas.DrawRoundRect(new SKRect(spectrumX + i * (barWidth + spacing), y, spectrumX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
                            }
                        }
                    }
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
                            canvas.DrawText(_lastMediaTitle, textStartX, coverY + 18f, _titlePaint);

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
                        float centerX = left + currentWidth / 2f;
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
                }

                canvas.Restore();
                canvas.Restore();
            }
            finally
            {
                Monitor.Exit(_renderLock);
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

        private static SKPath CreatePlayPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(10, 6); path.LineTo(0, 12); path.Close(); return path; }
        private static SKPath CreatePausePath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 3, 12)); path.AddRect(new SKRect(6, 0, 9, 12)); return path; }
        private static SKPath CreatePrevPath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 2, 10)); path.MoveTo(8, 0); path.LineTo(2, 5); path.LineTo(8, 10); path.Close(); return path; }
        private static SKPath CreateNextPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(6, 5); path.LineTo(0, 10); path.Close(); path.AddRect(new SKRect(6, 0, 8, 10)); return path; }

        // 文本拆分引擎，实现emoji显示
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
        private static void DrawKaraoke(SKCanvas canvas, string text, float x, float y, SKPaint paint, byte targetAlpha, float progress, bool isLyric)
        {
            // 如果没开启卡拉OK，直接短路渲染普通的实体文字，瞬间返回，0 性能开销
            if (!isLyric || progress <= 0f || !MediaController.IsKaraokeEnabled)
            {
                paint.Color = paint.Color.WithAlpha(targetAlpha);
                canvas.DrawText(text, x, y, paint);
                return;
            }

            // 1. 先画完整的半透明底板 (40% 亮度)
            paint.Color = paint.Color.WithAlpha((byte)(targetAlpha * 0.4f));
            canvas.DrawText(text, x, y, paint);

            // 2. 算出现在应该亮起到多宽
            float scanWidth = paint.MeasureText(text) * progress;

            // 3. 硬件级裁剪高亮部分并覆盖上去
            canvas.Save();
            // y-30 到 y+10 足够包裹住字体的上下最高/低点
            canvas.ClipRect(new SKRect(x, y - 30f, x + scanWidth, y + 10f), SKClipOperation.Intersect, true);
            paint.Color = paint.Color.WithAlpha(targetAlpha);
            canvas.DrawText(text, x, y, paint);
            canvas.Restore();
        }

        public static float GetCompositeWidth(MediaController media)
        {
            if (!CompositeModeEnabled) return STANDBY_WIDTH;

            float width = 16f; // 初始只有左边距 16px
            bool hasPrev = false;

            // 1. 时间日期组件实际宽度
            if (CompShowDateTime)
            {
                width += _cachedTimeWidth + 12f + _cachedDateWidth;
                hasPrev = true;
            }

            // 2. 硬件占用组件实际宽度
            if (CompShowHardware)
            {
                if (hasPrev) width += 16f; // 如果前面有组件，加上 16px 间距
                float cpuLabelW = _tagTextPaint.MeasureText("CPU");
                float ramLabelW = _tagTextPaint.MeasureText("RAM");
                float pctW = _textPaint.MeasureText("100%");
                float cpuTagW = cpuLabelW + 6f;
                float ramTagW = ramLabelW + 6f;
                float cpuGroupW = cpuTagW + 4f + pctW;
                float ramGroupW = ramTagW + 4f + pctW;
                width += cpuGroupW + 16f + ramGroupW;
                hasPrev = true;
            }

            // 3. 媒体控制器组件实际宽度
            bool mediaActive = media != null && media.IsActive;
            if (CompShowMedia && mediaActive)
            {
                if (hasPrev) width += 16f; // 如果前面有组件，加上 16px 间距

                // 加上 MediaController 类前缀
                float textWidth = (!string.IsNullOrEmpty(media.CurrentLyric) && MediaController.IsLyricsEnabled)
                    ? _textPaint.MeasureText(media.CurrentLyric)
                    : (string.IsNullOrEmpty(media.Artist)
                        ? _textPaint.MeasureText(media.Title)
                        : _textPaint.MeasureText(media.Artist) + _textPaint.MeasureText(media.Title) + 15f);

                float thumbW = media.Thumbnail != null ? 32f : 0f;
                float spectrumW = 21.2f;
                float gapBeforeSpectrum = 12f;

                width += thumbW + textWidth + gapBeforeSpectrum + spectrumW;
                hasPrev = true;
            }

            width += 16f; // 加上固定的右侧边距 16px

            return Math.Clamp(width, 60f, 900f);
        }
    }
}