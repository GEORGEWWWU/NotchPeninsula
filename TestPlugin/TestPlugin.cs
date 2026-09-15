using System;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace TestPlugin;

/// <summary>
/// 交互测试插件：把当前「已经开放」的插件能力全部用上一遍，作为接线是否真的通了的活体探针。
///
/// 覆盖清单：
///   1. 入口类 INotchPlugin（Id / DisplayName / Version / Initialize）
///   2. 主显示组件 IWidget（MeasureWidth / Draw / HitTest / OnLeftClick / OnRightClick / OnActivate / OnDeactivate）
///   3. 左键 = 开关插件（写设置持久化 + 弹提醒）
///   4. 右键 = 主机默认行为：在灵动岛展开本插件的详情页
///   5. 详情页 IDetailPage（MeasureWidth / MeasureHeight / Draw / HitTest / OnAction），尺寸由插件自己决定
///   6. 定时刷新 ScheduleRefresh（后台线程 + Interlocked 安全读写）
///   7. 提醒 PostReminder
///   8. 设置持久化 GetSetting / SetSetting，以及 SettingsChanged 订阅
///   9. 自定义窗口 CreateWindow / IPluginWindow（SetDraw / SetMouse / SetKey / RequestRedraw / Close）
///  10. 主题快照 RenderTheme + 渲染帧 WidgetFrame（Alpha / TextOffsetY / IsHovered）
///  11. 宿主交互调度 OpenDetailPage / CloseDetailPage
///
/// 线程模型：ScheduleRefresh 回调在后台线程跑，Draw / MeasureWidth 在渲染线程跑，
/// 所以共享的计数一律用 Interlocked / volatile，绝不让两边直接读写同一个普通字段。
/// </summary>
public sealed class TestPlugin : INotchPlugin, IDisposable
{
    private IPluginHost? _host;
    private IDisposable? _refresh;
    private TestWidget? _widget;
    private TestDetailPage? _detail;

    private int _ticks;      // 后台刷新次数
    private int _switches;   // 开关切换次数

    // 自定义窗口相关状态
    private IPluginWindow? _window;
    private int _windowClicks;
    private readonly object _typedLock = new();
    private string _typed = "";

    public string Id => "com.nps.testplugin";
    public string DisplayName => "测试插件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // ① 提醒：证明插件确实被宿主加载起来了
        host.PostReminder(new ReminderData
        {
            Title = "测试插件已加载",
            Body = "左键点它 = 开关插件，右键点它 = 展开详情页",
            Duration = TimeSpan.FromSeconds(5),
        });

        // ② 定时刷新：每 2 秒在后台线程 +1（渲染线程每帧自动读到新值，无需手动重绘）
        _refresh = host.ScheduleRefresh(TimeSpan.FromSeconds(2), () => Interlocked.Increment(ref _ticks));

        // ③ 设置变更订阅（设置页尚未开放，机制先接上，开放后即可即时响应）
        host.SettingsChanged += OnSettingsChanged;

        // ④ 注册主显示组件（不注册就不会出现在灵动岛上）
        _widget = new TestWidget(this);
        host.RegisterWidget(_widget);

