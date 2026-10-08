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

    float MeasureWidth(float availableHeight);

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

    // 窗口
    IPluginWindow CreateWindow(string title, int width, int height);

    bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false);
}
