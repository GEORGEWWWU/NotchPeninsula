using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Windows.Media.Control;

namespace NotchPeninsula
{
    internal static class MediaAppLauncher
    {
        private sealed class AppTarget
        {
            public uint ProcessId;
            public IntPtr Window;
            public string? ExePath;
        }

        private static readonly ConcurrentDictionary<string, AppTarget> s_targets
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object s_lock = new();

        private static string s_sampledAppId = "";

        private static int s_openCount;

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

            if (string.Equals(appId, s_sampledAppId, StringComparison.OrdinalIgnoreCase)) return;
            s_sampledAppId = appId;
            s_targets.TryRemove(appId, out _);

            try
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                if (IsOwnWindow(hwnd)) return;
                if (!IsUsableWindow(hwnd)) return;

                uint pid = Win32.GetWindowThreadProcessId(hwnd, out _);
                if (pid == 0) return;

                if (!IsProcessOfApp(appId, pid)) return;

                var target = s_targets.GetOrAdd(appId, _ => new AppTarget());
                lock (s_lock)
                {
                    target.ProcessId = pid;
                    target.Window = hwnd;
                    if (string.IsNullOrEmpty(target.ExePath)) target.ExePath = TryGetProcessImagePath(pid);
                }
                Logger.Info($"媒体跳转：已记下「{appId}」的应用窗口 0x{hwnd.ToInt64():X}（PID {pid}）");
            }
            catch (Exception ex)
            {
                Logger.Error("媒体跳转：采样应用窗口失败", ex);
            }
        }

        internal static void OpenCurrentSessionApp()
        {
            string appId = MediaController.Instance?.CurrentAppId ?? "";
            if (appId.Length == 0)
            {
                Logger.Warn("媒体跳转：当前没有可接管的媒体会话，已忽略");
                return;
            }

            if (++s_openCount % 16 == 0) PruneDeadTargets();

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
            // 现在按可靠性排序：
            if (TryGetExePath(appId, exeName, out string? exePath))
            {
                LaunchByExe(appId, exePath);
                return;
            }

            LaunchByAumid(appId);
        }

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

        /// 分隔符里不含 '.'：把点当分隔符会让 "QQMusic.exe" 变成 "exe"。
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

        private static string? TryGetProcessImagePath(uint pid)
        {
            string path = Win32.TryGetProcessImagePath((int)pid);
            return path.Length > 0 && File.Exists(path) ? path : null;
        }

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
                        int pid = candidates[i].Id;
                        string path = Win32.TryGetProcessImagePath(pid);   // 同上：不用 MainModule
                        if (path.Length > 0 && File.Exists(path))
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

                if (fallback == IntPtr.Zero) fallback = hwnd;
                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero ? found : fallback;
        }

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

        private static void LaunchByAumid(string appId)
        {
            try
            {
                // 避免每次双击都留一个待 GC 的可释放对象
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"shell:AppsFolder\\{appId}\"",
                    UseShellExecute = true,
                    WorkingDirectory = string.Empty
                })) { }
                Logger.Info($"媒体跳转：已按 AUMID 激活「{appId}」");
            }
            catch (Exception ex)
            {
                Logger.Error($"媒体跳转：按 AUMID 激活「{appId}」失败", ex);
            }
        }

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

        private static bool IsUsableWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
            long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            return (exStyle & Win32.WS_EX_TOOLWINDOW) == 0;
        }

        private static bool IsOwnWindow(IntPtr hwnd)
            => Win32.GetWindowThreadProcessId(hwnd, out uint pid) != 0
               && pid == (uint)Environment.ProcessId;
    }
}
