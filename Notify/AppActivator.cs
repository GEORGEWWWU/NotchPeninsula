using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NotchPeninsula
{
    /// <summary>
    /// 「点了通知，把发通知的那个应用叫到前台」。
    ///
    /// 三条路，按可靠性和代价从高到低：
    ///   1. WinRT  <c>PackageManager.AppListEntry.LaunchAsync</c>：只对 MSIX / UWP 包有效；
    ///   2. COM    <c>IApplicationActivationManager.ActivateApplication</c>：同样只认打包应用，
    ///             对 QQ 这种 Win32 / Electron 应用一律返回 E_INVALIDARG(0x80070057) ——
    ///             它的 AUMID「QQ」只是通知署名，不是可激活的包标识；
    ///   3. 窗口   自己 EnumWindows 找它名下的顶层窗口，ShowWindow 请出来再置前。
    ///             这是 QQ 这类应用的**唯一**出路，也是本类新增的主路径。
    ///
    /// 踩过的坑（务必别再退回 <c>Process.MainWindowHandle</c>）：
    /// QQ 关到托盘后，它 8 个进程的 MainWindowHandle 全是 0，主面板的
    /// Chrome_WidgetWin_1 是**隐藏**状态 —— 只要还按「主窗口句柄非 0」筛选，
    /// 「按进程名置前」在 QQ 上就恒失败（旧日志：未找到匹配 appName=QQ 的进程窗口）。
    /// </summary>
    public static class AppActivator
    {
        #region 对外入口

        /// <summary>
        /// 唤醒发出这条通知的应用。
        /// </summary>
        /// <param name="aumid">通知里的 AppUserModelId，可能为空（HTTP / 转发通道）。</param>
        /// <param name="displayName">通知里的应用显示名，AUMID 匹配不到进程时用它兜底。</param>
        /// <param name="preferredTitle">通知标题（QQ 是群名 / 好友名），有同名窗口时优先它。</param>
        /// <param name="launchUri">
        /// 通知自带的启动协议。有它就先走它 —— QQ 这类 Electron 应用被外部 ShowWindow 拽出来后
        /// 窗口是僵的（内部仍认为隐藏，渲染和输入都停着），只有应用自己开出来的才可操作。
        /// </param>
        public static async Task<(bool Success, string Message)> active_app(
            string aumid, string? displayName = null, string? preferredTitle = null,
            string? launchUri = null, string? conversationName = null)
        {
            Logger.Info($"请求激活应用，AUMID={aumid}，显示名={displayName}，标题={preferredTitle}，启动协议={launchUri}，会话名={conversationName}");

            // 手机连接（Phone Link）转发来的微信通知：点岛应该打开 PC 微信客户端，
            // 而不是把「手机连接」这个中转 App 叫到前台。
            // 只在 PC 微信进程确实在跑（即已登录）时接管；没开就维持原路径，免得弹登录二维码。
            if (IsPhoneLinkWeChatRelay(aumid, preferredTitle))
            {
                var pc = await TryActivatePcWeChatAsync(conversationName);
                if (pc.Success)
                {
                    Logger.Info($"[AppActivator] 微信转发通知 → PC 微信：{pc.Message}");
                    return pc;
                }
                Logger.Warn($"[AppActivator] PC 微信未接管（{pc.Message}），回退原激活路径");
            }

            string windowHint = !string.IsNullOrWhiteSpace(aumid) ? aumid : (displayName ?? "");

            // 第 0 条路：通知自带协议 → 交给应用自己处理
            if (!string.IsNullOrWhiteSpace(launchUri))
            {
                var uriResult = TryActivateByUri(launchUri!);
                if (uriResult.Success)
                {
                    Logger.Info($"协议路径激活成功：{uriResult.Message}");
                    // 协议拉起是异步的（外壳 → timwp.exe → QQ 主进程），等它把窗口开出来再置前。
                    // 这里只碰**已经可见**的窗口：绝不去 ShowWindow 一个隐藏窗，那是僵界面的来源。
                    await Task.Run(() =>
                        TryActivateByWindow(windowHint, displayName, preferredTitle,
                                            visibleOnly: true, pollMs: 2500));
                    return uriResult;
                }
                Logger.Warn($"协议路径失败：{uriResult.Message}");
            }

            (bool Success, string Message) winrtResult = (false, "未尝试");
            (bool Success, string Message) comResult = (false, "未尝试");

            if (!string.IsNullOrWhiteSpace(aumid))
            {
                winrtResult = await TryLaunchViaWinRT(aumid);
                if (winrtResult.Success)
                {
                    Logger.Info($"WinRT 路径激活成功：{winrtResult.Message}");
                    return winrtResult;
                }
                Logger.Warn($"WinRT 路径失败：{winrtResult.Message}");

                comResult = TryLaunchViaCom(aumid);
                if (comResult.Success)
                {
                    Logger.Info($"COM 路径激活成功：{comResult.Message}");
                    return comResult;
                }
                Logger.Warn($"COM 路径失败：{comResult.Message}");
            }
            else
            {
                Logger.Info("AUMID 为空（转发通道），直接走窗口唤醒");
            }

            // 窗口路径要遍历进程和所有顶层窗口，别卡在 UI 线程上。
            var windowResult = await Task.Run(() => TryActivateByWindow(windowHint, displayName, preferredTitle));
            if (windowResult.Success)
            {
                Logger.Info($"窗口路径激活成功：{windowResult.Message}");
                return windowResult;
            }
            Logger.Warn($"窗口路径失败：{windowResult.Message}");

            // 进程根本没起来：只能把它拉起来（只对真 AUMID 尝试，转发通道的显示名不是 AUMID）
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                var launchResult = TryLaunchByShell(aumid);
                if (launchResult.Success)
                {
                    Logger.Info($"启动兜底成功：{launchResult.Message}");
                    return launchResult;
                }
                Logger.Warn($"启动兜底失败：{launchResult.Message}");
            }

            var msg = $"无法唤醒应用：{aumid}（{displayName}）\n" +
                      $"WinRT：{winrtResult.Message}\n" +
                      $"COM：{comResult.Message}\n" +
                      $"窗口：{windowResult.Message}";

            // 转发通道（AUMID 为空）本来就没有「本机应用」可唤醒，别把这当成故障刷 ERROR。
            if (string.IsNullOrWhiteSpace(aumid)) Logger.Debug(msg);
            else Logger.Error(msg);
            return (false, msg);
        }

        /// <summary>兼容旧调用：仅按名字置前（不含启动兜底）。</summary>
        public static (bool Success, string Message) TryBringToFrontByAppName(string appName, string? preferredTitle = null)
            => TryActivateByWindow(appName, null, preferredTitle);

        #endregion

        #region 第 3 条路：按窗口唤醒（Win32 / Electron 应用的主力）

        private struct WindowHit
        {
            public IntPtr Hwnd;
            public string ProcessName;
            public string Title;
            public string ClassName;
            public bool Visible;
            public int Score;
        }

        /// <summary>
        /// 这些类名的窗口不是「应用界面」：托盘宿主、输入法、桌面壳、消息专用窗。
        /// 不筛掉的话，QQ 名下那堆 Chrome_WidgetWin_0 / Electron_NotifyIconHostWindow
        /// 会把真正的窗口挤到候选列表后面。
        /// </summary>
        private static readonly string[] WindowClassBlacklist =
        {
            "IME", "MSCTFIME UI", "Base_PowerMessageWindow", "Electron_NotifyIconHostWindow",
            "Chrome_WidgetWin_0", "Static", "ToolTip32", "GDI+ Hook Window Class",
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "Progman", "WorkerW", "SysShadow", "TaskListThumbnailWnd"
        };

        private static (bool Success, string Message) TryActivateByWindow(
            string nameHint, string? displayName, string? preferredTitle,
            bool visibleOnly = false, int pollMs = 0)
        {
            long deadline = Environment.TickCount64 + pollMs;
            while (true)
            {
                var r = TryActivateByWindowOnce(nameHint, displayName, preferredTitle, visibleOnly);
                if (r.Success) return r;
                if (Environment.TickCount64 >= deadline) return r;
                Thread.Sleep(150);   // 等应用把窗口开出来（协议激活是异步的）
            }
        }

        /// <param name="visibleOnly">
        /// true = 只考虑**当前已经可见**的窗口，绝不 ShowWindow 隐藏窗。
        /// 用在「应用已经自己把窗口开出来了，只需要顶到前面」的场景。
        /// </param>
        private static (bool Success, string Message) TryActivateByWindowOnce(
            string nameHint, string? displayName, string? preferredTitle, bool visibleOnly)
        {
            if (string.IsNullOrWhiteSpace(nameHint) && string.IsNullOrWhiteSpace(displayName))
                return (false, "应用名为空，无法置前");

            var candidates = BuildNameCandidates(
                !string.IsNullOrWhiteSpace(nameHint) ? nameHint : displayName!);
            foreach (var extra in BuildNameCandidates(displayName ?? ""))
                if (!candidates.Contains(extra, StringComparer.OrdinalIgnoreCase)) candidates.Add(extra);

            var pids = CollectTargetProcesses(candidates);
            if (pids.Count == 0)
            {
                Logger.Warn($"未找到候选名 [{string.Join(", ", candidates)}] 对应的运行中进程");
                return (false, "未找到对应的运行中进程");
            }
            Logger.Info($"按窗口唤醒：命中 {pids.Count} 个进程（{string.Join(", ", pids.Values.Distinct())}）");

            var hits = new List<WindowHit>(pids.Count * 2);

            Win32.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner == 0) return true;
                    if (owner == (uint)Environment.ProcessId) return true;          // 别把自己的窗算进去
                    if (!pids.TryGetValue(owner, out string? procName)) return true;

                    long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
                    if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return true;        // 工具窗 / 托盘浮层

                    string cls = ClassNameOf(hwnd);
                    for (int i = 0; i < WindowClassBlacklist.Length; i++)
                        if (string.Equals(cls, WindowClassBlacklist[i], StringComparison.OrdinalIgnoreCase)) return true;

                    string title = WindowTitleOf(hwnd);
                    bool visible = Win32.IsWindowVisible(hwnd);
                    if (visibleOnly && !visible) return true;

                    // 无标题又不可见 = 纯壳窗口，请出来了也是一片空白
                    if (!visible && title.Length == 0) return true;

                    int area = WindowArea(hwnd);
                    if (!visible && area < 4000) return true;                       // 0 尺寸的幽灵窗

                    hits.Add(new WindowHit
                    {
                        Hwnd = hwnd,
                        ProcessName = procName,
                        Title = title,
                        ClassName = cls,
                        Visible = visible,
                        Score = ScoreWindow(visible, title, area, preferredTitle, displayName, candidates)
                    });
                }
                catch (Exception ex)
                {
                    Logger.Debug($"枚举窗口 0x{hwnd.ToInt64():X} 时异常：{ex.Message}");
                }
                return true;
            }, IntPtr.Zero);

            if (hits.Count == 0)
            {
                // visibleOnly：只是来「置前」的，没窗口就老实说没窗口，别去重启一个正在跑的应用。
                if (visibleOnly) return (false, "没有可置前的可见窗口");

                // 进程在跑，但名下没有任何可用窗口（纯后台 / 只有托盘图标）：
                // 把它的 exe 再拉一次，单实例应用通常会把主窗口请出来。
                foreach (var kv in pids)
                {
                    string exe = Win32.TryGetProcessImagePath((int)kv.Key);
                    if (exe.Length == 0 || !File.Exists(exe)) continue;
                    if (TryLaunchExe(exe))
                        return (true, $"进程 {kv.Value} 无可用窗口，已重新拉起 {exe}");
                }
                return (false, "进程在运行，但名下没有可置前的窗口");
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));

            int tried = 0;
            foreach (var hit in hits)
            {
                if (tried++ >= 3) break;                                            // 别把整排窗口都翻出来
                if (!Win32.IsWindow(hit.Hwnd)) continue;

                if (ActivateWindow(hit.Hwnd))
                {
                    Logger.Info($"已置前窗口：进程={hit.ProcessName}，标题「{hit.Title}」，类={hit.ClassName}，" +
                                $"0x{hit.Hwnd.ToInt64():X}，原可见={hit.Visible}");
                    if (!hit.Visible)
                        Logger.Warn("该窗口原本被应用藏在托盘里，是外部硬显示出来的；" +
                                    "这类界面往往不接收输入，正常应优先用通知自带的启动协议。");
                    return (true, $"已将 {hit.ProcessName} 的窗口置前");
                }
                Logger.Warn($"窗口 0x{hit.Hwnd.ToInt64():X}（{hit.ProcessName} / {hit.ClassName}）置前失败，试下一个");
            }

            return (false, $"找到 {hits.Count} 个候选窗口，但都没能置前");
        }

        private static int ScoreWindow(bool visible, string title, int area,
                                       string? preferredTitle, string? displayName, List<string> candidates)
        {
            int score = visible ? 1000 : 0;

            if (!string.IsNullOrEmpty(preferredTitle) && title.Length > 0)
            {
                if (string.Equals(title, preferredTitle, StringComparison.OrdinalIgnoreCase)) score += 900;
                else if (title.IndexOf(preferredTitle, StringComparison.OrdinalIgnoreCase) >= 0) score += 450;
            }

            if (title.Length > 0)
            {
                if (!string.IsNullOrEmpty(displayName)
                    && string.Equals(title, displayName, StringComparison.OrdinalIgnoreCase)) score += 400;

                foreach (var name in candidates)
                {
                    if (name.Length < 2) continue;
                    if (string.Equals(title, name, StringComparison.OrdinalIgnoreCase)) { score += 300; break; }
                }
            }

            // 面积只做温和加权：QQ 托盘里躺着 5 个隐藏窗口，主面板是最大的那个（1222x813），
            // 但别让「大」压过「可见 / 标题命中」。
            score += (int)Math.Min(area / 1000, 200);
            return score;
        }

        private static bool ActivateWindow(IntPtr hwnd)
        {
            try
            {
                bool wasVisible = Win32.IsWindowVisible(hwnd);
                if (!wasVisible) Win32.ShowWindow(hwnd, Win32.SW_SHOW);   // 托盘隐藏窗：先请出来
                if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

                if (Win32.SetForegroundWindow(hwnd) && Win32.IsWindowVisible(hwnd)) return true;

                // 前台锁：本进程不是前台应用时 SetForegroundWindow 会被系统拒掉，
                // 把输入队列挂到目标窗口所在线程上借用它的前台权，完事必须解挂。
                uint targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
                uint thisThread = Win32.GetCurrentThreadId();
                if (targetThread != 0 && targetThread != thisThread)
                {
                    bool attached = false;
                    try
                    {
                        attached = Win32.AttachThreadInput(thisThread, targetThread, true);
                        Win32.BringWindowToTop(hwnd);
                        if (Win32.SetForegroundWindow(hwnd) && Win32.IsWindowVisible(hwnd)) return true;
                    }
                    finally
                    {
                        if (attached) Win32.AttachThreadInput(thisThread, targetThread, false);
                    }
                }

                // 最后一道：至少把它顶到 Z 序最上并显示出来 ——
                // 对用户来说「窗口出来了」就已经算唤醒了。
                Win32.SetWindowPos(hwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE_NOSIZE | (uint)Win32.SWP_SHOWWINDOW);
                return Win32.IsWindowVisible(hwnd);
            }
            catch (Exception ex)
            {
                Logger.Error($"置前窗口 0x{hwnd.ToInt64():X} 异常", ex);
                return false;
            }
        }

        #endregion

        #region 名字 → 进程

        /// <summary>
        /// 把 AUMID / 显示名拆成若干「可能是进程名」的候选。
        /// 以前拿整串 AUMID 去 IndexOf 进程名，像 WorkBuddy.WorkBuddy 这样带点的
        /// 永远匹配不到（这就是旧版注释里那句「有时候点不开」）。
        /// </summary>
        private static List<string> BuildNameCandidates(string raw)
        {
            var list = new List<string>();

            void Add(string? s)
            {
                s = s?.Trim();
                if (string.IsNullOrEmpty(s)) return;
                if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            }

            Add(raw);

            string s = raw.Trim();

            int bang = s.IndexOf('!');                     // Microsoft.YourPhone_8wekyb3d8bbwe!App
            if (bang > 0) s = s[..bang];
            Add(s);

            int underscore = s.IndexOf('_');                // 包族名后面那段哈希
            if (underscore > 0) s = s[..underscore];
            Add(s);

            int slash = s.LastIndexOfAny(['\\', '/']);
            if (slash >= 0 && slash < s.Length - 1) { s = s[(slash + 1)..]; Add(s); }

            int dot = s.LastIndexOf('.');                   // WorkBuddy.WorkBuddy → WorkBuddy
            if (dot > 0 && dot < s.Length - 1) Add(s[(dot + 1)..]);

            // 与进程名对齐：去掉 .exe，再抹掉非字母数字（QQ.exe → QQ，WeChat.exe → WeChat）
            string tail = s;
            if (tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) tail = tail[..^4];
            var sb = new StringBuilder(tail.Length);
            foreach (char c in tail) if (char.IsLetterOrDigit(c)) sb.Append(c);
            Add(sb.ToString());

            return list;
        }

        private static Dictionary<uint, string> CollectTargetProcesses(List<string> candidates)
        {
            var map = new Dictionary<uint, string>();

            // 先精确：短名必须精确，否则「QQ」会把 QQ音乐 / QQ浏览器 一起算进来
            foreach (var name in candidates)
            {
                if (name.Length == 0) continue;
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { map[(uint)p.Id] = p.ProcessName; }
                        catch (Exception ex) { Logger.Debug($"读取进程信息失败：{ex.Message}"); }
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"按进程名「{name}」取进程失败：{ex.Message}");
                }
            }

            if (map.Count > 0) return map;

            // 再模糊：AUMID 里的包名和真实进程名常常只共享一段
            var loose = candidates.Where(c => c.Length >= 3 && c.All(char.IsLetterOrDigit)).ToList();
            if (loose.Count == 0) return map;

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Logger.Debug($"枚举全部进程失败：{ex.Message}"); return map; }

            foreach (var p in all)
            {
                try
                {
                    string procName = p.ProcessName;
                    foreach (var c in loose)
                    {
                        if (procName.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            map[(uint)p.Id] = procName;
                            break;
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            return map;
        }

        private static string WindowTitleOf(IntPtr hwnd)
        {
            try
            {
                int len = Win32.GetWindowTextLength(hwnd);
                if (len <= 0) return "";
                var sb = new StringBuilder(len + 2);
                Win32.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int n = Win32.GetClassName(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        private static int WindowArea(IntPtr hwnd)
        {
            if (!Win32.GetWindowRect(hwnd, out Win32.RECT r)) return 0;
            long w = Math.Max(0, r.Right - r.Left);
            long h = Math.Max(0, r.Bottom - r.Top);
            long area = w * h;
            return area > int.MaxValue ? int.MaxValue : (int)area;
        }

        #endregion

        #region 第 1 条路：WinRT（PackageManager + AppListEntry）

        private static async Task<(bool Success, string Message)> TryLaunchViaWinRT(string aumid)
        {
            try
            {
                var packageManager = new Windows.Management.Deployment.PackageManager();
                try
                {
                    var packages = packageManager.FindPackagesForUserWithPackageTypes(
                        null,
                        Windows.Management.Deployment.PackageTypes.Main |
                        Windows.Management.Deployment.PackageTypes.Optional);

                    foreach (var package in packages)
                    {
                        try
                        {
                            var entries = await package.GetAppListEntriesAsync();
                            foreach (var entry in entries)
                            {
                                if (!string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase)) continue;

                                bool launched = await entry.LaunchAsync();
                                if (launched)
                                {
                                    var display = entry.DisplayInfo?.DisplayName ?? aumid;
                                    Logger.Info($"WinRT LaunchAsync 成功，显示名={display}");
                                    return (true, $"已通过 WinRT 唤醒应用：{display}");
                                }

                                Logger.Warn("WinRT LaunchAsync 返回 false（用户可能取消了启动）");
                                return (false, "LaunchAsync 返回 false（用户可能取消了启动）");
                            }
                        }
                        finally
                        {
                            ReleaseWinRT(package);
                        }
                    }

                    Logger.Warn($"WinRT 未找到匹配 AUMID 的已安装包：{aumid}");
                    return (false, "未在当前用户的已安装包中找到匹配的 AUMID");
                }
                finally
                {
                    ReleaseWinRT(packageManager);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WinRT 激活异常，AUMID={aumid}", ex);
                return (false, $"WinRT 激活异常：{ex.Message}");
            }
        }

        private static void ReleaseWinRT(object? o)
        {
            try { (o as IDisposable)?.Dispose(); }
            catch (Exception ex) { Logger.Debug($"释放 WinRT 对象失败：{ex.Message}"); }
        }

        #endregion

        #region 第 2 条路：COM（IApplicationActivationManager）

        [ComImport]
        [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig]
            int ActivateApplication(
                [In] string appUserModelId,
                [In] string arguments,
                [In] ActivateOptions options,
                [Out] out uint processId);

            [PreserveSig]
            int ActivateForFile(
                [In] string appUserModelId,
                [In] IntPtr itemArray,
                [In] string verb,
                [Out] out uint processId);

            [PreserveSig]
            int ActivateForProtocol(
                [In] string appUserModelId,
                [In] IntPtr itemArray,
                [Out] out uint processId);
        }

        [ComImport]
        [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        [ClassInterface(ClassInterfaceType.None)]
        private class ApplicationActivationManager { }

        [Flags]
        private enum ActivateOptions : uint
        {
            None = 0x00000000,
            DesignMode = 0x00000001,
            NoErrorUI = 0x00000002,
            NoSplashScreen = 0x00000004
        }

        private static (bool Success, string Message) TryLaunchViaCom(string aumid)
        {
            object? activatorRaw = null;
            try
            {
                activatorRaw = new ApplicationActivationManager();
                var activator = (IApplicationActivationManager?)activatorRaw;
                if (activator == null)
                {
                    Logger.Error("COM 创建 ApplicationActivationManager 实例失败");
                    return (false, "创建 ApplicationActivationManager 实例失败");
                }

                int hr = activator.ActivateApplication(
                    aumid,
                    string.Empty,
                    ActivateOptions.None,
                    out uint pid);

                if (hr == 0)
                {
                    Logger.Info($"COM ActivateApplication 成功，PID={pid}");
                    return (true, $"已通过 COM 唤醒应用，进程 PID = {pid}");
                }

                Logger.Warn($"COM ActivateApplication 返回 HRESULT=0x{hr:X8}，AUMID={aumid}");
                return (false, $"ActivateApplication 返回 HRESULT = 0x{hr:X8}");
            }
            catch (COMException comEx)
            {
                Logger.Error($"COM 激活 COMException，AUMID={aumid}", comEx);
                return (false, $"COM 异常：{comEx.Message} (HRESULT 0x{comEx.HResult:X8})");
            }
            catch (Exception ex)
            {
                Logger.Error($"COM 激活异常，AUMID={aumid}", ex);
                return (false, $"COM 激活异常：{ex.Message}");
            }
            finally
            {
                if (activatorRaw != null)
                {
                    try { Marshal.FinalReleaseComObject(activatorRaw); }
                    catch (Exception ex) { Logger.Debug($"释放 ApplicationActivationManager 失败：{ex.Message}"); }
                }
            }
        }

        #endregion

        #region 第 0 条路：通知自带的启动协议（最贴近「用户点系统通知」的行为）

        /// <summary>
        /// 把通知里的 <c>launch</c> 交给外壳执行，例如 QQ 的
        /// <c>ntqq-notification://externalCommand?subcmd=openExternalWindow;peerUid=...;scene=aio;</c>
        /// 由系统按 HKCR 里注册的处理器（QQ 是 <c>timwp.exe "%1"</c>）转交给应用本体。
        ///
        /// 为什么必须走这条：QQ 关到托盘时主面板是 <c>SW_HIDE</c> 状态，我们从外面
        /// <c>ShowWindow(SW_SHOW)</c> 只能让它「显示」，QQ 自己的状态机仍认为面板是关的 ——
        /// 渲染和输入都被挂着，用户看到的就是一个卡死的界面。只有让 QQ 自己开才正常。
        /// </summary>
        private static (bool Success, string Message) TryActivateByUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme.Length < 2)
                return (false, "启动协议不是有效的 URI");

            // http/https 是「打开网页」，不是「唤醒应用」，别接。
            if (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return (false, $"启动协议是 {parsed.Scheme}，不当作应用唤醒");

            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                    WorkingDirectory = string.Empty
                })) { }
                return (true, $"已按通知的启动协议唤起应用：{parsed.Scheme}://");
            }
            catch (Exception ex)
            {
                Logger.Error($"按启动协议唤起失败：{uri}", ex);
                return (false, $"按启动协议唤起失败：{ex.Message}");
            }
        }

        #endregion

        #region 第 4 条路：把它拉起来

        /// <summary>
        /// shell:AppsFolder 对「开始菜单里注册过的」应用都有效（含 Win32 快捷方式），
        /// 比 ActivateApplication 宽容得多 —— QQ 的 AUMID 走不通 COM，却能从这里起来。
        /// </summary>
        private static (bool Success, string Message) TryLaunchByShell(string aumid)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"shell:AppsFolder\\{aumid}\"",
                    UseShellExecute = true,
                    WorkingDirectory = string.Empty
                })) { }
                Logger.Info($"已通过 shell:AppsFolder 拉起「{aumid}」");
                return (true, $"已按 AUMID 拉起应用：{aumid}");
            }
            catch (Exception ex)
            {
                Logger.Error($"按 AUMID 拉起「{aumid}」失败", ex);
                return (false, $"按 AUMID 拉起失败：{ex.Message}");
            }
        }

        private static bool TryLaunchExe(string exePath)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                })) { }
                Logger.Info($"已重新拉起「{exePath}」");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"拉起「{exePath}」失败", ex);
                return false;
            }
        }

        #endregion

        #region 手机连接转发的微信通知 → 打开 PC 微信

        // 微信 4.x 的进程名是 Weixin，旧版是 WeChat，两个都认。
        private static readonly string[] PcWeChatProcessNames = { "Weixin", "WeChat" };

        /// <summary>
        /// 这条通知是不是「手机连接转发来的个人微信消息」。
        /// AUMID 是硬证据（Microsoft.YourPhone_…!App），标题是手机端 App 名「微信 / WeChat」。
        /// 「企业微信」对应另一个客户端，不在这里接管，仍按原逻辑跳手机连接。
        /// </summary>
        private static bool IsPhoneLinkWeChatRelay(string aumid, string? title)
        {
            if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(title)) return false;

            bool relay = aumid.IndexOf("YourPhone", StringComparison.OrdinalIgnoreCase) >= 0
                      || aumid.IndexOf("CrossDevice", StringComparison.OrdinalIgnoreCase) >= 0
                      || aumid.IndexOf("PhoneExperienceHost", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!relay) return false;

            if (title.Contains("企业微信") ||
                title.IndexOf("WeCom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("WXWork", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return title.Contains("微信") ||
                   title.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 把 PC 微信叫到前台，而不是打开「手机连接」。
        ///
        /// 微信 4.2（Qt 自绘，收在托盘时主窗口隐藏）三条路都实测过：
        ///   · 外部 ShowWindow 隐藏主窗口 —— 窗口出来却不接收输入，和 QQ 的「僵界面」一样，不能用；
        ///   · 重新运行 Weixin.exe —— 不激活已登录实例，反而新弹 mmui::LoginWindow 登录窗，不能用；
        ///   · weixin:// 协议 —— 微信自己把已登录实例的主窗口开出来（pid 不变），界面正常，用它。
        /// 另外通知自带的 launch 是 ms-phone:phonenotifications?…，指向手机连接，绝不能沿用。
        ///
        /// 只置前「协议打开后已经可见」的窗口，不碰隐藏窗；
        /// PC 微信进程不在（未登录）时不接管，免得点一条消息弹出登录二维码。
        /// </summary>
        // 点微信通知时是否继续跳到「对应会话」。关掉就退回只打开微信主界面（v6 行为）。
        private const bool EnableWeChatChatJump = true;

        private static async Task<(bool Success, string Message)> TryActivatePcWeChatAsync(string? conversationName)
        {
            long tStart = Environment.TickCount64;
            string procNameFound = "";
            var pids = new HashSet<uint>();
            foreach (string procName in PcWeChatProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try { pids.Add((uint)p.Id); } catch { }
                    finally { p.Dispose(); }
                }
                if (pids.Count > 0) { procNameFound = procName; break; }
            }

            if (procNameFound.Length == 0)
                return (false, "PC 微信未运行（未登录），保持原跳转");

            // 主窗口已经摆在屏幕上（且不是最小化）时，直接置前就够了 —— 省掉 weixin:// 的
            // ShellExecute + 微信内部处理这一趟。托盘隐藏 / 最小化才必须让微信用自己的协议开窗：
            // 外部 ShowWindow 硬拽出来的 Qt 窗口不接收输入（v5 踩过的僵界面）。
            IntPtr mainHwnd = FindWeChatMainWindow(pids);
            bool alreadyUp = mainHwnd != IntPtr.Zero && !Win32.IsIconic(mainHwnd);
            string how;

            if (alreadyUp)
            {
                ActivateWindow(mainHwnd);
                how = "直接置前";
            }
            else
            {
                // 直接 ShellExecute，别过 Uri.TryCreate —— "weixin://" 这种无主机名的协议串
                // 在 .NET 的 Uri 解析里可能被判非法，而 ShellExecute 本身是认的（实测可用）。
                try
                {
                    using (Process.Start(new ProcessStartInfo
                    {
                        FileName = "weixin://",
                        UseShellExecute = true,
                        WorkingDirectory = string.Empty
                    })) { }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"weixin:// 协议唤起失败：{ex.Message}");
                    return (false, $"weixin:// 协议失败：{ex.Message}");
                }

                // 协议拉起是异步的，轮询等窗口出来（原来直接走通用置前，固定节奏 150ms 一跳）。
                mainHwnd = await WaitForWeChatMainWindowAsync(pids, 2500);
                if (mainHwnd == IntPtr.Zero)
                {
                    // 兜底：交给通用置前再试一次，行为同旧版。
                    var r = await Task.Run(() =>
                        TryActivateByWindow(procNameFound, procNameFound, null, visibleOnly: true));
                    mainHwnd = FindWeChatMainWindow(pids);
                    Logger.Warn($"[AppActivator] weixin:// 后未拿到主窗口（{r.Message}）");
                }
                else
                {
                    ActivateWindow(mainHwnd);
                }
                how = "weixin:// 唤醒";
            }

            long tActivate = Environment.TickCount64 - tStart;

            string jumpMsg = "";
            if (EnableWeChatChatJump && !string.IsNullOrWhiteSpace(conversationName) && mainHwnd != IntPtr.Zero)
            {
                var jump = await TryOpenWeChatConversationAsync(mainHwnd, conversationName!, pids);
                if (jump.Success) jumpMsg = $"；{jump.Message}";
                else
                {
                    jumpMsg = $"；仅打开主窗口（{jump.Message}）";
                    Logger.Warn($"[AppActivator] 微信会话跳转跳过：{jump.Message}");
                }
            }

            Logger.Info($"[AppActivator] PC 微信激活完成：{how}，打开窗口 {tActivate}ms，总耗时 {Environment.TickCount64 - tStart}ms");
            return (true, $"已唤起 PC 微信主窗口（{how}）" + jumpMsg);
        }

        /// <summary>轮询等微信主窗口出现（协议唤起是异步的，窗口一般几百毫秒内出来）。</summary>
        private static async Task<IntPtr> WaitForWeChatMainWindowAsync(HashSet<uint> pids, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                IntPtr h = FindWeChatMainWindow(pids);
                if (h != IntPtr.Zero) return h;
                if (Environment.TickCount64 >= deadline) return IntPtr.Zero;
                await Task.Delay(50);
            }
        }

        /// <summary>找微信自己开出来的、可见的 Qt 主窗口（QWindowIcon，面积最大的那个）。</summary>
        private static IntPtr FindWeChatMainWindow(HashSet<uint> pids)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            try
            {
                Win32.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!Win32.IsWindowVisible(hwnd)) return true;
                        if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || !pids.Contains(owner)) return true;
                        if (ClassNameOf(hwnd).IndexOf("QWindowIcon", StringComparison.OrdinalIgnoreCase) < 0) return true;
                        int area = WindowArea(hwnd);
                        if (area > bestArea) { bestArea = area; best = hwnd; }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best;
        }

        /// <summary>
        /// Ctrl+F 之后微信会新建一个独立的搜索下拉窗口（类名 Qt51514QWindowToolSaveBits）。
        /// 它既是「搜索框确实开了」的硬证据，也是后面点第一项的坐标来源。
        /// </summary>
        private static IntPtr FindWeChatSearchDropdown(HashSet<uint> pids)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            try
            {
                Win32.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!Win32.IsWindowVisible(hwnd)) return true;
                        if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || !pids.Contains(owner)) return true;
                        if (ClassNameOf(hwnd).IndexOf("ToolSaveBits", StringComparison.OrdinalIgnoreCase) < 0) return true;
                        int area = WindowArea(hwnd);
                        if (area > bestArea) { bestArea = area; best = hwnd; }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best;
        }

        /// <summary>把第 2 段文本洗成能拿去搜索的会话名；汇总通知、正文、过长文本一律放弃。</summary>
        private static string NormalizeConversationName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            int cut = s.IndexOfAny(new[] { ':', '：' });
            if (cut > 0 && cut <= 24) s = s.Substring(0, cut).Trim();
            if (s.Length == 0 || s.Length > 32) return "";
            if (s == "微信" || s.Equals("WeChat", StringComparison.OrdinalIgnoreCase)) return "";
            if (s.Contains("条新消息") || s.Contains("多个通知") || s.Contains("消息汇总")) return "";
            foreach (char c in s) if (char.IsControl(c)) return "";
            return s;
        }

        /// <summary>
        /// 微信桌面端 4.2 没有任何「按会话跳转」的协议（安装目录里找不到 weixin://dl/chat），
        /// Qt 自绘窗口也不暴露 UIA 控件，唯一稳定路径是让微信自己开搜索框再点第一项：
        ///   weixin:// 唤起 → Ctrl+F → 独立下拉窗口出现 → 剪贴板粘贴会话名 → 点下拉里第一个联系人结果。
        /// 实测（4.2.0.13，125% DPI）：空搜索下拉 460×120，粘贴后 460×285，
        /// 点 (Left+150, Top+80) 正好命中「联系人」第一项，右侧直接切到该会话且界面可正常操作。
        ///
        /// 安全设计：
        ///   · 只有检测到下拉窗口才粘贴，绝不盲按按键；
        ///   · 用鼠标点击而不是回车 —— 不会把文字打进聊天输入框，也不会像「盲按回车」那样打开搜一搜；
        ///   · 前台不是微信窗口 / 下拉过矮（无结果） / 任一步异常，都只保留「打开主窗口」；
        ///   · 全程不按 Enter，鼠标位置和剪贴板用完即还原。
        /// </summary>
        private static async Task<(bool Success, string Message)> TryOpenWeChatConversationAsync(
            IntPtr mainHwnd, string rawName, HashSet<uint> pids)
        {
            string name = NormalizeConversationName(rawName);
            if (name.Length == 0) return (false, "会话名不可用");
            if (mainHwnd == IntPtr.Zero) return (false, "没找到微信主窗口");

            long t0 = Environment.TickCount64;
            bool dropdownOpened = false;
            bool jumped = false;

            // 剪贴板放到后台线程写好，和「等前台 / 微信建搜索框」并行；真要用之前才等它。
            System.Windows.IDataObject? savedClipboard = null;
            var writeClip = Task.Run(() => RunOnStaThread(() =>
            {
                try { savedClipboard = System.Windows.Clipboard.GetDataObject(); } catch { }
                System.Windows.Clipboard.SetText(name);
            }, 1500));

            try
            {
                // 前台就绪通常几毫秒，给个上限就行。
                if (!await WaitUntilAsync(() => Win32.GetForegroundWindow() == mainHwnd, 250, 10))
                    return (false, "前台不是微信窗口");
                long tFocus = Environment.TickCount64 - t0;

                // 上次没命中可能留了个下拉，日志里标一下，别当成新开的。
                if (FindWeChatSearchDropdown(pids) != IntPtr.Zero)
                    Logger.Debug("[AppActivator] 检测到已存在的微信搜索下拉（上次残留或用户手动打开）");

                // 实测（4.2.0.13，连续四轮）：搜索面板关掉后只是隐藏、窗口会复用，所以热路径下
                // Ctrl+F 到下拉出现只要 35-45ms，粘贴到出结果 11-34ms，端到端 ~110ms。只有微信
                // 首次加载搜索模块时才会慢到 1-2s。
                //
                // 这里刻意「先等下拉出现再粘贴」：之前是先睡 100ms 抢跑粘贴，那时搜索框常常还没
                // 接管输入，Ctrl+V 被吞 → 只能等 750ms 超时再重贴，反而把总耗时拖到 1.5s。
                WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_F);
                IntPtr dd = await WaitForDropdownAsync(pids, 1000, 10);

                if (dd == IntPtr.Zero)
                {
                    // 冷启动时见过第一次 Ctrl+F 被吞（焦点还没落到微信上），补发一次。
                    WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_F);
                    dd = await WaitForDropdownAsync(pids, 700, 10);
                }
                if (dd == IntPtr.Zero)
                    return (false, "Ctrl+F 未打开搜索下拉（未注入输入）");
                dropdownOpened = true;
                long tOpen = Environment.TickCount64 - t0;

                if (!writeClip.Result)
                    return (false, "写剪贴板失败");

                // 搜索框已就绪，Ctrl+A 覆盖可能残留的旧关键词，再粘贴。
                WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_A);
                await Task.Delay(20);
                WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_V);

                int height = await WaitForDropdownHeightAsync(pids, 160, 700, 10);

                // 下拉在、但没长高 = 粘贴被吞，覆盖重贴一次（只有确认搜索框已开才重试，
                // 避免把会话名反复粘进聊天输入框）。
                if (height < 160)
                {
                    Logger.Debug("[AppActivator] 首次粘贴没出结果，重试一次");
                    WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_A);
                    await Task.Delay(20);
                    WxInput.Combo(WxInput.VK_CONTROL, WxInput.VK_V);
                    height = await WaitForDropdownHeightAsync(pids, 160, 500, 10);
                }

                if (height < 160)
                    return (false, "搜索无匹配结果");
                long tReady = Environment.TickCount64 - t0;

                // 剪贴板在后台还回去，不挡点击。
                _ = Task.Run(() => RunOnStaThread(() =>
                {
                    try
                    {
                        if (savedClipboard != null) System.Windows.Clipboard.SetDataObject(savedClipboard, true);
                        else System.Windows.Clipboard.Clear();
                    }
                    catch { }
                }, 1500));

                dd = FindWeChatSearchDropdown(pids);
                if (dd == IntPtr.Zero || !Win32.GetWindowRect(dd, out Win32.RECT r))
                    return (false, "搜索下拉已消失");

                uint dpi = 0;
                try { dpi = Win32.GetDpiForWindow(dd); } catch { }
                if (dpi == 0) dpi = 96;
                double scale = dpi / 120.0;   // 实测基准是 125%(DPI 120) 下的 (150, 80)
                int x = r.Left + (int)Math.Round(150 * scale);
                int y = r.Top + (int)Math.Round(80 * scale);

                Win32.GetCursorPos(out Win32.POINT old);
                WxInput.SetCursorPos(x, y);
                WxInput.Click();

                // 实测点击后下拉要 720-840ms 才收起，但会话在几十毫秒内就切过去了。
                // 原来在这里等 250ms 再等下拉消失，白等近 1 秒 —— 现在只留一小段让点击落地。
                await Task.Delay(120);
                try { WxInput.SetCursorPos(old.x, old.y); } catch { }

                jumped = true;
                long cost = Environment.TickCount64 - t0;
                Logger.Info($"[AppActivator] 微信会话跳转：搜索「{name}」并点开首个联系人结果" +
                            $"（下拉 {r.Right - r.Left}×{height}, DPI {dpi}, 分段 焦点 {tFocus}ms / 搜索框 {tOpen - tFocus}ms" +
                            $" / 出结果 {tReady - tOpen}ms / 点击 {cost - tReady}ms / 合计 {cost}ms）");
                return (true, $"已跳到「{name}」会话（{cost}ms）");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AppActivator] 微信会话跳转异常（不影响主窗口）：{ex.Message}");
                return (false, $"异常：{ex.Message}");
            }
            finally
            {
                // 中途断了就把搜索框收掉，别在界面上留一个空搜索框。
                if (dropdownOpened && !jumped) CloseWeChatSearch(mainHwnd);
            }
        }

        /// <summary>轮询直到条件成立或超时（毫秒级粒度，用来换掉固定 Delay）。</summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> probe, int timeoutMs, int intervalMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                if (probe()) return true;
                if (Environment.TickCount64 >= deadline) return false;
                await Task.Delay(intervalMs);
            }
        }

        /// <summary>轮询等微信的搜索下拉窗口出现。</summary>
        private static async Task<IntPtr> WaitForDropdownAsync(HashSet<uint> pids, int timeoutMs, int intervalMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                IntPtr dd = FindWeChatSearchDropdown(pids);
                if (dd != IntPtr.Zero) return dd;
                if (Environment.TickCount64 >= deadline) return IntPtr.Zero;
                await Task.Delay(intervalMs);
            }
        }

        /// <summary>轮询等下拉长到 minHeight（= 搜索出结果了），返回实际高度，超时返回 0。</summary>
        private static async Task<int> WaitForDropdownHeightAsync(
            HashSet<uint> pids, int minHeight, int timeoutMs, int intervalMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                IntPtr dd = FindWeChatSearchDropdown(pids);
                if (dd != IntPtr.Zero && Win32.GetWindowRect(dd, out Win32.RECT r))
                {
                    int h = r.Bottom - r.Top;
                    if (h >= minHeight) return h;
                }
                if (Environment.TickCount64 >= deadline) return 0;
                await Task.Delay(intervalMs);
            }
        }

        /// <summary>
        /// 收掉还开着的微信搜索下拉。实测（4.2.0.13）：再按一次 Ctrl+F 关不掉（下拉原样还在），
        /// Esc 更不行（会把微信整个收回托盘），只有点一下主窗口非搜索区的标题条才会失焦收起。
        /// </summary>
        private static void CloseWeChatSearch(IntPtr mainHwnd)
        {
            try
            {
                if (mainHwnd == IntPtr.Zero) return;
                // 只有微信仍在前台时才点，避免误点到别的窗口上。
                if (Win32.GetForegroundWindow() != mainHwnd) return;
                if (!Win32.GetWindowRect(mainHwnd, out Win32.RECT r)) return;

                Win32.GetCursorPos(out Win32.POINT old);
                WxInput.SetCursorPos(r.Left + 60, r.Top + 12);
                Thread.Sleep(120);
                WxInput.Click();
                Thread.Sleep(150);
                try { WxInput.SetCursorPos(old.x, old.y); } catch { }
                Logger.Debug("[AppActivator] 已收掉残留的微信搜索下拉");
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AppActivator] 收搜索下拉失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 剪贴板 API 走 OLE，必须在 STA 线程上调用。点击回调经若干 await 之后落在 MTA 线程池，
        /// 直接调 System.Windows.Clipboard 就会抛「必须先将当前线程设置为单个线程单元(STA)模式
        /// 方可进行 OLE 调用」——v7 的会话跳转正是断在这一步（Ctrl+F 已发出，搜索框空留）。
        /// </summary>
        private static bool RunOnStaThread(Action action, int timeoutMs = 3000)
        {
            Exception? err = null;
            bool ok = false;
            var t = new Thread(() =>
            {
                try { action(); ok = true; }
                catch (Exception ex) { err = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(timeoutMs))
            {
                Logger.Warn("[AppActivator] STA 线程超时未返回");
                return false;
            }
            if (err != null)
            {
                Logger.Warn($"[AppActivator] STA 线程执行失败：{err.Message}");
                return false;
            }
            return ok;
        }

        /// <summary>只给上面这段用的键盘 / 鼠标注入，避免把一次性 API 塞进全局 Win32。</summary>
        private static class WxInput
        {
            internal const byte VK_CONTROL = 0x11;
            internal const byte VK_A = 0x41;
            internal const byte VK_F = 0x46;
            internal const byte VK_V = 0x56;
            private const uint KEYEVENTF_KEYUP = 0x0002;
            private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            private const uint MOUSEEVENTF_LEFTUP = 0x0004;

            [DllImport("user32.dll")]
            private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

            [DllImport("user32.dll")]
            internal static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);

            internal static void Combo(byte mod, byte vk)
            {
                keybd_event(mod, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(mod, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            internal static void Click()
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
        }

        #endregion
    }
}
