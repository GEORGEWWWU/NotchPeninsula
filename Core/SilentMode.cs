using NotchPeninsula.Plugins;

namespace NotchPeninsula;

/// <summary>岛的三种显示模式（持久化在 <c>DisplayMode</c> 里）。</summary>
public enum IslandDisplayMode
{
    /// <summary>待机模式：岛体收成胶囊，只显示「待机显示内容」选中的那一项。</summary>
    Standby = 0,

    /// <summary>普通模式：按「显示内容」列表常驻显示。</summary>
    Regular = 1,

    /// <summary>静默模式：平时完全退场，只有插件上报「重要信息」时才出现。</summary>
    Silent = 2
}

/// <summary>
/// 静默模式的重要信息登记表。
///
/// <para><b>谁是重要信息</b>：插件调 <see cref="IPluginHost.SetImportantInformation"/> 主动上报
/// （比如「正在下载」），另外声明了 <see cref="ILiveWindow.CountsAsImportantInfo"/> 的实况窗也算 ——
/// 静默模式下气泡要在屏上，岛就得回来陪它（气泡挂在岛左侧，岛退场它也会跟着出画面）。</para>
///
/// <para><b>勾选 ≠ 一定显示</b>：静默模式还有一份自己的显示列表（<see cref="PluginManager.SetSilentDisplayed"/>）。
/// 只有「勾选了 **并且** 当前上报了重要信息」的插件才会出现在岛内；勾了但没重要信息 = 什么都不出现。</para>
///
/// <para><b>线程</b>：插件刷新线程随时可能上报，渲染线程每帧读，所以全部走锁 + 版本号。
/// 本类只做登记与聚合判断，不持有任何绘制资源。</para>
/// </summary>
public static class SilentMode
{
    private sealed class Entry
    {
        public bool Has;
        public bool UsePresentation;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Entry> _reports = new(StringComparer.OrdinalIgnoreCase);

    // 每次登记表变化 +1：渲染侧据此判断「本帧要不要重新算静默内容」。
    private static int _revision;

    /// <summary>登记表版本号（只增不减）。</summary>
    public static int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// 插件上报自己当前有没有重要信息。
    /// </summary>
    /// <param name="pluginId">宿主补的插件 Id，插件不用管。</param>
    /// <param name="hasImportant">true = 有重要信息（静默模式下岛要出现）。</param>
    /// <param name="useImportantPresentation">
    /// true = 静默模式用该插件自己的详情页（自定义页面）展示；false = 用折叠态组件（默认）。
    /// </param>
    public static void Report(string pluginId, bool hasImportant, bool useImportantPresentation)
    {
        if (string.IsNullOrEmpty(pluginId)) return;

        lock (_lock)
        {
            // 只要插件调用过本方法（哪怕报的是 false）就算「声明了自己是重要信息型插件」：
            // 静默模式设置页据此列出候选（否则一个没在下载的 RayBurst 永远不会出现在列表里）。
            if (!_reports.TryGetValue(pluginId, out var entry))
            {
                entry = new Entry();
                _reports[pluginId] = entry;
                _revision++;
            }

            if (entry.Has == hasImportant && entry.UsePresentation == useImportantPresentation) return;
            entry.Has = hasImportant;
            entry.UsePresentation = useImportantPresentation;
            _revision++;
        }
    }

    /// <summary>该插件是否声明过「会用到重要信息」（调用过 <see cref="Report"/>，不论 true / false）。</summary>
    public static bool IsDeclared(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        lock (_lock) return _reports.ContainsKey(pluginId);
    }

    /// <summary>该插件当前是否上报了重要信息。</summary>
    public static bool IsImportant(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        lock (_lock)
            return _reports.TryGetValue(pluginId, out var e) && e.Has;
    }

    /// <summary>该插件是否要求静默模式用「自定义页面」展示（而不是折叠态）。</summary>
    public static bool UsesPresentation(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        lock (_lock)
            return _reports.TryGetValue(pluginId, out var e) && e.Has && e.UsePresentation;
    }

    /// <summary>插件卸载 / 取消注册时清掉登记，免得残留的 true 让岛永远不藏。</summary>
    public static void Clear(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return;
        lock (_lock)
        {
            if (_reports.Remove(pluginId)) _revision++;
        }
    }

    /// <summary>静默模式下当前是否有任何「勾选了且正在上报」的插件内容。</summary>
    public static bool HasPluginContent
        => Renderer.SilentActive && PluginManager.Instance.SilentImportantCount > 0;

    /// <summary>静默模式当前是否该让岛出现（插件重要信息 or 重要实况窗）。</summary>
    public static bool ShouldShowIsland
        => Renderer.SilentActive
           && (HasPluginContent || LiveWindowHost.HasImportantLiveWindow);

    /// <summary>静默模式当前是否该把岛藏起来。</summary>
    public static bool ShouldHideIsland => Renderer.SilentActive && !ShouldShowIsland;
}
