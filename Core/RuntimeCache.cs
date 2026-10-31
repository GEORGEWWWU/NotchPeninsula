using System;
using System.IO;

namespace NotchPeninsula
{
    // 单文件发布（PublishSingleFile + IncludeAllContentForSelfExtract）会把程序自己解包到
    //     %TEMP%\.net\<程序名>\<bundle-hash>\
    // 再从那跑。每换一个 exe 就多一个目录，而旧目录**永远不会被回收** ——
    // 实测这台机器上堆到 79 个目录 / 1.45 GB，全在用户 Temp 里，且没有任何界面能看到它。
    //
    // 这里只做两件事：算占用、删「不是当前这次运行」的那些目录。
    // 当前目录一律跳过：里面的 dll 全被映射着，删不掉，也不该删。
    internal static class RuntimeCache
    {
        /// <summary>
        /// 当前这次运行用的解包目录；非单文件模式（比如开发时从 bin 直接跑）返回 null。
        /// </summary>
        public static string? CurrentDir()
        {
            try
            {
                string dir = AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (dir.Length == 0) return null;

                var cur = new DirectoryInfo(dir);
                // 只认 "<...>\Temp\.net\<名>\<hash>" 这一层：往上两级的目录名必须是 .net。
                // 这样开发环境（bin\Release\...）不会把旁边的东西当成缓存。
                if (cur.Parent?.Parent == null) return null;
                if (!string.Equals(cur.Parent.Parent.Name, ".net", StringComparison.OrdinalIgnoreCase)) return null;
                return cur.FullName;
            }
            catch { return null; }
        }

        /// <summary>解包缓存根目录（同级所有 &lt;hash&gt; 目录的父目录）；非单文件模式返回 null。</summary>
        public static string? Root()
        {
            var cur = CurrentDir();
            if (cur == null) return null;
            try { return Path.GetDirectoryName(cur); } catch { return null; }
        }

        /// <summary>整个缓存根目录的占用（字节）。</summary>
        public static long MeasureRoot()
        {
            var root = Root();
            if (root == null) return 0;
            try { return MeasureDir(new DirectoryInfo(root)); } catch { return 0; }
        }

        /// <summary>清掉「不是当前这次运行」的解包目录，返回（删掉的目录数, 释放字节数）。</summary>
        public static (int Dirs, long Bytes) Clean()
        {
            var cur = CurrentDir();
            var root = Root();
            if (cur == null || root == null) return (0, 0);

            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(root).GetDirectories(); }
            catch (Exception ex)
            {
                Logger.Warn($"[缓存] 枚举解包目录失败：{ex.Message}");
                return (0, 0);
            }

            int dirs = 0;
            long bytes = 0;
            foreach (var d in subs)
            {
                if (string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), cur,
                        StringComparison.OrdinalIgnoreCase))
                    continue;   // 正在用的这份

                long size = MeasureDir(d);
                try
                {
                    d.Delete(true);
                    dirs++;
                    bytes += size;
                }
                catch (Exception ex)
                {
                    // 被另一个实例占着 / 杀软锁着：跳过这一个，别让整次清理作废
                    Logger.Debug($"[缓存] 跳过 {d.Name}：{ex.Message}");
                }
            }

            Logger.Info($"[缓存] 清理解包缓存：{dirs} 个目录，{FormatSize(bytes)}");
            return (dirs, bytes);
        }

        private static long MeasureDir(DirectoryInfo d)
        {
            long sum = 0;
            try
            {
                foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { sum += f.Length; } catch { }
                }
            }
            catch { }
            return sum;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024.0:F0} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
