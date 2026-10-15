using System.IO;
using System.Text;

namespace NotchPeninsula
{
    /// <summary>
    /// 系统通知的头像取证：WinRT 的投影（<c>NotificationBinding</c>）只给 GetTextElements / Hints / Template，
    /// **没有任何图片 API**，所以 toast 里的 <c>&lt;image src="..."&gt;</c> 只能去 Windows 通知平台自己的库里翻
    /// （<c>%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db</c>，新数据先在 -wal 里）。
    ///
    /// 实测 QQ(NT) 的聊天通知长这样（头像是本地文件，**没有扩展名**，按内容嗅探解码即可）：
    /// <code>
    /// &lt;toast launch="ntqq-notification://...peerName=LoveYi.;chatType=1;..."&gt;
    ///   &lt;visual&gt;&lt;binding template="ToastGeneric"&gt;
    ///     &lt;text hint-maxLines="1"&gt;LoveYi.&lt;/text&gt;
    ///     &lt;text hint-maxLines="1"&gt;头像测试&lt;/text&gt;
    ///     &lt;image placement="appLogoOverride" hint-crop="circle"
    ///            src="C:\Users\...\nt_data\avatar\user\39\s_39a71c74ea91fbc43eec913a7201ee44"/&gt;
    /// </code>
    /// 库里没有图片（QQ 群聊之外的空 src）就什么都不做，由渲染侧的预设图标兜底。
    /// </summary>
    internal static class ToastAvatarProbe
    {
        // 只读文件尾部这么多字节：通知库的增量（含刚到的通知）都在尾部，避免被几十 MB 的历史拖慢。
        private const int TailBytes = 8 * 1024 * 1024;

        // 命中标题后往前/往后各取这么多字节当 XML 窗口：一条 toast 载荷不到 2KB，够了。
        private const int WindowBytes = 6144;

        // 最多回看多少个同名候选（标题很短时可能出现很多次）
        private const int MaxCandidates = 40;

        // 通知落库可能晚一拍，隔一会儿再来一轮；最后一轮结束也就 2 秒，仍在通知显示的几秒窗口内。
        private static readonly int[] RetryDelaysMs = [0, 500, 1500];

        private static readonly string[] FileNames = ["wpndatabase.db-wal", "wpndatabase.db"];

        private static readonly Encoding[] Encodings = [Encoding.UTF8, Encoding.Unicode];

