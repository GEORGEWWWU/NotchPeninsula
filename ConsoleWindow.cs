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
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static bool _classRegistered = false;

        private const int WIDTH = 600;
        private const int HEIGHT = 660;
        private const int TITLE_BAR_HEIGHT = 32;

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
        // 交互设置状态
        private bool _autoHideToggleHovered = false;
        private bool _mediaExpToggleHovered = false;
        private bool _passToggleHovered = false;
        private bool _clipboardToggleHovered = false;

        // 媒体设置状态
        private bool _mediaToggleHovered = false;
        private bool _dropdownOpen = false;
        private bool _dropdownHovered = false;
        private int _hoveredDropdownIndex = -1;
        // 插件平台页垂直滚动偏移
        private float _pluginScrollY = 0f;
        // 关于页交互状态
        private int _hoveredLinkIndex = -1;
        private int _hoveredPluginSetting = -1;
        private readonly List<(ToggleSetting Setting, string PluginId, SKRect Rect)> _pluginToggles = new();
        private readonly List<(string Id, int Dir, SKRect Rect)> _widgetOrderButtons = new();
        private int _hoveredWidgetOrderBtn = -1;
        private readonly List<(string Id, SKRect Rect)> _widgetEnabledChecks = new();
        private int _hoveredWidgetEnabled = -1;
        private ICustomSettingsPage? _customSettingsPage;
        private SKRect _customSettingsRect;
        private readonly List<(string PluginId, ChoiceSetting Setting, SKRect Rect)> _pluginChoices = new();
        private int _openPluginChoice = -1;
        private int _hoveredPluginChoiceOpt = -1;
        private readonly List<(string PluginId, NumberSetting Setting, SKRect Minus, SKRect Plus)> _pluginNumbers = new();
        private int _hoveredPluginNumber = -1; // 0=减, 1=加
        private int _selectedPluginIndex = 0;
        private int _hoveredPluginIndex = -1;
        private readonly List<(int Index, SKRect Rect)> _pluginSelectorRects = new();

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
        private static readonly float[] _defaultCustomValues = [130f, 34f, 250f, 35f, 260f, 55f, 1.0f, 12f];
        private readonly string[] _valStrCache = new string[8];
        private int _hoveredThemeIndex = -1; // -1:无, 0:黑, 1:白, 2:系统
        private int _hoveredOpacityIndex = -1;
        // DPI 缩放相关
        private float _dpiScale = 1f;
        private int _scaledWidth;
        private int _scaledHeight;
        // 预设媒体平台数组
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
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(_instance._hwnd);
            }
        }

        private ConsoleWindow()
        {
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
            _customValues[0] = Renderer.STANDBY_WIDTH;
            _customValues[1] = Renderer.BASE_HEIGHT;
            _customValues[2] = Renderer.MEDIA_WIDTH;
            _customValues[3] = Renderer.MEDIA_HEIGHT;
            _customValues[4] = Renderer.TOAST_WIDTH;
            _customValues[5] = Renderer.TOAST_HEIGHT;
            _customValues[6] = Renderer.GLOBAL_DPI;
            _customValues[7] = Renderer.NOTCH_BOTTOM_RADIUS;

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

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED,
                "NotchConsoleClass", "NotchPeninsula",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                (screenWidth - _scaledWidth) / 2, (screenHeight - _scaledHeight) / 2, // 使用物理尺寸居中
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero, IntPtr.Zero
            );

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

        private void UpdateValueString(int index)
        {
            _valStrCache[index] = index == 6 ? $"{_customValues[index]:F2} x" : $"{(int)_customValues[index]} px";
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instance != null && hwnd == _instance._hwnd)
                return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
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
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 140 && y <= TITLE_BAR_HEIGHT + 176) newHoveredTab = 3; // 4. 交互设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 190 && y <= TITLE_BAR_HEIGHT + 226) newHoveredTab = 4; // 5. 关于软件
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 230 && y <= TITLE_BAR_HEIGHT + 266) newHoveredTab = 6; // 6. 插件

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
                    bool newDropdownHovered = false;
                    int newHoveredDropdownIndex = -1;
                    bool newMediaExpToggleHovered = false;
                    bool newPassToggleHovered = false;
                    bool newClipboardToggleHovered = false;
                    bool newMonitorDropdownHovered = false;
                    int newHoveredMonitorDropdownIndex = -1;
                    bool newCompositeToggleHover = false;
                    bool newCompDateTimeHover = false;
                    bool newCompHardwareHover = false;
                    bool newCompMediaHover = false;

                    if (_selectedTab == 0) // 通用设置
                    {
                        // 开机自启
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newToggleHovered = true;
                        // 系统消息通知开关
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                            newToastToggleHovered = true;
                        // 窗口置顶开关
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 176 && y <= TITLE_BAR_HEIGHT + 196)
                            newTopmostToggleHovered = true;
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
                    else if (_selectedTab == 3) // 交互设置
                    {
                        // 自动隐藏
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newAutoHideToggleHovered = true;
                        // 媒体交互模式
                        if (!Renderer.CompositeModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                            newMediaExpToggleHovered = true;
                        // 使用局部变量，防止状态死锁
                        newPassToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 176 && y <= TITLE_BAR_HEIGHT + 196;
                        // 剪贴板链接识别
                        newClipboardToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 248 && y <= TITLE_BAR_HEIGHT + 268;
                    }

                    int newHoveredLinkIndex = -1;
                    int newHoveredPluginSetting = -1;
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

                    if (_selectedTab == 6)
                    {
                        for (int i = 0; i < _pluginToggles.Count; i++)
                        {
                            if (_pluginToggles[i].Rect.Contains(x, y)) { newHoveredPluginSetting = i; break; }
                        }
                        int newOrderBtn = -1;
                        for (int i = 0; i < _widgetOrderButtons.Count; i++)
                        {
                            if (_widgetOrderButtons[i].Rect.Contains(x, y)) { newOrderBtn = i; break; }
                        }
                        if (newOrderBtn != _hoveredWidgetOrderBtn)
                        {
                            _hoveredWidgetOrderBtn = newOrderBtn;
                            Render();
                        }
                        int newCheck = -1;
                        for (int i = 0; i < _widgetEnabledChecks.Count; i++)
                        {
                            if (_widgetEnabledChecks[i].Rect.Contains(x, y)) { newCheck = i; break; }
                        }
                        if (newCheck != _hoveredWidgetEnabled)
                        {
                            _hoveredWidgetEnabled = newCheck;
                            Render();
                        }
                        // 下拉选项悬停
                        int newChoiceOpt = -1;
                        if (_openPluginChoice != -1)
                        {
                            var (cpid, cchoice, crect) = _pluginChoices[_openPluginChoice];
                            for (int o = 0; o < cchoice.Options.Length; o++)
                            {
                                var optRect = new SKRect(crect.Left, crect.Bottom + o * 26, crect.Right, crect.Bottom + (o + 1) * 26);
                                if (optRect.Contains(x, y)) { newChoiceOpt = o; break; }
                            }
                        }
                        if (newChoiceOpt != _hoveredPluginChoiceOpt)
                        {
                            _hoveredPluginChoiceOpt = newChoiceOpt;
                            Render();
                        }
                        // 步进器按钮悬停
                        int newNumBtn = -1;
                        for (int i = 0; i < _pluginNumbers.Count; i++)
                        {
                            if (_pluginNumbers[i].Minus.Contains(x, y)) { newNumBtn = 0; break; }
                            if (_pluginNumbers[i].Plus.Contains(x, y)) { newNumBtn = 1; break; }
                        }
                        if (newNumBtn != _hoveredPluginNumber)
                        {
                            _hoveredPluginNumber = newNumBtn;
                            Render();
                        }
                        // 插件选择器悬停
                        int newPluginIndex = -1;
                        for (int i = 0; i < _pluginSelectorRects.Count; i++)
                        {
                            if (_pluginSelectorRects[i].Rect.Contains(x, y)) { newPluginIndex = i; break; }
                        }
                        if (newPluginIndex != _hoveredPluginIndex)
                        {
                            _hoveredPluginIndex = newPluginIndex;
                            Render();
                        }
                        // 自定义设置页鼠标移动
                        if (_customSettingsPage != null && _customSettingsRect.Contains(x, y))
                        {
                            _customSettingsPage.OnMouseMove(x - _customSettingsRect.Left, y - _customSettingsRect.Top);
                        }
                    }

                    bool newIsHoveringDisabledArea = false;
                    // 当处于“显示设置(1)”或“交互设置(3)”且开启了组合模式时，拦截特定卡片区域的指针
                    if (_selectedTab == 1 && Renderer.CompositeModeEnabled && x >= 200 && x <= WIDTH - 20 && y >= TITLE_BAR_HEIGHT + 248 && y <= TITLE_BAR_HEIGHT + 360)
                        newIsHoveringDisabledArea = true;
                    else if (_selectedTab == 3 && Renderer.CompositeModeEnabled && x >= 200 && x <= WIDTH - 20 && y >= TITLE_BAR_HEIGHT + 84 && y <= TITLE_BAR_HEIGHT + 146)
                        newIsHoveringDisabledArea = true;

                    if (newIsHoveringDisabledArea != _isHoveringDisabledArea) _isHoveringDisabledArea = newIsHoveringDisabledArea;

                    if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                        newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered ||
                        newToastToggleHovered != _toastToggleHovered || newTopmostToggleHovered != _topmostToggleHovered ||
                        newMediaToggleHovered != _mediaToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
                        newDropdownHovered != _dropdownHovered ||
                        newHoveredDropdownIndex != _hoveredDropdownIndex || newHoveredLinkIndex != _hoveredLinkIndex ||
                        newHoveredDisplayOptionIndex != _hoveredDisplayOptionIndex ||
                        newHoveredStyleIndex != _hoveredStyleIndex ||
                        newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                        newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
                        newHoveredTheme != _hoveredThemeIndex || newHoveredOpacityIndex != _hoveredOpacityIndex ||
                        newMonitorDropdownHovered != _monitorDropdownHovered ||
                        newHoveredMonitorDropdownIndex != _hoveredMonitorDropdownIndex ||
                        newCompositeToggleHover != _compositeToggleHovered ||
                        newCompDateTimeHover != _compDateTimeHovered ||
                        newCompHardwareHover != _compHardwareHovered ||
                        newCompMediaHover != _compMediaHovered || newPassToggleHovered != _passToggleHovered ||
                        newHoveredPluginSetting != _hoveredPluginSetting ||
                        newClipboardToggleHovered != _clipboardToggleHovered
                        )
                    {
                        _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                        _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                        _toastToggleHovered = newToastToggleHovered;
                        _mediaToggleHovered = newMediaToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
                        _dropdownHovered = newDropdownHovered;
                        _hoveredDropdownIndex = newHoveredDropdownIndex;
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
                        _compositeToggleHovered = newCompositeToggleHover;
                        _compDateTimeHovered = newCompDateTimeHover;
                        _compHardwareHovered = newCompHardwareHover;
                        _compMediaHovered = newCompMediaHover;
                        _topmostToggleHovered = newTopmostToggleHovered;
                        _passToggleHovered = newPassToggleHovered;
                        _hoveredPluginSetting = newHoveredPluginSetting;
                        _clipboardToggleHovered = newClipboardToggleHovered;
                        Render();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
                    int clickX = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                    int clickY = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                    if (_closeHovered) Win32.DestroyWindow(hwnd);
                    else if (_minHovered) Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
                    else if (clickY <= TITLE_BAR_HEIGHT)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
                    }
                    else if (_dropdownOpen && _hoveredDropdownIndex == -1)
                    {
                        _dropdownOpen = false; Render(); // 点击菜单外部收起浮窗
                    }
                    else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex == -1) { _monitorDropdownOpen = false; Render(); }
                    else if (_selectedTab == 1 && _hoveredStyleIndex != -1)
                    {
                        Renderer.NotchStyle = _hoveredStyleIndex;
                        Program.SaveSetting("NotchStyle", _hoveredStyleIndex);
                        Render();
                    }
                    else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 5 && _selectedTab != 5) { _selectedTab = 5; _dropdownOpen = false; Render(); }
                    else if (_hoveredTab == 6 && _selectedTab != 6) { _selectedTab = 6; _dropdownOpen = false; Render(); }
                    else if (_selectedTab == 6 && _hoveredPluginIndex != -1)
                    {
                        _selectedPluginIndex = _hoveredPluginIndex;
                        Render();
                    }
                    else if (_selectedTab == 6 && _customSettingsPage != null && _customSettingsRect.Contains(clickX, clickY))
                    {
                        _customSettingsPage.OnMouseDown(clickX - _customSettingsRect.Left, clickY - _customSettingsRect.Top);
                        Render();
                    }
                    else if (_selectedTab == 6 && _openPluginChoice != -1 && _hoveredPluginChoiceOpt != -1)
                    {
                        var (cpid, cchoice, _) = _pluginChoices[_openPluginChoice];
                        NotchWindow.PluginHostInstance.SetSetting(cpid, cchoice.Key, _hoveredPluginChoiceOpt.ToString());
                        _openPluginChoice = -1;
                        _hoveredPluginChoiceOpt = -1;
                        Render();
                    }
                    else if (_selectedTab == 6 && _hoveredPluginNumber != -1)
                    {
                        for (int i = 0; i < _pluginNumbers.Count; i++)
                        {
                            var (npid, nnum, nminus, nplus) = _pluginNumbers[i];
                            if (_hoveredPluginNumber == 0 && nminus.Contains(clickX, clickY))
                            {
                                float v = float.TryParse(NotchWindow.PluginHostInstance.GetSetting(npid, nnum.Key, nnum.Default.ToString()), out var nv) ? nv : nnum.Default;
                                NotchWindow.PluginHostInstance.SetSetting(npid, nnum.Key, Math.Max(nnum.Min, v - nnum.Step).ToString());
                                Render();
                                break;
                            }
                            if (_hoveredPluginNumber == 1 && nplus.Contains(clickX, clickY))
                            {
                                float v = float.TryParse(NotchWindow.PluginHostInstance.GetSetting(npid, nnum.Key, nnum.Default.ToString()), out var nv) ? nv : nnum.Default;
                                NotchWindow.PluginHostInstance.SetSetting(npid, nnum.Key, Math.Min(nnum.Max, v + nnum.Step).ToString());
                                Render();
                                break;
                            }
                        }
                    }
                    else if (_selectedTab == 6 && _pluginChoices.Any(p => p.Rect.Contains(clickX, clickY)))
                    {
                        for (int i = 0; i < _pluginChoices.Count; i++)
                        {
                            if (_pluginChoices[i].Rect.Contains(clickX, clickY))
                            {
                                _openPluginChoice = (_openPluginChoice == i) ? -1 : i;
                                _hoveredPluginChoiceOpt = -1;
                                Render();
                                break;
                            }
                        }
                    }
                    else if (_selectedTab == 6 && _hoveredPluginSetting != -1)
                    {
                        var (ptoggle, pplugin, _) = _pluginToggles[_hoveredPluginSetting];
                        bool pcurrent = NotchWindow.PluginHostInstance.GetSetting(pplugin, ptoggle.Key, ptoggle.DefaultValue ? "1" : "0") == "1";
                        NotchWindow.PluginHostInstance.SetSetting(pplugin, ptoggle.Key, pcurrent ? "0" : "1");
                        Render();
                    }
                    else if (_selectedTab == 6 && _hoveredWidgetOrderBtn != -1)
                    {
                        var (oid, odir, _) = _widgetOrderButtons[_hoveredWidgetOrderBtn];
                        NotchWindow.MoveWidget(oid, odir);
                        Render();
                    }
                    else if (_selectedTab == 6 && _hoveredWidgetEnabled != -1)
                    {
                        var (wid, _) = _widgetEnabledChecks[_hoveredWidgetEnabled];
                        NotchWindow.ToggleWidgetEnabled(wid);
                        Render();
                    }
                    else if (_monitorDropdownHovered) { _monitorDropdownOpen = true; Render(); }
                    else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex != -1)
                    {
                        Renderer.TargetMonitorIndex = _hoveredMonitorDropdownIndex;
                        Program.SaveSetting("TargetMonitorIndex", Renderer.TargetMonitorIndex);
                        _monitorDropdownOpen = false;
                        Render();
                    }
                    else if (_selectedTab == 5 && (_hoveredMinusIndex != -1 || _hoveredPlusIndex != -1 || _hoveredResetIndex != -1))
                    {
                        int updateIdx;
                        if (_hoveredResetIndex != -1)
                        {
                            updateIdx = _hoveredResetIndex;
                            float[] defaultVals = { 130f, 34f, 250f, 35f, 260f, 55f, 1.0f, 12f };
                            _customValues[updateIdx] = defaultVals[updateIdx];

                            // 重置时如果处于硬件监控，拦截至最小限制
                            if (Renderer.StandbyDisplayMode == 2)
                            {
                                if (updateIdx == 0 && _customValues[0] < 170f) _customValues[0] = 170f;
                                if (updateIdx == 1 && _customValues[1] < 34f) _customValues[1] = 34f;
                            }

                            // 重置待机宽度时，同步更新快照，防止切回时恢复到旧值
                            if (updateIdx == 0)
                                _savedStandbyWidth = -1f; // 清除快照，切回时用默认130
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
                    else if (_autoHideToggleHovered)
                    {
                        NotchWindow.IsAutoHideEnabled = !NotchWindow.IsAutoHideEnabled;
                        // 保存自动隐藏开关
                        Program.SaveSetting("AutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0);

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
                        NotchWindow.IsClipboardLinkEnabled = !NotchWindow.IsClipboardLinkEnabled;
                        Program.SaveSetting("ClipboardLinkEnabled", NotchWindow.IsClipboardLinkEnabled ? 1 : 0);
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
                            float restoreWidth = _savedStandbyWidth > 0f ? _savedStandbyWidth : 130f;
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
                    break;

                case Win32.WM_MOUSEWHEEL:
                    if (_selectedTab == 6)
                    {
                        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        _pluginScrollY = Math.Clamp(_pluginScrollY - delta / 120f * 40f, 0f, PluginContentScrollMax());
                        Render();
                    }
                    break;

                case Win32.WM_DESTROY:
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

        // 插件平台页内容可滚动的最大偏移（组件顺序 + 选择器 + 选中页控件）
        private float PluginContentScrollMax()
        {
            int widgets = NotchWindow.GetAllWidgetsInOrder().Count;
            float content = TITLE_BAR_HEIGHT + 46 + widgets * 40 + 38;
            var pages = NotchWindow.PluginHostInstance.SettingsPages;
            if (pages.Count > 0)
            {
                int idx = Math.Clamp(_selectedPluginIndex, 0, pages.Count - 1);
                content += pages[idx].Page.Controls.Count * 74f;
                if (pages[idx].Page is ICustomSettingsPage custom)
                    content += custom.MeasureHeight() + 12f;
            }
            return Math.Max(0f, content - (HEIGHT - TITLE_BAR_HEIGHT));
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

            // 标题栏区
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
            DrawTab(3, "交互设置", 140);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 182, 160, TITLE_BAR_HEIGHT + 182, _separatorPaint);
            DrawTab(4, "关于软件", 190);
            DrawTab(6, "插件平台", 230);

            // 右侧卡片内容区
            void DrawToggleCard(float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
            {
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + 62);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

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
                DrawToggleCard(84, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);
                DrawToggleCard(156, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
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
            else if (_selectedTab == 3)
            {
                DrawToggleCard(12, "自动隐藏", "当鼠标离开时自动隐藏刘海", NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered);

                bool isMediaExpDisabled = Renderer.CompositeModeEnabled;
                DrawToggleCard(84, "媒体交互方式", isMediaExpDisabled ? "组合模式下固定为直接交互" : "开启为展开交互，关闭为直接交互",
                    isMediaExpDisabled ? false : (Renderer.MediaInteractionMode == 1),
                    !isMediaExpDisabled && _mediaExpToggleHovered,
                    isMediaExpDisabled);

                DrawToggleCard(156, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered);

                DrawToggleCard(228, "剪贴板链接识别", "复制链接后在刘海中显示，可一键打开", NotchWindow.IsClipboardLinkEnabled, _clipboardToggleHovered);
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
            else if (_selectedTab == 6)
            {
                // ---- 组件顺序 ----
                _widgetOrderButtons.Clear();
                _widgetEnabledChecks.Clear();
                canvas.Save();
                canvas.ClipRect(new SKRect(200, TITLE_BAR_HEIGHT, WIDTH, HEIGHT));
                canvas.DrawText("组件顺序", 216, TITLE_BAR_HEIGHT + 28 - _pluginScrollY, _uiTextPaint);
                float wy = TITLE_BAR_HEIGHT + 46 - _pluginScrollY;
                var row = NotchWindow.GetAllWidgetsInOrder();
                for (int i = 0; i < row.Count; i++)
                {
                    var w = row[i];
                    bool enabled = !NotchWindow.IsWidgetDisabled(w.Id);
                    canvas.DrawRoundRect(new SKRect(200, wy, WIDTH - 20, wy + 32), 4, 4, _cardBg);

                    // 启用开关（小方块 + 对勾）
                    var checkRect = new SKRect(212, wy + 8, 228, wy + 24);
                    _widgetEnabledChecks.Add((w.Id, checkRect));
                    _dynamicFillPaint.Color = enabled ? new SKColor(0, 140, 240) : new SKColor(255, 255, 255, 10);
                    canvas.DrawRoundRect(checkRect, 3, 3, _dynamicFillPaint);
                    if (enabled)
                    {
                        canvas.DrawLine(215, wy + 16, 220, wy + 21, _uiTextPaint);
                        canvas.DrawLine(220, wy + 21, 227, wy + 12, _uiTextPaint);
                    }

                    _uiTextPaint.Color = enabled ? SKColors.White : new SKColor(120, 120, 120);
                    canvas.DrawText(w.DisplayName, 240, wy + 21, _uiTextPaint);
                    _uiTextPaint.Color = SKColors.White;

                    var upRect = new SKRect(WIDTH - 88, wy + 6, WIDTH - 60, wy + 26);
                    var downRect = new SKRect(WIDTH - 56, wy + 6, WIDTH - 28, wy + 26);
                    _widgetOrderButtons.Add((w.Id, -1, upRect));
                    _widgetOrderButtons.Add((w.Id, 1, downRect));
                    int upIdx = _widgetOrderButtons.Count - 2;
                    int downIdx = _widgetOrderButtons.Count - 1;

                    _dynamicFillPaint.Color = _hoveredWidgetOrderBtn == upIdx ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 8);
                    canvas.DrawRoundRect(upRect, 4, 4, _dynamicFillPaint);
                    _dynamicFillPaint.Color = _hoveredWidgetOrderBtn == downIdx ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 8);
                    canvas.DrawRoundRect(downRect, 4, 4, _dynamicFillPaint);
                    canvas.DrawText("▲", upRect.Left + 5, upRect.Top + 16, _uiTextPaint);
                    canvas.DrawText("▼", downRect.Left + 5, downRect.Top + 16, _uiTextPaint);

                    wy += 40;
                }

                // ---- 插件选择器 + 选中插件设置 ----
                _pluginToggles.Clear();
                _pluginChoices.Clear();
                _pluginNumbers.Clear();
                _customSettingsPage = null;
                _pluginSelectorRects.Clear();
                var pages = NotchWindow.PluginHostInstance.SettingsPages;
                if (_selectedPluginIndex >= pages.Count) _selectedPluginIndex = 0;
                float selY = wy + 6f;
                float selX = 200f;
                for (int i = 0; i < pages.Count; i++)
                {
                    float tw = _uiTextPaint.MeasureText(pages[i].Page.Title) + 24f;
                    var selRect = new SKRect(selX, selY, selX + tw, selY + 30);
                    _pluginSelectorRects.Add((i, selRect));
                    _dynamicFillPaint.Color = _selectedPluginIndex == i ? new SKColor(0, 120, 212, 50) : new SKColor(255, 255, 255, 8);
                    canvas.DrawRoundRect(selRect, 6, 6, _dynamicFillPaint);
                    _uiTextPaint.Color = _selectedPluginIndex == i ? new SKColor(0, 140, 240) : SKColors.White;
                    canvas.DrawText(pages[i].Page.Title, selX + 12, selY + 20, _uiTextPaint);
                    selX += tw + 8f;
                }
                _uiTextPaint.Color = SKColors.White; // 重置，避免后续文字继承选中蓝色

                float ty = selY + 38f - TITLE_BAR_HEIGHT;
                if (pages.Count > 0)
                {
                    var (pluginId, page) = pages[_selectedPluginIndex];
                    foreach (var control in page.Controls)
                    {
                        switch (control)
                        {
                            case ToggleSetting toggle:
                            {
                                bool state = NotchWindow.PluginHostInstance.GetSetting(pluginId, toggle.Key, toggle.DefaultValue ? "1" : "0") == "1";
                                int idx = _pluginToggles.Count;
                                DrawToggleCard(ty, toggle.Label, page.Title, state, _hoveredPluginSetting == idx);
                                _pluginToggles.Add((toggle, pluginId, new SKRect(200, TITLE_BAR_HEIGHT + ty, WIDTH - 20, TITLE_BAR_HEIGHT + ty + 62)));
                                ty += 74f;
                                break;
                            }
                            case ChoiceSetting choice:
                            {
                                int cur = int.TryParse(NotchWindow.PluginHostInstance.GetSetting(pluginId, choice.Key, choice.DefaultIndex.ToString()), out var ci) ? ci : choice.DefaultIndex;
                                int idx = _pluginChoices.Count;
                                var rect = new SKRect(200, TITLE_BAR_HEIGHT + ty, WIDTH - 20, TITLE_BAR_HEIGHT + ty + 62);
                                _pluginChoices.Add((pluginId, choice, rect));
                                canvas.DrawRoundRect(rect, 6, 6, _cardBg);
                                canvas.DrawRoundRect(rect, 6, 6, _cardBorder);
                                canvas.DrawText(choice.Label, 216, TITLE_BAR_HEIGHT + ty + 24, _uiTextPaint);
                                string curLabel = (cur >= 0 && cur < choice.Options.Length) ? choice.Options[cur] : "?";
                                canvas.DrawText(curLabel, WIDTH - 160, TITLE_BAR_HEIGHT + ty + 24, _uiTextPaint);
                                canvas.DrawLine(WIDTH - 30, TITLE_BAR_HEIGHT + ty + 24, WIDTH - 22, TITLE_BAR_HEIGHT + ty + 30, _chevronPaint);
                                canvas.DrawLine(WIDTH - 22, TITLE_BAR_HEIGHT + ty + 30, WIDTH - 14, TITLE_BAR_HEIGHT + ty + 24, _chevronPaint);
                                ty += 74f;
                                break;
                            }
                            case NumberSetting number:
                            {
                                float val = float.TryParse(NotchWindow.PluginHostInstance.GetSetting(pluginId, number.Key, number.Default.ToString()), out var nv) ? nv : number.Default;
                                int nidx = _pluginNumbers.Count;
                                var minus = new SKRect(WIDTH - 150, TITLE_BAR_HEIGHT + ty + 14, WIDTH - 120, TITLE_BAR_HEIGHT + ty + 40);
                                var plus = new SKRect(WIDTH - 88, TITLE_BAR_HEIGHT + ty + 14, WIDTH - 58, TITLE_BAR_HEIGHT + ty + 40);
                                _pluginNumbers.Add((pluginId, number, minus, plus));
                                var rect = new SKRect(200, TITLE_BAR_HEIGHT + ty, WIDTH - 20, TITLE_BAR_HEIGHT + ty + 62);
                                canvas.DrawRoundRect(rect, 6, 6, _cardBg);
                                canvas.DrawRoundRect(rect, 6, 6, _cardBorder);
                                canvas.DrawText(number.Label, 216, TITLE_BAR_HEIGHT + ty + 24, _uiTextPaint);
                                _dynamicFillPaint.Color = _hoveredPluginNumber == 0 ? new SKColor(255,255,255,25) : new SKColor(255,255,255,8);
                                canvas.DrawRoundRect(minus, 4, 4, _dynamicFillPaint);
                                _dynamicFillPaint.Color = _hoveredPluginNumber == 1 ? new SKColor(255,255,255,25) : new SKColor(255,255,255,8);
                                canvas.DrawRoundRect(plus, 4, 4, _dynamicFillPaint);
                                canvas.DrawText("−", minus.Left + 7, minus.Top + 19, _uiTextPaint);
                                canvas.DrawText("+", plus.Left + 7, plus.Top + 19, _uiTextPaint);
                                canvas.DrawText(val.ToString("0.#"), WIDTH - 118, TITLE_BAR_HEIGHT + ty + 30, _uiTextPaint);
                                ty += 74f;
                                break;
                            }
                        }
                    }

                    // 自定义 UI
                    if (page is ICustomSettingsPage custom)
                    {
                        float ch = custom.MeasureHeight();
                        var crect = new SKRect(200, TITLE_BAR_HEIGHT + ty, WIDTH - 20, TITLE_BAR_HEIGHT + ty + ch);
                        canvas.Save();
                        canvas.ClipRect(crect);
                        custom.Draw(canvas, crect, Renderer.GetCurrentTheme());
                        canvas.Restore();
                        _customSettingsPage = custom;
                        _customSettingsRect = crect;
                        ty += ch + 12f;
                    }

                    // 下拉选项（绘制在所有控件之上，避免被后续控件遮挡）
                    if (_openPluginChoice != -1 && _openPluginChoice < _pluginChoices.Count)
                    {
                        var (cpid, cchoice, crect) = _pluginChoices[_openPluginChoice];
                        float optY = crect.Bottom;
                        for (int o = 0; o < cchoice.Options.Length; o++)
                        {
                            var optRect = new SKRect(crect.Left, optY, crect.Right, optY + 26);
                            _dynamicFillPaint.Color = _hoveredPluginChoiceOpt == o ? new SKColor(255, 255, 255, 20) : new SKColor(40, 40, 40);
                            canvas.DrawRect(optRect, _dynamicFillPaint);
                            canvas.DrawText(cchoice.Options[o], crect.Left + 16, optY + 18, _uiTextPaint);
                            optY += 26;
                        }
                    }
                }
                else
                {
                    canvas.DrawText("暂无插件", 216, selY + 20, _subTextPaint);
                }
                canvas.Restore();
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

            canvas.Restore();

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
            UpdateWindow(surface.PeekPixels());
        }

        private unsafe void UpdateWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
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
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);

            long bytes = (long)_scaledWidth * _scaledHeight * 4;
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT(0, 0);
            Win32.GetWindowRect(_hwnd, out var rect);
            ptDst.x = rect.Left;
            ptDst.y = rect.Top;

            var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
            var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };

            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            Win32.SelectObject(memDc, hOldBitmap);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(memDc);
            _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
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