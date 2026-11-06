using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
            string aumid, string? displayName = null, string? preferredTitle = null, string? launchUri = null)
        {
            Logger.Info($"请求激活应用，AUMID={aumid}，显示名={displayName}，标题={preferredTitle}，启动协议={launchUri}");

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
    }
}
