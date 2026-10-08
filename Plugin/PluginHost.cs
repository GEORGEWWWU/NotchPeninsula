using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 原生（内置）内容模块的伪 Id，与插件组件共处同一张「内容显示顺序表」。
/// 有了它们，用户就能在「显示设置 → 显示内容」里把插件挪到时间日期 / 硬件占用 / 媒体控制器之间或之前。
/// </summary>
public static class BuiltinWidgets
{
    public const string Clock = "builtin.clock";        // 时间日期
    public const string Hardware = "builtin.hardware";  // CPU / RAM 占用
    public const string Media = "builtin.media";        // 媒体控制器

    /// <summary>默认排列：原生模块在左，插件跟在其后（与引入顺序表之前的行为完全一致）。</summary>
    public static readonly string[] Default = { Clock, Hardware, Media };

    public static bool IsBuiltin(string id)
        => string.Equals(id, Clock, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Hardware, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Media, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 插件宿主：注册中心 + 服务入口。
/// 由 NotchWindow 持有单例。外部插件通过 CreateScopedHost 获得绑定自身 Id 的视图。
///
/// 热加载关键点：所有注册物（组件/设置页/刷新句柄/设置事件）都按 PluginId 归组登记，
/// UnregisterPlugin 能把某个插件留下的引用全部摘掉，这样承载它的
/// AssemblyLoadContext 才有可能被 GC 真正回收。
/// </summary>
public sealed class PluginHost
{
    private const string RegistryBase = @"SOFTWARE\NotchPeninsula";

    private readonly object _lock = new();

    private readonly List<IWidget> _widgets = new();
    private readonly List<ISecondaryWidget> _secondaryWidgets = new();
    private readonly List<(string PluginId, ISettingsPage Page)> _settingsPages = new();
    private readonly List<(string Id, string DisplayName, string Version)> _plugins = new();

    // 组件 -> 所属插件 的反查表
    private readonly Dictionary<string, string> _widgetPluginMap = new();
    private readonly Dictionary<string, string> _secondaryWidgetPluginMap = new();

    // 每个插件登记的资源，卸载时统一释放
    private readonly Dictionary<string, List<IDisposable>> _refreshes = new();
    private readonly Dictionary<string, Action?> _settingsHandlers = new();

    // 每个插件打开的窗口。插件窗口的绘制/输入回调是插件实例方法的委托，会直接引用插件类型；
    // 若不在这里记账并在卸载时关掉，窗口会被 PluginWindow 的静态路由表强引用，
    // 承载它的可回收 ALC 就永远回收不掉（热重载持续泄漏旧版本程序集）。
    private readonly Dictionary<string, List<IPluginWindow>> _windows = new();

    /// <summary>
    /// 正在卸载中的插件 id。卸载期间拒绝新的登记（刷新句柄 / 窗口）。
    ///
    /// 卸载顺序是「先让插件 Dispose、再 UnregisterPlugin」，而插件的 Dispose 里完全可能再调一次
    /// ScheduleRefresh（「清理时顺手重置一下定时刷新」很常见），也可能有刷新回调正在别的线程上飞。
    /// 那时 _refreshes 里的条目已/即将被摘掉，它新建的条目再没人遍历到：定时器一直跑 →
    /// 回调委托 → 插件类型 → Assembly → ALC 永远回收不掉。这类幽灵定时器还会反复打扰下一次
    /// 加载的同一插件。所以卸载期间一律拒绝登记：新句柄当场 Dispose，新窗口直接不建。
    /// </summary>
    private readonly HashSet<string> _unregistering = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 卸载时没能立刻销毁的窗口（拖放进行中时 TryDestroyNow 会拒绝，Close() 只是投一条
    /// WM_CLOSE 又被推后）。
    ///
    /// 以前是先把 _windows[pluginId] 整表摘掉、再尝试关窗：一旦这次关不掉，宿主就永久失去了这个
    /// 窗口的记账，而它还被 PluginWindow 的静态路由表强引用着，于是 HWND + DIB 位图 + 内存 DC
    /// + 插件程序集一起永久留下。现在改成留着记账、每帧重试。
    /// </summary>
    private readonly List<IPluginWindow> _pendingWindowClose = new();

    /// <summary>重试关窗的累计次数，只用于「极长时间关不掉就不再重试」那条兜底判定。</summary>
    private int _windowCloseAttempts;

    // 组件注册表版本号：任何 Register/Unregister/排序 都会自增。
    // 渲染侧（Renderer）用它做快照缓存 —— 只有版本变化时才重新拷贝组件数组，
    // 稳态 60FPS 下读取零分配，插件禁用/卸载后渲染侧下一帧自动感知。
    private int _widgetsVersion;

    // ---- 详情页展开状态（右键组件 / IPluginHost.OpenDetailPage）----
    // 由宿主统一持有：渲染侧每帧读 ActiveDetailPage 决定岛体是否整块切成详情页，
    // NotchWindow 读尺寸决定岛体展开多大。组件卸载/插件卸载时这里必须同步失效。
    private string? _activeDetailWidgetId;
    private IDetailPage? _activeDetailPage;

    /// <summary>详情页展开 / 收起时触发（渲染侧可借此立即重绘；不保证在 UI 线程）。</summary>
    public event Action? DetailPageChanged;

    // 内容显示顺序（builtin.* 原生模块 + 插件 pluginId 混排）：决定灵动岛上各内容的排列次序。
    // 由 PluginManager 从注册表读回后通过 SetPluginOrder 注入，宿主只按它输出组件，不做持久化。
    private readonly List<string> _pluginOrder = new();
    // _pluginOrder 的只读快照：渲染侧每帧读取，避免每帧 ToArray 分配
    private string[] _contentOrderArr = Array.Empty<string>();

    // 「已加载但不显示」的插件 id：显示设置里取消勾选 = 只从岛上收起，不禁用、不卸载。
    // 由 PluginManager 通过 SetHiddenPlugins 注入；过滤必须放在 Widgets 这个唯一出口上 ——
    // 渲染侧（DrawPluginWidgets / SumPluginRowWidth / SumPluginRowWidthNotIn）全部只吃 Widgets，
    // 把 id 从 ContentOrder 里删掉不够（那样组件会落进「未登记顺序」的兜底桶里，照样被画出来）。
    private readonly HashSet<string> _hiddenPlugins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 内容显示顺序（含 builtin.* 原生模块与插件 pluginId）。渲染侧据此把原生模块与插件组件混排。
    /// 返回内部快照数组，读取零分配。
    /// </summary>
    public IReadOnlyList<string> ContentOrder { get { lock (_lock) return _contentOrderArr; } }

    /// <summary>
    /// 注入「已加载但不显示」的插件 id 集合（显示设置为空的那些）。只影响组件的输出，不触发加载/卸载。
    /// </summary>
    public void SetHiddenPlugins(IReadOnlyCollection<string> hiddenIds)
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in hiddenIds)
            if (!string.IsNullOrEmpty(id)) next.Add(id);

        lock (_lock)
        {
            if (next.SetEquals(_hiddenPlugins)) return; // 没变就别惊动渲染侧

            _hiddenPlugins.Clear();
            foreach (var id in next) _hiddenPlugins.Add(id);

            _widgetsVersion++; // 组件集合变了 → 让渲染侧下一帧重建排序快照
        }
    }

