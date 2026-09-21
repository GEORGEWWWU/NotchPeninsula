using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;

namespace NotchPeninsula
{
    /// <summary>
    /// 把「图标描述」解析成可直接绘制的 <see cref="SKBitmap"/>，供 HTTP 消息 / 插件提醒自定义图标。
    ///
    /// 支持四种写法，按前缀自动识别：
    ///   1. 内置别名      "qq" / "bilibili" / "chrome" ...（见 ResolveBuiltin）
    ///   2. 图片链接      "https://host/a.png" / "http://host/a.png"
    ///   3. 内联图        "data:image/png;base64,xxxx" 或纯 base64 串
    ///   4. 本地文件路径  "C:\icons\a.png" / "/path/a.png"（也接受 file:// 前缀）
    ///
    /// 任何一步失败都返回 null，调用方回退到默认的 Toast 图标。
    /// 解析结果按「描述串」缓存，同一条消息反复推送不会重复下载 / 解码。
    /// 全部方法都可以安全地在后台线程调用。
    /// </summary>
    public static class ToastIconProvider
    {
        // 岛上只画 28px，留 4x 余量足够；再大纯属浪费内存
        private const int MAX_EDGE = 112;

        // 防呆上限：发送端塞一张几十 MB 的图不至于把进程撑爆
        private const long MAX_BYTES = 4L * 1024 * 1024;
        private const int MAX_BASE64_CHARS = 6 * 1024 * 1024;

        // 缓存条目上限。单张 112×112 约 50KB，24 条 ≈ 1.2MB，可以忽略。
        private const int MAX_CACHE_ENTRIES = 24;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly object _lock = new();

        // 值允许为 null：表示「这个描述解析失败」，避免同一条坏消息每次都重试一遍
        private static readonly Dictionary<string, SKBitmap?> _cache = new(StringComparer.Ordinal);
        private static readonly Queue<string> _cacheOrder = new();

        // 别名里文件名不规则的那几个；其余按 data/image/<别名>-icon|logo.<ext> 约定自动找
        private static readonly Dictionary<string, string> _aliasFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "wintoast-icon.png",
            ["wintoast"] = "wintoast-icon.png",
            ["default"] = "wintoast-icon.png",
            ["bili"] = "bilibili-logo.png",
        };

        /// <summary>解析图标描述；失败返回 null。可安全地在后台线程调用。</summary>
        public static SKBitmap? Resolve(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return null;

            string key = spec.Trim();

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }

            SKBitmap? bmp = ResolveCore(key);

            lock (_lock)
            {
                _cache[key] = bmp;
                _cacheOrder.Enqueue(key);

                // 淘汰时【不 Dispose】：这张位图可能正被某个还没消失的 ToastData 拿着绘制，
                // 提前释放会变成 use-after-free。从字典里摘掉即可，剩下交给 GC 的终结器回收。
                while (_cacheOrder.Count > MAX_CACHE_ENTRIES)
                {
                    string oldest = _cacheOrder.Dequeue();
                    _cache.Remove(oldest);
                }
            }

