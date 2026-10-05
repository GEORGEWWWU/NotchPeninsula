using SkiaSharp;

namespace NotchPeninsula.Plugins;

// 插件 API 契约：本文件只定义接口与数据模型，不引用任何实现。
// 暂放在主项目命名空间下；如需拆分，可独立成 Abstractions 程序集。

/// <summary>插件入口。主机反射实例化后调用 Initialize。</summary>
public interface INotchPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }

    /// <summary>
    /// 作者名，显示在插件中心列表里。
    /// 带默认实现是刻意的：此成员后加，写成抽象成员会让所有已编译的老插件在实例化时
    /// 抛 TypeLoadException。为空时主机退回读程序集的 AssemblyCompany（csproj 里的 Authors/Company）。
    /// </summary>
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

/// <summary>每帧当前主题，由主机在 ApplyThemeColors 后快照给插件。</summary>
public readonly record struct RenderTheme(
    SKColor TextColor,
    SKColor SubTextColor,
    SKColor BackgroundColor,
    float GlobalDpi,
    float NotchBottomRadius);

/// <summary>
/// 每帧渲染上下文。Alpha 为状态叠化 / 启动淡入的合成透明度（0-255），
/// 组件绘制时用它给主题色做 WithAlpha。
/// </summary>
public readonly record struct WidgetFrame(
    RenderTheme Theme,
    byte Alpha,
    float TextOffsetY,
    float[]? Bars,
    bool IsHovered);

// ---- 数据来源 ----
// 数据由插件自行处理（抓接口 / 轮询进程 / WebSocket 等）。主机不提供数据源，
// 只提供刷新调度、提醒、持久化三类能力。内置组件是内部插件，可直接访问同程序集单例。

// ---- 五种「页」 ----

