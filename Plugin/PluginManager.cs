using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

/// <summary>插件运行状态。</summary>
public enum PluginState
{
    /// <summary>未加载（禁用 / 已卸载）。</summary>
    NotLoaded,
    /// <summary>已成功加载并初始化。</summary>
    Loaded,
    /// <summary>加载失败（DLL 损坏、缺少依赖、初始化抛异常等）。</summary>
    Failed
}

/// <summary>一个插件的运行时描述。</summary>
public sealed class PluginEntry
{
    /// <summary>相对 plugins 根的稳定标识（持久化用）。</summary>
    public string Key { get; init; } = "";
    /// <summary>入口 DLL 绝对路径。</summary>
    public string DllPath { get; set; } = "";
    /// <summary>是否目录型布局。</summary>
    public bool IsFolderLayout { get; init; }
    /// <summary>插件所在目录。</summary>
    public string RootDir { get; init; } = "";

    public string Id { get; internal set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; internal set; } = "";
    /// <summary>作者（插件没实现 <see cref="INotchPlugin.Author"/> 时退回程序集元数据，可能为空）。</summary>
    public string Author { get; internal set; } = "";
    public PluginState State { get; internal set; } = PluginState.NotLoaded;
    public string? Error { get; internal set; }

    public bool IsEnabled => State == PluginState.Loaded;

    internal INotchPlugin? Instance;
    internal PluginLoadContext? Context;
    internal string? ShadowDir;

    /// <summary>未加载时用于列表展示的友好名称。</summary>
    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName)
        ? Path.GetFileNameWithoutExtension(DllPath)
        : DisplayName;
}

/// <summary>
/// 插件运行时管理器（单例）。
///
/// 职责：
///   1. 扫描 plugins 目录并维护可管理列表；
///   2. 用「影子拷贝 + 可回收 AssemblyLoadContext」加载 / 卸载 / 热重载 DLL；
///   3. 持久化每个插件的启用 / 禁用状态；
///   4. 向 UI 暴露 <see cref="Changed"/> 事件。
///
/// 为什么用影子拷贝：直接加载用户目录里的 DLL 会被进程持有文件句柄，
/// 覆盖升级或热更新时会出现“文件被占用”。先把 DLL（目录型含依赖）复制到临时目录再加载，
/// 原文件就始终可以被替换；同时可回收 ALC 允许卸载旧代码。
/// </summary>
public sealed class PluginManager
{
    /// <summary>官网插件市场地址</summary>
    public const string MarketplaceUrl = "https://nps.georgewu.top/market";

    private const string RegistryBase = @"SOFTWARE\NotchPeninsula";
    private const string DisabledListValue = "Plugins_Disabled";
    private const string OrderListValue = "Plugins_Order";
    private const string HiddenListValue = "Plugins_Hidden";
    private const string NoPluginError = "DLL 中未找到 INotchPlugin 的实现";

    private static readonly Lazy<PluginManager> _lazy = new(() => new PluginManager());
    public static PluginManager Instance => _lazy.Value;

    private readonly PluginHost _host = new();
    private readonly List<PluginEntry> _entries = new();
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    // 🚫 「已加载但不显示」的插件（存 Key）：显示设置里取消勾选只把它从岛上收起，
    //    **插件照常运行**（不卸载、不禁用），下次启动也保持不显示。
    //    与 _disabled 是两件独立的事：_disabled = 不跑；_hidden = 跑但不显示。
    //    ⚠️ 两者的联动是**单向**的：启用会自动取消隐藏（启用即要显示），
    //       取消勾选显示则绝不动启用状态。
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    // 插件显示顺序（存 Key，即相对 plugins 根的稳定标识）：持久化在注册表，决定灵动岛上的排列位置。
    // 为什么不用 pluginId：pluginId 只有「加载成功」后才知道，插件一旦被禁用/加载失败就查不到，
    // 会导致顺序位丢失、甚至只剩一个启用插件时排序按钮全部失效。Key 是磁盘上的稳定标识，与运行状态无关。
    private readonly List<string> _order = new();
    // 构造时从注册表读回的原始条目（可能是早期版本写入的 pluginId 格式），首次 EnsureOrder 时迁移成 Key
    private readonly List<string> _rawOrder = new();
    private bool _orderMigrated;
    private readonly object _lock = new();

    public PluginHost Host => _host;

