using System.IO;
using System.Reflection;

namespace NotchPeninsula;

internal static class DataResources
{
    private static readonly Assembly _asm = typeof(DataResources).Assembly;
    private static string[]? _names;

    private static string[] Names => _names ??= _asm.GetManifestResourceNames();

    private static IEnumerable<string> DiskRoots()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;

        string? up = null;
        try { up = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")); } catch { }
        if (!string.IsNullOrEmpty(up) && !string.Equals(up, baseDir, StringComparison.OrdinalIgnoreCase))
            yield return up;
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');

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
            }
        }
        return false;
    }

    internal static bool Exists(string relativePath)
        => ExistsOnDisk(relativePath, out _) || FindResource(Normalize(relativePath)) != null;

    internal static Stream? OpenRead(string relativePath)
    {
        if (ExistsOnDisk(relativePath, out string disk))
        {
            try { return File.OpenRead(disk); }
            catch
            {
            }
        }

        string? res = FindResource(Normalize(relativePath));
        if (res == null) return null;

        try { return _asm.GetManifestResourceStream(res); }
        catch { return null; }
    }

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
