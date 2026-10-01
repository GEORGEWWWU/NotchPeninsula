using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace NotchPeninsula
{
    /// <summary>IShellItemImageFactory::GetImage 的取图方式（只保留本项目用得到的几位）。</summary>
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

    /// <summary>
    /// 从「任意 shell 项」抠出应用图标 —— 用的是 Windows 自己给资源管理器列表用的那套：
    /// <c>SHCreateItemFromParsingName</c> + <c>IShellItemImageFactory::GetImage</c>。
    ///
    /// <para>相比 <c>ExtractIconEx</c> / <c>SHGetFileInfo</c> 的好处：同时接受 exe 完整路径与 UWP 的
    /// <c>shell:AppsFolder\&lt;AUMID&gt;</c>，一套代码通吃；返回的 HBITMAP 自带 32bpp alpha，
    /// 不用自己合成掩码位图；拿到的是 shell 按注册表 <c>DefaultIcon</c> / 应用清单解析后的**真实应用图标**，
    /// 而不是 exe 里的第一个图标资源（那常常是安装程序图标或旧版本图标）。</para>
    ///
    /// <para>任何一步失败都返回 null，由调用方回退到占位绘制 —— 本类不抛异常。</para>
    /// </summary>
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

        /// <summary>
        /// 按 shell 解析名取图标。<paramref name="parsingName"/> 可以是 exe 完整路径，
        /// 也可以是 <c>shell:AppsFolder\Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic</c>。
        /// </summary>
        /// <param name="size">期望边长（逻辑像素）—— 岛上最大只画 50px，取 32 放大也够用</param>
        public static SKBitmap? Load(string parsingName, int size = 32)
        {
            if (string.IsNullOrWhiteSpace(parsingName)) return null;

            // 显式持有 RCW 并在 finally 里放掉：SHCreateItemFromParsingName 每次都会新建一个 COM 包装，
            // 只靠 GC 回收的话，解析失败（本条路径每次都会新建一个再丢掉）会攒出一条增长曲线。
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
                // shell 解析失败 / COM 未就绪 / 图标资源异常：一律当作「没有图标」
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

        /// <summary>HBITMAP（32bpp DIB section）→ SKBitmap。任何异常返回 null。</summary>
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

            // 少数图标源没有 alpha 通道，GetDIBits 会把 alpha 全补 0 —— 直接用会整块透明，
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

    /// <summary>
    /// 媒体程序应用图标的缓存与解析：SMTC 会话的 AUMID → 该程序自己的 <see cref="SKBitmap"/>。
    ///
    /// <para><b>用途</b>（2026-10-01 起）：媒体封面不再引用 <c>data\image</c> 里的内置平台站标
    /// （资源保留，只是不再被引用），改为按会话动态取图 ——
    /// 视频模式直接用它，音乐模式把它当作网络封面与 SMTC 缩略图之后的最后兜底。</para>
    ///
    /// <para><b>三级解析</b>（按可靠性排序，逐级回退）：</para>
    /// <list type="number">
    /// <item>AUMID 本身就是可执行文件路径 —— 直接用（部分 Win32 播放器这么上报）；</item>
    /// <item>AUMID 是 UWP / MSIX 的 <c>PackageFamily!AppId</c> —— 交给 <c>shell:AppsFolder</c> 解析；</item>
    /// <item>从 AUMID 推出应用名，去进程表里找同名 exe，再取其图标 —— 常规 Win32 播放器走这条；
    ///       对不上名字时再按「词元重叠」兜底（反向域名式 AUMID 只有这一级能命中）。</item>
    /// </list>
    ///
    /// <para>返回的位图是缓存的**副本**，归调用方所有（可以被 Dispose）；
    /// 解析失败返回 null，此时调用方画占位图标。</para>
    /// </summary>
    internal static class AppIconProvider
    {
        private const int IconPixels = 32;

        /// <summary>
        /// 缓存条目上限。一个成功条目约 4KB 原生内存（32×32 BGRA），正常机器上 SMTC 见过的 AUMID
        /// 也就十来个；这个上限纯粹是防「AUMID 花样生成」那种极端输入。
        /// </summary>
        private const int CacheCap = 32;

        /// <summary>
        /// 解析失败后的重试冷却（退避）。**失败也必须记一笔**，否则每次属性刷新都会去枚举一遍进程表；
        /// 但**不能像以前那样一律等 60 秒** —— 媒体程序刚启动、或刚切歌那一刻，它的进程名常常还查不到
        /// （进程尚未完全就绪 / 权限时序），一两秒后就正常了，干等一分钟的表现就是
        /// 「有时候拿不到应用 logo」。所以：第一次失败只等 1 秒，连续失败再逐级翻倍到 30 秒上限。
        /// </summary>
        private const double MissRetryBaseSeconds = 1.0;
        private const double MissRetryMaxSeconds = 30.0;

        // 条目：Icon 为 null 表示解析失败，RetryAt 是下次允许重试的时刻（成功后为 MaxValue）。
        // 缓存的位图**永远不直接交给调用方**（只给副本），所以淘汰 / 替换时可以安全地 Dispose。
        private readonly record struct IconEntry(SKBitmap? Icon, int Misses, DateTime RetryAt);

        private static readonly Dictionary<string, IconEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> _cacheOrder = new();
        private static readonly object _gate = new();

        /// <summary>取该会话程序的应用图标副本；解析不出来返回 null。</summary>
        public static SKBitmap? Get(string? aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return null;

            // 整个缓存决策（含解析）都在锁内：Get 只在换歌 / 属性刷新时被调，解析也就几毫秒，
            // 换来的是「同一时刻只有一个线程在解析」，顺带根治并发解析下换图的竞态。
            lock (_gate)
            {
                bool known = _cache.TryGetValue(aumid, out var hit);
                if (known)
                {
                    if (hit.Icon != null) return hit.Icon.Copy();
                    if (DateTime.UtcNow < hit.RetryAt) return null; // 退避冷却中
                }

                var icon = Resolve(aumid);

                // 只在这个 AUMID「本轮第一次」失败时留痕：下次用户报「拿不到 logo」时，
                // 日志里能直接看到是哪个程序、省的又只能靠猜。退避重试期间不重复刷。
                if (icon == null && (!known || hit.Misses == 0))
                    Logger.Debug($"应用图标解析失败，{MissRetryBaseSeconds:F0}s 后重试：AUMID=[{aumid}]");

                if (!known)
                {
                    _cacheOrder.Enqueue(aumid);
                    while (_cacheOrder.Count > CacheCap)
                    {
                        string oldest = _cacheOrder.Dequeue();
                        if (_cache.Remove(oldest, out var evicted)) evicted.Icon?.Dispose();
                    }
                }
                else if (hit.Icon is { } stale && !ReferenceEquals(stale, icon))
                {
                    stale.Dispose(); // 覆盖旧条目：那张图已无人持有（Get 只发副本），放掉原生内存
                }

                if (icon != null)
                {
                    _cache[aumid] = new IconEntry(icon, 0, DateTime.MaxValue);
                    return icon.Copy();
                }

                int misses = known ? hit.Misses + 1 : 1;
                double wait = Math.Min(MissRetryBaseSeconds * Math.Pow(2, misses - 1), MissRetryMaxSeconds);
                _cache[aumid] = new IconEntry(null, misses, DateTime.UtcNow.AddSeconds(wait));
                return null;
            }
        }

        private static SKBitmap? Resolve(string aumid)
        {
            // 诊断留痕：四级全落空时把「每一级的结果」一次性写进日志。
            // 「拿不到图标」只看最终结果是查不出原因的 —— 必须知道是没找到 exe 路径，
            // 还是找到了路径但 shell 取图失败。分别对应完全不同的修法。
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

            // 2) UWP / MSIX 包标识：交给 shell:AppsFolder 解析（shell 按包清单取图标）
            if (aumid.Contains('!'))
            {
                diag += "L2=shell:AppsFolder; ";
                if (ShellIcon.Load("shell:AppsFolder\\" + aumid, IconPixels) is { } fromPackage) return fromPackage;
            }

            // 3) 按 AUMID 推出应用名 → 进程表里找同名 exe
            string exe = FindExeByAumid(aumid);
            diag += $"L3={(exe.Length == 0 ? "没找到进程" : exe)}; ";
            if (exe.Length > 0 && ShellIcon.Load(exe, IconPixels) is { } fromExe) return fromExe;

            // 4) 词元重叠兜底（exe 名与 AUMID 毫无字面关系时，比如中文 exe 名 + 反向域名 AUMID）
            string byToken = FindExeByTokenOverlap(aumid);
            diag += $"L4={(byToken.Length == 0 ? "没找到/被判并列" : byToken)}; ";
            if (byToken.Length > 0 && ShellIcon.Load(byToken, IconPixels) is { } fromToken) return fromToken;

            // L3/L4 找到了路径却走到这里 ⇒ 是 shell 取图那一步失败（路径本身没问题）
            Logger.Debug($"应用图标四级全部落空：{diag}");
            return null;
        }

        /// <summary>
        /// 按可执行文件名找该应用的 exe 路径。命中优先级：**归一化后完全同名 &gt; 互相包含（取匹配得最长的那个）**。
        /// 顺序不保证时「谁先被枚举到就算谁」会让长得像同一家的应用互相串图标，所以要显式排序。
        /// </summary>
        private static string FindExeByAumid(string aumid)
        {
            string wanted = Normalize(ProbeName(aumid));
            if (wanted.Length < 3) return "";

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return ""; }

            string? exact = null;
            string? partial = null;
            int partialLength = 0;

            try
            {
                foreach (var p in all)
                {
                    string path = TryGetPath(p);
                    if (path.Length == 0) continue;

                    bool isExact = false, isPartial = false;
                    int best = 0;

                    ScoreMatch(Normalize(p.ProcessName), wanted, ref isExact, ref isPartial, ref best);
                    ScoreMatch(Normalize(Path.GetFileNameWithoutExtension(path)), wanted, ref isExact, ref isPartial, ref best);

                    if (isExact) { exact = path; break; }
                    if (isPartial && best > partialLength) { partialLength = best; partial = path; }
                }
            }
            finally
            {
                foreach (var p in all) p.Dispose();
            }

            return exact ?? partial ?? "";
        }

        /// <summary>把候选名与目标名比对一次，更新「全等 / 包含」与包含长度。</summary>
        private static void ScoreMatch(string candidate, string wanted, ref bool isExact, ref bool isPartial, ref int bestLength)
        {
            if (candidate.Length == 0) return;

            if (candidate == wanted) { isExact = true; return; }
            if (!candidate.Contains(wanted, StringComparison.Ordinal)
                && !wanted.Contains(candidate, StringComparison.Ordinal)) return;

            // 两边必有包含关系，所以被包含的一定是较短的那个，它越长说明匹配得越具体
            isPartial = true;
            int length = Math.Min(candidate.Length, wanted.Length);
            if (length > bestLength) bestLength = length;
        }

        /// <summary>
        /// 最后一级：AUMID 与进程的「词元集合」做重叠打分，**只在唯一最高分时认**。
        /// 反向域名式 AUMID 靠它命中 —— <c>com.bilibili.bilibiliPC</c> 与
        /// <c>Program Files\bilibili\哔哩哔哩.exe</c> 共享目录名词元 bilibili。
        /// 并列最高分直接放弃：宁可没有图标，也不能挂上别的程序的图标。
        /// </summary>
        private static string FindExeByTokenOverlap(string aumid)
        {
            var wanted = Tokenize(aumid);
            if (wanted.Count == 0) return "";

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return ""; }

            string best = "";
            int bestScore = 0;
            bool tied = false;

            try
            {
                foreach (var p in all)
                {
                    string path = TryGetPath(p);
                    if (path.Length == 0) continue;

                    var tokens = Tokenize(p.ProcessName);
                    tokens.UnionWith(Tokenize(Path.GetFileNameWithoutExtension(path)));
                    string? folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) tokens.UnionWith(Tokenize(Path.GetFileName(folder)));

                    int score = 0;
                    bool strong = false; // 命中的词元里有没有一个「够特别」的（够长且不是通用词）
                    foreach (string token in tokens)
                    {
                        if (!wanted.Contains(token)) continue;
                        score++;
                        if (token.Length >= 5 && !GenericTokens.Contains(token)) strong = true;
                    }

                    // 只有一个共同词元时，它必须足够特别才算数（洛雪音乐靠 music + desktop 两个词元命中）
                    if (score == 0 || (score == 1 && !strong)) continue;

                    if (score > bestScore) { bestScore = score; best = path; tied = false; }
                    // ⚠️ 只有「分数相同、但**不是同一个 exe**」才算并列。
                    //    同一个应用常常同时跑多个进程（Electron / Chromium 的主进程 + 渲染进程 + GPU 进程…），
                    //    它们的可执行文件路径**完全一样** —— 那只是重复，不是并列。
                    //    以前这里无条件判 tied，于是这类多进程应用会**永远拿不到图标**
                    //    （实测：哔哩哔哩 PC 版 com.bilibili.bilibiliPC，日志里稳定复现）。
                    else if (score == bestScore && !string.Equals(path, best, StringComparison.OrdinalIgnoreCase))
                        tied = true;
                }
            }
            finally
            {
                foreach (var p in all) p.Dispose();
            }

            return tied ? "" : best;
        }

        /// <summary>
        /// 「通用词」：单独命中它不足以指认一个应用，必须有第二个词元一起命中才算数。
        /// 否则 Rainmeter 的文件描述里带 "desktop"，就会把 <c>cn.toside.music.desktop</c> 配到它身上 ——
        /// 那比配不上更糟：用户看到的是完全不相干的图标。
        /// </summary>
        private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
        {
            "desktop", "client", "player", "media", "video", "audio", "music",
            "application", "utility", "service", "update", "setup", "tool",
            "windows", "program", "files", "folder", "version",
        };

        /// <summary>
        /// 从 AUMID 推出一个能拿去进程表里比对的「应用名」。
        ///
        /// <para>⚠️ 不能对整串直接套 <see cref="Path.GetFileNameWithoutExtension"/>：
        /// 反向域名式 AUMID（<c>com.tencent.qqmusic</c>）会被它当成「扩展名是 qqmusic」，
        /// 截出 <c>com.tencent</c> 这种残片，拿残片去比对就会命中任意一个腾讯系进程。</para>
        ///
        /// <para>规则：带路径分隔符 → 取文件名去扩展名；反向域名式 → 取最后一段
        /// （最后一段短于 4 个字符时判定为扩展名，退回整串 —— <see cref="Normalize"/> 本来就会去掉结尾的 exe）。</para>
        /// </summary>
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

        /// <summary>切成「够长、可能承载信息」的词元：长度 ≥ 4 的字母数字片段转小写。
        /// 门槛定在 4 是为了避开 <c>com</c> / <c>exe</c> 这类无意义短片段，同时留住 <c>music</c> / <c>bilibili</c>。</summary>
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

        /// <summary>读某进程的 exe 完整路径；受保护 / 位数不匹配 / 已退出的进程读不到，一律返回空串。</summary>
        private static string TryGetPath(Process p)
        {
            try { return p.MainModule?.FileName ?? ""; }
            catch { return ""; }
        }
    }
}