/// <summary>
/// 主显示区小组件。
///
/// 线程模型：MeasureWidth / Draw 在渲染线程每帧调用，而 ScheduleRefresh 回调在后台线程执行。
/// 因此 Draw / MeasureWidth 读到的插件内部状态必须线程安全（volatile / lock / Interlocked）。
/// </summary>
public interface IWidget
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>null 表示无详情页（一个组件对应一个详情页）。</summary>
    IDetailPage? DetailPage { get; }

    /// <summary>
    /// 收起态（没展开详情页）时，把文件拖到组件身上是否自动展开详情页并接收这次拖放。
    /// 默认 false，不声明即维持原行为。适用于组件本身是「文件入口」的场景：用户从资源管理器
    /// 把文件拖到岛上图标即展开面板、松手即加入，不必先点开面板。
    /// 生效前提：本组件确实提供详情页，且当前没有别的详情页展开着。
    /// 带默认实现是刻意的：组件由插件实现，加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    bool AcceptsFileDropWhenCollapsed => false;

    /// <summary>
    /// 完整显示本组件内容所需的宽度（逻辑像素）。
    /// 主机与剩余空间比对：放得下就按此宽度布局，放不下则本帧整个组件不显示
    /// （主机不会压缩、截断或加省略号）。请返回真实需求值，别少报，也别用岛体总长上限
    /// （Renderer.MAX_ISLAND_WIDTH）去夹自己。组合模式下主机逐块累加。
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

    /// <summary>
    /// 是否把岛内**右键单击**透传给本组件。默认 false —— 右键仍然是主机的默认行为
    /// （有详情页就展开它，没有就打开设置窗口）。
    ///
    /// 置 true 后，命中本组件的右键**不再触发展开详情页 / 打开设置窗口**，只回调 OnRightClick()，
    /// 这次右键完全由组件消费（自己弹菜单 / 自己调 IPluginHost.OpenDetailPage）。
    /// ⚠️ 开了它就等于放弃了「右键展开详情页」这个默认入口，插件得自己给用户另留一条路（左键单击等）。
    ///
    /// 与 AcceptsDoubleClick 的分工：
    ///   · 只开双击、不开单击 —— 主机用「第一下挂起、等一个系统双击窗口」的办法保住单击的默认行为
    ///     （代价：单击响应晚约 500ms）；
    ///   · 两个都开 —— 不需要等：单击直接回调 OnRightClick()，第二下到了再补一次 OnRightDoubleClick()。
    ///
    /// 带默认实现是刻意的：加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    bool AcceptsRightClick => false;

    /// <summary>
    /// 是否注册接收「双击」通知（左键 / 右键各一条回调）。默认 false —— 双击完全走主机原有行为
    /// （左键：媒体封面跳转 / 待机切换；右键：展开本组件详情页），与老插件的行为一模一样。
    ///
    /// 置 true 后，主机在**真的检测到双击**时才回调 OnLeftDoubleClick / OnRightDoubleClick；
    /// 单击不会触发这两个回调，主机也不会「延迟单击等双击」——
    /// 所以双击的第一下仍会照常走一次普通交互（左键：OnLeftClick；右键见下），插件要自己保证幂等。
    ///
    /// 右键的特殊处理：命中本组件的第一下右键**不会立即展开详情页**，而是先等一个系统双击判定窗口
    /// （GetDoubleClickTime，默认 500ms）—— 窗口内来了第二下就回调插件（不展开详情页）；
    /// 窗口过了还没来，才执行原来的「展开详情页 / 打开设置窗口」。
    /// 也就是说：开了这个开关，右键单击的响应会晚约 500ms（换双击能被识别），单击仍然是原来的行为。
    ///
    /// 带默认实现是刻意的：组件由插件实现，加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    bool AcceptsDoubleClick => false;

    /// <summary>
    /// 左键双击（仅在 <see cref="AcceptsDoubleClick"/> 为 true 时调用）。
    /// x/y 是相对本组件矩形左上角的逻辑坐标，与 OnLeftClick 同一套口径。
    /// </summary>
    void OnLeftDoubleClick(float x, float y) { }

    /// <summary>
    /// 右键双击（仅在 <see cref="AcceptsDoubleClick"/> 为 true 时调用）。坐标口径同 OnLeftDoubleClick。
    /// 这条只在「第一下右键之后又来了第二下」时触发；单击走的是主机原有行为，不会到这里。
    /// </summary>
    void OnRightDoubleClick(float x, float y) { }

    void OnActivate(IPluginHost host);
    void OnDeactivate();
}

