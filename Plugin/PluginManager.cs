using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace NotchPeninsula.Plugins;

public enum PluginState
{
    NotLoaded,
    Loaded,
    Failed
}

public sealed class PluginEntry
{
    public string Key { get; init; } = "";
    public string DllPath { get; set; } = "";
    public bool IsFolderLayout { get; init; }
    public string RootDir { get; init; } = "";

    public string Id { get; internal set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; internal set; } = "";
    public string Author { get; internal set; } = "";
    public PluginState State { get; internal set; } = PluginState.NotLoaded;
    public string? Error { get; internal set; }

    internal string CachedName { get; set; } = "";

    public bool IsEnabled => State == PluginState.Loaded;

    internal INotchPlugin? Instance;
    internal PluginLoadContext? Context;
    internal string? ShadowDir;

    public string FriendlyName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName;
            if (!string.IsNullOrWhiteSpace(CachedName)) return CachedName;
            return Path.GetFileNameWithoutExtension(DllPath);
        }
    }
}

public sealed class PluginManager
{
    public const string MarketplaceUrl = "https://nps.georgewu.top/market";

    private const string RegistryBase = @"SOFTWARE\NotchPeninsula";
    private const string DisabledListValue = "Plugins_Disabled";
    private const string OrderListValue = "Plugins_Order";
    private const string HiddenListValue = "Plugins_Hidden";
    private const string NameCacheValue = "Plugins_Names";
    private const string NoPluginError = "DLL 中未找到 INotchPlugin 的实现";

    private static readonly Lazy<PluginManager> _lazy = new(() => new PluginManager());
    public static PluginManager Instance => _lazy.Value;

    private readonly PluginHost _host = new();
    private readonly List<PluginEntry> _entries = new();
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    // 插件照常运行（不卸载、不禁用），下次启动也保持不显示。
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly List<string> _rawOrder = new();
    private bool _orderMigrated;
    private readonly object _lock = new();

    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    public PluginHost Host => _host;

    // 有了这个序号就能只在真变了的时候重建一次。
    private int _changeVersion;

    private readonly object _displayItemsLock = new();
    private IReadOnlyList<DisplayItem>? _displayItemsCache;
    private int _displayItemsVersion = -1;

    public int ChangeVersion => System.Threading.Volatile.Read(ref _changeVersion);
    public string PluginsRoot { get; }
    public IReadOnlyList<PluginEntry> Entries { get { lock (_lock) return _entries.ToArray(); } }

    public event Action? Changed;

    public IReadOnlyList<string> Order { get { lock (_lock) return _order.ToArray(); } }

    private PluginManager()
    {
        PluginsRoot = Path.Combine(GetAppDirectory(), "plugins");
        LoadDisabledList();
        LoadHiddenList();
        LoadOrderList();
        LoadNameCache();
    }

    // ---- 生命周期 ----

    public void Initialize()
    {
        try
        {
            CleanShadowRoot();
            Directory.CreateDirectory(PluginsRoot);
            Refresh();
            foreach (var e in Entries)
                if (!_disabled.Contains(e.Key) && e.State != PluginState.Loaded)
                    Load(e);

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

            foreach (var e in _entries)
                if (string.IsNullOrEmpty(e.CachedName) && _nameCache.TryGetValue(e.Key, out var cached))
                    e.CachedName = cached;
        }
    }

    public PluginEntry? Find(string key)
    {
        lock (_lock) return _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDisabled(PluginEntry? e)
    {
        if (e == null || string.IsNullOrEmpty(e.Key)) return false;
        lock (_lock) return _disabled.Contains(e.Key);
    }

    public sealed class DisplayItem
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsShown { get; init; }
        public bool IsBuiltin { get; init; }
    }

