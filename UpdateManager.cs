using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using SkiaSharp;
using System.Reflection;

namespace NotchPeninsula
{
    public class UpdateManager
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        public static void StartSilentCheck()
        {
            Task.Run(async () =>
            {
                try
                {
                    if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                    {
                        _http.DefaultRequestHeaders.Add("User-Agent", "NotchPeninsula-UpdateChecker");
                        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                    }

                    Logger.Info("[更新检测] 正在向 GitHub 请求最新版本信息...");

                    var response = await _http.GetStringAsync("https://api.github.com/repos/GEORGEWWWU/NotchPeninsula/releases/latest");

                    using var doc = JsonDocument.Parse(response);
                    string? tag = doc.RootElement.GetProperty("tag_name").GetString()?.Trim();
                    string? body = doc.RootElement.GetProperty("body").GetString();

                    if (string.IsNullOrEmpty(tag) || !tag.StartsWith("NPS-v")) return;

                    var latestVersionStr = tag.Replace("NPS-v", "");
                    var latestVersion = new Version(latestVersionStr);
                    var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

                    if (latestVersion > currentVersion)
                    {
                        Logger.Info("[更新检测] 发现新版本！拉起全局弹窗...");

                        var notifyThread = new Thread(() =>
                        {
                            try
                            {
                                var notifyWin = new NotifyWindow(tag, body ?? "修复了一些已知问题，建议立即更新。");
                                notifyWin.Run();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error("[更新检测] 弹窗渲染失败", ex);
                            }
                        });

                        notifyThread.SetApartmentState(ApartmentState.STA);
                        notifyThread.IsBackground = true;
                        notifyThread.Start();
                    }
                }
                catch { /* 静默防干扰 */ }
            });
        }
    }

    public class NotifyWindow
    {
        private readonly IntPtr _hwnd;
        private readonly string _tag;
        private readonly Win32.WndProc _wndProcDelegate;

        private const int WIDTH = 460;
        private const int HEIGHT = 560;
        private int _scaledW, _scaledH;
        private float _dpiScale;

        // UI 交互与滚动状态
        private bool _confirmHovered = false;
        private bool _cancelHovered = false;
        private float _scrollY = 0f;
        private float _maxScroll = 0f;
        private readonly List<string> _wrappedLines = new();
        private const float LINE_HEIGHT = 24f;
        private const float CONTENT_BOX_HEIGHT = 220f;

        // 静态复用资源
        private static SKBitmap? _iconBitmap;
        private static readonly SKPaint _bgPaint = new() { Color = new SKColor(32, 32, 32), IsAntialias = true };
        private static readonly SKPaint _borderPaint = new() { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        private static readonly SKPaint _dynamicFill = new() { IsAntialias = true };
        private static readonly SKPaint _hqSamplingOpts = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };

        // 字体画笔
        private static readonly SKPaint _title1Paint = new() { Color = SKColors.White, TextSize = 22f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
        private static readonly SKPaint _title2Paint = new() { Color = new SKColor(200, 200, 200), TextSize = 15f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
        private static readonly SKPaint _contentPaint = new() { Color = new SKColor(170, 170, 170), TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _btnTextPaint = new() { Color = SKColors.White, TextSize = 14f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        public NotifyWindow(string tag, string content)
        {
            _tag = tag;
            _wndProcDelegate = WndProc;

            LoadAppIcon();
            BuildWrappedLines(content, 380f); // 文本最大宽度 380 (左右边距40)

            // 计算最大滚动距离
            float totalTextHeight = _wrappedLines.Count * LINE_HEIGHT;
            _maxScroll = Math.Max(0, totalTextHeight - CONTENT_BOX_HEIGHT);

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _wndProcDelegate,
                hInstance = Marshal.GetHINSTANCE(typeof(NotifyWindow).Module),
                lpszClassName = "NpsNotifyClass",
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };
            Win32.RegisterClass(ref wc);

            _dpiScale = Win32.GetDpiForSystem() / 96f;
            _scaledW = (int)(WIDTH * _dpiScale);
            _scaledH = (int)(HEIGHT * _dpiScale);

            int screenW = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenH = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST,
                "NpsNotifyClass", "Notify",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                (screenW - _scaledW) / 2, (screenH - _scaledH) / 2,
                _scaledW, _scaledH,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero
            );

            Render();
        }

        private static void LoadAppIcon()
        {
            if (_iconBitmap != null) return;
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "NPS_NotchPeninsula-logo.ico");
                if (File.Exists(iconPath))
                {
                    _iconBitmap = SKBitmap.Decode(iconPath);
                }
                else
                {
                    var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (sysIcon != null)
                    {
                        using var bmp = sysIcon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        _iconBitmap = SKBitmap.Decode(ms);
                    }
                }
            }
            catch { }
        }

        private void BuildWrappedLines(string content, float maxWidth)
        {
            _wrappedLines.Clear();
            string[] lines = content.Replace("\r", "").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) { _wrappedLines.Add(""); continue; }

                int start = 0;
                while (start < line.Length)
                {
                    int len = line.Length - start;
                    string sub = line.Substring(start, len);
                    while (len > 0 && _contentPaint.MeasureText(sub) > maxWidth)
                    {
                        len--;
                        if (len > 0) sub = line.Substring(start, len);
                    }
                    if (len == 0) { len = 1; sub = line.Substring(start, 1); } // 兜底防止死循环

                    _wrappedLines.Add(sub);
                    start += len;
                }
            }
        }

        public void Run()
        {
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                    int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);

                    // 确定按钮热区：X 居中偏左
                    bool cHover = x >= 100 && x <= 220 && y >= HEIGHT - 65 && y <= HEIGHT - 25;
                    // 取消按钮热区：X 居中偏右
                    bool xHover = x >= 240 && x <= 360 && y >= HEIGHT - 65 && y <= HEIGHT - 25;

                    if (cHover != _confirmHovered || xHover != _cancelHovered)
                    {
                        _confirmHovered = cHover;
                        _cancelHovered = xHover;
                        Render();
                    }
                    break;

                case 0x020A: // WM_MOUSEWHEEL
                    int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    _scrollY -= delta * 0.2f; // 调整滚轮灵敏度
                    _scrollY = Math.Clamp(_scrollY, 0, _maxScroll);
                    Render();
                    break;

                case Win32.WM_LBUTTONDOWN:
                    int clickY = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                    if (_confirmHovered)
                    {
                        Process.Start(new ProcessStartInfo { FileName = "https://github.com/GEORGEWWWU/NotchPeninsula/releases/latest", UseShellExecute = true });
                        Win32.DestroyWindow(hwnd);
                    }
                    else if (_cancelHovered) Win32.DestroyWindow(hwnd);
                    // 拖拽窗口
                    else if (clickY <= 150)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
                    }
                    break;

                case Win32.WM_DESTROY:
                    Win32.PostQuitMessage(0);
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private unsafe void Render()
        {
            var info = new SKImageInfo(_scaledW, _scaledH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Scale(_dpiScale);
            canvas.Clear(SKColors.Transparent);

            // 1. 背景与边框
            canvas.DrawRoundRect(new SKRect(0, 0, WIDTH, HEIGHT), 10, 10, _bgPaint);
            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f), 10, 10, _borderPaint);

            // 2. 顶部大 Icon (纯净无阴影)
            if (_iconBitmap != null)
            {
                float iconSize = 100f;
                float iconX = (WIDTH - iconSize) / 2f;
                canvas.DrawBitmap(_iconBitmap, new SKRect(iconX, 35, iconX + iconSize, 35 + iconSize), _hqSamplingOpts);
            }

            // 3. 核心标题
            canvas.DrawText("NotchPeninsula", WIDTH / 2f, 175, _title1Paint);
            canvas.DrawText($"发现新版本 {_tag}！是否前往下载？", WIDTH / 2f, 210, _title2Paint);

            // 4. 更新内容 (滚动视窗区)
            float contentStartY = 240f;
            canvas.Save();
            // 限制绘制范围，超出 CONTENT_BOX_HEIGHT 的内容将被自动剪裁隐藏
            canvas.ClipRect(new SKRect(30, contentStartY, WIDTH - 30, contentStartY + CONTENT_BOX_HEIGHT));
            // 根据滚轮状态向上偏移画布
            canvas.Translate(0, -_scrollY);

            for (int i = 0; i < _wrappedLines.Count; i++)
            {
                // X = 40 (左对齐)，Y 逐行递增
                canvas.DrawText(_wrappedLines[i], 40, contentStartY + 15 + (i * LINE_HEIGHT), _contentPaint);
            }
            canvas.Restore();

            // 5. 底部按钮组
            float btnY = HEIGHT - 65f;
            float btnHeight = 40f;
            float btnWidth = 120f;
            float centerX = WIDTH / 2f;

            // 确定按钮 (左边，带品牌色)
            var confirmRect = new SKRect(centerX - btnWidth - 10, btnY, centerX - 10, btnY + btnHeight);
            _dynamicFill.Color = _confirmHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
            canvas.DrawRoundRect(confirmRect, 6, 6, _dynamicFill);
            canvas.DrawText("前往下载", confirmRect.MidX, btnY + 26, _btnTextPaint);

            // 取消按钮 (右边，暗色调)
            var cancelRect = new SKRect(centerX + 10, btnY, centerX + btnWidth + 10, btnY + btnHeight);
            _dynamicFill.Color = _cancelHovered ? new SKColor(255, 255, 255, 25) : new SKColor(255, 255, 255, 10);
            canvas.DrawRoundRect(cancelRect, 6, 6, _dynamicFill);
            canvas.DrawText("取消", cancelRect.MidX, btnY + 26, _btnTextPaint);

            UpdateWindow(surface.PeekPixels());
        }

        private unsafe void UpdateWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = _scaledW, biHeight = -_scaledH, biPlanes = 1, biBitCount = 32, biCompression = 0 }
            };

            IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), (long)_scaledW * _scaledH * 4, (long)_scaledW * _scaledH * 4);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT(0, 0);
            Win32.GetWindowRect(_hwnd, out var winRect);
            ptDst.x = winRect.Left; ptDst.y = winRect.Top;

            var size = new Win32.SIZE(_scaledW, _scaledH);
            var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };

            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            Win32.SelectObject(memDc, hOldBitmap); Win32.DeleteObject(hBitmap); Win32.DeleteDC(memDc); Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}