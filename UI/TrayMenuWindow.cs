using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    /// <summary>
    /// 托盘右键菜单（自绘）。
    ///
    /// 为什么不用 WinForms 的 ContextMenuStrip：
    ///   原生菜单走的是系统 Menu 主题（Win10/11 那套灰白或跟随系统的方块），和我们整个
    ///   岛体 / 设置面板的暗色 UI 完全不是一个语言，观感很割裂。这里干脆自己画一个，
    ///   刻意不用任何材质（不碰 SetWindowCompositionAttribute / Mica），就是一块纯色，
    ///   靠 per-pixel alpha 把圆角和抗锯齿边缘做干净。
    ///
    /// 实现要点：
    ///   - 复用 ConsoleWindow 那套「layered + UpdateLayeredWindow + Skia」的成熟画法，
    ///     保证和设置面板同一套颜色常量、同一种字体、同样的圆角/间距节奏。
    ///   - 窗口带 WS_EX_NOACTIVATE：菜单弹出时不抢焦点。这很关键，托盘菜单本来就不该
    ///     夺走前台窗口的激活态。
    /// - 代价：WS_EX_NOACTIVATE 的窗口永远不是前台窗口，于是所有「靠前台身份才能收到
    ///     的通知」全都收不到 —— SetCapture 的捕获对后台窗口是残废的（见 CreateAndShow 的注释）、
    ///     WM_ACTIVATEAPP 不会来、WM_KEYDOWN(ESC) 也不会来。所以「点菜单外面收起」这条唯一的
    ///     出路是主动轮询鼠标状态（PollDismiss，和岛体「点岛外收起」同一套办法）。
    ///     历史坑：曾经先写过「延迟 200ms 再 SetCapture」，后来又改成「立刻 SetCapture」，
    ///     两条都不行 —— 不是时序问题，是这个窗口风格根本拿不到前台身份。
    ///
    /// 菜单项状态（尤其「开机自启」的 ）由 SyncAutoStart 双向同步：
    ///   设置面板改了 → 调 SyncAutoStart，托盘菜单下次弹出/立即刷新都对得上；
    ///   托盘菜单点了 → 走 NotchWindow.ToggleAutoStart(enable, true) 回写注册表并通知设置面板。
    /// </summary>
    public class TrayMenuWindow
    {
        // ---- 布局常量（逻辑像素，最终按 DPI 缩放） ----
        private const int MENU_WIDTH = 178;
        private const int ITEM_HEIGHT = 34;
        private const int PADDING_V = 6;
        private const int PADDING_H = 6;          // 外框到高亮块的水平内缩
        private const int CORNER_RADIUS = 8;
        private const int TEXT_LEFT = 16;         // 文字基线左侧起点（相对菜单左边缘）
        private const int CHECK_SLOT = 18;        // 勾选图标占位宽度，保证有无勾选时文字左对齐一致
        private const int ARROW_SLOT = 18;        // 子菜单箭头占位
        private const float TEXT_SIZE = 13.5f;

        private const float HOVER_RADIUS = 5f;

        // ---- 收起轮询（菜单唯一的「点外面关掉」通路） ----
        // 菜单窗口带 WS_EX_NOACTIVATE → 不是前台窗口 → SetCapture 只对「光标压在自己身上」有效，
        // 点菜单外面那一下会被正常投递给别的窗口，我们什么都收不到。所以只能自己按帧轮询按键状态。
        // 20ms ≈ 一帧：远小于人手一次点击的按住时长（通常 50ms 以上），既不会漏也不会太费。
        private const int POLL_TIMER_ID = 0x7EA1;  // 窗口私有定时器 ID，不会和别的 SetTimer 撞号
        private const int POLL_INTERVAL_MS = 20;

        // 与 ConsoleWindow 同一套色板，避免两处 UI 出现两种"暗色"
        private static readonly SKColor COLOR_BG = new SKColor(32, 32, 32);
        private static readonly SKColor COLOR_BORDER = new SKColor(60, 60, 60);
        private static readonly SKColor COLOR_HOVER = new SKColor(255, 255, 255, 20);
        private static readonly SKColor COLOR_TEXT = new SKColor(240, 240, 240);
        private static readonly SKColor COLOR_TEXT_DISABLED = new SKColor(120, 120, 120);
        private static readonly SKColor COLOR_ACCENT = new SKColor(0, 120, 212);
        private static readonly SKColor COLOR_SEPARATOR = new SKColor(255, 255, 255, 20);

        // ---- 菜单项 ----
        private enum MenuAction
        {
            OpenSettings,
            WakeIsland,
            ToggleAutoStart,
            Separator,
            Exit
        }

        private sealed class MenuItem
        {
            public MenuAction Action;
            public string Text = string.Empty;
            public bool Enabled = true;
            /// <summary>是否为可勾选项（渲染时预留 槽位）。</summary>
            public bool IsCheckable;
            /// <summary>当前勾选状态。</summary>
            public bool Checked;
            /// <summary>热区（相对菜单左上角，逻辑像素）。</summary>
            public SKRect HitRect;
        }

        private readonly List<MenuItem> _items = new();
        private readonly List<SKColor> _separatorColors = new(); // 与 _items 等长，仅分隔线项有意义

        // ---- 窗口与渲染状态 ----
        private IntPtr _hwnd = IntPtr.Zero;
        private static IntPtr _classAtom = IntPtr.Zero;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static TrayMenuWindow? _active;   // 全局同时只允许一个菜单实例
        private static int _tokenSeed;            // 每个实例发一个唯一标记

        /// <summary>
        /// 本实例的唯一标记，随 Win32.WM_TRAYMENU_CLOSE 的 wParam 一起投递。
        ///
        /// 为什么需要它：关闭走的是 PostMessage（排队），而窗口句柄会被系统复用 ——
        /// 用户"菜单开着时再点一次托盘图标"时，旧菜单刚排队的那条关闭消息，可能在
        /// CloseActive() 销毁旧窗、新菜单建好（并恰好拿到同一个 HWND 值）之后才被派发，
        /// 于是把刚弹出的新菜单秒掉。带上 token 就能把这类"发给上一个菜单的消息"识别出来丢掉。
        /// </summary>
        private readonly int _token = ++_tokenSeed;

        private float _dpiScale = 1f;
        private readonly int _logicalWidth = MENU_WIDTH;
        private int _logicalHeight = 0;
        private int _pixelWidth = 0;
        private int _pixelHeight = 0;

        private int _hoveredIndex = -1;
        private bool _dismissRequested;
        private bool _trackingMouse;

        // ---- 持久化渲染缓冲（与 ConsoleWindow / Core.NotchWindow 同一套做法）----
        //
        // 一次弹出菜单会渲染很多帧：鼠标划过每一项都 RequestRender()，还有 16ms 的
        // 出菜单动画定时器在连续刷。原实现每帧 CreateCompatibleDC + CreateDIBSection +
        // Buffer.MemoryCopy + DeleteObject + DeleteDC，全是固定开销。
        //
        // 这里比 ConsoleWindow 更简单也更需要注意：_pixelWidth / _pixelHeight 由
        // LayoutItems() 在构造函数里算一次（菜单项是构造时定死的），
        // 菜单实例是一次性的（Show 里 new 出来，关掉就整只丢弃），
        // 所以缓冲在第一次 Render 时按当时的尺寸建、并在 Dispose 里释放即可，
        // 不需要（也不该有）任何重建逻辑。若哪天改成菜单项可变，必须补上尺寸比对 + 重建。
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _hBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _pBits = IntPtr.Zero;
        private SKSurface? _surface;

        private readonly int _anchorX;            // 菜单左上角屏幕坐标（物理像素）
        private readonly int _anchorY;

        // 由外部注入的三个回调，避免这个类反向依赖 NotchWindow / ConsoleWindow 的单例
        private readonly Action _onOpenSettings;
        private readonly Action _onWakeIsland;
        private readonly Action _onExit;

        // 字体按 DPI 缓存一次，别每帧 FromFamilyName（那玩意儿内部有锁，很贵）
        private SKTypeface? _typeface;
        private SKPaint? _textPaint;
        private SKPaint? _checkPaint;
        private SKPaint? _bgPaint;
        private SKPaint? _borderPaint;
        private SKPaint? _hoverPaint;
        private SKPaint? _separatorPaint;

        private readonly System.Timers.Timer _renderTimer;
        private bool _renderScheduled;

        /// <summary>
        /// 构建菜单（不弹窗）。布局在这里一次性算完，之后 Render 只做绘制。
        /// </summary>
        private TrayMenuWindow(int anchorX, int anchorY, Action onOpenSettings, Action onWakeIsland, Action onExit)
        {
            _anchorX = anchorX;
            _anchorY = anchorY;
            _onOpenSettings = onOpenSettings;
            _onWakeIsland = onWakeIsland;
            _onExit = onExit;

            _dpiScale = Math.Max(1f, Win32.GetDpiForSystem() / 96f);

            _items.Add(new MenuItem { Action = MenuAction.OpenSettings, Text = "打开设置" });
            // 自动隐藏把岛体收走后，屏幕顶部那条细边就是唯一入口；这一项是它被遮挡 /
            // 折叠高度过大点不到时的兜底，任何时候点一下都能把岛体叫回来。
            _items.Add(new MenuItem { Action = MenuAction.WakeIsland, Text = "唤回灵动岛" });
            _items.Add(new MenuItem
            {
                Action = MenuAction.ToggleAutoStart,
                Text = "开机自启",
                IsCheckable = true,
                Checked = NotchWindow.IsAutoStartEnabled() // 弹出瞬间读一次真实注册表状态
            });
            _items.Add(new MenuItem { Action = MenuAction.Separator });
            _items.Add(new MenuItem { Action = MenuAction.Exit, Text = "退出" });

            LayoutItems();

            _renderTimer = new System.Timers.Timer(16) { AutoReset = false };
            // 用具名方法而不是 lambda：lambda 闭包会捕获 this（整个窗口对象），
            // 且无法在销毁时 -= 退订 —— 计时器 + 闭包会把窗口一直钉在内存里。
            _renderTimer.Elapsed += OnRenderTick;
        }

        private void OnRenderTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _renderScheduled = false;
            if (!_dismissRequested) Render();
        }

        private void LayoutItems()
        {
            float y = PADDING_V;

            foreach (var item in _items)
            {
                if (item.Action == MenuAction.Separator)
                {
                    item.HitRect = new SKRect(0, y, MENU_WIDTH, y + 7);
                    y += 7;
                    continue;
                }

                item.HitRect = new SKRect(PADDING_H, y, MENU_WIDTH - PADDING_H, y + ITEM_HEIGHT);
                y += ITEM_HEIGHT;
            }

            _logicalHeight = (int)Math.Ceiling(y + PADDING_V);
            _pixelWidth = (int)Math.Ceiling(_logicalWidth * _dpiScale);
            _pixelHeight = (int)Math.Ceiling(_logicalHeight * _dpiScale);
        }

        // ---- 对外入口 ----

        /// <summary>
        /// 在屏幕坐标 (x, y) 弹出菜单。锚点是托盘图标位置，菜单会自动调整方向避免出屏。
        /// </summary>
        public static void Show(int x, int y, Action onOpenSettings, Action onWakeIsland, Action onExit)
        {
            CloseActive();

            try
            {
                var menu = new TrayMenuWindow(x, y, onOpenSettings, onWakeIsland, onExit);
                menu.CreateAndShow();
                _active = menu;
            }
            catch (Exception ex)
            {
                Error("弹出托盘菜单失败", ex);
                _active = null;
            }
        }

        /// <summary>
        /// 「开机自启」状态的双向同步入口。
        ///
        /// 设置面板里切换开关时调这个方法，托盘菜单（如果正开着或在下次弹出时）会立刻反映。
        /// 注意这里不回写注册表——调用方才是状态的权威来源，这里只负责把 UI 对齐。
        /// </summary>
        public static void SyncAutoStart(bool enabled)
        {
            var menu = _active;
            if (menu == null) return;

            foreach (var item in menu._items)
            {
                if (item.Action != MenuAction.ToggleAutoStart) continue;
                if (item.Checked == enabled) return;

                item.Checked = enabled;
                menu.RequestRender();
                return;
            }
        }

        public static bool IsMenuOpen => _active != null;

        private static void CloseActive()
        {
            var menu = _active;
            _active = null;
            menu?.Destroy();
        }

        // ---- 窗口创建 ----

        private void CreateAndShow()
        {
            EnsureClassRegistered();

            IntPtr hInstance = Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;

            // 先按默认位置建，拿到尺寸后再按屏幕边界夹一次
            var (fx, fy) = ClampToScreen(_anchorX, _anchorY);

            // 刻意不带 WS_EX_APPWINDOW / WS_EX_TOOLWINDOW：
            //   NOACTIVATE 保证不抢焦点；不激活的窗口本来就不会出现在 Alt+Tab 里。
            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST,
                "NotchTrayMenuClass", string.Empty,
                Win32.WS_POPUP,
                fx, fy, _pixelWidth, _pixelHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new Exception($"创建托盘菜单窗口失败，错误码: {Marshal.GetLastWin32Error()}");

            CreateResources();
            Render();

            // ShowWindow(SW_SHOWNOACTIVATE) 而不是 ShowWindow(SW_SHOW)：
            // 后者会把焦点从用户当前的前台窗口抢走。
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, fx, fy, _pixelWidth, _pixelHeight,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // 立刻捕获鼠标。
            //
            // 但必须说清楚：这个捕获对本窗口是残废的，它不是收起菜单的依靠。
            //    SetCapture 官方 Remarks 写得很死：
            //      "Only the foreground window can capture the mouse. When a background window
            //       attempts to do so, the window receives messages only for mouse events that
            //       occur when the cursor hot spot is within the visible portion of the window."
            //    而本窗口带 WS_EX_NOACTIVATE，永远不可能成为前台窗口 —— 于是"捕获"退化成了
            //    "光标在自己身上时才收消息"，点菜单外面那一下会被正常投递给别的窗口，我们收不到。
            //    这就是"打开菜单后除了点菜单项，怎么都关不掉"的根因（不是时序问题，
            //    所以之前"延迟 200ms 再武装"和"立刻武装"两种写法都一样不行）。
            //    保留这次 SetCapture 只是因为：万一将来窗口变成前台，WM_LBUTTONDOWN / WM_RBUTTONUP
            //    那两条既有路径立刻就能用；它对现状无害。
            //
            //    另外还要处理一个边界：菜单是由右键抬起拉起来的，如果用户此刻正按着右键，
            //    捕获后第一个到达的可能就是我们自己那次右键的抬起 —— 用 _ignoreNextButtonUp 吃掉。
            _ignoreNextButtonUp = (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
            Win32.SetCapture(_hwnd);

            // 真正负责"点菜单外面收起"的是这个 20ms 轮询（见 PollDismiss）。
            //    走窗口自己的 SetTimer / WM_TIMER：回调天然在 UI 线程上，不用任何跨线程同步，
            //    也不用像 System.Timers.Timer 那样在 Destroy 里退订+Dispose（窗口销毁会自动清掉）。
            Win32.SetTimer(_hwnd, (IntPtr)POLL_TIMER_ID, POLL_INTERVAL_MS, IntPtr.Zero);
        }

        private static void EnsureClassRegistered()
        {
            if (_classAtom != IntPtr.Zero) return;

            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = _staticWndProc,
                hInstance = Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
                lpszClassName = "NotchTrayMenuClass",
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
            };

            _classAtom = (IntPtr)Win32.RegisterClass(ref wc);
        }

        /// <summary>把菜单夹回工作区内：托盘一般在右下角，菜单要往左上方向展开。</summary>
        private (int x, int y) ClampToScreen(int x, int y)
        {
            var cursor = new Win32.POINT(x, y);
            IntPtr monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTONEAREST);
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };

            int left = 0, top = 0, right = 1920, bottom = 1080;
            if (monitor != IntPtr.Zero && Win32.GetMonitorInfo(monitor, ref mi))
            {
                left = mi.rcWork.Left;
                top = mi.rcWork.Top;
                right = mi.rcWork.Right;
                bottom = mi.rcWork.Bottom;
            }

            // 默认把菜单的左下角贴到锚点（托盘图标的典型位置）
            int fx = x - _pixelWidth;
            int fy = y - _pixelHeight;

            if (fx < left) fx = left;
            if (fy < top) fy = top;
            if (fx + _pixelWidth > right) fx = right - _pixelWidth;
            if (fy + _pixelHeight > bottom) fy = bottom - _pixelHeight;

            return (fx, fy);
        }

        private void CreateResources()
        {
            _typeface ??= SKTypeface.FromFamilyName("Microsoft YaHei UI");

            _bgPaint ??= new SKPaint { Color = COLOR_BG, IsAntialias = true };
            _hoverPaint ??= new SKPaint { Color = COLOR_HOVER, IsAntialias = true };
            _separatorPaint ??= new SKPaint { Color = COLOR_SEPARATOR, StrokeWidth = 1, IsAntialias = false };
            _bgPaint.Color = COLOR_BG;

            _borderPaint ??= new SKPaint
            {
                Color = COLOR_BORDER,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };

            _textPaint ??= new SKPaint
            {
                TextSize = TEXT_SIZE,
                IsAntialias = true,
                Typeface = _typeface,
                Color = COLOR_TEXT
            };

            _checkPaint ??= new SKPaint
            {
                Color = COLOR_ACCENT,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
        }

        private void Destroy()
        {
            _tearingDown = true;
            _tornDown = true;   // 从这一刻起不再调度任何新渲染

            try
            {
                // 必须 Stop + 退订 + Dispose 三件套：只 Stop 不会释放内部的 System.Threading.Timer 注册，
                // 而注册里挂着 OnRenderTick（实例方法）→ 会把整个 TrayMenuWindow 一直钉住。
                // 菜单每次弹出都是 new 一个实例，所以这里不严格释放就是"每开一次托盘菜单泄漏一个窗口对象"。
                _renderTimer.Elapsed -= OnRenderTick;
                _renderTimer.Stop();
                _renderTimer.Dispose();
            }
            catch { /* 关窗路径上不值得为计时器异常打断 */ }

            if (_hwnd != IntPtr.Zero)
            {
                // 轮询定时器挂在窗口上，DestroyWindow 会一并清掉；显式 Kill 一次只是为了意图清楚
                Win32.KillTimer(_hwnd, (IntPtr)POLL_TIMER_ID);
                // 释放鼠标捕获，否则捕获会跟着句柄一起消失、还把点击吞在别处
                Win32.ReleaseCapture();
                Win32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            _tearingDown = false;
            _trackingMouse = false;

            // 常驻渲染缓冲（memDC + DIB + SKSurface）不归 GC 管，必须在这里显式释放 ——
            // 菜单每次弹出都 new 一个新实例，漏掉就是"每开关一次菜单泄漏一对内核对象 + 一块 DIB"。
            // 放在窗口销毁之后：此时已不再需要提交任何一帧。
            DisposeRenderBuffer();

            // 逐个 Dispose 再置 null：SKPaint / SKTypeface 持有 Skia 原生资源，
            // 只把字段置 null 不会释放原生句柄（要等 GC 终结器），而 CreateResources() 用的是 `??=`，
            // 下次打开菜单又会重新分配一整套 —— 于是"每开关一次菜单泄漏 1 字体 + 7 画笔"。
            DisposePaint(ref _textPaint);
            DisposePaint(ref _checkPaint);
            DisposePaint(ref _bgPaint);
            DisposePaint(ref _borderPaint);
            DisposePaint(ref _hoverPaint);
            DisposePaint(ref _separatorPaint);
            try { _typeface?.Dispose(); } catch { }
            _typeface = null;
        }

        /// <summary>释放一支画笔并置空；重复调用安全（字段为 null 时为空操作）。</summary>
        private static void DisposePaint(ref SKPaint? paint)
        {
            try { paint?.Dispose(); } catch { }
            paint = null;
        }

        // ---- 消息处理 ----

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var menu = _active;
            if (menu != null && menu._hwnd == hwnd)
                return menu.InstanceWndProc(hwnd, msg, wParam, lParam);

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    {
                        // 必须显式订阅 WM_MOUSELEAVE，否则每次移动都要重新申请一次（系统只发一次）
                        if (!_trackingMouse)
                        {
                            var tme = new Win32.TRACKMOUSEEVENT
                            {
                                cbSize = (uint)Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                                dwFlags = Win32.TME_LEAVE,
                                hwndTrack = hwnd,
                                dwHoverTime = 0
                            };
                            if (Win32.TrackMouseEvent(ref tme)) _trackingMouse = true;
                        }

                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        int hit = HitTest(x, y);
                        if (hit != _hoveredIndex)
                        {
                            _hoveredIndex = hit;
                            RequestRender();
                        }
                    }
                    break;

                case Win32.WM_MOUSELEAVE:
                    _trackingMouse = false;
                    if (_hoveredIndex != -1)
                    {
                        _hoveredIndex = -1;
                        RequestRender();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
                    {
                        // 故意不在这里无条件 ReleaseCapture：
                        // 释放捕获会立刻触发 WM_CAPTURECHANGED，那样"往菜单项上按一下"
                        // 就会把菜单关掉，连点击都送不到。
                        // 正确姿势是保持捕获，等 WM_LBUTTONUP 再统一判定：
                        //   落在项上 → 执行；落在菜单外 → 收起。
                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        if (HitTest(x, y) == -1)
                        {
                            // 按下位置在菜单外 → 收起，并立刻交还捕获，
                            // 让这一下点击能正常落到用户真正想点的那个窗口上，
                            // 而不是被我们攥到鼠标抬起为止。
                            Win32.ReleaseCapture();
                            RequestDismiss();
                        }
                    }
                    return IntPtr.Zero;

                case Win32.WM_LBUTTONUP:
                    {
                        // 捕获期间坐标可能落在菜单外（负数或超界），HitTest 自然判为 -1
                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        int hit = HitTest(x, y);

                        if (hit >= 0 && hit < _items.Count)
                        {
                            var item = _items[hit];
                            if (item.Action != MenuAction.Separator && item.Enabled)
                            {
                                Execute(item);
                                return IntPtr.Zero;
                            }
                        }

                        // 落在菜单外、或者落在分隔线上：收起（原生菜单的手感）
                        RequestDismiss();
                    }
                    return IntPtr.Zero;

                case Win32.WM_RBUTTONUP:
                    // 菜单是由右键抬起拉起来的，那一下的抬起消息可能也落到我们头上 → 吃掉
                    if (_ignoreNextButtonUp)
                    {
                        _ignoreNextButtonUp = false;
                        return IntPtr.Zero;
                    }
                    // 之后再点右键才是"用户想在菜单上再点一次" → 收起（原生菜单就是这个行为）
                    RequestDismiss();
                    return IntPtr.Zero;

                case Win32.WM_CAPTURECHANGED:
                    // 捕获被别处抢走 → 等价于"用户跑到别的地方去了"，收起自己。
                    // 注意：DestroyWindow 也会触发这条，靠 _tearingDown 区分，别递归。
                    if (!_tearingDown) RequestDismiss();
                    return IntPtr.Zero;

                case Win32.WM_TRAYMENU_CLOSE:
                    // 只认自己那条：句柄被复用的情况下，这条消息可能是发给"上一个菜单"的
                    if (wParam.ToInt32() != _token) return IntPtr.Zero;
                    CloseActive();
                    return IntPtr.Zero;

                case Win32.WM_KEYDOWN:
                    // 事实上这条分支永远不会被触发：WS_EX_NOACTIVATE 的窗口拿不到键盘焦点。
                    // ESC 收起实际由 PollDismiss 轮询 GetAsyncKeyState(VK_ESCAPE) 完成。
                    // 保留它只是为了"窗口万一变前台"时不至于丢掉 ESC。
                    if (wParam.ToInt32() == Win32.VK_ESCAPE)
                    {
                        RequestDismiss();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_TIMER:
                    // 菜单存活期间唯一的"点外面关掉"通路（原因见 PollDismiss 的注释）
                    if (wParam.ToInt32() == POLL_TIMER_ID)
                    {
                        PollDismiss();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_PAINT:
                    {
                        IntPtr dc = Win32.BeginPaint(hwnd, out var ps);
                        if (dc != IntPtr.Zero) Win32.EndPaint(hwnd, ref ps);
                        return IntPtr.Zero;
                    }

                case Win32.WM_DESTROY:
                    return IntPtr.Zero;
            }

            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private bool _tearingDown;   // 销毁过程中，用来屏蔽 WM_CAPTURECHANGED 的递归关闭
        private bool _tornDown;      // 已彻底销毁（计时器与画笔均已释放），禁止再次调度渲染
        private bool _ignoreNextButtonUp; // 吃掉"拉起菜单的那一下右键抬起"，防止刚弹出就自杀
        private bool _pollArmed;     // 轮询是否已武装（先等所有鼠标键松开一次，躲开"拉起菜单的那一下"）
        private bool _pollButtonDown;// 上一次轮询时是否有鼠标键按着，用来识别"新按下"

        private int HitTest(int x, int y)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Action == MenuAction.Separator || !item.Enabled) continue;
                if (item.HitRect.Contains(x, y)) return i;
            }
            return -1;
        }

        private void Execute(MenuItem item)
        {
            switch (item.Action)
            {
                case MenuAction.OpenSettings:
                    RequestDismiss();
                    _onOpenSettings?.Invoke();
                    break;

                case MenuAction.WakeIsland:
                    RequestDismiss();
                    _onWakeIsland?.Invoke();
                    break;

                case MenuAction.ToggleAutoStart:
                    {
                        bool newState = !item.Checked;

                        // sourceIsTray: true → NotchWindow 走注册表写入 + ConsoleWindow.UpdateAutoStartState
                        // 这条路径是既有的双向同步契约，必须原样保留，否则设置面板的开关就不跟手了。
                        NotchWindow.ToggleAutoStart(newState, true);

                        // 以注册表真实结果为准回读一次，避免写失败时菜单却显示"已勾选"
                        item.Checked = NotchWindow.IsAutoStartEnabled();

                        // 如果菜单还开着，刷新勾选；通常这里会顺手收起
                        RequestDismiss();
                    }
                    break;

                case MenuAction.Exit:
                    RequestDismiss();
                    _onExit?.Invoke();
                    break;
            }
        }

        private void RequestDismiss()
        {
            if (_dismissRequested) return;
            _dismissRequested = true;

            // 回到消息循环后再销毁，不要在 WndProc 里直接 DestroyWindow
            // wParam 带本实例的 token，用来挡住"句柄复用导致旧菜单的消息误杀新菜单"（见 _token 注释）
            Win32.PostMessage(_hwnd, Win32.WM_TRAYMENU_CLOSE, (IntPtr)_token, IntPtr.Zero);
        }

        private void RequestRender()
        {
            // _tornDown：Destroy() 已经把计时器 Dispose 了，此时再 Start() 会抛 ObjectDisposedException。
            // 窗口销毁后仍可能有已排队的消息进来（销毁与消息派发之间没有同步保证），必须挡住。
            if (_tornDown || _renderScheduled || _dismissRequested) return;
            _renderScheduled = true;
            _renderTimer.Start();
        }

        // ---- 收起判定（20ms 轮询） ----

        /// <summary>
        /// 每 20ms 走一次：光标在菜单外且鼠标新按下 → 收起菜单。
        ///
        /// 为什么必须轮询：菜单窗口带 WS_EX_NOACTIVATE，永远不是前台窗口，而系统只把鼠标捕获
        /// 交给前台窗口（见 CreateAndShow 里引的 SetCapture 文档原文）。所以
        ///   · SetCapture + WM_LBUTTONDOWN  → 点外面那一下根本收不到；
        ///   · WM_ACTIVATEAPP / WM_ACTIVATE → 窗口从不被激活，也收不到；
        ///   · WM_KEYDOWN(ESC)             → 没有键盘焦点，同样收不到。
        /// 三条路全被这个窗口风格堵死，只能主动去问系统"现在键按着没、光标在哪"。
        /// 岛体的「点岛外收起」用的也是这套 GetAsyncKeyState 办法，行为一致。
        /// </summary>
        private void PollDismiss()
        {
            if (_dismissRequested || _tornDown) return;

            bool anyDown = AnyMouseButtonDown();

            // 武装：菜单可能是"右键还按着"的时候弹出来的，先等所有鼠标键松开一次，
            // 否则拉起菜单的那一下会被当成"在菜单外新按下"，菜单刚弹出就自己关掉。
            if (!_pollArmed)
            {
                if (anyDown) return;
                _pollArmed = true;
                _pollButtonDown = false;
                return;
            }

            // ESC：菜单拿不到键盘焦点，WM_KEYDOWN 永远不会来，这里兜一下
            if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0)
            {
                DismissNow();
                return;
            }

            // 只认「新按下」那一帧：在菜单里按住再拖出去不收起（和系统菜单一致），
            // 一次完整点击（按下→抬起）不可能短于 20ms，所以不会漏。
            bool freshPress = anyDown && !_pollButtonDown;
            _pollButtonDown = anyDown;
            if (!freshPress) return;

            if (IsCursorInsideMenu()) return;
            DismissNow();
        }

        private static bool AnyMouseButtonDown()
        {
            return (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_MBUTTON) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_XBUTTON1) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_XBUTTON2) & 0x8000) != 0;
        }

        /// <summary>光标是否落在菜单窗口矩形内（都用屏幕物理像素，不做 DPI 换算）。</summary>
        private bool IsCursorInsideMenu()
        {
            if (_hwnd == IntPtr.Zero) return false;
            if (!Win32.GetCursorPos(out var pt)) return true; // 取不到就当作在里面，宁可多等一帧也别误关
            if (!Win32.GetWindowRect(_hwnd, out var r)) return false;

            return pt.x >= r.Left && pt.x < r.Right && pt.y >= r.Top && pt.y < r.Bottom;
        }

        /// <summary>收起菜单：先交还捕获（对后台窗口是空操作，留着以防窗口日后变前台），再请求销毁。</summary>
        private void DismissNow()
        {
            Win32.ReleaseCapture();
            RequestDismiss();
        }

        // ---- 绘制 ----

        private unsafe void Render()
        {
            if (_hwnd == IntPtr.Zero || _pixelWidth <= 0 || _pixelHeight <= 0) return;

            // 常驻 surface 按菜单尺寸建一次（见字段声明处的说明）。
            var surface = _surface;
            if (surface == null)
            {
                if (!EnsureRenderBuffer()) return;
                surface = _surface!;
            }

            var canvas = surface.Canvas;
            canvas.ResetMatrix();          // surface 复用：清掉上一帧的矩阵/裁剪状态
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(_dpiScale);

            float w = _logicalWidth;
            float h = _logicalHeight;
            float radius = CORNER_RADIUS;

            // 纯色底 + 1px 描边。刻意不叠任何材质/模糊，只要简单的纯色。
            var full = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
            canvas.DrawRoundRect(full, radius, radius, _bgPaint);
            canvas.DrawRoundRect(full, radius, radius, _borderPaint);

            DrawItems(canvas);

            // Skia 延迟光栅化：不 Flush 就读 pBits 会拿到半成品（现在 Skia 直接画进 DIB 内存）
            canvas.Flush();
            UpdateLayeredContent();
        }

        /// <summary>建常驻渲染缓冲（memDC + DIB + 绑在 pBits 上的 SKSurface）。失败返回 false。</summary>
        private bool EnsureRenderBuffer()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;

            try
            {
                _memDc = Win32.CreateCompatibleDC(screenDc);
                if (_memDc == IntPtr.Zero) return false;

                var bmi = new Win32.BITMAPINFO
                {
                    bmiHeader = new Win32.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                        biWidth = _pixelWidth,
                        biHeight = -_pixelHeight, // 负数 = 自上而下，和 Skia 的内存布局一致
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
                if (_hBitmap == IntPtr.Zero || _pBits == IntPtr.Zero)
                {
                    if (_hBitmap != IntPtr.Zero) { Win32.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
                    Win32.DeleteDC(_memDc);
                    _memDc = IntPtr.Zero;
                    return false;
                }

                _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);

                var info = new SKImageInfo(_pixelWidth, _pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                _surface = SKSurface.Create(info, _pBits, _pixelWidth * 4);
                if (_surface == null)
                {
                    Win32.SelectObject(_memDc, _oldBitmap);
                    Win32.DeleteObject(_hBitmap);
                    Win32.DeleteDC(_memDc);
                    _hBitmap = IntPtr.Zero; _memDc = IntPtr.Zero; _oldBitmap = IntPtr.Zero; _pBits = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Error("创建托盘菜单渲染缓冲失败", ex);
                return false;
            }
            finally
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>释放常驻渲染缓冲。顺序不能改：先选回旧位图解锁，再删 hBitmap、surface、memDC。</summary>
        private void DisposeRenderBuffer()
        {
            _surface?.Dispose();
            _surface = null;

            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
                Win32.SelectObject(_memDc, _oldBitmap);

            if (_hBitmap != IntPtr.Zero) { Win32.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { Win32.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            _oldBitmap = IntPtr.Zero;
            _pBits = IntPtr.Zero;
        }

        private void DrawItems(SKCanvas canvas)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];

                if (item.Action == MenuAction.Separator)
                {
                    float lineY = item.HitRect.MidY;
                    canvas.DrawLine(PADDING_H * 2, lineY, _logicalWidth - PADDING_H * 2, lineY, _separatorPaint);
                    continue;
                }

                var rect = item.HitRect;

                if (i == _hoveredIndex && item.Enabled)
                    canvas.DrawRoundRect(rect, HOVER_RADIUS, HOVER_RADIUS, _hoverPaint);

                _textPaint!.Color = item.Enabled ? COLOR_TEXT : COLOR_TEXT_DISABLED;

                // 文字垂直居中：用字体度量算基线，别拿 TextSize 硬凑（中文字体的 baseline 偏移很明显）
                float baseline = rect.MidY - (_textPaint.FontMetrics.Ascent + _textPaint.FontMetrics.Descent) / 2f;
                float textX = TEXT_LEFT;

                if (item.IsCheckable)
                {
                    if (item.Checked)
                    {
                        // 手绘一个对勾（比塞字体符号稳，不会因为字体缺失变成豆腐块）
                        float cx = TEXT_LEFT - 2;
                        float cy = rect.MidY;
                        canvas.DrawLine(cx - 5.5f, cy + 0.5f, cx - 2f, cy + 4f, _checkPaint);
                        canvas.DrawLine(cx - 2f, cy + 4f, cx + 5f, cy - 4f, _checkPaint);
                    }

                    textX = TEXT_LEFT + CHECK_SLOT;
                }

                canvas.DrawText(item.Text, textX, baseline, _textPaint);
            }
        }

        /// <summary>
        /// 把常驻 DIB 提交给分层窗口。缓冲已常驻，所以这里没有 Create / Delete，
        /// 也没有整缓冲拷贝（Skia 直接画在 pBits 上）——只剩一次 UpdateLayeredWindow。
        /// </summary>
        private void UpdateLayeredContent()
        {
            if (_memDc == IntPtr.Zero) return;

            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;

            try
            {
                Win32.GetWindowRect(_hwnd, out var rect);

                var ptSrc = new Win32.POINT(0, 0);
                var ptDst = new Win32.POINT(rect.Left, rect.Top);
                var size = new Win32.SIZE(_pixelWidth, _pixelHeight);
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
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