        // ⑤ 读回上次的开关状态，做到「重启还记得」
        _widget.IsOn = host.GetSetting("enabled", "1") != "0";
    }

    private void OnSettingsChanged()
    {
        // 设置被写入（例如设置页里改了配置）时会走到这里
    }

    internal IPluginHost? Host => _host;
    internal int Ticks => Volatile.Read(ref _ticks);
    internal int Switches => Volatile.Read(ref _switches);
    internal bool IsOn => _widget?.IsOn ?? false;

    /// <summary>详情页实例只建一次并缓存，避免右键时每帧 new 一个新对象。</summary>
    internal IDetailPage Detail => _detail ??= new TestDetailPage(this);

    /// <summary>左键 / 详情页按钮：切换插件开关（持久化 + 提醒）。</summary>
    internal void Toggle()
    {
        bool next = !IsOn;
        if (_widget != null) _widget.IsOn = next;
        Interlocked.Increment(ref _switches);

        _host?.SetSetting("enabled", next ? "1" : "0"); // 写入注册表，下次启动还记得
        _host?.PostReminder(new ReminderData
        {
            Title = "测试插件",
            Body = next ? "已开启（再点一次关闭）" : "已关闭（再点一次开启）",
        });
    }

    /// <summary>详情页按钮：弹一条提醒（验证 PostReminder 通道）。</summary>
    internal void Remind()
        => _host?.PostReminder(new ReminderData
        {
            Title = "来自详情页",
            Body = $"后台刷新 {Ticks} 次 · 开关 {Switches} 次",
        });

    /// <summary>详情页按钮：插件主动收起自己的详情页（验证 host.CloseDetailPage）。</summary>
    internal void CloseDetail() => _host?.CloseDetailPage();

    /// <summary>详情页按钮：插件主动展开自己的详情页（验证 host.OpenDetailPage）。</summary>
    internal void OpenDetail() => _host?.OpenDetailPage("com.nps.testplugin.widget");

    /// <summary>详情页按钮：打开 / 重建插件自有窗口（验证 CreateWindow 通道）。</summary>
    internal void OpenWindow()
    {
        // 旧窗口先关掉再建新的：这里跑在 UI 线程上，Close 只是投递消息，
        // 所以旧窗口句柄在新建时仍存活，不会出现句柄被复用而误关新窗口的情况。
        try { _window?.Close(); } catch { }

        var w = _host?.CreateWindow("测试插件 · 独立窗口", 380, 192);
        if (w == null) return;

        w.SetDraw(DrawPluginWindow);
        w.SetMouse(
            down: (x, y) => { Interlocked.Increment(ref _windowClicks); w.RequestRedraw(); },
            move: null,
            up: null);
        w.SetKey(ch =>
        {
            lock (_typedLock)
            {
                if (ch == '\b') { if (_typed.Length > 0) _typed = _typed[..^1]; }
                else if (ch >= ' ') _typed += ch;
                if (_typed.Length > 24) _typed = _typed[^24..];
            }
            w.RequestRedraw();
        });

        _window = w;
    }

    /// <summary>插件自有窗口的绘制回调（宿主已画好圆角背景与边框，这里只画内容）。</summary>
    private void DrawPluginWindow(SKCanvas canvas, int width, int height)
    {
        // 画笔 / 字体都是原生对象：随用随建、用完即释放，绝不缓存成字段跨线程复用（详见文档「踩坑」）
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        using var title = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 15f, Typeface = typeface };
        using var body = new SKPaint { IsAntialias = true, Color = new SKColor(200, 200, 200), TextSize = 12.5f, Typeface = typeface };

        string typed;
        lock (_typedLock) typed = _typed;

        canvas.DrawText("测试插件 · 独立窗口", 20f, 34f, title);
        canvas.DrawText($"点击窗口任意位置计数：{Volatile.Read(ref _windowClicks)}", 20f, 68f, body);
        canvas.DrawText($"键盘输入：{typed}", 20f, 96f, body);
        canvas.DrawText("按 Esc 或点右上角圆点可关闭", 20f, 124f, body);
    }

    public void Dispose()
    {
        _refresh?.Dispose();
        if (_host != null) _host.SettingsChanged -= OnSettingsChanged;
        try { _window?.Close(); } catch { }
        _window = null;
        _widget = null;
        _detail = null;
    }
}

/// <summary>
/// 灵动岛上的主显示组件：一个状态圆点 + 一段文字。
/// 左键切换开关；右键不做任何事 —— 主机会检查 DetailPage 并展开详情页。
/// </summary>
internal sealed class TestWidget : IWidget
{
    private readonly TestPlugin _plugin;
    private volatile bool _isOn = true;

