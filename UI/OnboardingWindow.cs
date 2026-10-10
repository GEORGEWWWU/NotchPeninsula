using System.Runtime.InteropServices;
using System.IO;
using SkiaSharp;
using Microsoft.Win32;

namespace NotchPeninsula
{
    /// <summary>
    /// 首次启动引导：灵动岛起来之前跑一遍的独立向导窗口（自绘 layered 窗口，圆角 + 投影，横版）。
    /// 写入的注册表键与设置窗口完全一致，所以"引导里设置过" == "在设置窗口里设置过"，
    /// 之后随便在设置窗口改都互不冲突。不碰任何岛体核心代码。
    ///
    /// 防卡死：整个向导跑在自己的 STA 后台线程上，主线程只做「等 + 看门狗」——
    /// ① 窗口迟迟建不出来、② 心跳停摆（消息循环/绘制卡住）、③ 线程内任何未捕获异常，
    /// 三种情况都会立刻放弃引导、继续正常启动流程。IsBackground 保证即使线程真的死住也不拦进程退出。
    /// </summary>
    internal sealed class OnboardingWindow
    {
        private const string ClassName = "NPSOnboardingClass";
        private const string RegValue = "OnboardedVersion";
        private const int PM_REMOVE = 0x0001;
        private const uint WM_APP_THEME = 0x8001;      // 系统明暗变化 → 本窗口重绘

        // 看门狗口径
        private const int HeartbeatStuckMs = 12000;    // 心跳停这么久 = 判定卡死
        private const int WindowCreateBudgetMs = 8000; // 窗口建不出来就给这么多时间

        // ---- 版式（DIP；卡片左上角为原点，窗口四周留 SHADOW 给投影）----
        private const float CARD_W = 700f;             // 横版：宽 > 高
        private const float CARD_H = 400f;
        private const float PAD = 34f;
        private const float SHADOW = 28f;
        private const float WIN_W = CARD_W + SHADOW * 2f;
        private const float WIN_H = CARD_H + SHADOW * 2f;

        private const float BODY_TOP = 146f;           // 内容区首行顶
        private const float ROW_GAP = 48f;
        private const float CTRL_H = 36f;
        private const float CTRL_X = CARD_W - PAD - 300f;   // 控件统一右对齐
        private const float CTRL_W = 300f;
        private const float FOOT_CY = 322f;            // 底部按钮行中心
        private const float BTN_H = 40f;
        private const float PROG_CY = 368f;            // 进度指示（卡片底部居中）

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Win32.MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        // 命中区动作号
        private const int ACT_CLOSE = 1, ACT_SKIP = 2, ACT_PRIMARY = 3, ACT_BACK = 4,
                          ACT_STYLE = 10, ACT_THEME = 11, ACT_MATERIAL = 12,
                          ACT_MONITOR = 13, ACT_MONITOR_DD = 14, ACT_MODE = 15, ACT_AUTOSTART = 16;

        private static OnboardingWindow? _self;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;

        // ── 看门狗状态（主线程读，引导线程写）──
        private static long _heartbeat;
        private static volatile bool _windowAlive;
        private static volatile bool _aborted;
        private static IntPtr _hwndShared;
        private static int _exitCode;                  // 0 正常 / 1 崩溃 / 2 卡死 / 3 建窗失败
        private static int _selfTest;                  // 仅供回归测试：1 构造崩溃 / 2 活窗死循环 / 3 建窗前死循环

        private readonly Win32.WndProc _wndProc;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _memDc = IntPtr.Zero, _hBitmap = IntPtr.Zero, _oldBitmap = IntPtr.Zero, _pBits = IntPtr.Zero;
        private SKSurface? _surface;
        private SKImage? _shadow;
        private bool _shadowLight;
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

        // 动画
        private long _introStart, _stepStart, _lastFrame;
        private float _introT, _stepT;
        private readonly float[] _segNow = new float[32];
        private readonly float[] _segDst = new float[32];
        private readonly bool[] _segInit = new bool[32];
        private float _introEase = 1f, _stepEase = 1f;
        private float _mul = 1f;

        private readonly SKPaint _fill = new() { IsAntialias = true };
        private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        private readonly SKPaint _text = new() { IsAntialias = true };
        private readonly SKPaint _imgPaint = new() { IsAntialias = true };
        private SKImage? _logoTileImg;
        private SKBitmap? _logoBmp;                    // FromBitmap 的像素后端，随 image 一起释放

        // ═══════════════════════ 入口（含看门狗）═══════════════════════

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

