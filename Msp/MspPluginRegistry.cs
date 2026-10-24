// 插件接入 MSP 的唯一登记处：插件注册的工具 / 声明的信号都先落在这里，
// 由本类负责落到 MspNode 上（命名、前缀、去重、清单推送）。
//
// 为什么要有这一层（而不是让插件直接拿 MspNode）：
//   · 命名要统一加前缀 `notchpeninsula_<id>_<tool>`。插件名与工具名都可能带
//     `.` / 大写 / 中文，而 MSP 的工具名文法是 ^[a-z0-9_-]+$（见 schema 的 tool_name），
//     规范化这一步必须只有一处，否则同一个插件会因为入口不同注册出两个名字。
//   · 插件能在运行中装卸，而 MspProtocol 没有「移除工具」的 API —— 撤销只能做到
//     「从清单里摘掉 + 宿主不再承认」，这件事必须有单一记账点。
//   · 与 Python SDK 的语义对齐：注册即推送清单（node.py 的 _push_manifest），
//     批量装配时压住推送（defer_manifest），对端靠 manifest_update 当场刷新。
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Msp.Core;

namespace NotchPeninsula;

public static partial class MspPluginRegistry
{
    /// <summary>工具名前缀。取节点 id，天然不和宿主自带的 ping / notify / media_* 相撞。</summary>
    public const string ToolPrefix = "notchpeninsula_";

    /// <summary>插件信号的域前缀。插件只能给末段动作名，宿主补前半截。</summary>
    private const string SignalDomain = "plugin";

    // MSP 的两条文法（schema/…/field-index.json）：工具名不含点，信号路径可以用点分段。
    [GeneratedRegex(@"^[a-z0-9_-]+$")]
    private static partial Regex ToolNameGrammar();

    [GeneratedRegex(@"^[a-z0-9_-]+(\.[a-z0-9_-]+)*$")]
    private static partial Regex PathGrammar();

    private sealed class Entry
    {
        public required string PluginId { get; init; }
        public required string Tool { get; init; }       // 插件给的逻辑名（未规范化）
        public required string FinalName { get; init; }  // notchpeninsula_<id>_<tool>
        public required string Description { get; init; }
        public required Delegate Handler { get; init; }
        public JsonElement? InputSchema { get; init; }
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Entry> _tools = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string PluginId, string Description)> _signals = new(StringComparer.Ordinal);

    private static MspNode? _node;
    private static int _deferDepth;

    /// <summary>
    /// 挂到节点上（MspNotchBridge.Start 里在 node.Start() **之前**调用）。
    ///
    /// 在 Start 之前挂，是为了让已登记的插件工具进入**握手时的那份清单**；
    /// 之后的注册则靠 manifest_update 增量推送。
    /// </summary>
    public static void Attach(MspNode node)
    {
        List<Entry> tools;
        List<KeyValuePair<string, (string PluginId, string Description)>> signals;
        lock (_gate)
        {
            _node = node;
            tools = _tools.Values.ToList();
            signals = _signals.ToList();
        }

        using (DeferPush())
        {
            foreach (var e in tools) PublishTool(e);
            foreach (var s in signals) PublishSignalDeclaration(s.Key, s.Value.Description);
        }

        Logger.Info($"[MSP] 插件接口已挂载：{tools.Count} 个工具、{signals.Count} 条信号");
    }

    /// <summary>摘下来（MspNotchBridge.Stop 里调用）。注册表**保留**，下次 Start 重新挂上。</summary>
    public static void Detach()
    {
        lock (_gate) _node = null;
    }

    // ---- 注册：工具 ----

    /// <summary>
    /// 注册一个工具。返回的 IDisposable 用于单独撤销这一条。
    /// <paramref name="handler"/> 是**原样**交给 SDK 的委托 —— 不套包装，
    /// 因为 SDK 从委托签名生成 inputSchema（参数名 / 默认值 → 可选参数），
    /// 包一层 Expression 会把默认值弄丢，语法糖的收益就没了。
    /// </summary>
    public static IDisposable RegisterTool(
        string pluginId, string tool, string description, Delegate handler, JsonElement? inputSchema = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var entry = MakeEntry(pluginId, tool, description, handler, inputSchema);

        bool publish;
        lock (_gate)
        {
            if (_tools.TryGetValue(entry.FinalName, out var existing))
            {
                if (!string.Equals(existing.PluginId, entry.PluginId, StringComparison.OrdinalIgnoreCase))
                {
                    // 跨插件撞名：**拒绝**，不做自动加后缀 —— 后缀会让清单随装载顺序变化，
                    // 对端缓存更不可预测。谁占了就写明是谁。
                    Logger.Warn($"[MSP] 插件 {pluginId} 的工具 {entry.FinalName} 注册被拒："
                                + $"该名称已属于插件 {existing.PluginId}");
                    return EmptyRegistration.Instance;
                }
                // 同插件同名：覆盖（插件重载 / 热替换的正常路径）
                Logger.Debug($"[MSP] 插件 {pluginId} 重新注册工具 {entry.FinalName}，按覆盖处理");
            }
            _tools[entry.FinalName] = entry;
            publish = _node != null;
        }

        if (publish)
        {
            PublishTool(entry);
            Push();
        }
        return new ToolRegistration(entry.FinalName, entry.PluginId);
    }

