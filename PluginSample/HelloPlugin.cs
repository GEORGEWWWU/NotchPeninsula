using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace PluginSample;

/// <summary>
/// 最小可用的测试插件。
///
/// 用法：编译后把 HelloPlugin.dll 放到主程序的 plugins 目录（或在「插件中心」点「导入 DLL」），
/// 即可看到灵动岛弹出提醒，证明 DLL 已被动态加载并运行在主程序进程里。
///
/// 宿主会在卸载 / 热重载时自动回收它交给插件的资源（刷新句柄、组件、设置页），
/// 所以这里不需要额外清理。若插件自己创建了线程 / 文件句柄 / 网络连接，
/// 可以让插件实现 <see cref="IDisposable"/>，宿主在卸载时会调用它。
/// </summary>
public sealed class HelloPlugin : INotchPlugin
{
    private IPluginHost? _host;
    private IDisposable? _timer;   // ScheduleRefresh 返回的句柄，Dispose 即停止刷新
    private int _tick;

    // ---- INotchPlugin 的三个身份字段 ----
    // Id 建议用反域名，保证全局唯一；"启用/禁用"就是按 Id 与文件路径持久化的。
    public string Id => "com.example.hello-plugin";
    public string DisplayName => "Hello 插件示例";
    public string Version => "1.0.0";

    /// <summary>主机反射实例化本类后立刻调用，这里是插件的"入口"。</summary>
    public void Initialize(IPluginHost host)
    {
        _host = host;

        // 1) 立刻弹一条灵动岛提醒，最直观地证明插件已经跑起来了。
        host.PostReminder(new ReminderData
        {
            Title = "Hello 插件已加载",
            Body = $"我来自 HelloPlugin.dll，当前主题文字色 {host.CurrentTheme.TextColor}",
        });

        // 2) 每 60 秒刷新一次，演示 ScheduleRefresh（回调在后台线程执行）。
        //    不需要周期提醒时，把下面这行删掉即可。
        _timer = host.ScheduleRefresh(TimeSpan.FromSeconds(60), OnTick);

        // 3) 注册设置页与小组件（接口示例，等主机把渲染阶段接线完成后即可显示在灵动岛上）。
        host.RegisterSettingsPage(new HelloSettingsPage());
        host.RegisterWidget(new HelloWidget());
    }

    /// <summary>后台线程回调：在这里更新插件自己的数据，再用 PostReminder 通知 UI。</summary>
    private void OnTick()
    {
        _tick++;
        _host?.PostReminder(new ReminderData
        {
            Title = "Hello 插件运行中",
            Body = $"已运行 {_tick} 分钟，一切正常。",
            Duration = TimeSpan.FromSeconds(4)
        });
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

/// <summary>
/// 主显示区小组件示例。主机每帧调用 MeasureWidth / Draw，
/// 注意 Draw 在渲染线程、ScheduleRefresh 回调在后台线程，共享状态需线程安全。
/// </summary>
internal sealed class HelloWidget : IWidget
{
    public string Id => "com.example.hello-plugin.widget";
    public string DisplayName => "Hello";
    public IDetailPage? DetailPage => null;

    /// <summary>期望占用的宽度（逻辑像素）。</summary>
    public float MeasureWidth(float availableHeight) => 64f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 13f,
            // frame.Alpha 是主机的叠化透明度，配合主题色使用
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText("Hello", rect.Left + 6f, rect.MidY + 5f, paint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y) { /* 处理点击 */ }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}
