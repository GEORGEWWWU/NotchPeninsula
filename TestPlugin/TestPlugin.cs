using System;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace TestPlugin;

/// <summary>
/// 简单测试插件：只在灵动岛上显示一个「TEST 圆点 + 运行秒数」组件。
///
/// 用途：验证插件加载 / 渲染 / 刷新 / 点击的整条接线是否可用。
/// 行为：
///   · 加载时弹一条灵动岛提醒（证明 Initialize 被调用）；
///   · 注册一个常驻组件，每 30 秒后台线程刷新运行秒数（证明 ScheduleRefresh）；
///   · 点击组件切换开关（圆点变灰 + 反馈提醒，证明命中与持久化）。
/// </summary>
public sealed class TestPlugin : INotchPlugin, IDisposable
{
    private IPluginHost? _host;
    private IDisposable? _timer;
    private TestWidget? _widget;
    private int _seconds; // 后台线程写(Interlocked)，渲染线程读(Volatile)

    public string Id => "com.test.notch-plugin";      // 反域名，保证唯一
    public string DisplayName => "TEST 测试插件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // 1) 立刻弹提醒，直观确认插件跑起来了。
        host.PostReminder(new ReminderData
        {
            Title = "TEST 插件已加载",
            Body = "灵动岛右上角出现 TEST 圆点，可点击切换",
        });

        // 2) 每 30 秒后台累加一次运行秒数。
        _timer = host.ScheduleRefresh(TimeSpan.FromSeconds(30), () => Interlocked.Add(ref _seconds, 30));

        // 3) 注册灵动岛组件。
        _widget = new TestWidget(this);
        host.RegisterWidget(_widget);
    }

    /// <summary>渲染线程安全读取后台累计的运行秒数。</summary>
    internal int Seconds => Volatile.Read(ref _seconds);

    /// <summary>点击回调：持久化开关状态 + 反馈提醒。</summary>
    internal void OnToggled(bool enabled)
    {
        if (_host is not IPluginHost h) return;
        h.SetSetting("enabled", enabled ? "1" : "0");
        h.PostReminder(new ReminderData
        {
            Title = "TEST 插件",
            Body = enabled ? "已开启（再点一下关闭）" : "已关闭（再点一下开启）",
        });
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _widget?.Dispose();
    }
}

/// <summary>灵动岛常驻组件：绿色圆点 + 运行秒数，点击切换开关。</summary>
internal sealed class TestWidget : IWidget, IDisposable
{
    private readonly TestPlugin _plugin;
    private volatile bool _on = true;

    // 重要设计取舍：这里「绝不缓存」任何 SKPaint / SKTypeface 原生对象。
    //  1) 插件运行在可回收的独立 AssemblyLoadContext，开关插件时会 Dispose 旧组件并卸载上下文；
    //  2) 渲染线程可能仍在一帧内引用旧组件（卸载与重绘存在竞态），复用已被释放的画笔会直接
    //     触发原生访问冲突 0xC0000005 崩溃（原实现即因此崩溃）；
    //  3) 画笔改为「绘制线程上临时创建、用后即弃」，既避免跨线程共享原生对象，
    //     也保证卸载瞬间绝无残留引用 —— 是测试插件「稳定性优先」的正确取舍。
    // 组件文字全为 ASCII/半角符号（"TEST · 12s"），直接用 SkiaSharp 默认字体即可，无需显式微软雅黑。

    private string _lastText = "";
    private float _lastTextWidth;
    private bool _lastOn;
    private int _lastSeconds = -1;

    public TestWidget(TestPlugin plugin) => _plugin = plugin;

    public string Id => "com.test.notch-plugin.widget";
    public string DisplayName => "TEST";
    public IDetailPage? DetailPage => null;

    /// <summary>期望宽度：圆点 + 间距 + 文字宽，随内容变化（岛会自动做宽度动画）。</summary>
    public float MeasureWidth(float availableHeight)
    {
        RefreshText();
        return 10f + 8f + _lastTextWidth + 4f;
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        RefreshText();
        float cy = rect.MidY + frame.TextOffsetY;

        // 圆点：开启=绿，关闭=灰；Alpha 跟随主机的淡入/叠化。画笔本帧临时创建、用完即释放。
        using var dot = new SKPaint
        {
            IsAntialias = true,
            Color = (_on ? new SKColor(76, 175, 80) : new SKColor(130, 130, 130)).WithAlpha(frame.Alpha),
        };
        canvas.DrawCircle(rect.Left + 6f, cy, 4f, dot);

        // 文字用主机当前主题色（自适应黑/白主题）。
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12.5f,
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText(_lastText, rect.Left + 18f, cy + 4.5f, textPaint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y)
    {
        _on = !_on; // volatile 写：渲染线程下一帧立刻可见。
        _plugin.OnToggled(_on);
    }

    public void OnRightClick() { }

    public void OnActivate(IPluginHost host)
        => _on = host.GetSetting("enabled", "1") != "0";

    public void OnDeactivate() { }

    /// <summary>仅当内容变化时才重建并重新测量文本（稳态渲染零分配）。</summary>
    private void RefreshText()
    {
        int sec = _plugin.Seconds;
        if (sec == _lastSeconds && _on == _lastOn && _lastText.Length > 0) return;

        _lastSeconds = sec;
        _lastOn = _on;
        _lastText = _on
            ? (sec <= 0 ? "TEST" : $"TEST · {sec}s")
            : $"OFF · {sec}s";
        using var measure = new SKPaint { IsAntialias = true, TextSize = 12.5f };
        _lastTextWidth = measure.MeasureText(_lastText);
    }

    public void Dispose() { } // 已无缓存原生对象需要释放
}
