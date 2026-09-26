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
    /// <b>组件处于收起态（没展开详情页）时，把文件拖到它身上是否自动展开它的详情页并接收这次拖放。</b>
    /// 默认 <c>false</c> —— 不声明就完全维持原行为（拖到收起态组件上什么都不会发生）。
    ///
    /// <para>
    /// 什么时候该打开它：组件本身就是「文件入口」，用户的意图是「把东西丢到这个插件上」。
    /// 典型就是文件中转站 —— 打开之后，用户从资源管理器把文件拖到岛上那个图标上，
    /// 面板会自己展开、光标变成「可放入」，松手即加入，<b>不必先点开面板</b>。
    /// </para>
    ///
    /// <para>
    /// 生效前提有两个：本组件<b>确实提供了详情页</b>（<see cref="DetailPage"/> 非 null），
    /// 且当前没有别的详情页展开着（否则不会抢走已有面板）。
    /// 拖放期间的收尾、悬停高亮仍由 <see cref="IDetailPage"/> 的拖入回调负责，这里只决定「要不要自动展开」。
    /// </para>
    ///
    /// <para>带默认实现是刻意的：组件是<b>插件实现</b>的接口，加抽象成员会让所有已编译好的老插件加载失败。</para>
    /// </summary>
    bool AcceptsFileDropWhenCollapsed => false;

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

/// <summary>
/// 详情页（右键组件展开后显示）。
///
/// <para>
/// <b>关于下面这些带默认实现的成员</b>：详情页是<b>插件实现</b>的接口，加抽象成员会让所有
/// 已编译好的老插件在加载时直接抛 <see cref="TypeLoadException"/>，所以鼠标与拖放这两组
/// 一律用默认接口实现（DIM）追加 —— 老插件照常工作，只是收不到这些回调。
/// </para>
///
/// <para>
/// <b>鼠标事件与 <see cref="HitTest"/> / <see cref="OnAction"/> 的关系</b>：老的一套是
/// 「宿主帮你做命中检测、只告诉你点了哪个动作」，一次点击只有一个回调；
/// 新的一套是完整的按下 / 移动 / 抬起，需要自己做命中检测，但能实现「按住拖动」这类交互。
/// 两者并存：<see cref="HitTest"/> 返回 <see cref="WidgetHit.None"/> 就能把自己从老那套里摘出去，
/// 只走鼠标事件。
/// </para>
/// </summary>
public interface IDetailPage
{
    float MeasureWidth();
    float MeasureHeight();
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
    WidgetHit HitTest(float x, float y, SKRect rect);
    void OnAction(string? action, float x, float y);

    // ---- 鼠标事件（比 HitTest/OnAction 更细，实现「按住拖动」必须靠它）----
    // 三个坐标都是**详情页内**的逻辑坐标（原点在详情页左上角），与 HitTest 收到的是同一套。

    /// <summary>左键按下。</summary>
    void OnMouseDown(float x, float y) { }

    /// <summary>鼠标移动。只在鼠标位于灵动岛内时触发（拖放过程中系统接管鼠标，不会触发）。</summary>
    void OnMouseMove(float x, float y) { }

    /// <summary>左键抬起。</summary>
    void OnMouseUp(float x, float y) { }

    /// <summary>
    /// 鼠标离开灵动岛。⚠️ 用它复位「按住」之类的状态 ——
    /// 按下之后把鼠标拖出岛体再松手，<see cref="OnMouseUp"/> 是<b>不会</b>来的，
    /// 只有这条会到。
    /// </summary>
    void OnMouseLeave() { }

    // ---- 文件拖放（岛体上的详情页也能拖入 / 拖出）----

    /// <summary>
    /// 有文件被拖到详情页上。返回 true = 接受这次拖放（光标变成「可放入」，
    /// 后续才会收到 <see cref="OnFilesDragOver"/> 与 <see cref="OnFilesDrop"/>）；
    /// 返回 false 或保持默认实现 = 拒绝，光标显示为禁止。
    ///
    /// <para>只有带文件系统路径的拖入才会走到这里：从网页拖的文字、从画图工具拖的位图
    /// 在宿主那一层就被挡掉了，一个回调都不会触发。</para>
    /// </summary>
    /// <param name="count">本次拖入的条目数（拖一个文件夹算 1 个）。</param>
    bool OnFilesDragEnter(int count) => false;

    /// <summary>拖放过程中鼠标在详情页内移动。x/y 是详情页内的逻辑坐标。</summary>
    void OnFilesDragOver(float x, float y) { }