    public IReadOnlyList<DisplayItem> DisplayItems
    {
        get
        {
            int version = ChangeVersion;
            lock (_displayItemsLock)
            {
                if (_displayItemsCache != null && _displayItemsVersion == version)
                    return _displayItemsCache;
            }

            IReadOnlyList<DisplayItem> built;
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
                    if (e == null || string.IsNullOrEmpty(e.Key)) continue;
                    if (e.State != PluginState.Loaded) continue;
                    if (IgnoresDisplayOrder(e)) continue;
                    list.Add(new DisplayItem
                    {
                        Key = e.Key,
                        Name = e.FriendlyName,
                        IsShown = !_hidden.Contains(e.Key)
                    });
                }

                foreach (var b in BuiltinWidgets.Default)
                    if (!list.Any(x => string.Equals(x.Key, b, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new DisplayItem { Key = b, Name = BuiltinName(b), IsShown = IsBuiltinDisplayed(b), IsBuiltin = true });

                built = list;
            }

            lock (_displayItemsLock)
            {
                _displayItemsCache = built;
                _displayItemsVersion = version;
            }
            return built;
        }
    }

    private static bool IgnoresDisplayOrder(PluginEntry e)
    {
        const string marker = "taskbarwidget";
        return ContainsMarker(e.Key, marker) || ContainsMarker(e.Id, marker);
    }

