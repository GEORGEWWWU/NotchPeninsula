using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

public static class BuiltinWidgets
{
    public const string Clock = "builtin.clock";        // 时间日期
    public const string Hardware = "builtin.hardware";  // CPU / RAM 占用
    public const string Media = "builtin.media";        // 媒体控制器

    public static readonly string[] Default = { Clock, Hardware, Media };

    public static bool IsBuiltin(string id)
        => string.Equals(id, Clock, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Hardware, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Media, StringComparison.OrdinalIgnoreCase);
}

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

    private readonly Dictionary<string, List<IPluginWindow>> _windows = new();

    private readonly HashSet<string> _unregistering = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IPluginWindow> _pendingWindowClose = new();

    private int _windowCloseAttempts;

    private int _widgetsVersion;

    private string? _activeDetailWidgetId;
    private IDetailPage? _activeDetailPage;

    public event Action? DetailPageChanged;

    private readonly List<string> _pluginOrder = new();
    private string[] _contentOrderArr = Array.Empty<string>();

    private readonly HashSet<string> _hiddenPlugins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ContentOrder { get { lock (_lock) return _contentOrderArr; } }

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

    public bool TryGetWidgetPlugin(string widgetId, out string pluginId)
    {
        lock (_lock) return _widgetPluginMap.TryGetValue(widgetId, out pluginId!);
    }

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

        // 别持着宿主锁进插件代码。
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

    public void InvalidateWidgetLayout()
    {
        lock (_lock) _widgetsVersion++;
    }

    public float GetPluginRowBudget() => Renderer.GetPluginRowBudget();

    public float GetPluginRowBudgetFor(string pluginId) => Renderer.GetPluginRowRemaining(pluginId);

    public IPluginHost CreateScopedHost(string pluginId) => new ScopedPluginHost(this, pluginId);

    public RenderTheme CurrentTheme => Renderer.GetCurrentTheme();

    // ---- 刷新调度 ----
    public IDisposable ScheduleRefresh(string pluginId, TimeSpan interval, Action callback)
    {
        var handle = new RefreshHandle(interval, callback);

        lock (_lock)
        {
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
        var duration = reminder.Duration;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(4);
        else if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        else if (duration > TimeSpan.FromSeconds(60)) duration = TimeSpan.FromSeconds(60);

        var toast = new ToastData
        {
            AppName = "插件提醒",
            Title = DetachString(reminder.Title),
            Body = DetachString(reminder.Body),
            ProcessName = "PluginReminder",
            NotificationId = (uint)Environment.TickCount,
            Duration = duration,
            OnClick = reminder.OnClick
        };
        string iconSpec = DetachString(reminder.IconPath);

        Logger.Info($"[PluginHost] 插件提醒已投递: {toast.Title} — {toast.Body}");
        ReminderPosted?.Invoke(toast);

        ToastIconProvider.ResolveInBackground(iconSpec, bmp => toast.CustomIcon = bmp);
    }

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
    public void UnregisterPlugin(string pluginId)
    {
        List<IDisposable>? refreshes = null;
        List<IPluginWindow>? windows = null;
        List<IWidget>? removedWidgets = null;
        bool detailInvalidated = false;
        lock (_lock)
        {
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
            if (_windows.TryGetValue(pluginId, out var winList))
                windows = winList;
        }

        // 渲染线程此后一帧都画不到这些组件，回调怎么释放都安全。
        if (removedWidgets != null)
        {
            Renderer.InvalidatePluginSnapshot();

            foreach (var w in removedWidgets)
            {
                try { w.OnDeactivate(); }
                catch (Exception ex) { Logger.Error($"[PluginHost] 组件 OnDeactivate 异常: {w.Id}", ex); }
            }
        }

        if (refreshes != null)
            foreach (var r in refreshes)
                try { r.Dispose(); } catch { }

        if (windows != null)
        {
            foreach (var w in windows.ToArray())
            {
                try
                {
                    if (w is PluginWindow pw0) pw0.DetachPluginCallbacks();

                    if (w is PluginWindow pw && pw.TryDestroyNow()) continue;

                    w.Close();
                    ScheduleWindowCloseRetry(w);
                }
                catch { /* 单个窗口关不掉不影响其余资源回收，重试队列会继续尝试 */ }
            }
        }

        if (detailInvalidated) DetailPageChanged?.Invoke();
    }

    private void ScheduleWindowCloseRetry(IPluginWindow window)
    {
        lock (_lock)
        {
            if (window is PluginWindow pw && pw.IsDestroyed) return;
            if (!_pendingWindowClose.Contains(window)) _pendingWindowClose.Add(window);
        }
    }

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

    public IDetailPage? ActiveDetailPage { get { lock (_lock) return _activeDetailPage; } }

    public string? ActiveDetailWidgetId { get { lock (_lock) return _activeDetailWidgetId; } }

    public bool HasActiveDetailPage { get { lock (_lock) return _activeDetailPage != null; } }

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
    public IPluginWindow CreateWindow(string pluginId, string title, int width, int height)
    {
        var win = new PluginWindow(this, pluginId, title, width, height);
        win.Show();   // Show() 内部会调 AttachWindow 完成登记
        return win;
    }

    public bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false)
        => NotchWindow.StartFileDragOnIsland(paths, allowMove);

    internal void AttachWindow(string pluginId, IPluginWindow window)
    {
        lock (_lock)
        {
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

    internal void DetachWindow(string pluginId, IPluginWindow window)
    {
        lock (_lock)
        {
            if (_pendingWindowClose.Contains(window)) _pendingWindowClose.Remove(window);

            if (!_windows.TryGetValue(pluginId, out var list)) return;
            list.Remove(window);
            if (list.Count == 0) _windows.Remove(pluginId);
        }
    }

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
    public float GetPluginRowBudget() => _host.GetPluginRowBudgetFor(_pluginId);
    public void OpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    public bool TryOpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    public void CloseDetailPage() => _host.CloseDetailPage();
    public bool ToggleDetailPage(string widgetId) => _host.ToggleDetailPage(widgetId);
    public IPluginWindow CreateWindow(string title, int width, int height) => _host.CreateWindow(_pluginId, title, width, height);
    public bool StartFileDrag(IReadOnlyList<string> paths, bool allowMove = false) => _host.StartFileDrag(paths, allowMove);
}
