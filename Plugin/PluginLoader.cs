using System.IO;
using System.Text.Json;

namespace NotchPeninsula.Plugins;

/// <summary>一个待加载的插件来源（磁盘上的 DLL）。</summary>
public sealed class PluginSource
{
    /// <summary>相对 plugins 根目录的稳定标识，用于持久化“启用/禁用”状态。例如 "HelloPlugin.dll" 或 "MyPlugin/MyPlugin.dll"。</summary>
    public string Key { get; init; } = "";

    /// <summary>入口 DLL 的绝对路径。</summary>
    public string DllPath { get; init; } = "";

    /// <summary>true=目录型插件（会复制整个目录，支持带依赖）；false=根目录下的单文件插件。</summary>
    public bool IsFolderLayout { get; init; }

    /// <summary>插件所在目录：目录型为插件文件夹，单文件型为 plugins 根目录。</summary>
    public string RootDir { get; init; } = "";
}

/// <summary>
/// 插件发现器：扫描 plugins 目录，产出所有可加载的 DLL 来源。
///
/// 支持的两种目录布局：
///   plugins/HelloPlugin.dll            单文件型（适合无外部依赖的简单插件）
///   plugins/MyPlugin/MyPlugin.dll      目录型（可携带依赖 DLL，可用 plugin.json 指定入口）
///   plugins/MyPlugin/plugin.json       { "dll": "MyPlugin.dll" }
///
/// 以 "_" 或 "." 开头的目录会被跳过（例如 _recycle 回收站）。
/// </summary>
public static class PluginLoader
{
    // 明显属于宿主/通用依赖的 DLL 名，目录型插件在自动挑选入口时应跳过它们
    private static readonly HashSet<string> _notPluginDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "NotchPeninsula", "SkiaSharp", "SkiaSharp.NativeAssets.Win32", "NAudio.Core", "NAudio.Wasapi",
        "System.Text.Json", "Newtonsoft.Json", "Microsoft.Win32.SystemEvents"
    };

    public static List<PluginSource> Discover(string pluginsRoot)
    {
        var list = new List<PluginSource>();
        if (!Directory.Exists(pluginsRoot)) return list;

        // 1. 根目录下的单文件插件
        foreach (var dll in Directory.GetFiles(pluginsRoot, "*.dll"))
        {
            list.Add(new PluginSource
            {
                Key = Path.GetFileName(dll),
                DllPath = dll,
                IsFolderLayout = false,
                RootDir = pluginsRoot
            });
        }

        // 2. 子目录型插件
        foreach (var dir in Directory.GetDirectories(pluginsRoot))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('_') || folder.StartsWith('.')) continue; // _recycle 等内部目录

            var dll = PickEntryDll(dir, folder);
            if (dll == null) { Logger.Warn($"[PluginLoader] 目录中未找到可加载的 DLL: {dir}"); continue; }

            list.Add(new PluginSource
            {
                Key = $"{folder}/{Path.GetFileName(dll)}",
                DllPath = dll,
                IsFolderLayout = true,
                RootDir = dir
            });
        }

        return list;
    }

    /// <summary>按优先级挑选入口 DLL：plugin.json 指定 → 与目录同名 → 目录内首个非通用 DLL。</summary>
    private static string? PickEntryDll(string dir, string folderName)
    {
        // a) plugin.json 显式指定
        var manifest = Path.Combine(dir, "plugin.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("dll", out var d) && d.GetString() is { Length: > 0 } name)
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                    Logger.Warn($"[PluginLoader] plugin.json 指定的 DLL 不存在: {p}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PluginLoader] 解析 plugin.json 失败: {manifest} — {ex.Message}");
            }
        }

        // b) 与目录同名
        var sameName = Path.Combine(dir, folderName + ".dll");
        if (File.Exists(sameName)) return sameName;

        // c) 目录内首个非通用 DLL
        return Directory.GetFiles(dir, "*.dll")
            .FirstOrDefault(f => !_notPluginDlls.Contains(Path.GetFileNameWithoutExtension(f)));
    }
}