    /// <summary>
    /// 拖出详情页 / 拖放被取消（含用户按 Esc）。
    /// ⚠️ 这条一定会来，是复位悬停高亮的唯一可靠时机 —— 别把高亮只挂在 <see cref="OnFilesDragOver"/> 上。
    /// </summary>
    void OnFilesDragLeave() { }

    /// <summary>用户在详情页里松手。paths 是落下的完整路径数组。只在 <see cref="OnFilesDragEnter"/> 接受后才会触发。</summary>
    void OnFilesDrop(string[] paths) { }

    /// <summary>
    /// 鼠标离开灵动岛之后，本详情页多久自动收起。三种取值：
    /// <list type="bullet">
    ///   <item><see cref="TimeSpan.Zero"/>（默认实现）—— 用宿主内置时长，普通详情页不用管这个属性。</item>
    ///   <item><b>正数</b> —— 自定义时长，宿主会夹在 0.5 秒 ~ 60 秒之间（防呆）。</item>
    ///   <item><b>负数</b>（惯例写 <see cref="Timeout.InfiniteTimeSpan"/>）—— <b>鼠标离开也不收起</b>，
    ///         面板一直开着，直到用户自己关掉。</item>
    /// </list>
    ///
    /// <para>
    /// <b>什么时候需要动它</b>：当你的面板要求用户「先离开面板、去别处拿点东西再回来」的时候。
    /// 典型就是拖入文件 —— 用户得把鼠标移到资源管理器里挑文件，而宿主内置的收起延迟只有几百毫秒，
    /// 鼠标刚移开面板就自己收了，拖放目标当场消失，等于根本没法拖进来。
    /// 把时间调长（几秒）通常就够；如果用户可能需要翻目录找一阵子，那就直接用「不收起」。
    /// </para>
    ///
    /// <para>
    /// <b>选「不收起」= 全局屏蔽自动收起</b>：鼠标离开不收，<b>点到岛外也不收</b> ——
    /// 因为正在从资源管理器往面板里拖文件的用户，鼠标必然要经过岛外，
    /// 那种「点到别处去了」不能算「想关面板」。
    /// </para>
    ///
    /// <para>
    /// <b>那怎么关掉它</b>：在岛内按右键（用户明确冲着面板来的手势，不受这一档影响），
    /// 或者你自己调 <see cref="IPluginHost.CloseDetailPage"/>。所以选了这一档的详情页
    /// 最好仍然给用户留一个看得见的关闭出口（或者做成根本不需要关的常驻面板）。
    /// </para>
    ///
    /// <para>带默认实现是刻意的：详情页是插件实现的接口，加抽象成员会让所有已编译好的老插件加载失败。</para>
    /// </summary>
    TimeSpan AutoCollapseDelay => TimeSpan.Zero;
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

    /// <summary>
    /// 切换指定组件详情页的开合：已展开就收起，没展开就展开。
    ///
    /// <para>
    /// 这正是「右键组件」的宿主默认行为 —— 想让左键和右键表现一致，
    /// 就在 <see cref="IWidget.OnLeftClick"/> 里调它。
    /// </para>
    /// </summary>
    /// <returns>true = 这次操作被消费（展开或收起了）；false = 该组件不存在、或它没有详情页。</returns>
    bool ToggleDetailPage(string widgetId);

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

    /// <summary>
    /// 发起一次系统拖放（把内容「拖出去」）。用于<b>详情页</b>这种画在灵动岛上的插件内容 ——
    /// 它没有自己的窗口，所以拖出只能由宿主在岛体上代为发起。
    /// （插件自有窗口请改用 <see cref="IPluginWindow.StartDragFiles"/>。）
    ///
    /// <para><b>阻塞</b>：会一直阻塞到用户松手或按 Esc 取消，期间系统接管鼠标。
    /// 所以别在 <see cref="IDetailPage.Draw"/> 里调用 —— 标准做法是在
    /// <see cref="IDetailPage.OnMouseMove"/> 里，判断左键仍按下且位移超过阈值后再调。</para>
    ///
    /// <para>不存在的路径会被静默过滤；路径全部无效时直接返回 false，不进入拖放循环。</para>
    /// </summary>
    /// <param name="paths">要拖出的文件 / 文件夹路径。</param>
    /// <param name="allowMove">true 时同时允许「移动」效果（拖到同盘目录会真的移动文件）；默认只允许复制。</param>
    /// <returns>true = 用户把内容放到了目标上；false = 取消、无有效路径或拖放失败。</returns>
    bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false);
}
