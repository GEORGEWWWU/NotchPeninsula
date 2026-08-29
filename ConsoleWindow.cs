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
                    hInstance = Process.GetCurrentProcess().Handle,
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
                IntPtr.Zero, IntPtr.Zero, Process.GetCurrentProcess().Handle, IntPtr.Zero
            );

            Render();
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
                    if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 10 && y <= TITLE_BAR_HEIGHT + 46) newHoveredTab = 0; // 通用设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 50 && y <= TITLE_BAR_HEIGHT + 86) newHoveredTab = 1; // 显示设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 90 && y <= TITLE_BAR_HEIGHT + 126) newHoveredTab = 2; // 媒体设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 130 && y <= TITLE_BAR_HEIGHT + 166) newHoveredTab = 3; // 交互设置
                    else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 170 && y <= TITLE_BAR_HEIGHT + 206) newHoveredTab = 4; // 关于页

                    bool newDisplayDropdownHovered = false;
                    int newHoveredDisplayDropdownIndex = -1;

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
                        // 下拉菜单判定 (直接位于顶部第一张卡片)
                        if (!_displayDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 26 && y <= TITLE_BAR_HEIGHT + 58)
                            newDisplayDropdownHovered = true;

                        if (_displayDropdownOpen)
                        {
                            if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 60 && y < TITLE_BAR_HEIGHT + 60 + _displayOptions.Length * 26)
                                newHoveredDisplayDropdownIndex = (y - (TITLE_BAR_HEIGHT + 60)) / 26;
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
                        newHoveredDisplayDropdownIndex != _hoveredDisplayDropdownIndex)
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
                    else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
                    else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; _dropdownOpen = false; _displayDropdownOpen = false; Render(); }
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

        private unsafe void Render()
        {
            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // 调用 Skia 硬件级矩阵缩放
            canvas.Scale(_dpiScale);

            canvas.Clear(SKColors.Transparent);
            float cornerRadius = 8f;
            var windowRect = new SKRect(0, 0, WIDTH, HEIGHT);

            using var bgPaint = new SKPaint { Color = new SKColor(32, 32, 32), IsAntialias = true };
            canvas.DrawRoundRect(windowRect, cornerRadius, cornerRadius, bgPaint);

            canvas.Save();
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(windowRect, cornerRadius, cornerRadius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            // 标题栏区
            using var titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };
            canvas.DrawRect(0, 0, WIDTH, TITLE_BAR_HEIGHT, titleBarPaint);

            float textX = 14f;
            if (_appIconBitmap != null)
            {
                // 标题栏图标：16x16 逻辑像素，FilterQuality.High 确保从 256→16 是高质量 Lanczos 缩放
                var iconRect = new SKRect(14, 8, 14 + 16, 8 + 16);
                using var samplingOpts = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawBitmap(_appIconBitmap, iconRect, samplingOpts);
                textX += 24f;
            }

            using var uiTextPaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
            using var subTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 12f, IsAntialias = true, Typeface = uiTextPaint.Typeface };

            using var titleTextPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 12.5f, IsAntialias = true, Typeface = uiTextPaint.Typeface };
            canvas.DrawText(_appTitleWithVersion, textX, 21.2f, titleTextPaint);

            if (_minHovered) { using var hp = new SKPaint { Color = new SKColor(255, 255, 255, 20) }; canvas.DrawRect(WIDTH - 92, 0, 46, TITLE_BAR_HEIGHT, hp); }
            if (_closeHovered) { using var hp = new SKPaint { Color = new SKColor(232, 17, 35) }; canvas.DrawRect(WIDTH - 46, 0, 46, TITLE_BAR_HEIGHT, hp); }

            using var iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, iconPaint);
            float cx = WIDTH - 46 + 23; float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, iconPaint);

            // 侧边栏
            void DrawTab(int index, string label, float yOffset)
            {
                var tabRect = new SKRect(10, TITLE_BAR_HEIGHT + yOffset, 170, TITLE_BAR_HEIGHT + yOffset + 36);
                if (_selectedTab == index)
                {
                    using var tabBg = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };
                    canvas.DrawRoundRect(tabRect, 4, 4, tabBg);
                    using var indicator = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };
                    canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + yOffset + 8, 13, TITLE_BAR_HEIGHT + yOffset + 28), 1.5f, 1.5f, indicator);
                }
                else if (_hoveredTab == index)
                {
                    using var tabBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                    canvas.DrawRoundRect(tabRect, 4, 4, tabBg);
                }
                canvas.DrawText(label, 30, TITLE_BAR_HEIGHT + yOffset + 24, uiTextPaint);
            }

            DrawTab(0, "通用设置", 10);
            DrawTab(1, "显示设置", 50);  // 调至第二位
            DrawTab(2, "媒体设置", 90);  // 顺延
            DrawTab(3, "交互设置", 130); // 顺延
            DrawTab(4, "关于软件", 170); // 顺延

            // 右侧卡片内容区
            void DrawToggleCard(float yOffset, string title, string sub, bool state, bool hovered)
            {
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + 62);
                using var cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                using var cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(cardRect, 6, 6, cardBg); canvas.DrawRoundRect(cardRect, 6, 6, cardBorder);

                canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, uiTextPaint);
                canvas.DrawText(sub, 216, TITLE_BAR_HEIGHT + yOffset + 46, subTextPaint);

                float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = TITLE_BAR_HEIGHT + yOffset + 20;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                using var tBg = new SKPaint { IsAntialias = true };
                if (state) { tBg.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212); canvas.DrawRoundRect(tRect, tH / 2, tH / 2, tBg); }
                else { tBg.Style = SKPaintStyle.Stroke; tBg.StrokeWidth = 1.5f; tBg.Color = hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100); canvas.DrawRoundRect(tRect, tH / 2, tH / 2, tBg); }

                using var tCircle = new SKPaint { Color = SKColors.White, IsAntialias = true };
                if (state) canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, tCircle);
                else { tCircle.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150); canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, tCircle); }
            }

            if (_selectedTab == 0) // 通用设置
            {
                DrawToggleCard(12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
                DrawToggleCard(84, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);
            }
            else if (_selectedTab == 1) // 显示设置
            {
                // 卡片直接贴在顶部 (Y轴 Offset 12)
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 74);
                using var cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                using var cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(cardRect, 6, 6, cardBg); canvas.DrawRoundRect(cardRect, 6, 6, cardBorder);

                canvas.DrawText("待机显示内容", 216, TITLE_BAR_HEIGHT + 38, uiTextPaint);
                canvas.DrawText("刘海处于待机状态时默认展示的信息", 216, TITLE_BAR_HEIGHT + 58, subTextPaint);

                // 右侧的伪输入下拉框
                float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 26; float dH = 32;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                using var dBg = new SKPaint { Color = _displayDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8), IsAntialias = true };
                canvas.DrawRoundRect(dRect, 4, 4, dBg);
                canvas.DrawText(_displayOptions[_selectedDisplayIndex], dX + 10, dY + 21, uiTextPaint);

                using var chevronPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, chevronPaint);
                canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, chevronPaint);
            }
            else if (_selectedTab == 2) // 媒体设置
            {
                DrawToggleCard(12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

                // 绘制下拉选择卡片
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + 80, WIDTH - 20, TITLE_BAR_HEIGHT + 142);
                using var cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                using var cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(cardRect, 6, 6, cardBg); canvas.DrawRoundRect(cardRect, 6, 6, cardBorder);

                canvas.DrawText("目标媒体平台", 216, TITLE_BAR_HEIGHT + 106, uiTextPaint);
                canvas.DrawText("多平台共存时，优先截获并接管的平台", 216, TITLE_BAR_HEIGHT + 126, subTextPaint);

                // Dropdown 伪输入框
                float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                using var dBg = new SKPaint { Color = _dropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8), IsAntialias = true };
                canvas.DrawRoundRect(dRect, 4, 4, dBg);
                canvas.DrawText(_platforms[_selectedPlatformIndex].Name, dX + 10, dY + 21, uiTextPaint);

                // 向下的 Chevron 箭头
                using var chevronPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, chevronPaint);
                canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, chevronPaint);
            }
            else if (_selectedTab == 3) // 交互设置
            {
                DrawToggleCard(12, "自动隐藏", "当鼠标离开时自动隐藏刘海", NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered);
            }
            else if (_selectedTab == 4) // 关于页面
            {
                float centerX = 200 + (WIDTH - 200) / 2f;
                float startY = TITLE_BAR_HEIGHT + 30f; // 稍微上移一点，给高清大图标腾出空间

                // 图标绘制 (恢复 64x64 大尺寸！现在原汁原味绝对高清)
                if (_appIconBitmap != null)
                {
                    // 关于页图标：64x64 逻辑像素，从 256 缩放到 64 使用 High 质量
                    var iconRect = new SKRect(centerX - 32, startY, centerX + 32, startY + 64);
                    using var hqPaint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                    canvas.DrawBitmap(_appIconBitmap, iconRect, hqPaint);
                    startY += 90f;
                }

                // 大标题 (NotchPeninsula)
                using var titlePaint = new SKPaint { Color = SKColors.White, TextSize = 20f, IsAntialias = true, Typeface = uiTextPaint.Typeface, TextAlign = SKTextAlign.Center };
                canvas.DrawText("NotchPeninsula", centerX, startY, titlePaint);
                startY += 22f; // 标题到版本号的间距

                // 小文本 (NPS v1.0.0)
                using var versionPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 13f, IsAntialias = true, Typeface = uiTextPaint.Typeface, TextAlign = SKTextAlign.Center };
                string displayVersion = _appTitleWithVersion.Replace("NotchPeninsula ", "NPS v");
                canvas.DrawText(displayVersion, centerX, startY, versionPaint);
                startY += 35f; // 版本号到下方超链接的间距

                // 超链接
                string[] links = ["检测更新", "项目仓库", "开发者"];
                using var linkPaint = new SKPaint { TextSize = 13f, IsAntialias = true, Typeface = uiTextPaint.Typeface, TextAlign = SKTextAlign.Left };

                float spacing = 15f;
                float totalWidth = linkPaint.MeasureText(links[0]) + linkPaint.MeasureText(links[1]) + linkPaint.MeasureText(links[2]) + (spacing * 2);
                float currentX = centerX - (totalWidth / 2f);

                for (int i = 0; i < links.Length; i++)
                {
                    float textWidth = linkPaint.MeasureText(links[i]);
                    linkPaint.Color = _hoveredLinkIndex == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawText(links[i], currentX, startY, linkPaint);
                    currentX += textWidth + spacing;
                }
            }

            canvas.Restore(); // 结束大边界裁切

            // 浮动在顶层的下拉菜单
            // 媒体设置下拉菜单
            if (_selectedTab == 2 && _dropdownOpen)
            {
                float mX = WIDTH - 140; float mY = TITLE_BAR_HEIGHT + 130; float mW = 110; float mH = _platforms.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);

                // 绘制不透明底色和阴影感边框，防止背后的UI透过来
                using var menuBg = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true };
                canvas.DrawRoundRect(mRect, 4, 4, menuBg);
                using var menuBorder = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(mRect, 4, 4, menuBorder);

                for (int i = 0; i < _platforms.Length; i++)
                {
                    // 每个 item 的 Y 轴步长改为 26
                    float itemY = mY + i * 26;
                    if (_hoveredDropdownIndex == i)
                    {
                        using var itemHover = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };
                        // 底部边界减去 2px 的 padding，也就是 26-2 = 24
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, itemHover);
                    }
                    using var itemTextPaint = new SKPaint { Color = i == _selectedPlatformIndex ? new SKColor(0, 120, 212) : SKColors.White, TextSize = 13f, IsAntialias = true, Typeface = uiTextPaint.Typeface };
                    // 文字的 Y 轴光学居中点调到 18
                    canvas.DrawText(_platforms[i].Name, mX + 12, itemY + 18, itemTextPaint);
                }
            }

            // 显示设置下拉菜单
            if (_selectedTab == 1 && _displayDropdownOpen)
            {
                float mX = WIDTH - 140; float mY = TITLE_BAR_HEIGHT + 60; float mW = 110; float mH = _displayOptions.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);

                using var menuBg = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true };
                canvas.DrawRoundRect(mRect, 4, 4, menuBg);
                using var menuBorder = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(mRect, 4, 4, menuBorder);

                for (int i = 0; i < _displayOptions.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredDisplayDropdownIndex == i)
                    {
                        using var itemHover = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, itemHover);
                    }
                    using var itemTextPaint = new SKPaint { Color = i == _selectedDisplayIndex ? new SKColor(0, 120, 212) : SKColors.White, TextSize = 13f, IsAntialias = true, Typeface = uiTextPaint.Typeface };
                    canvas.DrawText(_displayOptions[i], mX + 12, itemY + 18, itemTextPaint);
                }
            }

            // 最后盖上一层极细的全局边框
            using var globalBorderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f), cornerRadius, cornerRadius, globalBorderPaint);

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