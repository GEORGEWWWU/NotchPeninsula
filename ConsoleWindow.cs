using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace NotchPeninsula
{
    public class ConsoleWindow
    {
        private static ConsoleWindow? _instance;
        private readonly IntPtr _hwnd;

        // 将委托变为静态只读，使其成为 GC Root，永远不会被回收
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        // 标记窗口类是否已注册
        private static bool _classRegistered = false;

        private const int WIDTH = 600;
        private const int HEIGHT = 600;
        private const int TITLE_BAR_HEIGHT = 32;

        private bool _minHovered = false;
        private bool _closeHovered = false;
        private static SKBitmap? _appIconBitmap;
        // 缓存带版本号的标题，避免渲染时产生字符串分配开销
        private static string _appTitleWithVersion = "NotchPeninsula";
        private int _selectedTab = 0; // 0代表当前选中“通用设置”
        private int _hoveredTab = -1; // -1代表鼠标没悬浮在任何 Tab 上
        private bool _isAutoStartEnabled; // 当前自启状态
        private bool _toggleHovered = false; // 鼠标是否悬浮在开关上

        public static void Toggle()
        {
            if (_instance == null)
                _instance = new ConsoleWindow();
            else
            {
                // 每次重新呼出时，同步一下最新的自启状态
                _instance._isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
                _instance.Render();
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(_instance._hwnd);
            }
        }

        private ConsoleWindow()
        {
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();

            // 确保整个进程生命周期内只注册一次窗口类
            if (!_classRegistered)
            {
                // 读取程序集版本号并拼接缓存
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    // 格式化为 v1.0.0 形式
                    _appTitleWithVersion = $"NotchPeninsula {version.Major}.{version.Minor}.{version.Build}";
                }

                IntPtr appIconHandle = IntPtr.Zero;
                try
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);
                    if (icon != null)
                    {
                        appIconHandle = icon.Handle;

                        // 无损转码为 Skia 格式并缓存（只需执行一次，极低开销）
                        using var bmp = icon.ToBitmap();
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        _appIconBitmap = SKBitmap.Decode(ms);
                    }
                }
                catch { /* 忽略无伤大雅的图标提取错误 */ }

                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc,
                    hInstance = System.Diagnostics.Process.GetCurrentProcess().Handle,
                    lpszClassName = "NotchConsoleClass",
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
                    hIcon = appIconHandle // 绑定系统任务栏图标
                };
                Win32.RegisterClass(ref wc);
                _classRegistered = true;
            }

            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenHeight = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED,
                "NotchConsoleClass", "NotchPeninsula",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                (screenWidth - WIDTH) / 2, (screenHeight - HEIGHT) / 2, WIDTH, HEIGHT,
                IntPtr.Zero, IntPtr.Zero, System.Diagnostics.Process.GetCurrentProcess().Handle, IntPtr.Zero
            );

            Render();
        }

        // 静态消息路由，安全地将底层消息转发给当前活跃的实例
        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instance != null && hwnd == _instance._hwnd)
            {
                return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    int x = (short)(lParam.ToInt32() & 0xFFFF);
                    int y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                    bool newMinHovered = x >= WIDTH - 92 && x < WIDTH - 46 && y <= TITLE_BAR_HEIGHT;
                    bool newCloseHovered = x >= WIDTH - 46 && x <= WIDTH && y <= TITLE_BAR_HEIGHT;

                    // 侧边栏菜单悬浮判定 (范围: X:10~170, Y:42~78)
                    int newHoveredTab = -1;
                    if (x >= 10 && x <= 170 && y >= 42 && y <= 78) newHoveredTab = 0;

                    // 右侧开关悬浮判定 (卡片范围)
                    bool newToggleHovered = false;
                    if (_selectedTab == 0 && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                        newToggleHovered = true;

                    if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                        newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered)
                    {
                        _minHovered = newMinHovered;
                        _closeHovered = newCloseHovered;
                        _hoveredTab = newHoveredTab;
                        _toggleHovered = newToggleHovered;
                        Render();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
                    int clickX = (short)(lParam.ToInt32() & 0xFFFF);
                    int clickY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                    if (_closeHovered)
                        Win32.DestroyWindow(hwnd);
                    else if (_minHovered)
                        Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
                    else if (clickY <= TITLE_BAR_HEIGHT)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
                    }
                    else if (_hoveredTab == 0) // 点击左侧 Tab
                    {
                        if (_selectedTab != 0)
                        {
                            _selectedTab = 0;
                            Render();
                        }
                    }
                    else if (_toggleHovered) // 点击开机自启开关
                    {
                        _isAutoStartEnabled = !_isAutoStartEnabled;
                        Render(); // 界面立即响应
                        // 告诉主逻辑去写注册表，并标明“这来自控制台(false)”，让它顺便去更新托盘
                        NotchWindow.ToggleAutoStart(_isAutoStartEnabled, false);
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
            var info = new SKImageInfo(WIDTH, HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // 1. 核心：彻底清空为透明背景！(依赖 Layered Window 的天生特性)
            canvas.Clear(SKColors.Transparent);

            float cornerRadius = 8f; // 你可以随意调节圆角大小
            var windowRect = new SKRect(0, 0, WIDTH, HEIGHT);

            // 2. 绘制带圆角的主窗口底色
            using var bgPaint = new SKPaint { Color = new SKColor(32, 32, 32), IsAntialias = true };
            canvas.DrawRoundRect(windowRect, cornerRadius, cornerRadius, bgPaint);

            // --- 开启裁剪：防止顶部的标题栏和按钮背景画出圆角的范围 ---
            canvas.Save();
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(windowRect, cornerRadius, cornerRadius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            // 3. 标题栏区域
            using var titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };
            canvas.DrawRect(0, 0, WIDTH, TITLE_BAR_HEIGHT, titleBarPaint);

            // 绘制软件图标与名称 (WinUI 风格)
            float textX = 14f;
            if (_appIconBitmap != null)
            {
                // 将图标缩放到 16x16 的精巧尺寸，并在垂直方向居中 (32-16)/2 = 8
                var iconRect = new SKRect(14, 8, 14 + 16, 8 + 16);
                // 高质量采样器，保证缩放后的图标依然清晰
                using var samplingOpts = new SKPaint { FilterQuality = SKFilterQuality.High };
                canvas.DrawBitmap(_appIconBitmap, iconRect, samplingOpts);
                textX += 16f + 8f; // 让出图标和间距
            }

            using var textPaint = new SKPaint
            {
                Color = new SKColor(200, 200, 200), // 稍暗的浅灰色，在标题栏上比刺眼的纯白更护眼
                TextSize = 12.5f,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
            };
            // 文本 Y 轴精准对齐，对于 12.5 号字在 32px 的高度里，21px 是光学居中甜点位
            // 使用预先拼接好的缓存字符串
            canvas.DrawText(_appTitleWithVersion, textX, 21.2f, textPaint);

            // 4. 绘制 WinUI 风格按钮背景 (悬浮态)
            if (_minHovered)
            {
                using var hoverPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20) };
                canvas.DrawRect(WIDTH - 92, 0, 46, TITLE_BAR_HEIGHT, hoverPaint);
            }
            if (_closeHovered)
            {
                using var hoverPaint = new SKPaint { Color = new SKColor(232, 17, 35) };
                canvas.DrawRect(WIDTH - 46, 0, 46, TITLE_BAR_HEIGHT, hoverPaint);
            }

            // --- 结束裁剪 ---
            canvas.Restore();

            // 5. 绘制按钮图标 (保留原来的代码)
            using var iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, iconPaint);
            float cx = WIDTH - 46 + 23; float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, iconPaint);

            // ================= 设置页面布局 =================

            // 绘制左侧侧边栏项
            var tabRect = new SKRect(10, TITLE_BAR_HEIGHT + 10, 170, TITLE_BAR_HEIGHT + 46);
            if (_selectedTab == 0)
            {
                using var tabBg = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };
                canvas.DrawRoundRect(tabRect, 4, 4, tabBg); // 选中的底色

                // 蓝色竖条指示器 (Win11 标志性设计)
                using var indicator = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + 18, 13, TITLE_BAR_HEIGHT + 38), 1.5f, 1.5f, indicator);
            }
            else if (_hoveredTab == 0)
            {
                using var tabBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                canvas.DrawRoundRect(tabRect, 4, 4, tabBg); // 悬浮的底色
            }

            using var uiTextPaint = new SKPaint { Color = SKColors.White, TextSize = 14f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
            canvas.DrawText("通用设置", 30, TITLE_BAR_HEIGHT + 34, uiTextPaint);

            // 绘制右侧详情区内容
            if (_selectedTab == 0)
            {
                // 设置卡片背景容器
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 74);
                using var cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };
                canvas.DrawRoundRect(cardRect, 6, 6, cardBg);
                using var cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                canvas.DrawRoundRect(cardRect, 6, 6, cardBorder);

                // 左对齐的标题和描述文本
                canvas.DrawText("开机自启", 216, TITLE_BAR_HEIGHT + 38, uiTextPaint);
                using var subTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 12f, IsAntialias = true, Typeface = uiTextPaint.Typeface };
                canvas.DrawText("跟随系统启动自动运行该程序", 216, TITLE_BAR_HEIGHT + 58, subTextPaint);

                // 右对齐绘制 Toggle 拨动开关
                float toggleW = 42; float toggleH = 20;
                float toggleX = WIDTH - 20 - 16 - toggleW;
                float toggleY = TITLE_BAR_HEIGHT + 32;
                var toggleRect = new SKRect(toggleX, toggleY, toggleX + toggleW, toggleY + toggleH);

                using var toggleBg = new SKPaint { IsAntialias = true };
                if (_isAutoStartEnabled)
                {
                    // 开启状态：品牌蓝填充
                    toggleBg.Color = _toggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(toggleRect, toggleH / 2, toggleH / 2, toggleBg);
                }
                else
                {
                    // 关闭状态：灰色空心描边
                    toggleBg.Style = SKPaintStyle.Stroke;
                    toggleBg.StrokeWidth = 1.5f;
                    toggleBg.Color = _toggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(toggleRect, toggleH / 2, toggleH / 2, toggleBg);
                }

                // 绘制开关里的白色小圆点
                using var toggleCircle = new SKPaint { Color = SKColors.White, IsAntialias = true };
                if (_isAutoStartEnabled)
                {
                    // 靠右的实心圆
                    canvas.DrawCircle(toggleX + toggleW - toggleH / 2, toggleY + toggleH / 2, toggleH / 2 - 4, toggleCircle);
                }
                else
                {
                    // 靠左的暗色圆
                    toggleCircle.Color = _toggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(toggleX + toggleH / 2, toggleY + toggleH / 2, toggleH / 2 - 4, toggleCircle);
                }
            }
            // ================= 设置页面布局结束 =================

            // 6. 最后盖上一层极细的抗锯齿圆角描边边框
            using var borderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            var borderRect = new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f);
            canvas.DrawRoundRect(borderRect, cornerRadius, cornerRadius, borderPaint);

            // 7. 提交给系统
            UpdateWindow(surface.PeekPixels());
        }

        // 复用 NotchWindow 的图像提交逻辑
        private unsafe void UpdateWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)), biWidth = WIDTH, biHeight = -HEIGHT, biPlanes = 1, biBitCount = 32, biCompression = 0 }
            };

            IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);

            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), WIDTH * HEIGHT * 4, WIDTH * HEIGHT * 4);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT(0, 0);
            Win32.GetWindowRect(_hwnd, out var rect);
            ptDst.x = rect.Left;
            ptDst.y = rect.Top;

            var size = new Win32.SIZE(WIDTH, HEIGHT);
            var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };

            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            Win32.SelectObject(memDc, hOldBitmap);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }

        // 供 NotchWindow 在托盘被点击时调用，实时同步状态并重绘
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