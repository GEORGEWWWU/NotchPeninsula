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
    public IDetailPage? DetailPage { get; } = new HelloDetailPage();

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

/// <summary>Hello 组件的详情页，右键组件打开。</summary>
public sealed class HelloDetailPage : IDetailPage
{
    private static readonly SKPaint _titlePaint = new()
    {
        Color = SKColors.White,
        TextSize = 15f,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _bodyPaint = new()
    {
        Color = new SKColor(200, 200, 200),
        TextSize = 12f,
        IsAntialias = true
    };

    public float MeasureWidth() => 320f;
    public float MeasureHeight() => 130f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        _titlePaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        _bodyPaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);

        canvas.DrawText("Hello 详情页", rect.Left + 20f, rect.Top + 28f, _titlePaint);
        canvas.DrawText("这是插件详情页，由右键打开", rect.Left + 20f, rect.Top + 52f, _bodyPaint);
        canvas.DrawText("组件 ID: hello.text", rect.Left + 20f, rect.Top + 74f, _bodyPaint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnAction(string? action, float x, float y) { }
}