    /// <summary>查询某个组件属于哪个插件（渲染侧按插件分组绘制用）。</summary>
    public bool TryGetWidgetPlugin(string widgetId, out string pluginId)
    {
        lock (_lock) return _widgetPluginMap.TryGetValue(widgetId, out pluginId!);
    }

    /// <summary>
    /// 主显示区组件（已按插件显示顺序排列）。
    /// 顺序由 SetPluginOrder 注入；未登记顺序的组件保持注册顺序追加在末尾。
    /// SetHiddenPlugins 标记的插件，其组件一律不出现在这里（既不排布也不绘制）。
    /// </summary>
    public IReadOnlyList<IWidget> Widgets
    {
        get
        {
            lock (_lock)
            {
                if (_widgets.Count == 0) return Array.Empty<IWidget>();
                if (_pluginOrder.Count == 0 && _hiddenPlugins.Count == 0) return _widgets.ToArray();

                // 按插件顺序输出，同一插件内部保持其注册顺序
                var ordered = new IWidget[_widgets.Count];
                int n = 0;
                foreach (var pid in _pluginOrder)
                {
                    if (_hiddenPlugins.Contains(pid)) continue; // 被隐藏的插件：组件留着，但不参与排布
                    foreach (var w in _widgets)
                        if (_widgetPluginMap.TryGetValue(w.Id, out var p) && string.Equals(p, pid, StringComparison.OrdinalIgnoreCase))
                            ordered[n++] = w;
                }

                // 追加未登记顺序的组件（例如直接 RegisterWidget(IWidget) 注册的测试组件）；
                // 被隐藏插件的组件在这里同样要挡住，否则会从兜底路径漏回岛上。
                foreach (var w in _widgets)
                {
                    if (_widgetPluginMap.TryGetValue(w.Id, out var p)
                        && (ContainsIgnoreCase(_pluginOrder, p) || _hiddenPlugins.Contains(p))) continue;
                    ordered[n++] = w;
                }

                if (n == ordered.Length) return ordered;
                var trimmed = new IWidget[n];
                Array.Copy(ordered, trimmed, n);
                return trimmed;
            }
        }
    }

    private static bool ContainsIgnoreCase(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 注入内容显示顺序（builtin.* 原生模块 + 插件 pluginId）。只影响内容的输出/排布次序，不触发任何加载/卸载。
    /// </summary>
    public void SetPluginOrder(IReadOnlyList<string> order)
    {
        lock (_lock)
        {
            _pluginOrder.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                var id = order[i];
                if (string.IsNullOrEmpty(id) || ContainsIgnoreCase(_pluginOrder, id)) continue;
                _pluginOrder.Add(id);
            }
            _contentOrderArr = _pluginOrder.ToArray(); // 渲染侧读取的零分配快照
            _widgetsVersion++; // 让渲染侧下一帧重建排序快照（仍然零稳态分配）
        }
    }