    // 变更序号：任何「发现 / 加载 / 卸载 / 启用状态 / 排序 / 加载失败」都会自增。
    // 设置面板用它做缓存判据 —— 插件列表与行内文案原本是每次渲染都重建，
    // 有了这个序号就能只在真变了的时候重建一次。
    private int _changeVersion;

    /// <summary>注册表变更序号（单调递增，只在 <see cref="Changed"/> 触发前自增）。</summary>
    public int ChangeVersion => System.Threading.Volatile.Read(ref _changeVersion);
    public string PluginsRoot { get; }
    public IReadOnlyList<PluginEntry> Entries { get { lock (_lock) return _entries.ToArray(); } }

    /// <summary>插件列表 / 状态发生变化时触发（UI 订阅后刷新即可）。</summary>
    public event Action? Changed;

    /// <summary>插件显示顺序（Key 列表），持久化在注册表。</summary>
    public IReadOnlyList<string> Order { get { lock (_lock) return _order.ToArray(); } }

    private PluginManager()
    {
        PluginsRoot = Path.Combine(GetAppDirectory(), "plugins");
        LoadDisabledList();
        LoadHiddenList();
        LoadOrderList();
    }

    // ====================================================================
    // 生命周期
    // ====================================================================

    /// <summary>程序启动时调用：清临时影子目录 → 发现插件 → 自动加载已启用的插件。</summary>
    public void Initialize()
    {
        try
        {
            CleanShadowRoot();
            Directory.CreateDirectory(PluginsRoot);
            Refresh();
            // 顺序表还原发生在 Load() 内部第一次 EnsureOrder()（见 Load 里的调用）：
            // 那时 Refresh() 已经把所有插件（含禁用/加载失败的）都登记进 _entries，
            // 所以保存的顺序能按 Key 原样还原，与「插件是否加载成功」无关。
            foreach (var e in Entries)
                if (!_disabled.Contains(e.Key) && e.State != PluginState.Loaded)
                    Load(e);

            // 把本次加载出来的插件补进顺序表并注入宿主，灵动岛据此排列插件位置
            EnsureOrder();
            PushOrderToHost();

            Logger.Info($"[PluginManager] 初始化完成，共发现 {Entries.Count} 个插件，已加载 {Entries.Count(x => x.State == PluginState.Loaded)} 个");
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginManager] 初始化失败", ex);
        }
    }

    /// <summary>重新扫描 plugins 目录（保留已加载项的状态）。</summary>
    public void Refresh()
    {
        var sources = PluginLoader.Discover(PluginsRoot);
        lock (_lock)
        {
            var existing = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _entries) existing[e.Key] = e;

            var fresh = new List<PluginEntry>(sources.Count);
            foreach (var s in sources)
            {
                if (existing.TryGetValue(s.Key, out var e))
                {
                    e.DllPath = s.DllPath;
                    fresh.Add(e);
                }
                else
                {
                    fresh.Add(new PluginEntry
                    {
                        Key = s.Key,
                        DllPath = s.DllPath,
                        IsFolderLayout = s.IsFolderLayout,
                        RootDir = s.RootDir
                    });
                }
            }
            _entries.Clear();
            _entries.AddRange(fresh);
        }
    }

    public PluginEntry? Find(string key)
    {
        lock (_lock) return _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    // ====================================================================
    // 插件显示顺序（决定插件内容在灵动岛上的排列位置）
    // ====================================================================

    /// <summary>「显示内容」列表里的一行（显示设置页据此渲染复选框与左右移动按钮）。</summary>
    public sealed class DisplayItem
    {
        /// <summary>顺序项标识：内置模块为 builtin.*，插件为 <see cref="PluginEntry.Key"/>。</summary>
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>
        /// 当前是否显示在灵动岛上：内置模块 = CompShow*，插件 = **已加载且未被隐藏**。
        /// 插件这里为 false 有两种原因：没跑（禁用 / 加载失败），或跑着但被用户取消了显示。
        /// </summary>
        public bool IsShown { get; init; }
        /// <summary>是否内置模块（时间日期 / 硬件占用 / 媒体控制器），UI 用它加「（内置）」标记。</summary>
        public bool IsBuiltin { get; init; }
    }

    /// <summary>
    /// 「显示内容」列表：内置模块（时间日期 / 硬件占用 / 媒体控制器）+ 全部插件，
    /// 严格按显示顺序表排列 —— 这就是灵动岛上内容的排列次序。
    ///
    /// <para>
    /// <b>向下兼容</b>：读的就是老版本那张顺序表（注册表 <c>Plugins_Order</c>），
    /// 内置模块与插件的 Key 混排在同一张表里，所以升级后**用户此前调好的插件位置会原样带过来**，
    /// 只是入口从「插件中心」搬到了「显示设置」。表里没有的内容（老版本从未排过序的插件）
    /// 由 <see cref="EnsureOrder"/> 追加到末尾；老版本用 pluginId 写下的历史顺序也在那里迁移成 Key。
    /// </para>
    ///
    /// <para>
    /// 未勾选的插件（禁用 / 加载失败）**也在列表里**（复选框空着），
    /// 用户才能在同一处把它重新勾回来；已经不在磁盘上的残留顺序项直接跳过。
    /// </para>
    /// </summary>
    public IReadOnlyList<DisplayItem> DisplayItems
    {
        get
        {
            lock (_lock)
            {
                var list = new List<DisplayItem>(_order.Count + BuiltinWidgets.Default.Length);
                foreach (var key in _order)
                {
                    if (BuiltinWidgets.IsBuiltin(key))
                    {
                        list.Add(new DisplayItem { Key = key, Name = BuiltinName(key), IsShown = IsBuiltinDisplayed(key), IsBuiltin = true });
                        continue;
                    }

                    var e = FindEntryByKeyOrId(key);
                    if (e == null || string.IsNullOrEmpty(e.Key)) continue; // 顺序表里的失效残留
                    list.Add(new DisplayItem
                    {
                        Key = e.Key,
                        Name = e.FriendlyName,
                        IsShown = e.State == PluginState.Loaded && !_hidden.Contains(e.Key)
                    });
                }

                // 🛟 兜底：内置模块一行都不能少。
                //    正常路径下 EnsureOrder 已把它们补进表里，但那个调用只发生在 Initialize() 里 ——
                //    万一初始化没跑（plugins 目录异常 / 初始化抛错），老版本的显示设置页仍然有
                //    「时间日期 / 硬件占用 / 空白」三选一可点，这里要是空了，用户就彻底没入口把时钟勾回来了。
                foreach (var b in BuiltinWidgets.Default)
                    if (!list.Any(x => string.Equals(x.Key, b, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new DisplayItem { Key = b, Name = BuiltinName(b), IsShown = IsBuiltinDisplayed(b), IsBuiltin = true });

                return list;
            }
        }
    }

    /// <summary>内置模块的中文名（与显示设置页的文案一致）。调用方需持有 _lock。</summary>
    private static string BuiltinName(string key)
    {
        if (string.Equals(key, BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase)) return "时间日期";
        if (string.Equals(key, BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase)) return "资源占用检测";
        return "媒体控制器(含频谱)";
    }

    /// <summary>
    /// 勾选 / 取消勾选某个顺序项并持久化。
    ///
    /// <para>
    /// 内置模块写 <c>CompShow*</c>，插件写「隐藏清单」——与插件中心的开关**不是同一件事**：
    /// </para>
    /// <list type="bullet">
    ///   <item>勾选插件 = 要看到它 → 顺带启用（没跑就没法显示），会退出隐藏清单；</item>
    ///   <item>取消勾选插件 = **只从岛上收起，不禁用、不卸载**，插件继续在后台跑；</item>
    ///   <item>反向由 <see cref="SetEnabled"/> 兜：启用会自动取消隐藏，禁用则天然不显示。</item>
    /// </list>
    /// </summary>
    public void SetDisplayed(string key, bool shown)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (BuiltinWidgets.IsBuiltin(key))
        {
            if (string.Equals(key, BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase))
            {
                Renderer.CompShowDateTime = shown;
                Program.SaveSetting("Composite_ShowDateTime", shown ? 1 : 0);
            }
            else if (string.Equals(key, BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase))
            {
                Renderer.CompShowHardware = shown;
                Program.SaveSetting("Composite_ShowHardware", shown ? 1 : 0);
            }
            else
            {
                Renderer.CompShowMedia = shown;
                Program.SaveSetting("Composite_ShowMedia", shown ? 1 : 0);
            }
            RaiseChanged();
            return;
        }

        var e = Find(key);
        if (e == null) return;

        if (shown)
        {
            // 勾上显示 = 用户要看到它。插件没在跑就先跑起来（SetEnabled 内部会把它移出隐藏清单）
            if (e.State != PluginState.Loaded) { SetEnabled(e, true); return; }
            if (!_hidden.Remove(e.Key)) return; // 本来就显示着，什么都不用做
        }
        else
        {
            // 只收起显示：**不动启用状态**（插件继续运行，下次启动也保持收起）
            if (!_hidden.Add(e.Key)) return;
        }

        SaveHiddenList();
        PushOrderToHost(); // 立即重排/收起，灵动岛下一帧生效
        RaiseChanged();
    }

    /// <summary>
    /// 该顺序项能否朝指定方向移动（delta = -1 上移 / +1 下移）。
    /// 跳过失效的残留项（插件已移除），所以「视觉上相邻的两行」永远是彼此的落点。
    /// </summary>
    public bool CanMoveDisplay(string key, int delta)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_lock)
        {
            int idx = IndexOfOrder(key);
            return idx >= 0 && FindMoveTargetLocked(idx, delta) >= 0;
        }
    }

    /// <summary>
    /// 把顺序项朝指定方向与相邻项交换位置并持久化，灵动岛下一帧即生效。
    /// 内置模块与插件走同一条路径 —— 它们本来就在同一张顺序表里。
    /// </summary>
    public bool MoveDisplay(string key, int delta)
    {
        if (string.IsNullOrEmpty(key)) return false;

        lock (_lock)
        {
            int idx = IndexOfOrder(key);
            if (idx < 0) return false;

            int target = FindMoveTargetLocked(idx, delta);
            if (target < 0) return false;

            string moving = _order[idx];
            _order[idx] = _order[target];
            _order[target] = moving;
        }

        SaveOrderList();
        PushOrderToHost(); // 顺序变化 → 重新按新顺序输出组件，灵动岛下一帧即生效
        RaiseChanged();
        return true;
    }

    /// <summary>顺序项在顺序表里的下标；不在表里返回 -1。调用方需持有 _lock。</summary>
    private int IndexOfOrder(string key)
        => _order.FindIndex(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>idx 朝 delta 方向第一个「列表里真的会显示出来」的位置；没有则返回 -1。调用方需持有 _lock。</summary>
    private int FindMoveTargetLocked(int idx, int delta)
    {
        for (int t = idx + delta; t >= 0 && t < _order.Count; t += delta)
            if (IsListedLocked(_order[t])) return t;
        return -1;
    }

    /// <summary>该顺序项会不会出现在「显示内容」列表里（内置模块或磁盘上还在的插件）。调用方需持有 _lock。</summary>
    private bool IsListedLocked(string key)
        => BuiltinWidgets.IsBuiltin(key) || FindEntryByKeyOrId(key) != null;

    /// <summary>原生模块当前是否勾选显示（与显示设置页的复选框、渲染器的绘制门控同源）。</summary>
    private static bool IsBuiltinDisplayed(string id)
    {
        if (string.Equals(id, BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase)) return Renderer.CompShowDateTime;
        if (string.Equals(id, BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase)) return Renderer.CompShowHardware;
        return Renderer.CompShowMedia; // 媒体控制器
    }

    /// <summary>
    /// 把发现到的插件补进顺序表：首次调用时先把注册表里的历史顺序迁移成 Key，
    /// 之后把尚未登记顺序的插件按发现顺序追加到末尾。
    /// </summary>
    private void EnsureOrder()
    {
        bool changed = false;
        lock (_lock)
        {
            // ① 迁移历史数据：早期版本写的是 pluginId，这里按「Key 优先、Id 兜底」还原成 Key
            if (!_orderMigrated)
            {
                _orderMigrated = true;
                foreach (var raw in _rawOrder)
                {
                    // ⚠️ 原生模块（builtin.clock / hardware / media）**不在** _entries 里，
                    //    必须原样保留。此前这里只按插件条目查找，导致注册表里保存的顺序中
                    //    三个原生模块被整批丢弃，紧接着第 ② 步又把它们补回**最前面**，
                    //    用户调好的插件位置每次重启都会被冲掉（表现：调好的 #1 重启后回到 #4）。
                    //    （用户 2026-09-20 反馈「插件调整位置后没有记忆化」）
                    if (Plugins.BuiltinWidgets.IsBuiltin(raw))
                    {
                        if (!ContainsOrder(raw)) { _order.Add(raw); changed = true; }
                        continue;
                    }

                    var matched = FindEntryByKeyOrId(raw);
                    if (matched == null || string.IsNullOrEmpty(matched.Key)) continue;
                    if (ContainsOrder(matched.Key)) continue;
                    _order.Add(matched.Key);
                    changed = true;
                }
                _rawOrder.Clear();
            }

            // ② 原生模块（时间日期 / 硬件 / 媒体）默认排在所有插件之前 —— 与引入顺序表之前的表现一致。
            //    只有顺序表里完全找不到它们时才补，避免覆盖用户已经调过的位置。
            var missingBuiltin = new List<string>(Plugins.BuiltinWidgets.Default.Length);
            foreach (var b in Plugins.BuiltinWidgets.Default)
                if (!ContainsOrder(b)) missingBuiltin.Add(b);
            if (missingBuiltin.Count > 0)
            {
                _order.InsertRange(0, missingBuiltin);
                changed = true;
            }

            // ③ 新发现的插件追加到末尾
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                if (ContainsOrder(e.Key)) continue;
                _order.Add(e.Key);
                changed = true;
            }
        }
        if (changed) SaveOrderList();
    }

    /// <summary>按 Key 查找；找不到再按已加载插件的 pluginId 查找（兼容旧顺序数据）。调用方需持有 _lock。</summary>
    private PluginEntry? FindEntryByKeyOrId(string value)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Key, value, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (var e in _entries)
            if (!string.IsNullOrEmpty(e.Id) && string.Equals(e.Id, value, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>
    /// 把顺序表注入宿主：原生模块（builtin.*）原样传递，插件则把 Key 换成 pluginId。
    /// 未加载（禁用 / 失败）的插件不输出，但其顺序位在表里保留，启用后自动回到原位。
    /// <b>被隐藏的插件</b>同样不进顺序表，而是单独交给 <see cref="PluginHost.SetHiddenPlugins"/> ——
    /// 宿主据此把它的组件从 Widgets 里滤掉，插件照常运行、只是不在岛上。
    /// </summary>
    private void PushOrderToHost()
    {
        string[] ids;
        string[] hiddenIds;
        lock (_lock)
        {
            var list = new List<string>(_order.Count);
            var hidden = new List<string>(_hidden.Count);
            foreach (var item in _order)
            {
                if (Plugins.BuiltinWidgets.IsBuiltin(item)) { list.Add(item); continue; }

                var e = FindEntryByKeyOrId(item);
                if (e != null && !string.IsNullOrEmpty(e.Id))
                {
                    if (_hidden.Contains(e.Key)) hidden.Add(e.Id);
                    else list.Add(e.Id);
                }
            }
            ids = list.ToArray();
            hiddenIds = hidden.ToArray();
        }
        _host.SetPluginOrder(ids);
        _host.SetHiddenPlugins(hiddenIds);
    }

    private void LoadOrderList()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryBase);
            if (key?.GetValue(OrderListValue) is not string raw || string.IsNullOrWhiteSpace(raw)) return;
            lock (_lock)
            {
                foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ContainsRawOrder(item)) continue;
                    _rawOrder.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginManager] 读取插件显示顺序失败", ex);
        }
    }

    private bool ContainsRawOrder(string value)
    {
        for (int i = 0; i < _rawOrder.Count; i++)
            if (string.Equals(_rawOrder[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool ContainsOrder(string key)
    {
        for (int i = 0; i < _order.Count; i++)
            if (string.Equals(_order[i], key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void SaveOrderList()
    {
        try
        {
            string raw;
            lock (_lock) raw = string.Join(";", _order);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryBase);
            key?.SetValue(OrderListValue, raw);
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginManager] 保存插件显示顺序失败", ex);
        }
    }

    // ====================================================================
    // 加载 / 卸载 / 热重载
    // ====================================================================

    /// <summary>加载一个插件。返回是否成功。</summary>
    public bool Load(PluginEntry e)
    {
        if (e.State == PluginState.Loaded) return true;
        try
        {
            e.Error = null;
            var shadow = CreateShadowCopy(e);
            var ctx = new PluginLoadContext(shadow);
            var dllInShadow = Path.Combine(shadow, Path.GetFileName(e.DllPath));
            var asm = ctx.LoadFromAssemblyPath(dllInShadow);

            var pluginType = FindPluginType(asm);
            if (pluginType == null)
            {
                ctx.Unload();
                TryDeleteDir(shadow);
                e.State = PluginState.Failed;
                e.Error = NoPluginError;
                RaiseChanged();
                return false;
            }

            var plugin = (INotchPlugin)Activator.CreateInstance(pluginType)!;
            e.Context = ctx;
            e.ShadowDir = shadow;
            e.Instance = plugin;

            // 元数据字符串必须复制到宿主堆：插件返回的是 loader heap 上的常量，
            // 宿主长期持有会导致该 ALC 无法回收（详见 PluginHost.DetachString）
            e.Id = PluginHost.DetachString(plugin.Id);
            e.DisplayName = PluginHost.DetachString(plugin.DisplayName);
            e.Version = PluginHost.DetachString(plugin.Version);
            e.Author = PluginHost.DetachString(ReadAuthor(plugin, asm));

            _host.RegisterPlugin(e.Id, e.DisplayName, e.Version);
            plugin.Initialize(_host.CreateScopedHost(e.Id));

            e.State = PluginState.Loaded;
            // 新加载的插件若还没有顺序位置，追加到末尾并同步给宿主
            EnsureOrder();
            PushOrderToHost();
            Logger.Info($"[PluginManager] 已加载 {plugin.Id} v{plugin.Version} ({plugin.DisplayName})");
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is ReflectionTypeLoadException rtle)
                foreach (var le in rtle.LoaderExceptions)
                    if (le != null) Logger.Error($"[PluginManager] 类型加载失败: {le.Message}");

            e.State = PluginState.Failed;
            e.Error = ex.Message;
            Logger.Error($"[PluginManager] 加载失败: {e.Key}", ex);
            RaiseChanged();
            return false;
        }
    }

    /// <summary>卸载插件：让插件释放资源 → 摘除全部注册物 → 卸载 ALC → 回收影子目录。</summary>
    public void Unload(PluginEntry e)
    {
        var shadow = e.ShadowDir;
        e.ShadowDir = null;

        // 整个「释放 + 注销 + 卸载」都在独立栈帧里完成（见 Teardown）。
        // 这样本方法（下面要执行 GC 循环）的栈帧里不会残留任何插件对象引用。
        var weak = Teardown(e);
        if (weak != null)
        {
            // 必须在同步 GC 之前：渲染侧的静态字段（组件数组 / 详情页 / 命中区）平时要等到
            // 下一帧才发现版本号变化，此刻它们还钉着插件对象，WaitForUnload 必然判「未被回收」。
            Renderer.InvalidatePluginSnapshot();
            WaitForUnload(weak, e.Key);
        }

        TryDeleteDir(shadow);
        Logger.Info($"[PluginManager] 已卸载 {e.Key}");
        RaiseChanged();
    }

    /// <summary>
    /// 完成插件的资源释放、宿主注销与 ALC 卸载，只把 <see cref="WeakReference"/> 交回调用方。
    ///
    /// 这是热重载最容易踩的坑：可回收 ALC 要求「卸载后没有任何强引用指向它」。
    /// 如果调用方（做 GC 的那个方法）的局部变量里还留着插件实例 / 加载上下文，
    /// ——哪怕已经把字段置空——在未优化的 Debug 构建中这些局部变量会存活到方法结束，
    /// ALC 就永远回收不掉。所以必须让持有强引用的栈帧提前销毁。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? Teardown(PluginEntry e)
    {
        var instance = e.Instance;
        var ctx = e.Context;
        var id = e.Id;

        e.Instance = null;
        e.Context = null;
        e.State = PluginState.NotLoaded;
        e.Error = null;

        // 1) 给插件一个主动释放资源的机会（实现 IDisposable 即可，非强制）。
        //    宿主交给插件的资源（刷新句柄 / 组件 / 设置页）会由 UnregisterPlugin 自动回收，
        //    这里主要是给插件自己创建的线程、句柄、连接等一个清理时机。
        if (instance is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { Logger.Error($"[PluginManager] 插件 Dispose 异常: {e.Key}", ex); }
        }

        // 2) 摘除宿主中该插件的全部登记引用（组件 / 设置页 / 刷新句柄 / 设置事件）
        if (!string.IsNullOrEmpty(id))
            _host.UnregisterPlugin(id);

        if (ctx == null) return null;

        // 3) 卸载加载上下文：可回收 ALC 需要 GC + 终结器轮次才会真正释放
        var weak = new WeakReference(ctx, trackResurrection: true);
        ctx.Unload();
        return weak;
    }

    /// <summary>只持有弱引用，反复 GC 直到加载上下文被回收。</summary>
    private static void WaitForUnload(WeakReference weak, string key)
    {
        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }
        if (weak.IsAlive)
            Logger.Warn($"[PluginManager] {key} 的加载上下文暂未被回收（插件可能仍持有外部引用），不影响重新加载。");
    }

    /// <summary>热重载：卸载后重新读取磁盘上的 DLL（可先在外部替换 DLL 再点重载）。</summary>
    public bool Reload(PluginEntry e)
    {
        Unload(e);
        e.Id = "";
        e.Version = "";
        e.DisplayName = "";
        e.Author = "";
        Refresh();
        var fresh = Find(e.Key) ?? e;
        return Load(fresh);
    }

    /// <summary>
    /// 启用 / 禁用插件（持久化）。
    ///
    /// <para>
    /// 与「显示」（<see cref="SetDisplayed"/>）的联动是**单向**的：
    /// <list type="bullet">
    ///   <item>启用 → 自动取消隐藏（用户打开开关就是要用它）；</item>
    ///   <item>禁用 → 插件被卸载，自然不显示，隐藏清单不用动（下次启用会自动显示）。</item>
    /// </list>
    /// 反方向不动：取消勾选显示**绝不禁用**插件。
    /// </para>
    /// </summary>
    public void SetEnabled(PluginEntry e, bool enabled)
    {
        if (enabled)
        {
            _disabled.Remove(e.Key);
            SaveDisabledList();

            // 🔗 「启用插件自动显示」：把隐藏标记摘掉，否则插件跑起来了岛上却还是看不到
            if (_hidden.Remove(e.Key)) SaveHiddenList();

            Refresh();
            var fresh = Find(e.Key) ?? e;
            if (fresh.State != PluginState.Loaded) Load(fresh);
            // Load 内部已经推过一次顺序；这里再推一次是为了覆盖「本来就已加载、只是被隐藏」这条路径
            PushOrderToHost();
        }
        else
        {
            _disabled.Add(e.Key);
            SaveDisabledList();
            var cur = Find(e.Key) ?? e;
            Unload(cur);
        }
        RaiseChanged();
    }

    // ====================================================================
    // 导入 / 移除 / 打开目录
    // ====================================================================

    /// <summary>把外部 DLL 复制进 plugins 目录并尝试加载。若不是合法插件则回滚删除。</summary>
    public (bool Ok, string Message) Import(string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath)) return (false, "文件不存在");

            Directory.CreateDirectory(PluginsRoot);
            var target = Path.Combine(PluginsRoot, Path.GetFileName(dllPath));
            if (File.Exists(target))
                target = Path.Combine(PluginsRoot,
                    $"{Path.GetFileNameWithoutExtension(dllPath)}_{DateTime.Now:yyyyMMddHHmmss}.dll");

            File.Copy(dllPath, target, false);
            Refresh();

            var e = Find(Path.GetFileName(target));
            if (e == null) { TryDelete(target); return (false, "导入失败：无法识别插件"); }

            if (Load(e)) return (true, $"已导入并加载：{e.FriendlyName}");

            // 根本不是插件 → 回滚；是插件但初始化失败 → 保留以便排查
            if (e.Error == NoPluginError)
            {
                TryDelete(target);
                lock (_lock) _entries.Remove(e);
                RaiseChanged();
                return (false, "所选 DLL 不是合法的 NotchPeninsula 插件");
            }
            return (false, $"已导入但加载失败：{e.Error}");
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginManager] 导入插件失败", ex);
            return (false, $"导入失败：{ex.Message}");
        }
    }

    /// <summary>移除插件：先卸载，再把文件/目录移入 plugins/_recycle 回收站（不直接删除，可手动找回）。</summary>
    public void Remove(PluginEntry e)
    {
        Unload(e);

        // 插件已移出 plugins 目录 → 从显示顺序表里彻底摘掉，避免残留顺序位占位
        lock (_lock)
        {
            _order.RemoveAll(x => string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase));
            _rawOrder.RemoveAll(x => string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase));
        }
        SaveOrderList();

        // 隐藏清单同理：文件都没了，标记留着只会在将来重名插件上莫名其妙地生效
        if (_hidden.Remove(e.Key)) SaveHiddenList();

        try
        {
            var recycle = Path.Combine(PluginsRoot, "_recycle", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(recycle);

            if (e.IsFolderLayout && Directory.Exists(e.RootDir))
                Directory.Move(e.RootDir, Path.Combine(recycle, Path.GetFileName(e.RootDir)));
            else if (File.Exists(e.DllPath))
                File.Move(e.DllPath, Path.Combine(recycle, Path.GetFileName(e.DllPath)));

            Logger.Info($"[PluginManager] 已移入回收站: {e.Key} → {recycle}");
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginManager] 移除插件文件失败", ex);
        }
        Refresh();
        PushOrderToHost();
        RaiseChanged();
    }

    /// <summary>用资源管理器打开插件目录。</summary>
    public void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(PluginsRoot);
            Process.Start(new ProcessStartInfo(PluginsRoot) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 打开插件目录失败", ex); }
    }

    /// <summary>打开官网插件市场。</summary>
    public void OpenMarketplace()
    {
        try { Process.Start(new ProcessStartInfo(MarketplaceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("[PluginManager] 打开插件市场失败", ex); }
    }

    // ====================================================================
    // 内部工具
    // ====================================================================

    /// <summary>
    /// 取插件作者：优先插件自己声明的 <see cref="INotchPlugin.Author"/>，
    /// 为空（老插件没实现该成员）时退回程序集的 <c>AssemblyCompany</c> 元数据（csproj 的 Authors/Company）。
    /// 取作者失败绝不能让插件加载失败，所以两条路径都各自兜住异常。
    /// </summary>
    private static string ReadAuthor(INotchPlugin plugin, Assembly asm)
    {
        try
        {
            string declared = plugin.Author;
            if (!string.IsNullOrWhiteSpace(declared)) return declared.Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[PluginManager] 读取插件作者失败，改用程序集元数据: {ex.Message}");
        }

        try { return asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static Type? FindPluginType(Assembly asm)
    {
        foreach (var t in asm.GetTypes())
            if (!t.IsAbstract && !t.IsInterface && typeof(INotchPlugin).IsAssignableFrom(t))
                return t;
        return null;
    }

    /// <summary>构建影子拷贝目录（目录型复制整个目录的关键文件，单文件型只复制该 DLL）。</summary>
    private string CreateShadowCopy(PluginEntry e)
    {
        var dir = Path.Combine(ShadowRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        if (e.IsFolderLayout && Directory.Exists(e.RootDir))
        {
            foreach (var file in Directory.GetFiles(e.RootDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".dll" or ".json" or ".pdb" or ".config" or ".xml" or ".txt")
                    File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
            }
        }
        File.Copy(e.DllPath, Path.Combine(dir, Path.GetFileName(e.DllPath)), true);
        return dir;
    }

    private static string ShadowRoot => Path.Combine(Path.GetTempPath(), "NotchPeninsula", "plugins");

    private static void CleanShadowRoot()
    {
        TryDeleteDir(ShadowRoot);
    }

    private static void TryDeleteDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try { Directory.Delete(dir, true); } catch { /* 文件被占用时忽略，留待下次启动清理 */ }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    private void RaiseChanged()
    {
        System.Threading.Interlocked.Increment(ref _changeVersion);
        try { Changed?.Invoke(); } catch (Exception ex) { Logger.Error("[PluginManager] Changed 事件处理异常", ex); }
    }

    // ---- 启用状态持久化（禁用清单） ----

    private void LoadDisabledList()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryBase);
            var raw = key?.GetValue(DisabledListValue) as string;
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _disabled.Add(item);
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 读取插件禁用清单失败", ex); }
    }

    private void SaveDisabledList()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryBase);
            key?.SetValue(DisabledListValue, string.Join(";", _disabled));
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 保存插件禁用清单失败", ex); }
    }

    // ---- 显示状态持久化（隐藏清单：插件照常运行，只是不出现在岛上） ----

    private void LoadHiddenList()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryBase);
            var raw = key?.GetValue(HiddenListValue) as string;
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _hidden.Add(item);
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 读取插件隐藏清单失败", ex); }
    }

    private void SaveHiddenList()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryBase);
            key?.SetValue(HiddenListValue, string.Join(";", _hidden));
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 保存插件隐藏清单失败", ex); }
    }

    /// <summary>exe 所在目录：单文件发布时 AppContext.BaseDirectory 是临时解压目录，插件必须放 exe 同级。</summary>
    private static string GetAppDirectory()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) &&
            !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>
/// 插件加载上下文：可回收（isCollectible: true），以便卸载旧版本代码。
/// 依赖解析策略：主程序/系统已加载的程序集一律交回默认上下文（保证 INotchPlugin 类型唯一），
/// 其余从影子目录加载。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;

    public PluginLoadContext(string dir) : base($"plugin:{Path.GetFileName(dir)}", isCollectible: true)
    {
        _dir = dir;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        bool alreadyShared = Default.Assemblies.Any(a =>
            string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyShared) return null;

        var path = Path.Combine(_dir, assemblyName.Name + ".dll");
        if (File.Exists(path)) return LoadFromAssemblyPath(path);
        return null;
    }
}