/// <summary>
/// 详情页（右键组件展开后显示）。
/// 鼠标、双击 / 右键透传、拖放三组成员用默认接口实现（DIM）追加，是刻意的：详情页由插件实现，
/// 加抽象成员会让已编译的老插件在加载时抛 TypeLoadException。
///
/// 鼠标事件与 HitTest / OnAction 的关系：后者是宿主代为命中检测、一次点击只有一个回调；
/// 前者是完整的按下 / 移动 / 抬起，需要自己命中检测，但能实现「按住拖动」。
/// 两者并存——HitTest 返回 WidgetHit.None 即把自己从后者摘出去，只走鼠标事件。
/// </summary>
public interface IDetailPage
{
    float MeasureWidth();
    float MeasureHeight();
    void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame);
    WidgetHit HitTest(float x, float y, SKRect rect);
    void OnAction(string? action, float x, float y);

    // ---- 鼠标事件 ----
    // 三个坐标都是详情页内的逻辑坐标（原点在详情页左上角），与 HitTest 同一套。

    /// <summary>左键按下。</summary>
    void OnMouseDown(float x, float y) { }

    /// <summary>鼠标移动。只在鼠标位于灵动岛内时触发（拖放期间系统接管鼠标，不会触发）。</summary>
    void OnMouseMove(float x, float y) { }

    /// <summary>左键抬起。</summary>
    void OnMouseUp(float x, float y) { }

    /// <summary>
    /// 鼠标离开灵动岛。用它复位「按住」之类的状态：按下后把鼠标拖出岛体再松手，
    /// OnMouseUp 不会触发，只有这条会到。
    /// </summary>
    void OnMouseLeave() { }

    // ---- 双击 / 右键透传 ----
    // 详情页展开时，岛内右键是主机的「关闭面板」手势（立即折叠）。插件想在面板里做右键交互，
    // 就没法在这个手势里拿到坐标 —— 这一组就是给它留的口子，两档互相独立：
    //   · AcceptsRightClick —— 右键**单击**也交回插件（面板不再折叠，最直接，也没有等待）；
    //   · AcceptsDoubleClick —— 只把「双击」交回插件，单击照旧折叠（主机挂一个双击窗口来分辨）。

    /// <summary>
    /// 是否把岛内**右键单击**透传给本详情页。默认 false —— 右键单击立即折叠面板，与老插件行为完全一致。
    ///
    /// 置 true 后，详情页展开期间的岛内右键**不再折叠面板**：
    ///   第一下 → 回调 <see cref="OnRightClick(float,float)"/>；
    ///   第二下（双击）→ 在同时声明了 <see cref="AcceptsDoubleClick"/> 时再补一次 OnRightDoubleClick。
    /// 这一档没有等待：单击当场就通知，双击只是「再来一次」。
    ///
    /// ⚠️ 它把宿主「右键关面板」的手势整个让给了你，所以务必自己留一个看得见的关闭出口
    /// （面板里的 × 按钮，或自己调 IPluginHost.CloseDetailPage()）。
    /// 否则面板只剩「鼠标移开自动收起」这一条路（若又设了 AutoCollapseDelay 为负数，那就彻底关不掉了）。
    ///
    /// 带默认实现是刻意的：详情页由插件实现，加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    bool AcceptsRightClick => false;

    /// <summary>
    /// 右键单击（仅在 <see cref="AcceptsRightClick"/> 为 true 时调用）。
    /// x/y 为详情页内的逻辑坐标，与 OnMouseDown / HitTest 同一套口径。
    /// </summary>
    void OnRightClick(float x, float y) { }

    /// <summary>
    /// 是否把岛内右键**双击**透传给本详情页。默认 false —— 右键单击立即折叠面板（老行为）。
    ///
    /// 置 true 后，右键第一下**不立即折叠**，而是先等一个系统双击判定窗口
    /// （GetDoubleClickTime，默认 500ms）：
    ///   · 窗口内来了第二下 → 回调 <see cref="OnRightDoubleClick"/>，**面板保持展开不折叠**；
    ///   · 窗口过了还没来 → 按原行为折叠面板。
    /// 即「只有检测到双击才通知插件」：单击不会触发任何回调，只是折叠晚约 500ms。
    ///
    /// 如果同时开了 <see cref="AcceptsRightClick"/>，这一档的等待就不需要了 —— 单击已经给了插件，
    /// 双击只是随后再补一次；两者都开时主机走「不等待」那条路。
    ///
    /// 带默认实现是刻意的：详情页由插件实现，加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    bool AcceptsDoubleClick => false;

    /// <summary>
    /// 左键双击（仅在 <see cref="AcceptsDoubleClick"/> 为 true 时调用）。
    /// x/y 为详情页内的逻辑坐标，与 OnMouseDown / HitTest 同一套口径。
    /// ⚠️ 双击的第一下仍会照常走一次 HitTest / OnAction 与 OnMouseDown / OnMouseUp，插件自己保证幂等。
    /// </summary>
    void OnLeftDoubleClick(float x, float y) { }

    /// <summary>
    /// 右键双击（仅在 <see cref="AcceptsDoubleClick"/> 为 true 时调用）—— 这就是「右键透传」的落地回调。
    /// x/y 为详情页内的逻辑坐标。**只有真的检测到双击才会到**，单击不触发（单击只会折叠面板）。
    /// </summary>
    void OnRightDoubleClick(float x, float y) { }

    // ---- 文件拖放（岛体上的详情页也能拖入 / 拖出）----

    /// <summary>
    /// 有文件被拖到详情页上。返回 true 表示接受（光标变为可放入，之后才会收到
    /// OnFilesDragOver 与 OnFilesDrop）；返回 false 或保持默认实现即拒绝。
    /// 只有带文件系统路径的拖入才会走到这里：从网页拖的文字、从画图工具拖的位图在宿主层就被挡掉。
    /// </summary>
    /// <param name="count">本次拖入的条目数（拖一个文件夹算 1 个）。</param>
    bool OnFilesDragEnter(int count) => false;

    /// <summary>拖放过程中鼠标在详情页内移动。x/y 是详情页内的逻辑坐标。</summary>
    void OnFilesDragOver(float x, float y) { }

    /// <summary>
    /// 拖出详情页 / 拖放被取消（含按 Esc）。
    /// 这条一定会来，是复位悬停高亮的唯一可靠时机，别把高亮只挂在 OnFilesDragOver 上。
    /// </summary>
    void OnFilesDragLeave() { }

    /// <summary>用户在详情页里松手。paths 是落下的完整路径数组。只在 OnFilesDragEnter 接受后才会触发。</summary>
    void OnFilesDrop(string[] paths) { }

    /// <summary>
    /// 鼠标离开灵动岛后，本详情页多久自动收起。三种取值：
    ///   TimeSpan.Zero（默认）—— 用宿主内置时长，普通详情页不用管；
    ///   正数 —— 自定义时长，宿主夹在 0.5 秒 ~ 60 秒之间；
    ///   负数（惯例写 Timeout.InfiniteTimeSpan）—— 鼠标离开也不收起，直到用户自己关掉。
    ///
    /// 需要动它的场景：面板要求用户「先离开面板、去别处拿点东西再回来」，典型是拖入文件 ——
    /// 宿主内置延迟只有几百毫秒，鼠标刚移开面板就收了，拖放目标当场消失。调长（几秒）通常够用；
    /// 要翻目录找一阵子就直接用「不收起」。
    ///
    /// 选「不收起」等于全局屏蔽自动收起：鼠标离开不收，点到岛外也不收——因为正在从资源管理器
    /// 往面板里拖文件的用户，鼠标必然经过岛外，那不能算「想关面板」。
    /// 关闭出口：在岛内按右键（不受这一档影响），或自行调 IPluginHost.CloseDetailPage。
    /// 因此选这一档的详情页最好仍留一个看得见的关闭出口，或做成根本不需要关的常驻面板。
    /// 带默认实现是刻意的：加抽象成员会让已编译的老插件加载失败。
    /// </summary>
    TimeSpan AutoCollapseDelay => TimeSpan.Zero;
}