    // 只在文字变化时重新测量宽度，避免无意义地每帧算一遍
    private string _text = "";
    private float _textWidth;

    public TestWidget(TestPlugin plugin) => _plugin = plugin;

    public string Id => "com.nps.testplugin.widget";
    public string DisplayName => "测试插件";

    /// <summary>非 null 时，右键本组件由主机默认展开这个详情页。</summary>
    public IDetailPage? DetailPage => _plugin.Detail;

    internal bool IsOn { get => _isOn; set => _isOn = value; }

    /// <summary>告诉宿主自己要多宽（逻辑像素）：左边距 + 圆点 + 间距 + 文字 + 右边距。</summary>
    public float MeasureWidth(float availableHeight)
    {
        RefreshText();
        return 16f + _textWidth + 6f;
    }

    /// <summary>每帧被调用：把圆点和文字画到宿主分给我们的区域里。</summary>
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        RefreshText();
        float cy = rect.MidY + frame.TextOffsetY; // 用宿主给的偏移量做垂直居中

        // 圆点：开启=绿，关闭=灰；透明度跟随宿主的淡入 / 叠化动画
        using var dot = new SKPaint
        {
            IsAntialias = true,
            Color = (_isOn ? new SKColor(76, 175, 80) : new SKColor(130, 130, 130)).WithAlpha(frame.Alpha),
        };
        canvas.DrawCircle(rect.Left + 6f, cy, 4f, dot);

        // 文字用当前主题色，黑 / 白主题下都能看清
        using var text = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12.5f,
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText(_text, rect.Left + 16f, cy + 4.5f, text);
    }

    /// <summary>命中检测：x / y 是相对 rect 左上角的逻辑坐标。</summary>
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0f && x <= rect.Width && y >= 0f && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    /// <summary>左键：开关插件。</summary>
    public void OnLeftClick(string? action, float x, float y) => _plugin.Toggle();

    /// <summary>右键：插件自己不用做事，主机检测到 DetailPage 非空就会展开详情页。</summary>
    public void OnRightClick() { }

    /// <summary>组件被启用时调用：把上次保存的开关状态读回来。</summary>
    public void OnActivate(IPluginHost host) => _isOn = host.GetSetting("enabled", "1") != "0";

    /// <summary>组件被停用时调用。</summary>
    public void OnDeactivate() { }

    /// <summary>只在内容真的变化时重建文字并重新测量宽度。</summary>
    private void RefreshText()
    {
        string next = _isOn ? $"TEST · {_plugin.Ticks}" : $"OFF · {_plugin.Ticks}";
        if (next == _text && _textWidth > 0f) return;

        _text = next;
        using var measure = new SKPaint { IsAntialias = true, TextSize = 12.5f };
        _textWidth = measure.MeasureText(_text);
    }
}

/// <summary>
/// 详情页：右键组件后，灵动岛会整块展开成这个尺寸，内容由插件自己画、自己处理点击。
/// 尺寸完全由插件决定（宿主只做 180~1000 × 48~480 的裁剪保护，防止把岛体撑到屏幕外）。
/// </summary>
internal sealed class TestDetailPage : IDetailPage
{
    private readonly TestPlugin _plugin;

    private const float W = 360f;
    private const float H = 192f;

    // 四个按钮的布局（相对详情页左上角的逻辑坐标）
    private static readonly SKRect BtnToggle = new(16f, 104f, 176f, 134f);
    private static readonly SKRect BtnRemind = new(184f, 104f, 344f, 134f);
    private static readonly SKRect BtnWindow = new(16f, 140f, 176f, 170f);
    private static readonly SKRect BtnClose = new(184f, 140f, 344f, 170f);

    public TestDetailPage(TestPlugin plugin) => _plugin = plugin;

    /// <summary>详情页想要的宽度（宿主会裁剪到 180 ~ 1000）。</summary>
    public float MeasureWidth() => W;

