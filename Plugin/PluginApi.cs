using SkiaSharp;

namespace NotchPeninsula.Plugins;

public interface INotchPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }

    string Author => "";

    void Initialize(IPluginHost host);
}

// ---- 命中模型 ----

/// <summary>命中小结果。Action 由 widget 自定义（如 "toggle" / "next" / "detail"）；未命中返回 None。</summary>
public readonly record struct WidgetHit(string? Action)
{
    public static readonly WidgetHit None = new(null);
    public bool IsHit => Action != null;
}

// ---- 主题 / 渲染上下文 ----

public readonly record struct RenderTheme(
    SKColor TextColor,
    SKColor SubTextColor,
    SKColor BackgroundColor,
    float GlobalDpi,
    float NotchBottomRadius);

public readonly record struct WidgetFrame(
    RenderTheme Theme,
    byte Alpha,
    float TextOffsetY,
    float[]? Bars,
    bool IsHovered);

// ---- 数据来源 ----

// ---- 五种「页」 ----

public interface IWidget
{
    string Id { get; }
    string DisplayName { get; }
    IDetailPage? DetailPage { get; }

    bool AcceptsFileDropWhenCollapsed => false;

    /// <summary>
    /// <b>老协议</b>：插件自己上报折叠态宽度（逻辑像素，完整显示内容所需）。
    ///
    /// <para>
    /// 实现了 <see cref="IAutoWidthWidget"/> 的插件不必实现本成员 —— 宿主走 AutoWidth 路径，
    /// 根本不会调到这里；给个默认实现只是免得新插件写一个永远返回 0 的空方法。
    /// </para>
    ///
    /// <para>
    /// <b>兼容性：</b>已编译的旧插件自带本成员实现，宿主调用时优先用它的，行为与加默认实现之前完全一致。
    /// 宿主也不会对旧插件的返回值做任何加工（不取整、不加留白、不量化、不压缩）。
    /// </para>
    /// </summary>
    float MeasureWidth(float availableHeight) => 0f;

    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);

    WidgetHit HitTest(float x, float y, SKRect rect);

    void OnLeftClick(string? action, float x, float y);

    void OnRightClick();

    bool AcceptsRightClick => false;

    bool AcceptsDoubleClick => false;

    void OnLeftDoubleClick(float x, float y) { }

    void OnRightDoubleClick(float x, float y) { }

    void OnActivate(IPluginHost host);
    void OnDeactivate();
}

public interface IDetailPage
{
    float MeasureWidth();
    float MeasureHeight();
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
    WidgetHit HitTest(float x, float y, SKRect rect);
    void OnAction(string? action, float x, float y);

    // ---- 鼠标事件 ----

    void OnMouseDown(float x, float y) { }

    void OnMouseMove(float x, float y) { }

    void OnMouseUp(float x, float y) { }

    void OnMouseLeave() { }

    // ---- 双击 / 右键透传 ----

    bool AcceptsRightClick => false;

    void OnRightClick(float x, float y) { }

    bool AcceptsDoubleClick => false;

    void OnLeftDoubleClick(float x, float y) { }

    void OnRightDoubleClick(float x, float y) { }

    bool OnFilesDragEnter(int count) => false;

    void OnFilesDragOver(float x, float y) { }

    void OnFilesDragLeave() { }

    void OnFilesDrop(string[] paths) { }

    TimeSpan AutoCollapseDelay => TimeSpan.Zero;
}

public interface ISecondaryWidget
{
    string Id { get; }
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
}

public interface ISettingsPage
{
    string Title { get; }
    IReadOnlyList<SettingControl> Controls { get; }
}

public interface ICustomSettingsPage
{
    float MeasureHeight();
    void Draw(SKCanvas canvas, SKRect rect, RenderTheme theme);
    void OnMouseDown(float x, float y);
    void OnMouseMove(float x, float y);
    void OnMouseUp(float x, float y);
}

public interface IPluginWindow
{
    void SetDraw(Action<SKCanvas, int, int>? draw);
    void SetMouse(Action<float, float>? down, Action<float, float>? move, Action<float, float>? up);
    void SetKey(Action<char>? key);