    public IReadOnlyList<ISecondaryWidget> SecondaryWidgets { get { lock (_lock) return _secondaryWidgets.ToArray(); } }
    public IReadOnlyList<(string PluginId, ISettingsPage Page)> SettingsPages { get { lock (_lock) return _settingsPages.ToArray(); } }
    public IReadOnlyList<(string Id, string DisplayName, string Version)> Plugins { get { lock (_lock) return _plugins.ToArray(); } }

    /// <summary>组件注册表版本号，随任何注册/注销自增（渲染侧快照缓存依据）。</summary>
    public int WidgetsVersion { get { lock (_lock) return _widgetsVersion; } }

    public void RegisterPlugin(string id, string displayName, string version = "")
    {
        lock (_lock)
        {
            var idx = _plugins.FindIndex(p => p.Id == id);
            if (idx >= 0) _plugins[idx] = (id, displayName, version);
            else _plugins.Add((id, displayName, version));
        }
    }

    public void RegisterWidget(IWidget widget)
    {
        lock (_lock) { _widgets.Add(widget); _widgetsVersion++; }
    }

    public void RegisterWidget(string pluginId, IWidget widget)
    {
        lock (_lock)
        {
            _widgets.Add(widget);
            _widgetPluginMap[widget.Id] = pluginId;
            _widgetsVersion++;
        }

        // 「组件被启用」通知（锁外调用）：插件可能在里面读回持久化设置、或申请重测宽度，
        // 别持着宿主锁进插件代码。
        //
        // 这是 2026-10-05 补上的接线 —— 在此之前 IWidget.OnActivate / OnDeactivate 只有接口声明，
        // 全程序零调用点（文档却写着「启用和停止时各调用一次」），
        // 于是 nps-media-mixer 只能改成「构造时注入 host」，rayburst 干脆在注释里记下了这个坑。
        // 补它是安全的：老插件早就实现了这两个方法（以前只是没人调），空实现的就是什么都不做。
        try { widget.OnActivate(CreateScopedHost(pluginId)); }
        catch (Exception ex) { Logger.Error($"[PluginHost] 组件 OnActivate 异常: {widget.Id}", ex); }
    }

    public string? GetWidgetPluginId(string widgetId)
    {
        lock (_lock) return _widgetPluginMap.TryGetValue(widgetId, out var p) ? p : null;
    }

    public void RegisterSecondaryWidget(ISecondaryWidget widget)
    {
        lock (_lock) { _secondaryWidgets.Add(widget); _widgetsVersion++; }
    }

    public void RegisterSecondaryWidget(string pluginId, ISecondaryWidget widget)
    {
        lock (_lock)
        {
            _secondaryWidgets.Add(widget);
            _secondaryWidgetPluginMap[widget.Id] = pluginId;
            _widgetsVersion++;
        }
    }

    public void RegisterSettingsPage(string pluginId, ISettingsPage page)
    {
        lock (_lock) _settingsPages.Add((pluginId, page));
    }

    /// <summary>
    /// 请求宿主重新测量插件组件宽度：把注册表版本号自增一次，
    /// 渲染侧下一帧的 RefreshPluginWidgets 就会重新调用各组件的 MeasureWidth 并重建快照，
    /// NotchWindow 随之把岛体宽度平滑过渡到新值（宽度变化走既有弹簧动画）。
    ///
    /// 供「宽度随内容变化」的插件使用（内容变化时调一次即可，别每帧调用）。
    /// </summary>
    public void InvalidateWidgetLayout()
    {
        lock (_lock) _widgetsVersion++;
    }

    /// <summary>
    /// 本帧插件行的总可用宽度（含与原生内容之间的 16px 间距）。转发到渲染侧的预算字段——
    /// 它由 NotchWindow 每帧按「岛体总长上限 − 原生内容本帧占用宽度」写入，
    /// 组合模式则由 GetCompositeWidth 内部按同一规则写入。
    ///
    /// 注意这是整行预算；某个插件实际能用多少还取决于它排在第几位，
    /// 插件应通过 ScopedPluginHost 拿 GetPluginRowBudgetFor 的结果。
    /// </summary>
    public float GetPluginRowBudget() => Renderer.GetPluginRowBudget();

    /// <summary>
    /// 某个插件处的剩余可用宽度：「不显示这个插件时，它所在位置还剩多少长度」。
    /// 按组件从左到右的放行优先级，扣掉排在它前面的插件已占的宽度（含间距）。
    /// </summary>
    public float GetPluginRowBudgetFor(string pluginId) => Renderer.GetPluginRowRemaining(pluginId);

    /// <summary>为某个插件创建绑定其 Id 的宿主视图（设置持久化自动加前缀）。</summary>
    public IPluginHost CreateScopedHost(string pluginId) => new ScopedPluginHost(this, pluginId);

    public RenderTheme CurrentTheme => Renderer.GetCurrentTheme();