        /// <summary>后台把系统通知里的头像挂到这条通知上；不覆盖已存在的头像（插件/HTTP/MSP 给的那张优先）。</summary>
        public static void ResolveInBackground(ToastData toast)
        {
            if (toast.Avatar != null) return;

            string title = toast.Title;
            string body = toast.Body;
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;

            _ = Task.Run(() =>
            {
                try
                {
                    foreach (int delay in RetryDelaysMs)
                    {
                        if (delay > 0) Thread.Sleep(delay);
                        if (toast.Avatar != null) return;

                        string? src = FindImageSrc(title, body);
                        if (string.IsNullOrEmpty(src)) continue;

                        var bmp = Load(src);
                        if (bmp == null)
                        {
                            Logger.Debug($"[通知头像] 找到图片源但解不出来: {src}");
                            continue;
                        }

                        if (toast.Avatar == null)
                        {
                            toast.Avatar = bmp;
                            Logger.Info($"[通知头像] 已从系统通知取出头像: {src}");
                        }
                        return;
                    }

                    Logger.Debug($"[通知头像] 通知库里没找到带头像的那条（{title} / {body}）");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[通知头像] 探测异常: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        private static string? FindImageSrc(string title, string body)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications");

            foreach (string name in FileNames)
            {
                byte[]? data = ReadTail(Path.Combine(dir, name));
                if (data == null) continue;

                string? src = Scan(data, title, body);
                if (!string.IsNullOrEmpty(src)) return src;
            }
            return null;
        }

        private static byte[]? ReadTail(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long len = fs.Length;
                if (len <= 0) return null;

                long start = Math.Max(0, len - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);

                var buf = new byte[len - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return read == buf.Length ? buf : buf[..read];
            }
            catch (Exception ex)
            {
                Logger.Debug($"[通知头像] 读取通知库失败 ({Path.GetFileName(path)}): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static string? Scan(byte[] data, string title, string body)
        {
            foreach (var enc in Encodings)
            {
                byte[] needle = enc.GetBytes(title);
                if (needle.Length == 0) continue;

                // 从严到宽：先要「同一条 toast 里标题和正文都对上」，都不满足才退而只认标题。
                // （正文可能是多个 <text> 拼起来的，未必能整段命中，所以留这一档兜底。）
                string? titleOnly = null;
                var span = data.AsSpan();
                int at = span.LastIndexOf(needle);   // 从最新的一条往回找

                for (int n = 0; at >= 0 && n < MaxCandidates; n++)
                {
                    int from = Math.Max(0, at - WindowBytes);
                    int to = Math.Min(data.Length, at + needle.Length + WindowBytes);
                    string? toast = BoundedToast(enc.GetString(data, from, to - from), title);

                    if (toast != null)
                    {
                        string? src = ImageSrcOf(toast);
                        if (!string.IsNullOrWhiteSpace(src))
                        {
                            if (body.Length == 0 || IsElementText(toast, body)) return src;
                            titleOnly ??= src;
                        }
                    }

                    at = data.AsSpan(0, at).LastIndexOf(needle);
                }

                if (titleOnly != null) return titleOnly;
            }
            return null;
        }

        /// <summary>在窗口里定位「标题作为某个 &lt;text&gt; 元素正文」的那一条 toast，并**把它自己的
        /// &lt;toast&gt;...&lt;/toast&gt; 边界切出来** —— 不切的话窗口会越界抓到库里相邻下一条通知的图。</summary>
        private static string? BoundedToast(string window, string title)
        {
            int titleAt = IndexOfElementText(window, title);
            if (titleAt < 0) return null;

            int head = window.LastIndexOf("<toast", titleAt, StringComparison.OrdinalIgnoreCase);
            int tail = window.IndexOf("</toast>", titleAt, StringComparison.OrdinalIgnoreCase);
            if (head < 0 || tail < 0) return null;

            return window.Substring(head, tail + 8 - head);
        }

        /// <summary>必须是某个 &lt;text&gt; 元素的正文，不能只是子串命中 —— 否则「测试」会被「头像测试」抢走。</summary>
        private static bool IsElementText(string toast, string text) => IndexOfElementText(toast, text) >= 0;

        private static int IndexOfElementText(string toast, string text)
        {
            int i = toast.IndexOf(text + "</text>", StringComparison.Ordinal);
            return i >= 0 ? i : toast.IndexOf(Escape(text) + "</text>", StringComparison.Ordinal);
        }

        private static string? ImageSrcOf(string window)
        {
            int at = 0;
            while (true)
            {
                at = window.IndexOf("<image", at, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;

                int end = window.IndexOf('>', at);
                if (end < 0) return null;

                int s = window.IndexOf("src=", at, StringComparison.OrdinalIgnoreCase);
                if (s >= 0 && s < end)
                {
                    int q1 = window.IndexOf('"', s + 4);
                    if (q1 >= 0)
                    {
                        int q2 = window.IndexOf('"', q1 + 1);
                        if (q2 > q1)
                        {
                            string src = window.Substring(q1 + 1, q2 - q1 - 1);
                            if (!string.IsNullOrWhiteSpace(src)) return src;
                        }
                    }
                }

                at = end;   // 这一张没有 src（QQ 的空头像），看下一张
            }
        }

        private static SkiaSharp.SKBitmap? Load(string src)
        {
            src = Unescape(src.Trim());

            if (src.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
            {
                // 打包应用的资源：路径要经包容器解析，这里不做（QQ 这类桌面应用给的是绝对路径）。
                Logger.Debug($"[通知头像] 暂不支持包内资源地址: {src}");
                return null;
            }

            return ToastIconProvider.Resolve(src);   // 本地路径（无扩展名也行）/ http(s) 都由它处理
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

        private static string Unescape(string s) => s
            .Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&apos;", "'")
            .Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">");
    }
}
