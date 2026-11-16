using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace NotchPeninsula;

// Retained GPU composition: background changes do not require an app render tick.
// Inner-edge refraction is a piecewise lens, with real source-coordinate changes.
internal sealed class NativeGlass : IDisposable
{
    private const string WindowClass = "NotchPeninsulaSharedGlass";
    private static readonly Win32.WndProc WindowProc = (hwnd, msg, w, l) =>
        msg == Win32.WM_NCHITTEST ? (nint)Win32.HTTRANSPARENT : Win32.DefWindowProc(hwnd, msg, w, l);
    private nint _window;
    private GlassDesktopSource? _desktop;
    private ContainerVisual? _root;
    private CompositionSurfaceBrush? _rawBrush;
    private CompositionEffectBrush? _blurBrush;
    private SpriteVisual? _blurVisual;
    private CompositionVisualSurface? _blurSurface;
    private float _blurAmount = -1f;
    private (float Blur, float Thickness, float Strength)? _optics;
    private readonly List<Lens> _lenses = [];
    private (int X, int Y, int Width, int Height)? _bounds;
    private bool _visible;

    internal NativeGlass()
    {
        try
        {
            var module = GetModuleHandle(null);
            var wc = new Win32.WNDCLASS { hInstance = module, lpfnWndProc = WindowProc, lpszClassName = WindowClass };
            Win32.RegisterClass(ref wc);
            _window = Win32.CreateWindowEx(0x00200000 | 0x20 | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                WindowClass, "NotchPeninsula Glass", Win32.WS_POPUP, 0, 0, 1, 1, 0, 0, module, 0);
            if (_window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _desktop = new GlassDesktopSource(_window);
            var compositor = _desktop.Compositor;
            _root = compositor.CreateContainerVisual();
            _desktop.SetRoot(_root);
            using var blurFactory = compositor.CreateEffectFactory(new GlassBlurEffect(
                new CompositionEffectSourceParameter(GlassBlurEffect.SourceName)), [GlassBlurEffect.AmountProperty]);
            _rawBrush = compositor.CreateSurfaceBrush(_desktop.Surface);
            _rawBrush.Stretch = CompositionStretch.None;
            _rawBrush.HorizontalAlignmentRatio = _rawBrush.VerticalAlignmentRatio = 0f;
            _blurBrush = blurFactory.CreateBrush();
            _blurBrush.SetSourceParameter(GlassBlurEffect.SourceName, _rawBrush);
            _blurVisual = compositor.CreateSpriteVisual();
            _blurVisual.Brush = _blurBrush;
            _blurSurface = compositor.CreateVisualSurface();
            _blurSurface.SourceVisual = _blurVisual;
        }
        catch { Dispose(); throw; }
    }

    internal void Present(nint foreground, int x, int y, int width, int height, Vector2 offset, Vector2 size,
        IslandOutline outline, float opacity, float dpi, ReadOnlySpan<GlassMaterial.Region> regions, Vector2 canvasOrigin)
    {
        var bounds = (x, y, width, height);
        bool dirty = _bounds != bounds;
        if (dirty) _root!.Size = new Vector2(width, height);
        float blur = GlassMaterial.BlurAmount * dpi;
        float thickness = GlassMaterial.RefractionThickness * dpi;
        float strength = GlassMaterial.RefractionAmount * dpi;
        int padding = (int)MathF.Ceiling(3f * blur + strength + 4f);
        // Clamp to the virtual desktop, retaining the exact crop offset on negative-coordinate monitors.
        int sx = Math.Max(x - padding, GetSystemMetrics(76));
        int sy = Math.Max(y - padding, GetSystemMetrics(77));
        int right = Math.Min(x + width + padding, GetSystemMetrics(76) + GetSystemMetrics(78));
        int bottom = Math.Min(y + height + padding, GetSystemMetrics(77) + GetSystemMetrics(79));
        if (right <= sx || bottom <= sy) { Hide(); return; }
        dirty |= _desktop!.Update(foreground, sx, sy, right - sx, bottom - sy);
        // Blur once for all lenses and bands, instead of running one blur per rim slice.
        var sourceSize = new Vector2(right - sx, bottom - sy);
        if (_blurSurface!.SourceSize != sourceSize)
        {
            _blurSurface.SourceSize = _blurVisual!.Size = sourceSize;
            dirty = true;
        }
        if (_blurAmount != blur)
        {
            _blurBrush!.Properties.InsertScalar(GlassBlurEffect.AmountProperty, blur);
            _blurAmount = blur;
            dirty = true;
        }
        if (_optics != (blur, thickness, strength))
        {
            _optics = (blur, thickness, strength);
            Logger.Info($"[液态玻璃] 共享视觉模糊={blur:F2}px；内缘折射={strength:F2}px；厚度={thickness:F2}px");
        }
        var surface = blur == 0f ? _desktop.Surface : _blurSurface;
        var sourceOffset = new Vector2(sx - x, sy - y);
        var canvasSize = new Vector2(width, height);
        dirty |= GetLens(0).Present(surface, canvasSize, sourceOffset, offset, size, outline, 0f, opacity, thickness, strength);
        for (int i = 0; i < regions.Length; i++)
        {
            ref readonly var region = ref regions[i];
            var rect = region.Rect;
            float radius = region.Radius * dpi;
            float inset = region.InnerRadius > 0f ? (region.Radius - region.InnerRadius) * dpi : 0f;
            dirty |= GetLens(i + 1).Present(surface, canvasSize, sourceOffset,
                new Vector2(rect.Left, rect.Top) * dpi + canvasOrigin,
                new Vector2(rect.Width, rect.Height) * dpi, new IslandOutline(radius, radius, radius),
                inset, region.Opacity, thickness, strength);
        }
        for (int i = regions.Length + 1; i < _lenses.Count; i++) dirty |= _lenses[i].Hide();
        if (dirty) _desktop.Commit();
        if (!_visible || _bounds != bounds || GetWindow(foreground, 2) != _window)
        {
            if (!Win32.SetWindowPos(_window, foreground, x, y, width, height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _bounds = bounds;
            _visible = true;
        }
    }

    private Lens GetLens(int index)
    {
        if (index == _lenses.Count)
        {
            var lens = new Lens(_desktop!.Compositor, _desktop.Surface);
            _lenses.Add(lens);
            _root!.Children.InsertAtTop(lens.Visual);
        }
        return _lenses[index];
    }

    internal void Hide()
    {
        if (!_visible) return;
        Win32.ShowWindow(_window, Win32.SW_HIDE);
        _visible = false;
    }

    public void Dispose()
    {
        Hide();
        _root?.Children.RemoveAll();
        foreach (var lens in _lenses) lens.Dispose();
        _lenses.Clear();
        if (_blurSurface != null) _blurSurface.SourceVisual = null;
        _blurSurface?.Dispose();
        _blurVisual?.Dispose();
        _blurBrush?.Dispose();
        _rawBrush?.Dispose();
        _root?.Dispose();
        _desktop?.Dispose();
        if (_window != 0) { DestroyWindow(_window); _window = 0; }
    }

    private sealed class Lens : IDisposable
    {
        // Narrow distance bands approximate a smooth curved lens; no full-screen draw per band.
        private const int BandCount = 12;
        internal ContainerVisual Visual { get; }
        private readonly CompositionPathGeometry _geometry;
        private readonly CompositionGeometricClip _clip;
        private readonly Layer[] _layers = new Layer[BandCount + 1];
        private (Vector2 Offset, Vector2 Size, IslandOutline Outline, float Inset, float Thickness)? _shape;
        private (CompositionVisualSurface Surface, Vector2 Canvas, Vector2 SourceOffset, float Strength)? _material;

        internal Lens(Compositor compositor, CompositionVisualSurface source)
        {
            Visual = compositor.CreateContainerVisual();
            _geometry = compositor.CreatePathGeometry();
            _clip = compositor.CreateGeometricClip(_geometry);
            Visual.Clip = _clip;
            for (int i = 0; i < _layers.Length; i++)
            {
                _layers[i] = new Layer(compositor, source, i != 0);
                Visual.Children.InsertAtTop(_layers[i].Visual);
            }
        }

        internal bool Present(CompositionVisualSurface surface, Vector2 canvas, Vector2 sourceOffset, Vector2 offset, Vector2 size,
            IslandOutline outline, float inset, float opacity, float thickness, float strength)
        {
            thickness = Math.Min(thickness, Math.Min(size.X, size.Y) * 0.4f);
            bool shapeChanged = _shape != (offset, size, outline, inset, thickness);
            bool dirty = shapeChanged || _material != (surface, canvas, sourceOffset, strength);
            if (shapeChanged)
            {
                _geometry.Path = new CompositionPath(new AcrylicClipGeometry(offset.X, offset.Y, size.X, size.Y, outline, inset));
                for (int i = 1; i < _layers.Length; i++)
                    _layers[i].Geometry!.Path = new CompositionPath(new AcrylicClipGeometry(offset.X, offset.Y,
                        size.X, size.Y, outline, inset, thickness * (i - 1) / BandCount, thickness * i / BandCount));
                _shape = (offset, size, outline, inset, thickness);
            }
            if (dirty)
            {
                Visual.Size = canvas;
                var center = offset + size / 2f;
                for (int i = 0; i < _layers.Length; i++)
                {
                    // Zero displacement in the center, maximum near the outer inner edge.
                    float t = i == 0 ? 1f : (i - 0.5f) / BandCount;
                    float displacement = strength * (1f - t) * (1f - t);
                    float scaleX = size.X / (size.X + 2f * displacement);
                    float scaleY = size.Y / (size.Y + 2f * displacement);
                    var matrix = Matrix3x2.CreateTranslation(sourceOffset)
                        * Matrix3x2.CreateScale(scaleX, scaleY, center);
                    _layers[i].Present(surface, canvas, matrix, i == 0 || strength > 0f);
                }
                _material = (surface, canvas, sourceOffset, strength);
            }
            if (Visual.Opacity != opacity) { Visual.Opacity = opacity; dirty = true; }
            if (!Visual.IsVisible) { Visual.IsVisible = true; dirty = true; }
            return dirty;
        }

        internal bool Hide()
        {
            if (!Visual.IsVisible) return false;
            Visual.IsVisible = false;
            return true;
        }

        public void Dispose()
        {
            Visual.Children.RemoveAll();
            foreach (var layer in _layers) layer.Dispose();
            Visual.Dispose();
            _clip.Dispose();
            _geometry.Dispose();
        }
    }

    private sealed class Layer : IDisposable
    {
        internal SpriteVisual Visual { get; }
        internal CompositionPathGeometry? Geometry { get; }
        private readonly CompositionGeometricClip? _clip;
        private readonly CompositionSurfaceBrush _source;

        internal Layer(Compositor compositor, CompositionVisualSurface surface, bool band)
        {
            Visual = compositor.CreateSpriteVisual();
            _source = compositor.CreateSurfaceBrush(surface);
            _source.Stretch = CompositionStretch.None;
            _source.HorizontalAlignmentRatio = _source.VerticalAlignmentRatio = 0f;
            Visual.Brush = _source;
            if (band)
            {
                Geometry = compositor.CreatePathGeometry();
                _clip = compositor.CreateGeometricClip(Geometry);
                Visual.Clip = _clip;
            }
        }

        internal void Present(CompositionVisualSurface surface, Vector2 size, Matrix3x2 transform, bool visible)
        {
            Visual.Size = size;
            Visual.IsVisible = visible;
            _source.TransformMatrix = transform;
            if (!ReferenceEquals(_source.Surface, surface)) _source.Surface = surface;
        }

        public void Dispose()
        {
            Visual.Dispose();
            _source.Dispose();
            _clip?.Dispose();
            Geometry?.Dispose();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