    // ---- 刷新调度 ----
    public IDisposable ScheduleRefresh(string pluginId, TimeSpan interval, Action callback)
    {
        var handle = new RefreshHandle(interval, callback);

        lock (_lock)
        {
            // 卸载中的插件一律拒绝登记：它的 Dispose 里 / 正在飞的刷新回调里再调本方法时，
            // _refreshes 里的条目已经或即将被摘掉，收下它就等于留一个永远没人 Dispose 的定时器
            //（定时器 → 回调委托 → 插件类型 → ALC 永不回收）。见 _unregistering 的说明。
            if (_unregistering.Contains(pluginId))
            {
                handle.Dispose();
                Logger.Debug($"[PluginHost] 插件 {pluginId} 正在卸载，已忽略其新的刷新登记");
                return handle;
            }

            if (!_refreshes.TryGetValue(pluginId, out var list))
                _refreshes[pluginId] = list = new List<IDisposable>();
            list.Add(handle);
        }
        return handle;
    }

    /// <summary>
    /// 标记 / 解除「某个插件正在卸载」。卸载期间新的刷新登记与窗口登记一律被拒绝。
    /// 由 PluginManager 的卸载流程包住「插件 Dispose + 宿主注销」这一整段。
    /// </summary>
    internal void SetUnregistering(string pluginId, bool value)
    {
        if (string.IsNullOrEmpty(pluginId)) return;
        lock (_lock)
        {
            if (value) _unregistering.Add(pluginId);
            else _unregistering.Remove(pluginId);
        }
    }

    // ---- 提醒（接现有 Toast 流） ----
    public event Action<ToastData>? ReminderPosted;

    public void PostReminder(ReminderData reminder)
    {
        // 展示时长（可选）：夹到 1~60 秒，非正数退回默认 4 秒 ——
        // 免得插件传 0 / 负数把通知变成一闪而过，或者传个巨大的值把岛体永久占住。
        var duration = reminder.Duration;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(4);
        else if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        else if (duration > TimeSpan.FromSeconds(60)) duration = TimeSpan.FromSeconds(60);

        var toast = new ToastData
        {
            // 来源标签：调用方给了就用它（MSP 接入用它标出对端节点 id / 调用方给的 kind，
            // 否则所有通知都顶着「插件提醒」，来源全糊成一样）。没给则保持原文案，
            // 插件调用方的显示一个字节都不变。同样 DetachString 拷到宿主堆。
            AppName = string.IsNullOrWhiteSpace(reminder.Source) ? "插件提醒" : DetachString(reminder.Source),
            // 复制到宿主堆，避免长期持有插件 loader heap 上的字符串（会锁住可回收 ALC）
            Title = DetachString(reminder.Title),
            Body = DetachString(reminder.Body),
            ProcessName = "PluginReminder",
            NotificationId = (uint)Environment.TickCount,
            Duration = duration,
            // 点击回调（可选）：用户点这条通知时由 NotchWindow 调用（见 WM_LBUTTONDOWN）。
            // 委托引用插件类型，所以它只在「这条通知还挂着」的几秒内被宿主持有；
            // 通知一过就被替换 / 清空，不会长期钉住可回收 ALC。
            OnClick = reminder.OnClick
        };
        // 图标（可选）：本地路径 / 图片链接 / data:image base64 / 内置别名，见 ToastIconProvider。
        // 同样走 DetachString 拷到宿主堆，避免长期持有插件 loader heap 上的字符串。
        string iconSpec = DetachString(reminder.IconPath);

        Logger.Info($"[PluginHost] 插件提醒已投递: {toast.Title} — {toast.Body}");
        ReminderPosted?.Invoke(toast);

        // 解析放后台：先按默认图标弹出来，解析完由 Renderer 下一帧自动换图
        ToastIconProvider.ResolveInBackground(iconSpec, bmp => toast.CustomIcon = bmp);
    }

    /// <summary>
    /// 把插件返回的字符串复制到宿主自己的托管堆。
    ///
    /// 为什么必须这样做：插件方法返回的字符串常量位于「可回收 AssemblyLoadContext 的
    /// loader heap」内，宿主若长期持有该引用（例如存进 PluginEntry / ToastData），
    /// 这个 ALC 就永远无法被 GC 回收，热重载会持续泄漏旧版本代码。
    /// new string(...) 会在当前（宿主）上下文重新分配，从而切断这条引用链。
    /// </summary>
    internal static string DetachString(string? s)
        => string.IsNullOrEmpty(s) ? string.Empty : new string(s.ToCharArray());

    // ---- 设置持久化 ----
    public string GetSetting(string pluginId, string key, string fallback)
    {
        try
        {
            using var reg = Registry.CurrentUser.CreateSubKey(RegistryBase);
            return reg?.GetValue(PrefixedKey(pluginId, key)) as string ?? fallback;
        }
        catch { return fallback; }
    }

    public void SetSetting(string pluginId, string key, string value)
    {
        try
        {
            using var reg = Registry.CurrentUser.CreateSubKey(RegistryBase);
            reg?.SetValue(PrefixedKey(pluginId, key), value);
        }
        catch (Exception ex) { Logger.Error($"保存插件设置失败: {pluginId}.{key}", ex); }

        Action? handler;
        lock (_lock) _settingsHandlers.TryGetValue(pluginId, out handler);
        handler?.Invoke();
    }

