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
    ///   刻意**不用任何材质**（不碰 SetWindowCompositionAttribute / Mica），就是一块纯色，
    ///   靠 per-pixel alpha 把圆角和抗锯齿边缘做干净。
    ///
    /// 实现要点：
    ///   - 复用 ConsoleWindow 那套「layered + UpdateLayeredWindow + Skia」的成熟画法，
    ///     保证和设置面板同一套颜色常量、同一种字体、同样的圆角/间距节奏。
    ///   - 窗口带 WS_EX_NOACTIVATE：菜单弹出时**不抢焦点**。这很关键，托盘菜单本来就不该
    ///     夺走前台窗口的激活态；同时也避免了「激活 → 失活 → 自己把自己关掉」的自杀循环。
    ///   - 关闭走两条路：点在自己身上（执行命令后关） / 收到 WM_ACTIVATEAPP 失活通知
    ///     （点了别的窗口，或者右键弹了系统任务栏菜单）。
    ///
    /// 菜单项状态（尤其「开机自启」的 ✅）由 <see cref="SyncAutoStart"/> 双向同步：
    ///   设置面板改了 → 调 SyncAutoStart，托盘菜单下次弹出/立即刷新都对得上；
    ///   托盘菜单点了 → 走 NotchWindow.ToggleAutoStart(enable, true) 回写注册表并通知设置面板。
    /// </summary>
    public class TrayMenuWindow
    {
        // ==================== 布局常量（逻辑像素，最终按 DPI 缩放） ====================
        private const int MENU_WIDTH = 178;
        private const int ITEM_HEIGHT = 34;
        private const int PADDING_V = 6;
        private const int PADDING_H = 6;          // 外框到高亮块的水平内缩
        private const int CORNER_RADIUS = 8;
        private const int TEXT_LEFT = 16;         // 文字基线左侧起点（相对菜单左边缘）
        private const int CHECK_SLOT = 18;        // ✅ 图标占位宽度，保证有无勾选时文字左对齐一致
        private const int ARROW_SLOT = 18;        // 子菜单箭头占位
        private const float TEXT_SIZE = 13.5f;

        private const float HOVER_RADIUS = 5f;

        // 与 ConsoleWindow 同一套色板，避免两处 UI 出现两种"暗色"
        private static readonly SKColor COLOR_BG = new SKColor(32, 32, 32);
        private static readonly SKColor COLOR_BORDER = new SKColor(60, 60, 60);
        private static readonly SKColor COLOR_HOVER = new SKColor(255, 255, 255, 20);
        private static readonly SKColor COLOR_TEXT = new SKColor(240, 240, 240);
        private static readonly SKColor COLOR_TEXT_DISABLED = new SKColor(120, 120, 120);
        private static readonly SKColor COLOR_ACCENT = new SKColor(0, 120, 212);
        private static readonly SKColor COLOR_SEPARATOR = new SKColor(255, 255, 255, 20);

        // ==================== 菜单项 ====================
        private enum MenuAction
        {
            OpenSettings,
            ToggleAutoStart,
            Separator,
            Exit
        }

        private sealed class MenuItem
        {
            public MenuAction Action;
            public string Text = string.Empty;
            public bool Enabled = true;
            /// <summary>是否为可勾选项（渲染时预留 ✅ 槽位）。</summary>
            public bool IsCheckable;
            /// <summary>当前勾选状态。</summary>
            public bool Checked;
            /// <summary>热区（相对菜单左上角，逻辑像素）。</summary>
            public SKRect HitRect;
        }

        private readonly List<MenuItem> _items = new();
        private readonly List<SKColor> _separatorColors = new(); // 与 _items 等长，仅分隔线项有意义

        // ==================== 窗口与渲染状态 ====================
        private IntPtr _hwnd = IntPtr.Zero;
        private static IntPtr _classAtom = IntPtr.Zero;
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static TrayMenuWindow? _active;   // 全局同时只允许一个菜单实例

        private float _dpiScale = 1f;
        private readonly int _logicalWidth = MENU_WIDTH;
        private int _logicalHeight = 0;
        private int _pixelWidth = 0;
        private int _pixelHeight = 0;

        private int _hoveredIndex = -1;
        private bool _dismissRequested;
        private bool _trackingMouse;

        private readonly int _anchorX;            // 菜单左上角屏幕坐标（物理像素）
        private readonly int _anchorY;

        // 由外部注入的三个回调，避免这个类反向依赖 NotchWindow / ConsoleWindow 的单例
        private readonly Action _onOpenSettings;
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
        private TrayMenuWindow(int anchorX, int anchorY, Action onOpenSettings, Action onExit)
        {
            _anchorX = anchorX;
            _anchorY = anchorY;
            _onOpenSettings = onOpenSettings;
            _onExit = onExit;

            _dpiScale = Math.Max(1f, Win32.GetDpiForSystem() / 96f);

            _items.Add(new MenuItem { Action = MenuAction.OpenSettings, Text = "打开设置" });
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
            _renderTimer.Elapsed += (_, __) =>
            {
                _renderScheduled = false;
                if (!_dismissRequested) Render();
            };
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

        // ==================== 对外入口 ====================

        /// <summary>
        /// 在屏幕坐标 (x, y) 弹出菜单。锚点是托盘图标位置，菜单会自动调整方向避免出屏。
        /// </summary>
        public static void Show(int x, int y, Action onOpenSettings, Action onExit)
        {
            CloseActive();

            try
            {
                var menu = new TrayMenuWindow(x, y, onOpenSettings, onExit);
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
        /// 注意这里**不回写注册表**——调用方才是状态的权威来源，这里只负责把 UI 对齐。
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

        // ==================== 窗口创建 ====================

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
            // 之前这里是"延迟 200ms 再武装"，那是为了躲 WM_ACTIVATEAPP 的假通知——
            // 现在关闭逻辑改走捕获了，那个顾虑不存在。而且延迟是有害的：
            // 弹窗后头 200ms 内点外面会因为还没捕获而漏掉，菜单就一直挂着。
            //
            // 另外必须处理一个边界：菜单是由**右键抬起**拉起来的，
            // 如果用户此刻正按着右键（或者右键抬起事件的时序刚好落在同一个消息批次里），
            // 捕获后第一个到达的可能就是我们自己那次右键的抬起。
            // 用 _ignoreNextButtonUp 把这一下吃掉，避免菜单"刚弹出就自己关掉"。
            _ignoreNextButtonUp = (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
            Win32.SetCapture(_hwnd);
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

            // 默认把菜单的**左下角**贴到锚点（托盘图标的典型位置）
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

            try
            {
                _renderTimer.Stop();
            }
            catch { /* 关窗路径上不值得为计时器异常打断 */ }

            if (_hwnd != IntPtr.Zero)
            {
                // 释放鼠标捕获，否则捕获会跟着句柄一起消失、还把点击吞在别处
                Win32.ReleaseCapture();
                Win32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            _tearingDown = false;
            _trackingMouse = false;

            _typeface = null;
            _textPaint = null;
            _checkPaint = null;
            _bgPaint = null;
            _borderPaint = null;
            _hoverPaint = null;
            _separatorPaint = null;
        }

        // ==================== 消息处理 ====================

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
                        // 故意**不**在这里无条件 ReleaseCapture：
                        // 释放捕获会立刻触发 WM_CAPTURECHANGED，那样"往菜单项上按一下"
                        // 就会把菜单关掉，连点击都送不到。
                        // 正确姿势是保持捕获，等 WM_LBUTTONUP 再统一判定：
                        //   落在项上 → 执行；落在菜单外 → 收起。
                        int x = (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale);
                        if (HitTest(x, y) == -1)
                        {
                            // 按下位置在菜单外 → 收起，并**立刻**交还捕获，
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
                    CloseActive();
                    return IntPtr.Zero;

                case Win32.WM_KEYDOWN:
                    if (wParam.ToInt32() == Win32.VK_ESCAPE)
                    {
                        RequestDismiss();
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
        private bool _ignoreNextButtonUp; // 吃掉"拉起菜单的那一下右键抬起"，防止刚弹出就自杀

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
            Win32.PostMessage(_hwnd, Win32.WM_TRAYMENU_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private void RequestRender()
        {
            if (_renderScheduled || _dismissRequested) return;
            _renderScheduled = true;
            _renderTimer.Start();
        }

        // ==================== 绘制 ====================

        private unsafe void Render()
        {
            if (_hwnd == IntPtr.Zero || _pixelWidth <= 0 || _pixelHeight <= 0) return;

            var info = new SKImageInfo(_pixelWidth, _pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(_dpiScale);

            float w = _logicalWidth;
            float h = _logicalHeight;
            float radius = CORNER_RADIUS;

            // 纯色底 + 1px 描边。刻意不叠任何材质/模糊，用户明确要"简单的纯色"。
            var full = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
            canvas.DrawRoundRect(full, radius, radius, _bgPaint);
            canvas.DrawRoundRect(full, radius, radius, _borderPaint);

            DrawItems(canvas);

            UpdateLayeredContent(surface.PeekPixels());
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

        private unsafe void UpdateLayeredContent(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;

            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            try
            {
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

                IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero) return;

                IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);
                try
                {
                    long bytes = (long)_pixelWidth * _pixelHeight * 4;
                    Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

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

                    Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
                }
                finally
                {
                    Win32.SelectObject(memDc, hOldBitmap);
                    Win32.DeleteObject(hBitmap);
                }
            }
            finally
            {
                Win32.DeleteDC(memDc);
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
