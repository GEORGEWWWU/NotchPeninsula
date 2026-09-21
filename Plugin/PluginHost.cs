using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 原生（内置）内容模块的伪 Id，与插件组件共处同一张「内容显示顺序表」。
/// 有了它们，用户就能在「插件中心」用 ← / → 把插件挪到时间日期 / 硬件占用 / 媒体控制器之间或之前。
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
/// 由 NotchWindow 持有单例。外部插件通过 <see cref="CreateScopedHost"/> 获得绑定自身 Id 的视图。
///
/// 热加载关键点：所有注册物（组件/设置页/刷新句柄/设置事件）都按 PluginId 归组登记，
/// <see cref="UnregisterPlugin"/> 能把某个插件留下的引用全部摘掉，这样承载它的
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

    /// <summary>
    /// 内容显示顺序（含 builtin.* 原生模块与插件 pluginId）。渲染侧据此把原生模块与插件组件混排。
    /// 返回内部快照数组，读取零分配。
    /// </summary>
    public IReadOnlyList<string> ContentOrder { get { lock (_lock) return _contentOrderArr; } }

    /// <summary>查询某个组件属于哪个插件（渲染侧按插件分组绘制用）。</summary>
    public bool TryGetWidgetPlugin(string widgetId, out string pluginId)
    {
        lock (_lock) return _widgetPluginMap.TryGetValue(widgetId, out pluginId!);
    }

    /// <summary>
    /// 主显示区组件（已按插件显示顺序排列）。
    /// 顺序由 <see cref="SetPluginOrder"/> 注入；未登记顺序的组件保持注册顺序追加在末尾。
    /// </summary>
    public IReadOnlyList<IWidget> Widgets
    {
        get
        {
            lock (_lock)
            {
                if (_widgets.Count == 0) return Array.Empty<IWidget>();
                if (_pluginOrder.Count == 0) return _widgets.ToArray();

                // 按插件顺序输出，同一插件内部保持其注册顺序
                var ordered = new IWidget[_widgets.Count];
                int n = 0;
                foreach (var pid in _pluginOrder)
                    foreach (var w in _widgets)
                        if (_widgetPluginMap.TryGetValue(w.Id, out var p) && string.Equals(p, pid, StringComparison.OrdinalIgnoreCase))
                            ordered[n++] = w;

                // 追加未登记顺序的组件（例如直接 RegisterWidget(IWidget) 注册的测试组件）
                foreach (var w in _widgets)
                {
                    if (_widgetPluginMap.TryGetValue(w.Id, out var p) && ContainsIgnoreCase(_pluginOrder, p)) continue;
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
    /// 渲染侧下一帧的 <c>RefreshPluginWidgets</c> 就会重新调用各组件的 MeasureWidth 并重建快照，
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
    /// 组合模式则由 <c>GetCompositeWidth</c> 内部按同一规则写入。
    ///
    /// 注意这是**整行**预算；某个插件实际能用多少还取决于它排在第几位，
    /// 插件应通过 <see cref="ScopedPluginHost"/> 拿 <see cref="GetPluginRowBudgetFor"/> 的结果。
    /// </summary>
    public float GetPluginRowBudget() => Renderer.GetPluginRowBudget();

    /// <summary>
    /// 某个插件处的**剩余**可用宽度：「不显示这个插件时，它所在位置还剩多少长度」。
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
            if (!_refreshes.TryGetValue(pluginId, out var list))
                _refreshes[pluginId] = list = new List<IDisposable>();
            list.Add(handle);
        }
        return handle;
    }

    // ---- 提醒（接现有 Toast 流） ----
    public event Action<ToastData>? ReminderPosted;

    public void PostReminder(ReminderData reminder)
    {
        var toast = new ToastData
        {
            AppName = "插件提醒",
            // 复制到宿主堆，避免长期持有插件 loader heap 上的字符串（会锁住可回收 ALC）
            Title = DetachString(reminder.Title),
            Body = DetachString(reminder.Body),
            ProcessName = "PluginReminder",
            NotificationId = (uint)Environment.TickCount
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
        }

        // 定时器在锁外释放，避免 Dispose 回调再次进入宿主造成死锁
        if (refreshes != null)
            foreach (var r in refreshes)
                try { r.Dispose(); } catch { }

        if (detailInvalidated) DetailPageChanged?.Invoke();
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
    public IPluginWindow CreateWindow(string title, int width, int height)
    {
        var win = new PluginWindow(title, width, height);
        win.Show();
        return win;
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

/// <summary>绑定插件 Id 的宿主视图，自动为设置 key 加 "Plugin.&lt;id&gt;." 前缀。</summary>
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
    /// <summary>本插件所在位置的**剩余**可用宽度（已扣掉排在它前面的插件占用）——见 PluginHost.GetPluginRowBudgetFor。</summary>
    public float GetPluginRowBudget() => _host.GetPluginRowBudgetFor(_pluginId);
    public void OpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    public void CloseDetailPage() => _host.CloseDetailPage();
    public IPluginWindow CreateWindow(string title, int width, int height) => _host.CreateWindow(title, width, height);
}