    internal void AddSettingsHandler(string pluginId, Action? handler)
    {
        lock (_lock)
        {
            _settingsHandlers.TryGetValue(pluginId, out var cur);
            _settingsHandlers[pluginId] = cur + handler;
        }
    }

    internal void RemoveSettingsHandler(string pluginId, Action? handler)
    {
        lock (_lock)
        {
            _settingsHandlers.TryGetValue(pluginId, out var cur);
            var next = cur - handler;
            if (next == null) _settingsHandlers.Remove(pluginId);
            else _settingsHandlers[pluginId] = next;
        }
    }

    private static string PrefixedKey(string pluginId, string key) => $"Plugin.{pluginId}.{key}";

    // ---- 注销（热卸载 / 热重载的核心） ----
    /// <summary>
    /// 摘除某个插件登记的全部内容：组件、二级组件、设置页、刷新句柄、设置事件订阅。
    /// 调用后该插件留下的对象将不再被宿主引用，可被 GC 回收。
    /// </summary>
    public void UnregisterPlugin(string pluginId)
    {
        List<IDisposable>? refreshes = null;
        List<IPluginWindow>? windows = null;
        List<IWidget>? removedWidgets = null;
        bool detailInvalidated = false;
        lock (_lock)
        {
            // 若被卸载的插件正开着详情页，必须先收起：否则宿主会一直强引用已卸载插件的对象，
            // 可回收 ALC 永远回收不掉（热重载会持续泄漏旧版本代码）。
            if (_activeDetailWidgetId != null)
            {
                bool ownsActive = _widgetPluginMap.TryGetValue(_activeDetailWidgetId, out var ap)
                                  && string.Equals(ap, pluginId, StringComparison.OrdinalIgnoreCase);
                bool widgetGone = !_widgets.Any(w => string.Equals(w.Id, _activeDetailWidgetId, StringComparison.OrdinalIgnoreCase));
                if (ownsActive || widgetGone)
                {
                    _activeDetailWidgetId = null;
                    _activeDetailPage = null;
                    detailInvalidated = true;
                }
            }

            foreach (var w in _widgets.Where(w => _widgetPluginMap.TryGetValue(w.Id, out var p) && p == pluginId).ToArray())
            {
                _widgets.Remove(w);
                _widgetPluginMap.Remove(w.Id);
                (removedWidgets ??= new List<IWidget>(1)).Add(w);   // 留到锁外做「组件被停用」回调
            }
            foreach (var w in _secondaryWidgets.Where(w => _secondaryWidgetPluginMap.TryGetValue(w.Id, out var p) && p == pluginId).ToArray())
            {
                _secondaryWidgets.Remove(w);
                _secondaryWidgetPluginMap.Remove(w.Id);
            }
            _settingsPages.RemoveAll(p => p.PluginId == pluginId);
            _plugins.RemoveAll(p => p.Id == pluginId);
            _settingsHandlers.Remove(pluginId);
            _widgetsVersion++;
            if (_refreshes.TryGetValue(pluginId, out var list))
            {
                refreshes = list;
                _refreshes.Remove(pluginId);
            }
            // 该插件打开的窗口一并收回：窗口的绘制/输入委托引用插件类型，
            // 不关掉的话下面 PluginManager 的 ctx.Unload() + GC 永远回收不到这个 ALC。
            //
            // 这里不再 Remove(pluginId)：以前是先把记账整表摘掉、再尝试关窗，一旦这次关不掉
            //（拖放进行中就是这样，见 _pendingWindowClose 的说明），宿主就永久失去了它，
            // 窗口连同 HWND / DIB / 内存 DC / 插件程序集一起留在 PluginWindow 的静态路由表里没人管。
            // 现在记账保留到窗口真的销毁（WM_DESTROY 里会回调 DetachWindow 摘掉），关不掉的进重试队列。
            if (_windows.TryGetValue(pluginId, out var winList))
                windows = winList;
        }

        // 「组件被停用」通知 —— 必须在切断渲染侧快照之后才回调。
        //
        // 顺序不能反：插件在 OnDeactivate 里释放自己的画笔是常规做法（rayburst 就是把
        // _barBackgroundPaint / _iconPaint / _textPaints 全 Dispose 掉的那一个），
        // 而 SKPaint 一旦在「宿主还会再画一帧」的窗口里被释放，渲染线程拿到的就是悬垂 native 指针
        // —— 直接 0xC0000005，不是能 catch 的异常。
        // 这里先调 InvalidatePluginSnapshot 把 _pluginWidgets / _pluginDetailPage / 命中区全部置空，
        // 渲染线程此后一帧都画不到这些组件，回调怎么释放都安全。
        //
        // 清的是全量快照（不是只清本插件）：Unload 路径本来紧接着就要调它（见 PluginManager.Unload），
        // 这里只是提前到回调之前，总次数没变；代价是下一帧渲染线程重建一次快照（一次宽度重测）。
        if (removedWidgets != null)
        {
            Renderer.InvalidatePluginSnapshot();

            foreach (var w in removedWidgets)
            {
                try { w.OnDeactivate(); }
                catch (Exception ex) { Logger.Error($"[PluginHost] 组件 OnDeactivate 异常: {w.Id}", ex); }
            }
        }

        // 定时器在锁外释放，避免 Dispose 回调再次进入宿主造成死锁
        if (refreshes != null)
            foreach (var r in refreshes)
                try { r.Dispose(); } catch { }

        // 窗口同样在锁外处理（销毁过程会回调 DetachWindow，再进宿主锁）
        if (windows != null)
        {
            foreach (var w in windows.ToArray())
            {
                try
                {
                    // 第一步永远是切断插件委托，与能不能关掉无关：这些委托是插件实例方法 →
                    // 插件类型 → Assembly → ALC，是「旧程序集回收不掉」的主链。
                    // 窗口还被 PluginWindow 的静态路由表强引用，所以哪怕窗口多活一会儿，
                    // 也必须先让它不再引用插件，否则 ctx.Unload() 之后的同步 GC 一定判失败。
                    if (w is PluginWindow pw0) pw0.DetachPluginCallbacks();

                    // 优先同步销毁：卸载路径紧接着就要做同步 GC，PostMessage 那种异步关窗赶不上。
                    // 销毁成功时 WM_DESTROY 会回调 DetachWindow 把记账摘掉，这里不用管。
                    if (w is PluginWindow pw && pw.TryDestroyNow()) continue;

                    // 关不掉（典型：该窗口正在拖放循环里）→ 交给 Close() + 逐帧重试，不要再"算了"
                    w.Close();
                    ScheduleWindowCloseRetry(w);
                }
                catch { /* 单个窗口关不掉不影响其余资源回收，重试队列会继续尝试 */ }
            }
        }

        if (detailInvalidated) DetailPageChanged?.Invoke();
    }

