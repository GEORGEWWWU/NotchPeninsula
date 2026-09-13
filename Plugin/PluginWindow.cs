using System.Runtime.InteropServices;
using SkiaSharp;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 插件自有窗口：Win32 分层窗口 + SkiaSharp 绘制 + 鼠标输入路由。
/// 在独立线程运行自己的消息循环。
/// </summary>
public sealed class PluginWindow : IPluginWindow
{
    private const uint WM_APP_REDRAW = 0x8000 + 1;
    private const int WM_CHAR = 0x0102;

    private readonly string _title;
    private readonly int _width, _height;
    private IntPtr _hwnd;
    private readonly Win32.WndProc _wndProc;
    private Action<SKCanvas, int, int>? _draw;
    private Action<float, float>? _mouseDown, _mouseMove, _mouseUp;
    private Action<char>? _key;

    private IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
    private SKSurface? _surface;
    private bool _closing;

    public PluginWindow(string title, int width, int height)
    {
        _title = title;
        _width = width;
        _height = height;
        _wndProc = WndProc;
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

    /// <summary>创建窗口并启动消息循环线程。</summary>
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

        // 屏幕居中
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        int x = screen.WorkingArea.Left + (screen.WorkingArea.Width - _width) / 2;
        int y = screen.WorkingArea.Top + (screen.WorkingArea.Height - _height) / 2;

        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED,
            "NPSPluginWindow", _title,
            Win32.WS_POPUP | Win32.WS_VISIBLE,
            x, y, _width, _height,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;
        InitBuffer();
        Redraw();

        var thread = new System.Threading.Thread(MessageLoop) { IsBackground = true };
        thread.Start();
    }

    private void MessageLoop()
    {
        while (!_closing && Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REDRAW:
                Redraw();
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                _mouseMove?.Invoke(Lo(lParam), Hi(lParam));
                return IntPtr.Zero;
            case Win32.WM_LBUTTONDOWN:
                _mouseDown?.Invoke(Lo(lParam), Hi(lParam));
                return IntPtr.Zero;
            case Win32.WM_LBUTTONUP:
                _mouseUp?.Invoke(Lo(lParam), Hi(lParam));
                return IntPtr.Zero;
            case WM_CHAR:
                _key?.Invoke((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case Win32.WM_CLOSE:
                _closing = true;
                CleanupBuffer();
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static int Lo(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int Hi(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private void Redraw()
    {
        if (_surface == null || _draw == null) return;
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        _draw(canvas, _width, _height);

        var screenDc = Win32.GetDC(IntPtr.Zero);
        var ptSrc = new Win32.POINT(0, 0);
        var ptDst = new Win32.POINT { x = 0, y = 0 };
        var size = new Win32.SIZE(_width, _height);
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

    private void InitBuffer()
    {
        var screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                biWidth = _width,
                biHeight = -_height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _pBits, _width * 4);
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
