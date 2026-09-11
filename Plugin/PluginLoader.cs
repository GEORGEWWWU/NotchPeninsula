using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace NotchPeninsula.Plugins;

/// <summary>从 plugins 目录发现并加载插件 DLL。</summary>
public static class PluginLoader
{
    public static List<INotchPlugin> LoadAll(PluginHost host, string? pluginsRoot = null)
    {
        var loaded = new List<INotchPlugin>();
        pluginsRoot ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            Logger.Info($"[PluginLoader] 插件目录不存在: {pluginsRoot}");
            return loaded;
        }

        foreach (var dir in Directory.GetDirectories(pluginsRoot))
        {
            try
            {
                // 1. 读 manifest（可选，缺省用目录名 + ".dll"）
                var manifestPath = Path.Combine(dir, "plugin.json");
                string? dllName = null;
                if (File.Exists(manifestPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (doc.RootElement.TryGetProperty("dll", out var d)) dllName = d.GetString();
                }
                dllName ??= Path.GetFileName(dir) + ".dll";

                var dllPath = Path.Combine(dir, dllName);
                if (!File.Exists(dllPath)) { Logger.Warn($"[PluginLoader] 缺少 DLL: {dllPath}"); continue; }

                // 2. 用独立 AssemblyLoadContext 加载，避免污染主程序
                var alc = new AssemblyLoadContext($"plugin:{Path.GetFileName(dir)}", isCollectible: false);
                var asm = alc.LoadFromAssemblyPath(dllPath);

                // 3. 反射找 INotchPlugin 实现
                INotchPlugin? plugin = null;
                foreach (var t in asm.GetTypes())
                {
                    if (!t.IsAbstract && typeof(INotchPlugin).IsAssignableFrom(t))
                    {
                        plugin = (INotchPlugin)Activator.CreateInstance(t)!;
                        break;
                    }
                }

                if (plugin == null) { Logger.Warn($"[PluginLoader] {dllName} 未找到 INotchPlugin 实现"); continue; }

                // 4. 初始化，并绑定该插件自己的 Id（设置持久化自动加前缀）
                plugin.Initialize(host.CreateScopedHost(plugin.Id));
                loaded.Add(plugin);
                Logger.Info($"[PluginLoader] 已加载插件: {plugin.Id} ({plugin.DisplayName})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[PluginLoader] 加载插件目录失败: {Path.GetFileName(dir)}", ex);
            }
        }

        return loaded;
    }
}
