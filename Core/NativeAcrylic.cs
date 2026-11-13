using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace NotchPeninsula;

internal sealed class NativeAcrylic : IDisposable
{
    private const int UseHostBackdropBrush = 17;
    private const int NoRedirectionBitmap = 0x00200000;
    private const int Transparent = 0x20;
    private const uint NextWindow = 2;
    private const string WindowClass = "NotchPeninsulaNativeAcrylic";
    private static readonly Win32.WndProc WindowProc = ProcessWindowMessage;

    internal static readonly bool Enabled = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
#if !NATIVE_ACRYLIC_PREVIEW
        && Array.IndexOf(Environment.GetCommandLineArgs(), "--native-acrylic") >= 0
#endif
        ;

    private IntPtr _window;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _visual;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionPathGeometry? _pathGeometry;
    private CompositionGeometricClip? _clip;
    private CompositionBackdropBrush? _brush;
    private (int X, int Y, int Width, int Height) _bounds;
    private float _radius = -1f;
    private (Vector2 Offset, Vector2 Size, IslandOutline Outline)? _path;
    private float _opacity = 1f;
    private bool _visible;

    internal NativeAcrylic()
    {
        try
        {
            var module = GetModuleHandle(null);
            var wc = new Win32.WNDCLASS
            {
                hInstance = module,
                lpfnWndProc = WindowProc,
                lpszClassName = WindowClass
            };
            Win32.RegisterClass(ref wc);
            _window = Win32.CreateWindowEx(
                NoRedirectionBitmap | Transparent | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                WindowClass, "NotchPeninsula Acrylic", Win32.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_window == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            int enabled = 1;
            Marshal.ThrowExceptionForHR(Win32.DwmSetWindowAttribute(_window, UseHostBackdropBrush, ref enabled, sizeof(int)));

            _compositor = new Compositor();
            _target = CreateDesktopTarget(_compositor, _window);
            _geometry = _compositor.CreateRoundedRectangleGeometry();
            _clip = _compositor.CreateGeometricClip(_geometry);
            _brush = _compositor.CreateHostBackdropBrush();
            _visual = _compositor.CreateSpriteVisual();
            _visual.Clip = _clip;
            _visual.Brush = _brush;
            _target.Root = _visual;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Present(IntPtr foreground, int x, int y, int width, int height,
        Vector2 offset, Vector2 size, IslandOutline outline, bool capsule, float opacity)
    {
        var bounds = (x, y, width, height);
        bool resized = width != _bounds.Width || height != _bounds.Height;
        if (resized)
        {
            _visual!.Size = new Vector2(width, height);
        }
        if (capsule)
        {
            if (_geometry!.Size != size) _geometry.Size = size;
            if (_geometry.Offset != offset) _geometry.Offset = offset;
            if (outline.Bottom != _radius)
            {
                _geometry.CornerRadius = new Vector2(outline.Bottom);
                _radius = outline.Bottom;
            }
            if (_path != null)
            {
                _clip!.Geometry = _geometry;
                _path = null;
            }
        }
        else if (_path != (offset, size, outline))
        {
            _pathGeometry ??= _compositor!.CreatePathGeometry();
            _pathGeometry.Path = new CompositionPath(new AcrylicClipGeometry(offset.X, offset.Y, size.X, size.Y, outline));
            if (_path == null) _clip!.Geometry = _pathGeometry;
            _path = (offset, size, outline);
        }
        if (opacity != _opacity)
        {
            _visual!.Opacity = opacity;
            _opacity = opacity;
        }
        if (!_visible || bounds != _bounds || GetWindow(foreground, NextWindow) != _window)
        {
            if (!Win32.SetWindowPos(_window, foreground, x, y, width, height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _bounds = bounds;
            _visible = true;
        }
    }

    internal void Hide()
    {
        if (!_visible) return;
        Win32.ShowWindow(_window, Win32.SW_HIDE);
        _visible = false;
    }

    public void Dispose()
    {
        if (_target != null)
        {
            _target.Root = null;
            _target = null;
        }
        _visual?.Dispose();
        _visual = null;
        _brush?.Dispose();
        _brush = null;
        _clip?.Dispose();
        _clip = null;
        _pathGeometry?.Dispose();
        _pathGeometry = null;
        _geometry?.Dispose();
        _geometry = null;
        _compositor?.Dispose();
        _compositor = null;
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    internal static DispatcherQueueController CreateDispatcher()
    {
        // DQTYPE_THREAD_CURRENT + DQTAT_COM_NONE: the render thread is already STA.
        var options = new DispatcherOptions { Size = Marshal.SizeOf<DispatcherOptions>(), ThreadType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out IntPtr abi));
        try { return WinRT.MarshalInspectable<DispatcherQueueController>.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    internal static void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessage(ref message);
        }
    }

    private static IntPtr ProcessWindowMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => msg == Win32.WM_NCHITTEST ? (IntPtr)Win32.HTTRANSPARENT
            : Win32.DefWindowProc(hwnd, msg, wParam, lParam);

    private static unsafe DesktopWindowTarget CreateDesktopTarget(Compositor compositor, IntPtr window)
    {
        var reference = ((WinRT.IWinRTObject)compositor).NativeObject;
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, in iid, out IntPtr interop));
        try
        {
            var createTarget = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(IntPtr**)interop)[3];
            IntPtr abi;
            Marshal.ThrowExceptionForHR(createTarget(interop, window, 0, &abi));
            try { return WinRT.MarshalInspectable<DesktopWindowTarget>.FromAbi(abi); }
            finally { Marshal.Release(abi); }
        }
        finally { Marshal.Release(interop); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherOptions options, out IntPtr controller);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out Win32.MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
}