    /// <summary>
    /// 把「这次没关掉的窗口」挂进重试队列（去重）。之后由 DrainPendingWindowClose
    /// 每帧重试，直到窗口真的销毁（DetachWindow 会顺手把它从队列里摘掉）。
    /// </summary>
    private void ScheduleWindowCloseRetry(IPluginWindow window)
    {
        lock (_lock)
        {
            // 已经销毁了（WM_DESTROY 可能就在 Close() 之后立刻到达）就不用排队
            if (window is PluginWindow pw && pw.IsDestroyed) return;
            if (!_pendingWindowClose.Contains(window)) _pendingWindowClose.Add(window);
        }
    }

    /// <summary>
    /// 重试关闭卸载时没关掉的窗口。由渲染循环每帧调用一次（与 TickPanelCollapse 同一处），
    /// 没有待办时只做一次 Count == 0 判断，稳态零开销。
    ///
    /// 拖放结束后 PluginWindow 自己也会补做那次 Close（见 StartDragFiles 的 finally），
    /// 所以正常情况下这个队列一两帧内就空了；这里的重试是为了兜住「拖放循环卡死 / 用户一直不松手」
    /// 那类窗口，保证它不会因为没人再管而永久占着 HWND、DIB 和插件程序集。
    /// </summary>
    internal void DrainPendingWindowClose()
    {
        if (_pendingWindowClose.Count == 0) return;

        IPluginWindow[] batch;
        lock (_lock) batch = _pendingWindowClose.ToArray();

        foreach (var w in batch)
        {
            _windowCloseAttempts++;
            try
            {
                if (w is PluginWindow pw && pw.IsDestroyed)
                {
                    lock (_lock) _pendingWindowClose.Remove(w);
                    continue;
                }
                if (w is PluginWindow pw2 && pw2.TryDestroyNow())
                {
                    lock (_lock) _pendingWindowClose.Remove(w);
                    continue;
                }
                w.Close();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[PluginHost] 重试关闭插件窗口失败：{ex.GetType().Name}");
            }
        }

        // 兜底：极长时间（约 10 分钟 @60FPS）都关不掉的窗口说明那次拖放循环已经卡死，
        // 这时必须连宿主记账一起摘掉 —— 否则 _windows 会攒下永远不销毁的条目（列表又变成新的常驻）。
        // 窗口的插件回调在第一次尝试时就已经切断，所以丢掉它不会让程序集回收失败。
        if (_windowCloseAttempts >= 36000 && _pendingWindowClose.Count > 0)
        {
            IPluginWindow[] giveUp;
            lock (_lock)
            {
                giveUp = _pendingWindowClose.ToArray();
                _pendingWindowClose.Clear();
            }
            _windowCloseAttempts = 0;

            foreach (var w in giveUp)
            {
                try { w.Close(); } catch { }
                ForgetWindowAccounting(w);
            }
            Logger.Warn($"[PluginHost] {giveUp.Length} 个插件窗口长时间无法销毁，已停止重试并注销记账（它们的插件回调早已切断）");
        }
    }

