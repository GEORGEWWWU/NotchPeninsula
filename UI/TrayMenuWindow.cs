using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    public class TrayMenuWindow
    {
        private const int MENU_WIDTH = 178;
        private const int ITEM_HEIGHT = 34;
        private const int HEADER_HEIGHT = 26;      // 顶部版本号标题行
        private const int PADDING_V = 6;
        private const int PADDING_H = 6;          // 外框到高亮块的水平内缩
        private const int CORNER_RADIUS = 8;
        private const int TEXT_LEFT = 16;         // 文字基线左侧起点（相对菜单左边缘）
        private const int CHECK_SLOT = 18;        // 勾选图标占位宽度，保证有无勾选时文字左对齐一致
        private const int ARROW_SLOT = 18;        // 子菜单箭头占位
        private const float TEXT_SIZE = 13.5f;
        private const float TEXT_TITLE_SIZE = 11.5f;

        private const float HOVER_RADIUS = 5f;

        private const int POLL_TIMER_ID = 0x7EA1;  // 窗口私有定时器 ID，不会和别的 SetTimer 撞号
        private const int POLL_INTERVAL_MS = 20;

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
            Header,
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
            public bool IsCheckable;
            public bool Checked;
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

        private readonly int _token = ++_tokenSeed;

        private float _dpiScale = 1f;
        private readonly int _logicalWidth = MENU_WIDTH;
        private int _logicalHeight = 0;
        private int _pixelWidth = 0;
        private int _pixelHeight = 0;

        private int _hoveredIndex = -1;
        private bool _dismissRequested;
        private bool _trackingMouse;

        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _hBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _pBits = IntPtr.Zero;
        private SKSurface? _surface;

        private readonly int _anchorX;            // 菜单左上角屏幕坐标（物理像素）
        private readonly int _anchorY;

        private readonly Action _onOpenSettings;
        private readonly Action _onWakeIsland;
        private readonly Action _onExit;

        private SKTypeface? _typeface;
        private SKPaint? _textPaint;
        private SKPaint? _checkPaint;
        private SKPaint? _bgPaint;
        private SKPaint? _borderPaint;
        private SKPaint? _hoverPaint;
        private SKPaint? _separatorPaint;

        private readonly System.Timers.Timer _renderTimer;
        private bool _renderScheduled;

        private TrayMenuWindow(int anchorX, int anchorY, Action onOpenSettings, Action onWakeIsland, Action onExit)
        {
            _anchorX = anchorX;
            _anchorY = anchorY;
            _onOpenSettings = onOpenSettings;
            _onWakeIsland = onWakeIsland;
            _onExit = onExit;

            _dpiScale = Math.Max(1f, Win32.GetDpiForSystem() / 96f);

            _items.Add(new MenuItem
            {
                Action = MenuAction.Header,
                Text = "NPS v" + FormatVersion(),
                Enabled = false            // 灰色标题：HitTest 跳过 Enabled=false，天然不可点
            });
            _items.Add(new MenuItem { Action = MenuAction.OpenSettings, Text = "打开设置" });
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

                if (item.Action == MenuAction.Header)
                {
                    item.HitRect = new SKRect(0, y, MENU_WIDTH, y + HEADER_HEIGHT);
                    y += HEADER_HEIGHT;
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

        /// <summary>版本号，口径与设置窗口标题一致（Major.Minor.Build）。</summary>
        private static string FormatVersion()
        {
            var v = typeof(TrayMenuWindow).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

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

            // 后者会把焦点从用户当前的前台窗口抢走。
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, fx, fy, _pixelWidth, _pixelHeight,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // 立刻捕获鼠标。
            //    所以之前"延迟 200ms 再武装"和"立刻武装"两种写法都一样不行）。
            //    那两条既有路径立刻就能用；它对现状无害。
            _ignoreNextButtonUp = (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
            Win32.SetCapture(_hwnd);

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
                _renderTimer.Elapsed -= OnRenderTick;
                _renderTimer.Stop();
                _renderTimer.Dispose();
            }
            catch { /* 关窗路径上不值得为计时器异常打断 */ }

            if (_hwnd != IntPtr.Zero)
            {
                Win32.KillTimer(_hwnd, (IntPtr)POLL_TIMER_ID);
                Win32.ReleaseCapture();
                Win32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            _tearingDown = false;
            _trackingMouse = false;

            // 放在窗口销毁之后：此时已不再需要提交任何一帧。
            DisposeRenderBuffer();

            DisposePaint(ref _textPaint);
            DisposePaint(ref _checkPaint);
            DisposePaint(ref _bgPaint);
            DisposePaint(ref _borderPaint);
            DisposePaint(ref _hoverPaint);
            DisposePaint(ref _separatorPaint);
            try { _typeface?.Dispose(); } catch { }
            _typeface = null;
        }

        private static void DisposePaint(ref SKPaint? paint)
        {
            try { paint?.Dispose(); } catch { }
            paint = null;
        }

        // ---- 消息处理 ----

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var menu = _active;
                if (menu != null && menu._hwnd == hwnd)
                    return menu.InstanceWndProc(hwnd, msg, wParam, lParam);

                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Error($"托盘菜单窗口过程处理消息 0x{msg:X4} 时异常，已忽略", ex);
                return IntPtr.Zero;
            }
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    {
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

                        int x = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
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
                        // 就会把菜单关掉，连点击都送不到。
                        //   落在项上 → 执行；落在菜单外 → 收起。
                        int x = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
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
                        int x = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                        int y = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);
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
                    if (_ignoreNextButtonUp)
                    {
                        _ignoreNextButtonUp = false;
                        return IntPtr.Zero;
                    }
                    RequestDismiss();
                    return IntPtr.Zero;

                case Win32.WM_CAPTURECHANGED:
                    if (!_tearingDown) RequestDismiss();
                    return IntPtr.Zero;

                case Win32.WM_TRAYMENU_CLOSE:
                    if (Win32.Low32(wParam) != _token) return IntPtr.Zero;
                    CloseActive();
                    return IntPtr.Zero;

                case Win32.WM_KEYDOWN:
                    // 保留它只是为了"窗口万一变前台"时不至于丢掉 ESC。
                    if (Win32.Low32(wParam) == Win32.VK_ESCAPE)
                    {
                        RequestDismiss();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_TIMER:
                    if (Win32.Low32(wParam) == POLL_TIMER_ID)
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

                        NotchWindow.ToggleAutoStart(newState, true);

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

            Win32.PostMessage(_hwnd, Win32.WM_TRAYMENU_CLOSE, (IntPtr)_token, IntPtr.Zero);
        }

        private void RequestRender()
        {
            if (_tornDown || _renderScheduled || _dismissRequested) return;
            _renderScheduled = true;
            _renderTimer.Start();
        }

        // ---- 收起判定（20ms 轮询） ----

        private void PollDismiss()
        {
            if (_dismissRequested || _tornDown) return;

            bool anyDown = AnyMouseButtonDown();

            if (!_pollArmed)
            {
                if (anyDown) return;
                _pollArmed = true;
                _pollButtonDown = false;
                return;
            }

            if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0)
            {
                DismissNow();
                return;
            }

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

        private bool IsCursorInsideMenu()
        {
            if (_hwnd == IntPtr.Zero) return false;
            if (!Win32.GetCursorPos(out var pt)) return true; // 取不到就当作在里面，宁可多等一帧也别误关
            if (!Win32.GetWindowRect(_hwnd, out var r)) return false;

            return pt.x >= r.Left && pt.x < r.Right && pt.y >= r.Top && pt.y < r.Bottom;
        }

        private void DismissNow()
        {
            Win32.ReleaseCapture();
            RequestDismiss();
        }

        // ---- 绘制 ----

        private unsafe void Render()
        {
            if (_hwnd == IntPtr.Zero || _pixelWidth <= 0 || _pixelHeight <= 0) return;

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

            var full = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
            canvas.DrawRoundRect(full, radius, radius, _bgPaint);
            canvas.DrawRoundRect(full, radius, radius, _borderPaint);

            DrawItems(canvas);

            canvas.Flush();
            UpdateLayeredContent();
        }

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

                _textPaint!.TextSize = item.Action == MenuAction.Header ? TEXT_TITLE_SIZE : TEXT_SIZE;
                _textPaint.Color = item.Action == MenuAction.Header
                    ? COLOR_TEXT_DISABLED
                    : item.Enabled ? COLOR_TEXT : COLOR_TEXT_DISABLED;

                float baseline = rect.MidY - (_textPaint.FontMetrics.Ascent + _textPaint.FontMetrics.Descent) / 2f;
                float textX = TEXT_LEFT;

                if (item.IsCheckable)
                {
                    if (item.Checked)
                    {
                        float cx = TEXT_LEFT + 5;   // 勾选块留在缩进槽里，但别贴着菜单左边缘
                        float cy = rect.MidY;
                        canvas.DrawLine(cx - 5.5f, cy + 0.5f, cx - 2f, cy + 4f, _checkPaint);
                        canvas.DrawLine(cx - 2f, cy + 4f, cx + 5f, cy - 4f, _checkPaint);
                    }

                    textX = TEXT_LEFT + CHECK_SLOT;
                }

                canvas.DrawText(item.Text, textX, baseline, _textPaint);
            }
        }

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