            return bmp;
        }

        /// <summary>
        /// 后台解析，成功时回调。用于「先把消息按默认图标弹出来，图标解析完再补上」——
        /// 这样下载图片的耗时不会拖慢消息弹出，也不会拖慢 HTTP 响应。
        /// </summary>
        public static void ResolveInBackground(string? spec, Action<SKBitmap> onResolved)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return;

            string key = spec.Trim();
            _ = Task.Run(() =>
            {
                try
                {
                    var bmp = Resolve(key);
                    if (bmp != null)
                        onResolved(bmp);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Toast图标] 后台解析异常: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 把图标描述压成一行短日志：base64 只报长度，绝不把图片本身写进日志（会撑爆 app.log）。
        /// </summary>
        public static string Describe(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return "(未提供)";

            string s = spec.Trim();
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return $"内联图({s.Length} 字符)";
            if (s.Length > 80)
                return s.Substring(0, 77) + "...";
            return s;
        }

        private static SKBitmap? ResolveCore(string spec)
        {
            try
            {
                // 1) 内置别名：不含路径分隔符和冒号的短标识
                if (IsPlainAlias(spec))
                {
                    var builtin = ResolveBuiltin(spec);
                    if (builtin != null)
                        return builtin;
                }

                // 2) data URI
                if (spec.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = spec.IndexOf(',');
                    if (comma < 0)
                        return null;

                    string meta = spec.Substring(5, comma - 5);
                    if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
                        return null;

                    return DecodeBase64(spec.Substring(comma + 1));
                }

                // 3) 图片链接
                if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return DownloadAndDecode(spec);
                }

                // 4) 本地文件路径
                string path = spec;
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    path = new Uri(path).LocalPath;

                // 历史包袱：HTTP 入口为修「非法转义」会把反斜杠统一加倍（见 Notify/Toast.cs）。
                // 发送端若按标准 JSON 写 \\，解析出来就是双反斜杠路径，这里兜一下。
                // 最省事的写法是直接用正斜杠（C:/icons/a.png），Windows 一样认。
                if (!File.Exists(path) && path.Contains(@"\\"))
                    path = path.Replace(@"\\", @"\");

                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    return DecodeScaled(fs);
                }

                // 5) 兜底：发送端直接把 base64 当字符串塞进来的情况
                if (spec.Length >= 64 && IsLikelyBase64(spec))
                    return DecodeBase64(spec);

                Logger.Debug($"[Toast图标] 无法识别的图标描述: {Describe(spec)}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Toast图标] 解析失败 ({Describe(spec)}): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool IsPlainAlias(string s)
        {
            if (s.Length == 0 || s.Length > 32)
                return false;

            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    return false;
            }
            return true;
        }

        private static SKBitmap? ResolveBuiltin(string alias)
        {
            string? file = _aliasFiles.TryGetValue(alias, out var mapped) ? mapped : FindAliasFile(alias);
            if (file == null)
                return null;

            string full = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "image", file);
            if (!File.Exists(full))
                return null;

            using var fs = File.OpenRead(full);
            return DecodeScaled(fs);
        }

        /// <summary>
        /// 约定：data/image 下的 <c>&lt;别名&gt;-icon.*</c> / <c>&lt;别名&gt;-logo.*</c> 都能直接用别名引用。
        /// 想给某个 App 加内置图标，把 <c>wechat-icon.png</c> 丢进 data/image 即可，不用改代码。
        /// </summary>
        private static string? FindAliasFile(string alias)
        {
            string[] kinds = ["-icon", "-logo", ""];
            string[] exts = [".png", ".jpg", ".jpeg", ".bmp"];

            foreach (string kind in kinds)
            {
                foreach (string ext in exts)
                {
                    string name = alias + kind + ext;
                    if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "image", name)))
                        return name;
                }
            }
            return null;
        }

        private static SKBitmap? DownloadAndDecode(string url)
        {
            try
            {
                using var resp = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Debug($"[Toast图标] 下载失败 HTTP {(int)resp.StatusCode}: {Describe(url)}");
                    return null;
                }

                if (resp.Content.Headers.ContentLength is long declared && declared > MAX_BYTES)
                {
                    Logger.Debug($"[Toast图标] 图片过大({declared} 字节)，已跳过: {Describe(url)}");
                    return null;
                }

                using var stream = resp.Content.ReadAsStream();
                using var ms = new MemoryStream();
                byte[] buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > MAX_BYTES)
                    {
                        Logger.Debug($"[Toast图标] 图片超过 {MAX_BYTES} 字节上限，已放弃: {Describe(url)}");
                        return null;
                    }
                    ms.Write(buffer, 0, read);
                }

                ms.Position = 0;
                return DecodeScaled(ms);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Toast图标] 下载异常 ({Describe(url)}): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static SKBitmap? DecodeBase64(string b64)
        {
            if (b64.Length == 0 || b64.Length > MAX_BASE64_CHARS)
                return null;

            byte[] bytes = Convert.FromBase64String(b64);
            if (bytes.Length > MAX_BYTES)
                return null;

            using var ms = new MemoryStream(bytes, writable: false);
            return DecodeScaled(ms);
        }

        private static SKBitmap? DecodeScaled(Stream stream)
        {
            SKBitmap? raw = SKBitmap.Decode(stream);
            if (raw == null)
                return null;

            return ScaleDown(raw);
        }

        // 缩到 MAX_EDGE 以内再进缓存：一张 1024×1024 的 PNG 解出来是 4MB，
        // 而实际只画 28px，没必要常驻。
        private static SKBitmap ScaleDown(SKBitmap src)
        {
            int maxEdge = Math.Max(src.Width, src.Height);
            if (maxEdge <= MAX_EDGE || maxEdge <= 0)
                return src;

            float scale = (float)MAX_EDGE / maxEdge;
            int w = Math.Max(1, (int)MathF.Round(src.Width * scale));
            int h = Math.Max(1, (int)MathF.Round(src.Height * scale));

            var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
            }

            src.Dispose();
            return dst;
        }

        private static bool IsLikelyBase64(string s)
        {
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=' ||
                    c == '\n' || c == '\r' || c == ' ')
                    continue;
                return false;
            }
            return true;
        }
    }
}
