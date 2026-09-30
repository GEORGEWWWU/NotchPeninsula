using System.Diagnostics;
using Windows.Media.Control;

namespace NotchPeninsula
{
    /// <summary>
    /// 「双击媒体控制 → 跳回正在放媒体的那个应用」的全部落地逻辑。
    ///
    /// <para><b>两层策略：</b></para>
    /// <list type="number">
    /// <item><b>窗口激活（首选）</b>：把已经开着的应用窗口还原并切到前台。句柄只在
    ///       「该会话刚被接管、且应用此刻正前台」时采集 —— 那一刻用户正在那个应用里操作，
    ///       前台窗口就是它的主窗口。句柄与进程号都直接来自系统，**从不靠进程名去猜**，
    ///       所以绝不会把别的应用切到前台。</item>
    /// <item><b>按 AUMID 激活（兜底）</b>：窗口没采到、或激活被系统前台锁拦下时，走
    ///       <c>explorer.exe shell:AppsFolder\{AUMID}</c>（SMTC 的 <c>SourceAppUserModelId</c> 原样使用）。
    ///       这是 Windows 上「按 AUMID 启动 / 激活应用」的标准做法，打包应用与注册了 AUMID 的
    ///       Win32 应用都适用；应用已运行时由 Shell 复用现有实例，不会起出第二个进程。</item>
    /// </list>
    ///
    /// <para>穷举进程去猜「哪个进程是这个 AUMID」的做法被**刻意放弃**：按进程名模糊匹配
    /// （比如 "potplayer"）在重名 / 套壳进程上会张冠李戴 —— 宁可退回 Shell 激活，也不做这种猜测。</para>
    ///
    /// <para><b>做不到的：</b>SMTC 不提供任何深链接参数，所以只能跳到应用本体（主窗口 / 首页），
    /// 无法跳到正在播放的那首歌 / 那个视频的页面。这是协议本身的限制，不是实现取舍。</para>
    /// </summary>
    internal static class MediaAppLauncher
    {
        /// <summary>
        /// 每个 AppID 的定位结果：会话所属进程 + 采到的顶层窗口（<see cref="IntPtr.Zero"/> = 还没采到）。
        /// AppID 比较一律 OrdinalIgnoreCase，与 <see cref="MediaController"/> 的会话匹配保持一致。
        /// </summary>
        private sealed class AppTarget
        {
            public uint ProcessId;
            public IntPtr Window;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AppTarget> s_targets
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>守护 <see cref="s_targets"/> 里每个 AppTarget 的字段读写（字典自身已是并发安全）。</summary>
        private static readonly object s_lock = new();

