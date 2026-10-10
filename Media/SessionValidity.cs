using System.Diagnostics;
using System.Text;

namespace NotchPeninsula
{
    // 「僵尸会话」判定：客户端关掉视频后不注销 SMTC 会话，系统媒体控件里就留着一个
    // 点不动的假媒体，岛体也跟着一直挂在上面。
    //
    // 消费侧没有任何 API 能移除别人的会话（ClearAll 属于发布方那套 DisplayUpdater），
    // 所以这里只做「宿主内部判失效并忽略」，不去动系统状态 —— 观感一致，也不会误伤别的程序。
    //
    // 判据全部来自实测（独立探针 220+ 帧，覆盖大窗/小窗 × 播放/暂停、关闭视频、最小化）：
    //   · SMTC 时间轴恒为 0、LastUpdatedTime 是脏值（08:00:00）→ 时间维度不可用；
    //   · 该进程在所有音频设备上都没有 WASAPI 会话 → 音频维度不可用；
    //   · PlaybackInfo.Controls 关闭视频后一位不改（只有播放↔暂停会翻 play/pause）→ 控制位不可用；
    //   · 唯一有效信号：该进程是否还有「可见」窗口，其标题与 SMTC 标题匹配。
    //     关闭视频时视频窗口只是 IsWindowVisible=false、标题原样保留 —— 所以必须判可见性，
    //     只比标题会永远匹配上，等于永远判不出残留。
    internal static class SessionValidity
    {
        // 启用该判定的 App（SMTC appId 关键字）。只放「已确认会残留」的客户端，别顺手扩大范围：
        // 判错的代价是岛体突然空掉，而这类客户端毕竟是少数。
        private static readonly string[] TrackedAppIds = ["bilibili"];

        // 对应客户端的进程名（按名字收 pid 用）。B站 PC 客户端的 ProcessName 就是中文「哔哩哔哩」。
        private static readonly string[] ClientProcessNames = ["哔哩哔哩", "bilibili"];

        // 连续多少次「找不到匹配窗口」才判残留。换视频的瞬间 SMTC 会短暂断档（实测 1 帧），
        // 单次判定会误伤；采样 250ms 一跳，2 次约 0.5 秒 —— 这同时就是用户感知到的清理延迟。
        private const int StaleConfirmTicks = 2;

        private static readonly object _gate = new();

        private static readonly Dictionary<string, int> _streak = new();

        // pid 集合按名字枚举一次约几毫秒，没必要每秒重来
        private static uint[] _clientPids = [];

        private static DateTime _clientPidsAt = DateTime.MinValue;

        private const double ClientPidCacheSec = 3;

        public static bool IsTrackedApp(string? appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            foreach (string key in TrackedAppIds)
                if (appId.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 判定某个会话是否为残留。内部做迟滞：连续 StaleConfirmTicks 次拿不到匹配窗口才返回 true，
        // 任何一次拿到都立即清零。
        public static bool IsStale(string appId, string mediaTitle)
        {
            if (!IsTrackedApp(appId)) return false;
            if (mediaTitle.Length == 0) return false;   // 没有标题就无从比对，宁可不动

            string key = appId.ToLowerInvariant();

            if (HasVisibleWindowWithTitle(mediaTitle))
            {
                lock (_gate) _streak.Remove(key);
                return false;
            }

            lock (_gate)
            {
                int n = _streak.TryGetValue(key, out int v) ? v + 1 : 1;
                _streak[key] = n;
                return n >= StaleConfirmTicks;
            }
        }

        // 播放中的会话直接算活着 —— 这是判据唯一的豁免口，也是迟滞计数的复位点。
        public static void NoteAlive(string appId)
        {
            if (appId.Length == 0) return;
            lock (_gate) _streak.Remove(appId.ToLowerInvariant());
        }

        public static void Reset()
        {
            lock (_gate) _streak.Clear();
        }

        private static bool HasVisibleWindowWithTitle(string mediaTitle)
        {
            string want = Normalize(mediaTitle);
            if (want.Length < 2) return true;   // 标题太短没法可靠比对：当作「判不出来」，不判残留

            uint[] pids = ClientPids();
            if (pids.Length == 0) return false;   // 客户端的进程都没了

            bool hit = false;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (hit) return false;
                // 关键：关闭视频后窗口还在、标题也在，只是不可见。不看 visibility 就永远判不出来。
                if (!Win32.IsWindowVisible(hwnd)) return true;
                if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0) return true;
                if (Array.IndexOf(pids, owner) < 0) return true;

                string title = WindowTitle(hwnd);
                if (title.Length == 0) return true;

                string have = Normalize(title);
                if (have.Length == 0) return true;
                if (have.Contains(want)) { hit = true; return false; }
                return true;
            }, IntPtr.Zero);

            return hit;
        }

        private static uint[] ClientPids()
        {
            var now = DateTime.UtcNow;
            if ((now - _clientPidsAt).TotalSeconds < ClientPidCacheSec) return _clientPids;

            var list = new List<uint>(4);
            foreach (string name in ClientProcessNames)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        list.Add((uint)p.Id);
                        p.Dispose();
                    }
                }
                catch
                {
                    // 权限不足的进程枚举失败不影响判定，跳过即可
                }
            }

            _clientPids = list.Count == 0 ? Array.Empty<uint>() : list.ToArray();
            _clientPidsAt = now;
            return _clientPids;
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            int len = Win32.GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // 归一化：抹掉空白与标点符号、统一小写，再做包含比对。
        // 实测 SMTC 标题与视频窗口标题完全一致，这里只是兜住「- 哔哩哔哩」类后缀与全角空格。
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
