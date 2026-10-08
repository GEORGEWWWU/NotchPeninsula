using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace NotchPeninsula
{
    [Flags]
    internal enum ShellImageFlags : uint
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
        ScaleUp = 0x100,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        void GetImage(Win32.SIZE size, ShellImageFlags flags, out IntPtr phbm);
    }

    internal static class ShellIcon
    {
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        public static SKBitmap? Load(string parsingName, int size = 32)
        {
            if (string.IsNullOrWhiteSpace(parsingName)) return null;

            IShellItemImageFactory? comRef = null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
                if (hr < 0 || factory == null) return null;
                comRef = factory;

                factory.GetImage(new Win32.SIZE(size, size),
                    ShellImageFlags.IconOnly | ShellImageFlags.BiggerSizeOk, out IntPtr hbm);
                if (hbm == IntPtr.Zero) return null;

                try { return FromHBitmap(hbm); }
                finally { Win32.DeleteObject(hbm); }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (comRef != null)
                {
                    try { Marshal.FinalReleaseComObject(comRef); } catch { }
                }
            }
        }

        private static SKBitmap? FromHBitmap(IntPtr hbm)
        {
            var bm = new Win32.BITMAP();
            if (Win32.GetObject(hbm, Marshal.SizeOf<Win32.BITMAP>(), ref bm) == 0) return null;

            int w = bm.bmWidth;
            int h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;

            var info = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // 负高度 = 自上而下的行序，省得再翻一遍
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                }
            };

            var buffer = new byte[w * h * 4];
            IntPtr hdc = Win32.CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (Win32.GetDIBits(hdc, hbm, 0, (uint)h, buffer, ref info, DIB_RGB_COLORS) == 0) return null;
            }
            finally
            {
                Win32.DeleteDC(hdc);
            }

            // 这里扫一遍，全 0 就按不透明处理。
            bool hasAlpha = false;
            for (int i = 3; i < buffer.Length; i += 4)
            {
                if (buffer[i] != 0) { hasAlpha = true; break; }
            }
            if (!hasAlpha)
            {
                for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
            }

            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(buffer, 0, bitmap.GetPixels(), buffer.Length);
            return bitmap;
        }
    }

    internal static class AppIconProvider
    {
        private const int IconPixels = 32;

        private const int CacheCap = 32;

        private const double MissRetryBaseSeconds = 1.0;
        private const double MissRetryMaxSeconds = 30.0;

        // 期间再来问就只回 null，绝不排队第二个。
        private readonly record struct IconEntry(SKBitmap? Icon, int Misses, DateTime RetryAt, bool Resolving);

        private static readonly Dictionary<string, IconEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> _cacheOrder = new();
        private static readonly object _gate = new();

        public static SKBitmap? Get(string? aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return null;

            lock (_gate)
            {
                if (_cache.TryGetValue(aumid, out var hit))
                {
                    if (hit.Icon != null) return hit.Icon.Copy();
                    if (hit.Resolving || DateTime.UtcNow < hit.RetryAt) return null;

                    _cache[aumid] = hit with { Resolving = true };   // 先占位再排队，避免并发重复解析
                }
                else
                {
                    _cacheOrder.Enqueue(aumid);
                    while (_cacheOrder.Count > CacheCap)
                    {
                        string oldest = _cacheOrder.Dequeue();
                        if (_cache.Remove(oldest, out var evicted)) evicted.Icon?.Dispose();
                    }
                    _cache[aumid] = new IconEntry(null, 0, DateTime.UtcNow.AddSeconds(MissRetryBaseSeconds), true);
                }
            }

            StartResolve(aumid);
            return null;
        }

        private static void StartResolve(string aumid)
        {
            _ = Task.Run(() =>
            {
                SKBitmap? icon = null;
                try { icon = Resolve(aumid); }
                catch (Exception ex) { Logger.Debug($"应用图标解析异常（忽略）：{ex.Message}"); }

                lock (_gate)
                {
                    bool known = _cache.TryGetValue(aumid, out var cur);

                    if (icon != null)
                    {
                        if (known && cur.Icon is { } stale && !ReferenceEquals(stale, icon)) stale.Dispose();
                        _cache[aumid] = new IconEntry(icon, 0, DateTime.MaxValue, false);
                        return;
                    }

                    int misses = known ? cur.Misses + 1 : 1;
                    double wait = Math.Min(MissRetryBaseSeconds * Math.Pow(2, misses - 1), MissRetryMaxSeconds);
                    _cache[aumid] = new IconEntry(null, misses, DateTime.UtcNow.AddSeconds(wait), false);

                    if (!known || cur.Misses == 0)
                        Logger.Debug($"应用图标解析失败，{wait:F0}s 后重试：AUMID=[{aumid}]");
                }
            });
        }

        private static SKBitmap? Resolve(string aumid)
        {
            string diag = "";

            // 1) AUMID 本身就是可执行文件路径
            if (aumid.Contains('\\') || aumid.Contains('/'))
            {
                if (!File.Exists(aumid)) diag += "L1=AUMID是路径但文件不存在; ";
                else
                {
                    diag += $"L1={aumid}; ";
                    if (ShellIcon.Load(aumid, IconPixels) is { } fromPath) return fromPath;
                }
            }

            if (aumid.Contains('!'))
            {
                diag += "L2=shell:AppsFolder; ";
                if (ShellIcon.Load("shell:AppsFolder\\" + aumid, IconPixels) is { } fromPackage) return fromPackage;
            }

            var procs = SnapshotProcesses();

            string exe = FindExeByAumid(procs, aumid);
            diag += $"L3={(exe.Length == 0 ? "没找到进程" : exe)}; ";
            if (exe.Length > 0 && ShellIcon.Load(exe, IconPixels) is { } fromExe) return fromExe;

            string byToken = FindExeByTokenOverlap(procs, aumid);
            diag += $"L4={(byToken.Length == 0 ? "没找到/被判并列" : byToken)}; ";
            if (byToken.Length > 0 && ShellIcon.Load(byToken, IconPixels) is { } fromToken) return fromToken;

            Logger.Debug($"应用图标四级全部落空：{diag}");
            return null;
        }

        private readonly record struct ProcCandidate(string Path, string Name);

        private static List<ProcCandidate> SnapshotProcesses()
        {
            var list = new List<ProcCandidate>(256);

            Process[] all;
            try { all = Process.GetProcesses(); }   // 一次性快照，本身很快
            catch { return list; }

            try
            {
                var budget = System.Diagnostics.Stopwatch.StartNew();
                foreach (var p in all)
                {
                    if (budget.ElapsedMilliseconds > SnapshotBudgetMs)
                    {
                        Logger.Debug($"应用图标：进程表扫描超出 {SnapshotBudgetMs}ms 预算，提前收工（AUMID 可能匹配不到）");
                        break;
                    }

                    int pid;
                    try { pid = p.Id; } catch { continue; }

                    string path = Win32.TryGetProcessImagePath(pid);
                    if (path.Length == 0) continue;

                    list.Add(new ProcCandidate(path, Path.GetFileNameWithoutExtension(path)));
                }
            }
            finally
            {
                foreach (var p in all) p.Dispose();
            }

            return list;
        }

        private const int SnapshotBudgetMs = 250;

        private static string FindExeByAumid(List<ProcCandidate> procs, string aumid)
        {
            string wanted = Normalize(ProbeName(aumid));
            if (wanted.Length < 3) return "";

            string? exact = null;
            string? partial = null;
            int partialLength = 0;

            foreach (var p in procs)
            {
                bool isExact = false, isPartial = false;
                int best = 0;

                ScoreMatch(Normalize(p.Name), wanted, ref isExact, ref isPartial, ref best);
                ScoreMatch(Normalize(Path.GetFileNameWithoutExtension(p.Path)), wanted, ref isExact, ref isPartial, ref best);

                if (isExact) { exact = p.Path; break; }
                if (isPartial && best > partialLength) { partialLength = best; partial = p.Path; }
            }

            return exact ?? partial ?? "";
        }

        private static void ScoreMatch(string candidate, string wanted, ref bool isExact, ref bool isPartial, ref int bestLength)
        {
            if (candidate.Length == 0) return;

            if (candidate == wanted) { isExact = true; return; }
            if (!candidate.Contains(wanted, StringComparison.Ordinal)
                && !wanted.Contains(candidate, StringComparison.Ordinal)) return;

            isPartial = true;
            int length = Math.Min(candidate.Length, wanted.Length);
            if (length > bestLength) bestLength = length;
        }

        private static string FindExeByTokenOverlap(List<ProcCandidate> procs, string aumid)
        {
            var wanted = Tokenize(aumid);
            if (wanted.Count == 0) return "";

            string best = "";
            int bestScore = 0;
            bool tied = false;

            foreach (var p in procs)
            {
                var tokens = Tokenize(p.Name);
                tokens.UnionWith(Tokenize(Path.GetFileNameWithoutExtension(p.Path)));
                string? folder = Path.GetDirectoryName(p.Path);
                if (!string.IsNullOrEmpty(folder)) tokens.UnionWith(Tokenize(Path.GetFileName(folder)));

                int score = 0;
                bool strong = false; // 命中的词元里有没有一个「够特别」的（够长且不是通用词）
                foreach (string token in tokens)
                {
                    if (!wanted.Contains(token)) continue;
                    score++;
                    if (token.Length >= 5 && !GenericTokens.Contains(token)) strong = true;
                }

                if (score == 0 || (score == 1 && !strong)) continue;

                if (score > bestScore) { bestScore = score; best = p.Path; tied = false; }
                // 只有「分数相同、但不是同一个 exe」才算并列。
                else if (score == bestScore && !string.Equals(p.Path, best, StringComparison.OrdinalIgnoreCase))
                    tied = true;
            }

            return tied ? "" : best;
        }

        private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
        {
            "desktop", "client", "player", "media", "video", "audio", "music",
            "application", "utility", "service", "update", "setup", "tool",
            "windows", "program", "files", "folder", "version",
        };

        private static string ProbeName(string aumid)
        {
            if (aumid.Contains('\\') || aumid.Contains('/'))
                return Path.GetFileNameWithoutExtension(aumid);

            int lastDot = aumid.LastIndexOf('.');
            if (lastDot >= 0 && aumid.Length - lastDot - 1 >= 4) return aumid[(lastDot + 1)..];
            return aumid;
        }

        /// <summary>去掉空格 / 连字符 / 点 / 结尾扩展名后转小写 —— "Microsoft Edge" → "microsoftedge"、"Chrome.exe" → "chrome"。</summary>
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));

            if (sb.Length > 3 && sb.ToString().EndsWith("exe", StringComparison.Ordinal)) sb.Length -= 3;
            return sb.ToString();
        }

        private static HashSet<string> Tokenize(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            int start = -1;

            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && char.IsLetterOrDigit(text[i]))
                {
                    if (start < 0) start = i;
                    continue;
                }

                if (start < 0) continue;
                var word = text.AsSpan(start, i - start);
                if (word.Length >= 4) set.Add(word.ToString().ToLowerInvariant());
                start = -1;
            }

            return set;
        }
    }
}
