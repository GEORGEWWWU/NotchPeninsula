using System.IO;
using System.Text.Json;

namespace NotchPeninsula.Plugins;

public sealed class PluginSource
{
    /// <summary>相对 plugins 根目录的稳定标识，用于持久化「启用/禁用」状态。如 "HelloPlugin.dll" 或 "MyPlugin/MyPlugin.dll"。</summary>
    public string Key { get; init; } = "";

    public string DllPath { get; init; } = "";

    public bool IsFolderLayout { get; init; }

    public string RootDir { get; init; } = "";
}

///   plugins/MyPlugin/plugin.json   { "dll": "MyPlugin.dll" }
/// 以 "_" 或 "." 开头的目录会被跳过（如 _recycle 回收站）。
public static class PluginLoader
{
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