/// <summary>副显示区小组件（仅只读信息展示）。</summary>
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
/// 一个设置页可同时实现 ISettingsPage（声明式控件）与本接口（自定义 UI），两者都会渲染。
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

/// <summary>插件窗口（由 IPluginHost.CreateWindow 创建）。</summary>
public interface IPluginWindow
{
    /// <summary>设置绘制回调 (canvas, width, height)。每次重绘时调用。</summary>
    void SetDraw(Action<SKCanvas, int, int>? draw);
    /// <summary>设置鼠标回调 (x, y)。</summary>
    void SetMouse(Action<float, float>? down, Action<float, float>? move, Action<float, float>? up);
    /// <summary>设置键盘输入回调（char）。</summary>
    void SetKey(Action<char>? key);

    /// <summary>
    /// 设置「文件拖入」回调：用户从外部程序把文件 / 文件夹拖到本窗口并松手时触发，
    /// 参数是拖入项的完整路径数组（顺序即拖入先后）。
    ///
    /// 传 null 取消订阅（同时关闭本窗口的文件拖放接收）。窗口默认不接收拖入，调用本方法即开启。
    /// 只接受文件拖入：拖入内容里没有文件系统路径时（网页文字、位图），宿主直接拒绝，本回调不触发。
    /// 回调在窗口消息线程同步执行，可以安全更新自己的列表、调 RequestRedraw；宿主捕获异常记日志，不打断消息循环。
    /// 回调只告知「拖进来了哪些路径」，不会移动 / 复制 / 删除任何文件——引用原路径还是拷贝到暂存目录由插件决定。
    /// 本方法只在松手那一刻触发；拖动过程中的悬停反馈请用 SetDragHover，两者互不依赖。
    /// </summary>
    void SetFilesDrop(Action<string[]>? onFiles);

