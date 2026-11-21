using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

            // 第 2 步：按窗口的 AppUserModelID 精确找
            // SMTC 的 SourceAppUserModelId 本身就是窗口上的 PKEY_AppUserModel_ID，比拿 appId 猜
            // exe 名可靠得多：B 站（com.bilibili.bilibiliPC）、QQ 音乐这类 Electron / 托盘客户端
            // 都靠它一次命中，不用为每个播放器写死进程名。
            if (TryFindWindowByAumid(appId, out IntPtr aumidHwnd, out uint aumidPid))
            {
                if (ActivateWindow(aumidHwnd))
                {
                    Logger.Info($"媒体跳转：已按 AppUserModelID 定位并激活「{appId}」的窗口（PID {aumidPid}）");
                    return;
                }
                Logger.Warn($"媒体跳转：「{appId}」按 AppUserModelID 定位到的窗口激活被系统拒绝，改按进程名找");
            }

            // 第 3 步：按可执行文件名精确找窗口（appId 是 exe 路径 / 进程名时走这条）
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

            // 第 4 步：把应用本体拉起来
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
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != pid) return true;
                if (!IsUsableWindow(hwnd)) return true;

                // 托盘里的主窗口是隐藏的（B 站 / QQ 音乐关闭到托盘就是这样）：
                // 不能像旧实现那样只认 IsWindowVisible，否则「按进程找窗口」对它们恒失败。
                int score = WindowScore(hwnd);
                if (score <= bestScore) return true;

                bestScore = score;
                best = hwnd;
                return true;
            }, IntPtr.Zero);

            return best;
        }

        /// <summary>顶层窗口打分：标题（主窗口）/ 可见性 / 有尺寸，越大越像主窗口。</summary>
        private static int WindowScore(IntPtr hwnd)
        {
            int score = 0;
            if (Win32.GetWindowTextLength(hwnd) > 0) score += 2;
            if (Win32.IsWindowVisible(hwnd)) score += 1;
            if (Win32.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) score += 1;
            return score;
        }

        // ---- 窗口的 AppUserModelID ----
        // SMTC 的 SourceAppUserModelId 就是窗口上的 PKEY_AppUserModel_ID：拿它找窗口，
        // 不依赖「appId → exe 名」的猜测，B 站 / QQ 音乐 / 网易云这类 Electron 客户端全都能命中。
        // （实现方式与 NPS 插件 BilibiliCdp 的 BilibiliAppLauncher 同源：EnumWindows + 自己抢前台。）

        private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5,
        };

        private const ushort VT_LPWSTR = 31;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr p;
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PROPERTYKEY pkey);
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            int Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(
            IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private static string GetWindowAumid(IntPtr hwnd)
        {
            IPropertyStore? store = null;
            try
            {
                var iid = IID_IPropertyStore;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0 || store == null) return "";

                var key = PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, out var pv) < 0) return "";
                try
                {
                    if (pv.vt != VT_LPWSTR || pv.p == IntPtr.Zero) return "";
                    return Marshal.PtrToStringUni(pv.p) ?? "";
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            catch
            {
                return "";
            }
            finally
            {
                if (store != null)
                {
                    try { Marshal.FinalReleaseComObject(store); } catch { }
                }
            }
        }

        private static bool TryFindWindowByAumid(string appId, out IntPtr found, out uint foundPid)
        {
            found = IntPtr.Zero;
            foundPid = 0;
            if (string.IsNullOrEmpty(appId)) return false;

            IntPtr best = IntPtr.Zero;
            uint bestPid = 0;
            int bestScore = -1;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (!IsUsableWindow(hwnd)) return true;

                string aumid = GetWindowAumid(hwnd);
                if (aumid.Length == 0 || !string.Equals(aumid, appId, StringComparison.OrdinalIgnoreCase)) return true;

                int score = WindowScore(hwnd);
                if (score <= bestScore) return true;

                if (Win32.GetWindowThreadProcessId(hwnd, out uint pid) == 0) return true;
                bestScore = score;
                best = hwnd;
                bestPid = pid;
                return true;
            }, IntPtr.Zero);

            if (best == IntPtr.Zero) return false;
            found = best;
            foundPid = bestPid;
            return true;
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
