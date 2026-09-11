using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 插件宿主：注册中心 + 服务入口。
/// 由 NotchWindow 持有单例。外部插件通过 <see cref="CreateScopedHost"/> 获得绑定自身 Id 的视图。
/// </summary>
public sealed class PluginHost
{
    private const string RegistryBase = @"SOFTWARE\NotchPeninsula";

    private readonly List<IWidget> _widgets = new();
    private readonly List<ISecondaryWidget> _secondaryWidgets = new();
    private readonly List<(string PluginId, ISettingsPage Page)> _settingsPages = new();

    public IReadOnlyList<IWidget> Widgets => _widgets;
    public IReadOnlyList<ISecondaryWidget> SecondaryWidgets => _secondaryWidgets;
    public IReadOnlyList<(string PluginId, ISettingsPage Page)> SettingsPages => _settingsPages;

    public void RegisterWidget(IWidget widget) => _widgets.Add(widget);
    public void RegisterSecondaryWidget(ISecondaryWidget widget) => _secondaryWidgets.Add(widget);
    public void RegisterSettingsPage(string pluginId, ISettingsPage page) => _settingsPages.Add((pluginId, page));

    /// <summary>为某个插件创建绑定其 Id 的宿主视图（设置持久化自动加前缀）。</summary>
    public IPluginHost CreateScopedHost(string pluginId) => new ScopedPluginHost(this, pluginId);

    public RenderTheme CurrentTheme => Renderer.GetCurrentTheme();

    // ---- 刷新调度 ----
    public IDisposable ScheduleRefresh(TimeSpan interval, Action callback)
        => new RefreshHandle(interval, callback);

    // ---- 提醒（Phase 4 接线到现有 Toast 流） ----
    public void PostReminder(ReminderData reminder)
        => Logger.Info($"[PluginHost] 提醒(占位): {reminder.Title} — {reminder.Body}");

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
    }

    private static string PrefixedKey(string pluginId, string key) => $"Plugin.{pluginId}.{key}";

    // ---- 交互调度（Phase 2 接线） ----
    public void OpenDetailPage(string widgetId) => Logger.Info($"[PluginHost] 打开详情(占位): {widgetId}");
    public void CloseDetailPage() { }

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

    public void RegisterWidget(IWidget widget) => _host.RegisterWidget(widget);
    public void RegisterSecondaryWidget(ISecondaryWidget widget) => _host.RegisterSecondaryWidget(widget);
    public void RegisterSettingsPage(ISettingsPage page) => _host.RegisterSettingsPage(_pluginId, page);

    public RenderTheme CurrentTheme => _host.CurrentTheme;
    public void PostReminder(ReminderData reminder) => _host.PostReminder(reminder);
    public string GetSetting(string key, string fallback) => _host.GetSetting(_pluginId, key, fallback);
    public void SetSetting(string key, string value) => _host.SetSetting(_pluginId, key, value);
    public IDisposable ScheduleRefresh(TimeSpan interval, Action callback) => _host.ScheduleRefresh(interval, callback);
    public void RequestRedraw() { /* 常驻 60FPS 渲染下为空操作，事件驱动化预留 */ }
    public void OpenDetailPage(string widgetId) => _host.OpenDetailPage(widgetId);
    public void CloseDetailPage() => _host.CloseDetailPage();
}
