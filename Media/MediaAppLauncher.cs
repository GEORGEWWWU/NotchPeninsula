using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Windows.Media.Control;

namespace NotchPeninsula
{
    /// <summary>
    /// 「双击封面 → 跳回正在放媒体的那个应用」的全部落地逻辑。
    ///
    /// 目标身份只有一个来源：正在展示的那个会话。
    /// MediaController.CurrentAppId 就是当前接管（画在岛上）的会话的
    /// SourceAppUserModelId，本类的一切定位都以它为起点，绝不去猜「哪个进程像这个应用」。
    ///
    /// 三步定位（自上而下，一步都不成就不做事）：
    /// 已采集到的窗口句柄：把那个窗口还原并切到前台。句柄只在「该会话刚被接管、
    ///       且前台窗口确实属于这个 App」时采集；激活前还会再复核一次归属 —— 句柄可能已被系统回收，
    ///       PID 也可能被复用给了别的程序。
    /// 按可执行文件名精确找窗口：AUMID 里带的消息源文件名就是进程名
    ///       （QQMusic.exe → 进程 QQMusic），按全等取进程，再枚举它自己的顶层窗口。
    ///       不做任何模糊匹配：名字对不上就是不认。
    /// 按 AUMID 交给 Shell 激活：explorer.exe shell:AppsFolder\{AUMID}，
    /// 也就是 SourceAppUserModelId 的原样用法。但必须先确认这个 AUMID 注册过
    ///       （IsRegisteredAumid）：没注册时 Shell 不报错，而是打开一个资源管理器窗口
    ///       —— 那正是「跳转跳到了文件资源管理器」的成因。未注册时改为直接拉起同名进程自己的 exe 路径。
    ///
    /// 做不到的：SMTC 不提供任何深链接参数，所以只能跳到应用本体（主窗口 / 首页），
    /// 无法跳到正在播放的那首歌 / 那个视频的页面。这是协议本身的限制，不是实现取舍。
    /// </summary>
    internal static class MediaAppLauncher
    {
        /// <summary>
        /// 每个 AppID 的定位结果：会话所属进程 + 采到的顶层窗口（IntPtr.Zero = 还没采到）。
        /// AppID 比较一律 OrdinalIgnoreCase，与 MediaController 的会话匹配保持一致。
        /// </summary>
        private sealed class AppTarget
        {
            public uint ProcessId;
            public IntPtr Window;
            /// <summary>
            /// 采到窗口那一刻顺手记下的 exe 完整路径。这是「应用已经关了还能把它拉起来」的关键：
            /// 进程一旦退出，Process.MainModule 就再也读不到路径了，届时第 3 步会陷入
            /// 「既没 AUMID 注册、又拿不到路径」的死局（实测就是这样，双击完全没反应）。
            /// 路径在进程活着的时候取一次、一直留着，代价是一个字符串。
            /// </summary>
            public string? ExePath;
        }

        private static readonly ConcurrentDictionary<string, AppTarget> s_targets
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>守护 s_targets 里每个 AppTarget 的字段读写（字典自身已是并发安全）。</summary>
        private static readonly object s_lock = new();

        /// <summary>
        /// 上一次采样针对的会话 AppID：只在接管目标真的换了（换应用 / 换平台）时才重新采样。
        /// 会话刷新（换歌 / 播放暂停 / 会话表变动）非常频繁，每次都采的话，用户只要在别的程序里忙着
        /// （比如正在 QQ 里聊天）而媒体恰好换歌，「当前前台窗口」就会被当成媒体的窗口记下来 ——
        /// 之后双击封面就会跳到那个无关程序去。这正是「双击跳到了 QQ」的根因，别把这道闸门删了。
        /// </summary>
        private static string s_sampledAppId = "";

        /// <summary>登记表清理计数：每若干次跳转顺手回收一次已失效的条目，避免长期运行只增不减。</summary>
        private static int s_openCount;

