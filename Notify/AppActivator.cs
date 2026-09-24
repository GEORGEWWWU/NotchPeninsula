using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NotchPeninsula
{

    public static class AppActivator
    {

        public static async Task<(bool Success, string Message)> active_app(string aumid)
        {
            Logger.Info($"请求激活应用，AUMID={aumid}");

            if (string.IsNullOrWhiteSpace(aumid))
            {
                Logger.Warn("激活失败：AUMID 为空");
                return (false, "AUMID 为空，无法激活应用");
            }

            var winrtResult = await TryLaunchViaWinRT(aumid);
            if (winrtResult.Success)
            {
                Logger.Info($"WinRT 路径激活成功：{winrtResult.Message}");
                return winrtResult;
            }
            Logger.Warn($"WinRT 路径失败：{winrtResult.Message}");

            var comResult = TryLaunchViaCom(aumid);
            if (comResult.Success)
            {
                Logger.Info($"COM 路径激活成功：{comResult.Message}");
                return comResult;
            }
            Logger.Warn($"COM 路径失败：{comResult.Message}");

            var msg = $"无法通过 AUMID 唤醒应用：{aumid}\n" +
                      $"WinRT 错误：{winrtResult.Message}\n" +
                      $"COM 错误：{comResult.Message}";
            Logger.Error(msg);
            return (false, msg);
        }

        #region WinRT 路径（PackageManager + AppListEntry）

        private static async Task<(bool Success, string Message)> TryLaunchViaWinRT(string aumid)
        {
            try
            {
                // PackageManager / Package 都是 WinRT 包装对象（底层 COM RCW + 原生资源）。
                // 这里要枚举当前用户的**全部**已安装包，单次就能产出数百个包装对象 ——
                // 不释放的话只能等 GC 终结器，高频点击通知会让 RCW 在两次 GC 之间持续累积。
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
                                if (string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                                {
                                    bool launched = await entry.LaunchAsync();
                                    if (launched)
                                    {
                                        var display = entry.DisplayInfo?.DisplayName ?? aumid;
                                        Logger.Info($"WinRT LaunchAsync 成功，显示名={display}");
                                        return (true, $"已通过 WinRT 唤醒应用：{display}");
                                    }
                                    else
                                    {
                                        Logger.Warn("WinRT LaunchAsync 返回 false（用户可能取消了启动）");
                                        return (false, "LaunchAsync 返回 false（用户可能取消了启动）");
                                    }
                                }
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

        /// <summary>
        /// 尽力确定性释放一个 WinRT / COM 包装对象。
        ///
        /// 用 <c>as IDisposable</c> 而不是 <c>using</c>：并非所有 WinRT 类型都投影出 IDisposable
        /// （只有底层实现 IClosable 的才有），写死 <c>using</c> 会因类型不带该接口而编译不过。
        /// 支持释放的当场释放，不支持的静默跳过。
        /// </summary>
        private static void ReleaseWinRT(object? o)
        {
            try { (o as IDisposable)?.Dispose(); }
            catch (Exception ex) { Logger.Debug($"释放 WinRT 对象失败：{ex.Message}"); }
        }

        #endregion

        #region COM 路径（IApplicationActivationManager）

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
            // RCW（运行时可调用包装）不再使用后应显式释放，否则这个 COM 对象要等 GC 终结器
            // 才断开与 ApplicationActivationManager 的连接（每次点击通知都会走一遍）。
            object? activatorRaw = null;
            try
            {
                // Instantiate the COM coclass and cast to the interface to get correct signature (int/HRESULT)
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

        // Fallback: try to find a running process by app name and bring its main window to foreground
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        public static (bool Success, string Message) TryBringToFrontByAppName(string appName)
        {
            Logger.Info($"请求置前窗口，appName={appName}");

            if (string.IsNullOrWhiteSpace(appName))
            {
                Logger.Warn("置前失败：应用名为空");
                return (false, "应用名为空，无法置前");
            }
            try
            {
                var procs = System.Diagnostics.Process.GetProcesses();
                try
                {
                    foreach (var p in procs)
                    {
                        // Process 持有原生进程句柄，必须确定性释放 ——
                        // GetProcesses() 为系统里每个进程都建了一个对象，靠 GC 终结器回收
                        // 会让句柄数在两次 GC 之间持续飙高（本方法每次"置前"都会调一次）。
                        try
                        {
                            if (p.MainWindowHandle == IntPtr.Zero)
                                continue;
                            if ((!string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowTitle.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                                || p.ProcessName.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string procName = SafeProcessName(p);
                                int procId = SafeProcessId(p);
                                IntPtr h = p.MainWindowHandle;
                                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                                SetForegroundWindow(h);
                                Logger.Info($"已将进程 {procName} (PID={procId}) 窗口置前");
                                return (true, $"已将进程 {procName} 的窗口置前");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"遍历进程 {SafeProcessName(p)} 时异常：{ex.Message}");
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                finally
                {
                    // 提前 return 时，剩余尚未遍历到的 Process 也要释放
                    foreach (var rest in procs)
                    {
                        try { rest.Dispose(); } catch { }
                    }
                }
                Logger.Warn($"未找到匹配 appName={appName} 的进程窗口");
                return (false, "未找到匹配的进程窗口");
            }
            catch (Exception ex)
            {
                Logger.Error($"置前异常，appName={appName}", ex);
                return (false, $"置前异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 取进程名 / PID 的容错包装：进程可能在遍历途中退出，
        /// 此时访问 ProcessName 会抛，而原实现是在 catch 里再读一次 ProcessName 打日志 —— 会二次抛。
        /// </summary>
        private static string SafeProcessName(System.Diagnostics.Process p)
        {
            try { return p.ProcessName; } catch { return "(已退出)"; }
        }

        private static int SafeProcessId(System.Diagnostics.Process p)
        {
            try { return p.Id; } catch { return -1; }
        }
    }
}
