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

    /// <summary>
    /// 作者名，显示在插件中心的列表里。
    ///
    /// <para>
    /// 带默认实现（返回空串）是**刻意**的：这个成员是后加的，写成抽象成员会让所有
    /// 已编译好的老插件在实例化时直接抛 <see cref="TypeLoadException"/> ——
    /// 有默认实现，老 DLL 照常加载，只是作者一栏空着；
    /// 主机在为空时会退回读程序集的 <c>AssemblyCompany</c> 元数据（即 csproj 里的 Authors/Company）。
    /// </para>
    /// </summary>
    string Author => "";

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

    /// <summary>
    /// <b>完整显示本组件内容所需的宽度</b>（逻辑像素）。
    ///
    /// 主机用它与本帧剩余空间比对：放得下就按这个宽度布局、内容完整显示；
    /// 放不下则本帧<b>整个组件都不显示</b>（主机不会替你压缩、截断或加省略号）。
    /// 所以请返回真实需求值——别为了「挤进去」少报，也别用岛体总长上限（1920，常量
    /// <c>Renderer.MAX_ISLAND_WIDTH</c>）去夹自己。
    /// 组合模式下主机逐块累加。
    /// </summary>
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
    /// <summary>设置键盘输入回调（char）。</summary>
    void SetKey(Action<char>? key);

    /// <summary>
    /// 设置「文件拖入」回调：用户从资源管理器等外部程序把文件 / 文件夹拖到本窗口上并松手时触发，
    /// 参数是拖入项的完整路径数组（顺序即用户拖入的先后）。
    ///
    /// <para>传 <c>null</c> 取消订阅（同时关闭本窗口的文件拖放接收）。窗口默认<b>不接收</b>拖入，调用本方法即开启。</para>
    ///
    /// <para><b>线程</b>：回调在窗口消息线程（通常就是创建窗口的那个线程）同步执行，可以安全地更新自己的列表、
    /// 调用 <see cref="RequestRedraw"/>。宿主会捕获回调里的异常并记日志，不会因此打断消息循环。</para>
    ///
    /// <para><b>只读语义</b>：回调只告诉你「用户拖进来了哪些路径」，不会移动 / 复制 / 删除任何文件 ——
    /// 是引用原路径、还是拷贝到自己的暂存目录，完全由你决定。窗口关闭（或插件卸载）后回调不再触发。</para>
    ///
    /// <para><b>本方法只在「松手」那一刻触发</b>。拖动过程中的悬停反馈（高亮、落点提示）请用
    /// <see cref="SetDragHover"/> 订阅 —— 两者互不依赖，可以只订阅其中一个。</para>
    ///
    /// <para><b>只接受文件拖入</b>：拖入内容里没有文件系统路径时（网页文字、画图工具的位图），
    /// 宿主直接拒绝，本回调不会触发。</para>
    /// </summary>
    void SetFilesDrop(Action<string[]>? onFiles);

    /// <summary>
    /// 设置「拖入悬停」回调，用来在拖放<b>进行中</b>给用户视觉反馈 —— 比如边框亮起来、显示「松手即导入」提示、
    /// 或者把落点画成一个插入位。三个回调都可以单独传 <c>null</c>；只要传了任意一个，本窗口就开始接收拖入。
    ///
    /// <para><b>与 <see cref="SetFilesDrop"/> 的分工</b>：本方法管「拖着的过程」（可能触发几十次），
    /// <see cref="SetFilesDrop"/> 管「松手那一刻」（只触发一次）。两者互不依赖，可以只订阅其中一个。</para>
    ///
    /// <para><b>线程</b>：三个回调都在窗口消息线程同步执行。宿主已捕获异常并记日志，不会打断消息循环。</para>
    ///
    /// <para><b>⚠️ onLeave 一定会来，别把高亮状态只挂在 onOver 上</b>：用户中途按 Esc、把鼠标拖出窗口、
    /// 或者松手放下，宿主都会调一次 onLeave。高亮该在 onEnter 点亮、在 onLeave 熄灭 —— 这是唯一可靠的配对。</para>
    ///
    /// <para><b>⚠️ 只接受文件拖入</b>：拖动内容里没有文件系统路径时（从网页拖一段文字、从画图工具拖一块图像），
    /// 宿主直接拒绝，三个回调一个都不会触发、光标显示为禁止。</para>
    /// </summary>
    /// <param name="onEnter">
    /// 拖入项第一次进入窗口时触发一次，参数是<b>本次拖入的条目数</b>（拖文件夹算 1 个），
    /// 适合用来显示「将导入 3 项」这类提示。
    /// </param>
    /// <param name="onOver">
    /// 鼠标在窗口内移动时持续触发（频率 = 鼠标移动频率），参数是鼠标在窗口内的<b>逻辑坐标</b>
    /// （原点在窗口左上角，和 <see cref="SetMouse"/> 的坐标系一致），可以用来把插入位画在鼠标附近。
    /// </param>
    /// <param name="onLeave">鼠标拖出窗口、拖放被取消、或松手放下时触发，用来复位悬停态。</param>
    void SetDragHover(Action<int>? onEnter, Action<float, float>? onOver, Action? onLeave);

    /// <summary>
    /// 发起一次系统拖放（把文件「拖出去」）：调用后本线程进入系统拖放循环，用户把内容拖到
    /// 资源管理器 / 桌面 / 其他接受文件的程序上松手即完成，也可以拖到另一个插件窗口上。
    ///
    /// <para><b>阻塞</b>：本方法会一直阻塞到用户松手或按 Esc 取消才返回 —— 这是 OLE 拖放的固有行为，
    /// 所以<b>不要在绘制回调里调用</b>。标准做法是在 <see cref="SetMouse"/> 的 move 回调里，
    /// 判断左键仍按下且移动距离超过系统拖拽阈值（<c>SystemInformation.DragSize</c>）后再调用。</para>
    ///
    /// <para><b>⚠️ 拖放期间的鼠标消息被系统接管</b>：<see cref="SetMouse"/> 注册的 up 回调在拖放结束时<b>不会</b>
    /// 触发，所以别指望用它复位「我正在拖动」这类内部状态 —— 请在本方法返回后自己复位。</para>
    ///
    /// <para>不存在的路径会被静默过滤掉；路径全部无效时直接返回 false，不进入拖放循环。</para>
    /// </summary>
    /// <param name="paths">要拖出的文件 / 文件夹路径。</param>
    /// <param name="allowMove">true 时同时允许「移动」效果（拖到同盘目录会真的移动文件）；默认只允许复制。</param>
    /// <returns>true = 用户把内容放到了目标上；false = 取消、无有效路径或拖放失败。</returns>
    bool StartDragFiles(IReadOnlyList<string> paths, bool allowMove = false);

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

    // 布局调度
    /// <summary>
    /// 请求宿主重新测量本插件组件的宽度。
    ///
    /// 宿主的组件宽度是按「组件注册表版本」缓存的：只在插件注册 / 注销 / 排序时调用一次
    /// <see cref="IWidget.MeasureWidth"/>，之后每帧直接复用缓存值（稳态 60FPS 零测量开销）。
    /// 所以插件内容变化导致期望宽度变化时（例如按文本长度自适应），必须调用本方法通知宿主，
    /// 宿主下一帧才会重新测量并用新宽度布局，岛体宽度会平滑过渡到新值。
    ///
    /// 开销：仅让宿主宽度缓存失效一次并重测所有插件组件，别每帧调用。
    /// </summary>
    void InvalidateWidgetLayout();

    /// <summary>
    /// <b>本插件所在位置的剩余可用宽度</b>（逻辑像素，含与原生内容之间的 16px 间距）。
    ///
    /// 语义就是「**不显示本插件时，它那个位置还剩多少长度**」——宿主按组件从左到右的优先级
    /// （先注册的在前、优先显示）逐个分配，把排在前面（更高优先级）的插件已经占掉的宽度扣掉，
    /// 剩下的就是这个值；原生内容（媒体控制器及其长歌词自适应、组合模式下的时间日期 / 硬件占用）占的部分同样已扣。
    ///
    /// <b>用途</b>：宽度随内容自适应的插件（<see cref="IWidget.MeasureWidth"/> 返回动态值）应当用它自查 ——
    /// 取到新内容后先比一下，若自己「完整显示所需宽度」超过这个剩余，宿主下一帧会把该组件**整体隐藏**
    /// （见第九节「岛体总长上限与插件行取舍」）。此时插件应当改取一条更短的内容、或干脆不更新，
    /// 而不是把超预算的宽度报上去等着被隐藏。
    ///
    /// <b>为什么不给固定值</b>：原生占用与岛体尺寸都可能是用户自定义的（控制台里 `Custom_StandbyW` 等），
    /// 组合显示开着时原生模块还会占掉一大截，写死任何阈值都会在某个场景下失准 —— 所以由宿主给实时值。
    ///
    /// <b>语义细节</b>：
    /// · 只减**本帧实际被放行**的组件（没放行的本来就不占宽），本插件自己的组件也不参与扣减。
    /// · 返回值只由原生内容与更高优先级的插件决定，与插件自身内容无关，所以同一场景下稳定；
    ///   切歌 / 通知弹出 / 面板开合 / 前面插件换内容会让它变化，取到数据后再比一次即可。
    /// · 返回 <c>0</c> 表示本位置已无空间（原生内容吃满，或本帧插件行被通知 / 剪贴板 / 详情页接管）；
    ///   返回正无穷表示宿主尚未完成首帧计算。
    ///   这两种情况都不适合判断内容长短，插件应退回自己的保守估值。
    /// · 该值随帧刷新，无需缓存；在渲染线程与后台线程调用都是安全的（内部只读快照，不加锁改状态）。
    /// </summary>
    float GetPluginRowBudget();

    // 窗口
    /// <summary>创建一个插件自有窗口（SkiaSharp 绘制 + 鼠标输入）。</summary>
    IPluginWindow CreateWindow(string title, int width, int height);
}
