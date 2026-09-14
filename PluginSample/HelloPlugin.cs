using System;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace PluginSample;

/// <summary>
/// 最小可用的测试插件（带灵动岛渲染演示）。
///
/// 用法：编译后把 HelloPlugin.dll 放到主程序的 plugins 目录（或在「插件中心」点「导入 DLL」），
/// 灵动岛上就会出现两个由本插件绘制的组件：
///   · 状态组件：绿色圆点 + 运行分钟数文字，宽度随内容变化（灵动岛会自动做宽度弹簧动画）；
///   · 律动组件：纯正弦波逐帧动画，演示插件可以画"活的"内容。
/// 两个组件都可以点击：状态组件切换开关并持久化，律动组件暂停/继续动画。
///
/// 零 GC 原则：Draw / MeasureWidth 每帧被主机渲染线程调用，
/// 因此所有画笔、字体在构造时创建一次并复用（Dispose 时释放），
/// 文字内容也只在变化时才重新测量，稳态渲染零分配。
///
/// 线程模型：ScheduleRefresh 回调在后台线程（用 Interlocked 写计数器），
/// 点击回调在 UI 线程（用 volatile 写开关状态），Draw 在渲染线程只读。
/// 宿主在卸载 / 热重载时会自动回收它交给插件的资源（刷新句柄、组件、设置页），
/// 插件自己创建的非托管资源（画笔/字体）通过 IDisposable 在卸载时释放。
/// </summary>
public sealed class HelloPlugin : INotchPlugin, IDisposable
{
    private IPluginHost? _host;
    private IDisposable? _timer;
    private int _minutes;                       // 后台线程写(Interlocked)，渲染线程读
    private HelloStatusWidget? _statusWidget;
    private HelloPulseWidget? _pulseWidget;

    // ---- INotchPlugin 的三个身份字段 ----
    // Id 建议用反域名，保证全局唯一；"启用/禁用"就是按 Id 与文件路径持久化的。
    public string Id => "com.example.hello-plugin";
    public string DisplayName => "Hello 插件示例";
    public string Version => "1.1.0";

    /// <summary>主机反射实例化本类后立刻调用，这里是插件的"入口"。</summary>
    public void Initialize(IPluginHost host)
    {
        _host = host;

        // 1) 立刻弹一条灵动岛提醒，最直观地证明插件已经跑起来了。
        host.PostReminder(new ReminderData
        {
            Title = "Hello 插件已加载",
            Body = "灵动岛上出现了两个插件组件：状态圆点与律动波形，都可以点击交互",
        });

        // 2) 每 60 秒在后台线程累加一次计数（渲染线程每帧读取最新值，无需手动触发重绘）。
        _timer = host.ScheduleRefresh(TimeSpan.FromSeconds(60), () => Interlocked.Increment(ref _minutes));

        // 3) 注册灵动岛组件（渲染接线已完成，真的会显示在岛上）与声明式设置页。
        _statusWidget = new HelloStatusWidget(this);
        _pulseWidget = new HelloPulseWidget();
        host.RegisterSettingsPage(new HelloSettingsPage());
        host.RegisterWidget(_statusWidget);
        host.RegisterWidget(_pulseWidget);
    }

    /// <summary>渲染线程安全地读取后台计数。</summary>
    internal int Minutes => Volatile.Read(ref _minutes);

    /// <summary>状态组件点击回调：持久化开关状态 + 弹灵动岛提醒反馈。</summary>
    internal void OnStatusToggled(bool enabled)
    {
        var host = _host;
        if (host == null) return;
        // key 会自动加上 "Plugin.<id>." 前缀，与其他插件互不干扰
        host.SetSetting("enabled", enabled ? "1" : "0");
        host.PostReminder(new ReminderData
        {
            Title = "Hello 插件",
            Body = enabled ? "状态组件已重新开启" : "状态组件已关闭（再点一下圆点可恢复）",
        });
    }

    /// <summary>卸载/热重载时释放插件自己创建的非托管资源（画笔、字体、刷新句柄）。</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _statusWidget?.Dispose();
        _pulseWidget?.Dispose();
    }
}

/// <summary>
/// 状态组件：彩色圆点 + 运行分钟数文字。
/// 点击切换开关（圆点变灰），并演示"宽度由插件决定"——文字变长时灵动岛会跟着变宽。
/// </summary>
internal sealed class HelloStatusWidget : IWidget, IDisposable
{
    private readonly HelloPlugin _plugin;
    private volatile bool _on = true;

