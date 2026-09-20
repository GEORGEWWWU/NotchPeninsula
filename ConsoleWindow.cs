using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public class ConsoleWindow
    {
        private static ConsoleWindow? _instance;
        private readonly IntPtr _hwnd;
        private IntPtr _backdropHwnd;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static bool _classRegistered = false;

        private enum BackdropMaterialMode
        {
            SolidDark,
            Acrylic,
            Mica
        }

        private BackdropMaterialMode _backdropMode = BackdropMaterialMode.SolidDark;

        private const int WIDTH = 600;
        private const int HEIGHT = 660;
        private const int TITLE_BAR_HEIGHT = 32;

        // 🧩 插件行排序小三角（渲染与鼠标命中必须使用同一组坐标）
        //    所有按钮均在下行（名称独占上行），按钮从左到右：← → [重载] [移除] [开关]
        private const float SORT_TRI_W = 16f;
        private const float PLUGIN_SORT_LEFT_X = 364f;   // ← 左移
        private const float PLUGIN_SORT_RIGHT_X = 382f;  // → 右移
        private const float PLUGIN_BTN_RELOAD_X = 404f;  // 重载按钮
        private const float PLUGIN_BTN_REMOVE_X = 460f;  // 移除按钮
        private const float PLUGIN_BTN_TOGGLE_X = 516f;  // 开关按钮

        // 🔤 通用设置页「切换灵动岛字体」卡片（渲染与鼠标命中必须使用同一组坐标）
        // 📐 通用设置页卡片顺序（2026-09-20 调整后）：
        //    开机自启 12 | 窗口置顶 84 | 通知卡片（两行）156..280 | 剪贴板链接检测 290 | 切换灵动岛字体 362
        private const float FONT_CARD_Y = 362f;        // 卡片相对标题栏的纵向偏移
        private const float FONT_BTN_H = 26f;          // 按钮高度
        private const float FONT_BTN_Y = FONT_CARD_Y + 18f;
        private const float FONT_RESET_W = 56f;        // [重置] 按钮宽度
        private const float FONT_PICK_W = 78f;         // [选择字体] 按钮宽度
        private const float FONT_RESET_X = WIDTH - 36 - FONT_RESET_W;
        private const float FONT_PICK_X = FONT_RESET_X - 10 - FONT_PICK_W;

        // 🎚 媒体设置页「目标媒体平台 + 匹配方式」合并卡片（渲染与鼠标命中必须使用同一组坐标）
        // 📐 媒体设置页卡片顺序（2026-09-20 合并后）：
        //    媒体控制 12..74 | 合并卡片（两行）84..208 | 歌词设置 222..398
        //    合并卡片：第 1 行「目标媒体平台」行首 84、分隔线 142、第 2 行「匹配方式」行首 146（行距 62）
        // ⚠️ 第 1 行下拉框 +96..+128 的命中判定写在 WM_MOUSEMOVE 的 tab 2 段里（+98..+128），
        //    第 2 行选项框/下拉菜单命中直接读下面的 MATCH_ROW_Y / MATCH_MENU_Y —— 改这里即两侧同时生效。
        private const float PLATFORM_CARD_Y = TITLE_BAR_HEIGHT + 84f;    // 合并卡片顶部
        private const float PLATFORM_ROW2_Y = TITLE_BAR_HEIGHT + 146f;   // 第 2 行「匹配方式」行首
        private const float LYRIC_CARD_Y = TITLE_BAR_HEIGHT + 222f;      // 歌词设置卡片顶部
        private const float MATCH_BOX_W = 110f;        // 两个选项框宽度
        private const float MATCH_BOX_H = 32f;
        private const float MATCH_MODE_X = 340f;       // 左框：自动匹配 / 手动选择软件
        private const float MATCH_APP_X = 460f;        // 右框：手动模式下的目标软件
        private const float MATCH_ROW_Y = TITLE_BAR_HEIGHT + 158f;   // 选项框顶部（= 第 2 行行首 +12）
        private const float MATCH_MENU_Y = TITLE_BAR_HEIGHT + 192f;  // 下拉菜单顶部（= 选项框底 +2）
        private const float MATCH_MENU_RIGHT = 570f;   // 软件菜单右边界
        private const float APP_MENU_W = 280f;         // 软件菜单宽度

        private bool _minHovered = false;
        private bool _closeHovered = false;
        private static SKBitmap? _appIconBitmap;
        private static string _appTitleWithVersion = "NotchPeninsula";

        // 侧边栏与通用设置状态
        private int _selectedTab = 0;
        private int _hoveredTab = -1;
        private bool _isAutoStartEnabled;
        private bool _toggleHovered = false;
        private bool _toastToggleHovered = false;
        private bool _topmostToggleHovered = false;
        private bool _clipboardToggleHovered = false; // 📋「剪贴板链接检测」（2026-09-20 从交互设置搬到通用设置）
        // 灵动岛字体切换状态（字体本身由 FontConfig 统一持有）
        private bool _fontPickHovered = false;
        private bool _fontResetHovered = false;
        private string _fontHint = ""; // 加载失败时在卡片副标题上直接提示，避免弹窗打断操作
        // 交互设置状态
        private bool _autoHideToggleHovered = false;
        private bool _pauseHideToggleHovered = false; // 「暂停播放后自动隐藏」——自动隐藏卡片的第二行
        private bool _fsHideToggleHovered = false;    // 「全屏自动隐藏」——自动隐藏卡片的第三行
        private bool _mediaExpToggleHovered = false;
        private bool _passToggleHovered = false;

        // 媒体设置状态
        private bool _mediaToggleHovered = false;
        private bool _dropdownOpen = false;
        private bool _dropdownHovered = false;
        private int _hoveredDropdownIndex = -1;
        private int _selectedPlatformIndex = 0;
        // 通用媒体匹配方式（左：自动匹配/手动选择软件；右：手动模式下的目标软件，直接显示 AppID）
        private bool _matchModeDropdownOpen = false;
        private bool _matchModeDropdownHovered = false;
        private int _hoveredMatchModeIndex = -1;
        private bool _appDropdownOpen = false;
        private bool _appDropdownHovered = false;
        private int _hoveredAppIndex = -1;
        private string[] _appOptions = [];
        private static readonly string[] _matchModeOptions = ["自动匹配", "手动选择软件"];
        // 消息通知内容状态（缩略/完整）
        private bool _toastModeDropdownOpen = false;
        private bool _toastModeDropdownHovered = false;
        private int _hoveredToastModeIndex = -1;
        private int _selectedToastModeIndex = 0; // 0=缩略, 1=紧凑, 2=完整
        private static readonly string[] _toastModeOptions = ["缩略", "紧凑", "完整"];
        private float _savedToastW = -1f; // 切到完整模式前的用户消息宽度快照
        private float _savedToastH = -1f; // 切到完整模式前的用户消息高度快照
        private bool _lyricToggleHovered = false;
        private bool _transToggleHovered = false;
        private bool _karaokeToggleHovered = false;
        private bool _lyricMinusHovered = false;
        private bool _lyricPlusHovered = false;
        private bool _lyricResetHovered = false;
        // 关于页交互状态
        private int _hoveredLinkIndex = -1;

        // 插件中心交互状态
        private int _hoveredPluginAction = -1;  // 0=导入 DLL, 1=打开目录, 2=插件市场
        private int _hoveredPluginToggle = -1;  // 行索引：启用/禁用开关
        private int _hoveredPluginReload = -1;  // 行索引：热重载
        private int _hoveredPluginRemove = -1;  // 行索引：移除
        private int _hoveredPluginMoveLeft = -1;   // 行索引：左移（调整灵动岛显示顺序）
        private int _hoveredPluginMoveRight = -1;  // 行索引：右移
        private List<PluginEntry> _pluginView = new();

        // 显示设置
        private int _selectedDisplayIndex = 0;
        private int _hoveredDisplayOptionIndex = -1;
        private static readonly string[] _displayOptions = ["时间日期", "空白"];
        private int _hoveredStyleIndex = -1;
        private bool _monitorDropdownOpen = false;
        private bool _monitorDropdownHovered = false;
        private int _hoveredMonitorDropdownIndex = -1;
        // 组合模式 UI 状态
        private bool _compositeToggleHovered = false;
        private bool _compDateTimeHovered = false;
        private bool _compHardwareHovered = false;
        private bool _compMediaHovered = false;
        private bool _isHoveringDisabledArea = false;

        private static string[] _monitorOptions = GetInitialMonitorOptions();

        private static string[] GetInitialMonitorOptions()
        {
            try
            {
                var screens = Screen.AllScreens;
                string[] opts = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                    opts[i] = screens[i].Primary ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}";
                return opts;
            }
            catch
            {
                return ["显示器 1 (主)"];
            }
        }
        // 个性化中心状态
        private int _hoveredMinusIndex = -1;
        private int _hoveredPlusIndex = -1;
        private int _hoveredResetIndex = -1;
        // 硬件检测模式切换前的待机宽度快照（用于切回时恢复）
        private float _savedStandbyWidth = -1f;
        private float[] _customValues = new float[8];
        // 「恢复默认」用的出厂值，顺序 = [待机宽, 待机高, 媒体宽, 媒体高, 通知宽, 通知高, DPI, 底部圆角]。
        // ⚠️ 这三个地方必须同步改，否则「恢复默认」和首次安装会给出不同的值：
        //    ① 本数组 ② Program.LoadSettings 里 key.GetValue 的兜底值 ③ Renderer 的字段初值
        private static readonly float[] _defaultCustomValues = [125f, 29f, 250f, 35f, 260f, 55f, 1.0f, 12f];
        private readonly string[] _valStrCache = new string[8];
        private int _hoveredThemeIndex = -1; // -1:无, 0:黑, 1:白, 2:系统
        private int _hoveredOpacityIndex = -1;
        // DPI 缩放相关
        private float _dpiScale = 1f;
        private int _scaledWidth;
        private int _scaledHeight;
        // 预设媒体平台数组
        private static readonly (string Id, string Name)[] _platforms = [
            ("other", "自动媒体"),
            ("browser", "浏览器媒体"),
            ("netease", "网易云音乐"),
            ("qqmusic", "QQ音乐"),
            ("kugou", "酷狗音乐"),
            ("spotify", "Spotify"),
            ("applemusic", "Apple Music"),
            ("echomusic", "Echo Music"),
            ("lxmusic", "LX Music")
        ];
        // 极致内存优化：全局复用画笔缓存
        private static readonly SKPaint _bgPaint = new SKPaint { Color = new SKColor(32, 32, 32), IsAntialias = true };
        private static readonly SKPaint _titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };
        private static readonly SKPaint _uiTextPaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _subTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 12f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _titleTextPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 12.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _hqSamplingOpts = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        private static readonly SKPaint _iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

        // 窗口控制按钮画笔
        private static readonly SKPaint _hoverMinPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20) };
        private static readonly SKPaint _hoverClosePaint = new SKPaint { Color = new SKColor(232, 17, 35) };

        // 侧边栏与卡片画笔
        private static readonly SKPaint _tabBgSelected = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };
        private static readonly SKPaint _tabBgHovered = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
        private static readonly SKPaint _tabIndicator = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };
        private static readonly SKPaint _cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
        private static readonly SKPaint _cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        private static readonly SKPaint _separatorPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20), StrokeWidth = 1, IsAntialias = true };

        // UI 组件画笔
        private static readonly SKPaint _chevronPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        private static readonly SKPaint _menuBg = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true };
        private static readonly SKPaint _menuBorder = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        private static readonly SKPaint _globalBorderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        private static readonly SKPaint _toggleCirclePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        // 动态状态画笔
        private static readonly SKPaint _dynamicFillPaint = new SKPaint { IsAntialias = true };
        private static readonly SKPaint _dynamicStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        private static readonly SKPaint _dynamicTextPaint = new SKPaint { TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        public static void Toggle()
        {
            if (_instance == null)
                _instance = new ConsoleWindow();
            else
            {
                _instance._isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
                _instance.Render();
                if (_instance._backdropHwnd != IntPtr.Zero)
                    Win32.ShowWindow(_instance._backdropHwnd, Win32.SW_RESTORE);
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                _instance.SyncBackdropToContent();
                Win32.SetForegroundWindow(_instance._hwnd);
            }
        }

        private ConsoleWindow()
        {
            // 先挂到静态实例上：CreateWindowEx 期间系统可能立刻发 WM_PAINT/WM_CREATE，
            // 如果此时 StaticWndProc 还看不到实例，初次打开就只会看到“空的模糊底板”。
            _instance = this;
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
            _customValues[0] = Renderer.STANDBY_WIDTH;
            _customValues[1] = Renderer.BASE_HEIGHT;
            _customValues[2] = Renderer.MEDIA_WIDTH;
            _customValues[3] = Renderer.MEDIA_HEIGHT;
            _customValues[4] = Renderer.TOAST_WIDTH;
            _customValues[5] = Renderer.TOAST_HEIGHT;
            _customValues[6] = Renderer.GLOBAL_DPI;
            _customValues[7] = Renderer.NOTCH_BOTTOM_RADIUS;

            // 匹配目前加载的媒体平台索引
            for (int i = 0; i < _platforms.Length; i++)
            {
                if (_platforms[i].Id == MediaController.TargetPlatform)
                {
                    _selectedPlatformIndex = i; break;
                }
            }

            // 消息通知内容模式（0=缩略, 1=完整）
            _selectedToastModeIndex = Renderer.IsToastFullMode ? 2 : (Renderer.IsToastCompactMode ? 1 : 0);

            if (!_classRegistered)
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    _appTitleWithVersion = $"NotchPeninsula {version.Major}.{version.Minor}.{version.Build}";
                }

                IntPtr appIconHandle = IntPtr.Zero;
                try
                {
                    // 提取系统级小图标 (专供窗口注册和任务栏底层使用)
                    var sysIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (sysIcon != null) appIconHandle = sysIcon.Handle;

                    string iconPath = Path.Combine(AppContext.BaseDirectory, "NPS_NotchPeninsula-logo.ico");

                    // 使用 SkiaSharp 直接解码 ICO，绕过 System.Drawing 的低质缩放
                    // SKBitmap.Decode 对 ICO 会自动选取容器中最大/最匹配的帧，且支持 256px PNG 压缩帧
                    if (File.Exists(iconPath))
                    {
                        _appIconBitmap = SKBitmap.Decode(iconPath);
                    }

                    // 兜底：如果外部文件丢失或解码失败，用系统图标转存
                    if (_appIconBitmap == null && sysIcon != null)
                    {
                        using var bmp = sysIcon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        ms.Position = 0;
                        _appIconBitmap = SKBitmap.Decode(ms);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("解析高清图标失败", ex);
                }

                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc,
                    hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
                    lpszClassName = "NotchConsoleClass",
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
                    hIcon = appIconHandle
                };
                Win32.RegisterClass(ref wc);
                _classRegistered = true;
            }

            _dpiScale = Win32.GetDpiForSystem() / 96f;
            _scaledWidth = (int)(WIDTH * _dpiScale);
            _scaledHeight = (int)(HEIGHT * _dpiScale);

            int screenWidth = Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            int left = (screenWidth - _scaledWidth) / 2;
            int top = (screenHeight - _scaledHeight) / 2;
            IntPtr hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;

            // 采用“双窗口”结构：
            // 1) 背景窗：普通 DWM HWND，只负责 Acrylic / Mica 材质；
            // 2) 内容窗：继续使用 layered + UpdateLayeredWindow，负责 Skia 前景 UI。
            // 这样既能拿到真实背景材质，又能保留前景的 per-pixel alpha，不会再把历史帧叠进客户区造成残影。
            _backdropHwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                "NotchConsoleClass", "NotchPeninsulaBackdrop",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            ApplyRoundedRegion(_backdropHwnd);
            TryEnableBackdropMaterial();
            ApplyBackdropPalette();

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW,
                "NotchConsoleClass", "NotchPeninsula",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            SyncBackdropToContent();

            for (int i = 0; i < 8; i++)
            {
                UpdateValueString(i);
            }

            System.Threading.Tasks.Task.Run(() => {
                var screens = Screen.AllScreens;
                string[] opts = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                    opts[i] = screens[i].Primary ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}";
                _monitorOptions = opts;
                if (Renderer.TargetMonitorIndex >= screens.Length) Renderer.TargetMonitorIndex = 0;

                // 异步加载完成后，主线程安全触发一次UI重绘
                if (_instance != null)
                {
                    _instance.Render();
                }
            });

            _selectedDisplayIndex = Renderer.StandbyDisplayMode; // 初始化时同步当前选择

            // 如果启动时就是硬件检测模式，标记快照为未记录（-1），
            // 这样切走时会回退到默认 130px
            if (Renderer.StandbyDisplayMode == 2)
                _savedStandbyWidth = -1f;

            Render();
        }

        private void ApplyRoundedRegion(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            int radius = Math.Max(12, (int)MathF.Round(8f * _dpiScale * 2f));
            IntPtr region = Win32.CreateRoundRectRgn(0, 0, _scaledWidth + 1, _scaledHeight + 1, radius, radius);
            if (region != IntPtr.Zero)
            {
                _ = Win32.SetWindowRgn(hwnd, region, true);
            }
        }

        private void SyncBackdropToContent()
        {
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            // 背景窗必须永远压在内容窗后面；之前传 IntPtr.Zero 会把 backdrop 提到 Z 序顶部，
            // 拖动时就只剩一块亚克力空板把内容盖住。
            _ = Win32.SetWindowPos(_backdropHwnd, _hwnd, rect.Left, rect.Top, width, height, Win32.SWP_NOACTIVATE);
        }

        private void ApplyBackdropPalette()
        {
            if (_backdropMode == BackdropMaterialMode.SolidDark)
            {
                _bgPaint.Color = new SKColor(32, 32, 32);
                _titleBarPaint.Color = new SKColor(40, 40, 40);
                _cardBg.Color = new SKColor(255, 255, 255, 8);
                _cardBorder.Color = new SKColor(255, 255, 255, 15);
                _menuBg.Color = new SKColor(40, 40, 40);
                _menuBorder.Color = new SKColor(80, 80, 80);
                _globalBorderPaint.Color = new SKColor(60, 60, 60);
                return;
            }

            bool mica = _backdropMode == BackdropMaterialMode.Mica;
            _bgPaint.Color = mica ? new SKColor(22, 22, 22, 164) : new SKColor(18, 18, 18, 112);
            // 标题栏不再单独盖一层深色底，否则顶栏会像“第二块面板”把亚克力吃掉。
            _titleBarPaint.Color = SKColors.Transparent;
            _cardBg.Color = new SKColor(255, 255, 255, mica ? (byte)18 : (byte)22);
            _cardBorder.Color = new SKColor(255, 255, 255, mica ? (byte)30 : (byte)38);
            _menuBg.Color = mica ? new SKColor(26, 26, 26, 210) : new SKColor(22, 22, 22, 172);
            _menuBorder.Color = new SKColor(255, 255, 255, mica ? (byte)28 : (byte)34);
            _globalBorderPaint.Color = new SKColor(255, 255, 255, mica ? (byte)34 : (byte)40);
        }

        private void TryEnableBackdropMaterial()
        {
            TrySetDarkMode();
            TryExtendFrameIntoClientArea();
            TrySetRoundedCornerPreference();

            // 用户这次要先看“有没有真实背景模糊”。
            // Mica 在很多 Win11 机器上更像带噪点的深色底，不像明显模糊；
            // 所以设置窗口实验优先尝试 Acrylic，失败再回退到 Win11 的 Mica。
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && TryEnableAcrylicBackdrop())
            {
                _backdropMode = BackdropMaterialMode.Acrylic;
                return;
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && TryEnableSystemBackdropMica())
            {
                _backdropMode = BackdropMaterialMode.Mica;
                return;
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && TryEnableLegacyMica())
            {
                _backdropMode = BackdropMaterialMode.Mica;
                return;
            }

            _backdropMode = BackdropMaterialMode.SolidDark;
        }

        private void TrySetDarkMode()
        {
            int enabled = 1;
            int size = Marshal.SizeOf<int>();
            if (_backdropHwnd == IntPtr.Zero) return;
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, size);
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, size);
        }

        private void TrySetRoundedCornerPreference()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            int rounded = Win32.DWMWCP_ROUND;
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, Marshal.SizeOf<int>());
        }

        private void TryExtendFrameIntoClientArea()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            var margins = new Win32.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            _ = Win32.DwmExtendFrameIntoClientArea(_backdropHwnd, ref margins);
        }

        private bool TryEnableSystemBackdropMica()
        {
            if (_backdropHwnd == IntPtr.Zero) return false;
            int backdrop = Win32.DWMSBT_MAINWINDOW;
            return Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, Marshal.SizeOf<int>()) == 0;
        }

        private bool TryEnableLegacyMica()
        {
            if (_backdropHwnd == IntPtr.Zero) return false;
            int enabled = 1;
            return Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_MICA_EFFECT, ref enabled, Marshal.SizeOf<int>()) == 0;
        }

        private bool TryEnableAcrylicBackdrop()
        {
            var accent = new Win32.ACCENT_POLICY
            {
                AccentState = Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                // 这个 tint 故意比上一版更浅：上一版 alpha 太高，视觉更像“深色底板”而不是背景模糊。
                GradientColor = ColorToAbgr(0x8C, 0x14, 0x14, 0x14),
                AnimationId = 0
            };

            IntPtr accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.ACCENT_POLICY>());
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new Win32.WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = Win32.WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = Marshal.SizeOf<Win32.ACCENT_POLICY>()
                };
                if (_backdropHwnd == IntPtr.Zero) return false;
                return Win32.SetWindowCompositionAttribute(_backdropHwnd, ref data) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        private static uint ColorToAbgr(byte a, byte r, byte g, byte b)
        {
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        }

        private void UpdateValueString(int index)
        {
            _valStrCache[index] = index == 6 ? $"{_customValues[index]:F2} x" : $"{(int)_customValues[index]} px";
        }

        // 应用“消息通知内容”模式（0=缩略默认，1=紧凑，2=完整）
        // 完整模式：强制消息通知尺寸不小于容纳应用名的最小值，并把当前用户尺寸快照保存，便于切回时恢复；
        // 缩略/紧凑模式：尺寸均恢复为用户设定的值（紧凑的弹窗尺寸与缩略一致）。
        private void ApplyToastContentMode(int modeIndex)
        {
            if (modeIndex == 2) // 完整
            {
                if (_savedToastW < 0f) { _savedToastW = Renderer.TOAST_WIDTH; _savedToastH = Renderer.TOAST_HEIGHT; }

                if (Renderer.TOAST_WIDTH < Renderer.FULL_TOAST_MIN_WIDTH)
                {
                    Renderer.TOAST_WIDTH = Renderer.FULL_TOAST_MIN_WIDTH;
                    _customValues[4] = Renderer.TOAST_WIDTH;
                    Program.SaveSetting("Custom_ToastW", Renderer.TOAST_WIDTH);
                    UpdateValueString(4);
                }
                if (Renderer.TOAST_HEIGHT < Renderer.FULL_TOAST_MIN_HEIGHT)
                {
                    Renderer.TOAST_HEIGHT = Renderer.FULL_TOAST_MIN_HEIGHT;
                    _customValues[5] = Renderer.TOAST_HEIGHT;
                    Program.SaveSetting("Custom_ToastH", Renderer.TOAST_HEIGHT);
                    UpdateValueString(5);
                }
                Renderer.IsToastFullMode = true;
                Renderer.IsToastCompactMode = false;
                Program.SaveSetting("ToastContentMode", 2);
            }
            else // 0=缩略 或 1=紧凑：尺寸均恢复为用户设定的值
            {
                Renderer.IsToastFullMode = false;
                Renderer.IsToastCompactMode = modeIndex == 1;
                Program.SaveSetting("ToastContentMode", modeIndex);

                float w = _savedToastW >= 0f ? _savedToastW : _defaultCustomValues[4];
                float h = _savedToastH >= 0f ? _savedToastH : _defaultCustomValues[5];
                _savedToastW = -1f; _savedToastH = -1f;

                Renderer.TOAST_WIDTH = w;
                Renderer.TOAST_HEIGHT = h;
                _customValues[4] = w;
                _customValues[5] = h;
                Program.SaveSetting("Custom_ToastW", w);
                Program.SaveSetting("Custom_ToastH", h);
                UpdateValueString(4);
                UpdateValueString(5);
            }

            _selectedToastModeIndex = modeIndex;
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instance != null)
            {
                bool initializingContent = _instance._hwnd == IntPtr.Zero;
                if (initializingContent || hwnd == _instance._hwnd || hwnd == _instance._backdropHwnd)
                    return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            bool isBackdropWindow = hwnd == _backdropHwnd && _backdropHwnd != IntPtr.Zero;
            if (isBackdropWindow)
            {
                switch (msg)
                {
                    case Win32.WM_PAINT:
                        IntPtr backdropDc = Win32.BeginPaint(hwnd, out var backdropPs);
                        if (backdropDc != IntPtr.Zero)
                            Win32.EndPaint(hwnd, ref backdropPs);
                        return IntPtr.Zero;
                    case Win32.WM_NCHITTEST:
                        return (IntPtr)Win32.HTTRANSPARENT;
                }
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            }

            switch (msg)
            {
                case Win32.WM_MOVE:
                    SyncBackdropToContent();
                    break;

                case Win32.WM_MOUSEMOVE:
                    int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                    int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                    bool newMinHovered = x >= WIDTH - 92 && x < WIDTH - 46 && y <= TITLE_BAR_HEIGHT;
                    bool newCloseHovered = x >= WIDTH - 46 && x <= WIDTH && y <= TITLE_BAR_HEIGHT;

                    // Tab Hover 判定
                    int newHoveredTab = -1;
                    if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 10 && y <= TITLE_BAR_HEIGHT + 46) newHoveredTab = 5;      // 1. 个性化中心
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 60 && y <= TITLE_BAR_HEIGHT + 96) newHoveredTab = 0; // 2. 通用设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 100 && y <= TITLE_BAR_HEIGHT + 136) newHoveredTab = 1; // 3. 显示设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 140 && y <= TITLE_BAR_HEIGHT + 176) newHoveredTab = 2; // 4. 媒体设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 180 && y <= TITLE_BAR_HEIGHT + 216) newHoveredTab = 3; // 5. 交互设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 230 && y <= TITLE_BAR_HEIGHT + 266) newHoveredTab = 6; // 6. 插件中心
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 280 && y <= TITLE_BAR_HEIGHT + 316) newHoveredTab = 4; // 7. 关于软件

                    int newHoveredTheme = -1;
                    int newHoveredOpacityIndex = -1;
                    int newHoverMinus = -1, newHoverPlus = -1, newHoverReset = -1;
                    if (_selectedTab == 5)
                    {
                        // 避免和下面的 rightX 冲突，改名为 themeRightX
                        float themeRightX = WIDTH - 36;
                        float themeY = GetBtnY(-1);

                        // 主题按钮的三个胶囊热区
                        if (x >= themeRightX - 140 && x <= themeRightX - 100 && y >= themeY && y <= themeY + 24) newHoveredTheme = 0;
                        if (x >= themeRightX - 90 && x <= themeRightX - 50 && y >= themeY && y <= themeY + 24) newHoveredTheme = 1;
                        if (x >= themeRightX - 40 && x <= themeRightX && y >= themeY && y <= themeY + 24) newHoveredTheme = 2;

                        // 透明度滑块热区判定与拖拽滑动逻辑
                        float sliderY = TITLE_BAR_HEIGHT + 95;
                        float sliderX = 216;
                        float sliderW = WIDTH - 40 - 216;

                        if (!Renderer.PassthroughModeEnabled)
                        {
                            // 放宽 Y 轴的判定区域，提升拖拽时的手感，防止手抖断触
                            if (x >= sliderX - 20 && x <= sliderX + sliderW + 20 && y >= sliderY - 20 && y <= sliderY + 20)
                            {
                                newHoveredOpacityIndex = (int)Math.Round((x - sliderX) / (sliderW / 4));
                                if (newHoveredOpacityIndex < 0) newHoveredOpacityIndex = 0;
                                if (newHoveredOpacityIndex > 4) newHoveredOpacityIndex = 4;

                                // 核心滑动逻辑：判断此时鼠标左键是否处于“按住”状态 (MK_LBUTTON = 0x0001)
                                if ((wParam.ToInt32() & 0x0001) != 0)
                                {
                                    if (Renderer.BgOpacityLevel != newHoveredOpacityIndex)
                                    {
                                        Renderer.BgOpacityLevel = newHoveredOpacityIndex;
                                        Renderer.ApplyThemeColors();
                                        Program.SaveSetting("BgOpacityLevel", newHoveredOpacityIndex);
                                        Render(); // 数据一旦跨越档位，立刻触发重绘，实现跟手滑动
                                    }
                                }
                            }
                        } 

                        for (int i = 0; i < 8; i++)
                        {
                            float btnY = GetBtnY(i);
                            float rightX = WIDTH - 36; // 保持原有变量不动
                            if (x >= rightX - 175 && x <= rightX - 145 && y >= btnY && y <= btnY + 24) newHoverMinus = i;
                            if (x >= rightX - 80 && x <= rightX - 50 && y >= btnY && y <= btnY + 24) newHoverPlus = i;
                            if (x >= rightX - 40 && x <= rightX && y >= btnY && y <= btnY + 24) newHoverReset = i;
                        }
                    }

                    int newHoveredDisplayOptionIndex = -1;
                    int newHoveredStyleIndex = -1;
                    bool newToggleHovered = false;
                    bool newToastToggleHovered = false;
                    bool newTopmostToggleHovered = false;
                    bool newMediaToggleHovered = false;
                    bool newAutoHideToggleHovered = false;
                    bool newPauseHideToggleHovered = false; // 「暂停播放后自动隐藏」
                    bool newFsHideToggleHovered = false;    // 「全屏自动隐藏」
                    bool newDropdownHovered = false;
                    int newHoveredDropdownIndex = -1;
                    bool newMatchModeDropdownHovered = false;
                    int newHoveredMatchModeIndex = -1;
                    bool newAppDropdownHovered = false;
                    int newHoveredAppIndex = -1;
                    bool newMediaExpToggleHovered = false;
                    bool newPassToggleHovered = false;
                    bool newClipboardToggleHovered = false;
                    bool newMonitorDropdownHovered = false;
                    int newHoveredMonitorDropdownIndex = -1;
                    bool newToastModeDropdownHovered = false;
                    int newHoveredToastModeIndex = -1;
                    bool newCompositeToggleHover = false;
                    bool newCompDateTimeHover = false;
                    bool newCompHardwareHover = false;
                    bool newCompMediaHover = false;
                    int newHoveredPluginAction = -1;
                    int newHoveredPluginToggle = -1;
                    int newHoveredPluginReload = -1;
                    int newHoveredPluginRemove = -1;
                    int newHoveredPluginMoveLeft = -1;
                    int newHoveredPluginMoveRight = -1;
                    bool newFontPickHovered = false;
                    bool newFontResetHovered = false;

                    if (_selectedTab == 0) // 通用设置
                    {
                        // ⚠️ 本段的 y 值必须与下面 tab 0 的渲染保持同步
                        //    （通知卡片是两行高：行1 开关 +176..+196、行2 下拉 +233..+265；
                        //      剪贴板卡片 +310；字体按钮见 FONT_BTN_Y）
                        // 开机自启
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newToggleHovered = true;
                        // 窗口置顶开关
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                            newTopmostToggleHovered = true;

                        // 🔔 通知卡片第 1 行：系统消息通知开关
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 176 && y <= TITLE_BAR_HEIGHT + 196)
                            newToastToggleHovered = true;
                        // 🔔 通知卡片第 2 行：消息通知内容下拉（复用现有下拉控件样式）
                        if (!_toastModeDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 233 && y <= TITLE_BAR_HEIGHT + 265)
                            newToastModeDropdownHovered = true;
                        if (_toastModeDropdownOpen)
                        {
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 267 && y < TITLE_BAR_HEIGHT + 267 + _toastModeOptions.Length * 26)
                                newHoveredToastModeIndex = (y - (TITLE_BAR_HEIGHT + 267)) / 26;
                        }

                        // 📋 剪贴板链接检测（2026-09-20 从「交互设置」搬到这里）
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 310 && y <= TITLE_BAR_HEIGHT + 330)
                            newClipboardToggleHovered = true;

                        // 切换灵动岛字体：[选择字体…] 与 [重置] 两个按钮
                        if (y >= TITLE_BAR_HEIGHT + FONT_BTN_Y && y <= TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H)
                        {
                            if (x >= FONT_PICK_X && x <= FONT_PICK_X + FONT_PICK_W) newFontPickHovered = true;
                            if (x >= FONT_RESET_X && x <= FONT_RESET_X + FONT_RESET_W) newFontResetHovered = true;
                        }
                    }
                    else if (_selectedTab == 1) // 显示设置
                    {
                        // 刘海形态选择器的点击热区
                        float styleY = TITLE_BAR_HEIGHT + 50;
                        if (x >= 220 && x <= 370 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 0;
                        if (x >= 390 && x <= 540 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 1;

                        // 目标显示器卡片
                        float mdY = TITLE_BAR_HEIGHT + 186;
                        if (!_monitorDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= mdY && y <= mdY + 32)
                            newMonitorDropdownHovered = true;

                        if (_monitorDropdownOpen)
                        {
                            float listY = TITLE_BAR_HEIGHT + 220;
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= listY && y < listY + _monitorOptions.Length * 26)
                                newHoveredMonitorDropdownIndex = (y - (int)listY) / 26;
                        }

                        // 待机显示内容卡片 (1/3 布局)
                        float displayOptY = TITLE_BAR_HEIGHT + 306;
                        if (x >= 220 && x <= 330 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 2; // 硬件占用
                        if (x >= 340 && x <= 450 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 0; // 时间日期
                        if (x >= 460 && x <= 570 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 1; // 空白

                        // 组合模式hover判定
                        float compCardY = TITLE_BAR_HEIGHT + 372;
                        newCompositeToggleHover = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= compCardY + 72 && y <= compCardY + 92;
                        if (Renderer.CompositeModeEnabled)
                        { // 状态拦截，防止关闭时产生幽灵悬停
                            newCompDateTimeHover = x >= 216 && x <= 350 && y >= compCardY + 105 && y <= compCardY + 121;
                            newCompHardwareHover = x >= 216 && x <= 350 && y >= compCardY + 140 && y <= compCardY + 156;
                            newCompMediaHover = x >= 216 && x <= 380 && y >= compCardY + 175 && y <= compCardY + 191;
                        }

                    }
                    if (_selectedTab == 1 && !Renderer.CompositeModeEnabled)
                    {
                        float displayOptY = TITLE_BAR_HEIGHT + 306;
                        if (x >= 220 && x <= 330 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 2;
                        if (x >= 340 && x <= 450 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 0;
                        if (x >= 460 && x <= 570 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 1;
                    }
                    else if (_selectedTab == 2) // 媒体设置
                    {
                        // 任意下拉展开时，底层控件一律不吃悬停，避免浮窗底下的按钮被误触
                        bool anyPopupOpen = _dropdownOpen || _matchModeDropdownOpen || _appDropdownOpen;

                        // 媒体控制
                        if (!anyPopupOpen && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newMediaToggleHovered = true;

                        // 下拉菜单（合并卡片第 1 行「目标媒体平台」；框体 +96..+128，命中内缩 2px）
                        // ⚠️ 必须与 Render() 里 tab 2 的 `dY = TITLE_BAR_HEIGHT + 96` / `dH = 32` 保持同步
                        if (!anyPopupOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 98 && y <= TITLE_BAR_HEIGHT + 128)
                            newDropdownHovered = true;

                        if (_dropdownOpen)
                        {
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 130 && y < TITLE_BAR_HEIGHT + 130 + _platforms.Length * 26)
                                newHoveredDropdownIndex = (y - (TITLE_BAR_HEIGHT + 130)) / 26;
                        }

                        // 匹配方式（合并卡片第 2 行）：仅通用媒体可选；右框只在手动模式下可选
                        // 坐标全部来自 MATCH_ROW_Y / MATCH_MENU_Y，与 Render() 同源
                        bool matchRowEnabled = MediaController.TargetPlatform == "other";
                        newMatchModeDropdownHovered = !anyPopupOpen && matchRowEnabled
                            && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;
                        newAppDropdownHovered = !anyPopupOpen && matchRowEnabled && MediaController.IsManualSessionMatch
                            && x >= MATCH_APP_X && x <= MATCH_APP_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;

                        if (_matchModeDropdownOpen
                            && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W
                            && y >= MATCH_MENU_Y && y < MATCH_MENU_Y + _matchModeOptions.Length * 26)
                            newHoveredMatchModeIndex = (int)((y - MATCH_MENU_Y) / 26);

                        if (_appDropdownOpen)
                        {
                            int appRows = Math.Clamp(_appOptions.Length, 1, 8);
                            float appMenuX = MATCH_MENU_RIGHT - APP_MENU_W;
                            if (x >= appMenuX && x <= MATCH_MENU_RIGHT && y >= MATCH_MENU_Y && y < MATCH_MENU_Y + appRows * 26)
                                newHoveredAppIndex = (int)((y - MATCH_MENU_Y) / 26);
                        }

                        // 歌词设置卡片（坐标常量与 Render() 同源）
                        float lyricY = LYRIC_CARD_Y;
                        bool newLyricToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 37 && y <= lyricY + 57);
                        bool newTransToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 77 && y <= lyricY + 97);
                        bool newKaraokeToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 117 && y <= lyricY + 137);

                        // 延迟补偿按钮整行排在最后（歌词卡片里第四行）
                        float btnY = lyricY + 147;
                        float cardRightX = WIDTH - 36;
                        bool newLyricMinusHovered = !anyPopupOpen && (x >= cardRightX - 175 && x <= cardRightX - 145 && y >= btnY && y <= btnY + 24);
                        bool newLyricPlusHovered = !anyPopupOpen && (x >= cardRightX - 80 && x <= cardRightX - 50 && y >= btnY && y <= btnY + 24);
                        bool newLyricResetHovered = !anyPopupOpen && (x >= cardRightX - 40 && x <= cardRightX && y >= btnY && y <= btnY + 24);

                        if (newLyricToggleHovered != _lyricToggleHovered || newTransToggleHovered != _transToggleHovered || newKaraokeToggleHovered != _karaokeToggleHovered || newLyricMinusHovered != _lyricMinusHovered || newLyricPlusHovered != _lyricPlusHovered || newLyricResetHovered != _lyricResetHovered)
                        {
                            _lyricToggleHovered = newLyricToggleHovered;
                            _transToggleHovered = newTransToggleHovered;
                            _karaokeToggleHovered = newKaraokeToggleHovered;
                            _lyricMinusHovered = newLyricMinusHovered;
                            _lyricPlusHovered = newLyricPlusHovered;
                            _lyricResetHovered = newLyricResetHovered;
                            Render();
                        }
                    }
                    else if (_selectedTab == 3) // 交互设置
                    {
                        // ⚠️ 本段的 y 值必须与下面 tab 3 的渲染保持同步
                        //    （自动隐藏卡片是三行高：行1 +32、行2 +94、行3 +156；其余两张卡 +228 / +300）
                        // 自动隐藏（穿透模式下禁止）
                        if (!Renderer.PassthroughModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newAutoHideToggleHovered = true;
                        // 🎵 暂停播放后自动隐藏（自动隐藏卡片的第二行）
                        // 可用性 = 自动隐藏已开启 且 非穿透模式；不可用时不给 hover（避免「灰着还能点」）
                        if (!Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled
                            && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 94 && y <= TITLE_BAR_HEIGHT + 114)
                            newPauseHideToggleHovered = true;
                        // 🖥 全屏自动隐藏（自动隐藏卡片的第三行）——可用性同上
                        if (!Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled
                            && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 156 && y <= TITLE_BAR_HEIGHT + 176)
                            newFsHideToggleHovered = true;
                        // 媒体交互模式
                        if (!Renderer.CompositeModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 228 && y <= TITLE_BAR_HEIGHT + 248)
                            newMediaExpToggleHovered = true;
                        // 使用局部变量，防止状态死锁
                        newPassToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 300 && y <= TITLE_BAR_HEIGHT + 320;
                    }
                    else if (_selectedTab == 6) // 插件中心
                    {
                        float topY = TITLE_BAR_HEIGHT + 12;
                        // 三个操作按钮
                        if (y >= topY + 60 && y <= topY + 84)
                        {
                            if (x >= 216 && x <= 312) newHoveredPluginAction = 0;       // 导入 DLL
                            else if (x >= 320 && x <= 416) newHoveredPluginAction = 1;  // 打开目录
                            else if (x >= 424 && x <= 520) newHoveredPluginAction = 2;  // 插件市场
                        }

                        // 列表行内按钮（全部在下行：← → 排序 | 重载 | 移除 | 开关）
                        // 上行（名称）无交互目标，仅下行按钮可点击
                        float listY = topY + 110;
                        int rows = Math.Min(_pluginView.Count, 7);
                        // ⚠️ 这里的行起点必须与 Render() 里的 `listY + 64` 严格一致
                        //    （2026-09-20 加「顺序：…」那一行时把渲染下移了 20px，热区漏改 →
                        //      整行交互热区整体上移 20px，按钮全部点不到）
                        if (x >= 216 && x <= WIDTH - 36 && y >= listY + 64)
                        {
                            int idx = (int)((y - (listY + 64)) / 56);
                            if (idx >= 0 && idx < rows)
                            {
                                float rowY = listY + 64 + idx * 56;
                                // 下行按钮区（rowY+22 .. rowY+48）
                                if (y >= rowY + 22 && y <= rowY + 48)
                                {
                                    // 从左到右：排序 ← → | 重载 | 移除 | 开关
                                    if (x >= PLUGIN_SORT_LEFT_X && x <= PLUGIN_SORT_LEFT_X + SORT_TRI_W) newHoveredPluginMoveLeft = idx;
                                    else if (x >= PLUGIN_SORT_RIGHT_X && x <= PLUGIN_SORT_RIGHT_X + SORT_TRI_W) newHoveredPluginMoveRight = idx;
                                    else if (x >= PLUGIN_BTN_RELOAD_X && x <= PLUGIN_BTN_RELOAD_X + 50) newHoveredPluginReload = idx;
                                    else if (x >= PLUGIN_BTN_REMOVE_X && x <= PLUGIN_BTN_REMOVE_X + 50) newHoveredPluginRemove = idx;
                                    else if (x >= PLUGIN_BTN_TOGGLE_X && x <= PLUGIN_BTN_TOGGLE_X + 42) newHoveredPluginToggle = idx;
                                }
                            }
                        }
                    }

                    int newHoveredLinkIndex = -1;
                    if (_selectedTab == 4) // 关于页
                    {
                        int yStart = TITLE_BAR_HEIGHT + 160;
                        int yEnd = TITLE_BAR_HEIGHT + 190;

                        if (y >= yStart && y <= yEnd)
                        {
                            if (x >= 305 && x <= 370) newHoveredLinkIndex = 0;      // 检测更新
                            else if (x >= 375 && x <= 440) newHoveredLinkIndex = 1; // 仓库地址
                            else if (x >= 445 && x <= 500) newHoveredLinkIndex = 2; // 开发者 (Ryen)
                        }
                    }

                    bool newIsHoveringDisabledArea = false;
                    // 「禁止」指针区域 —— 判据必须与 Render() 里对应卡片的 disabled **完全同源**，
                    // 否则就会出现「明明能点、却显示禁止指针」。
                    // ⚠️ 这些 y 区间是手写的，卡片一挪动就必须同步改（踩过一次：
                    //    自动隐藏卡片从两行加高到三行后，「媒体交互方式」卡片从 yOffset 84 挪到了 208，
                    //    这里没跟着改，禁止区域就压在了「暂停播放后自动隐藏」那一行上 ——
                    //    导致不管该开关是否被禁用，hover 上去都是禁止指针）。
                    if (x >= 200 && x <= WIDTH - 20)
                    {
                        if (_selectedTab == 1 && Renderer.CompositeModeEnabled
                            && y >= TITLE_BAR_HEIGHT + 248 && y <= TITLE_BAR_HEIGHT + 360)  // 待机显示内容卡片
                        {
                            newIsHoveringDisabledArea = true;
                        }
                        else if (_selectedTab == 3)
                        {
                            // 与 Render() tab 3 的 disabled 判据同源（改那边记得改这边）
                            bool autoHideDisabled = Renderer.PassthroughModeEnabled;
                            bool subToggleDisabled = autoHideDisabled || !NotchWindow.IsAutoHideEnabled;

                            if (Renderer.CompositeModeEnabled
                                && y >= TITLE_BAR_HEIGHT + 208 && y <= TITLE_BAR_HEIGHT + 270)  // 媒体交互方式卡片
                                newIsHoveringDisabledArea = true;
                            else if (autoHideDisabled
                                && y >= TITLE_BAR_HEIGHT + 12 && y <= TITLE_BAR_HEIGHT + 198)   // 自动隐藏卡片整卡（穿透模式下三行全禁用）
                                newIsHoveringDisabledArea = true;
                            else if (subToggleDisabled
                                && y >= TITLE_BAR_HEIGHT + 74 && y <= TITLE_BAR_HEIGHT + 198)   // 两个附属开关行（需先开启「自动隐藏」）
                                newIsHoveringDisabledArea = true;
                        }
                    }

                    if (newIsHoveringDisabledArea != _isHoveringDisabledArea) _isHoveringDisabledArea = newIsHoveringDisabledArea;

                    if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                        newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered ||
                        newToastToggleHovered != _toastToggleHovered || newTopmostToggleHovered != _topmostToggleHovered ||
                        newMediaToggleHovered != _mediaToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
                        newPauseHideToggleHovered != _pauseHideToggleHovered ||
                        newFsHideToggleHovered != _fsHideToggleHovered ||
                        newDropdownHovered != _dropdownHovered ||
                        newMatchModeDropdownHovered != _matchModeDropdownHovered ||
                        newHoveredMatchModeIndex != _hoveredMatchModeIndex ||
                        newAppDropdownHovered != _appDropdownHovered ||
                        newHoveredAppIndex != _hoveredAppIndex ||
                        newHoveredDropdownIndex != _hoveredDropdownIndex || newHoveredLinkIndex != _hoveredLinkIndex ||
                        newHoveredDisplayOptionIndex != _hoveredDisplayOptionIndex ||
                        newHoveredStyleIndex != _hoveredStyleIndex ||
                        newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                        newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
                        newHoveredTheme != _hoveredThemeIndex || newHoveredOpacityIndex != _hoveredOpacityIndex ||
                        newMonitorDropdownHovered != _monitorDropdownHovered ||
                        newHoveredMonitorDropdownIndex != _hoveredMonitorDropdownIndex ||
                        newToastModeDropdownHovered != _toastModeDropdownHovered ||
                        newHoveredToastModeIndex != _hoveredToastModeIndex ||
                        newCompositeToggleHover != _compositeToggleHovered ||
                        newCompDateTimeHover != _compDateTimeHovered ||
                        newCompHardwareHover != _compHardwareHovered ||
                        newCompMediaHover != _compMediaHovered || newPassToggleHovered != _passToggleHovered ||
                        newClipboardToggleHovered != _clipboardToggleHovered ||
                        newHoveredPluginAction != _hoveredPluginAction ||
                        newHoveredPluginToggle != _hoveredPluginToggle ||
                        newHoveredPluginReload != _hoveredPluginReload ||
                        newHoveredPluginRemove != _hoveredPluginRemove ||
                        newHoveredPluginMoveLeft != _hoveredPluginMoveLeft ||
                        newHoveredPluginMoveRight != _hoveredPluginMoveRight ||
                        newFontPickHovered != _fontPickHovered || newFontResetHovered != _fontResetHovered
                        )
                    {
                        _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                        _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                        _toastToggleHovered = newToastToggleHovered;
                        _mediaToggleHovered = newMediaToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
                        _pauseHideToggleHovered = newPauseHideToggleHovered;
                        _fsHideToggleHovered = newFsHideToggleHovered;
                        _dropdownHovered = newDropdownHovered;
                        _hoveredDropdownIndex = newHoveredDropdownIndex;
                        _matchModeDropdownHovered = newMatchModeDropdownHovered;
                        _hoveredMatchModeIndex = newHoveredMatchModeIndex;
                        _appDropdownHovered = newAppDropdownHovered;
                        _hoveredAppIndex = newHoveredAppIndex;
                        _hoveredLinkIndex = newHoveredLinkIndex;
                        _hoveredDisplayOptionIndex = newHoveredDisplayOptionIndex;
                        _hoveredStyleIndex = newHoveredStyleIndex;
                        _hoveredMinusIndex = newHoverMinus;
                        _hoveredPlusIndex = newHoverPlus;
                        _hoveredResetIndex = newHoverReset;
                        _hoveredThemeIndex = newHoveredTheme;
                        _mediaExpToggleHovered = newMediaExpToggleHovered;
                        _hoveredOpacityIndex = newHoveredOpacityIndex;
                        _monitorDropdownHovered = newMonitorDropdownHovered;
                        _hoveredMonitorDropdownIndex = newHoveredMonitorDropdownIndex;
                        _toastModeDropdownHovered = newToastModeDropdownHovered;
                        _hoveredToastModeIndex = newHoveredToastModeIndex;
                        _compositeToggleHovered = newCompositeToggleHover;
                        _compDateTimeHovered = newCompDateTimeHover;
                        _compHardwareHovered = newCompHardwareHover;
                        _compMediaHovered = newCompMediaHover;
                        _topmostToggleHovered = newTopmostToggleHovered;
                        _passToggleHovered = newPassToggleHovered;
                        _clipboardToggleHovered = newClipboardToggleHovered;
                        _hoveredPluginAction = newHoveredPluginAction;
                        _hoveredPluginToggle = newHoveredPluginToggle;
                        _hoveredPluginReload = newHoveredPluginReload;
                        _hoveredPluginRemove = newHoveredPluginRemove;
                        _hoveredPluginMoveLeft = newHoveredPluginMoveLeft;
                        _hoveredPluginMoveRight = newHoveredPluginMoveRight;
                        _fontPickHovered = newFontPickHovered;
                        _fontResetHovered = newFontResetHovered;
                        Render();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
                    int clickY = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                    if (_closeHovered)
                    {
                        if (_backdropHwnd != IntPtr.Zero)
                        {
                            Win32.DestroyWindow(_backdropHwnd);
                            _backdropHwnd = IntPtr.Zero;
                        }
                        Win32.DestroyWindow(hwnd);
                    }
                    else if (_minHovered)
                    {
                        if (_backdropHwnd != IntPtr.Zero)
                            Win32.ShowWindow(_backdropHwnd, Win32.SW_MINIMIZE);
                        Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
                    }
                    else if (clickY <= TITLE_BAR_HEIGHT)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
                    }
                    else if (_dropdownOpen && _hoveredDropdownIndex == -1)
                    {
                        CloseAllDropdowns(); Render(); // 点击菜单外部收起浮窗
                    }
                    else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex == -1) { CloseAllDropdowns(); Render(); }
                    else if (_toastModeDropdownOpen && _hoveredToastModeIndex == -1) { CloseAllDropdowns(); Render(); }
                    else if (_matchModeDropdownOpen && _hoveredMatchModeIndex == -1) { CloseAllDropdowns(); Render(); }
                    else if (_appDropdownOpen && _hoveredAppIndex == -1) { CloseAllDropdowns(); Render(); }
                    else if (_selectedTab == 1 && _hoveredStyleIndex != -1)
                    {
                        Renderer.NotchStyle = _hoveredStyleIndex;
                        Program.SaveSetting("NotchStyle", _hoveredStyleIndex);
                        Render();
                    }
                    else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 5 && _selectedTab != 5) { _selectedTab = 5; CloseAllDropdowns(); Render(); }
                    else if (_hoveredTab == 6 && _selectedTab != 6) { _selectedTab = 6; _dropdownOpen = false; RefreshPluginView(); Render(); }
                    else if (_monitorDropdownHovered) { _monitorDropdownOpen = true; Render(); }
                    else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex != -1)
                    {
                        Renderer.TargetMonitorIndex = _hoveredMonitorDropdownIndex;
                        Program.SaveSetting("TargetMonitorIndex", Renderer.TargetMonitorIndex);
                        _monitorDropdownOpen = false;
                        Render();
                    }
                    else if (_toastModeDropdownHovered) { _toastModeDropdownOpen = true; Render(); }
                    else if (_toastModeDropdownOpen && _hoveredToastModeIndex != -1)
                    {
                        ApplyToastContentMode(_hoveredToastModeIndex);
                        _toastModeDropdownOpen = false;
                        Render();
                    }
                    else if (_selectedTab == 2 && _matchModeDropdownHovered)
                    {
                        CloseAllDropdowns();
                        _matchModeDropdownOpen = true;
                        Render();
                    }
                    else if (_selectedTab == 2 && _matchModeDropdownOpen && _hoveredMatchModeIndex != -1)
                    {
                        MediaController.IsManualSessionMatch = _hoveredMatchModeIndex == 1;
                        Program.SaveSetting("ManualSessionMatch", MediaController.IsManualSessionMatch ? 1 : 0);
                        _matchModeDropdownOpen = false;
                        _ = MediaController.Instance?.ForceRefresh();
                        Render();
                    }
                    else if (_selectedTab == 2 && _appDropdownHovered)
                    {
                        // 展开时现取一次活动会话列表，保证「所有 SMTC 活动」是最新的
                        _appOptions = MediaController.Instance?.GetAvailableAppIds() ?? Array.Empty<string>();
                        CloseAllDropdowns();
                        _appDropdownOpen = true;
                        Render();
                    }
                    else if (_selectedTab == 2 && _appDropdownOpen && _hoveredAppIndex != -1 && _hoveredAppIndex < _appOptions.Length)
                    {
                        MediaController.ManualSessionAppId = _appOptions[_hoveredAppIndex];
                        Program.SaveSetting("ManualSessionAppId", MediaController.ManualSessionAppId);
                        _appDropdownOpen = false;
                        _ = MediaController.Instance?.ForceRefresh();
                        Render();
                    }
                    else if (_selectedTab == 5 && (_hoveredMinusIndex != -1 || _hoveredPlusIndex != -1 || _hoveredResetIndex != -1))
                    {
                        int updateIdx;
                        if (_hoveredResetIndex != -1)
                        {
                            updateIdx = _hoveredResetIndex;
                            float[] defaultVals = { 125f, 29f, 250f, 35f, 260f, 55f, 1.0f, 12f };
                            _customValues[updateIdx] = defaultVals[updateIdx];

                            // 重置时如果处于硬件监控，拦截至最小限制
                            if (Renderer.StandbyDisplayMode == 2)
                            {
                                if (updateIdx == 0 && _customValues[0] < 170f) _customValues[0] = 170f;
                                if (updateIdx == 1 && _customValues[1] < 34f) _customValues[1] = 34f;
                            }

                            // 重置待机宽度时，同步更新快照，防止切回时恢复到旧值
                            if (updateIdx == 0)
                                _savedStandbyWidth = -1f; // 清除快照，切回时用默认125

                            // 完整模式重置消息通知尺寸时，同样拦截至完整模式最小限制，避免缩得放不下应用名
                            if (Renderer.IsToastFullMode)
                            {
                                if (updateIdx == 4 && _customValues[4] < Renderer.FULL_TOAST_MIN_WIDTH) _customValues[4] = Renderer.FULL_TOAST_MIN_WIDTH;
                                if (updateIdx == 5 && _customValues[5] < Renderer.FULL_TOAST_MIN_HEIGHT) _customValues[5] = Renderer.FULL_TOAST_MIN_HEIGHT;
                            }
                        }
                        else
                        {
                            updateIdx = _hoveredMinusIndex != -1 ? _hoveredMinusIndex : _hoveredPlusIndex;
                            float delta = _hoveredPlusIndex != -1 ? (updateIdx == 6 ? 0.05f : 5f) : (updateIdx == 6 ? -0.05f : -5f);
                            if (updateIdx == 7)
                                _customValues[updateIdx] = Math.Clamp(_customValues[updateIdx] + delta, 0f, 28f);
                            else
                            {
                                float minLimit = updateIdx == 6 ? 0.5f : 20f;
                                // 滑动尺寸时的保护墙
                                if (Renderer.StandbyDisplayMode == 2)
                                {
                                    if (updateIdx == 0) minLimit = 170f;
                                    if (updateIdx == 1) minLimit = 34f;
                                }
                                // 完整模式下，消息通知最小尺寸必须能容纳应用名
                                if (Renderer.IsToastFullMode)
                                {
                                    if (updateIdx == 4) minLimit = Renderer.FULL_TOAST_MIN_WIDTH;
                                    if (updateIdx == 5) minLimit = Renderer.FULL_TOAST_MIN_HEIGHT;
                                }
                                _customValues[updateIdx] = Math.Max(minLimit, _customValues[updateIdx] + delta);
                            }
                        }

                        // 数值变动时才更新字符串缓存，避免渲染循环产生 GC 垃圾
                        UpdateValueString(updateIdx);

                        if (updateIdx == 0) { Renderer.STANDBY_WIDTH = _customValues[0]; Program.SaveSetting("Custom_StandbyW", _customValues[0]); }
                        else if (updateIdx == 1) { Renderer.BASE_HEIGHT = _customValues[1]; Program.SaveSetting("Custom_BaseH", _customValues[1]); }
                        else if (updateIdx == 2) { Renderer.MEDIA_WIDTH = _customValues[2]; Program.SaveSetting("Custom_MediaW", _customValues[2]); }
                        else if (updateIdx == 3) { Renderer.MEDIA_HEIGHT = _customValues[3]; Program.SaveSetting("Custom_MediaH", _customValues[3]); }
                        else if (updateIdx == 4) { Renderer.TOAST_WIDTH = _customValues[4]; Program.SaveSetting("Custom_ToastW", _customValues[4]); }
                        else if (updateIdx == 5) { Renderer.TOAST_HEIGHT = _customValues[5]; Program.SaveSetting("Custom_ToastH", _customValues[5]); }
                        else if (updateIdx == 6) { Renderer.GLOBAL_DPI = _customValues[6]; Program.SaveSetting("Custom_Dpi", _customValues[6]); }
                        else if (updateIdx == 7) { Renderer.NOTCH_BOTTOM_RADIUS = _customValues[7]; Program.SaveSetting("Custom_NotchBottomR", _customValues[7]); }

                        Render();
                    }
                    else if (_selectedTab == 5 && _hoveredThemeIndex != -1)
                    {
                        Renderer.ThemeMode = _hoveredThemeIndex;
                        Renderer.ApplyThemeColors(); // 立即反转画笔颜色
                        Program.SaveSetting("ThemeMode", _hoveredThemeIndex);
                        Render(); // 刷新控制台UI
                    }
                    else if (_selectedTab == 5 && _hoveredOpacityIndex != -1)
                    {
                        Renderer.BgOpacityLevel = _hoveredOpacityIndex;
                        Renderer.ApplyThemeColors(); // 立刻应用透明度
                        Program.SaveSetting("BgOpacityLevel", _hoveredOpacityIndex); // 持久化保存
                        Render(); // 刷新UI
                    }
                    else if (_selectedTab == 4 && _hoveredLinkIndex != -1)
                    {
                        string[] urls = [
                            "https://github.com/GEORGEWWWU/NotchPeninsula/releases", // 0: 检测更新
                            "https://github.com/GEORGEWWWU/NotchPeninsula",          // 1: 仓库地址
                            "https://georgewu.top/"                                  // 2: Ryen主页
                        ];
                        try
                        {
                            // .NET 5+ 环境下，调用浏览器打开网页必须指定 UseShellExecute = true
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = urls[_hoveredLinkIndex],
                                UseShellExecute = true
                            });
                        }
                        catch { /* 防止没装浏览器的极端环境崩溃 */ }
                    }
                    else if (_toggleHovered)
                    {
                        bool newState = !_isAutoStartEnabled;
                        NotchWindow.ToggleAutoStart(newState, false);
                        _isAutoStartEnabled = newState;
                        Render();
                    }
                    else if (_toastToggleHovered)
                    {
                        // 切换状态
                        NotchWindow.IsToastEnabled = !NotchWindow.IsToastEnabled;
                        // 保存设置
                        Program.SaveSetting("ToastEnabled", NotchWindow.IsToastEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_topmostToggleHovered)
                    {
                        NotchWindow.IsTopmostEnabled = !NotchWindow.IsTopmostEnabled;
                        Program.SaveSetting("TopmostEnabled", NotchWindow.IsTopmostEnabled ? 1 : 0);
                        // 直接调用底层 API 热重载层级，无需重启和重建画布
                        Win32.SetWindowPos(NotchWindow.InstanceHandle, NotchWindow.IsTopmostEnabled ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE_NOSIZE);
                        Render();
                    }
                    else if (_fontPickHovered)
                    {
                        PickCustomFont();
                    }
                    else if (_fontResetHovered)
                    {
                        ResetCustomFont();
                    }
                    else if (_mediaToggleHovered)
                    {
                        MediaController.IsMediaControlEnabled = !MediaController.IsMediaControlEnabled;
                        // 保存媒体控制开关 (转换为0/1)
                        Program.SaveSetting("MediaControl", MediaController.IsMediaControlEnabled ? 1 : 0);

                        _ = MediaController.Instance?.ForceRefresh();
                        Render();
                    }
                    else if (_selectedTab == 2 && _lyricToggleHovered)
                    {
                        MediaController.IsLyricsEnabled = !MediaController.IsLyricsEnabled;
                        Program.SaveSetting("LyricsEnabled", MediaController.IsLyricsEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_selectedTab == 2 && _transToggleHovered)
                    {
                        MediaController.IsTranslationEnabled = !MediaController.IsTranslationEnabled;
                        Program.SaveSetting("TranslationEnabled", MediaController.IsTranslationEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_selectedTab == 2 && _karaokeToggleHovered)
                    {
                        MediaController.IsKaraokeEnabled = !MediaController.IsKaraokeEnabled;
                        Program.SaveSetting("KaraokeEnabled", MediaController.IsKaraokeEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_selectedTab == 2 && (_lyricMinusHovered || _lyricPlusHovered || _lyricResetHovered))
                    {
                        if (_lyricResetHovered) MediaController.LyricDelayOffset = 0f;
                        else if (_lyricMinusHovered) MediaController.LyricDelayOffset -= 0.1f;
                        else if (_lyricPlusHovered) MediaController.LyricDelayOffset += 0.1f;

                        // 避免浮点数精度爆炸，固定为 1 位小数
                        MediaController.LyricDelayOffset = (float)Math.Round(MediaController.LyricDelayOffset, 1);
                        Program.SaveSetting("LyricDelayOffset", MediaController.LyricDelayOffset);
                        Render();
                    }
                    else if (_autoHideToggleHovered && !Renderer.PassthroughModeEnabled)
                    {
                        NotchWindow.IsAutoHideEnabled = !NotchWindow.IsAutoHideEnabled;
                        // 保存自动隐藏开关
                        Program.SaveSetting("AutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0);

                        // 🎵🖥 级联关闭：自动隐藏是父开关，关掉它时两个附属开关必须一起关，
                        //    否则会留下「自动隐藏已关、岛体却因暂停/全屏而躲」的矛盾状态。
                        //    （反向不级联：重新开启自动隐藏**不会**自动打开附属开关，保持「默认关闭」。）
                        if (!NotchWindow.IsAutoHideEnabled)
                        {
                            if (NotchWindow.IsPauseAutoHideEnabled)
                            {
                                NotchWindow.IsPauseAutoHideEnabled = false;
                                Program.SaveSetting("PauseAutoHide", 0);
                            }
                            if (NotchWindow.IsFullscreenAutoHideEnabled)
                            {
                                NotchWindow.IsFullscreenAutoHideEnabled = false;
                                Program.SaveSetting("FullscreenAutoHide", 0);
                            }
                        }

                        Render();
                    }
                    else if (_pauseHideToggleHovered && !Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled)
                    {
                        // 🎵 「暂停播放后自动隐藏」：可用前提是自动隐藏已开启且非穿透模式
                        //    （与 hover 判定同源，双保险，防止状态不同步时被点到）
                        NotchWindow.IsPauseAutoHideEnabled = !NotchWindow.IsPauseAutoHideEnabled;
                        Program.SaveSetting("PauseAutoHide", NotchWindow.IsPauseAutoHideEnabled ? 1 : 0);

                        // 🔒 互斥：两个附属开关都只是「放宽允许隐藏的条件」，同时开着会让
                        //    「到底因为哪条才藏的」变得难以预期 —— 面板上只允许开一个。
                        //    开启本项时顺手关掉另一项并写回注册表。
                        if (NotchWindow.IsPauseAutoHideEnabled && NotchWindow.IsFullscreenAutoHideEnabled)
                        {
                            NotchWindow.IsFullscreenAutoHideEnabled = false;
                            Program.SaveSetting("FullscreenAutoHide", 0);
                        }

                        Render();
                    }
                    else if (_fsHideToggleHovered && !Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled)
                    {
                        // 🖥 「全屏自动隐藏」：可用前提同上（自动隐藏已开启 + 非穿透模式）
                        NotchWindow.IsFullscreenAutoHideEnabled = !NotchWindow.IsFullscreenAutoHideEnabled;
                        Program.SaveSetting("FullscreenAutoHide", NotchWindow.IsFullscreenAutoHideEnabled ? 1 : 0);

                        // 🔒 互斥：同上，开启本项时关掉另一项
                        if (NotchWindow.IsFullscreenAutoHideEnabled && NotchWindow.IsPauseAutoHideEnabled)
                        {
                            NotchWindow.IsPauseAutoHideEnabled = false;
                            Program.SaveSetting("PauseAutoHide", 0);
                        }

                        Render();
                    }
                    else if (_mediaExpToggleHovered)
                    {
                        Renderer.MediaInteractionMode = Renderer.MediaInteractionMode == 1 ? 0 : 1;
                        if (Renderer.MediaInteractionMode == 0) Renderer.IsMediaExpanded = false; // 关闭时强制收起
                        Program.SaveSetting("MediaInteractionMode", Renderer.MediaInteractionMode);
                        Render();
                    }
                    else if (_passToggleHovered)
                    {
                        Renderer.PassthroughModeEnabled = !Renderer.PassthroughModeEnabled;
                        Program.SaveSetting("PassthroughMode", Renderer.PassthroughModeEnabled ? 1 : 0);
                        Renderer.ApplyThemeColors(); // 立刻刷新基底色
                        Render();
                    }
                    else if (_clipboardToggleHovered)
                    {
                        // 📋 剪贴板链接检测开关（关闭后正在展示的链接会由渲染循环立即收起）
                        NotchWindow.IsClipboardEnabled = !NotchWindow.IsClipboardEnabled;
                        Program.SaveSetting("ClipboardEnabled", NotchWindow.IsClipboardEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_dropdownHovered)
                    {
                        _dropdownOpen = true; Render();
                    }
                    else if (_dropdownOpen && _hoveredDropdownIndex != -1)
                    {
                        _selectedPlatformIndex = _hoveredDropdownIndex;
                        MediaController.TargetPlatform = _platforms[_selectedPlatformIndex].Id;

                        // 保存目标媒体平台字符串
                        Program.SaveSetting("TargetPlatform", MediaController.TargetPlatform);

                        _ = MediaController.Instance?.ForceRefresh();
                        _dropdownOpen = false;
                        Render();
                    }
                    else if (_selectedTab == 1 && _hoveredDisplayOptionIndex != -1)
                    {
                        int previousMode = Renderer.StandbyDisplayMode;
                        _selectedDisplayIndex = _hoveredDisplayOptionIndex;
                        Renderer.StandbyDisplayMode = _selectedDisplayIndex;

                        // 进入硬件检测：快照当前宽度，然后强制拉宽
                        if (Renderer.StandbyDisplayMode == 2 && previousMode != 2)
                        {
                            // 保存用户切换前的真实宽度（只在首次进入时快照，防止反复覆盖）
                            if (_savedStandbyWidth < 0f)
                                _savedStandbyWidth = Renderer.STANDBY_WIDTH;

                            if (Renderer.STANDBY_WIDTH < 170f)
                            {
                                Renderer.STANDBY_WIDTH = 170f;
                                _customValues[0] = 170f;
                                Program.SaveSetting("Custom_StandbyW", 170f);
                                UpdateValueString(0);
                            }
                            if (Renderer.BASE_HEIGHT < 34f)
                            {
                                Renderer.BASE_HEIGHT = 34f;
                                _customValues[1] = 34f;
                                Program.SaveSetting("Custom_BaseH", 34f);
                                UpdateValueString(1);
                            }
                        }
                        // 离开硬件检测：恢复用户之前的宽度
                        else if (previousMode == 2 && Renderer.StandbyDisplayMode != 2)
                        {
                            float restoreWidth = _savedStandbyWidth > 0f ? _savedStandbyWidth : 125f;
                            Renderer.STANDBY_WIDTH = restoreWidth;
                            _customValues[0] = restoreWidth;
                            Program.SaveSetting("Custom_StandbyW", restoreWidth);
                            UpdateValueString(0);

                            // 重置快照，下次再进入硬件检测时重新记录
                            _savedStandbyWidth = -1f;
                        }

                        Program.SaveSetting("StandbyDisplayMode", _selectedDisplayIndex);
                        Render();
                    }
                    // 组合模式点击处理
                    else if (_compositeToggleHovered)
                    {
                        Renderer.CompositeModeEnabled = !Renderer.CompositeModeEnabled;
                        Program.SaveSetting("CompositeMode_Enabled", Renderer.CompositeModeEnabled ? 1 : 0);
                        Render();
                    }
                    else if (_compDateTimeHovered && Renderer.CompositeModeEnabled)
                    {
                        Renderer.CompShowDateTime = !Renderer.CompShowDateTime;
                        Program.SaveSetting("Composite_ShowDateTime", Renderer.CompShowDateTime ? 1 : 0);
                        Render();
                    }
                    else if (_compHardwareHovered && Renderer.CompositeModeEnabled)
                    {
                        Renderer.CompShowHardware = !Renderer.CompShowHardware;
                        Program.SaveSetting("Composite_ShowHardware", Renderer.CompShowHardware ? 1 : 0);
                        Render();
                    }
                    else if (_compMediaHovered && Renderer.CompositeModeEnabled)
                    {
                        Renderer.CompShowMedia = !Renderer.CompShowMedia;
                        Program.SaveSetting("Composite_ShowMedia", Renderer.CompShowMedia ? 1 : 0);
                        Render();
                    }
                    // ===== 插件中心交互 =====
                    else if (_selectedTab == 6 && _hoveredPluginAction != -1)
                    {
                        switch (_hoveredPluginAction)
                        {
                            case 0: ImportPluginDll(); break;
                            case 1: PluginManager.Instance.OpenPluginsFolder(); break;
                            case 2: PluginManager.Instance.OpenMarketplace(); break;
                        }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginToggle != -1)
                    {
                        var pe = GetPluginAt(_hoveredPluginToggle);
                        if (pe != null) { PluginManager.Instance.SetEnabled(pe, !pe.IsEnabled); ResetPluginHover(); RefreshPluginView(); Render(); }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginReload != -1)
                    {
                        var pe = GetPluginAt(_hoveredPluginReload);
                        if (pe != null) { PluginManager.Instance.Reload(pe); ResetPluginHover(); RefreshPluginView(); Render(); }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginRemove != -1)
                    {
                        var pe = GetPluginAt(_hoveredPluginRemove);
                        if (pe != null) { PluginManager.Instance.Remove(pe); ResetPluginHover(); RefreshPluginView(); Render(); }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginMoveLeft != -1)
                    {
                        // 🧩 左移：调整该插件在灵动岛上的显示顺序（持久化，立即生效）
                        var pe = GetPluginAt(_hoveredPluginMoveLeft);
                        if (pe != null && PluginManager.Instance.MoveOrder(pe, -1)) { RefreshPluginView(); Render(); }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginMoveRight != -1)
                    {
                        var pe = GetPluginAt(_hoveredPluginMoveRight);
                        if (pe != null && PluginManager.Instance.MoveOrder(pe, 1)) { RefreshPluginView(); Render(); }
                    }
                    break;

                case Win32.WM_PAINT:
                    return IntPtr.Zero;

                case Win32.WM_DESTROY:
                    if (_backdropHwnd != IntPtr.Zero)
                    {
                        IntPtr backdrop = _backdropHwnd;
                        _backdropHwnd = IntPtr.Zero;
                        Win32.DestroyWindow(backdrop);
                    }
                    _instance = null;
                    break;

                case Win32.WM_SETCURSOR:
                    if (_isHoveringDisabledArea && (lParam.ToInt32() & 0xFFFF) == 1) // 1 代表 HTCLIENT (客户区)
                    {
                        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, (int)32648)); // 强制注入系统 NO (禁止) 指针
                        return (IntPtr)1;
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private float GetBtnY(int index)
        {
            return index switch
            {
                -1 => TITLE_BAR_HEIGHT + 35,
                0 => TITLE_BAR_HEIGHT + 147 + 40,
                1 => TITLE_BAR_HEIGHT + 147 + 40 + 34,
                7 => TITLE_BAR_HEIGHT + 147 + 40 + 68,
                2 => TITLE_BAR_HEIGHT + 299 + 40,
                3 => TITLE_BAR_HEIGHT + 299 + 40 + 34,
                4 => TITLE_BAR_HEIGHT + 417 + 40,
                5 => TITLE_BAR_HEIGHT + 417 + 40 + 34,
                6 => TITLE_BAR_HEIGHT + 535 + 40,
                _ => 0
            };
        }

        // ================= 插件中心辅助逻辑 =================
        private void RefreshPluginView()
        {
            _pluginView = PluginManager.Instance.Entries.ToList();
        }

        /// <summary>
        /// 把「内容显示顺序表」渲染成一行可读文本（原生模块用中文名、插件用友好名）。
        /// 让用户一眼看到 ← / → 调整后，插件与原生功能在灵动岛上的真实左右次序。
        /// </summary>
        private string DescribeContentOrder()
        {
            var mgr = PluginManager.Instance;
            var order = mgr.Order;
            if (order.Count == 0) return "（暂无内容）";

            var sb = new System.Text.StringBuilder(96);
            for (int i = 0; i < order.Count; i++)
            {
                string item = order[i];
                string name;
                if (string.Equals(item, Plugins.BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase)) name = "时间日期";
                else if (string.Equals(item, Plugins.BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase)) name = "硬件占用";
                else if (string.Equals(item, Plugins.BuiltinWidgets.Media, StringComparison.OrdinalIgnoreCase)) name = "媒体控制器";
                else
                {
                    var pe = mgr.Find(item);
                    name = pe != null ? pe.FriendlyName : item;
                }
                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append(i + 1).Append('.').Append(name);
            }
            return sb.ToString();
        }

        private PluginEntry? GetPluginAt(int index)
            => index >= 0 && index < _pluginView.Count ? _pluginView[index] : null;

        /// <summary>列表变化后清空行内悬停索引，避免指向错行。</summary>
        private void ResetPluginHover()
        {
            _hoveredPluginToggle = -1;
            _hoveredPluginReload = -1;
            _hoveredPluginMoveLeft = -1;
            _hoveredPluginMoveRight = -1;
            _hoveredPluginRemove = -1;
        }

        /// <summary>
        /// 弹出传统 Win32 打开文件对话框（comdlg32!GetOpenFileNameW），返回选中路径；用户取消返回 null。
        ///
        /// 为什么不用 System.Windows.Forms.OpenFileDialog：
        ///   WinForms 的 OpenFileDialog 走 Vista「通用项对话框」，会在本进程内加载 ExplorerBrowser
        ///   + 外壳命名空间 + 图标/缩略图缓存 —— 首次打开就常驻 20~30MB，而且这是 Windows 的
        ///   进程级外壳组件，Dispose 对话框、关闭资源管理器都不会归还，看起来就像"内存泄漏"。
        ///   传统对话框是 comdlg32 的普通模态窗口，不碰 ExplorerBrowser，开销可以忽略。
        /// </summary>
        private static string? ShowOpenFileDialog(IntPtr owner, string title, string filter)
        {
            // OPENFILENAME 里的字符串字段在 Win32.cs 中被声明成 IntPtr，所以要手工分配/释放原生内存。
            // 不能改成 string / StringBuilder 字段：.NET 10 的 Marshal.SizeOf 遇到含托管引用字段的
            // 结构体会抛 ArgumentException，而 lStructSize 又必须精确，两者冲突，只能手工封送。
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pFile = IntPtr.Zero;
            try
            {
                const int maxFile = 1024; // 单位是「字符」而非字节，故下面的缓冲区要 ×2
                pFilter = Marshal.StringToHGlobalUni(filter);
                pTitle = Marshal.StringToHGlobalUni(title);
                pFile = Marshal.AllocHGlobal(maxFile * 2);
                Marshal.WriteInt16(pFile, 0); // 首字符置 0：不预填文件名，对话框沿用上次访问的目录

                var ofn = new Win32.OPENFILENAME
                {
                    lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAME>(),
                    hwndOwner = owner,
                    lpstrFilter = pFilter,
                    lpstrFile = pFile,
                    nMaxFile = maxFile,
                    lpstrTitle = pTitle,
                    // NOCHANGEDIR：传统对话框默认会把进程当前目录改成用户选的目录，必须禁掉
                    Flags = Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_NOCHANGEDIR
                };

                if (Win32.GetOpenFileNameW(ref ofn))
                {
                    string? result = Marshal.PtrToStringUni(pFile);
                    return string.IsNullOrEmpty(result) ? null : result;
                }

                // 返回 false 时可能是"用户取消"（CommDlgExtendedError == 0），也可能是真出错
                uint err = Win32.CommDlgExtendedError();
                if (err != 0) Logger.Warn($"[文件对话框] GetOpenFileNameW 失败，CommDlgExtendedError=0x{err:X}");
                return null;
            }
            finally
            {
                if (pFilter != IntPtr.Zero) Marshal.FreeHGlobal(pFilter);
                if (pTitle != IntPtr.Zero) Marshal.FreeHGlobal(pTitle);
                if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
            }
        }

        /// <summary>弹出文件选择框导入插件 DLL。</summary>
        private void ImportPluginDll()
        {
            try
            {
                string? picked = ShowOpenFileDialog(_hwnd, "选择 NotchPeninsula 插件 DLL",
                    "插件动态库 (*.dll)\0*.dll\0所有文件 (*.*)\0*.*\0");
                if (picked == null) return;

                var (ok, msg) = PluginManager.Instance.Import(picked);
                Logger.Info($"[PluginCenter] 导入结果: {(ok ? "成功" : "失败")} — {msg}");
                ResetPluginHover();
                RefreshPluginView();
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 导入插件异常", ex);
            }
        }

        /// <summary>
        /// 弹出文件对话框挑选字体文件，选中后热替换灵动岛全部文本字体，并把路径写入注册表实现记忆化。
        /// 加载失败时不做任何改动，只在卡片副标题上提示原因。
        /// 走的是传统 Win32 对话框（见 <see cref="ShowOpenFileDialog"/>），不会把外壳组件拉进进程。
        /// </summary>
        private void PickCustomFont()
        {
            try
            {
                string? picked = ShowOpenFileDialog(_hwnd, "选择灵动岛字体文件",
                    "字体文件 (*.ttf;*.otf;*.ttc)\0*.ttf;*.otf;*.ttc\0所有文件 (*.*)\0*.*\0");
                if (picked == null) return;

                if (FontConfig.ApplyCustomFont(picked, out string error))
                {
                    Program.SaveSetting("CustomFontPath", picked); // 记忆化：下次启动自动恢复
                    _fontHint = "";
                }
                else
                {
                    _fontHint = error;
                    Logger.Error($"[FontCenter] 字体加载失败：{error}");
                }
                Render();
            }
            catch (Exception ex)
            {
                _fontHint = "打开字体选择框失败";
                Logger.Error("[FontCenter] 选择字体异常", ex);
                Render();
            }
        }

        /// <summary>恢复系统字体（等价于从未选择过自定义字体），并清空注册表里的记忆。</summary>
        private void ResetCustomFont()
        {
            FontConfig.ResetToSystemFont();
            Program.SaveSetting("CustomFontPath", "");
            _fontHint = "";
            Render();
        }

        /// <summary>按像素宽度截断文本并追加省略号（零 GC 不敏感，交互时才调用）。</summary>
        private static string TruncateText(string? text, SKPaint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (paint.MeasureText(text) <= maxWidth) return text;
            for (int len = text.Length - 1; len > 0; len--)
            {
                var candidate = text[..len] + "…";
                if (paint.MeasureText(candidate) <= maxWidth) return candidate;
            }
            return "…";
        }

        /// <summary>收起所有下拉浮窗（同一时刻只允许展开一个）。</summary>
        private void CloseAllDropdowns()
        {
            _dropdownOpen = false;
            _monitorDropdownOpen = false;
            _toastModeDropdownOpen = false;
            _matchModeDropdownOpen = false;
            _appDropdownOpen = false;
        }

        private unsafe void Render()
        {
            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Scale(_dpiScale);
            canvas.Clear(SKColors.Transparent);
            float cornerRadius = 8f;
            var windowRect = new SKRect(0, 0, WIDTH, HEIGHT);

            canvas.DrawRoundRect(windowRect, cornerRadius, cornerRadius, _bgPaint);

            canvas.Save();
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(windowRect, cornerRadius, cornerRadius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            // 标题栏区：纯暗色模式仍保留传统顶栏；材质模式下不再额外盖一整块底色，让亚克力/云母连续透过。
            if (_backdropMode == BackdropMaterialMode.SolidDark)
                canvas.DrawRect(0, 0, WIDTH, TITLE_BAR_HEIGHT, _titleBarPaint);

            float textX = 14f;
            if (_appIconBitmap != null)
            {
                var iconRect = new SKRect(14, 8, 14 + 16, 8 + 16);
                canvas.DrawBitmap(_appIconBitmap, iconRect, _hqSamplingOpts);
                textX += 24f;
            }

            canvas.DrawText(_appTitleWithVersion, textX, 21.2f, _titleTextPaint);

            if (_minHovered) canvas.DrawRect(WIDTH - 92, 0, 46, TITLE_BAR_HEIGHT, _hoverMinPaint);
            if (_closeHovered) canvas.DrawRect(WIDTH - 46, 0, 46, TITLE_BAR_HEIGHT, _hoverClosePaint);

            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, _iconPaint);
            float cx = WIDTH - 46 + 23; float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, _iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, _iconPaint);

            // 侧边栏重排与分割线绘制
            void DrawTab(int index, string label, float yOffset)
            {
                var tabRect = new SKRect(10, TITLE_BAR_HEIGHT + yOffset, 170, TITLE_BAR_HEIGHT + yOffset + 36);
                if (_selectedTab == index)
                {
                    canvas.DrawRoundRect(tabRect, 4, 4, _tabBgSelected);
                    canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + yOffset + 8, 13, TITLE_BAR_HEIGHT + yOffset + 28), 1.5f, 1.5f, _tabIndicator);
                }
                else if (_hoveredTab == index)
                {
                    canvas.DrawRoundRect(tabRect, 4, 4, _tabBgHovered);
                }
                canvas.DrawText(label, 30, TITLE_BAR_HEIGHT + yOffset + 24, _uiTextPaint);
            }

            // 个性化中心最上，两条分割线
            DrawTab(5, "个性化中心", 10);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 52, 160, TITLE_BAR_HEIGHT + 52, _separatorPaint);
            DrawTab(0, "通用设置", 60);
            DrawTab(1, "显示设置", 100);
            DrawTab(2, "媒体设置", 140);
            DrawTab(3, "交互设置", 180);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 222, 160, TITLE_BAR_HEIGHT + 222, _separatorPaint);
            DrawTab(6, "插件中心", 230);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 272, 160, TITLE_BAR_HEIGHT + 272, _separatorPaint);
            DrawTab(4, "关于软件", 280);

            // 右侧卡片内容区
            void DrawToggleCard(float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
            {
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + 62);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
                DrawToggleRow(yOffset, title, sub, state, hovered, disabled);
            }

            // 只画「一行开关」的内容（标题 / 副标题 / 右侧开关），**不画卡片底**。
            // 拆出来是为了让「自动隐藏」那张卡片能在同一个卡片底里放两行（主开关 + 附属开关）。
            // 单行内容的位置（标题 +26 / 副标题 +46 / 开关 +20..+40）与原来完全一致，所以既有调用方行为不变。
            void DrawToggleRow(float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
            {
                _uiTextPaint.Color = disabled ? new SKColor(100, 100, 100) : SKColors.White;
                canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);
                _uiTextPaint.Color = SKColors.White;

                _subTextPaint.Color = disabled ? new SKColor(80, 80, 80) : new SKColor(170, 170, 170);
                canvas.DrawText(sub, 216, TITLE_BAR_HEIGHT + yOffset + 46, _subTextPaint);
                _subTextPaint.Color = new SKColor(170, 170, 170);

                float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = TITLE_BAR_HEIGHT + yOffset + 20;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);

                if (disabled)
                {
                    _dynamicStrokePaint.Color = new SKColor(80, 80, 80);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = new SKColor(100, 100, 100);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
                else
                {
                    if (state)
                    {
                        _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                        canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                    }
                    else
                    {
                        _dynamicStrokePaint.Color = hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                        canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    }

                    if (state) canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    else
                    {
                        _toggleCirclePaint.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                        canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                        _toggleCirclePaint.Color = SKColors.White;
                    }
                }
            }

            // 行内小开关（组合模式总开关用）
            void DrawToggleCard_Inline(float yOffset, string title, string sub, bool state, bool hovered)
            {
                canvas.DrawText(title, 216, yOffset + 16, _uiTextPaint);
                canvas.DrawText(sub, 216, yOffset + 36, _subTextPaint);
                float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = yOffset + 10;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                if (state)
                {
                    _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
            }

            // 勾选框选项
            void DrawCheckItem(float yOffset, string label, bool isChecked, bool hovered, bool disabled)
            {
                float boxX = 216;
                float boxY = yOffset;
                float boxSize = 16f;
                var boxRect = new SKRect(boxX, boxY, boxX + boxSize, boxY + boxSize);

                if (disabled)
                {
                    _dynamicStrokePaint.Color = new SKColor(80, 80, 80);
                    _dynamicFillPaint.Color = new SKColor(60, 60, 60);
                }
                else
                {
                    _dynamicStrokePaint.Color = isChecked ? new SKColor(0, 120, 212) : (hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100));
                    _dynamicFillPaint.Color = isChecked ? new SKColor(0, 120, 212) : SKColors.Transparent;
                }

                canvas.DrawRoundRect(boxRect, 3, 3, _dynamicFillPaint);
                canvas.DrawRoundRect(boxRect, 3, 3, _dynamicStrokePaint);

                if (isChecked)
                {
                    _iconPaint.Color = SKColors.White;
                    canvas.DrawLine(boxX + 3, boxY + 8, boxX + 6, boxY + 11, _iconPaint);
                    canvas.DrawLine(boxX + 6, boxY + 11, boxX + 13, boxY + 4, _iconPaint);
                }

                if (disabled)
                    _uiTextPaint.Color = new SKColor(100, 100, 100);
                else
                    _uiTextPaint.Color = SKColors.White;
                canvas.DrawText(label, boxX + 24, boxY + 13, _uiTextPaint);
                _uiTextPaint.Color = SKColors.White;
            }


            if (_selectedTab == 0)
            {
                DrawToggleCard(12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
                // 窗口置顶（与下方消息通知整组互换位置）
                DrawToggleCard(84, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
                // 🔔 「系统消息通知」与「消息通知内容」**合并为一张两行卡片**（2026-09-20）：
                //    两者都是通知选项，拆成两张卡显得零碎。
                //    卡片高 124（yOffset 156..280），行1 +156（开关 +176..+196）、
                //    行2 +218（下拉 +233..+265），中间一条分隔线（+214）点明主从关系。
                //    ⚠️ 改这里的数值时必须同步改上面 tab 0 的悬停热区
                //    （当前 +176..+196 / +233..+265 / +310..+330）。
                var notifyCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 156, WIDTH - 20, TITLE_BAR_HEIGHT + 280);
                canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBorder);

                DrawToggleRow(156, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);

                canvas.DrawLine(216, TITLE_BAR_HEIGHT + 214, WIDTH - 36, TITLE_BAR_HEIGHT + 214, _separatorPaint);

                // 行2：消息通知内容下拉（完整时展示应用名并拉大通知尺寸）
                canvas.DrawText("消息通知内容", 216, TITLE_BAR_HEIGHT + 244, _uiTextPaint);
                string toastModeDesc = _selectedToastModeIndex switch
                {
                    2 => "完整显示应用名、发送者与消息主体",
                    1 => "右侧展示“现在”与应用名",
                    _ => "仅显示发送者与消息主体"
                };
                canvas.DrawText(toastModeDesc, 216, TITLE_BAR_HEIGHT + 264, _subTextPaint);

                float tmdW = 110, tmdX = WIDTH - 140, tmdY = TITLE_BAR_HEIGHT + 233, tmdH = 32;
                var tmdRect = new SKRect(tmdX, tmdY, tmdX + tmdW, tmdY + tmdH);
                _dynamicFillPaint.Color = _toastModeDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
                canvas.DrawRoundRect(tmdRect, 4, 4, _dynamicFillPaint);
                canvas.DrawText(_toastModeOptions[_selectedToastModeIndex], tmdX + 10, tmdY + 21, _uiTextPaint);
                canvas.DrawLine(tmdX + tmdW - 20, tmdY + 14, tmdX + tmdW - 15, tmdY + 19, _chevronPaint);
                canvas.DrawLine(tmdX + tmdW - 15, tmdY + 19, tmdX + tmdW - 10, tmdY + 14, _chevronPaint);

                // 📋 剪贴板链接检测（2026-09-20 从「交互设置」搬来 —— 它是个功能开关，不属于交互行为）
                DrawToggleCard(290, "剪贴板链接检测", "复制链接时在刘海中显示，可一键在默认浏览器打开", NotchWindow.IsClipboardEnabled, _clipboardToggleHovered);

                // 切换灵动岛字体：选中字体文件后立即热替换岛内全部文本字体（默认系统字体，不做任何改动）
                var fontCard = new SKRect(200, TITLE_BAR_HEIGHT + FONT_CARD_Y, WIDTH - 20, TITLE_BAR_HEIGHT + FONT_CARD_Y + 62);
                canvas.DrawRoundRect(fontCard, 6, 6, _cardBg);
                canvas.DrawRoundRect(fontCard, 6, 6, _cardBorder);
                canvas.DrawText("切换灵动岛字体", 216, TITLE_BAR_HEIGHT + FONT_CARD_Y + 26, _uiTextPaint);

                bool fontError = _fontHint.Length > 0;
                string fontSub = fontError ? _fontHint : $"当前：{FontConfig.DisplayName}";
                _subTextPaint.Color = fontError ? new SKColor(232, 100, 100) : new SKColor(170, 170, 170);
                canvas.DrawText(TruncateText(fontSub, _subTextPaint, FONT_PICK_X - 216 - 8), 216, TITLE_BAR_HEIGHT + FONT_CARD_Y + 46, _subTextPaint);
                _subTextPaint.Color = new SKColor(170, 170, 170);

                void DrawFontButton(bool hovered, string label, float bx, float bw)
                {
                    var btn = new SKRect(bx, TITLE_BAR_HEIGHT + FONT_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H);
                    _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                    float tw = _uiTextPaint.MeasureText(label);
                    canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + FONT_BTN_Y + 18, _uiTextPaint);
                }

                DrawFontButton(_fontPickHovered, "选择字体", FONT_PICK_X, FONT_PICK_W);
                DrawFontButton(_fontResetHovered, "重置", FONT_RESET_X, FONT_RESET_W);
            }
            else if (_selectedTab == 1)
            {
                // 刘海形态两列布局选择器
                var styleCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 160);
                canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBorder);
                canvas.DrawText("刘海形态", 216, TITLE_BAR_HEIGHT + 38, _uiTextPaint);

                void DrawStyleOption(int index, string name, float x, float y)
                {
                    bool isSelected = Renderer.NotchStyle == index;
                    bool isHovered = _hoveredStyleIndex == index;

                    // 选项外框与背景反馈
                    var optRect = new SKRect(x, y, x + 150, y + 90);
                    _dynamicFillPaint.Color = isSelected ? new SKColor(0, 120, 212, 40) : (isHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8));
                    canvas.DrawRoundRect(optRect, 6, 6, _dynamicFillPaint);
                    _dynamicStrokePaint.Color = isSelected ? new SKColor(0, 120, 212) : new SKColor(80, 80, 80);
                    canvas.DrawRoundRect(optRect, 6, 6, _dynamicStrokePaint);

                    // 绘制纯血 Skia 伪 PNG 视觉特效图
                    float cx = x + 75; float cy = y + 35;

                    // 颜色直接同步真实的明暗逻辑，并完美兼容“跟随系统”模式
                    bool isLight = Renderer.ThemeMode == 1 || (Renderer.ThemeMode == 2 && Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is int val && val == 1);
                    _dynamicFillPaint.Color = isLight ? SKColors.White : SKColors.Black;

                    if (index == 0) // 调整经典刘海的矢量绘图比例，使其视觉高度和灵动岛保持一致
                    {
                        var path = new SKPath();
                        path.MoveTo(cx - 35, cy - 10);
                        path.QuadTo(cx - 25, cy - 10, cx - 25, cy - 5);
                        path.LineTo(cx - 25, cy + 5);
                        path.QuadTo(cx - 25, cy + 10, cx - 15, cy + 10);
                        path.LineTo(cx + 15, cy + 10);
                        path.QuadTo(cx + 25, cy + 10, cx + 25, cy + 5);
                        path.LineTo(cx + 25, cy - 5);
                        path.QuadTo(cx + 25, cy - 10, cx + 35, cy - 10);
                        canvas.DrawPath(path, _dynamicFillPaint);
                    }
                    else // 模拟灵动岛
                    {
                        // 统一高度 20px，圆角 10px 形成胶囊
                        canvas.DrawRoundRect(new SKRect(cx - 25, cy - 10, cx + 25, cy + 10), 10, 10, _dynamicFillPaint);
                    }

                    // 单选 Radio 按钮与文本
                    float radioY = y + 72;
                    canvas.DrawCircle(cx - 30, radioY - 4, 6, _dynamicStrokePaint);
                    if (isSelected)
                    {
                        _dynamicFillPaint.Color = new SKColor(0, 120, 212);
                        canvas.DrawCircle(cx - 30, radioY - 4, 3, _dynamicFillPaint);
                    }
                    _dynamicTextPaint.Color = isSelected ? new SKColor(0, 140, 240) : SKColors.White;
                    canvas.DrawText(name, cx - 15, radioY + 1, _dynamicTextPaint);
                }

                DrawStyleOption(0, "经典刘海", 220, TITLE_BAR_HEIGHT + 50);
                DrawStyleOption(1, "悬浮胶囊", 390, TITLE_BAR_HEIGHT + 50);

                // 目标显示器卡片
                float monitorCardY = TITLE_BAR_HEIGHT + 172;
                var mCardRect = new SKRect(200, monitorCardY, WIDTH - 20, monitorCardY + 62);
                canvas.DrawRoundRect(mCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(mCardRect, 6, 6, _cardBorder);
                canvas.DrawText("目标显示器", 216, monitorCardY + 26, _uiTextPaint);
                canvas.DrawText("选择刘海灵动岛显示的目标屏幕", 216, monitorCardY + 46, _subTextPaint);

                float mdW = 110; float mdX = WIDTH - 140; float mdY = monitorCardY + 14; float mdH = 32;
                var mdRect = new SKRect(mdX, mdY, mdX + mdW, mdY + mdH);
                _dynamicFillPaint.Color = _monitorDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
                canvas.DrawRoundRect(mdRect, 4, 4, _dynamicFillPaint);
                string mName = Renderer.TargetMonitorIndex < _monitorOptions.Length ? _monitorOptions[Renderer.TargetMonitorIndex] : "未知";
                canvas.DrawText(mName, mdX + 10, mdY + 21, _uiTextPaint);
                canvas.DrawLine(mdX + mdW - 20, mdY + 14, mdX + mdW - 15, mdY + 19, _chevronPaint);
                canvas.DrawLine(mdX + mdW - 15, mdY + 19, mdX + mdW - 10, mdY + 14, _chevronPaint);

                // 待机显示内容卡片 (极简宫格布局)
                float displayCardY = TITLE_BAR_HEIGHT + 248;
                var displayCardRect = new SKRect(200, displayCardY, WIDTH - 20, displayCardY + 112); // 卡片高度减半收缩
                canvas.DrawRoundRect(displayCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(displayCardRect, 6, 6, _cardBorder);
                _uiTextPaint.Color = Renderer.CompositeModeEnabled ? new SKColor(100, 100, 100) : SKColors.White;
                canvas.DrawText("待机显示内容", 216, displayCardY + 26, _uiTextPaint);
                _uiTextPaint.Color = SKColors.White;
                _subTextPaint.Color = Renderer.CompositeModeEnabled ? new SKColor(80, 80, 80) : new SKColor(170, 170, 170);
                canvas.DrawText("刘海处于待机状态时默认展示的信息", 216, displayCardY + 46, _subTextPaint);
                _subTextPaint.Color = new SKColor(170, 170, 170);
                void DrawDisplayOpt(int index, string name, float x, float y)
                {
                    bool isDisabled = Renderer.CompositeModeEnabled;
                    bool isSelected = _selectedDisplayIndex == index && !isDisabled;
                    bool isHovered = _hoveredDisplayOptionIndex == index && !isDisabled;

                    var optRect = new SKRect(x, y, x + 110, y + 40);
                    _dynamicFillPaint.Color = isDisabled ? new SKColor(255, 255, 255, 3) : (isSelected ? new SKColor(0, 120, 212, 40) : (isHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8)));
                    canvas.DrawRoundRect(optRect, 6, 6, _dynamicFillPaint);
                    _dynamicStrokePaint.Color = isDisabled ? new SKColor(60, 60, 60) : (isSelected ? new SKColor(0, 120, 212) : new SKColor(80, 80, 80));
                    canvas.DrawRoundRect(optRect, 6, 6, _dynamicStrokePaint);

                    float cx = x + 20; float cy = y + 20;
                    _dynamicStrokePaint.Color = isDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 140, 240) : SKColors.White);
                    _dynamicStrokePaint.StrokeWidth = 1.5f;

                    if (index == 0) { canvas.DrawCircle(cx, cy, 8, _dynamicStrokePaint); canvas.DrawLine(cx, cy, cx, cy - 4, _dynamicStrokePaint); canvas.DrawLine(cx, cy, cx + 3, cy + 3, _dynamicStrokePaint); }
                    else if (index == 1) { canvas.DrawLine(cx - 6, cy, cx + 6, cy, _dynamicStrokePaint); }
                    else if (index == 2) { canvas.DrawRect(cx - 7, cy - 6, 14, 12, _dynamicStrokePaint); canvas.DrawLine(cx - 3, cy - 3, cx + 3, cy - 3, _dynamicStrokePaint); }

                    _dynamicTextPaint.Color = isDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 140, 240) : SKColors.White);
                    canvas.DrawText(name, cx + 18, cy + 5, _dynamicTextPaint);
                }

                DrawDisplayOpt(2, "硬件占用", 220, displayCardY + 58);
                DrawDisplayOpt(0, "时间日期", 340, displayCardY + 58);
                DrawDisplayOpt(1, "空白", 460, displayCardY + 58);

                // 自定义组合模式卡片
                float compositeCardY = TITLE_BAR_HEIGHT + 372;
                var compositeCardRect = new SKRect(200, compositeCardY, WIDTH - 20, compositeCardY + 200);
                canvas.DrawRoundRect(compositeCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(compositeCardRect, 6, 6, _cardBorder);
                canvas.DrawText("自定义组合模式", 216, compositeCardY + 26, _uiTextPaint);
                canvas.DrawText("自由选择刘海内显示的功能模块", 216, compositeCardY + 46, _subTextPaint);

                // 总开关
                DrawToggleCard_Inline(compositeCardY + 62, "启用组合模式", "开启后可同时显示多个功能模块", Renderer.CompositeModeEnabled, _compositeToggleHovered);

                // 子选项
                DrawCheckItem(compositeCardY + 105, "时间日期", Renderer.CompShowDateTime, _compDateTimeHovered, !Renderer.CompositeModeEnabled);
                DrawCheckItem(compositeCardY + 140, "资源占用检测", Renderer.CompShowHardware, _compHardwareHovered, !Renderer.CompositeModeEnabled);
                DrawCheckItem(compositeCardY + 175, "媒体控制器(含频谱)", Renderer.CompShowMedia, _compMediaHovered, !Renderer.CompositeModeEnabled);

            }
            else if (_selectedTab == 2)
            {
                DrawToggleCard(12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

                // 🎚 合并卡片：「目标媒体平台」+「匹配方式」共用一张卡（两行 × 62 = 124 高，84..208）
                //    第 1 行行首 84 / 分隔线 142（行首 +58）/ 第 2 行行首 146（行距 62）
                //    ⚠️ 本段所有 y 值必须与 WM_MOUSEMOVE 的 tab 2 命中段保持同步（见字段区的坐标常量注释）
                var platformCardRect = new SKRect(200, PLATFORM_CARD_Y, WIDTH - 20, PLATFORM_CARD_Y + 124);
                canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBorder);

                // ── 第 1 行：目标媒体平台 ──
                canvas.DrawText("目标媒体平台", 216, TITLE_BAR_HEIGHT + 110, _uiTextPaint);
                canvas.DrawText(MediaController.TargetPlatform == "browser" ? "仅接管浏览器内的播放会话" : "多平台共存时，优先截获并接管的平台",
                    216, TITLE_BAR_HEIGHT + 130, _subTextPaint);

                float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                _dynamicFillPaint.Color = _dropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
                canvas.DrawRoundRect(dRect, 4, 4, _dynamicFillPaint);
                canvas.DrawText(_platforms[_selectedPlatformIndex].Name, dX + 10, dY + 21, _uiTextPaint);

                canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, _chevronPaint);
                canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, _chevronPaint);

                // 两行之间的分隔线
                canvas.DrawLine(216, TITLE_BAR_HEIGHT + 142, WIDTH - 36, TITLE_BAR_HEIGHT + 142, _separatorPaint);

                // ── 第 2 行：匹配方式（仅「通用媒体」下可选，其余平台整行置灰）──
                bool matchEnabled = MediaController.TargetPlatform == "other";
                bool appBoxEnabled = matchEnabled && MediaController.IsManualSessionMatch;
                _uiTextPaint.Color = matchEnabled ? SKColors.White : new SKColor(100, 100, 100);
                canvas.DrawText("匹配方式", 216, PLATFORM_ROW2_Y + 26, _uiTextPaint);
                _uiTextPaint.Color = SKColors.White;
                _subTextPaint.Color = matchEnabled ? new SKColor(170, 170, 170) : new SKColor(80, 80, 80);
                canvas.DrawText("自动匹配或手动指定", 216, PLATFORM_ROW2_Y + 46, _subTextPaint);
                _subTextPaint.Color = new SKColor(170, 170, 170);

                float moX = MATCH_MODE_X, appX = MATCH_APP_X, mBoxY = MATCH_ROW_Y, mBoxW = MATCH_BOX_W, mBoxH = MATCH_BOX_H;

                // 左框：自动匹配 / 手动选择软件
                var moRect = new SKRect(moX, mBoxY, moX + mBoxW, mBoxY + mBoxH);
                _dynamicFillPaint.Color = !matchEnabled ? new SKColor(255, 255, 255, 4)
                    : (_matchModeDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8));
                canvas.DrawRoundRect(moRect, 4, 4, _dynamicFillPaint);
                _dynamicTextPaint.Color = matchEnabled ? SKColors.White : new SKColor(100, 100, 100);
                canvas.DrawText(_matchModeOptions[MediaController.IsManualSessionMatch ? 1 : 0], moX + 10, mBoxY + 21, _dynamicTextPaint);
                _dynamicTextPaint.Color = SKColors.White;
                _chevronPaint.Color = matchEnabled ? new SKColor(150, 150, 150) : new SKColor(90, 90, 90);
                canvas.DrawLine(moX + mBoxW - 20, mBoxY + 14, moX + mBoxW - 15, mBoxY + 19, _chevronPaint);
                canvas.DrawLine(moX + mBoxW - 15, mBoxY + 19, moX + mBoxW - 10, mBoxY + 14, _chevronPaint);

                // 右框：手动模式的目标软件（直接显示 AppID）；自动匹配或非通用媒体时置灰
                var appRect = new SKRect(appX, mBoxY, appX + mBoxW, mBoxY + mBoxH);
                _dynamicFillPaint.Color = appBoxEnabled ? (_appDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8))
                    : new SKColor(255, 255, 255, 4);
                canvas.DrawRoundRect(appRect, 4, 4, _dynamicFillPaint);
                string appLabel = !appBoxEnabled ? "自动匹配"
                    : (!MediaController.HasActiveSessions ? ""
                        : (MediaController.ManualSessionAppId.Length > 0 ? MediaController.ManualSessionAppId : "未选择"));
                _dynamicTextPaint.Color = appBoxEnabled ? SKColors.White : new SKColor(100, 100, 100);
                canvas.DrawText(TruncateText(appLabel, _dynamicTextPaint, mBoxW - 32), appX + 10, mBoxY + 21, _dynamicTextPaint);
                _dynamicTextPaint.Color = SKColors.White;
                _chevronPaint.Color = appBoxEnabled ? new SKColor(150, 150, 150) : new SKColor(90, 90, 90);
                canvas.DrawLine(appX + mBoxW - 20, mBoxY + 14, appX + mBoxW - 15, mBoxY + 19, _chevronPaint);
                canvas.DrawLine(appX + mBoxW - 15, mBoxY + 19, appX + mBoxW - 10, mBoxY + 14, _chevronPaint);
                _chevronPaint.Color = new SKColor(150, 150, 150);

                // 歌词设置卡片（合并卡片 208 底 + 14 间距；与 WM_MOUSEMOVE 的 lyricY 同源）
                float lyricY = LYRIC_CARD_Y;
                var lyricRect = new SKRect(200, lyricY, WIDTH - 20, lyricY + 176);
                canvas.DrawRoundRect(lyricRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(lyricRect, 6, 6, _cardBorder);
                canvas.DrawText("歌词设置", 216, lyricY + 26, _uiTextPaint);

                // 歌词开关
                canvas.DrawText("在刘海中显示歌词", 216, lyricY + 52, _subTextPaint);
                float tW = 42, tH = 20;
                float tX = WIDTH - 20 - 16 - tW, tY = lyricY + 37;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                if (MediaController.IsLyricsEnabled)
                {
                    _dynamicFillPaint.Color = _lyricToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = _lyricToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = _lyricToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }

                // 翻译歌词开关：译文作为第二行画在原文下方（仅当这句有译文时出现）
                canvas.DrawText("显示翻译歌词（上下两行）", 216, lyricY + 92, _subTextPaint);
                float trY = lyricY + 77;
                var trRect = new SKRect(tX, trY, tX + tW, trY + tH);
                if (MediaController.IsTranslationEnabled)
                {
                    _dynamicFillPaint.Color = _transToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(trRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, trY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = _transToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(trRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = _transToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, trY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }

                // 卡拉OK效果开关
                canvas.DrawText("开启卡拉OK动效", 216, lyricY + 132, _subTextPaint);
                float kY = lyricY + 117;
                var kRect = new SKRect(tX, kY, tX + tW, kY + tH);
                if (MediaController.IsKaraokeEnabled)
                {
                    _dynamicFillPaint.Color = _karaokeToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = _karaokeToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = _karaokeToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }

                // 延迟调整
                canvas.DrawText("歌词延迟补偿", 216, lyricY + 164, _subTextPaint);
                float cardRightX = WIDTH - 36;
                float btnY = lyricY + 147;

                _dynamicFillPaint.Color = _lyricMinusHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                canvas.DrawRoundRect(new SKRect(cardRightX - 175, btnY, cardRightX - 145, btnY + 24), 4, 4, _dynamicFillPaint);
                canvas.DrawText("-", cardRightX - 164, btnY + 17, _uiTextPaint);

                string valStr = $"{MediaController.LyricDelayOffset:F1} s";
                if (MediaController.LyricDelayOffset > 0) valStr = "+" + valStr;
                float textW = _uiTextPaint.MeasureText(valStr);
                canvas.DrawText(valStr, cardRightX - 90 - textW, btnY + 17, _uiTextPaint);

                _dynamicFillPaint.Color = _lyricPlusHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                canvas.DrawRoundRect(new SKRect(cardRightX - 80, btnY, cardRightX - 50, btnY + 24), 4, 4, _dynamicFillPaint);
                canvas.DrawText("+", cardRightX - 69, btnY + 17, _uiTextPaint);

                _dynamicFillPaint.Color = _lyricResetHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                canvas.DrawRoundRect(new SKRect(cardRightX - 40, btnY, cardRightX, btnY + 24), 4, 4, _dynamicFillPaint);
                canvas.DrawText("重置", cardRightX - 33, btnY + 17, _subTextPaint);
            }
            else if (_selectedTab == 3)
            {
                // 🎵🖥 「自动隐藏」与它的两个附属开关（暂停播放后 / 全屏时）**共用同一张卡片**，
                //    所以卡片底要单独画成三行高（186px，yOffset 12..198），再用 DrawToggleRow 画三行内容。
                //    三行 yOffset 12 / 74 / 136（行距 62），行间各一条分隔线，让「附属」关系一眼可见。
                //    两个附属开关**互斥**（见点击处理），所以两行看起来是「二选一」的一组。
                //    ⚠️ 改这里的数值时必须同步改上面 tab 3 的悬停热区（当前 +32/+94/+156/+228/+300）。
                //    （「剪贴板链接检测」已于 2026-09-20 搬到「通用设置」，这里只剩三张卡）
                bool isAutoHideDisabled = Renderer.PassthroughModeEnabled;
                var autoHideCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 198);
                canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBorder);

                DrawToggleRow(12, "自动隐藏", isAutoHideDisabled ? "穿透模式下禁止自动隐藏" : "当焦点离开时自动隐藏刘海",
                    NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered, isAutoHideDisabled);

                canvas.DrawLine(216, TITLE_BAR_HEIGHT + 70, WIDTH - 36, TITLE_BAR_HEIGHT + 70, _separatorPaint);

                // 附属开关的可用性 = 自动隐藏已开启 且 非穿透模式。
                // 两者任一不满足都置灰并**显示为关闭** —— 与「自动隐藏」在穿透模式下置灰显示为关闭的既有约定一致，
                // 免得出现「开关看着是开的、却怎么都不生效」的困惑。
                bool isPauseHideDisabled = isAutoHideDisabled || !NotchWindow.IsAutoHideEnabled;
                DrawToggleRow(74, "暂停播放后自动隐藏",
                    isAutoHideDisabled ? "穿透模式下禁止自动隐藏"
                        : !NotchWindow.IsAutoHideEnabled ? "需先开启「自动隐藏」"
                        : "媒体暂停播放时，也把刘海藏起来",
                    NotchWindow.IsPauseAutoHideEnabled,
                    !isPauseHideDisabled && _pauseHideToggleHovered,
                    isPauseHideDisabled);

                canvas.DrawLine(216, TITLE_BAR_HEIGHT + 132, WIDTH - 36, TITLE_BAR_HEIGHT + 132, _separatorPaint);

                // 🖥 「全屏自动隐藏」：检测到全屏视频 / 全屏游戏（含独占 D3D）时无条件让位，播放中也不显示。
                //    与上面那行**互斥**，所以副标题里点明「二者只开一个」。
                bool isFsHideDisabled = isAutoHideDisabled || !NotchWindow.IsAutoHideEnabled;
                DrawToggleRow(136, "全屏自动隐藏",
                    isAutoHideDisabled ? "穿透模式下禁止自动隐藏"
                        : !NotchWindow.IsAutoHideEnabled ? "需先开启「自动隐藏」"
                        : "检测到全屏视频 / 游戏时隐藏",
                    NotchWindow.IsFullscreenAutoHideEnabled,
                    !isFsHideDisabled && _fsHideToggleHovered,
                    isFsHideDisabled);

                bool isMediaExpDisabled = Renderer.CompositeModeEnabled;
                DrawToggleCard(208, "媒体交互方式", isMediaExpDisabled ? "组合模式下固定为直接交互" : "开启为展开交互，关闭为直接交互",
                    isMediaExpDisabled ? false : (Renderer.MediaInteractionMode == 1),
                    !isMediaExpDisabled && _mediaExpToggleHovered,
                    isMediaExpDisabled);

                DrawToggleCard(280, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered);
            }
            else if (_selectedTab == 4)
            {
                float centerX = 200 + (WIDTH - 200) / 2f;
                float startY = TITLE_BAR_HEIGHT + 30f;

                if (_appIconBitmap != null)
                {
                    var iconRect = new SKRect(centerX - 32, startY, centerX + 32, startY + 64);
                    canvas.DrawBitmap(_appIconBitmap, iconRect, _hqSamplingOpts);
                    startY += 90f;
                }

                _dynamicTextPaint.Color = SKColors.White;
                _dynamicTextPaint.TextSize = 20f;
                _dynamicTextPaint.TextAlign = SKTextAlign.Center;
                canvas.DrawText("NotchPeninsula", centerX, startY, _dynamicTextPaint);
                startY += 22f;

                _dynamicTextPaint.Color = new SKColor(170, 170, 170);
                _dynamicTextPaint.TextSize = 13f;
                string displayVersion = _appTitleWithVersion.Replace("NotchPeninsula ", "NPS v");
                canvas.DrawText(displayVersion, centerX, startY, _dynamicTextPaint);
                startY += 35f;

                string[] links = ["检测更新", "项目仓库", "开发者"];
                _dynamicTextPaint.TextAlign = SKTextAlign.Left;

                float spacing = 15f;
                float totalWidth = _dynamicTextPaint.MeasureText(links[0]) + _dynamicTextPaint.MeasureText(links[1]) + _dynamicTextPaint.MeasureText(links[2]) + (spacing * 2);
                float currentX = centerX - (totalWidth / 2f);

                for (int i = 0; i < links.Length; i++)
                {
                    float textWidth = _dynamicTextPaint.MeasureText(links[i]);
                    _dynamicTextPaint.Color = _hoveredLinkIndex == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawText(links[i], currentX, startY, _dynamicTextPaint);
                    currentX += textWidth + spacing;
                }
            }
            else if (_selectedTab == 5)
            {
                void DrawMultiCard(float yOffset, string title, string[] subLabels, int[] indices, string unit)
                {
                    // 检测该卡片对应的尺寸设置是否已被改动
                    bool isModified = false;
                    foreach (int index in indices)
                    {
                        if (Math.Abs(_customValues[index] - _defaultCustomValues[index]) > 0.001f)
                        {
                            isModified = true;
                            break;
                        }
                    }

                    float cardHeight = 36 + subLabels.Length * 34;
                    var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + cardHeight);
                    canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                    canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

                    canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);

                    // 如果改动了某个尺寸设置，在标题旁边显示已生效标签
                    if (isModified)
                    {
                        float titleWidth = _uiTextPaint.MeasureText(title);
                        float tagX = 216 + titleWidth + 10;
                        float tagY = TITLE_BAR_HEIGHT + yOffset + 13;
                        var tagRect = new SKRect(tagX, tagY, tagX + 38, tagY + 18);

                        _dynamicFillPaint.Color = new SKColor(0, 120, 212, 35); // 浅背景颜色
                        canvas.DrawRoundRect(tagRect, 3f, 3f, _dynamicFillPaint); // 小圆角

                        _dynamicTextPaint.TextSize = 10f; // 小文本样式
                        _dynamicTextPaint.Color = new SKColor(0, 140, 240);
                        canvas.DrawText("已生效", tagX + 4, tagY + 13, _dynamicTextPaint);
                        _dynamicTextPaint.TextSize = 13f; // 还原字号，防止污染后续文字渲染
                    }

                    for (int i = 0; i < subLabels.Length; i++)
                    {
                        int index = indices[i];
                        float cardBtnY = GetBtnY(index);

                        canvas.DrawText(subLabels[i], 216, cardBtnY + 17, _subTextPaint);
                        float cardRightX = WIDTH - 36;

                        _dynamicFillPaint.Color = _hoveredMinusIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                        canvas.DrawRoundRect(new SKRect(cardRightX - 175, cardBtnY, cardRightX - 145, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                        canvas.DrawText("-", cardRightX - 164, cardBtnY + 17, _uiTextPaint);

                        // 使用静态缓存字符串，零 GC 开销
                        string valStr = _valStrCache[index];
                        float textW = _uiTextPaint.MeasureText(valStr);
                        canvas.DrawText(valStr, cardRightX - 90 - textW, cardBtnY + 17, _uiTextPaint);

                        _dynamicFillPaint.Color = _hoveredPlusIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                        canvas.DrawRoundRect(new SKRect(cardRightX - 80, cardBtnY, cardRightX - 50, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                        canvas.DrawText("+", cardRightX - 69, cardBtnY + 17, _uiTextPaint);

                        _dynamicFillPaint.Color = _hoveredResetIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                        canvas.DrawRoundRect(new SKRect(cardRightX - 40, cardBtnY, cardRightX, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                        canvas.DrawText("重置", cardRightX - 33, cardBtnY + 17, _subTextPaint);
                    }
                }

                // 绘制新增的主题卡片
                float themeY = TITLE_BAR_HEIGHT + 12;
                var themeRect = new SKRect(200, themeY, WIDTH - 20, themeY + 125);
                canvas.DrawRoundRect(themeRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(themeRect, 6, 6, _cardBorder);

                canvas.DrawText("刘海 / 灵动岛主题", 216, themeY + 26, _uiTextPaint);
                canvas.DrawText("背景与文本颜色自适应反转", 216, themeY + 46, _subTextPaint);

                float themeRightX = WIDTH - 36; // 变量隔离
                float btnY = GetBtnY(-1);

                void DrawThemeBtn(int index, string label, float leftOffset, float rightOffset)
                {
                    bool isActive = Renderer.ThemeMode == index;
                    bool isHovered = _hoveredThemeIndex == index;
                    float btnWidth = leftOffset - rightOffset;

                    _dynamicFillPaint.Color = (isActive || isHovered) ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                    canvas.DrawRoundRect(new SKRect(themeRightX - leftOffset, btnY, themeRightX - rightOffset, btnY + 24), 4, 4, _dynamicFillPaint);

                    _dynamicTextPaint.Color = isActive ? new SKColor(0, 140, 240) : SKColors.White;

                    // 根据文本真实长度在胶囊内部完美居中
                    float textWidth = _dynamicTextPaint.MeasureText(label);
                    float textX = themeRightX - leftOffset + (btnWidth - textWidth) / 2f;
                    canvas.DrawText(label, textX, btnY + 17, _dynamicTextPaint);
                }

                DrawThemeBtn(0, "黑", 140, 100);
                DrawThemeBtn(1, "白", 90, 50);
                DrawThemeBtn(2, "系统", 40, 0);
                canvas.DrawText("背景透明度", 216, themeY + 75, _subTextPaint);
                float sliderY = themeY + 95;
                float sliderX = 216;
                float sliderW = WIDTH - 40 - 216;
                // 背景透明度滑轨
                canvas.DrawLine(sliderX, sliderY, sliderX + sliderW, sliderY, _separatorPaint);
                float activePx = sliderX + (sliderW / 4) * Renderer.BgOpacityLevel;
                _dynamicStrokePaint.Color = new SKColor(0, 120, 212);
                _dynamicStrokePaint.StrokeWidth = 2f;
                canvas.DrawLine(sliderX, sliderY, activePx, sliderY, _dynamicStrokePaint);
                _dynamicStrokePaint.StrokeWidth = 1.5f;
                bool isOpacityDisabled = Renderer.PassthroughModeEnabled;
                _dynamicStrokePaint.Color = isOpacityDisabled ? new SKColor(80, 80, 80) : new SKColor(0, 120, 212);
                for (int i = 0; i < 5; i++)
                {
                    float px = sliderX + (sliderW / 4) * i;
                    bool isSelected = Renderer.BgOpacityLevel == i;
                    bool isHovered = _hoveredOpacityIndex == i;
                    // 只画当前选中的小蓝球，或者鼠标悬停时的半透明反馈，去掉丑陋的灰色固定点
                    if (isSelected || isHovered)
                    {
                        _dynamicFillPaint.Color = isOpacityDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 120, 212) : new SKColor(255, 255, 255, 80));
                        canvas.DrawCircle(px, sliderY, isSelected ? 6 : 4, _dynamicFillPaint);
                    }
                    _dynamicTextPaint.Color = isOpacityDisabled ? new SKColor(100, 100, 100) : (isSelected ? SKColors.White : new SKColor(150, 150, 150));
                    _dynamicTextPaint.TextSize = 11f;
                    string pct = (i * 25) + "%";
                    float tw = _dynamicTextPaint.MeasureText(pct);
                    canvas.DrawText(pct, px - tw / 2, sliderY + 18, _dynamicTextPaint);
                    _dynamicTextPaint.TextSize = 13f;
                }
                DrawMultiCard(147, "待机显示", ["水平宽度", "垂直高度", "底部圆角"], [0, 1, 7], "px");
                DrawMultiCard(299, "媒体控制", ["激活时宽度", "激活时高度"], [2, 3], "px");
                DrawMultiCard(417, "消息通知", ["弹出的宽度", "弹出的高度"], [4, 5], "px");
                DrawMultiCard(535, "全局 DPI 缩放", ["视觉比例"], [6], "x");
            }
            else if (_selectedTab == 6)
            {
                RefreshPluginView();

                // ── 顶部操作卡片 ──
                float topY = TITLE_BAR_HEIGHT + 12;
                var topRect = new SKRect(200, topY, WIDTH - 20, topY + 96);
                canvas.DrawRoundRect(topRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(topRect, 6, 6, _cardBorder);
                canvas.DrawText("插件中心", 216, topY + 26, _uiTextPaint);
                canvas.DrawText("导入第三方 DLL 扩展灵动岛能力，支持热重载", 216, topY + 46, _subTextPaint);

                void DrawPluginButton(int index, string label, float bx, float by, float bw)
                {
                    bool hovered = _hoveredPluginAction == index;
                    var btn = new SKRect(bx, by, bx + bw, by + 24);
                    _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                    float tw = _uiTextPaint.MeasureText(label);
                    canvas.DrawText(label, bx + (bw - tw) / 2f, by + 17, _uiTextPaint);
                }

                DrawPluginButton(0, "导入 DLL", 216, topY + 60, 96);
                DrawPluginButton(1, "打开目录", 320, topY + 60, 96);
                DrawPluginButton(2, "插件市场", 424, topY + 60, 96);

                // ── 插件列表卡片 ──
                float listY = topY + 110;
                var listRect = new SKRect(200, listY, WIDTH - 20, HEIGHT - 20);
                canvas.DrawRoundRect(listRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(listRect, 6, 6, _cardBorder);
                canvas.DrawText($"已安装插件 ({_pluginView.Count})", 216, listY + 26, _uiTextPaint);
                // 「显示顺序」一览：← / → 调整的就是这张表里的位置。原生模块与插件同处一表，
                // 把它直接画出来，用户就不会再疑惑「岛上只看得见两个内容，插件为什么是 #4」。
                canvas.DrawText(TruncateText("顺序：" + DescribeContentOrder(), _subTextPaint, WIDTH - 36 - 216),
                    216, listY + 46, _subTextPaint);

                const int maxRows = 7;
                // 上行：名称独占整行，可延展至卡片右边界外侧
                // 下行：信息（左）+ 全部操作按钮（右，从左到右：← → 排序 | 重载 | 移除 | 开关）
                float nameTextMax = (WIDTH - 36) - 216;                // 名称几乎全宽
                float infoTextMax = PLUGIN_SORT_LEFT_X - 216 - 8;     // 信息止于排序三角之前

                if (_pluginView.Count == 0)
                    canvas.DrawText("暂无插件，点击「导入 DLL」或前往插件市场下载安装", 216, listY + 86, _subTextPaint);

                for (int i = 0; i < Math.Min(_pluginView.Count, maxRows); i++)
                {
                    var entry = _pluginView[i];
                    float rowY = listY + 64 + i * 56;      // 行高 56，上行名称独占，下行按钮全部一行排列
                    if (i > 0) canvas.DrawLine(216, rowY - 6, WIDTH - 36, rowY - 6, _separatorPaint);

                    // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                    canvas.DrawText(TruncateText(entry.FriendlyName, _uiTextPaint, nameTextMax), 216, rowY + 18, _uiTextPaint);

                    // ═══ 下行：信息 + 全部操作按钮（同一行从左到右排列） ═══
                    string sub;
                    SKColor subColor = new SKColor(170, 170, 170);
                    if (entry.State == PluginState.Failed)
                    {
                        sub = "加载失败：" + (entry.Error ?? "未知错误");
                        subColor = new SKColor(232, 100, 100);
                    }
                    else if (entry.State == PluginState.Loaded)
                    {
                        sub = string.IsNullOrEmpty(entry.Version) ? "运行中" : $"运行中 · v{entry.Version}";
                    }
                    else
                    {
                        sub = "已禁用 · " + entry.Key;
                    }
                    // 位置 = 在「内容显示顺序表」里的次序。这张表里同时住着三个原生模块
                    // （时间日期 / 硬件占用 / 媒体控制器），所以即便岛上当前只显示了两个内容，
                    // 插件也可能是 #4 —— 列表卡片顶部那行「顺序：…」把整张表摊开，一眼就能对上。
                    int pos = PluginManager.Instance.GetOrderIndex(entry);
                    int total = PluginManager.Instance.Order.Count;
                    if (pos > 0) sub += total > 0 ? $" · #{pos}/{total}" : $" · #{pos}";
                    _subTextPaint.Color = subColor;
                    float infoBaseline = rowY + 40;
                    canvas.DrawText(TruncateText(sub, _subTextPaint, infoTextMax), 216, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = new SKColor(170, 170, 170);

                    // ── 下行按钮（全部在同一行，y 中心 ≈ rowY+38） ──
                    const float btnTop = 25f, btnH = 20f;       // 操作按钮矩形（上移 2px，远离底部分割线）

                    // 排序箭头 < >（用 SKPath 描边绘制，相对下行按钮区垂直居中）
                    void DrawSortArrow(float bx, bool hovered, bool enabled, bool left)
                    {
                        using var stroke = new SKPaint
                        {
                            Color = !enabled ? new SKColor(130, 130, 130)
                                : hovered ? SKColors.White
                                : new SKColor(210, 210, 210),
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 1.6f,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                            IsAntialias = true
                        };
                        float cx = bx + SORT_TRI_W / 2f;   // 水平居中于 16px 槽
                        float cy = rowY + 35f;            // 相对下行按钮区（rowY+22..rowY+48）垂直居中
                        float s = 3f, h = 5f;
                        using var path = new SKPath();
                        if (left)
                        {
                            path.MoveTo(cx + s, cy - h);
                            path.LineTo(cx - s, cy);
                            path.LineTo(cx + s, cy + h);
                        }
                        else
                        {
                            path.MoveTo(cx - s, cy - h);
                            path.LineTo(cx + s, cy);
                            path.LineTo(cx - s, cy + h);
                        }
                        canvas.DrawPath(path, stroke);
                    }
                    DrawSortArrow(PLUGIN_SORT_LEFT_X, _hoveredPluginMoveLeft == i,
                        PluginManager.Instance.CanMoveOrder(entry, -1), true);
                    DrawSortArrow(PLUGIN_SORT_RIGHT_X, _hoveredPluginMoveRight == i,
                        PluginManager.Instance.CanMoveOrder(entry, 1), false);

                    // 操作按钮（重载 / 移除）+ 开关
                    void DrawRowButton(float bx, bool hovered, string label, bool danger)
                    {
                        var r = new SKRect(bx, rowY + btnTop, bx + 50, rowY + btnTop + btnH);
                        _dynamicFillPaint.Color = hovered
                            ? (danger ? new SKColor(180, 50, 50) : new SKColor(255, 255, 255, 30))
                            : new SKColor(255, 255, 255, 15);
                        canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                        float tw = _subTextPaint.MeasureText(label);
                        canvas.DrawText(label, bx + (50 - tw) / 2f, rowY + btnTop + 14, _uiTextPaint);
                    }
                    DrawRowButton(PLUGIN_BTN_RELOAD_X, _hoveredPluginReload == i, "重载", false);
                    DrawRowButton(PLUGIN_BTN_REMOVE_X, _hoveredPluginRemove == i, "移除", true);

                    float tW = 42, tH = 20;
                    float tX = PLUGIN_BTN_TOGGLE_X, tY = rowY + 26;
                    var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                    if (entry.IsEnabled)
                    {
                        _dynamicFillPaint.Color = _hoveredPluginToggle == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                        canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                        canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    }
                    else
                    {
                        _dynamicStrokePaint.Color = _hoveredPluginToggle == i ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                        canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                        _toggleCirclePaint.Color = _hoveredPluginToggle == i ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                        canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                        _toggleCirclePaint.Color = SKColors.White;
                    }
                }

                if (_pluginView.Count > maxRows)
                    canvas.DrawText($"还有 {_pluginView.Count - maxRows} 个插件未显示，可在“打开目录”中管理", 216, HEIGHT - 32, _subTextPaint);
            }

            canvas.Restore();

            if (_selectedTab == 2 && _dropdownOpen)
            {
                float mX = WIDTH - 140; float mY = TITLE_BAR_HEIGHT + 130; float mW = 110; float mH = _platforms.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);

                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                for (int i = 0; i < _platforms.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredDropdownIndex == i)
                    {
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    }
                    _dynamicTextPaint.Color = i == _selectedPlatformIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_platforms[i].Name, mX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            // 匹配方式下拉菜单（左框）
            if (_selectedTab == 2 && _matchModeDropdownOpen)
            {
                float mX = MATCH_MODE_X; float mY = MATCH_MENU_Y; float mW = MATCH_BOX_W; float mH = _matchModeOptions.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                int selectedMode = MediaController.IsManualSessionMatch ? 1 : 0;
                for (int i = 0; i < _matchModeOptions.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredMatchModeIndex == i)
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == selectedMode ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_matchModeOptions[i], mX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            // 手动选择软件下拉菜单（右框）：直接展示所有 SMTC 会话的 AppID
            if (_selectedTab == 2 && _appDropdownOpen)
            {
                int rows = Math.Clamp(_appOptions.Length, 1, 8);
                float mW = APP_MENU_W; float mX = MATCH_MENU_RIGHT - mW; float mY = MATCH_MENU_Y; float mH = rows * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                if (_appOptions.Length == 0)
                {
                    _dynamicTextPaint.Color = new SKColor(150, 150, 150);
                    canvas.DrawText("暂无活动会话", mX + 12, mY + 18, _dynamicTextPaint);
                    _dynamicTextPaint.Color = SKColors.White;
                }
                else
                {
                    for (int i = 0; i < rows; i++)
                    {
                        float itemY = mY + i * 26;
                        if (_hoveredAppIndex == i)
                            canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                        _dynamicTextPaint.Color = string.Equals(_appOptions[i], MediaController.ManualSessionAppId, StringComparison.OrdinalIgnoreCase)
                            ? new SKColor(0, 120, 212) : SKColors.White;
                        canvas.DrawText(TruncateText(_appOptions[i], _dynamicTextPaint, mW - 24), mX + 12, itemY + 18, _dynamicTextPaint);
                    }
                    _dynamicTextPaint.Color = SKColors.White;
                }
            }

            // 消息通知内容下拉菜单（通用设置）
            if (_selectedTab == 0 && _toastModeDropdownOpen)
            {
                float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 267; float dW = 110; float dH = _toastModeOptions.Length * 26;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);

                for (int i = 0; i < _toastModeOptions.Length; i++)
                {
                    float itemY = dY + i * 26;
                    if (_hoveredToastModeIndex == i)
                        canvas.DrawRoundRect(new SKRect(dX + 2, itemY + 2, dX + dW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == _selectedToastModeIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_toastModeOptions[i], dX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            // 目标显示器
            if (_selectedTab == 1 && _monitorDropdownOpen)
            {
                float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 220; float dW = 110; float dH = _monitorOptions.Length * 26;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);
                for (int i = 0; i < _monitorOptions.Length; i++)
                {
                    float itemY = dY + i * 26;
                    if (_hoveredMonitorDropdownIndex == i) canvas.DrawRoundRect(new SKRect(dX + 2, itemY + 2, dX + dW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == Renderer.TargetMonitorIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_monitorOptions[i], dX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f), cornerRadius, cornerRadius, _globalBorderPaint);
            UpdateLayeredContentWindow(surface.PeekPixels());
        }

        private unsafe void UpdateLayeredContentWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return;

            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            try
            {
                var bmi = new Win32.BITMAPINFO
                {
                    bmiHeader = new Win32.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                        biWidth = _scaledWidth,
                        biHeight = -_scaledHeight,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
                    return;

                IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);
                try
                {
                    long bytes = (long)_scaledWidth * _scaledHeight * 4;
                    Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

                    var ptSrc = new Win32.POINT(0, 0);
                    var ptDst = new Win32.POINT(0, 0);
                    Win32.GetWindowRect(_hwnd, out var rect);
                    ptDst.x = rect.Left;
                    ptDst.y = rect.Top;

                    var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
                    var blend = new Win32.BLENDFUNCTION
                    {
                        BlendOp = Win32.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = Win32.AC_SRC_ALPHA
                    };

                    Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
                }
                finally
                {
                    Win32.SelectObject(memDc, hOldBitmap);
                    Win32.DeleteObject(hBitmap);
                }
            }
            finally
            {
                Win32.DeleteDC(memDc);
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static void UpdateAutoStartState(bool enable)
        {
            if (_instance != null && _instance._isAutoStartEnabled != enable)
            {
                _instance._isAutoStartEnabled = enable;
                _instance.Render();
            }
        }
    }
}