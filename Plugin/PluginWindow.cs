using System.Runtime.InteropServices;
using SkiaSharp;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 插件自有窗口：Win32 分层窗口 + SkiaSharp 绘制 + 鼠标/键盘输入路由。
/// DPI 感知居中显示，右上角带关闭按钮，Esc 可关闭，置顶以阻止下方交互。
/// 消息通过静态 WndProc + 字典按 hwnd 路由到对应实例，支持多窗口、可重复开关。
/// </summary>
public sealed class PluginWindow : IPluginWindow
{
    private const uint WM_APP_REDRAW = 0x8000 + 1;
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    // WndProc 必须是静态方法（避免委托被 GC 后回调悬空），用字典按 hwnd 找回实例
    private static readonly Dictionary<IntPtr, PluginWindow> _windows = new();
    private static readonly Win32.WndProc _wndProc = WndProc;

    private readonly string _title;
    private readonly int _width, _height;
    private float _dpiScale = 1f;
    private int _scaledWidth, _scaledHeight;
    private IntPtr _hwnd;
    private Action<SKCanvas, int, int>? _draw;
    private Action<float, float>? _mouseDown, _mouseMove, _mouseUp;
    private Action<char>? _key;

    private IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
    private SKSurface? _surface;
    private bool _closing;
    private int _posX, _posY;

    public PluginWindow(string title, int width, int height)
    {
        _title = title;
        _width = width;
        _height = height;
    }

    public void SetDraw(Action<SKCanvas, int, int>? draw)
    {
        _draw = draw;
        RequestRedraw();
    }

    public void SetMouse(Action<float, float>? down, Action<float, float>? move, Action<float, float>? up)
    {
        _mouseDown = down;
        _mouseMove = move;
        _mouseUp = up;
    }

    public void SetKey(Action<char>? key) => _key = key;

    public void RequestRedraw()
    {
        if (_hwnd != IntPtr.Zero && !_closing)
            Win32.PostMessage(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>创建窗口并注册到消息路由表。</summary>
    public void Show()
    {
        var wc = new Win32.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
            lpszClassName = "NPSPluginWindow",
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
        };
        if (Win32.RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* CLASS_ALREADY_EXISTS */)
            return;

        // DPI 感知：尺寸按系统 DPI 缩放，居中于主屏工作区（物理像素），并兼容无主屏场景
        _dpiScale = Win32.GetDpiForSystem() / 96f;
        _scaledWidth = (int)(_width * _dpiScale);
        _scaledHeight = (int)(_height * _dpiScale);
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, _scaledWidth, _scaledHeight);
        int x = area.Left + (area.Width - _scaledWidth) / 2;
        int y = area.Top + (area.Height - _scaledHeight) / 2;

        // 置顶（阻止下方交互）+ 工具窗口（不进任务栏）+ 分层（透明绘制）
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST,
            "NPSPluginWindow", _title,
            Win32.WS_POPUP | Win32.WS_VISIBLE,
            x, y, _scaledWidth, _scaledHeight,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;
        lock (_windows) _windows[_hwnd] = this;
        _posX = x;
        _posY = y;
        Win32.SetForegroundWindow(_hwnd); // 激活窗口，让 Esc/键盘输入立即生效
        InitBuffer();
        Redraw();
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        lock (_windows)
        {
            if (_windows.TryGetValue(hwnd, out var self))
                return self.HandleMessage(hwnd, msg, wParam, lParam);
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REDRAW:
                Redraw();
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                _mouseMove?.Invoke(LogicalX(lParam), LogicalY(lParam));
                return IntPtr.Zero;
            case Win32.WM_LBUTTONDOWN:
            {
                float lx = LogicalX(lParam), ly = LogicalY(lParam);
                if (IsCloseButtonHit(lx, ly)) { Close(); return IntPtr.Zero; }
                _mouseDown?.Invoke(lx, ly);
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONUP:
                _mouseUp?.Invoke(LogicalX(lParam), LogicalY(lParam));
                return IntPtr.Zero;
            case WM_CHAR:
                _key?.Invoke((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            case WM_KEYDOWN:
                if (wParam.ToInt64() == VK_ESCAPE) { Close(); return IntPtr.Zero; }
                break;

            case Win32.WM_CLOSE:
                _closing = true;
                CleanupBuffer();
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                lock (_windows) _windows.Remove(hwnd);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private float LogicalX(IntPtr lParam) => Lo(lParam) / _dpiScale;
    private float LogicalY(IntPtr lParam) => Hi(lParam) / _dpiScale;
    private bool IsCloseButtonHit(float x, float y) => x >= _width - 28 && x <= _width - 8 && y >= 8 && y <= 28;

    private static int Lo(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int Hi(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private void Redraw()
    {
        if (_surface == null || _draw == null) return;
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_dpiScale); // 让插件按逻辑坐标绘制
        _draw(canvas, _width, _height);
        DrawCloseButton(canvas);
        canvas.Restore();

        var screenDc = Win32.GetDC(IntPtr.Zero);
        var ptSrc = new Win32.POINT(0, 0);
        var ptDst = new Win32.POINT { x = _posX, y = _posY };
        var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA
        };
        Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void DrawCloseButton(SKCanvas canvas)
    {
        var paint = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true, StrokeWidth = 2f };
        canvas.DrawLine(_width - 24, 12, _width - 12, 24, paint);
        canvas.DrawLine(_width - 12, 12, _width - 24, 24, paint);
    }

    private void InitBuffer()
    {
        var screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                biWidth = _scaledWidth,
                biHeight = -_scaledHeight,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
        var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _pBits, _scaledWidth * 4);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void CleanupBuffer()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
        if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
    }
}
