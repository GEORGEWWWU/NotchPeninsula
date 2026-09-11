using NotchPeninsula.Plugins;
using SkiaSharp;

namespace HelloPlugin;

/// <summary>最小演示插件：注册一个文本组件。</summary>
public sealed class HelloPlugin : INotchPlugin
{
    public string Id => "hello";
    public string DisplayName => "Hello 演示插件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        host.RegisterWidget(new HelloWidget());
    }
}

/// <summary>一个自包含文本组件：验证插件 DLL 被加载并渲染。</summary>
public sealed class HelloWidget : IWidget
{
    public string Id => "hello.text";
    public string DisplayName => "Hello";
    public IDetailPage? DetailPage => null;

    private static readonly SKPaint _paint = new()
    {
        Color = SKColors.White,
        TextSize = 13f,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    public float MeasureWidth(float availableHeight) => _paint.MeasureText("插件已运行 ✓") + 32f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        _paint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText("插件已运行 ✓", rect.Left + 16f, rect.MidY + 5f, _paint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}