    /// <summary>
    /// 设置「拖入悬停」回调，用于拖放进行中给用户视觉反馈（边框高亮、落点提示等）。
    /// 三个回调都可单独传 null；只要传了任意一个，本窗口就开始接收拖入。
    ///
    /// 与 SetFilesDrop 的分工：本方法管「拖着的过程」（可能触发几十次），SetFilesDrop 管「松手那一刻」（一次）。
    /// 三个回调都在窗口消息线程同步执行，宿主已捕获异常记日志。
    /// 只接受文件拖入：拖动内容里没有文件系统路径时，宿主直接拒绝，三个回调都不触发。
    /// </summary>
    /// <param name="onEnter">拖入项首次进入窗口时触发一次，参数是本次拖入的条目数（拖文件夹算 1 个）。</param>
    /// <param name="onOver">
    /// 鼠标在窗口内移动时持续触发（频率 = 鼠标移动频率），参数是窗口内逻辑坐标
    /// （原点在窗口左上角，与 SetMouse 同一套），可用于把插入位画在鼠标附近。
    /// </param>
    /// <param name="onLeave">鼠标拖出窗口、拖放取消、或松手放下时触发，用来复位悬停态。这条一定会来，别把高亮只挂在 onOver 上。</param>
    void SetDragHover(Action<int>? onEnter, Action<float, float>? onOver, Action? onLeave);

    /// <summary>
    /// 发起一次系统拖放（把文件拖出去）：本线程进入系统拖放循环，用户拖到资源管理器 / 桌面 /
    /// 其他接受文件的程序上松手即完成。不存在的路径会被静默过滤；路径全部无效时直接返回 false，不进入循环。
    ///
    /// 本方法阻塞到用户松手或按 Esc 才返回（OLE 固有行为），所以不要在绘制回调里调用。
    /// 标准做法是在 SetMouse 的 move 回调里，判断左键仍按下且位移超过系统拖拽阈值
    /// （SystemInformation.DragSize）后再调用。
    /// 拖放期间鼠标消息被系统接管：SetMouse 注册的 up 回调在拖放结束时不会触发，
    /// 别指望用它复位「我正在拖动」的状态，请在本方法返回后自己复位。
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
    /// 回调在后台线程执行，插件在回调内更新自己的数据；主机渲染循环每帧读取最新数据，
    /// 无需插件手动触发重绘。返回 IDisposable，Dispose 即停止刷新。
    /// </summary>
    IDisposable ScheduleRefresh(TimeSpan interval, Action callback);

    // 交互调度
    /// <summary>异步取到数据后主动请求重绘（常驻 60FPS 渲染下等价于空操作，作为事件驱动化预留）。</summary>
    void RequestRedraw();
    void OpenDetailPage(string widgetId);

    /// <summary>
    /// 展开指定组件的详情页，并返回本次是否展开成功——OpenDetailPage 的「带结果」版本。
    /// OpenDetailPage 在插件侧是 void，宿主包装层把内部结果丢掉了，插件无从判断组件是否存在 / 有无详情页。
    ///
    /// 想「有就开、没有就算了」且不希望收起已展开的面板时用它；
    /// ToggleDetailPage 虽然也返回 bool，但语义是切换——目标已展开时会把它收起。
    /// </summary>
    /// <returns>
    /// true = 目标详情页已处于展开状态（本次展开，或本来就开着）；
    /// false = 组件不存在、没有详情页，或读取详情页时抛了异常。
    /// </returns>
    bool TryOpenDetailPage(string widgetId);

    void CloseDetailPage();

