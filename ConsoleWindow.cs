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
                // 提取当前 exe 程序自身的图标句柄
                var appIconHandle = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)?.Handle ?? IntPtr.Zero;

                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc,
                    hInstance = System.Diagnostics.Process.GetCurrentProcess().Handle,
                    lpszClassName = "NotchConsoleClass",
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
                    hIcon = appIconHandle // 绑定图标
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

            // 5. 绘制按钮图标
            using var iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            // 最小化按钮图标
            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, iconPaint);
            // 关闭按钮图标 (X)
            float cx = WIDTH - 46 + 23;
            float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, iconPaint);

            // 6. 最后盖上一层极细的抗锯齿圆角描边边框
            using var borderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            // 坐标缩进 0.5f，这是 Skia 的渲染特性，能确保 1px 的线条画得最清晰锐利，且不被边界裁掉一半
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
    }
}