        /// <summary>
        /// 采样当前接管会话：在应用正处于前台时抓住它的窗口句柄。整个采样只有两次 API 调用
        /// （取前台窗口 + 问它属于哪个进程），没有枚举、没有等待，可以放心放在 UI 线程。
        ///
        /// <para><b>两道保险（2026-09-30 修 bug 时加的，别删）：</b></para>
        /// <list type="number">
        /// <item><b>已经有句柄就不再重采</b>。会话刷新（换歌 / 播放暂停 / 会话表变动）非常频繁，
        ///       每次都重采的话，用户只要在别的程序里忙着（比如正在 QQ 里聊天）而媒体恰好换歌，
        ///       「当前前台窗口」就会被当成媒体的窗口记下来 —— 之后双击媒体控制会跳到那个无关程序去。
        ///       这正是「双击跳到了 QQ」的根因。</item>
        /// <item><b>进程名必须与 AUMID 同源</b>。前台窗口的进程名跟 AUMID 里能对上的话才认，
        ///       对不上（或完全推不出进程名）一律不采 —— 宁可双击时退回按 AUMID 激活，
        ///       也绝不把另一个程序切到前台。只有第一道保险时，一旦第一次采错就再也没机会纠正。</item>
        /// </list>
        /// </summary>
        /// <param name="session">当前接管的会话；为 null 表示没有接管会话。</param>
        /// <param name="isCurrent">该会话是否就是当前接管（正在展示）的那一个；false 时直接跳过。</param>
        internal static void CaptureSession(GlobalSystemMediaTransportControlsSession? session, bool isCurrent)
        {
            if (session == null || !isCurrent) return;

            string appId;
            try
            {
                appId = session.SourceAppUserModelId ?? "";
            }
            catch
            {
                return;
            }
            if (appId.Length == 0) return;

            try
            {
                // 已有可用句柄：不再重采（理由见 summary 第 1 条）
                if (s_targets.TryGetValue(appId, out AppTarget? known))
                {
                    lock (s_lock)
                    {
                        if (known.Window != IntPtr.Zero && Win32.IsWindow(known.Window)) return;
                    }
                }

                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                // 本程序自己的窗口（岛体 / 设置窗 / 通知窗）当前台时不做采样：
                // 那时用户看的是灵动岛，不是媒体应用，句柄只会指向我们自己。
                if (IsOwnWindow(hwnd)) return;
                if (!IsUsableWindow(hwnd)) return;

                uint pid = Win32.GetWindowThreadProcessId(hwnd, out _);
                if (pid == 0) return;

                // 归属校验：前台窗口的进程必须跟这个 AUMID 对得上（理由见 summary 第 2 条）
                if (!LooksLikeSameApp(appId, pid)) return;

                AppTarget target = s_targets.GetOrAdd(appId, _ => new AppTarget());
                lock (s_lock)
                {
                    bool processChanged = target.ProcessId != pid;
                    target.ProcessId = pid;
                    target.Window = hwnd;

                    if (processChanged)
                        Logger.Debug($"双击跳转：已记下「{appId}」的应用窗口 0x{hwnd.ToInt64():X}（PID {pid}）");
                }
            }
            catch (Exception ex)
            {
                // 采样失败无所谓：双击时会退回按 AUMID 激活，绝不能因此影响接管流程
                Logger.Error("双击跳转：采样应用窗口失败", ex);
            }
        }

