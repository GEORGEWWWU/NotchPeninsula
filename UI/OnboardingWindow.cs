using System.Runtime.InteropServices;
using SkiaSharp;
using Microsoft.Win32;

namespace NotchPeninsula
{
    /// <summary>
    /// 首次启动引导：灵动岛起来之前跑一遍的独立向导窗口（自绘 layered 窗口，圆角 + 投影）。
    /// 写入的注册表键与设置窗口完全一致，所以"引导里设置过" == "在设置窗口里设置过"，
    /// 之后随便在设置窗口改都互不冲突。不碰任何岛体核心代码。
    /// </summary>
    internal sealed class OnboardingWindow
    {
        private const string ClassName = "NPSOnboardingClass";
        private const string RegValue = "OnboardedVersion";
        private const int PM_REMOVE = 0x0001;

        // ---- 版式（DIP，全部相对卡片左上角；x/y 与实际提交坐标差一个 SHADOW）----
        private const float CARD_W = 560f;
        private const float CARD_H = 460f;
        private const float PAD = 32f;
        private const float SHADOW = 26f;
        private const float WIN_W = CARD_W + SHADOW * 2f;
        private const float WIN_H = CARD_H + SHADOW * 2f;

        private const float SEP_Y = 380f;          // 底部按钮区与内容区的分割线
        private const float BTN_H = 38f;
        private const float CTRL_X = CARD_W - PAD - 250f;
        private const float CTRL_W = 250f;

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Win32.MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        // 命中区动作号
        private const int ACT_CLOSE = 1, ACT_SKIP = 2, ACT_PRIMARY = 3, ACT_BACK = 4,
                          ACT_STYLE = 10, ACT_THEME = 11, ACT_MATERIAL = 12,
                          ACT_MONITOR = 13, ACT_MONITOR_DD = 14, ACT_MODE = 15, ACT_AUTOSTART = 16;

        private static OnboardingWindow? _self;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;

        private readonly Win32.WndProc _wndProc;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _memDc = IntPtr.Zero, _hBitmap = IntPtr.Zero, _oldBitmap = IntPtr.Zero, _pBits = IntPtr.Zero;
        private SKSurface? _surface;
        private SKImage? _shadow;
        private int _pxW, _pxH;
        private float _dpi = 1f;

        private bool _running;
        private int _step;                // 0 欢迎 / 1 形态主题 / 2 显示 / 3 自启
        private bool _done;

        // 用户选择（初值取自当前内存里的设置）
        private int _style, _theme, _material, _monitor, _mode;   // mode: 0 待机 / 1 普通
        private bool _autoStart;
        private bool _listOpen;
        private string[] _monitors = ["显示器 1"];

        private readonly List<(SKRect Rect, int Act, int Val)> _hits = new(24);
        private int _hoverAct = -1, _hoverVal = -1;

        private readonly SKPaint _fill = new() { IsAntialias = true };
        private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        private readonly SKPaint _text = new() { IsAntialias = true };

        // ═══════════════════════ 入口 ═══════════════════════

        private static string CurrentVersion =>
            typeof(OnboardingWindow).Assembly.GetName().Version?.ToString() ?? "0";