    private static bool ContainsMarker(string? value, string marker)
        => !string.IsNullOrEmpty(value) && value.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string BuiltinName(string key)
    {
        if (string.Equals(key, BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase)) return "时间日期";
        if (string.Equals(key, BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase)) return "资源占用检测";
        return "媒体控制器(含频谱)";
    }

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
            if (e.State != PluginState.Loaded) { SetEnabled(e, true); return; }
            if (!_hidden.Remove(e.Key)) return; // 本来就显示着，什么都不用做
        }
        else
        {
            if (!_hidden.Add(e.Key)) return;
        }

        SaveHiddenList();
        PushOrderToHost(); // 立即重排/收起，灵动岛下一帧生效
        RaiseChanged();
    }

    public bool CanMoveDisplay(string key, int delta)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_lock)
        {
            int idx = IndexOfOrder(key);
            return idx >= 0 && FindMoveTargetLocked(idx, delta) >= 0;
        }
    }

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

    private int IndexOfOrder(string key)
        => _order.FindIndex(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

    private int FindMoveTargetLocked(int idx, int delta)
    {
        for (int t = idx + delta; t >= 0 && t < _order.Count; t += delta)
            if (IsListedLocked(_order[t])) return t;
        return -1;
    }

    private bool IsListedLocked(string key)
        => BuiltinWidgets.IsBuiltin(key) || IsListedPluginLocked(key);

    private bool IsListedPluginLocked(string key)
    {
        var e = FindEntryByKeyOrId(key);
        return e != null && e.State == PluginState.Loaded && !IgnoresDisplayOrder(e);
    }

    private static bool IsBuiltinDisplayed(string id)
    {
        if (string.Equals(id, BuiltinWidgets.Clock, StringComparison.OrdinalIgnoreCase)) return Renderer.CompShowDateTime;
        if (string.Equals(id, BuiltinWidgets.Hardware, StringComparison.OrdinalIgnoreCase)) return Renderer.CompShowHardware;
        return Renderer.CompShowMedia; // 媒体控制器
    }

    private void EnsureOrder()
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_orderMigrated)
            {
                _orderMigrated = true;
                foreach (var raw in _rawOrder)
                {
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

    private PluginEntry? FindEntryByKeyOrId(string value)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Key, value, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (var e in _entries)
            if (!string.IsNullOrEmpty(e.Id) && string.Equals(e.Id, value, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

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

    // ---- 加载 / 卸载 / 热重载 ----

    public bool Load(PluginEntry e) => Load(e, dedupSameId: false);

    private bool Load(PluginEntry e, bool dedupSameId)
    {
        if (e.State == PluginState.Loaded) return true;

        string? shadow = null;
        PluginLoadContext? ctx = null;

        if (e.Context != null)
        {
            var staleShadow = e.ShadowDir;
            try { Teardown(e); }
            catch (Exception ex) { Logger.Warn($"[PluginManager] 重载前清理旧上下文失败: {e.Key} — {ex.Message}"); }
            e.ShadowDir = null;
            e.Id = "";
            TryDeleteDir(staleShadow);
        }

        try
        {
            e.Error = null;
            shadow = CreateShadowCopy(e);
            ctx = new PluginLoadContext(shadow);
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

            e.Id = PluginHost.DetachString(plugin.Id);
            e.DisplayName = PluginHost.DetachString(plugin.DisplayName);
            e.Version = PluginHost.DetachString(plugin.Version);
            e.Author = PluginHost.DetachString(ReadAuthor(plugin, asm));

            CacheName(e);

        // 会把新版本刚登记进来的组件 / 设置页一并摘掉。
            if (dedupSameId) RemoveSameIdDuplicates(e);

            _host.RegisterPlugin(e.Id, e.DisplayName, e.Version);
            plugin.Initialize(_host.CreateScopedHost(e.Id));

            // 插件若实现了外部媒体会话源（见 Plugin/MediaSessionSource.cs），交给媒体链路。
            // 纯增量：媒体链路只在「当前会话是网易云 / 酷狗且 SMTC 给不出时间轴」时才去读它。
            if (plugin is IMediaSessionSource mediaSource)
            {
                MediaController.RegisterExternalSource(mediaSource);
            }
            else
            {
                // 留一条线索：插件明明实现了却走到这里，几乎都是「它自己那份接口定义遮蔽了宿主的」——
                // 比如把宿主专用的接口文件（host/MediaSessionSource.cs）一起编进了插件 dll，
                // 那样两边是不同的 Type，判等必然是 false，而且静默跳过、没有任何报错。
                Logger.Debug($"[PluginManager] {plugin.Id} 未实现 IMediaSessionSource，跳过外部媒体源登记");
            }

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

            // 失败清理：把已经建起来的加载上下文与影子目录收干净。
            if (e.Context != null)
            {
                try { Teardown(e); } catch { }
            }
            else
            {
                try { ctx?.Unload(); } catch { }
            }
            TryDeleteDir(e.ShadowDir ?? shadow);
            e.ShadowDir = null;

            e.State = PluginState.Failed;
            e.Error = ex.Message;
            Logger.Error($"[PluginManager] 加载失败: {e.Key}", ex);
            RaiseChanged();
            return false;
        }
    }

    private void RemoveSameIdDuplicates(PluginEntry loaded)
    {
        List<PluginEntry>? dups = null;
        lock (_lock)
        {
            foreach (var x in _entries)
            {
                if (ReferenceEquals(x, loaded)) continue;                                              // 就是自己
                if (string.Equals(x.Key, loaded.Key, StringComparison.OrdinalIgnoreCase)) continue;    // 同一个文件
                if (string.IsNullOrEmpty(x.Id)) continue;
                if (!string.Equals(x.Id, loaded.Id, StringComparison.OrdinalIgnoreCase)) continue;
                (dups ??= new List<PluginEntry>()).Add(x);
            }
        }
        if (dups == null) return;

        bool relocated = false;
        foreach (var dup in dups)
        {
            int pos;
            lock (_lock) pos = IndexOfOrder(dup.Key);

            Logger.Info($"[PluginManager] 导入同 Id 插件 {loaded.Id}，自动移除旧版本 {dup.Key}");
            Remove(dup); // 卸载 + 文件移入 _recycle + 摘掉顺序/隐藏项

            lock (_lock)
            {
                if (pos < 0 || ContainsOrder(loaded.Key)) continue;
                _order.Insert(Math.Min(pos, _order.Count), loaded.Key);
                relocated = true;
            }
        }
        if (relocated) SaveOrderList();
    }

    public void Unload(PluginEntry e)
    {
        var shadow = e.ShadowDir;
        e.ShadowDir = null;

        var weak = Teardown(e);
        if (weak != null)
        {
            Renderer.InvalidatePluginSnapshot();
            WaitForUnload(weak, e.Key);
        }

        TryDeleteDir(shadow);
        Logger.Info($"[PluginManager] 已卸载 {e.Key}");
        RaiseChanged();
    }

    public void ShutdownAll()
    {
        foreach (var e in Entries)
        {
            if (e.Context == null) continue;
            try
            {
                var shadow = e.ShadowDir;
                Teardown(e);          // Dispose 插件 + 注销宿主登记 + ctx.Unload + 清空字段
                e.ShadowDir = null;
                TryDeleteDir(shadow);
            }
            catch (Exception ex) { Logger.Error($"[PluginManager] 退出卸载 {e.Key} 失败", ex); }
        }
    }

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

        // 正是日志里「加载上下文暂未被回收」的根因。
        if (!string.IsNullOrEmpty(id)) _host.SetUnregistering(id, true);
        try
        {
            if (instance is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Logger.Error($"[PluginManager] 插件 Dispose 异常: {e.Key}", ex); }
            }

            // 反登记外部媒体源：插件实例持有 CDP 连接与轮询状态，热重载时旧实例会被换掉，
            // 不摘掉的话媒体链路会一直拿着一个已经作废的源。
            if (instance is IMediaSessionSource mediaSource)
                MediaController.UnregisterExternalSource(mediaSource);

            if (!string.IsNullOrEmpty(id)) _host.UnregisterPlugin(id);
        }
        finally
        {
            if (!string.IsNullOrEmpty(id)) _host.SetUnregistering(id, false);
        }

        if (ctx == null) return null;

        var weak = new WeakReference(ctx, trackResurrection: true);
        ctx.Unload();
        return weak;
    }

    private static void WaitForUnload(WeakReference weak, string key)
    {
        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }

        // 没退订的事件、没结束的线程或定时器、闭包捕获。
        if (weak.IsAlive)
        {
            Logger.Warn($"[PluginManager] {key} 的加载上下文暂未被回收：宿主侧引用已全部摘除，"
                + "多半是插件自己还握着静态状态 / 未退订的事件 / 未结束的线程或定时器（不影响重新加载）");
        }
    }

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

    public void SetEnabled(PluginEntry e, bool enabled)
    {
        if (enabled)
        {
            _disabled.Remove(e.Key);
            SaveDisabledList();

            if (_hidden.Remove(e.Key)) SaveHiddenList();

            Refresh();
            var fresh = Find(e.Key) ?? e;
            if (fresh.State != PluginState.Loaded) Load(fresh);
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

    // ---- 导入 / 移除 / 打开目录 ----

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

            if (Load(e, dedupSameId: true)) return (true, $"已导入并加载：{e.FriendlyName}");

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

    public void Remove(PluginEntry e)
    {
        Unload(e);

        lock (_lock)
        {
            _order.RemoveAll(x => string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase));
            _rawOrder.RemoveAll(x => string.Equals(x, e.Key, StringComparison.OrdinalIgnoreCase));
        }
        SaveOrderList();

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

    public void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(PluginsRoot);
            Process.Start(new ProcessStartInfo(PluginsRoot) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 打开插件目录失败", ex); }
    }

    public void OpenMarketplace()
    {
        try { Process.Start(new ProcessStartInfo(MarketplaceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("[PluginManager] 打开插件市场失败", ex); }
    }

    // ---- 内部工具 ----

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

    private void LoadNameCache()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryBase);
            var raw = key?.GetValue(NameCacheValue) as string;
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string k = line[..tab].Trim();
                string v = line[(tab + 1)..].Trim();
                if (k.Length > 0 && v.Length > 0) _nameCache[k] = v;
            }
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 读取插件显示名缓存失败", ex); }
    }

    private void SaveNameCache()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryBase);
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _nameCache)
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            key?.SetValue(NameCacheValue, sb.ToString());
        }
        catch (Exception ex) { Logger.Error("[PluginManager] 保存插件显示名缓存失败", ex); }
    }

    private void CacheName(PluginEntry e)
    {
        if (string.IsNullOrEmpty(e.Key) || string.IsNullOrWhiteSpace(e.DisplayName)) return;
        e.CachedName = e.DisplayName;
        lock (_lock)
        {
            if (_nameCache.TryGetValue(e.Key, out var old) && string.Equals(old, e.DisplayName, StringComparison.Ordinal))
                return; // 名字没变，不写注册表
            _nameCache[e.Key] = e.DisplayName;
        }
        SaveNameCache();
    }

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