    // 画笔/字体只创建一次：Draw 每帧被调用，绝不能在 Draw 里 new 任何对象
    private readonly SKTypeface _typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
    private readonly SKPaint _dotPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, TextSize = 12.5f };

    // 文本缓存：内容不变就复用上次测量结果，避免每帧字符串分配与 MeasureText
    private string _lastText = "";
    private float _lastTextWidth;
    private bool _lastOn;
    private int _lastMinutes = -1;

    public HelloStatusWidget(HelloPlugin plugin)
    {
        _plugin = plugin;
        _textPaint.Typeface = _typeface;
    }

    public string Id => "com.example.hello-plugin.widget";
    public string DisplayName => "Hello";
    public IDetailPage? DetailPage => null;

    /// <summary>期望宽度（逻辑像素）：圆点 + 间距 + 文字宽度，随内容变化。</summary>
    public float MeasureWidth(float availableHeight)
    {
        RefreshText();
        return 10f /* 左边距+圆点 */ + 8f /* 间距 */ + _lastTextWidth + 4f /* 右边距 */;
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        RefreshText();
        float cy = rect.MidY + frame.TextOffsetY;

        // 状态圆点：开启=绿色，关闭=灰色；Alpha 跟随主机的叠化/淡入
        _dotPaint.Color = (_on ? new SKColor(76, 175, 80) : new SKColor(130, 130, 130)).WithAlpha(frame.Alpha);
        canvas.DrawCircle(rect.Left + 6f, cy, 4f, _dotPaint);

        // 文字使用主机当前主题的文字色，自动适配黑/白主题
        _textPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(_lastText, rect.Left + 18f, cy + 4.5f, _textPaint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y)
    {
        _on = !_on; // volatile 写：渲染线程下一帧立刻可见
        _plugin.OnStatusToggled(_on);
    }

    public void OnRightClick() { }

    /// <summary>恢复激活时从持久化设置读取开关状态（key 自动带插件前缀）。</summary>
    public void OnActivate(IPluginHost host)
    {
        _on = host.GetSetting("enabled", "1") != "0";
    }

    public void OnDeactivate() { }

    /// <summary>仅当分钟数或开关状态变化时才重建文字并重新测量。</summary>
    private void RefreshText()
    {
        int minutes = _plugin.Minutes;
        if (minutes == _lastMinutes && _on == _lastOn && _lastText.Length > 0) return;

        _lastMinutes = minutes;
        _lastOn = _on;
        _lastText = _on
            ? (minutes <= 0 ? "Hi · Ready" : $"Hi · {minutes} min")
            : $"Off · {minutes} min";
        _lastTextWidth = _textPaint.MeasureText(_lastText);
    }

    public void Dispose()
    {
        _dotPaint.Dispose();
        _textPaint.Dispose();
        _typeface.Dispose();
    }
}

/// <summary>
/// 律动组件：用纯数学正弦波做逐帧动画（零分配），演示插件可以在灵动岛上渲染"活的"内容。
/// 点击暂停/继续：暂停时显示暂停符号，提示可再次点击恢复。
/// </summary>
internal sealed class HelloPulseWidget : IWidget, IDisposable
{
    private volatile bool _running = true;
    private readonly SKPaint _barPaint = new() { IsAntialias = true };
    private readonly SKPaint _pausePaint = new() { IsAntialias = true };

    public string Id => "com.example.hello-plugin.pulse";
    public string DisplayName => "律动";
    public IDetailPage? DetailPage => null;

    public float MeasureWidth(float availableHeight) => 40f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        float midY = rect.MidY + frame.TextOffsetY;

        if (_running)
        {
            _barPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
            // Environment.TickCount64 是零分配的时钟读取，正弦波纯计算无任何对象创建
            double t = Environment.TickCount64 / 1000.0;
            for (int i = 0; i < 5; i++)
            {
                float h = 4f + (float)Math.Abs(Math.Sin(t * 2.2 + i * 0.9)) * 14f;
                float x = rect.Left + 4f + i * 7f;
                canvas.DrawRoundRect(x, midY - h / 2f, 3f, h, 1.5f, 1.5f, _barPaint);
            }
        }
        else
        {
            // 暂停时画两根小竖条（暂停符号），提示点击可恢复
            _pausePaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);
            canvas.DrawRoundRect(rect.Left + 16f, midY - 5f, 2.5f, 10f, 1.2f, 1.2f, _pausePaint);
            canvas.DrawRoundRect(rect.Left + 21.5f, midY - 5f, 2.5f, 10f, 1.2f, 1.2f, _pausePaint);
        }
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y) => _running = !_running;

    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }

    public void Dispose()
    {
        _barPaint.Dispose();
        _pausePaint.Dispose();
    }
}

/// <summary>
/// 声明式设置页：插件只描述"有哪些开关"，具体绘制与持久化由主机负责，
/// key 会自动加上 "Plugin.&lt;id&gt;." 前缀做隔离。
/// </summary>
internal sealed class HelloSettingsPage : ISettingsPage
{
    public string Title => "Hello 插件";

    public IReadOnlyList<SettingControl> Controls { get; } = new SettingControl[]
    {
        new ToggleSetting("enabled", "启用提醒", true),
        new ChoiceSetting("style", "提醒风格", new[] { "简洁", "详细" }, 0),
        new NumberSetting("interval", "刷新间隔(秒)", 10, 3600, 10, 60),
    };
}
