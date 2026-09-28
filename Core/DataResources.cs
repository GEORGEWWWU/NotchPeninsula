using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NotchPeninsula;

/// <summary>
/// <c>data\</c> 内置资源的统一解析入口 —— **磁盘优先、嵌入兜底**。
///
/// 🔑 为什么需要它：单文件发布（<c>PublishSingleFile</c>）只把托管程序集与原生库打进 exe，
///    <c>Content</c> 文件依旧会作为散文件留在 exe 旁边。也就是说「单文件 exe」其实并不自足 ——
///    一旦把 exe 单独拷走（或用户清掉了 data 目录），内置提示音与内置图标会全部消失。
///    因此 <c>data\</c> 下的资源**同时**以 <c>EmbeddedResource</c> 打进 exe，由本类统一解析：
///      1. exe 同级目录 —— <see cref="AppContext.BaseDirectory"/>；单文件发布时它**就是 exe 所在目录**
///         （已实测：只有原生库会被解压到 <c>%TEMP%\.net\</c>，BaseDirectory 不受影响），
///         所以「往 data\sound 丢个 wav 就能用」的老玩法照旧有效，且优先级最高（用户可覆盖内置音）
///      2. 仓库根 —— 开发期从仓库直接跑 / <c>dotnet run</c> 时的兜底
///      3. exe 内部的嵌入资源 —— 任何情况下都在，单文件 exe 单独拷走也能出声、出图
///
/// ⚠️ 嵌入资源的名字**就是**相对路径（csproj 里用 <c>LogicalName</c> 固定，例如
///    <c>data/sound/QQ.wav</c>），因此本类所有 API 统一收「相对路径」，一律用正斜杠。
/// </summary>
internal static class DataResources
{
    private static readonly Assembly _asm = typeof(DataResources).Assembly;
    private static string[]? _names;

    /// <summary>exe 内全部嵌入资源名（惰性读取，只取一次）。</summary>
    private static string[] Names => _names ??= _asm.GetManifestResourceNames();

    /// <summary>磁盘候选根目录：exe 同级 → 仓库根（开发期兜底）。</summary>
    private static IEnumerable<string> DiskRoots()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;

        string? up = null;
        try { up = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")); } catch { }
        if (!string.IsNullOrEmpty(up) && !string.Equals(up, baseDir, StringComparison.OrdinalIgnoreCase))
            yield return up;
    }

    /// <summary>把相对路径规范成统一形式（正斜杠、无前导斜杠）。</summary>
    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>磁盘上是否存在（含开发期兜底路径）；命中时给出完整路径。</summary>
    internal static bool ExistsOnDisk(string relativePath, out string fullPath)
    {
        fullPath = "";
        string rel = Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar);

        foreach (string root in DiskRoots())
        {
            try
            {
                string p = Path.Combine(root, rel);
                if (File.Exists(p)) { fullPath = p; return true; }
            }
            catch
            {
                // 路径非法 / 权限异常：换下一个候选根，绝不因为一个根读不了就放弃
            }
        }
        return false;
    }

    /// <summary>是否存在（磁盘或 exe 内嵌资源）。</summary>
    internal static bool Exists(string relativePath)
        => ExistsOnDisk(relativePath, out _) || FindResource(Normalize(relativePath)) != null;

    /// <summary>
    /// 打开一个内置资源用于读取：**磁盘优先**，找不到就回落到 exe 内的嵌入资源。
    /// 返回 null 表示两处都没有。调用方负责 Dispose。
    /// </summary>
    internal static Stream? OpenRead(string relativePath)
    {
        if (ExistsOnDisk(relativePath, out string disk))
        {
            try { return File.OpenRead(disk); }
            catch
            {
                // 磁盘文件被占用 / 权限不足 → 继续尝试嵌入资源，能读到就不算失败
            }
        }

        string? res = FindResource(Normalize(relativePath));
        if (res == null) return null;

        try { return _asm.GetManifestResourceStream(res); }
        catch { return null; }
    }

    /// <summary>整块读文本（磁盘优先、嵌入兜底）。两处都没有或读取失败时返回 null。</summary>
    internal static string? ReadAllText(string relativePath)
    {
        try
        {
            using var s = OpenRead(relativePath);
            if (s == null) return null;
            using var sr = new StreamReader(s);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// 列出某个目录下**嵌入资源**里的文件名（不含目录，按名称排序）。
    ///
    /// 只用于「内置音列表」这类必须知道 exe 里到底带了哪些文件的场景 ——
    /// 磁盘目录可能整个不存在（单文件 exe 被单独拷走），那时列表就得从 exe 内部取。
    /// </summary>
    /// <param name="relativeFolder">相对目录，如 <c>data/sound</c>。</param>
    /// <param name="suffix">扩展名过滤，如 <c>.wav</c>；大小写不敏感。</param>
    internal static IReadOnlyList<string> ListEmbedded(string relativeFolder, string suffix)
    {
        string prefix = Normalize(relativeFolder).TrimEnd('/') + "/";
        var result = new List<string>();

        foreach (string name in Names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            string tail = name[prefix.Length..];
            if (tail.Length == 0 || tail.Contains('/')) continue;           // 只要本层文件
            if (!tail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(tail);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// 嵌入资源名匹配：先按 <c>LogicalName</c> 精确匹配（正常路径），
    /// 再退化成「按点分隔的后缀匹配」以容忍命名空间变化（万一哪天 LogicalName 被去掉）。
    /// </summary>
    private static string? FindResource(string normalizedPath)
    {
        foreach (string n in Names)
            if (string.Equals(n, normalizedPath, StringComparison.OrdinalIgnoreCase))
                return n;

        string dotted = normalizedPath.Replace('/', '.');
        foreach (string n in Names)
            if (n.EndsWith(dotted, StringComparison.OrdinalIgnoreCase))
                return n;

        return null;
    }
}
