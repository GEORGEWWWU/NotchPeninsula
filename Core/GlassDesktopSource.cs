using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.UI.Composition;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace NotchPeninsula;

// A live DWM visual reference, not a stream of captured frames. The desktop stays in
// the compositor; the application never obtains, copies or maps desktop pixels.
// Private DWM ABI documented by ADeltaX (see the README).
internal sealed unsafe class GlassDesktopSource : IDisposable
{
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
        && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 28000);

    private ID3D11Device? _d3d;
    private ID2D1Device? _d2d;
    private IDCompositionDesktopDevice? _device;
    private IDCompositionTarget? _target;
    private IDCompositionVisual2? _sharedVisual, _container;
    private nint _partner, _module, _thumbnail;
    private delegate* unmanaged[Stdcall]<nint, nint, nint*, nint*, int> _create;
    private nint _update;
    private Visual? _sourceVisual;
    private CompositionTarget? _compositionTarget;
    private (int X, int Y, int Width, int Height, nint Foreground)? _bounds;
    private readonly nint _window;
    private readonly WinEventProc _onWindowChanged;
    private nint _foregroundHook, _windowsHook;
    private int _revision, _appliedRevision = -1;
    private nint _foreground;

    internal Compositor Compositor { get; private set; } = null!;
    internal CompositionVisualSurface Surface { get; private set; } = null!;

    internal GlassDesktopSource(nint window)
    {
        _window = window;
        _onWindowChanged = (_, _, changedWindow, objectId, childId, _, _) =>
        {
            if (changedWindow != _window && changedWindow != _foreground && objectId == 0 && childId == 0)
                Interlocked.Increment(ref _revision);
        };
        try
        {
            if (!IsSupported) throw new PlatformNotSupportedException("DWM shared-visual ABI is unavailable on this Windows build.");
            _module = NativeLibrary.Load("dwmapi.dll", typeof(GlassDesktopSource).Assembly,
                DllImportSearchPath.System32);
            _create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, nint*, int>)GetProcAddress(_module, 163);
            _update = GetProcAddress(_module, 164);
            if (_create == null || _update == 0) throw new EntryPointNotFoundException("DWM shared-visual exports 163/164.");

            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out _d3d).CheckError();
            using var dxgi = _d3d!.QueryInterface<IDXGIDevice>();
            using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
            _d2d = factory.CreateDevice(dxgi);

            nint className = WinRT.MarshalString.FromManaged("Windows.UI.Composition.Compositor");
            var factoryId = new Guid("22118ADF-23F1-4801-BCFA-66CBF48CC51B");
            nint activation = 0;
            try
            {
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, in factoryId, out activation));
                var create = (delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)(*(nint**)activation)[6];
                var partnerId = new Guid("E7894C70-AF56-4F52-B382-4B3CD263DC6F");
                nint partner;
                Marshal.ThrowExceptionForHR(create(activation, _d2d.NativePointer, 0, &partnerId, &partner));
                _partner = partner;
            }
            finally
            {
                if (activation != 0) Marshal.Release(activation);
                WinRT.MarshalString.DisposeAbi(className);
            }

            Compositor = Project<Compositor>(_partner);
            var deviceId = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_partner, in deviceId, out nint device));
            _device = new IDCompositionDesktopDevice(device);
            _device.CreateTargetForHwnd(window, true, out _target).CheckError();
            _compositionTarget = Project<CompositionTarget>(_target.NativePointer);

            // Excluding this host from live previews is required by DWM's shared desktop.
            SetPreviewExclusion(window);
            nint visual, thumbnail;
            Marshal.ThrowExceptionForHR(_create(window, device, &visual, &thumbnail));
            _sharedVisual = new IDCompositionVisual2(visual);
            _thumbnail = thumbnail;
            _device.CreateVisual(out _container).CheckError();
            _container.AddVisual(_sharedVisual, true, null).CheckError();
            _sourceVisual = Project<Visual>(_container.NativePointer);
            Surface = Compositor.CreateVisualSurface();
            // Keep the source detached: only its surface is shown. No recursive backdrop.
            Surface.SourceVisual = _sourceVisual;
            // Refresh membership/z-order on window events, not on a frame-capture timer.
            _foregroundHook = SetWinEventHook(3, 3, 0, _onWindowChanged, 0, 0, 0);
            _windowsHook = SetWinEventHook(0x8000, 0x8004, 0, _onWindowChanged, 0, 0, 0);
            if (_foregroundHook == 0 || _windowsHook == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    internal void SetRoot(ContainerVisual root) => _compositionTarget!.Root = root;

    internal bool Update(nint foreground, int x, int y, int width, int height)
    {
        _foreground = foreground;
        var bounds = (x, y, width, height, foreground);
        int revision = Volatile.Read(ref _revision);
        if (_bounds == bounds && _appliedRevision == revision) return false;
        var source = new Win32.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var destination = new Win32.SIZE { cx = width, cy = height };
        nint* excluded = stackalloc nint[2] { foreground, _window };
        int hr = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, uint, Win32.RECT*, Win32.SIZE*, uint, int>)_update)
                (_thumbnail, 0, 0, excluded, 2, &source, &destination, 1)
            : ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, uint, Win32.RECT*, Win32.SIZE*, int>)_update)
                (_thumbnail, 0, 0, excluded, 2, &source, &destination);
        Marshal.ThrowExceptionForHR(hr);
        var size = new Vector2(width, height);
        _sourceVisual!.Size = size;
        Surface.SourceSize = size;
        _bounds = bounds;
        _appliedRevision = revision;
        return true;
    }

    internal void Commit() => _device!.Commit().CheckError();

    private static T Project<T>(nint unknown)
    {
        // Vortice exposes IUnknown vtables; the WinRT marshaler receives IInspectable.
        var inspectableId = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in inspectableId, out nint inspectable));
        try { return WinRT.MarshalInspectable<T>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    public void Dispose()
    {
        if (_foregroundHook != 0) { UnhookWinEvent(_foregroundHook); _foregroundHook = 0; }
        if (_windowsHook != 0) { UnhookWinEvent(_windowsHook); _windowsHook = 0; }
        if (_compositionTarget != null) _compositionTarget.Root = null;
        if (Surface != null) { Surface.SourceVisual = null; Surface.Dispose(); }
        _container?.RemoveAllVisuals();
        _sourceVisual?.Dispose();
        _container?.Dispose();
        // Shared visual owns the thumbnail handle; do not DwmUnregisterThumbnail it.
        _sharedVisual?.Dispose();
        _compositionTarget?.Dispose();
        _target?.Dispose();
        _device?.Dispose();
        Compositor?.Dispose();
        if (_partner != 0)
        {
            // Interop compositor has an explicit close in addition to IClosable.
            var close = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)_partner)[6];
            close(_partner);
            Marshal.Release(_partner);
            _partner = 0;
        }
        _d2d?.Dispose();
        _d3d?.Dispose();
        if (_module != 0) { NativeLibrary.Free(_module); _module = 0; }
    }

    private static void SetPreviewExclusion(nint window)
    {
        int enabled = 1;
        var data = new CompositionAttribute { Attribute = 13, Data = (nint)(&enabled), Size = sizeof(int) };
        if (!SetWindowCompositionAttribute(window, ref data))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttribute { internal int Attribute; internal nint Data; internal nuint Size; }
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, nint ordinal);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(nint window, ref CompositionAttribute data);
    private delegate void WinEventProc(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