    void SetFilesDrop(Action<string[]>? onFiles);

    void SetDragHover(Action<int>? onEnter, Action<float, float>? onOver, Action? onLeave);

    bool StartDragFiles(IReadOnlyList<string> paths, bool allowMove = false);

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

public sealed class ReminderData
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>来源标签（可选）：通知左上角那一栏。不给就是「插件提醒」（插件既有行为不变）。</summary>
    public string? Source { get; init; }
    public string? IconPath { get; init; }
    /// <summary>可选头像：写法与 <see cref="IconPath"/> 完全相同。**优先级高于 IconPath** —— 给了头像就画头像，
    /// IconPath 只在没有头像（解析失败也算没有）时才出场；两者都没有才回退到宿主兜底图标。</summary>
    public string? AvatarPath { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
    public Action? OnClick { get; init; }
}

// ---- 主机（插件反向拿服务与注册页） ----

public interface IPluginHost
{
    void RegisterWidget(IWidget widget);
    void RegisterSecondaryWidget(ISecondaryWidget widget);
    void RegisterSettingsPage(ISettingsPage page);

    // 主题（当前帧快照）
    RenderTheme CurrentTheme { get; }

    // 提醒
    void PostReminder(ReminderData reminder);

    string GetSetting(string key, string fallback);
    void SetSetting(string key, string value);

    // 设置变更事件（主机写入设置后触发，插件订阅以即时响应）
    event Action? SettingsChanged;

    // 刷新调度（插件主动刷新的核心能力）
    IDisposable ScheduleRefresh(TimeSpan interval, Action callback);

    // 交互调度
    void RequestRedraw();
    void OpenDetailPage(string widgetId);

    bool TryOpenDetailPage(string widgetId);

    void CloseDetailPage();

    bool ToggleDetailPage(string widgetId);

    // 布局调度
    void InvalidateWidgetLayout();

    float GetPluginRowBudget();

    /// <summary>
    /// <b>AutoWidth 插件的额度</b>：指定组件在本帧折叠态那一行里真正能占的槽位宽度。
    ///
    /// <para>
    /// 已经扣掉原生内容、排在它前面的组件，以及它自己与前者之间的槽间距 —— 拿到多少就能占多少，
    /// 不用再减 16 或再算一遍前面谁占了多宽。内容长度可选的组件（一言、天气提示语）应该在
    /// <c>Layout</c> 之前用它挑内容：盒子宽度超过它，宿主本帧就会把这个组件整体隐藏。
    /// </para>
    ///
    /// <para>
    /// 返回值语义：<c>0</c> = 本位置没空间了（原生吃满 / 本帧插件行被通知或详情页接管）；
    /// <c>PositiveInfinity</c> = 宿主还没跑过首帧，无从判断；其余为逻辑像素。
    /// 渲染线程与后台线程调用都安全。
    /// </para>
    /// </summary>
    float GetSlotBudget(string widgetId);

    // 窗口
    IPluginWindow CreateWindow(string title, int width, int height);

    bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false);

    /// <summary>
    /// 注册「卸载时允许宿主帮忙删除的目录」—— 插件自己建的数据目录。
    /// 最多注册一个，重复调用只认第一次（第二份会被忽略并在日志里留一条）。
    /// 宿主绝不会擅自删除：卸载时会弹窗把路径摆给用户看，用户点了「删除」才递归删掉，
    /// 点「保留」就只卸载插件、目录原样留在磁盘上。
    /// </summary>
    void RegisterUninstallDirectory(string path);

    /// <summary>
    /// 注册「卸载前要执行的操作」—— 插件自己清理宿主看不见的东西（注册表项、外部进程、临时文件……）。
    /// 只在用户**真正卸载这个插件**时执行一次：禁用、重载、程序退出都不执行，也不弹任何窗。
    /// 执行时机是「插件实例还活着、文件还没删」，所以里面可以正常用自己的资源；
    /// 抛异常只会记一条日志，不会挡住卸载。重复注册以最后一次为准。
    /// </summary>
    void RegisterUninstallAction(Action action);
}
