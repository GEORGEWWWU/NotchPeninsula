using System.Numerics;
using Windows.UI.Composition;

namespace NotchPeninsula;

internal sealed partial class NativeAcrylic
{
    private CompositionEffectBrush? _blurBrush;
    private CompositionBrush? _appliedBackdrop;
    private float _blurAmount = -1f;
    private bool _blurFailed;

    private CompositionBrush SelectBackdrop(float amount)
    {
        if (amount == 0f || _blurFailed) return _brush!;

        try
        {
            if (_blurBrush == null)
            {
                var source = new CompositionEffectSourceParameter(GlassBlurEffect.SourceName);
                using var factory = _compositor!.CreateEffectFactory(new GlassBlurEffect(source),
                    [GlassBlurEffect.AmountProperty]);
                _blurBrush = factory.CreateBrush();
                _blurBrush.SetSourceParameter(GlassBlurEffect.SourceName, _brush);
            }

            if (_blurAmount != amount)
            {
                _blurBrush.Properties.InsertScalar(GlassBlurEffect.AmountProperty, amount);
                _blurAmount = amount;
                Logger.Info($"[液态玻璃] Composition 高斯模糊={amount} (Host Backdrop)");
            }
            return _blurBrush;
        }
        catch (Exception ex)
        {
            _blurFailed = true;
            GlassMaterial.BlurUnavailable = true;
            Logger.Error("[液态玻璃] Composition 高斯模糊失败，使用原生背板和高光", ex);
            return _brush!;
        }
    }

    private readonly List<LiveGlassVisual> _liveGlass = [];
    internal bool LiveGlassAvailable { get; private set; } = true;

    private void PresentLiveGlass(ReadOnlySpan<GlassMaterial.Region> regions, Vector2 canvasOrigin, float dpi,
        CompositionBrush backdrop, float backdropOpacity)
    {
        if (!LiveGlassAvailable) return;
        try
        {
            for (int i = 0; i < regions.Length; i++)
            {
                if (i == _liveGlass.Count)
                {
                    var visual = new LiveGlassVisual(_compositor!);
                    _liveGlass.Add(visual);
                    _root!.Children.InsertAtTop(visual.Visual);
                }
                _liveGlass[i].Present(regions[i], canvasOrigin, dpi, backdrop, backdropOpacity);
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

        private CompositionBrush? _appliedBackdrop;

        internal LiveGlassVisual(Compositor compositor)
        {
            _geometry = compositor.CreatePathGeometry();
            _clip = compositor.CreateGeometricClip(_geometry);
            Visual = compositor.CreateSpriteVisual();
            Visual.Clip = _clip;
        }

        internal void Present(in GlassMaterial.Region region, Vector2 canvasOrigin, float dpi,
            CompositionBrush backdrop, float backdropOpacity)
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
            if (_appliedBackdrop != backdrop)
            {
                Visual.Brush = backdrop;
                _appliedBackdrop = backdrop;
            }
            float opacity = backdropOpacity * region.Opacity;
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

    private void DisposeGlass()
    {
        foreach (var live in _liveGlass) live.Dispose();
        _liveGlass.Clear();
        _blurBrush?.Dispose();
    }
}