    /// <summary>
    /// 摘掉某个窗口在宿主这里的全部记账（_windows 归属表 + 待关队列）。
    /// 与 DetachWindow 的区别：那个由窗口自己回调（真销毁时），这个给"放弃重试"用。
    /// </summary>
    private void ForgetWindowAccounting(IPluginWindow window)
    {
        lock (_lock)
        {
            _pendingWindowClose.Remove(window);
            foreach (var kv in _windows)
            {
                if (!kv.Value.Remove(window)) continue;
                if (kv.Value.Count == 0) _windows.Remove(kv.Key);
                return;
            }
        }
    }

    // ---- 交互调度：详情页（右键展开） ----
    // 数据流：右键命中组件 → NotchWindow 调 OpenDetailPage(widgetId) → 宿主取组件 DetailPage 缓存起来
    //        → 渲染侧每帧读 ActiveDetailPage，有值就把岛体整块换成详情页（尺寸由详情页 Measure* 决定）
    //        → 岛内左键经 DispatchDetailPageClick 交给详情页 HitTest/OnAction
    //        → 再右键 / 点击岛外 / CloseDetailPage() 收起。

    /// <summary>当前展开的详情页实例；null = 未展开。渲染侧据此把岛体内容整块换成详情页。</summary>
    public IDetailPage? ActiveDetailPage { get { lock (_lock) return _activeDetailPage; } }

    /// <summary>当前展开的详情页所属组件 Id；null = 未展开。</summary>
    public string? ActiveDetailWidgetId { get { lock (_lock) return _activeDetailWidgetId; } }

    /// <summary>是否正处于详情页展开状态。</summary>
    public bool HasActiveDetailPage { get { lock (_lock) return _activeDetailPage != null; } }

    /// <summary>
    /// 展开指定组件的详情页（右键组件的默认行为，插件也可通过 IPluginHost.OpenDetailPage 主动调用）。
    /// 组件不存在、组件 DetailPage 为 null、或取用过程抛异常时返回 false（宿主不展开，右键继续走原逻辑）。
    /// 详情页实例在这里取一次并缓存，避免插件每帧 new 一个新对象。
    /// </summary>
    public bool OpenDetailPage(string widgetId)
    {
        if (string.IsNullOrEmpty(widgetId)) return false;

        IWidget? target;
        lock (_lock)
            target = _widgets.FirstOrDefault(w => string.Equals(w.Id, widgetId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return false;

        IDetailPage? page;
        try { page = target.DetailPage; }
        catch (Exception ex)
        {
            Logger.Error($"[PluginHost] 读取组件详情页失败: {widgetId}", ex);
            return false;
        }
        if (page == null) return false;

        lock (_lock)
        {
            if (ReferenceEquals(_activeDetailPage, page)
                && string.Equals(_activeDetailWidgetId, widgetId, StringComparison.OrdinalIgnoreCase)) return true;
            _activeDetailWidgetId = widgetId;
            _activeDetailPage = page;
        }
        Logger.Info($"[PluginHost] 已展开插件详情页: {widgetId}");
        DetailPageChanged?.Invoke();
        return true;
    }

    /// <summary>收起当前详情页（未展开时为空操作）。</summary>
    public void CloseDetailPage()
    {
        bool had;
        lock (_lock)
        {
            had = _activeDetailPage != null;
            _activeDetailWidgetId = null;
            _activeDetailPage = null;
        }
        if (!had) return;
        Logger.Info("[PluginHost] 已收起插件详情页");
        DetailPageChanged?.Invoke();
    }

    /// <summary>
    /// 右键命中组件时的默认展开：该组件提供详情页则展开（已展开则收起），返回 true 表示右键已被消费。
    /// 返回 false 时调用方继续走原右键逻辑（打开设置窗口）。
    /// </summary>
    public bool ToggleDetailPage(string widgetId)
    {
        if (string.IsNullOrEmpty(widgetId)) return false;

        bool closed = false;
        lock (_lock)
        {
            // 已展开的正是它 → 收起
            if (_activeDetailPage != null
                && string.Equals(_activeDetailWidgetId, widgetId, StringComparison.OrdinalIgnoreCase))
            {
                _activeDetailWidgetId = null;
                _activeDetailPage = null;
                closed = true;
            }
        }

        if (closed)
        {
            Logger.Info($"[PluginHost] 已收起插件详情页: {widgetId}");
            DetailPageChanged?.Invoke();
            return true;
        }
        return OpenDetailPage(widgetId);
    }

    // ---- 窗口 ----
    /// <summary>
    /// 创建插件自有窗口。必须带上 ：宿主按插件记账，
    /// 卸载时统一关闭 —— 否则窗口会一直强引用插件类型，让可回收 ALC 回收失败。
    /// </summary>
    public IPluginWindow CreateWindow(string pluginId, string title, int width, int height)
    {
        var win = new PluginWindow(this, pluginId, title, width, height);
        win.Show();   // Show() 内部会调 AttachWindow 完成登记
        return win;
    }

    /// <summary>
    /// 在灵动岛本体上发起一次系统拖放（详情页「拖出」用）。
    ///
    /// 详情页画在岛体上、没有自己的窗口，所以「把条目拖出去」这件事只能由宿主代为发起。
    /// 会阻塞到用户松手或取消，语义详见 IPluginHost.StartFileDrag。
    /// </summary>
    public bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false)
        => NotchWindow.StartFileDragOnIsland(paths, allowMove);

    /// <summary>窗口创建成功后由 PluginWindow 回调登记。</summary>
    internal void AttachWindow(string pluginId, IPluginWindow window)
    {
        lock (_lock)
        {
            // 卸载中的插件不再收新窗口：收下来就又是一个"卸载之后没人管"的窗口
            //（它的回调链指向即将被卸载的程序集）。这里只登记到待关队列、交给逐帧重试，
            // 绝不能同步销毁 —— 本方法可能正处于 PluginWindow.Show() 建窗流程中间。
            if (_unregistering.Contains(pluginId))
            {
                Logger.Debug($"[PluginHost] 插件 {pluginId} 正在卸载，已拒绝其新窗口的登记");
                if (!_pendingWindowClose.Contains(window)) _pendingWindowClose.Add(window);
                return;
            }

            if (!_windows.TryGetValue(pluginId, out var list))
                _windows[pluginId] = list = new List<IPluginWindow>(1);
            if (!list.Contains(window)) list.Add(window);
        }
    }

    /// <summary>窗口销毁时由 PluginWindow 回调注销（含用户手动关窗），避免记账表无限增长。</summary>
    internal void DetachWindow(string pluginId, IPluginWindow window)
    {
        lock (_lock)
        {
            // 只有真的销毁了才允许摘记账 —— 这正是"关不掉的窗口不再被遗忘"的关键：
            // WM_CLOSE 被推迟（拖放中）时不会走这里，条目因此留着，由重试队列继续关。
            if (_pendingWindowClose.Contains(window)) _pendingWindowClose.Remove(window);

            if (!_windows.TryGetValue(pluginId, out var list)) return;
            list.Remove(window);
            if (list.Count == 0) _windows.Remove(pluginId);
        }
    }

    /// <summary>周期刷新句柄：后台定时器触发回调，Dispose 即停止。</summary>
    private sealed class RefreshHandle : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private readonly Action _callback;
        private int _disposed;
        private int _running;

        public RefreshHandle(TimeSpan interval, Action callback)
        {
            _callback = callback;
            _timer = new System.Timers.Timer(Math.Max(interval.TotalMilliseconds, 100.0));
            _timer.Elapsed += OnElapsed;
            _timer.AutoReset = true;
            _timer.Start();
        }

        private void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // 防重入：上次回调未跑完则跳过本次
            if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1) return;
            try { _callback(); }
            catch (Exception ex) { Logger.Error("插件刷新回调异常", ex); }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _timer.Stop();
            _timer.Elapsed -= OnElapsed;
            _timer.Dispose();
        }
    }
}