    /// <summary>详情页想要的高度（宿主会裁剪到 48 ~ 480）。</summary>
    public float MeasureHeight() => H;

    /// <summary>绘制详情页内容。rect 就是整个岛体区域（已扣除顶部悬浮位移）。</summary>
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        // 原生对象随用随建随释放；这里含中文，显式指定中文字体
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");

        byte a = frame.Alpha;
        using var title = new SKPaint { IsAntialias = true, TextSize = 14.5f, Typeface = typeface, Color = frame.Theme.TextColor.WithAlpha(a) };
        using var body = new SKPaint { IsAntialias = true, TextSize = 12f, Typeface = typeface, Color = frame.Theme.SubTextColor.WithAlpha(a) };
        using var btnText = new SKPaint { IsAntialias = true, TextSize = 12.5f, Typeface = typeface, Color = frame.Theme.TextColor.WithAlpha(a) };
        using var btnBg = new SKPaint { IsAntialias = true, Color = frame.Theme.TextColor.WithAlpha((byte)(a * 0.14f)) };
        using var dot = new SKPaint
        {
            IsAntialias = true,
            Color = (_plugin.IsOn ? new SKColor(76, 175, 80) : new SKColor(130, 130, 130)).WithAlpha(a),
        };

        float ox = rect.Left, oy = rect.Top;

        // 状态点 + 标题
        canvas.DrawCircle(ox + 22f, oy + 28f, 5f, dot);
        canvas.DrawText("测试插件 · 详情页", ox + 36f, oy + 33f, title);

        // 信息行：这些数据来自组件状态与后台刷新，能直观看到两边的联动
        canvas.DrawText($"状态：{(_plugin.IsOn ? "已开启" : "已关闭")}      后台刷新 {_plugin.Ticks} 次      开关 {_plugin.Switches} 次",
            ox + 20f, oy + 62f, body);
        canvas.DrawText($"详情页尺寸由插件决定：{W} × {H}（宿主已按此尺寸展开灵动岛）",
            ox + 20f, oy + 84f, body);

        // 四个按钮
        DrawButton(canvas, ox, oy, BtnToggle, "切换开关", btnBg, btnText);
        DrawButton(canvas, ox, oy, BtnRemind, "弹一条提醒", btnBg, btnText);
        DrawButton(canvas, ox, oy, BtnWindow, "打开插件窗口", btnBg, btnText);
        DrawButton(canvas, ox, oy, BtnClose, "收起详情页", btnBg, btnText);
    }

    private static void DrawButton(SKCanvas canvas, float ox, float oy, SKRect r, string label, SKPaint bg, SKPaint text)
    {
        var box = new SKRect(ox + r.Left, oy + r.Top, ox + r.Right, oy + r.Bottom);
        canvas.DrawRoundRect(box, 8f, 8f, bg);
        float w = text.MeasureText(label);
        canvas.DrawText(label, box.MidX - w / 2f, box.MidY + 4.5f, text);
    }

    /// <summary>命中检测：x / y 相对详情页左上角；返回动作名，未命中返回 None。</summary>
    public WidgetHit HitTest(float x, float y, SKRect rect)
    {
        if (BtnToggle.Contains(x, y)) return new WidgetHit("toggle");
        if (BtnRemind.Contains(x, y)) return new WidgetHit("remind");
        if (BtnWindow.Contains(x, y)) return new WidgetHit("window");
        if (BtnClose.Contains(x, y)) return new WidgetHit("close");
        return WidgetHit.None;
    }

    /// <summary>点击动作回调：action 就是 HitTest 返回的那个名字。</summary>
    public void OnAction(string? action, float x, float y)
    {
        switch (action)
        {
            case "toggle": _plugin.Toggle(); break;
            case "remind": _plugin.Remind(); break;
            case "window": _plugin.OpenWindow(); break;
            case "close": _plugin.CloseDetail(); break;
        }
    }
}