        private static bool IsFirstRunOfThisVersion()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula", false);
                return (key?.GetValue(RegValue) as string) != CurrentVersion;
            }
            catch
            {
                return true;   // 读不出来就当首次，宁可多问一次
            }
        }

        /// <summary>首次启动（或升级到新版本后第一次启动）时弹引导；`-onboard` 强制弹。阻塞直到引导结束。</summary>
        public static void ShowIfNeeded(string[] args)
        {
            bool force = Array.Exists(args, a => string.Equals(a, "-onboard", StringComparison.OrdinalIgnoreCase));
            if (!force && !IsFirstRunOfThisVersion()) return;

            try
            {
                var w = new OnboardingWindow();
                w.Run();
            }
            catch (Exception ex)
            {
                // 引导起不来绝不能拦着用户用软件：老老实实记一笔，然后直接放行。
                Logger.Error("[引导] 启动失败，跳过引导直接进入软件", ex);
            }
        }

        // ═══════════════════════ 窗口与消息循环 ═══════════════════════

        private OnboardingWindow()
        {
            _self = this;
            _wndProc = _staticWndProc;

            _dpi = Win32.GetDpiForSystem() / 96f;
            _pxW = (int)MathF.Round(WIN_W * _dpi);
            _pxH = (int)MathF.Round(WIN_H * _dpi);

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
                lpszClassName = ClassName,
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };
            Win32.RegisterClass(ref wc);

            var scr = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            int x = scr.X + (scr.Width - _pxW) / 2;
            int y = scr.Y + (scr.Height - _pxH) / 2;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST,
                ClassName, "NotchPeninsula",
                Win32.WS_POPUP, x, y, _pxW, _pxH,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new Exception($"引导窗口创建失败，错误码 {Marshal.GetLastWin32Error()}");

            InitBuffer();
            LoadInitialValues();
            Logger.Info($"[引导] 首次启动引导已开启（版本 {CurrentVersion}）");
        }

        private void LoadInitialValues()
        {
            _style = Math.Clamp(Renderer.NotchStyle, 0, 1);
            _theme = Math.Clamp(Renderer.ThemeMode, 0, 2);
            _material = Renderer.IslandAcrylic ? 1 : 0;
            _mode = Renderer.StandbyActive ? 0 : 1;
            _autoStart = true;   // 引导里默认推荐开启，用户不点就按开启处理
            _monitors = BuildMonitorNames();
            _monitor = Math.Clamp(Renderer.TargetMonitorIndex, 0, _monitors.Length - 1);
        }

        private static string[] BuildMonitorNames()
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (screens.Length == 0) return ["显示器 1"];
                var names = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                {
                    var b = screens[i].Bounds;
                    names[i] = $"显示器 {i + 1}{(screens[i].Primary ? "（主）" : "")} · {b.Width}×{b.Height}";
                }
                return names;
            }
            catch
            {
                return ["显示器 1"];
            }
        }

        private void Run()
        {
            _running = true;
            Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE_NOSIZE);
            Win32.SetForegroundWindow(_hwnd);
            Render();

            // 自建消息循环：**不能用 GetMessage/PostQuitMessage**（WM_QUIT 是线程级的，
            // 会把之后灵动岛那条消息循环一起毒死）。轮询 + 自己置位退出。
            while (_running)
            {
                if (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == Win32.WM_DESTROY || msg.message == 0x0012 /*WM_QUIT*/) { _running = false; break; }
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                else Thread.Sleep(8);
            }

            ReleaseBuffer();
            Logger.Info("[引导] 引导结束，进入灵动岛");
        }

        private void Finish()
        {
            if (_done) return;
            _done = true;

            if (_step == 3 && _autoStart != NotchWindow.IsAutoStartEnabled())
                NotchWindow.ToggleAutoStart(_autoStart, false);

            Program.SaveSetting(RegValue, CurrentVersion);
            Renderer.ApplyThemeColors();   // 引导里改过主题/材质，合上之前重新注入一次颜色
            Logger.Info($"[引导] 已保存偏好：形态={_style} 主题={_theme} 材质={_material} "
                + $"显示器={_monitor} 显示模式={(_mode == 0 ? "待机" : "普通")} 开机自启={_autoStart}");

            _running = false;
            if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var self = _self;
            if (self == null) return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            return self.WndProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Win32.WM_ERASEBKGND:
                        return new IntPtr(1);   // 全自绘，不参与系统擦除

                    case Win32.WM_LBUTTONDOWN:
                        OnClick(lParam);
                        return IntPtr.Zero;

                    case Win32.WM_MOUSEMOVE:
                    {
                        var (act, val) = HitTest(lParam);
                        if (act != _hoverAct || val != _hoverVal)
                        {
                            _hoverAct = act; _hoverVal = val;
                            Render();
                        }
                        var tme = new Win32.TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                            dwFlags = Win32.TME_LEAVE,
                            hwndTrack = hwnd
                        };
                        Win32.TrackMouseEvent(ref tme);
                        return IntPtr.Zero;
                    }

                    case Win32.WM_MOUSELEAVE:
                        if (_hoverAct != -1) { _hoverAct = -1; _hoverVal = -1; Render(); }
                        return IntPtr.Zero;

                    case Win32.WM_SETCURSOR:
                        Win32.SetCursor(_hoverAct != -1
                            ? Win32.LoadCursor(IntPtr.Zero, Win32.IDC_HAND)
                            : Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW));
                        return new IntPtr(1);

                    case Win32.WM_KEYDOWN:
                        if ((int)wParam == Win32.VK_ESCAPE) Finish();
                        else if ((int)wParam == Win32.VK_RETURN || (int)wParam == Win32.VK_SPACE) Advance();
                        return IntPtr.Zero;

                    case Win32.WM_CLOSE:
                        Finish();
                        return IntPtr.Zero;

                    case Win32.WM_DESTROY:
                        _running = false;
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[引导] 处理消息 {msg} 异常", ex);
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // ═══════════════════════ 交互 ═══════════════════════

        private (int Act, int Val) HitTest(IntPtr lParam)
        {
            // ⚠️ 命中表里的矩形是「卡片局部坐标」（绘制前 translate 了 SHADOW），
            //    而鼠标坐标是窗口坐标 —— 这里必须把 SHADOW 减掉，否则所有命中都会偏 26 DIP。
            float x = ((short)((long)lParam & 0xFFFF)) / _dpi - SHADOW;
            float y = ((short)(((long)lParam >> 16) & 0xFFFF)) / _dpi - SHADOW;

            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].Rect.Contains(x, y)) return (_hits[i].Act, _hits[i].Val);

            return (-1, -1);
        }

        private void OnClick(IntPtr lParam)
        {
            var (act, val) = HitTest(lParam);
            if (act == -1) return;

            switch (act)
            {
                case ACT_CLOSE: Finish(); return;
                case ACT_SKIP: Finish(); return;
                case ACT_PRIMARY: Advance(); return;
                case ACT_BACK: if (_step > 0) _step--; break;

                case ACT_STYLE:
                    _style = val;
                    Program.SaveSetting("NotchStyle", _style);
                    break;

                case ACT_THEME:
                    _theme = val;
                    Renderer.ThemeMode = _theme;          // setter 内部会失效系统主题缓存
                    Program.SaveSetting("ThemeMode", _theme);
                    Renderer.ApplyThemeColors();
                    break;

                case ACT_MATERIAL:
                    _material = val;
                    Renderer.IslandAcrylic = _material == 1;
                    Renderer.ApplyThemeColors();          // 岛体涂层厚度要跟着换
                    Program.SaveSetting("IslandAcrylic", Renderer.IslandAcrylic ? 1 : 0);
                    break;

                case ACT_MONITOR:
                    _monitor = val;
                    Renderer.TargetMonitorIndex = _monitor;
                    Program.SaveSetting("TargetMonitorIndex", _monitor);
                    break;

                case ACT_MONITOR_DD:
                    _listOpen = !_listOpen;
                    break;

                case ACT_MODE:
                    _mode = val;
                    if (_mode == 0)
                    {
                        if (!Renderer.StandbyActive)
                        {
                            Renderer.StandbyActive = true;
                            Program.SaveSetting("StandbyActive", 1);
                        }
                        // 待机模式下不给子选项：默认就是媒体控制器（场景 3）
                        Renderer.StandbyScene = 3;
                        Program.SaveSetting("StandbyScene", 3);
                    }
                    else
                    {
                        if (Renderer.StandbyActive)
                        {
                            Renderer.StandbyActive = false;
                            Program.SaveSetting("StandbyActive", 0);
                        }
                    }
                    break;

                case ACT_AUTOSTART:
                    _autoStart = !_autoStart;
                    NotchWindow.ToggleAutoStart(_autoStart, false);
                    break;
            }

            if (act != ACT_MONITOR_DD) _listOpen = false;
            Render();
        }

        private void Advance()
        {
            if (_step >= 3) { Finish(); return; }
            _step++;
            _listOpen = false;
            _hoverAct = -1; _hoverVal = -1;
            Render();
        }

        // ═══════════════════════ 缓冲区 ═══════════════════════

        private void InitBuffer()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            _memDc = Win32.CreateCompatibleDC(screenDc);

            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = _pxW,
                    biHeight = -_pxH,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
            _surface = SKSurface.Create(
                new SKImageInfo(_pxW, _pxH, SKColorType.Bgra8888, SKAlphaType.Premul), _pBits, _pxW * 4);

            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }

        private void ReleaseBuffer()
        {
            _shadow?.Dispose(); _shadow = null;
            _surface?.Dispose(); _surface = null;
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
            _hBitmap = _memDc = _oldBitmap = _pBits = IntPtr.Zero;
        }

        // ═══════════════════════ 配色 ═══════════════════════

        private bool Light => Renderer.ThemeMode == 2 ? Renderer.SystemIsLightTheme : Renderer.ThemeMode == 1;
        private SKColor CardBg => Light ? new SKColor(246, 246, 246) : new SKColor(35, 35, 35);
        private SKColor CardBorder => Light ? new SKColor(214, 214, 214) : new SKColor(58, 58, 58);
        private SKColor Fg => Light ? new SKColor(24, 24, 24) : new SKColor(242, 242, 242);
        private SKColor Sub => Light ? new SKColor(108, 108, 108) : new SKColor(160, 160, 160);
        private SKColor Over(byte a) => Light ? new SKColor(0, 0, 0, a) : new SKColor(255, 255, 255, a);

        private static readonly SKColor Accent = new(0, 120, 212);

        // ═══════════════════════ 渲染 ═══════════════════════

        private void Render()
        {
            if (_surface == null) return;
            var canvas = _surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // 投影是像素级的、且只建一次（每帧新建源图会被 Skia 按 uniqueID 缓存 → 只增不减，见 MEMORY）
            if (_shadow == null) BuildShadow();
            if (_shadow != null) canvas.DrawImage(_shadow, 0, 0);

            canvas.Save();
            canvas.Scale(_dpi);
            canvas.Translate(SHADOW, SHADOW);

            _hits.Clear();
            DrawCard(canvas);
            canvas.Restore();

            Submit();
        }

        private void BuildShadow()
        {
            var info = new SKImageInfo(_pxW, _pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info);
            if (surf == null) return;
            var c = surf.Canvas;
            c.Clear(SKColors.Transparent);

            float b = 9f * _dpi;
            using var filter = SKImageFilter.CreateBlur(b, b, SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, Light ? (byte)70 : (byte)150),
                IsAntialias = true,
                ImageFilter = filter
            };

            var r = SKRect.Create(
                (SHADOW - 2f) * _dpi, (SHADOW + 1f) * _dpi,
                (CARD_W + 4f) * _dpi, (CARD_H + 4f) * _dpi);
            c.DrawRoundRect(r, 14f * _dpi, 14f * _dpi, paint);
            _shadow = surf.Snapshot();
        }

        private void DrawCard(SKCanvas c)
        {
            var card = new SKRect(0, 0, CARD_W, CARD_H);
            _fill.Color = CardBg;
            c.DrawRoundRect(card, 14, 14, _fill);
            _stroke.Color = CardBorder;
            c.DrawRoundRect(card, 14, 14, _stroke);

            DrawProgress(c);
            DrawClose(c);

            switch (_step)
            {
                case 0: DrawWelcome(c); break;
                case 1: DrawAppearance(c); break;
                case 2: DrawDisplay(c); break;
                default: DrawAutoStart(c); break;
            }

            DrawFooter(c);
            DrawDropdownPopup(c);
        }

        private void DrawProgress(SKCanvas c)
        {
            const float gap = 8f;
            float w = (CARD_W - PAD * 2f - gap * 3f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                var r = new SKRect(PAD + i * (w + gap), 54f, PAD + i * (w + gap) + w, 57f);
                _fill.Color = i <= _step ? Accent : Over(18);
                c.DrawRoundRect(r, 1.5f, 1.5f, _fill);
            }
        }

        private void DrawClose(SKCanvas c)
        {
            var r = new SKRect(CARD_W - PAD - 20f, 20f, CARD_W - PAD, 40f);
            _hits.Add((r, ACT_CLOSE, 0));

            bool hover = _hoverAct == ACT_CLOSE;
            if (hover)
            {
                _fill.Color = Over(20);
                c.DrawRoundRect(r, 6, 6, _fill);
            }
            _stroke.Color = hover ? Fg : Sub;
            _stroke.StrokeWidth = 1.4f;
            c.DrawLine(r.Left + 6.5f, r.Top + 6.5f, r.Right - 6.5f, r.Bottom - 6.5f, _stroke);
            c.DrawLine(r.Right - 6.5f, r.Top + 6.5f, r.Left + 6.5f, r.Bottom - 6.5f, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        private void DrawWelcome(SKCanvas c)
        {
            Txt(c, "NotchPeninsula", CARD_W / 2f, 232f, 34f, FontConfig.Bold, Fg, true);
            Txt(c, "灵动岛 · 让刘海成为桌面的信息中枢", CARD_W / 2f, 268f, 14f, FontConfig.Normal, Sub, true);
            Txt(c, "用一分钟完成 3 项基础偏好设置", CARD_W / 2f, 296f, 12.5f, FontConfig.Normal, Sub, true);

            Button(c, new SKRect((CARD_W - 180f) / 2f, 322f, (CARD_W + 180f) / 2f, 322f + 42f),
                "开始设置偏好", true, ACT_PRIMARY, 0);
        }

        private void DrawAppearance(SKCanvas c)
        {
            Header(c, "形态与外观", "挑一个你喜欢的样式，之后随时都能改。");

            DrawPreview(c);

            Row(c, 244f, "形态", ["经典刘海", "悬浮灵动岛"], _style, ACT_STYLE);
            Row(c, 288f, "主题配色", ["黑", "白", "跟随系统"], _theme, ACT_THEME);
            Row(c, 332f, "背景材质", ["实体", "亚克力"], _material, ACT_MATERIAL);
        }

        private void DrawPreview(SKCanvas c)
        {
            float cx = CARD_W / 2f, cy = 200f;
            _fill.Color = Light ? SKColors.White : SKColors.Black;
            if (_material == 1) _fill.Color = _fill.Color.WithAlpha(170);
            _stroke.Color = Over(40);

            if (_style == 0)
            {
                // 经典刘海：描边必须沿同一条路径走，套圆角矩形会画出一圈穿模的框
                using var path = new SKPath();
                path.MoveTo(cx - 70, cy - 18);
                path.QuadTo(cx - 50, cy - 18, cx - 50, cy - 9);
                path.LineTo(cx - 50, cy + 9);
                path.QuadTo(cx - 50, cy + 18, cx - 30, cy + 18);
                path.LineTo(cx + 30, cy + 18);
                path.QuadTo(cx + 50, cy + 18, cx + 50, cy + 9);
                path.LineTo(cx + 50, cy - 9);
                path.QuadTo(cx + 50, cy - 18, cx + 70, cy - 18);
                c.DrawPath(path, _fill);
                c.DrawPath(path, _stroke);
            }
            else
            {
                var cap = new SKRect(cx - 70, cy - 18, cx + 70, cy + 18);
                c.DrawRoundRect(cap, 18, 18, _fill);
                c.DrawRoundRect(cap, 18, 18, _stroke);
            }

            // 里面画几根频谱条，就当"有内容"的暗示
            _fill.Color = Accent;
            for (int i = 0; i < 3; i++)
                c.DrawRoundRect(new SKRect(cx - 14f + i * 10f, cy + 8f - (i + 1) * 5f, cx - 10f + i * 10f, cy + 8f),
                    2, 2, _fill);
        }

        private void DrawDisplay(SKCanvas c)
        {
            Header(c, "显示设置", "决定灵动岛待在哪块屏幕、平时显示什么。");

            // ── 显示模式 ──
            float modeY = 176f;
            Label(c, modeY, "显示模式");
            Seg(c, CTRL_X, modeY + 3f, CTRL_W, 32f, ["待机模式", "普通模式"], _mode, ACT_MODE);
            Txt(c, _mode == 0
                    ? "待机模式下默认显示媒体控制器"
                    : "有媒体播放时自动显示媒体控制器",
                PAD, modeY + 58f, 12f, FontConfig.Normal, Sub);

            // ── 目标显示器（下拉；放在最后一行，展开的列表才落得进下方的空白） ──
            float rowY = 276f;
            Label(c, rowY, "目标显示器");
            if (_monitors.Length == 1)
                Txt(c, "只检测到一块显示器", PAD + 82f, rowY + 24f, 12f, FontConfig.Normal, Sub);

            float dw = 250f, dx = CARD_W - PAD - dw;
            var dd = new SKRect(dx, rowY + 3f, dx + dw, rowY + 35f);
            _hits.Add((dd, ACT_MONITOR_DD, 0));
            _fill.Color = _hoverAct == ACT_MONITOR_DD || _listOpen ? Over(16) : Over(8);
            c.DrawRoundRect(dd, 6, 6, _fill);
            string cur = _monitors[Math.Clamp(_monitor, 0, _monitors.Length - 1)];
            Txt(c, cur, dd.Left + 12f, dd.MidY + 5f, 13f, FontConfig.Normal, Fg);
            _stroke.Color = Sub;
            _stroke.StrokeWidth = 1.5f;
            float ax = dd.Right - 22f, ay = dd.MidY - 2f;
            c.DrawLine(ax, ay, ax + 5f, ay + 5f, _stroke);
            c.DrawLine(ax + 5f, ay + 5f, ax + 10f, ay, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        private void DrawAutoStart(SKCanvas c)
        {
            Header(c, "开机自启", "最后一步了。");

            float rowY = 196f;
            Txt(c, "开机自启", PAD, rowY + 22f, 13.5f, FontConfig.Normal, Fg);
            Txt(c, "跟随系统启动，开机后在后台常驻", PAD, rowY + 44f, 12f, FontConfig.Normal, Sub);
            Toggle(c, rowY + 6f, _autoStart, _hoverAct == ACT_AUTOSTART);

            Txt(c, "之后可随时在设置窗口「通用设置」里更改。", PAD, 330f, 12f, FontConfig.Normal, Sub);
        }

        private void DrawFooter(SKCanvas c)
        {
            _stroke.Color = Over(20);
            c.DrawLine(PAD, SEP_Y, CARD_W - PAD, SEP_Y, _stroke);

            float cy = SEP_Y + (CARD_H - SEP_Y) / 2f;
            float primW = 130f;
            var prim = new SKRect(CARD_W - PAD - primW, cy - BTN_H / 2f, CARD_W - PAD, cy + BTN_H / 2f);

            if (_step > 0)
            {
                var back = new SKRect(prim.Left - 88f, cy - 17f, prim.Left - 12f, cy + 17f);
                Button(c, back, "上一步", false, ACT_BACK, 0);
            }

            if (_step > 0)
                Button(c, prim, _step == 3 ? "开始使用" : "下一步", true, ACT_PRIMARY, 0);

            if (_step < 3)
            {
                var skip = new SKRect(PAD, cy - 17f, PAD + 88f, cy + 17f);
                Button(c, skip, "跳过引导", false, ACT_SKIP, 0);
            }
        }

        private void DrawDropdownPopup(SKCanvas c)
        {
            if (!_listOpen || _monitors.Length == 0) return;

            float dw = 250f, dx = CARD_W - PAD - dw;
            float top = 276f + 3f + 34f + 4f;
            float itemH = 30f;
            float h = itemH * _monitors.Length;

            var box = new SKRect(dx, top, dx + dw, top + h);
            _fill.Color = CardBg;
            c.DrawRoundRect(box, 6, 6, _fill);
            _stroke.Color = CardBorder;
            c.DrawRoundRect(box, 6, 6, _stroke);
            // 下拉要盖住下面的内容，所以最后加进命中表（命中从后往前扫）
            for (int i = 0; i < _monitors.Length; i++)
            {
                var r = new SKRect(dx, top + i * itemH, dx + dw, top + (i + 1) * itemH);
                var inner = new SKRect(r.Left + 3f, r.Top + 2f, r.Right - 3f, r.Bottom - 2f);
                _hits.Add((r, ACT_MONITOR, i));
                if (i == _monitor)
                {
                    _fill.Color = Accent;
                    c.DrawRoundRect(inner, 4, 4, _fill);
                }
                else if (_hoverAct == ACT_MONITOR && _hoverVal == i)
                {
                    _fill.Color = Over(20);
                    c.DrawRoundRect(inner, 4, 4, _fill);
                }
                Txt(c, _monitors[i], r.Left + 12f, r.MidY + 5f, 12.5f, FontConfig.Normal,
                    i == _monitor ? SKColors.White : Fg);
            }
        }

        // ═══════════════════════ 基础控件 ═══════════════════════

        private void Header(SKCanvas c, string title, string subtitle)
        {
            Txt(c, title, PAD, 96f, 21f, FontConfig.SemiBold, Fg);
            Txt(c, subtitle, PAD, 124f, 12.5f, FontConfig.Normal, Sub);
        }

        private void Label(SKCanvas c, float rowY, string text)
            => Txt(c, text, PAD, rowY + 24f, 13.5f, FontConfig.Normal, Fg);

        private void Row(SKCanvas c, float rowY, string label, string[] labels, int sel, int act)
        {
            Label(c, rowY, label);
            Seg(c, CTRL_X, rowY + 3f, CTRL_W, 32f, labels, sel, act);
        }

        private void Seg(SKCanvas c, float x, float y, float w, float h, string[] labels, int sel, int act)
        {
            float sw = w / labels.Length;
            _fill.Color = Over(8);
            c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 6, 6, _fill);

            for (int i = 0; i < labels.Length; i++)
            {
                var r = new SKRect(x + i * sw + (i > 0 ? 2f : 0f), y, x + (i + 1) * sw - (i < labels.Length - 1 ? 2f : 0f), y + h);
                _hits.Add((r, act, i));

                bool selected = i == sel;
                if (selected)
                {
                    _fill.Color = Accent;
                    c.DrawRoundRect(r, 5, 5, _fill);
                }
                else if (_hoverAct == act && _hoverVal == i)
                {
                    _fill.Color = Over(24);
                    c.DrawRoundRect(r, 5, 5, _fill);
                }

                Txt(c, labels[i], r.MidX, r.MidY + 5.5f, 13f, FontConfig.Normal,
                    selected ? SKColors.White : Fg, true);
            }
        }

        private void Button(SKCanvas c, SKRect r, string label, bool primary, int act, int val)
        {
            _hits.Add((r, act, val));
            bool hover = _hoverAct == act && _hoverVal == val;

            if (primary)
            {
                _fill.Color = hover ? new SKColor(0, 140, 240) : Accent;
                c.DrawRoundRect(r, 7, 7, _fill);
            }
            else
            {
                _fill.Color = hover ? Over(22) : Over(10);
                c.DrawRoundRect(r, 7, 7, _fill);
            }

            Txt(c, label, r.MidX, r.MidY + 5.5f, 13.5f, FontConfig.SemiBold,
                primary ? SKColors.White : Fg, true);
        }

        private void Toggle(SKCanvas c, float y, bool state, bool hover)
        {
            float h = 22f, w = 42f;
            var r = new SKRect(CARD_W - PAD - w, y + 19f - h / 2f, CARD_W - PAD, y + 19f + h / 2f);
            _hits.Add((r, ACT_AUTOSTART, 0));

            if (state)
            {
                _fill.Color = hover ? new SKColor(0, 140, 240) : Accent;
                c.DrawRoundRect(r, h / 2, h / 2, _fill);
            }
            else
            {
                _stroke.Color = hover ? Over(150) : Over(90);
                _stroke.StrokeWidth = 1.5f;
                c.DrawRoundRect(r, h / 2, h / 2, _stroke);
                _stroke.StrokeWidth = 1f;
            }

            _fill.Color = state ? SKColors.White : Over(hover ? (byte)220 : (byte)160);
            float cx = state ? r.Right - h / 2f : r.Left + h / 2f;
            c.DrawCircle(cx, r.MidY, h / 2f - 4f, _fill);
        }

        private void Txt(SKCanvas c, string s, float x, float y, float size, SKTypeface face, SKColor color, bool center = false)
        {
            _text.Typeface = face;
            _text.TextSize = size;
            _text.Color = color;
            c.DrawText(s, center ? x - _text.MeasureText(s) / 2f : x, y, _text);
        }

        // ═══════════════════════ 提交 ═══════════════════════

        private void Submit()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;
            try
            {
                Win32.GetWindowRect(_hwnd, out var rect);
                var ptDst = new Win32.POINT { x = rect.Left, y = rect.Top };
                var ptSrc = new Win32.POINT(0, 0);
                var size = new Win32.SIZE(_pxW, _pxH);
                var blend = new Win32.BLENDFUNCTION
                {
                    BlendOp = Win32.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Win32.AC_SRC_ALPHA
                };
                Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