    /// <summary>
    /// 扫一个对象上所有 <c>[MspTool]</c> 标了的方法并注册（语法糖入口）。
    /// 名字 = 前缀 + 插件 id + （attribute 的 Name ?? 方法名）。
    /// </summary>
    public static IDisposable RegisterToolsFrom(string pluginId, object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var disposables = new List<IDisposable>();
        using (DeferPush())
        {
            foreach (var m in instance.GetType()
                         .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.GetCustomAttribute<MspToolAttribute>() is not { } attr) continue;

                // 用 CreateDelegate 而不是 lambda：委托的目标要指向**原方法**，
                // SDK 才能从它的参数元数据里读出默认值（可选参数）与参数名。
                Delegate? handler;
                try
                {
                    var sig = m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType).ToArray();
                    handler = m.CreateDelegate(Expression.GetDelegateType(sig), instance);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[MSP] 插件 {pluginId} 的方法 {m.Name} 无法转成委托，跳过：{ex.Message}");
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(attr.Name) ? m.Name : attr.Name!;
                disposables.Add(RegisterTool(pluginId, name, attr.Description ?? "", handler));
            }
        }

        Logger.Info($"[MSP] 插件 {pluginId} 通过 [MspTool] 注册了 {disposables.Count} 个工具");
        return new CompositeRegistration(disposables);
    }

    // ---- 注册：信号 ----

    /// <summary>声明一条插件信号。插件只给末段动作名，宿主补 <c>plugin.&lt;id&gt;.</c>。</summary>
    public static IDisposable DeclareSignal(string pluginId, string action, string description)
    {
        var path = SignalPath(pluginId, action);
        bool publish;
        lock (_gate)
        {
            _signals[path] = (pluginId, description);
            publish = _node != null;
        }
        if (publish)
        {
            PublishSignalDeclaration(path, description);
            Push();
        }
        return new SignalRegistration(path, pluginId);
    }

    /// <summary>
    /// 以插件身份发布一条信号。路径是**完整路径**，宿主原样转发、暂时不做管控 ——
    /// 但入口只有这一处，将来要加白名单 / 限流 / 审计，改这里即可，插件不用动。
    /// </summary>
    public static void PublishSignal(string path, object? payload)
    {
        if (!PathGrammar().IsMatch(path))
        {
            Logger.Warn($"[MSP] 插件发布被拒：信号路径 {path} 不合 MSP 文法 ^[a-z0-9_-]+(\\.[a-z0-9_-]+)*$");
            return;
        }

        var node = _node;
        if (node == null)
        {
            Logger.Debug($"[MSP] 插件发布 {path}：MSP 未启动，丢弃");
            return;
        }

        try { node.Publish(path, payload); }
        catch (Exception ex) { Logger.Warn($"[MSP] 插件发布 {path} 失败：{ex.Message}"); }
    }

    // ---- 撤销 ----

    /// <summary>插件卸载时调用：摘掉它全部工具与信号。</summary>
    public static void RemovePlugin(string pluginId)
    {
        List<string> tools;
        List<string> signals;
        lock (_gate)
        {
            tools = _tools.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                          .Select(kv => kv.Key).ToList();
            signals = _signals.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                              .Select(kv => kv.Key).ToList();
            foreach (var t in tools) _tools.Remove(t);
            foreach (var s in signals) _signals.Remove(s);
        }

        if (tools.Count == 0 && signals.Count == 0) return;

        // 清单能摘，协议层的 handler 摘不掉（MspProtocol 没有移除 API，与 Python 同）——
        // 所以撤销的实际语义是「清单里不再宣告 + 宿主不再承认」，对端重取清单即可看到消失。
        var node = _node;
        if (node != null)
        {
            foreach (var t in tools) { try { node.Manifest.RemoveTool(t); } catch { /* 摘不掉就算了 */ } }
            foreach (var s in signals) { try { node.Manifest.RemoveSignal(s); } catch { /* 同上 */ } }
            Push();
        }

        Logger.Info($"[MSP] 插件 {pluginId} 已摘除：{tools.Count} 个工具、{signals.Count} 条信号");
    }

    /// <summary>批量装配期间压住清单推送，退出时统一推一次（对应 Python 的 defer_manifest）。</summary>
    public static IDisposable DeferPush()
    {
        lock (_gate) _deferDepth++;
        return new DeferScope();
    }

    // ---- 内部 ----

    private static void Push()
    {
        MspNode? node;
        lock (_gate)
        {
            if (_deferDepth > 0) return;
            node = _node;
        }
        if (node == null) return;

        try { node.Protocol.PushManifest(node.Manifest.ToElement()); }
        catch (Exception ex) { Logger.Warn($"[MSP] 清单推送失败：{ex.Message}"); }
    }

    private static void PublishTool(Entry e)
    {
        var node = _node;
        if (node == null) return;
        try { node.Tool(e.FinalName, e.Handler, e.Description, e.InputSchema); }
        catch (Exception ex) { Logger.Warn($"[MSP] 注册工具 {e.FinalName} 失败：{ex.Message}"); }
    }

    private static void PublishSignalDeclaration(string path, string description)
    {
        var node = _node;
        if (node == null) return;
        try { node.DeclareSignal(path, null, description); }
        catch (Exception ex) { Logger.Warn($"[MSP] 声明信号 {path} 失败：{ex.Message}"); }
    }

    private static Entry MakeEntry(
        string pluginId, string tool, string description, Delegate handler, JsonElement? inputSchema)
    {
        var final = FinalName(pluginId, tool);
        if (!ToolNameGrammar().IsMatch(final))
        {
            // 规范化之后仍然不合文法，说明插件 id / 工具名里有规范化也救不回来的东西。
            throw new ArgumentException(
                $"工具名 {final} 不合 MSP 文法 ^[a-z0-9_-]+$（原始：插件 {pluginId} / 工具 {tool}）");
        }
        return new Entry
        {
            PluginId = pluginId,
            Tool = tool,
            FinalName = final,
            Description = description,
            Handler = handler,
            InputSchema = inputSchema,
        };
    }

    /// <summary>`notchpeninsula_&lt;id&gt;_&lt;tool&gt;`，两段各自规范化。</summary>
    public static string FinalName(string pluginId, string tool)
        => ToolPrefix + Normalize(pluginId) + "_" + Normalize(tool);

    /// <summary>插件信号的完整路径：<c>plugin.&lt;id&gt;.&lt;action&gt;</c>。</summary>
    public static string SignalPath(string pluginId, string action)
        => $"{SignalDomain}.{Normalize(pluginId)}.{Normalize(action)}";

    /// <summary>转小写，并把不属于 [a-z0-9_-] 的字符换成下划线（Python 侧没有这层，是因为它不强制前缀）。</summary>
    private static string Normalize(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? ch : '_');
        return sb.ToString();
    }

    // ---- IDisposable 三种形态 ----

    private sealed class DeferScope : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool push;
            lock (_gate) { _deferDepth--; push = _deferDepth == 0; }
            if (push) Push();
        }
    }

    private sealed class ToolRegistration(string finalName, string pluginId) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool removed;
            lock (_gate)
            {
                removed = _tools.TryGetValue(finalName, out var e)
                          && string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                          && _tools.Remove(finalName);
            }
            if (!removed) return;
            if (_node != null) { try { _node.Manifest.RemoveTool(finalName); } catch { } Push(); }
        }
    }

    private sealed class SignalRegistration(string path, string pluginId) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool removed;
            lock (_gate)
            {
                removed = _signals.TryGetValue(path, out var s)
                          && string.Equals(s.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                          && _signals.Remove(path);
            }
            if (!removed) return;
            if (_node != null) { try { _node.Manifest.RemoveSignal(path); } catch { } Push(); }
        }
    }

    private sealed class CompositeRegistration(List<IDisposable> items) : IDisposable
    {
        public void Dispose() { foreach (var d in items) { try { d.Dispose(); } catch { } } }
    }

    private sealed class EmptyRegistration : IDisposable
    {
        public static readonly EmptyRegistration Instance = new();
        public void Dispose() { }
    }
}
