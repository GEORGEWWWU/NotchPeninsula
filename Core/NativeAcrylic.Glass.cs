using System.Numerics;
using Windows.UI.Composition;

namespace NotchPeninsula;

internal sealed partial class NativeAcrylic
{
    private SpriteVisual? _edge;
    private CompositionPathGeometry? _edgeGeometry;
    private CompositionGeometricClip? _edgeClip;
    private CompositionEffectBrush? _edgeBrush;
    private (Vector2 Offset, Vector2 Size, IslandOutline Outline, float Inset)? _edgeShape;
    private (Vector2 Center, Vector2 Scale)? _edgeTransform;
    private bool _edgeFailed;
    private bool _edgeAttached;

    private void PresentGlassEdge(bool glass, Vector2 offset, Vector2 size, IslandOutline outline,
        Vector2 hostSize, float dpi, float opacity)
    {
        float shift = glass ? GlassMaterial.RefractionPixels * dpi : 0f;
        if (_edgeFailed || shift <= 0f || Math.Min(size.X, size.Y) < 4f)
        {
            if (_edge is { IsVisible: true }) _edge.IsVisible = false;
            return;
        }

        try
        {
            if (_edge == null)
            {
                var compositor = _compositor!;
                var source = new CompositionEffectSourceParameter(GlassRefractionEffect.SourceName);
                using var factory = compositor.CreateEffectFactory(new GlassRefractionEffect(source),
                    [GlassRefractionEffect.TransformProperty]);
                _edgeBrush = factory.CreateBrush();
                _edgeBrush.SetSourceParameter(GlassRefractionEffect.SourceName, _brush);
                _edgeGeometry = compositor.CreatePathGeometry();
                _edgeClip = compositor.CreateGeometricClip(_edgeGeometry);
                _edge = compositor.CreateSpriteVisual();
                _edge.Brush = _edgeBrush;
                _edge.Clip = _edgeClip;
                _root!.Children.InsertAtTop(_edge);
                _edgeAttached = true;
                Logger.Info("[液态玻璃] 已建立 Composition 边缘折射");
            }

            float inset = Math.Min(GlassMaterial.EdgeWidth * dpi, Math.Min(size.X, size.Y) * 0.35f);
            var shape = (offset, size, outline, inset);
            if (_edgeShape != shape)
            {
                _edgeGeometry!.Path = new CompositionPath(new AcrylicClipGeometry(
                    offset.X, offset.Y, size.X, size.Y, outline, inset));
                _edgeShape = shape;
            }

            // Magnify around the island centre so the rim bends the background
            // towards the glass interior, with a bounded displacement at each side.
            var center = offset + size / 2f;
            var scale = Vector2.One + new Vector2(shift) / Vector2.Max(size / 2f, new Vector2(2f * shift));
            if (_edgeTransform != (center, scale))
            {
                _edgeBrush!.Properties.InsertMatrix3x2(GlassRefractionEffect.TransformProperty,
                    Matrix3x2.CreateScale(scale, center));
                _edgeTransform = (center, scale);
            }
            if (_edge.Size != hostSize) _edge.Size = hostSize;
            float edgeOpacity = opacity * 0.82f;
            if (_edge.Opacity != edgeOpacity) _edge.Opacity = edgeOpacity;
            if (!_edge.IsVisible) _edge.IsVisible = true;
        }
        catch (Exception ex)
        {
            _edgeFailed = true;
            DisposeEdge();
            Logger.Error("[液态玻璃] 边缘折射初始化或更新失败，保留 Composition 背板与高光", ex);
        }
    }

    private readonly List<LiveGlassVisual> _liveGlass = [];
    internal bool LiveGlassAvailable { get; private set; } = true;

    private void PresentLiveGlass(ReadOnlySpan<GlassMaterial.Region> regions, Vector2 canvasOrigin, float dpi)
    {
        if (!LiveGlassAvailable) return;
        try
        {
            for (int i = 0; i < regions.Length; i++)
            {
                if (i == _liveGlass.Count)
                {
                    var visual = new LiveGlassVisual(_compositor!, _brush!);
                    _liveGlass.Add(visual);
                    _root!.Children.InsertAtTop(visual.Visual);
                }
                _liveGlass[i].Present(regions[i], canvasOrigin, dpi);
            }
            for (int i = regions.Length; i < _liveGlass.Count; i++)
            {
                if (_liveGlass[i].Visual.IsVisible) _liveGlass[i].Visual.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            LiveGlassAvailable = false;
            foreach (var live in _liveGlass) live.Visual.IsVisible = false;
            Logger.Error("[液态玻璃] 实况窗背板失败，使用半透明色调和高光", ex);
        }
    }

    private sealed class LiveGlassVisual : IDisposable
    {
        internal SpriteVisual Visual { get; }
        private readonly CompositionPathGeometry _geometry;
        private readonly CompositionGeometricClip _clip;
        private (Vector2 Size, float Radius, float Inset)? _shape;

        internal LiveGlassVisual(Compositor compositor, CompositionBackdropBrush brush)
        {
            _geometry = compositor.CreatePathGeometry();
            _clip = compositor.CreateGeometricClip(_geometry);
            Visual = compositor.CreateSpriteVisual();
            Visual.Clip = _clip;
            Visual.Brush = brush;
        }

        internal void Present(in GlassMaterial.Region region, Vector2 canvasOrigin, float dpi)
        {
            var rect = region.Rect;
            var size = new Vector2(rect.Width, rect.Height) * dpi;
            var offset = new Vector3(new Vector2(rect.Left, rect.Top) * dpi + canvasOrigin, 0f);
            float radius = region.Radius * dpi;
            float inset = region.InnerRadius > 0f ? (region.Radius - region.InnerRadius) * dpi : 0f;
            if (_shape != (size, radius, inset))
            {
                _geometry.Path = new CompositionPath(new AcrylicClipGeometry(
                    0f, 0f, size.X, size.Y, new IslandOutline(radius, radius, radius), inset));
                _shape = (size, radius, inset);
            }
            if (Visual.Size != size) Visual.Size = size;
            if (Visual.Offset != offset) Visual.Offset = offset;
            float opacity = GlassMaterial.BackdropOpacity * region.Opacity;
            if (Visual.Opacity != opacity) Visual.Opacity = opacity;
            if (!Visual.IsVisible) Visual.IsVisible = true;
        }

        public void Dispose()
        {
            Visual.Dispose();
            _clip.Dispose();
            _geometry.Dispose();
        }
    }

    private void DisposeEdge()
    {
        if (_edge != null)
        {
            if (_edgeAttached) _root!.Children.Remove(_edge);
            _edge.Dispose();
            _edge = null;
            _edgeAttached = false;
        }
        _edgeBrush?.Dispose();
        _edgeClip?.Dispose();
        _edgeGeometry?.Dispose();
        _edgeBrush = null;
        _edgeClip = null;
        _edgeGeometry = null;
    }

    private void DisposeGlass()
    {
        DisposeEdge();
        foreach (var live in _liveGlass) live.Dispose();
        _liveGlass.Clear();
    }
}
