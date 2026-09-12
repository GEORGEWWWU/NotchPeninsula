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
        host.RegisterWidget(new HelloWidget(host));
        host.RegisterSettingsPage(new HelloSettingsPage(host));

        // 5 秒后发一条提醒，演示 PostReminder（提醒页）
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            host.PostReminder(new ReminderData { Title = "Hello 插件", Body = "插件已成功加载并运行" });
        });
    }
}

/// <summary>Hello 插件的设置页（声明式控件 + 自定义 UI）。</summary>
public sealed class HelloSettingsPage : ISettingsPage, ICustomSettingsPage
{
    private readonly IPluginHost _host;
    private bool _btnHover;

    public HelloSettingsPage(IPluginHost host) { _host = host; }

    public string Title => "Hello 演示插件";
    public IReadOnlyList<SettingControl> Controls { get; } = new SettingControl[]
    {
        new ToggleSetting("ShowCheck", "启用", true),
        new ChoiceSetting("Greeting", "问候语", new[] { "你好", "Hello", "こんにちは" }, 0),
        new NumberSetting("Interval", "刷新间隔(秒)", 1f, 60f, 1f, 2f),
    };

    // ---- 自定义 UI：一个「打开窗口」按钮 ----
    public float MeasureHeight() => 70f;

    public void Draw(SKCanvas canvas, SKRect rect, RenderTheme theme)
    {
        var btn = new SKRect(rect.Left, rect.Top, rect.Left + 110, rect.Top + 32);
        var paint = new SKPaint { Color = _btnHover ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212), IsAntialias = true };
        canvas.DrawRoundRect(btn, 6, 6, paint);
        var text = new SKPaint { Color = SKColors.White, TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        canvas.DrawText("打开窗口", btn.Left + 20, btn.Top + 21, text);
    }

    public void OnMouseDown(float x, float y)
    {
        if (x >= 0 && x <= 110 && y >= 0 && y <= 32)
        {
            var win = _host.CreateWindow("插件窗口", 320, 180);
            var p = new SKPaint { Color = SKColors.White, TextSize = 15f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
            win.SetDraw((canvas, w, h) =>
            {
                canvas.Clear(new SKColor(30, 30, 30));
                canvas.DrawText("这是插件创建的窗口", 24, 44, p);
                canvas.DrawText("支持 SkiaSharp 绘制 + 鼠标输入", 24, 72, p);
            });
        }
    }
    public void OnMouseMove(float x, float y) { _btnHover = x >= 0 && x <= 110 && y >= 0 && y <= 32; }
    public void OnMouseUp(float x, float y) { }
}

/// <summary>一个自包含文本组件：验证插件 DLL 被加载并渲染。</summary>
public sealed class HelloWidget : IWidget
{
    private readonly IPluginHost _host;
    private volatile bool _showCheck;

    public HelloWidget(IPluginHost host)
    {
        _host = host;
        _showCheck = host.GetSetting("ShowCheck", "1") == "1";
        // 订阅设置变更事件，改设置立即生效
        host.SettingsChanged += () =>
        {
            _showCheck = host.GetSetting("ShowCheck", "1") == "1";
        };
    }

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
        canvas.DrawText(_showCheck ? "插件已运行" : "插件已停止", rect.Left + 16f, rect.MidY + 5f, _paint);
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
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
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