        /// <summary>
        /// 采样当前接管会话：在应用正处于前台时抓住它的窗口句柄。整个采样只有「取前台窗口 + 问它属于哪个进程」
        /// 两次 API 调用，没有枚举、没有等待，可以放心放在 UI 线程。
        ///
        /// 三道保险（改这段之前先读完，每条都对应一个真实踩过的坑）：
        ///   1. 只在接管目标真的换了的时候采样（见 s_sampledAppId）。
        ///      否则用户正在别的程序里忙着的时候，媒体一换歌就会把那个无关程序的前台窗口记成媒体窗口。
        ///   2. 前台窗口的进程名必须与 AUMID 里的文件名完全相等（IsProcessOfApp）。
        ///      对不上、或推不出进程名，一律不采 —— 宁可双击时走第 2 步按进程找窗口，
        ///      也绝不把另一个程序切到前台。
        ///   3. 激活前还要复核一次（见 OpenCurrentSessionApp 第 1 步）：
        ///      句柄可能已被回收、PID 也可能被复用，不复核就等于闭着眼睛按句柄切前台。
        /// </summary>
        /// <param name="session">当前接管的会话；为 null 表示没有接管会话。</param>
        internal static void CaptureSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (session == null) return;

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

            // 保险 1：接管目标没变就不重采；变了就把旧记录作废（新目标一定不是旧窗口）
            if (string.Equals(appId, s_sampledAppId, StringComparison.OrdinalIgnoreCase)) return;
            s_sampledAppId = appId;
            s_targets.TryRemove(appId, out _);

            try
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                // 本程序自己的窗口（岛体 / 设置窗 / 通知窗 / 插件窗）当前台时不做采样：
                // 那时用户看的是灵动岛，不是媒体应用，句柄只会指向我们自己。
                if (IsOwnWindow(hwnd)) return;
                if (!IsUsableWindow(hwnd)) return;

                uint pid = Win32.GetWindowThreadProcessId(hwnd, out _);
                if (pid == 0) return;

                // 保险 2：归属校验 —— 前台窗口的进程必须就是 AUMID 指向的那个可执行文件
                if (!IsProcessOfApp(appId, pid)) return;

