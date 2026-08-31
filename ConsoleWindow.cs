using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;

namespace NotchPeninsula
{
    public class ConsoleWindow
    {
        private static ConsoleWindow? _instance;
        private readonly IntPtr _hwnd;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static bool _classRegistered = false;

        private const int WIDTH = 600;
        private const int HEIGHT = 600;
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
        // 交互设置状态
        private bool _autoHideToggleHovered = false;

        // 媒体设置状态
        private bool _mediaToggleHovered = false;
        private bool _dropdownOpen = false;
        private bool _dropdownHovered = false;
        private int _hoveredDropdownIndex = -1;
        private int _selectedPlatformIndex = 0;
        // 关于页交互状态
        private int _hoveredLinkIndex = -1;

        // 显示设置状态
        private bool _displayDropdownOpen = false;
        private bool _displayDropdownHovered = false;
        private int _hoveredDisplayDropdownIndex = -1;
        private int _selectedDisplayIndex = 0;
        private static readonly string[] _displayOptions = ["时间日期"];
        private int _hoveredStyleIndex = -1;
        // 个性化中心状态
        private int _hoveredMinusIndex = -1;
        private int _hoveredPlusIndex = -1;
        private int _hoveredResetIndex = -1;
        private float[] _customValues = new float[7];
        private static readonly float[] _defaultCustomValues = [130f, 34f, 260f, 34f, 260f, 55f, 1.0f];
        private readonly string[] _valStrCache = new string[7];
        private int _hoveredThemeIndex = -1; // -1:无, 0:黑, 1:白, 2:系统
        // DPI 缩放相关
        private float _dpiScale = 1f;
        private int _scaledWidth;
        private int _scaledHeight;
        // 预设媒体平台数组
        private static readonly (string Id, string Name)[] _platforms = [
            ("other", "通用媒体"),
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

        // 动态状态画笔 (专门用于需要根据 Hover 状态变色的元素)
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

            // 匹配目前加载的媒体平台索引
            for (int i = 0; i < _platforms.Length; i++)
            {
                if (_platforms[i].Id == MediaController.TargetPlatform)
                {
                    _selectedPlatformIndex = i; break;
                }
            }

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
                    hInstance = Marshal.GetHINSTANCE(typeof(ConsoleWindow).Module),
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
                IntPtr.Zero, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(ConsoleWindow).Module), IntPtr.Zero
            );

            for (int i = 0; i < 7; i++)
            {
                UpdateValueString(i);
            }

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