        /// <summary>
        /// 首次启动（或升级到新版本后第一次启动）时弹引导；`-onboard` 强制弹。
        /// 引导跑在自己的后台线程上，主线程只负责等；任何异常 / 卡死都不会拖住启动流程。
        /// </summary>
        public static void ShowIfNeeded(string[] args)
        {
            bool force;
            try { force = Array.Exists(args, a => string.Equals(a, "-onboard", StringComparison.OrdinalIgnoreCase)); }
            catch { force = false; }

            // 自测开关：只在显式传参时生效，正常运行永远走不到这三条路。
            //   -onboard-selftest=crash      构造阶段抛异常（验证"崩溃也照样进软件"）
            //   -onboard-selftest=hang       窗口已显示后死循环（验证心跳看门狗）
            //   -onboard-selftest=nocreate   建窗前死循环（验证建窗预算）
            if (force)
            {
                foreach (var a in args)
                {
                    if (a == "-onboard-selftest=crash") _selfTest = 1;
                    else if (a == "-onboard-selftest=hang") _selfTest = 2;
                    else if (a == "-onboard-selftest=nocreate") _selfTest = 3;
                }
                if (_selfTest != 0) force = true;   // 自测必须绕过"首次运行"判定
            }

            try
            {
                if (!force && !IsFirstRunOfThisVersion()) return;
            }
            catch (Exception ex)
            {
                Logger.Error("[引导] 首次运行判定失败，跳过引导", ex);
                return;
            }

            _heartbeat = Environment.TickCount64;   // 给构造阶段一段宽限
            var th = new Thread(ThreadBody) { IsBackground = true, Name = "NPS-Onboarding" };
            try { th.SetApartmentState(ApartmentState.STA); }
            catch (Exception ex) { Logger.Error("[引导] 无法设置 STA，跳过引导", ex); return; }

            try { th.Start(); }
            catch (Exception ex) { Logger.Error("[引导] 线程启动失败，跳过引导", ex); return; }

            // ── 看门狗：只等，不参与，绝不阻塞启动 ──
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (th.Join(400)) return;   // 正常收工

                    long idle = Environment.TickCount64 - Volatile.Read(ref _heartbeat);

                    if (!_windowAlive && clock.ElapsedMilliseconds > WindowCreateBudgetMs)
                    {
                        _aborted = true;
                        _exitCode = 3;
                        Logger.Error($"[引导] 窗口 {WindowCreateBudgetMs}ms 内未创建成功，判定引导不可用，直接进入软件");
                        if (!th.Join(1200)) Logger.Error("[引导] 构造线程仍未退出（后台线程，不阻塞启动）");
                        return;
                    }

                    if (_windowAlive && idle > HeartbeatStuckMs)
                    {
                        _exitCode = 2;
                        Logger.Error($"[引导] {idle}ms 无响应（疑似卡死），强制关闭引导并进入软件");
                        IntPtr h = _hwndShared;
                        if (h != IntPtr.Zero) Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        if (!th.Join(2000)) Logger.Error("[引导] 卡死线程未退出（后台线程，不阻塞启动）");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // 看门狗自己出错也绝不能拦住启动
                Logger.Error("[引导] 看门狗异常，跳过引导", ex);
            }
        }

        private static void ThreadBody()
        {
            try
            {
                var w = new OnboardingWindow();
                if (_aborted) { w.ForceClose(); return; }
                w.Run();
            }
            catch (Exception ex)
            {
                _exitCode = 1;
                Logger.Error("[引导] 运行异常，跳过引导直接进入软件", ex);
                try { _self?.ForceClose(); } catch { /* 收尾失败也不许再抛 */ }
                _windowAlive = false;
            }
        }

        // ═══════════════════════ 窗口与消息循环 ═══════════════════════

        private OnboardingWindow()
        {
            _self = this;
            if (_selfTest == 1) throw new Exception("自测：故意让引导构造崩溃");
            _wndProc = _staticWndProc;
            Beat();
            if (_selfTest == 3) { Logger.Error("[引导] 自测：建窗前死循环"); while (true) Thread.Sleep(1000); }

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
            Beat();

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

            _hwndShared = _hwnd;
            _windowAlive = true;
            Beat();

            InitBuffer();
            LoadInitialValues();
            TryHookSystemTheme();
            Logger.Info($"[引导] 首次启动引导已开启（版本 {CurrentVersion}，卡片 {CARD_W:0}×{CARD_H:0}）");
        }

        private static void Beat() => Volatile.Write(ref _heartbeat, Environment.TickCount64);

        private void TryHookSystemTheme()
        {
            try { SystemEvents.UserPreferenceChanged += OnSystemThemeChanged; }
            catch (Exception ex) { Logger.Error("[引导] 订阅系统主题变化失败（不影响使用）", ex); }
        }

        private static void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 事件来自 SystemEvents 自己的线程：只投递消息，绝不跨线程碰窗口
            IntPtr h = _hwndShared;
            if (h == IntPtr.Zero) return;
            try { Win32.PostMessage(h, WM_APP_THEME, IntPtr.Zero, IntPtr.Zero); }
            catch { /* 引导已在收尾就忽略 */ }
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
            Beat();
            Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE_NOSIZE);
            Win32.SetForegroundWindow(_hwnd);

