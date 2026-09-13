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
        pluginsRoot ??= Path.Combine(GetAppDirectory(), "plugins");
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
                var alc = new PluginLoadContext(dir);
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
                host.RegisterPlugin(plugin.Id, plugin.DisplayName);
                plugin.Initialize(host.CreateScopedHost(plugin.Id));
                loaded.Add(plugin);
                Logger.Info($"[PluginLoader] 已加载插件: {plugin.Id} ({plugin.DisplayName}), 设置页={host.SettingsPages.Count}");
            }
            catch (Exception ex)
            {
                if (ex is System.Reflection.ReflectionTypeLoadException rtle)
                {
                    foreach (var le in rtle.LoaderExceptions)
                        if (le != null) Logger.Error($"[PluginLoader] 类型加载失败: {le.Message}");
                }
                Logger.Error($"[PluginLoader] 加载插件目录失败: {Path.GetFileName(dir)}", ex);
            }
        }

        return loaded;
    }

    /// <summary>exe 所在目录：单文件发布时 AppContext.BaseDirectory 是临时解压目录，插件必须放 exe 同级，故优先取进程自身路径。</summary>
    private static string GetAppDirectory()
    {
        var exe = Environment.ProcessPath;
        // 通过 dotnet NotchPeninsula.dll 启动时，进程路径是 dotnet.exe，此时回退到程序集目录
        if (!string.IsNullOrEmpty(exe) &&
            !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>插件加载上下文：优先从插件目录加载依赖，否则回退默认解析。</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public PluginLoadContext(string dir) : base($"plugin:{Path.GetFileName(dir)}", isCollectible: false)
        {
            _dir = dir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(_dir, assemblyName.Name + ".dll");
            if (File.Exists(path))
                return LoadFromAssemblyPath(path);
            return null; // 回退默认解析（主程序及其依赖）
        }
    }
}