                var target = s_targets.GetOrAdd(appId, _ => new AppTarget());
                lock (s_lock)
                {
                    target.ProcessId = pid;
                    target.Window = hwnd;
                    // 进程还活着时顺手把 exe 路径记下来：应用关掉之后再双击，就靠它把应用拉回来
                    // （进程一退，Process.MainModule 就读不到路径了，那时第 3 步会无路可走）
                    if (string.IsNullOrEmpty(target.ExePath)) target.ExePath = TryGetProcessImagePath(pid);
                }
                // 用 Info 而不是 Debug：老版本只在调试模式下记这一行，导致「跳转跳错」时
                // 日志里看不到到底把哪个句柄记下来了，只能靠猜。这行只在接管目标变化时出现，不刷屏。
                Logger.Info($"媒体跳转：已记下「{appId}」的应用窗口 0x{hwnd.ToInt64():X}（PID {pid}）");
            }
            catch (Exception ex)
            {
                // 采样失败无所谓：双击时会退回按进程找窗口 / 按 AUMID 激活，绝不能因此影响接管流程
                Logger.Error("媒体跳转：采样应用窗口失败", ex);
            }
        }

        /// <summary>
        /// 「双击封面」的入口：跳回当前正在展示的那个会话所属的应用。
        /// 三步依次尝试，全都不成就不做事（绝不去激活一个不属于它的窗口）。
        /// 调用方（WndProc）已经保证「双击落点在封面上」，这里不再做任何命中判定。
        /// </summary>
        internal static void OpenCurrentSessionApp()
        {
            string appId = MediaController.Instance?.CurrentAppId ?? "";
            if (appId.Length == 0)
            {
                Logger.Warn("媒体跳转：当前没有可接管的媒体会话，已忽略");
                return;
            }

            // 顺手回收：登记表很小（一般 1~3 条），每 16 次跳转扫一遍把死掉的条目清掉
            if (++s_openCount % 16 == 0) PruneDeadTargets();

            // 它已经在前台就不用再折腾一次：用户双击的动机是「把它调到前面来」，
            // 已经在前面时再激活只会重排一次焦点。这条同时让「连点两下」变成幂等操作。
            IntPtr front = Win32.GetForegroundWindow();
            if (front != IntPtr.Zero && IsProcessOfApp(appId, Win32.GetWindowThreadProcessId(front, out _)))
            {
                Logger.Info($"媒体跳转：「{appId}」已在前台，无需激活");
                return;
            }

            // 第 1 步：已采集到的窗口
            IntPtr hwnd = IntPtr.Zero;
            uint pid = 0;
            lock (s_lock)
            {
                if (s_targets.TryGetValue(appId, out AppTarget? target))
                {
                    pid = target.ProcessId;
                    hwnd = target.Window;
                }
            }

            if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd))
            {
                // 复核归属：句柄可能已被回收，PID 也可能被复用给了别的程序
                if (IsProcessOfApp(appId, Win32.GetWindowThreadProcessId(hwnd, out _)))
                {
                    if (ActivateWindow(hwnd))
                    {
                        Logger.Info($"媒体跳转：已激活「{appId}」的应用窗口（PID {pid}）");
                        return;
                    }
                    Logger.Warn($"媒体跳转：「{appId}」的窗口激活被系统拒绝，改按进程找窗口");
                }
                else
                {
                    Logger.Warn($"媒体跳转：「{appId}」缓存的窗口 0x{hwnd.ToInt64():X} 已不属于该应用，已作废");
                    lock (s_lock)
                    {
                        if (s_targets.TryGetValue(appId, out AppTarget? stale))
                        {
                            stale.Window = IntPtr.Zero;
                            stale.ProcessId = 0;
                        }
                    }
                }
            }

            // 第 2 步：按可执行文件名精确找窗口
            // AUMID 里的文件名就是进程名（QQMusic.exe → QQMusic），按全等取进程，不做模糊匹配。
            string exeName = ExeNameOf(appId);
            if (TryFindAppWindow(exeName, out IntPtr found, out uint foundPid))
            {
                if (ActivateWindow(found))
                {
                    Logger.Info($"媒体跳转：已按进程定位并激活「{appId}」的窗口（PID {foundPid}）");
                    return;
                }
                Logger.Warn($"媒体跳转：「{appId}」定位到的窗口激活被系统拒绝");
            }

            // 第 3 步：把应用本体拉起来
            // 上一版在这里加了「AUMID 未注册就不交给 Shell」的闸门，
            // 结果把唯一还能用的那条路也堵死了 —— Just Solo 这类应用只在开始菜单注册了带 AUMID 的快捷方式
            // （`shell:AppsFolder\{AUMID}` 正是靠它解析的，实测 `Just Solo.lnk` 就带着这个 AUMID），
            // 注册表里查不到 → 判定「未注册」→ 一旦应用已经关闭（进程没了，读不到 exe 路径）就彻底没反应。
            // 现在按可靠性排序：
            //   · 有 exe 路径（采集时缓存 / 从还活着的进程读到）→ 直接拉起它本人，最确定；
            //   · 否则 → 交给 Shell 按 AUMID 激活，也就是「双击跳回应用」原先一直在用、也确实能用的方式。
            if (TryGetExePath(appId, exeName, out string? exePath))
            {
                LaunchByExe(appId, exePath);
                return;
            }

            LaunchByAumid(appId);
        }

        /// <summary>
        /// 取这个应用的 exe 路径，两条来源按可靠性排序：
        ///   1. 采集时缓存的路径 —— 会话刚被接管、进程还在的时候记下来的，进程退出后依然有效
        ///      （这是「应用关了也能把它拉起来」的关键：进程一退出 Process.MainModule 就再也读不到路径）；
        ///   2. 从当前还活着的同名进程读（进程名全等才认）。
        /// 都没有就返回 false，调用方转去按 AUMID 交给 Shell。
        /// </summary>
        private static bool TryGetExePath(string appId, string exeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? exePath)
        {
            exePath = null;

            lock (s_lock)
            {
                if (s_targets.TryGetValue(appId, out AppTarget? target)
                    && !string.IsNullOrEmpty(target.ExePath)
                    && File.Exists(target.ExePath))
                {
                    exePath = target.ExePath;
                    return true;
                }
            }

            return TryGetProcessImagePath(exeName, out exePath);
        }

        /// <summary>
        /// 从 AUMID 取出可执行文件名（不含扩展名），作为进程名使用。这是确定性提取，不是猜测：
        ///   · QQMusic.exe → QQMusic
        ///   · chrome.exe → chrome
        ///   · JustSolo.JustSolo（包标识）→ JustSolo
        ///   · PotPlayerMini64.exe → PotPlayerMini64
        /// 取最后一个 '!' / '\' / '/' 之后那段再去掉扩展名与标点。
        /// 分隔符里不含 '.'：把点当分隔符会让 "QQMusic.exe" 变成 "exe"。
        /// </summary>
        private static string ExeNameOf(string appId)
        {
            int cut = appId.LastIndexOfAny(['!', '\\', '/']);
            string tail = cut >= 0 && cut < appId.Length - 1 ? appId[(cut + 1)..] : appId;

            if (tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || tail.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                tail = tail[..^4];
            }

            var sb = new System.Text.StringBuilder(tail.Length);
            foreach (char c in tail)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// 这个进程是不是 AUMID 指向的那个应用 —— 全等比较（都归一成不含扩展名、只留字母数字的形式）。
        /// 做成全等是刻意的：任何模糊匹配（包含 / 前缀 / 相似度）都可能在同类软件之间张冠李戴，
        /// 而这个方法的返回值直接决定「要不要把一个窗口切到前台」，容不得猜。
        /// 读不到进程名（进程已退出 / 无权限）时一律返回 false。
        /// </summary>
        private static bool IsProcessOfApp(string appId, uint pid)
        {
            string exeName = ExeNameOf(appId);
            if (exeName.Length == 0) return false;

            try
            {
                using var p = Process.GetProcessById((int)pid);
                return string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按可执行文件名全等取进程，再枚举它自己的顶层窗口（按「有标题 + 可见」优先，
        /// EnumWindows 本身按 Z 序返回，所以取到的是它最近用过的那个窗口）。找不到返回 false。
        /// </summary>
        private static bool TryFindAppWindow(string exeName, out IntPtr found, out uint foundPid)
        {
            found = IntPtr.Zero;
            foundPid = 0;
            if (exeName.Length == 0) return false;

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(exeName);
            }
            catch (Exception ex)
            {
                Logger.Error($"媒体跳转：按进程名「{exeName}」取进程列表失败", ex);
                return false;
            }

            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    uint pid = (uint)candidates[i].Id;
                    if (pid == (uint)Environment.ProcessId) continue;
                    // GetProcessesByName 已按名字过滤，这里再全等确认一次（它的匹配规则比全等宽）
                    if (!string.Equals(candidates[i].ProcessName, exeName, StringComparison.OrdinalIgnoreCase)) continue;

                    IntPtr hwnd = FindTopLevelWindow(pid);
                    if (hwnd == IntPtr.Zero) continue;

                    found = hwnd;
                    foundPid = pid;
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"媒体跳转：按进程「{exeName}」找窗口失败", ex);
            }
            finally
            {
                foreach (var p in candidates) p.Dispose();
            }

            return found != IntPtr.Zero;
        }

        /// <summary>从指定进程读它的 exe 完整路径；进程已退出 / 无权限读到时返回 null。</summary>
        private static string? TryGetProcessImagePath(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                string? path = p.MainModule?.FileName;
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 取该应用的 exe 路径（第 3 步「直接拉起」用）。进程名全等才认，
        /// 所以拿到的路径一定属于这个应用，不会拉起别的程序。
        /// </summary>
        private static bool TryGetProcessImagePath(string exeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? exePath)
        {
            exePath = null;
            if (exeName.Length == 0) return false;

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(exeName);
            }
            catch
            {
                return false;
            }

            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (!string.Equals(candidates[i].ProcessName, exeName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        string? path = candidates[i].MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            exePath = path;
                            return true;
                        }
                    }
                    catch
                    {
                        // 该进程读不到模块信息（权限 / 已退出）：试下一个
                    }
                }
            }
            finally
            {
                foreach (var p in candidates) p.Dispose();
            }

            return false;
        }

        /// <summary>
        /// 在某个进程的顶层窗口里挑一个能当前台目标的：可见、非 WS_EX_TOOLWINDOW、
        /// 且带标题（标题为空的多半是隐藏的消息窗 / 宿主窗，切上去等于什么都没发生）。
        /// EnumWindows 按 Z 序枚举，所以这里天然优先返回最靠前的那个窗口。
        /// </summary>
        private static IntPtr FindTopLevelWindow(uint pid)
        {
            IntPtr found = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != pid) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true;
                if (!IsUsableWindow(hwnd)) return true;

                if (Win32.GetWindowTextLength(hwnd) > 0)
                {
                    found = hwnd;
                    return false; // 有标题的窗口就是它了，停止枚举
                }

                // 没标题的先记着：万一遍历完都没有带标题的，至少还有个可见窗口可用
                if (fallback == IntPtr.Zero) fallback = hwnd;
                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero ? found : fallback;
        }

        /// <summary>
        /// 这个 AUMID 在系统里注册过吗（注册过才敢交给 shell:AppsFolder 激活）。
        /// 两条注册表路径覆盖两类应用，都很便宜、只读不写（HKCU / HKLM 都查，打包应用通常落在 HKLM）：
        ///   · {HK??}\Software\Classes\AppUserModelId\{AUMID} —— 自己注册了 AUMID 的 Win32 应用
        ///     （键名就是完整的 AUMID）；
        ///   · {HK??}\Software\Classes\ActivatableClasses\Package\{包族名}… —— 打包应用（UWP / MSIX），
        ///     它的 AUMID 形如 PackageFamilyName!AppId。
        /// 两条都查不到时返回 false：此时 Shell 会退化成「打开资源管理器」而不是应用，
        /// 所以宁可走「直接拉起它自己的 exe」那条保守路径。
        /// </summary>
        private static bool IsRegisteredAumid(string appId)
        {
            try
            {
                if (HasAumidKey(Registry.CurrentUser, appId)) return true;
                if (HasAumidKey(Registry.LocalMachine, appId)) return true;

                // 打包应用：按 '!' 前半段（包族名）找包键，再确认它下面真的有可激活类 —— 不做全表模糊匹配
                int bang = appId.IndexOf('!');
                string family = bang > 0 ? appId[..bang] : appId;

                if (HasPackagedApp(Registry.CurrentUser, family)) return true;
                if (HasPackagedApp(Registry.LocalMachine, family)) return true;
            }
            catch (Exception ex)
            {
                // 注册表读不了（权限 / 策略）时保守返回 false：宁可这次不跳，也不打开一个资源管理器窗口
                Logger.Error($"媒体跳转：查询 AUMID「{appId}」注册信息失败", ex);
            }
            return false;
        }

        /// <summary>该注册表配置单元下是否存在这个 AUMID 的注册项。</summary>
        private static bool HasAumidKey(RegistryKey hive, string appId)
        {
            using RegistryKey? key = hive.OpenSubKey($@"Software\Classes\AppUserModelId\{appId}");
            return key != null;
        }

        /// <summary>该配置单元下是否存在这个包族名对应的打包应用注册项。</summary>
        private static bool HasPackagedApp(RegistryKey hive, string family)
        {
            using RegistryKey? packages = hive.OpenSubKey(@"Software\Classes\ActivatableClasses\Package");
            if (packages == null) return false;

            // 包键名是「包族名_版本_架构_发布者哈希」，所以只能按前缀找（数量是几十条级别，且只在兜底路径上跑）
            foreach (string sub in packages.GetSubKeyNames())
            {
                if (!sub.StartsWith(family, StringComparison.OrdinalIgnoreCase)) continue;
                using RegistryKey? classes = packages.OpenSubKey($@"{sub}\ActivatableClassId");
                if (classes != null && classes.GetSubKeyNames().Length > 0) return true;
            }
            return false;
        }

        /// <summary>顺手回收登记表里窗口已失效 / 进程已不在的条目（每 16 次跳转一次，代价可忽略）。</summary>
        private static void PruneDeadTargets()
        {
            foreach (var kv in s_targets)
            {
                IntPtr hwnd;
                uint pid;
                lock (s_lock)
                {
                    hwnd = kv.Value.Window;
                    pid = kv.Value.ProcessId;
                }

                bool dead = hwnd == IntPtr.Zero
                    || !Win32.IsWindow(hwnd)
                    || !IsProcessOfApp(kv.Key, pid);

                if (dead) s_targets.TryRemove(kv.Key, out _);
            }
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
                Logger.Error("媒体跳转：激活应用窗口失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 按 AUMID 交给 Shell 激活（shell:AppsFolder\{AUMID}），也就是 SourceAppUserModelId
        /// 的原样用法。调用前必须确认它已注册（见 IsRegisteredAumid），
        /// 否则 Shell 会打开一个资源管理器窗口而不是应用。
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
                Logger.Info($"媒体跳转：已按 AUMID 激活「{appId}」");
            }
            catch (Exception ex)
            {
                Logger.Error($"媒体跳转：按 AUMID 激活「{appId}」失败", ex);
            }
        }

        /// <summary>
        /// 第 3 步的保守分支：拿这个应用自己的 exe 路径直接把它拉起来。
        /// 只在「AUMID 未注册」时才走这里。路径来自同名进程的模块信息，所以拉起来的必然是它本人；
        /// 媒体类应用基本都是单实例，已在运行时再启动一次也只会激活现有实例。
        /// </summary>
        private static void LaunchByExe(string appId, string exePath)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                })) { }
                Logger.Info($"媒体跳转：AUMID「{appId}」未注册，已直接拉起「{exePath}」");
            }
            catch (Exception ex)
            {
                Logger.Error($"媒体跳转：直接拉起「{exePath}」失败", ex);
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