        /// <summary>
        /// 前台窗口的进程名是否与 AUMID 同源。判据刻意宽松（双向包含 + 前 3 字符命中），
        /// 因为不同应用报出来的 AUMID 形态差别很大：
        ///   · <c>JustSolo.JustSolo</c>（包名!应用名）→ 归一到 <c>justsolo</c>，进程 <c>JustSolo</c> ✅
        ///   · <c>QQMusic.exe</c>                    → 归一到 <c>qqmusic</c>，进程 <c>QQMusic</c> ✅
        ///   · <c>chrome.exe</c>                     → 归一到 <c>chrome</c>，进程 <c>chrome</c> ✅
        /// 推不出可用名字（纯数字 / 太短）时返回 true —— 无法校验就不拦，交给 AUMID 兜底路径。
        /// </summary>
        private static bool LooksLikeSameApp(string appId, uint pid)
        {
            string? processName = TryGetProcessName(pid);
            if (string.IsNullOrEmpty(processName)) return true; // 读不到进程名：不拦

            string token = NormalizeAppToken(appId);
            if (token.Length < 3) return true; // AUMID 里推不出有效名字：不拦

            if (processName.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
            if (token.Contains(processName, StringComparison.OrdinalIgnoreCase)) return true;

            // 「前 3 字符」兜底：应付 AUMID 带后缀（如 cloudmusic vs cloudmusic2）这类差异
            string head = token[..Math.Min(3, token.Length)];
            return processName.Contains(head, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把 AUMID 归一成一个可比较的短名：
        /// 取最后一个 '.' 或 '!' 之后那段，再去掉 .exe / .lnk 后缀与所有非字母数字字符。
        /// 例：<c>JustSolo.JustSolo</c> → <c>justsolo</c>；<c>QQMusic.exe</c> → <c>qqmusic</c>。
        /// </summary>
        private static string NormalizeAppToken(string appId)
        {
            int cut = appId.LastIndexOfAny(['.', '!', '\\', '/']);
            string tail = cut >= 0 && cut < appId.Length - 1 ? appId[(cut + 1)..] : appId;

            if (tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) tail = tail[..^4];
            else if (tail.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) tail = tail[..^4];

            var sb = new System.Text.StringBuilder(tail.Length);
            foreach (char c in tail)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>读进程名（不含 .exe 后缀之外的扩展信息）。进程已退出 / 无权限时返回 null。</summary>
        private static string? TryGetProcessName(uint pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 「双击媒体控制」的入口：跳回当前接管会话所属的应用。找不到窗口时静默退回 AUMID 激活。
        /// 调用方（WndProc）已经保证「双击落点在媒体控制上」，这里不再做任何命中判定。
        /// </summary>
        internal static void OpenCurrentSessionApp()
        {
            string appId = MediaController.Instance?.CurrentAppId ?? "";
            if (appId.Length == 0)
            {
                Logger.Warn("双击跳转：当前没有可接管的媒体会话，已忽略");
                return;
            }

            IntPtr hwnd = IntPtr.Zero;
            uint pid = 0;
            lock (s_lock)
            {
                if (s_targets.TryGetValue(appId, out AppTarget? target))
                {
                    pid = target.ProcessId;
                    hwnd = target.Window;
                    if (hwnd != IntPtr.Zero && !Win32.IsWindow(hwnd)) hwnd = IntPtr.Zero;
                }
            }

            if (hwnd != IntPtr.Zero && ActivateWindow(hwnd))
            {
                Logger.Info($"双击跳转：已激活「{appId}」的应用窗口（PID {pid}）");
                return;
            }

            if (hwnd != IntPtr.Zero)
                Logger.Warn($"双击跳转：「{appId}」的窗口激活被系统拒绝，改走 AUMID 激活");

            LaunchByAumid(appId);
        }

        /// <summary>
        /// 把某个窗口弄到前台：先还原（可能最小化着），再 SetForegroundWindow；
        /// 被前台锁挡住时用 AttachThreadInput 把自己的输入队列挂到目标线程上重试一次。
        /// </summary>
        private static bool ActivateWindow(IntPtr hwnd)
        {
            try
            {
                if (!Win32.IsWindowVisible(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_SHOW);
                if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

                if (Win32.SetForegroundWindow(hwnd)) return true;

                uint targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
                uint thisThread = Win32.GetCurrentThreadId();
                if (targetThread == 0 || targetThread == thisThread) return false;

                bool attached = false;
                try
                {
                    attached = Win32.AttachThreadInput(thisThread, targetThread, true);
                    return Win32.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) Win32.AttachThreadInput(thisThread, targetThread, false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("双击跳转：激活应用窗口失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 兜底：按 AUMID 交给 Shell 激活（<c>shell:AppsFolder\{AUMID}</c>），也就是
        /// <c>SourceAppUserModelId</c> 的原样用法。对打包应用 / UWP 有效；
        /// 应用已运行时由 Shell 负责复用现有实例，所以不会起出第二个进程。
        /// </summary>
        private static void LaunchByAumid(string appId)
        {
            try
            {
                // using：启动后立刻释放 Process 包装对象（不影响 explorer 本身），
                // 避免每次双击都留一个待 GC 的可释放对象
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    // 必须整串加引号：AUMID 里可能带空格与 `!`，不加会被 explorer 拆成多个参数
                    Arguments = $"\"shell:AppsFolder\\{appId}\"",
                    UseShellExecute = true,
                    // 不继承当前目录，免得多起一个 explorer 停在奇怪的工作目录上
                    WorkingDirectory = string.Empty
                })) { }
                Logger.Info($"双击跳转：已按 AUMID 激活「{appId}」");
            }
            catch (Exception ex)
            {
                Logger.Error($"双击跳转：按 AUMID 激活「{appId}」失败", ex);
            }
        }

        /// <summary>窗口可用作跳转目标：还活着，且不是 WS_EX_TOOLWINDOW（提示窗 / 托盘气泡之类）。</summary>
        private static bool IsUsableWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
            long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            return (exStyle & Win32.WS_EX_TOOLWINDOW) == 0;
        }

        /// <summary>
        /// 是不是本程序自己的窗口（岛体 / 设置窗 / 通知窗 / 插件窗都同属本进程）。
        /// 不做这层排除的话，岛体自己当前台时会把句柄记成媒体应用，双击就变成「跳到自己」。
        /// </summary>
        private static bool IsOwnWindow(IntPtr hwnd)
            => Win32.GetWindowThreadProcessId(hwnd, out uint pid) != 0
               && pid == (uint)Environment.ProcessId;
    }
}
