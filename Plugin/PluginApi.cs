using SkiaSharp;

namespace NotchPeninsula.Plugins;

// ============================================================================
// Phase 0 冻结版插件 API 契约
// 本文件只定义接口与数据模型，不引用任何现有实现，
// 供社区评审与后续阶段逐步接线。
// 说明：暂放在主项目命名空间下；Phase 3 可拆成独立 Abstractions 程序集。
// ============================================================================

/// <summary>插件入口。主机反射实例化后调用 <see cref="Initialize"/>。</summary>
public interface INotchPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    void Initialize(IPluginHost host);
}

// ---- 命中模型 ----

/// <summary>
/// 命中小结果。Action 由 widget 自行定义（如 "toggle" / "next" / "detail"）；
/// 未命中返回 <see cref="None"/>。
/// </summary>
public readonly record struct WidgetHit(string? Action)
{
    public static readonly WidgetHit None = new(null);
    public bool IsHit => Action != null;
}

// ---- 主题 / 渲染上下文 ----

/// <summary>每帧当前主题，由主机在 ApplyThemeColors 后快照给插件。</summary>
public readonly record struct RenderTheme(
    SKColor TextColor,
    SKColor SubTextColor,
    SKColor BackgroundColor,
    float GlobalDpi,
    float NotchBottomRadius);

/// <summary>
/// 每帧渲染上下文，由主机在绘制前统一计算并快照给组件。
/// <see cref="Alpha"/> 为状态叠化 / 启动淡入的合成透明度（0-255），组件绘制时用它对主题色做 WithAlpha。
/// </summary>
public readonly record struct WidgetFrame(
    RenderTheme Theme,
    byte Alpha,
    float TextOffsetY,
    float[]? Bars,
    bool IsHovered);

// ---- 数据来源 ----
// 约定：数据由插件自行处理（自己抓教务接口 / 轮询进程状态 / WebSocket 推送等）。
// 主机不提供数据源服务，只提供「刷新调度 + 重绘」「提醒」「持久化」三类能力。
// 内置媒体 / 硬件 / 通知组件是「内部插件」，可直接访问同程序集单例，无需走公共 API。

// ---- 五种「页」 ----

/// <summary>主显示区小组件。</summary>
/// <remarks>
/// 线程模型：<see cref="MeasureWidth"/> / <see cref="Draw"/> 在主机渲染线程被每帧调用，
/// 而插件的 <see cref="IPluginHost.ScheduleRefresh"/> 回调在后台线程执行。
/// 因此 Draw / MeasureWidth 读取的插件内部状态必须线程安全（volatile / lock / Interlocked）。
/// </remarks>
public interface IWidget
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>null 表示无详情页（一个组件对应一个详情页）。</summary>
    IDetailPage? DetailPage { get; }

    /// <summary>期望宽度（逻辑像素）。组合模式下主机逐块累加。</summary>
    float MeasureWidth(float availableHeight);

    /// <summary>绘制。rect 由主机布局后给出，坐标均为逻辑像素；frame 含主题 / 动画 / 频谱等每帧上下文。</summary>
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);

    /// <summary>左键命中：x/y 为相对 rect 左上角的逻辑坐标。</summary>
    WidgetHit HitTest(float x, float y, SKRect rect);

    /// <summary>左键动作回调（HitTest 命中后触发）。</summary>
    void OnLeftClick(string? action, float x, float y);

    /// <summary>右键回调。主机默认行为：若 DetailPage 非空则展开详情。</summary>
    void OnRightClick();

    void OnActivate(IPluginHost host);
    void OnDeactivate();
}

/// <summary>详情页（右键 / 展开后显示）。</summary>
public interface IDetailPage
{
    float MeasureWidth();
    float MeasureHeight();
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
    WidgetHit HitTest(float x, float y, SKRect rect);
    void OnAction(string? action, float x, float y);
}

/// <summary>副显示区小组件（Phase 4，仅只读信息展示）。</summary>
public interface ISecondaryWidget
{
    string Id { get; }
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
}