/// <summary>绑定插件 Id 的宿主视图，自动为设置 key 加 "Plugin.[id]." 前缀。</summary>
public sealed class ScopedPluginHost : IPluginHost
{
    private readonly PluginHost _host;
    private readonly string _pluginId;

    internal ScopedPluginHost(PluginHost host, string pluginId)
    {
        _host = host;
        _pluginId = pluginId;
    }

    public void RegisterWidget(IWidget widget) => _host.RegisterWidget(_pluginId, widget);
    public void RegisterSecondaryWidget(ISecondaryWidget widget) => _host.RegisterSecondaryWidget(_pluginId, widget);
    public void RegisterSettingsPage(ISettingsPage page) => _host.RegisterSettingsPage(_pluginId, page);

    public RenderTheme CurrentTheme => _host.CurrentTheme;
    public void PostReminder(ReminderData reminder) => _host.PostReminder(reminder);
    public string GetSetting(string key, string fallback) => _host.GetSetting(_pluginId, key, fallback);
    public void SetSetting(string key, string value) => _host.SetSetting(_pluginId, key, value);
    public event Action? SettingsChanged
    {
        add => _host.AddSettingsHandler(_pluginId, value);
        remove => _host.RemoveSettingsHandler(_pluginId, value);
    }
    public IDisposable ScheduleRefresh(TimeSpan interval, Action callback) => _host.ScheduleRefresh(_pluginId, interval, callback);
    public void RequestRedraw() { /* 常驻 60FPS 渲染下为空操作，事件驱动化预留 */ }
    public void InvalidateWidgetLayout() => _host.InvalidateWidgetLayout();
    /// <summary>本插件所在位置的剩余可用宽度（已扣掉排在它前面的插件占用）——见 PluginHost.GetPluginRowBudgetFor。</summary>
    public float GetPluginRowBudget() => _host.GetPluginRowBudgetFor(_pluginId);
    public void OpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    /// <summary>与 OpenDetailPage 同一动作，但把宿主内部的成功 / 失败结果带回给插件。</summary>
    public bool TryOpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    public void CloseDetailPage() => _host.CloseDetailPage();
    public bool ToggleDetailPage(string widgetId) => _host.ToggleDetailPage(widgetId);
    public IPluginWindow CreateWindow(string title, int width, int height) => _host.CreateWindow(_pluginId, title, width, height);
    public bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false) => _host.StartFileDrag(paths, allowMove);
}