    /// <summary>
    /// 切换指定组件详情页的开合：已展开就收起，没展开就展开。
    /// 这就是「右键组件」的宿主默认行为，想让左键与右键表现一致就在 IWidget.OnLeftClick 里调它。
    /// </summary>
    /// <returns>true = 本次操作被消费（展开或收起）；false = 组件不存在，或它没有详情页。</returns>
    bool ToggleDetailPage(string widgetId);

    // 布局调度
    /// <summary>
    /// 请求宿主重新测量本插件组件的宽度。
    ///
    /// 宿主按「组件注册表版本」缓存宽度：只在插件注册 / 注销 / 排序时调用一次 IWidget.MeasureWidth，
    /// 之后每帧复用缓存值（稳态 60FPS 零测量开销）。所以插件内容变化导致期望宽度变化时
    /// （如按文本长度自适应），必须调用本方法通知宿主，宿主下一帧才会重测并以新宽度布局，
    /// 岛体宽度平滑过渡。开销仅一次缓存失效与重测，别每帧调用。
    /// </summary>
    void InvalidateWidgetLayout();

    /// <summary>
    /// 本插件所在位置的剩余可用宽度（逻辑像素，含与原生内容之间的 16px 间距）。
    /// 即「不显示本插件时，它那个位置还剩多少长度」——宿主按组件从左到右的优先级逐个分配，
    /// 扣掉已放行的更高优先级插件与原生内容（媒体控制器及其长歌词、组合模式下的时间日期 / 硬件占用）所占宽度。
    ///
    /// 宽度自适应的插件应当用它自查：取到新内容后先比对，若完整显示所需宽度超过这个剩余，
    /// 宿主下一帧会把该组件整体隐藏。此时应改取更短的内容或干脆不更新，而不是把超预算宽度报上去。
    ///
    /// 不给固定值的原因：原生占用与岛体尺寸都可能是用户自定义的，组合显示开着时原生模块还会占一大截，
    /// 写死阈值总会在某个场景失准，所以由宿主给实时值。
    ///
    /// 细节：只减本帧实际放行的组件，本插件自身组件不参与扣减；该值只由原生内容与更高优先级插件决定，
    /// 与插件自身内容无关，所以同场景下稳定，切歌 / 通知 / 面板开合 / 前面插件换内容会让它变化。
    /// 返回 0 表示本位置已无空间（原生吃满，或本帧插件行被通知 / 剪贴板 / 详情页接管）；
    /// 返回正无穷表示宿主尚未完成首帧计算。这两种情况都不适合判断内容长短，插件应退回保守估值。
    /// 该值随帧刷新，无需缓存；渲染线程与后台线程调用都安全（内部只读快照）。
    /// </summary>
    float GetPluginRowBudget();

    // 窗口
    /// <summary>创建一个插件自有窗口（SkiaSharp 绘制 + 鼠标输入）。</summary>
    IPluginWindow CreateWindow(string title, int width, int height);

    /// <summary>
    /// 发起一次系统拖放（把内容拖出去）。用于画在灵动岛上的插件内容（如详情页）——
    /// 它没有自己的窗口，拖出只能由宿主在岛体上代为发起。插件自有窗口请用 IPluginWindow.StartDragFiles。
    /// 不存在的路径会被静默过滤；路径全部无效时直接返回 false，不进入拖放循环。
    ///
    /// 本方法阻塞到用户松手或按 Esc，期间系统接管鼠标，所以别在 IDetailPage.Draw 里调用。
    /// 标准做法是在 IDetailPage.OnMouseMove 里，判断左键仍按下且位移超过阈值后再调。
    /// </summary>
    /// <param name="paths">要拖出的文件 / 文件夹路径。</param>
    /// <param name="allowMove">true 时同时允许「移动」效果（拖到同盘目录会真的移动文件）；默认只允许复制。</param>
    /// <returns>true = 用户把内容放到了目标上；false = 取消、无有效路径或拖放失败。</returns>
    bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false);
}