                    // Tab Hover 判定（匹配新的视觉排版位置与分割线）
                    int newHoveredTab = -1;
                    if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 10 && y <= TITLE_BAR_HEIGHT + 46) newHoveredTab = 5;      // 1. 个性化中心
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 60 && y <= TITLE_BAR_HEIGHT + 96) newHoveredTab = 0; // 2. 通用设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 100 && y <= TITLE_BAR_HEIGHT + 136) newHoveredTab = 1; // 3. 显示设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 140 && y <= TITLE_BAR_HEIGHT + 176) newHoveredTab = 2; // 4. 媒体设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 180 && y <= TITLE_BAR_HEIGHT + 216) newHoveredTab = 3; // 5. 交互设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 230 && y <= TITLE_BAR_HEIGHT + 266) newHoveredTab = 4; // 6. 关于软件

                    int newHoveredTheme = -1;
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

                        for (int i = 0; i < 7; i++)
                        {
                            float btnY = GetBtnY(i);
                            float rightX = WIDTH - 36; // 保持原有变量不动
                            if (x >= rightX - 175 && x <= rightX - 145 && y >= btnY && y <= btnY + 24) newHoverMinus = i;
                            if (x >= rightX - 80 && x <= rightX - 50 && y >= btnY && y <= btnY + 24) newHoverPlus = i;
                            if (x >= rightX - 40 && x <= rightX && y >= btnY && y <= btnY + 24) newHoverReset = i;
                        }
                    }

                    bool newDisplayDropdownHovered = false;
                    int newHoveredDisplayDropdownIndex = -1;
                    int newHoveredStyleIndex = -1;
                    bool newToggleHovered = false;
                    bool newToastToggleHovered = false;
                    bool newMediaToggleHovered = false;
                    bool newAutoHideToggleHovered = false;
                    bool newDropdownHovered = false;
                    int newHoveredDropdownIndex = -1;

                    if (_selectedTab == 0) // 通用设置
                    {
                        // 开机自启
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newToggleHovered = true;
                        // 系统消息通知开关
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                            newToastToggleHovered = true;
                    }
                    else if (_selectedTab == 1) // 显示设置
                    {
                        // 刘海形态选择器的点击热区
                        float styleY = TITLE_BAR_HEIGHT + 50;
                        if (x >= 220 && x <= 370 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 0;
                        if (x >= 390 && x <= 540 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 1;

                        // 下拉菜单判定 (原卡片整体下移避让)
                        float dY = TITLE_BAR_HEIGHT + 186; // 172 + 14
                        if (!_displayDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= dY && y <= dY + 32)
                            newDisplayDropdownHovered = true;

                        if (_displayDropdownOpen)
                        {
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= dY + 34 && y < dY + 34 + _displayOptions.Length * 26)
                                newHoveredDisplayDropdownIndex = (y - (int)(dY + 34)) / 26;
                        }
                    }
                    else if (_selectedTab == 2) // 媒体设置
                    {
                        // 媒体控制
                        if (!_dropdownOpen && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newMediaToggleHovered = true;

                        // 下拉菜单
                        if (!_dropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 98 && y <= TITLE_BAR_HEIGHT + 128)
                            newDropdownHovered = true;

                        if (_dropdownOpen)
                        {
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 130 && y < TITLE_BAR_HEIGHT + 130 + _platforms.Length * 26)
                                newHoveredDropdownIndex = (y - (TITLE_BAR_HEIGHT + 130)) / 26;
                        }
                    }
                    else if (_selectedTab == 3) // 交互设置
                    {
                        // 自动隐藏
                        if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                            newAutoHideToggleHovered = true;
                    }

                    int newHoveredLinkIndex = -1;
                    if (_selectedTab == 4) // 关于页
                    {
                        // 根据 Render 中的排版高度叠加，文字基线实际在 TITLE_BAR_HEIGHT + 177 左右
                        int yStart = TITLE_BAR_HEIGHT + 160;
                        int yEnd = TITLE_BAR_HEIGHT + 190;

                        if (y >= yStart && y <= yEnd)
                        {
                            // 修正后的 X 轴热区：基于动态居中的实际渲染宽度重新测量计算，并适当加宽容错
                            if (x >= 305 && x <= 370) newHoveredLinkIndex = 0;      // 检测更新
                            else if (x >= 375 && x <= 440) newHoveredLinkIndex = 1; // 仓库地址
                            else if (x >= 445 && x <= 500) newHoveredLinkIndex = 2; // 开发者 (Ryen)
                        }
                    }

                    if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                        newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered ||
                        newToastToggleHovered != _toastToggleHovered ||
                        newMediaToggleHovered != _mediaToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
                        newDropdownHovered != _dropdownHovered ||
                        newHoveredDropdownIndex != _hoveredDropdownIndex || newHoveredLinkIndex != _hoveredLinkIndex ||
                        newDisplayDropdownHovered != _displayDropdownHovered ||
                        newHoveredDisplayDropdownIndex != _hoveredDisplayDropdownIndex ||
                        newHoveredStyleIndex != _hoveredStyleIndex ||
                        newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                        newHoverReset != _hoveredResetIndex ||
                        newHoveredTheme != _hoveredThemeIndex)
                    {
                        _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                        _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                        _toastToggleHovered = newToastToggleHovered;
                        _mediaToggleHovered = newMediaToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
                        _dropdownHovered = newDropdownHovered;
                        _hoveredDropdownIndex = newHoveredDropdownIndex;
                        _hoveredLinkIndex = newHoveredLinkIndex;
                        _displayDropdownHovered = newDisplayDropdownHovered;
                        _hoveredDisplayDropdownIndex = newHoveredDisplayDropdownIndex;
                        _hoveredStyleIndex = newHoveredStyleIndex;
                        _hoveredMinusIndex = newHoverMinus;
                        _hoveredPlusIndex = newHoverPlus;
                        _hoveredResetIndex = newHoverReset;
                        _hoveredThemeIndex = newHoveredTheme;
                        Render();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
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
                    // 点击外部收起新增的显示下拉浮窗
                    else if (_displayDropdownOpen && _hoveredDisplayDropdownIndex == -1)
                    {
                        _displayDropdownOpen = false; Render();
                    }
                    else if (_selectedTab == 1 && _hoveredStyleIndex != -1)
                    {
                        Renderer.NotchStyle = _hoveredStyleIndex;
                        Program.SaveSetting("NotchStyle", _hoveredStyleIndex);
                        Render();
                    }
                    else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 5 && _selectedTab != 5) { _selectedTab = 5; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_selectedTab == 5 && (_hoveredMinusIndex != -1 || _hoveredPlusIndex != -1 || _hoveredResetIndex != -1))
                    {
                        int updateIdx;
                        if (_hoveredResetIndex != -1)
                        {
                            updateIdx = _hoveredResetIndex;
                            float[] defaultVals = { 130f, 34f, 260f, 34f, 260f, 55f, 1.0f };
                            _customValues[updateIdx] = defaultVals[updateIdx];
                        }
                        else
                        {
                            updateIdx = _hoveredMinusIndex != -1 ? _hoveredMinusIndex : _hoveredPlusIndex;
                            float delta = _hoveredPlusIndex != -1 ? (updateIdx == 6 ? 0.05f : 5f) : (updateIdx == 6 ? -0.05f : -5f);
                            _customValues[updateIdx] = Math.Max(updateIdx == 6 ? 0.5f : 20f, _customValues[updateIdx] + delta);
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

                        Render();
                    }
                    else if (_selectedTab == 5 && _hoveredThemeIndex != -1)
                    {
                        Renderer.ThemeMode = _hoveredThemeIndex;
                        Renderer.ApplyThemeColors(); // 立即反转画笔颜色
                        Program.SaveSetting("ThemeMode", _hoveredThemeIndex);
                        Render(); // 刷新控制台UI
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
                    else if (_mediaToggleHovered)
                    {
                        MediaController.IsMediaControlEnabled = !MediaController.IsMediaControlEnabled;
                        // 保存媒体控制开关 (转换为0/1)
                        Program.SaveSetting("MediaControl", MediaController.IsMediaControlEnabled ? 1 : 0);

                        _ = MediaController.Instance?.ForceRefresh();
                        Render();
                    }
                    else if (_autoHideToggleHovered)
                    {
                        NotchWindow.IsAutoHideEnabled = !NotchWindow.IsAutoHideEnabled;
                        // 保存自动隐藏开关 (转换为0/1)
                        Program.SaveSetting("AutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0);

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
                    else if (_displayDropdownHovered)
                    {
                        _displayDropdownOpen = true; Render();
                    }
                    else if (_displayDropdownOpen && _hoveredDisplayDropdownIndex != -1)
                    {
                        _selectedDisplayIndex = _hoveredDisplayDropdownIndex;
                        _displayDropdownOpen = false;
                        Render();
                    }
                    break;

                case Win32.WM_DESTROY:
                    _instance = null;
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private float GetBtnY(int index)
        {
            return index switch
            {
                -1 => TITLE_BAR_HEIGHT + 35,                 // 精准对应卡片高度的垂直中位线
                0 => TITLE_BAR_HEIGHT + 92 + 40,             // 待机宽度
                1 => TITLE_BAR_HEIGHT + 92 + 40 + 34,
                2 => TITLE_BAR_HEIGHT + 210 + 40,            // 媒体宽度
                3 => TITLE_BAR_HEIGHT + 210 + 40 + 34,
                4 => TITLE_BAR_HEIGHT + 328 + 40,            // 通知宽度
                5 => TITLE_BAR_HEIGHT + 328 + 40 + 34,
                6 => TITLE_BAR_HEIGHT + 446 + 40,            // DPI 缩放
                _ => 0
            };
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
            DrawTab(2, "媒体设置", 140);
            DrawTab(3, "交互设置", 180);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 222, 160, TITLE_BAR_HEIGHT + 222, _separatorPaint);
            DrawTab(4, "关于软件", 230);

            // 右侧卡片内容区
            void DrawToggleCard(float yOffset, string title, string sub, bool state, bool hovered)
            {
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + 62);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

                canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);
                canvas.DrawText(sub, 216, TITLE_BAR_HEIGHT + yOffset + 46, _subTextPaint);

                float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = TITLE_BAR_HEIGHT + yOffset + 20;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);

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

                if (state)
                {
                    canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _toggleCirclePaint.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White; // 恢复白色供下次使用
                }
            }

            if (_selectedTab == 0)
            {
                DrawToggleCard(12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
                DrawToggleCard(84, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);
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
                    _dynamicFillPaint.Color = Renderer.ThemeMode == 1 ? SKColors.Black : SKColors.White;

                    if (index == 0) // 模拟经典刘海
                    {
                        var path = new SKPath();
                        path.MoveTo(cx - 30, cy - 20);
                        path.QuadTo(cx - 20, cy - 20, cx - 20, cy - 10);
                        path.LineTo(cx - 20, cy + 5);
                        path.QuadTo(cx - 20, cy + 15, cx - 10, cy + 15);
                        path.LineTo(cx + 10, cy + 15);
                        path.QuadTo(cx + 20, cy + 15, cx + 20, cy + 5);
                        path.LineTo(cx + 20, cy - 10);
                        path.QuadTo(cx + 20, cy - 20, cx + 30, cy - 20);
                        canvas.DrawPath(path, _dynamicFillPaint);
                    }
                    else // 模拟灵动岛
                    {
                        canvas.DrawRoundRect(new SKRect(cx - 25, cy - 8, cx + 25, cy + 12), 10, 10, _dynamicFillPaint);
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

                // 待机显示内容卡片
                float displayCardY = TITLE_BAR_HEIGHT + 172;
                var cardRect = new SKRect(200, displayCardY, WIDTH - 20, displayCardY + 62);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
                canvas.DrawText("待机显示内容", 216, displayCardY + 26, _uiTextPaint);
                canvas.DrawText("刘海处于待机状态时默认展示的信息", 216, displayCardY + 46, _subTextPaint);

                float dW = 110; float dX = WIDTH - 140; float dY = displayCardY + 14; float dH = 32;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                _dynamicFillPaint.Color = _displayDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
                canvas.DrawRoundRect(dRect, 4, 4, _dynamicFillPaint);
                canvas.DrawText(_displayOptions[_selectedDisplayIndex], dX + 10, dY + 21, _uiTextPaint);

                canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, _chevronPaint);
                canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, _chevronPaint);
            }
            else if (_selectedTab == 2)
            {
                DrawToggleCard(12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + 84, WIDTH - 20, TITLE_BAR_HEIGHT + 146);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg); canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
                canvas.DrawText("目标媒体平台", 216, TITLE_BAR_HEIGHT + 110, _uiTextPaint);
                canvas.DrawText("多平台共存时，优先截获并接管的平台", 216, TITLE_BAR_HEIGHT + 130, _subTextPaint);

                float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                _dynamicFillPaint.Color = _dropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
                canvas.DrawRoundRect(dRect, 4, 4, _dynamicFillPaint);
                canvas.DrawText(_platforms[_selectedPlatformIndex].Name, dX + 10, dY + 21, _uiTextPaint);

                canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, _chevronPaint);
                canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, _chevronPaint);
            }
            else if (_selectedTab == 3)
            {
                DrawToggleCard(12, "自动隐藏", "当鼠标离开时自动隐藏刘海", NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered);
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
                var themeRect = new SKRect(200, themeY, WIDTH - 20, themeY + 70);
                canvas.DrawRoundRect(themeRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(themeRect, 6, 6, _cardBorder);

                canvas.DrawText("刘海主题", 216, themeY + 26, _uiTextPaint);
                canvas.DrawText("刘海背景与文本颜色自适应反转", 216, themeY + 46, _subTextPaint);

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
                DrawMultiCard(92, "待机显示", ["水平宽度", "垂直高度"], [0, 1], "px");
                DrawMultiCard(210, "媒体控制", ["激活时宽度", "激活时高度"], [2, 3], "px");
                DrawMultiCard(328, "消息通知", ["弹出的宽度", "弹出的高度"], [4, 5], "px");
                DrawMultiCard(446, "全局 DPI 缩放", ["视觉比例"], [6], "x");
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

            if (_selectedTab == 1 && _displayDropdownOpen)
            {
                // 将 mY 的坐标由 + 60 调整到下移后的避让位置：+ 220
                float mX = WIDTH - 140; float mY = TITLE_BAR_HEIGHT + 220; float mW = 110; float mH = _displayOptions.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);

                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                for (int i = 0; i < _displayOptions.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredDisplayDropdownIndex == i)
                    {
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    }
                    _dynamicTextPaint.Color = i == _selectedDisplayIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_displayOptions[i], mX + 12, itemY + 18, _dynamicTextPaint);
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