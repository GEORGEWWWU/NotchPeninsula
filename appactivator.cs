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
                var packageManager = new Windows.Management.Deployment.PackageManager();
                var packages = packageManager.FindPackagesForUserWithPackageTypes(
                    null,
                    Windows.Management.Deployment.PackageTypes.Main |
                    Windows.Management.Deployment.PackageTypes.Optional);

                foreach (var package in packages)
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

                Logger.Warn($"WinRT 未找到匹配 AUMID 的已安装包：{aumid}");
                return (false, "未在当前用户的已安装包中找到匹配的 AUMID");
            }
            catch (Exception ex)
            {
                Logger.Error($"WinRT 激活异常，AUMID={aumid}", ex);
                return (false, $"WinRT 激活异常：{ex.Message}");
            }
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
            try
            {
                // Instantiate the COM coclass and cast to the interface to get correct signature (int/HRESULT)
                var activator = (IApplicationActivationManager?)new ApplicationActivationManager();
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
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero)
                            continue;
                        if ((!string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowTitle.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                            || p.ProcessName.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            IntPtr h = p.MainWindowHandle;
                            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                            SetForegroundWindow(h);
                            Logger.Info($"已将进程 {p.ProcessName} (PID={p.Id}) 窗口置前");
                            return (true, $"已将进程 {p.ProcessName} 的窗口置前");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"遍历进程 {p.ProcessName} 时异常：{ex.Message}");
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
    }
}
