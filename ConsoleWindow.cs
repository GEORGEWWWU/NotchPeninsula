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
        private const int HEIGHT = 800;
        private const int TITLE_BAR_HEIGHT = 32;

        private bool _minHovered = false;
        private bool _closeHovered = false;

        public static void Toggle()
        {
            if (_instance == null)
                _instance = new ConsoleWindow();
            else
            {
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(_instance._hwnd);
            }
        }

        private ConsoleWindow()
        {
            // 确保整个进程生命周期内只注册一次窗口类
            if (!_classRegistered)
            {
                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc, // 绑定静态委托
                    hInstance = System.Diagnostics.Process.GetCurrentProcess().Handle,
                    lpszClassName = "NotchConsoleClass",
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
                };
                Win32.RegisterClass(ref wc);
                _classRegistered = true;
            }

            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenHeight = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED,
                "NotchConsoleClass", "Console",
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

                    if (newMinHovered != _minHovered || newCloseHovered != _closeHovered)
                    {
                        _minHovered = newMinHovered;
                        _closeHovered = newCloseHovered;
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
                    break;

                case Win32.WM_DESTROY:
                    _instance = null; // 实例被置空，GC 将回收这 2MB 内存，但静态委托依然安全存活
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private unsafe void Render()
        {
            var info = new SKImageInfo(WIDTH, HEIGHT, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // 1. 窗口底色 (深色模式) 和 边框
            canvas.Clear(new SKColor(32, 32, 32));
            using var borderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(0, 0, WIDTH, HEIGHT, borderPaint);

            // 2. 标题栏区域
            using var titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };
            canvas.DrawRect(1, 1, WIDTH - 2, TITLE_BAR_HEIGHT, titleBarPaint);

            // 3. 绘制 WinUI 风格按钮
            using var iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

            // 最小化按钮 (46x32)
            if (_minHovered)
            {
                using var hoverPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20) };
                canvas.DrawRect(WIDTH - 92, 1, 46, TITLE_BAR_HEIGHT, hoverPaint);
            }
            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, iconPaint);

            // 关闭按钮 (46x32)
            if (_closeHovered)
            {
                using var hoverPaint = new SKPaint { Color = new SKColor(232, 17, 35) };
                canvas.DrawRect(WIDTH - 46, 1, 45, TITLE_BAR_HEIGHT, hoverPaint);
            }
            // 简单的 SVG Path 式 X 号
            float cx = WIDTH - 46 + 23;
            float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, iconPaint);

            // 4. 提交到 Layered Window
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
    }
}