            _introStart = Environment.TickCount64;
            _stepStart = _introStart;
            _lastFrame = _introStart;
            Render();

            if (_selfTest == 2) { Logger.Error("[引导] 自测：窗口已显示后死循环（心跳停摆）"); while (true) Thread.Sleep(1000); }

            // 自建消息循环：**不能用 GetMessage/PostQuitMessage**（WM_QUIT 是线程级的，
            // 会把之后灵动岛那条消息循环一起毒死）。轮询 + 自己置位退出。
            while (_running)
            {
                Beat();

                long now = Environment.TickCount64;
                float dt = Math.Clamp(now - _lastFrame, 0, 100);
                _lastFrame = now;
                if (AdvanceAnimation(dt)) Render();

                if (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == Win32.WM_DESTROY || msg.message == 0x0012 /*WM_QUIT*/) { _running = false; break; }
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                else Thread.Sleep(6);
            }

            ReleaseBuffer();
            if (_exitCode == 0) Logger.Info("[引导] 引导结束，进入灵动岛");
        }

        /// <summary>推进入场 / 步骤切换 / 分段器滑块动画；返回 true 表示这一帧需要重绘。</summary>
        private bool AdvanceAnimation(float dt)
        {
            bool need = false;
            long now = Environment.TickCount64;

            if (_introT < 1f)
            {
                _introT = Math.Min(1f, (now - _introStart) / 260f);
                need = true;
            }
            if (_stepT < 1f)
            {
                _stepT = Math.Min(1f, (now - _stepStart) / 190f);
                need = true;
            }

            for (int i = 0; i < _segNow.Length; i++)
            {
                if (!_segInit[i]) continue;
                float d = _segDst[i] - _segNow[i];
                if (MathF.Abs(d) < 0.002f) { _segNow[i] = _segDst[i]; continue; }
                _segNow[i] += d * (1f - MathF.Exp(-dt / 42f));
                need = true;
            }

            return need;
        }

        private void Finish()
        {
            if (_done) return;
            _done = true;

            try { SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged; } catch { }

            try
            {
                if (_step == 3 && _autoStart != NotchWindow.IsAutoStartEnabled())
                    NotchWindow.ToggleAutoStart(_autoStart, false);

                Program.SaveSetting(RegValue, CurrentVersion);
                Renderer.ApplyThemeColors();   // 引导里改过主题/材质，合上之前重新注入一次颜色
                Logger.Info($"[引导] 已保存偏好：形态={_style} 主题={_theme} 材质={_material} "
                    + $"显示器={_monitor} 显示模式={(_mode == 0 ? "待机" : "普通")} 开机自启={_autoStart}");
            }
            catch (Exception ex)
            {
                Logger.Error("[引导] 保存偏好失败（已跳过引导，不影响启动）", ex);
            }

            _running = false;
            if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
        }

        /// <summary>看门狗 / 异常路径用：不做任何注册表写入，只把窗口和循环收掉。</summary>
        private void ForceClose()
        {
            _done = true;
            _running = false;
            try { SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged; } catch { }
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

                    case WM_APP_THEME:
                        // 系统明暗变了：让 Renderer 丢掉主题缓存后重绘（跟随系统模式下窗口就地换亮暗）
                        Renderer.InvalidateSystemThemeCache();
                        Render();
                        return IntPtr.Zero;

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
                        // 看门狗发的也是这一条：正常路径写注册表，被放弃时只收窗口
                        if (_aborted) ForceClose(); else Finish();
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
            //    而鼠标坐标是窗口坐标 —— 这里必须把 SHADOW 减掉，否则所有命中都会偏 28 DIP。
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
                case ACT_BACK: if (_step > 0) { _step--; OnStepChanged(); } break;

                case ACT_STYLE:
                    _style = val;
                    Program.SaveSetting("NotchStyle", _style);
                    break;

                case ACT_THEME:
                    _theme = val;
                    Renderer.ThemeMode = _theme;          // setter 内部会失效系统主题缓存
                    Program.SaveSetting("ThemeMode", _theme);
                    Renderer.ApplyThemeColors();
                    InvalidateShadow();                    // 明暗换 → 投影深浅跟着换
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

            if (act >= ACT_STYLE && act <= ACT_MODE) _segDst[act] = val;

            if (act != ACT_MONITOR_DD) _listOpen = false;
            Render();
        }

        private void Advance()
        {
            if (_step >= 3) { Finish(); return; }
            _step++;
            OnStepChanged();
        }

        private void OnStepChanged()
        {
            _stepStart = Environment.TickCount64;
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
            _logoTileImg?.Dispose(); _logoTileImg = null;
            _logoBmp?.Dispose(); _logoBmp = null;
            _surface?.Dispose(); _surface = null;
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
            _hBitmap = _memDc = _oldBitmap = _pBits = IntPtr.Zero;
        }

        private void InvalidateShadow() { _shadow?.Dispose(); _shadow = null; }

        // ═══════════════════════ 配色（Apple 风：纯色卡片，靠层次和留白而不是花哨）═══════════════════════

        private bool Light => Renderer.ThemeMode == 2 ? Renderer.SystemIsLightTheme : Renderer.ThemeMode == 1;

        private SKColor CardBg => Light ? new SKColor(255, 255, 255) : new SKColor(28, 28, 30);
        private SKColor CardBorder => Light ? new SKColor(0, 0, 0, 16) : new SKColor(255, 255, 255, 24);
        private SKColor Fg => Light ? new SKColor(29, 29, 31) : new SKColor(245, 245, 247);
        private SKColor Sub => Light ? new SKColor(110, 110, 115) : new SKColor(152, 152, 157);
        private SKColor Faint => Light ? new SKColor(0, 0, 0, 90) : new SKColor(255, 255, 255, 80);
        private SKColor Accent => Light ? new SKColor(0, 122, 255) : new SKColor(10, 132, 255);
        private SKColor AccentHot => Light ? new SKColor(20, 138, 255) : new SKColor(48, 154, 255);
        private SKColor Track => Light ? new SKColor(120, 120, 128, 32) : new SKColor(120, 120, 128, 56);
        private SKColor Thumb => Light ? SKColors.White : new SKColor(99, 99, 102);
        private SKColor SoftFill => Light ? new SKColor(0, 0, 0, 14) : new SKColor(255, 255, 255, 20);
        private SKColor SoftFillHot => Light ? new SKColor(0, 0, 0, 26) : new SKColor(255, 255, 255, 34);
        private SKColor Tile => Light ? new SKColor(120, 120, 128, 22) : new SKColor(120, 120, 128, 40);

        private SKColor M(SKColor c) => c.WithAlpha((byte)Math.Clamp(c.Alpha * _mul, 0f, 255f));

        // ═══════════════════════ 渲染 ═══════════════════════

        private static float EaseOut(float t) => 1f - MathF.Pow(1f - t, 3f);

        private void Render()
        {
            if (_surface == null) return;
            Beat();

            _introEase = EaseOut(_introT);
            _stepEase = EaseOut(_stepT);

            var canvas = _surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // 投影是像素级的、且**只建一次**（每帧新建源图会被 Skia 按 uniqueID 缓存 → 只增不减，见 MEMORY）
            if (_shadow == null || _shadowLight != Light) { _shadow?.Dispose(); _shadow = null; BuildShadow(); }
            if (_shadow != null)
            {
                _imgPaint.Color = new SKColor(255, 255, 255, (byte)(255 * _introEase));
                canvas.DrawImage(_shadow, 0, 0, _imgPaint);
            }

            canvas.Save();
            canvas.Scale(_dpi);

            // 入场：以卡片中心为轴轻微放大 + 淡入
            float sc = 0.986f + 0.014f * _introEase;
            float px = SHADOW + CARD_W / 2f, py = SHADOW + CARD_H / 2f;
            canvas.Translate(px, py);
            canvas.Scale(sc);
            canvas.Translate(-px, -py);
            canvas.Translate(SHADOW, SHADOW);

            _hits.Clear();
            DrawCard(canvas);
            canvas.Restore();

            Submit();
        }

        private void BuildShadow()
        {
            _shadowLight = Light;   // 建失败也不每帧重试（省得反复新建 surface）
            var info = new SKImageInfo(_pxW, _pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info);
            if (surf == null) return;
            var c = surf.Canvas;
            c.Clear(SKColors.Transparent);

            float b = 16f * _dpi;
            using var filter = SKImageFilter.CreateBlur(b, b, SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, Light ? (byte)64 : (byte)180),
                IsAntialias = true,
                ImageFilter = filter
            };

            var r = SKRect.Create(
                (SHADOW - 4f) * _dpi, (SHADOW + 3f) * _dpi,
                (CARD_W + 8f) * _dpi, (CARD_H + 8f) * _dpi);
            c.DrawRoundRect(r, 18f * _dpi, 18f * _dpi, paint);
            _shadow = surf.Snapshot();
        }

        private void DrawCard(SKCanvas c)
        {
            var card = new SKRect(0, 0, CARD_W, CARD_H);

            _mul = _introEase;
            RoundFill(c, card, 18f, CardBg);
            if (!Light) Line(c, 22f, 0.5f, CARD_W - 22f, 0.5f, new SKColor(255, 255, 255, 12), 1f);
            RoundStroke(c, card, 18f, CardBorder, 1f);

            DrawClose(c);
            DrawProgress(c);
            DrawFooter(c);

            // 内容区参与步骤切换的淡入 + 轻微上移；命中表用的是卡片逻辑坐标，不受位移影响
            _mul = _introEase * _stepEase;
            c.Save();
            c.Translate(0f, (1f - _stepEase) * 10f);
            switch (_step)
            {
                case 0: DrawWelcome(c); break;
                case 1: DrawAppearance(c); break;
                case 2: DrawDisplay(c); break;
                default: DrawAutoStart(c); break;
            }
            c.Restore();

            DrawDropdownPopup(c);   // 最后画：下拉要盖住内容
            _mul = 1f;
        }

        // ═══════════════════════ 卡片骨架部件 ═══════════════════════

        private void DrawClose(SKCanvas c)
        {
            _mul = _introEase;
            var r = new SKRect(PAD - 10f, 16f, PAD + 14f, 40f);   // 左上角（macOS 习惯）
            _hits.Add((r, ACT_CLOSE, 0));

            bool hover = _hoverAct == ACT_CLOSE;
            if (hover) RoundFill(c, r, 12f, SoftFillHot);

            _stroke.Color = M(hover ? Fg : Sub);
            _stroke.StrokeWidth = 1.5f;
            float cx = r.MidX, cy = r.MidY, d = 4.5f;
            c.DrawLine(cx - d, cy - d, cx + d, cy + d, _stroke);
            c.DrawLine(cx + d, cy - d, cx - d, cy + d, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        private void DrawProgress(SKCanvas c)
        {
            _mul = _introEase;
            const float w = 26f, h = 4f, gap = 6f;
            float total = w * 4f + gap * 3f;
            float x = (CARD_W - total) / 2f, y = PROG_CY - h / 2f;
            for (int i = 0; i < 4; i++)
            {
                var col = i < _step ? Accent.WithAlpha(110) : i == _step ? Accent : Track;
                RoundFill(c, new SKRect(x + i * (w + gap), y, x + i * (w + gap) + w, y + h), h / 2f, col);
            }
        }

        private void DrawFooter(SKCanvas c)
        {
            if (_step == 0) return;   // 欢迎页只要 X 和主按钮，不要分割线也不要跳过

            _mul = _introEase;
            Line(c, PAD, FOOT_CY - BTN_H / 2f - 22f, CARD_W - PAD, FOOT_CY - BTN_H / 2f - 22f,
                Light ? new SKColor(0, 0, 0, 14) : new SKColor(255, 255, 255, 20), 1f);

            float primW = 132f;
            var prim = new SKRect(CARD_W - PAD - primW, FOOT_CY - BTN_H / 2f, CARD_W - PAD, FOOT_CY + BTN_H / 2f);
            Button(c, prim, _step == 3 ? "开始使用" : "下一步", true, ACT_PRIMARY, 0);

            var back = new SKRect(prim.Left - 96f, FOOT_CY - BTN_H / 2f + 2f, prim.Left - 14f, FOOT_CY + BTN_H / 2f - 2f);
            Button(c, back, "上一步", false, ACT_BACK, 0);

            var skip = new SKRect(PAD, FOOT_CY - BTN_H / 2f + 4f, PAD + 92f, FOOT_CY + BTN_H / 2f - 4f);
            Button(c, skip, "跳过引导", false, ACT_SKIP, 0);
        }

        // ═══════════════════════ 各步内容 ═══════════════════════

        private void DrawWelcome(SKCanvas c)
        {
            float cx = CARD_W / 2f;

            DrawLogo(c, cx - 28f, 92f, 56f);

            Txt(c, "NotchPeninsula", cx, 202f, 34f, FontConfig.Bold, Fg, true);
            Txt(c, "让刘海成为桌面的信息中枢", cx, 234f, 15f, FontConfig.Normal, Sub, true);
            Txt(c, "用一分钟完成 3 项基础偏好设置", cx, 260f, 12.5f, FontConfig.Normal, Faint, true);

            Button(c, new SKRect(cx - 104f, 290f, cx + 104f, 334f), "开始设置偏好", true, ACT_PRIMARY, 0);
        }

        private void DrawLogo(SKCanvas c, float x, float y, float size)
        {
            if (_logoTileImg == null) BuildLogoTile();
            if (_logoTileImg == null) return;
            _imgPaint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(255 * _mul, 0f, 255f));
            c.DrawImage(_logoTileImg, new SKRect(x, y, x + size, y + size), _imgPaint);
        }

        /// <summary>
        /// 欢迎页 logo = 程序自己的 .ico（DataResources 磁盘优先→嵌入兜底）。
        /// 本 ico 单帧 256×256 PNG（四角透明），把帧数据直接喂给 SKBitmap.Decode 即可。
        /// 存成 SKImage 是为了让入场淡入走 DrawImage 的 paint alpha（带 shader 时 paint.Color 会被忽略）。
        /// </summary>
        private void BuildLogoTile()
        {
            try
            {
                byte[] ico;
                using (var s = DataResources.OpenRead("NPS_NotchPeninsula-logo.ico"))
                {
                    if (s == null) return;
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ico = ms.ToArray();
                }

                SKBitmap? bmp = null;
                if (ico.Length > 22 && ico[0] == 0 && ico[1] == 0 && ico[2] == 1 && ico[3] == 0)
                {
                    int cnt = BitConverter.ToUInt16(ico, 4);
                    for (int i = 0; i < cnt; i++)                       // 逐帧找 PNG 帧，第一个就够
                    {
                        int off = 6 + i * 16;
                        uint sz = BitConverter.ToUInt32(ico, off + 8);
                        uint fo = BitConverter.ToUInt32(ico, off + 12);
                        if (fo + sz > ico.Length || sz < 8) continue;
                        if (ico[fo] == 0x89 && ico[fo + 1] == (byte)'P')
                        {
                            var frame = new byte[sz];
                            Array.Copy(ico, (long)fo, frame, 0, (long)sz);
                            bmp = SKBitmap.Decode(frame);
                            if (bmp != null) break;
                        }
                    }
                }
                bmp ??= SKBitmap.Decode(ico);                           // 兜底：整个文件就是裸 PNG
                if (bmp == null) return;

                _logoBmp = bmp;
                _logoTileImg = SKImage.FromBitmap(bmp);
            }
            catch { }
        }

        private void DrawAppearance(SKCanvas c)
        {
            Header(c, "形态与外观", "挑一个你喜欢的样式，之后随时都能改。");

            // 右上角留白正好放一块小预览，改形态/材质立刻能看到
            var pv = new SKRect(CARD_W - PAD - 180f, 58f, CARD_W - PAD, 124f);
            RoundFill(c, pv, 14f, Tile);
            DrawPreview(c, pv);

            Row(c, BODY_TOP, "形态", ["经典刘海", "悬浮灵动岛"], _style, ACT_STYLE, CTRL_X, CTRL_W);
            Row(c, BODY_TOP + ROW_GAP, "主题配色", ["黑", "白", "跟随系统"], _theme, ACT_THEME, CTRL_X, CTRL_W);
            Row(c, BODY_TOP + ROW_GAP * 2f, "背景材质", ["实体", "亚克力"], _material, ACT_MATERIAL, CTRL_X, CTRL_W);
        }

        private void DrawPreview(SKCanvas c, SKRect box)
        {
            float cx = box.MidX, cy = box.MidY;

            _fill.Color = M(Light ? SKColors.White : SKColors.Black);
            if (_material == 1) _fill.Color = _fill.Color.WithAlpha((byte)(_fill.Color.Alpha * 0.62f));
            _stroke.Color = M(Light ? new SKColor(0, 0, 0, 34) : new SKColor(255, 255, 255, 46));
            _stroke.StrokeWidth = 1f;

            float hw = 58f, hh = 15f;
            if (_style == 0)
            {
                // 经典刘海：描边必须沿同一条路径走，套圆角矩形会画出一圈穿模的框
                using var path = new SKPath();
                path.MoveTo(cx - hw, cy - hh);
                path.QuadTo(cx - hw + 16f, cy - hh, cx - hw + 16f, cy - hh / 2f);
                path.LineTo(cx - hw + 16f, cy + hh / 2f);
                path.QuadTo(cx - hw + 16f, cy + hh, cx - hw + 34f, cy + hh);
                path.LineTo(cx + hw - 34f, cy + hh);
                path.QuadTo(cx + hw - 16f, cy + hh, cx + hw - 16f, cy + hh / 2f);
                path.LineTo(cx + hw - 16f, cy - hh / 2f);
                path.QuadTo(cx + hw - 16f, cy - hh, cx + hw, cy - hh);
                c.DrawPath(path, _fill);
                c.DrawPath(path, _stroke);
            }
            else
            {
                var cap = new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
                c.DrawRoundRect(cap, hh, hh, _fill);
                c.DrawRoundRect(cap, hh, hh, _stroke);
            }

            // 几根频谱条，就当"有内容"的暗示
            _fill.Color = M(Accent);
            for (int i = 0; i < 3; i++)
                c.DrawRoundRect(new SKRect(cx - 10f + i * 8f, cy + 6f - (i + 1) * 4f, cx - 6f + i * 8f, cy + 6f),
                    2, 2, _fill);
        }

        private void DrawDisplay(SKCanvas c)
        {
            Header(c, "显示设置", "决定灵动岛待在哪块屏幕、平时显示什么。");

            // ── 目标显示器放前面：下拉往下展开时才不会压到按钮行 ──
            float rowY = BODY_TOP;
            Label(c, rowY, "目标显示器");
            if (_monitors.Length == 1)
                Txt(c, "只检测到一块显示器", PAD + 92f, rowY + 23f, 12f, FontConfig.Normal, Faint);

            var dd = new SKRect(CTRL_X, rowY + 2f, CTRL_X + CTRL_W, rowY + 2f + CTRL_H);
            _hits.Add((dd, ACT_MONITOR_DD, 0));
            RoundFill(c, dd, 10f, _hoverAct == ACT_MONITOR_DD || _listOpen ? SoftFillHot : SoftFill);
            Txt(c, _monitors[Math.Clamp(_monitor, 0, _monitors.Length - 1)],
                dd.Left + 14f, dd.MidY + 5f, 13f, FontConfig.Normal, Fg);

            _stroke.Color = M(Sub);
            _stroke.StrokeWidth = 1.6f;
            float ax = dd.Right - 26f, ay = dd.MidY - 2.5f;
            c.DrawLine(ax, ay, ax + 5f, ay + 5f, _stroke);
            c.DrawLine(ax + 5f, ay + 5f, ax + 10f, ay, _stroke);
            _stroke.StrokeWidth = 1f;

            // ── 显示模式 ──
            float modeY = BODY_TOP + 60f;
            Row(c, modeY, "显示模式", ["待机模式", "普通模式"], _mode, ACT_MODE, CTRL_X, CTRL_W);
            Txt(c, _mode == 0 ? "待机模式下默认显示媒体控制器" : "有媒体播放时自动显示媒体控制器",
                CTRL_X, modeY + CTRL_H + 20f, 12f, FontConfig.Normal, Faint);
        }

        private void DrawDropdownPopup(SKCanvas c)
        {
            if (!_listOpen || _monitors.Length == 0) return;

            float top = BODY_TOP + 2f + CTRL_H + 6f;
            float itemH = 34f;
            float h = itemH * _monitors.Length;

            var box = new SKRect(CTRL_X, top, CTRL_X + CTRL_W, top + h);
            RoundFill(c, box, 10f, Light ? new SKColor(255, 255, 255) : new SKColor(44, 44, 46));
            RoundStroke(c, box, 10f, CardBorder, 1f);

            // 下拉要盖住下面的内容，所以最后加进命中表（命中从后往前扫）
            for (int i = 0; i < _monitors.Length; i++)
            {
                var r = new SKRect(CTRL_X, top + i * itemH, CTRL_X + CTRL_W, top + (i + 1) * itemH);
                var inner = new SKRect(r.Left + 4f, r.Top + 2f, r.Right - 4f, r.Bottom - 2f);
                _hits.Add((r, ACT_MONITOR, i));
                if (i == _monitor) RoundFill(c, inner, 7f, Accent);
                else if (_hoverAct == ACT_MONITOR && _hoverVal == i) RoundFill(c, inner, 7f, SoftFillHot);

                Txt(c, _monitors[i], r.Left + 14f, r.MidY + 5f, 12.5f, FontConfig.Normal,
                    i == _monitor ? SKColors.White : Fg);
            }
        }

        private void DrawAutoStart(SKCanvas c)
        {
            Header(c, "开机自启", "最后一步了。");

            var card = new SKRect(PAD, BODY_TOP + 8f, CARD_W - PAD, BODY_TOP + 96f);
            RoundFill(c, card, 14f, Tile);

            Txt(c, "开机自启", PAD + 24f, card.Top + 40f, 14f, FontConfig.SemiBold, Fg);
            Txt(c, "跟随系统启动，开机后在后台常驻", PAD + 24f, card.Top + 64f, 12f, FontConfig.Normal, Sub);

            float tw = 46f, th = 27f;
            var tr = new SKRect(card.Right - 24f - tw, card.MidY - th / 2f, card.Right - 24f, card.MidY + th / 2f);
            _hits.Add((tr, ACT_AUTOSTART, 0));
            Toggle(c, tr, _autoStart, _hoverAct == ACT_AUTOSTART);

            Txt(c, "之后可随时在设置窗口「通用设置」里更改。", PAD, card.Bottom + 26f, 12f, FontConfig.Normal, Faint);
        }

        // ═══════════════════════ 基础控件 ═══════════════════════

        private void Header(SKCanvas c, string title, string subtitle)
        {
            Txt(c, title, PAD, 84f, 22f, FontConfig.SemiBold, Fg);
            Txt(c, subtitle, PAD, 108f, 12.5f, FontConfig.Normal, Sub);
        }

        private void Label(SKCanvas c, float rowY, string text)
            => Txt(c, text, PAD, rowY + 23f, 13.5f, FontConfig.Normal, Fg);

        private void Row(SKCanvas c, float rowY, string label, string[] labels, int sel, int act, float x, float w)
        {
            Label(c, rowY, label);
            Seg(c, x, rowY, w, CTRL_H, labels, sel, act);
        }

        /// <summary>iOS 风格分段控件：胶囊轨道 + 带投影的滑块 + 滑块位置动画。</summary>
        private void Seg(SKCanvas c, float x, float y, float w, float h, string[] labels, int sel, int act)
        {
            var track = new SKRect(x, y, x + w, y + h);
            RoundFill(c, track, h / 2f, Track);

            int idx = Math.Clamp(act, 0, _segNow.Length - 1);
            if (!_segInit[idx]) { _segNow[idx] = _segDst[idx] = sel; _segInit[idx] = true; }
            float pos = Math.Clamp(_segNow[idx], 0, labels.Length - 1);

            float inner = w - 4f;
            float sw = inner / labels.Length;
            float sx = x + 2f + pos * sw;
            var thumb = new SKRect(sx, y + 2f, sx + sw, y + h - 2f);

            // 滑块投影用"偏移一层的低透明底片"近似（不在每帧用 SKImageFilter —— 见 MEMORY 的缓存坑）
            _fill.Color = M(Light ? new SKColor(0, 0, 0, 22) : new SKColor(0, 0, 0, 60));
            c.DrawRoundRect(new SKRect(thumb.Left, thumb.Top + 1.5f, thumb.Right, thumb.Bottom + 1.5f),
                h / 2f - 2f, h / 2f - 2f, _fill);
            RoundFill(c, thumb, h / 2f - 2f, Thumb);

            for (int i = 0; i < labels.Length; i++)
            {
                var r = new SKRect(x + 2f + i * sw, y + 2f, x + 2f + (i + 1) * sw, y + h - 2f);
                _hits.Add((r, act, i));

                if (_hoverAct == act && _hoverVal == i && i != sel)
                    RoundFill(c, r, h / 2f - 2f, Light ? new SKColor(0, 0, 0, 12) : new SKColor(255, 255, 255, 16));

                bool on = i == sel;
                Txt(c, labels[i], r.MidX, r.MidY + 5f, 13f, on ? FontConfig.SemiBold : FontConfig.Normal,
                    on ? (Light ? new SKColor(29, 29, 31) : SKColors.White) : Sub, true);
            }
        }

        private void Button(SKCanvas c, SKRect r, string label, bool primary, int act, int val)
        {
            _hits.Add((r, act, val));
            bool hover = _hoverAct == act && _hoverVal == val;

            if (primary)
            {
                RoundFill(c, r, 11f, hover ? AccentHot : Accent);
                Txt(c, label, r.MidX, r.MidY + 5.5f, 13.5f, FontConfig.SemiBold, SKColors.White, true);
            }
            else
            {
                RoundFill(c, r, 11f, hover ? SoftFillHot : SoftFill);
                Txt(c, label, r.MidX, r.MidY + 5.5f, 13f, FontConfig.Normal, hover ? Fg : Sub, true);
            }
        }

        private void Toggle(SKCanvas c, SKRect r, bool state, bool hover)
        {
            float rad = r.Height / 2f;

            if (state)
            {
                RoundFill(c, r, rad, hover ? AccentHot : Accent);
            }
            else
            {
                _stroke.Color = M(Light ? new SKColor(0, 0, 0, 40) : new SKColor(255, 255, 255, 56));
                _stroke.StrokeWidth = 1.5f;
                c.DrawRoundRect(r, rad, rad, _stroke);
                _stroke.StrokeWidth = 1f;
            }

            float kr = rad - 3.5f;
            float kx = state ? r.Right - rad : r.Left + rad;
            _fill.Color = M(new SKColor(0, 0, 0, 46));
            c.DrawCircle(kx, r.MidY + 1f, kr, _fill);
            _fill.Color = M(SKColors.White);
            c.DrawCircle(kx, r.MidY, kr, _fill);
        }

        private void RoundFill(SKCanvas c, SKRect r, float rad, SKColor col)
        {
            _fill.Color = M(col);
            c.DrawRoundRect(r, rad, rad, _fill);
        }

        private void RoundStroke(SKCanvas c, SKRect r, float rad, SKColor col, float width)
        {
            _stroke.Color = M(col);
            _stroke.StrokeWidth = width;
            c.DrawRoundRect(r, rad, rad, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        private void Line(SKCanvas c, float x1, float y1, float x2, float y2, SKColor col, float width)
        {
            _stroke.Color = M(col);
            _stroke.StrokeWidth = width;
            c.DrawLine(x1, y1, x2, y2, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        private void Txt(SKCanvas c, string s, float x, float y, float size, SKTypeface face, SKColor color, bool center = false)
        {
            _text.Typeface = face;
            _text.TextSize = size;
            _text.Color = M(color);
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