/// <summary>设置页（声明式，主机负责绘制 / 命中 / 持久化）。</summary>
public interface ISettingsPage
{
    string Title { get; }
    IReadOnlyList<SettingControl> Controls { get; }
}

/// <summary>
/// 自定义设置页：插件自行绘制整个设置页内容并处理鼠标交互。
/// 一个设置页可同时实现 <see cref="ISettingsPage"/>（声明式控件）与本接口（自定义 UI），两者都会渲染。
/// </summary>
public interface ICustomSettingsPage
{
    /// <summary>内容高度（用于设置页布局）。</summary>
    float MeasureHeight();
    /// <summary>绘制自定义 UI。rect 为可用区域（逻辑坐标）。</summary>
    void Draw(SKCanvas canvas, SKRect rect, RenderTheme theme);
    /// <summary>鼠标按下，x/y 相对 rect 左上角。</summary>
    void OnMouseDown(float x, float y);
    void OnMouseMove(float x, float y);
    void OnMouseUp(float x, float y);
}

/// <summary>插件窗口（由 <see cref="IPluginHost.CreateWindow"/> 创建）。</summary>
public interface IPluginWindow
{
    /// <summary>设置绘制回调 (canvas, width, height)。每次重绘时调用。</summary>
    void SetDraw(Action<SKCanvas, int, int>? draw);
    /// <summary>设置鼠标回调 (x, y)。</summary>
    void SetMouse(Action<float, float>? down, Action<float, float>? move, Action<float, float>? up);
    /// <summary>请求重绘。</summary>
    void RequestRedraw();
    void Close();
}

public abstract record SettingControl(string Key, string Label);

public sealed record ToggleSetting(string Key, string Label, bool DefaultValue)
    : SettingControl(Key, Label);

public sealed record ChoiceSetting(string Key, string Label, string[] Options, int DefaultIndex)
    : SettingControl(Key, Label);

public sealed record NumberSetting(string Key, string Label, float Min, float Max, float Step, float Default)
    : SettingControl(Key, Label);

/// <summary>提醒数据（主机调度一次性展示，复用现有 4 秒 Toast 机制）。</summary>
public sealed class ReminderData
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? IconPath { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
    public Action? OnClick { get; init; }
}

// ---- 主机（插件反向拿服务与注册页） ----

public interface IPluginHost
{
    // 注册（插件在 Initialize 中可多次调用，一个插件可注册多个组件）
    void RegisterWidget(IWidget widget);
    void RegisterSecondaryWidget(ISecondaryWidget widget);
    void RegisterSettingsPage(ISettingsPage page);

    // 主题（当前帧快照）
    RenderTheme CurrentTheme { get; }

    // 提醒
    void PostReminder(ReminderData reminder);

    // 设置持久化（key 会加 "Plugin.<id>." 前缀做隔离）
    string GetSetting(string key, string fallback);
    void SetSetting(string key, string value);

    // 设置变更事件（主机写入设置后触发，插件订阅以即时响应）
    event Action? SettingsChanged;

    // 刷新调度（插件主动刷新的核心能力）
    /// <summary>
    /// 注册周期性刷新回调（如每 5 分钟拉课表、每 10 秒轮询状态）。
    /// 回调在后台线程执行，插件在回调内更新自己的数据；
    /// 主机渲染循环每帧读取最新数据，无需插件手动触发重绘。
    /// 返回 IDisposable，Dispose 即停止刷新。
    /// </summary>
    IDisposable ScheduleRefresh(TimeSpan interval, Action callback);

    // 交互调度
    /// <summary>异步取到数据后主动请求重绘（常驻 60FPS 渲染下等价于空操作，作为事件驱动化预留）。</summary>
    void RequestRedraw();
    void OpenDetailPage(string widgetId);
    void CloseDetailPage();

    // 窗口
    /// <summary>创建一个插件自有窗口（SkiaSharp 绘制 + 鼠标输入）。</summary>
    IPluginWindow CreateWindow(string title, int width, int height);
}
