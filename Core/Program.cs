using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32; // 添加注册表命名空间

namespace NotchPeninsula
{
    class Program
    {
        public static bool _isDebugMode = false;

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        // 启动时极速加载配置，只在栈上操作，不产生多余GC
        public static void LoadSettings()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
                if (key != null)
                {
                    FontConfig.Restore(key.GetValue("CustomFontPath", "") as string);

                    NotchWindow.IsAutoHideEnabled = (int)key.GetValue("AutoHide", 0) != 0;
                    // 穿透模式不参与判定。
                    NotchWindow.IsFocusAutoHideEnabled =
                        (int)key.GetValue("FocusAutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0) != 0;
                    // 与设置面板显示的状态一一对应。
                    NotchWindow.IsPauseAutoHideEnabled = (int)key.GetValue("PauseAutoHide", 0) != 0;
                    NotchWindow.IsFullscreenAutoHideEnabled = (int)key.GetValue("FullscreenAutoHide", 0) != 0;
                    MediaController.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaController.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                    MediaController.IsManualSessionMatch = (int)key.GetValue("ManualSessionMatch", 0) != 0;
                    MediaController.ManualSessionAppId = (string)key.GetValue("ManualSessionAppId", "") ?? "";
                    MediaController.IsLyricsEnabled = (int)key.GetValue("LyricsEnabled", 1) != 0;
                    MediaController.IsTranslationEnabled = (int)key.GetValue("TranslationEnabled", 1) != 0;
                    bool legacyScan = (int)key.GetValue("KaraokeEnabled", 1) != 0
                                      || (int)key.GetValue("WordByWordEnabled", 0) != 0;
                    MediaController.IsLyricScanEnabled = (int)key.GetValue("LyricScanEnabled", legacyScan ? 1 : 0) != 0;
                    // 一律以注册表里的值为准。
                    MediaController.IsAppLaunchEnabled = (int)key.GetValue("MediaAppLaunchEnabled", 0) != 0;
                    MediaController.LyricDelayOffset = Convert.ToSingle(key.GetValue("LyricDelayOffset", 0f));
                    NotchWindow.IsToastEnabled = (int)key.GetValue("ToastEnabled", 1) != 0;
                    // 「推荐安装网易酷狗时间轴插件」：用户点过拒收就永久静默。
                    NotchWindow.IsTimelinePluginTipDismissed =
                        (int)key.GetValue("TimelinePluginTipDismissed", 0) != 0;
                    // MSP 接入（实验性）：默认关闭 —— 打开它会监听一个本地端口，让本机任何 MSP
                    // 程序都能弹通知、读控系统媒体，属于对外暴露的接口面。
                    MspNotchBridge.IsEnabled = (int)key.GetValue("MspEnabled", 0) != 0;
                    NotchWindow.IsTopmostEnabled = (int)key.GetValue("TopmostEnabled", 1) != 0;
                    int toastContentMode = (int)key.GetValue("ToastContentMode", 1); // 0=缩略, 1=紧凑, 2=完整
                    Renderer.IsToastFullMode = toastContentMode == 2;
                    Renderer.IsToastCompactMode = toastContentMode == 1;

                    // 不会带着坏配置启动。
                    ToastSoundConfig.RefreshBuiltins();
                    ToastSoundConfig.Restore(
                        (int)key.GetValue("ToastSoundIndex", 0),
                        key.GetValue("ToastSoundKey", "") as string ?? "",
                        key.GetValue("ToastSoundPath", "") as string ?? "",
                        (int)key.GetValue("ToastSoundEnabled", 0) != 0,
                        (int)key.GetValue("ToastSoundVolume", ToastSoundConfig.DefaultVolumePercent));

                    // 读取个性化参数
                    Renderer.STANDBY_WIDTH = Convert.ToSingle(key.GetValue("Custom_StandbyW", 125f));
                    Renderer.MEDIA_WIDTH = Convert.ToSingle(key.GetValue("Custom_MediaW", 250f));
                    Renderer.MEDIA_HEIGHT = Convert.ToSingle(
                        key.GetValue("Custom_MediaH", key.GetValue("Custom_BaseH", 35f)));
                    Renderer.TOAST_WIDTH = Convert.ToSingle(key.GetValue("Custom_ToastW", 260f));
                    Renderer.TOAST_HEIGHT = Convert.ToSingle(key.GetValue("Custom_ToastH", 55f));
                    Renderer.GLOBAL_DPI = Convert.ToSingle(key.GetValue("Custom_Dpi", 1.0f));
                    Renderer.NOTCH_BOTTOM_RADIUS = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_NotchBottomR", 12f)), 0f, 28f);
                    Renderer.ThemeMode = (int)key.GetValue("ThemeMode", 0);
                    Renderer.NotchStyle = (int)key.GetValue("NotchStyle", 0);
                    Renderer.MediaInteractionMode = (int)key.GetValue("MediaInteractionMode", 1);
                    Renderer.StandbyDisplayMode = (int)key.GetValue("StandbyDisplayMode", 2);

                    // 待机显示内容：新键存插件 Key（空串 = 空白）。
                    // 老键 StandbyScene 存的是 1/2/3 的场景号，只在没写过新键时用于迁移。
                    string? savedSceneKey = key.GetValue("StandbySceneKey") as string;
                    Renderer.StandbySceneKey = savedSceneKey ?? ((int)key.GetValue("StandbyScene", 1) switch
                    {
                        2 => "",
                        3 => Plugins.BuiltinWidgets.Media,
                        _ => Plugins.BuiltinWidgets.Clock
                    });
                    // 迁移结果立刻写回新键：之后启动不再依赖老键，「自动选中」就固定下来了。
                    if (savedSceneKey == null)
                        Program.SaveSetting("StandbySceneKey", Renderer.StandbySceneKey);
                    Renderer.StandbyToggleByDoubleClick =
                        (int)key.GetValue("StandbyToggleByDoubleClick", 0) != 0;
                    Renderer.StandbyActive = (int)key.GetValue("StandbyActive", 0) != 0;
                    // 显示器拔插过之后注册表里可能留着越界下标（原来选的是第 2 块，现在只剩 1 块）：
                    // 越界会让设置页显示「未知」、岛体只能靠兜底落回主屏。这里就地夹紧并写回。
                    int loadedMonitor = (int)key.GetValue("TargetMonitorIndex", 0);
                    int monitorCount = System.Windows.Forms.Screen.AllScreens.Length;
                    if (loadedMonitor < 0 || (monitorCount > 0 && loadedMonitor >= monitorCount))
                    {
                        loadedMonitor = 0;
                        Program.SaveSetting("TargetMonitorIndex", 0);
                    }
                    Renderer.TargetMonitorIndex = loadedMonitor;
                    Renderer.BgOpacityLevel = (int)key.GetValue("BgOpacityLevel", 4);
                    Renderer.IslandAcrylic = (int)key.GetValue("IslandAcrylic", 0) != 0;
                    Renderer.IslandLiquidGlass = (int)key.GetValue("IslandLiquidGlass", 0) != 0;
                    Renderer.IslandLiquidGlass2 = (int)key.GetValue("IslandLiquidGlass2", 0) != 0;
                    // 三种材质互斥：玻璃2 优先于玻璃，玻璃优先于亚克力
                    if (Renderer.IslandLiquidGlass2) Renderer.IslandLiquidGlass = false;
                    if (Renderer.IslandLiquidGlass) Renderer.IslandAcrylic = false;

                    // 液态玻璃：质量档 + 12 根滑轨（越界一律夹回，老配置/手改注册表都不怕）
                    // 抓屏方式不再读配置：放大镜可用即用、不可用自动回退兼容抓屏。
                    LiquidGlass.Quality = (int)key.GetValue("LiquidGlassQuality", 1);
                    for (int i = 0; i < LiquidGlass.ParamCount; i++)
                        LiquidGlass.SetLevel(i, (int)key.GetValue(LiquidGlass.ParamKeys[i], LiquidGlass.DefaultLevelOf(i)));

                    // 液态玻璃 2：9 根滑轨（质量档与抓屏管线跟液态玻璃共用同一个设置项）
                    for (int i = 0; i < LiquidGlass2.ParamCount; i++)
                        LiquidGlass2.SetLevel(i, (int)key.GetValue(LiquidGlass2.ParamKeys[i], LiquidGlass2.DefaultLevelOf(i)));

                    // 本来就开着的，沿用注册表里的勾选状态。
                    // 不勾的话升级后媒体会整个消失。
                    if ((int)key.GetValue("CompositeMode_Enabled", 0) != 0)
                    {
                        Renderer.CompShowDateTime = (int)key.GetValue("Composite_ShowDateTime", 1) != 0;
                        Renderer.CompShowHardware = (int)key.GetValue("Composite_ShowHardware", 1) != 0;
                        Renderer.CompShowMedia = (int)key.GetValue("Composite_ShowMedia", 1) != 0;
                    }
                    else
                    {
                        Renderer.CompShowDateTime = Renderer.StandbyDisplayMode == 0;
                        Renderer.CompShowHardware = Renderer.StandbyDisplayMode == 2;
                        Renderer.CompShowMedia = true;

                        Program.SaveSetting("CompositeMode_Enabled", 1);
                    }

                    Renderer.PassthroughModeEnabled = (int)key.GetValue("PassthroughMode", 0) != 0;

                    MediaHotkeys.Load(key);
                    IslandHotkey.Load(key);

                    Renderer.ApplyThemeColors(); // 启动时注入颜色
                }

                //（每次系统主题变化触发 N 次重绘）。
                SubscribeSystemPreferenceChanged();
            }
            catch (Exception ex)
            {
                Logger.Error("加载注册表配置失败，将使用默认值", ex);
            }
        }

        private static bool _systemPreferenceSubscribed;

        private static void SubscribeSystemPreferenceChanged()
        {
            if (_systemPreferenceSubscribed) return;
            _systemPreferenceSubscribed = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            //    那时读的必须是切回之后的实时值。
            Renderer.InvalidateSystemThemeCache();

            if (Renderer.ThemeMode == 2)
            {
                Renderer.ApplyThemeColors();
                NotchWindow.RequestAcrylicRebuild();   // 跟系统的明暗变了，亚克力色调跟着换
            }
        }

        // 暴露出保存配置的方法，供控制台UI调用
        public static void SaveSetting(string name, object value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
                key?.SetValue(name, value);
            }
            catch (Exception ex)
            {
                Logger.Error($"保存配置 {name} 失败", ex);
            }
        }

        private static void InstallCrashLogger()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    Exception? ex = e.ExceptionObject as Exception;
                    string detail = ex != null
                        ? $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"
                        : e.ExceptionObject?.ToString() ?? "(null)";

                    Logger.Error($"[崩溃] 未处理异常，进程即将退出（IsTerminating={e.IsTerminating}）{Environment.NewLine}{detail}");

                    // app.log 会被轮转冲掉，崩溃另存一份独立的、带完整异常链与末尾日志的文件
                    Diagnostics.WriteCrash("未处理异常", ex, e.IsTerminating);
                }
                catch
                {
                    // 记日志本身绝不能再抛
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Logger.Error("[崩溃] 未观察的 Task 异常", e.Exception);
                Diagnostics.WriteCrash("未观察的 Task 异常", e.Exception, terminating: false);
            };
        }

        [STAThread]
        static void Main(string[] args)
        {
            InstallCrashLogger();

            using (Mutex mutex = new Mutex(true, "Local\\NotchPeninsula_SingleInstanceMutex", out bool createdNew))
            {
                if (!createdNew)
                {
                    return; // 极速退出，不分配任何多余内存，不执行任何初始化
                }

                // 支持多屏幕不同缩放自动适应
                SetProcessDpiAwarenessContext(new IntPtr(-4));

                // 让「几笔卡住的调用」再也吃不掉整池。
                int minWorkers = Math.Max(16, Environment.ProcessorCount * 2);
                ThreadPool.GetMinThreads(out int curWorkers, out int curIo);
                if (curWorkers < minWorkers) ThreadPool.SetMinThreads(minWorkers, curIo);

                if (args.Length > 0 && args[0] == "-debug")
                {
                    _isDebugMode = true;
                    Logger.Debug("调试模式已启用");
                }

                // 在实例化任何窗口和媒体控制器之前，先将配置注入内存
                LoadSettings();

                // 从没引导过时先走一遍偏好引导（升级不算）：只写注册表，
                // 写完就直接进软件。跳过 / 关闭 / 按 ESC 都算过，失败也绝不拦着启动。
                OnboardingWindow.ShowIfNeeded(args);

                // 启动时异步静默检测更新，不阻塞主线程
                UpdateManager.StartSilentCheck();

                var window = new NotchWindow();
                window.WindowClicked += async (s, e) =>
                {
                    if (window.isToastActive)
                    {
                        Logger.Debug($"窗口点击：X={e.X}, Y={e.Y}");
                        if (window.CurrentToast == null) return;

                            try
                            {
                                var (ok, msg) = await AppActivator.active_app(window.CurrentToast.Aumid);
                                if (!ok)
                                {
                                    Logger.Debug($"[AppActivator] 唤醒失败：{msg}");
                                    // 置前候选先 AUMID 再显示名：AUMID 可能整串匹配不到进程
                                    //（如 WorkBuddy.WorkBuddy），显示名反而就是进程名。
                                    if (!string.IsNullOrWhiteSpace(window.CurrentToast.Aumid))
                                    {
                                        var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(window.CurrentToast.Aumid);
                                        if (!ok2 && !string.IsNullOrWhiteSpace(window.CurrentToast.AppName))
                                            (ok2, msg2) = AppActivator.TryBringToFrontByAppName(window.CurrentToast.AppName);
                                        if (!ok2)
                                            Logger.Debug($"[AppActivator] 按应用名置前失败：{msg2}");
                                    }
                                }else window.clicked_info = true;
                            }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[AppActivator] 异步唤醒异常：{ex.Message}");
                        }
                    }
                };
                // 原生 MSP 接入（实验性，默认关）：失败不致命，MSP 起不来不该拖垮灵动岛本体。
                if (MspNotchBridge.IsEnabled)
                {
                    try { MspNotchBridge.Start(); }
                    catch (Exception ex) { Logger.Error("[MSP] 节点启动失败", ex); }
                }
                else
                {
                    Logger.Info("[MSP] 未启用（设置 → 通用设置 → MSP 接入）");
                }

                window.Run();

                // 走到这里说明消息循环是自己结束的（收到 WM_QUIT），而不是被 Environment.Exit 硬杀的。
                // 这一行必须留着：否则「正常退出」和「退出时被强杀」在 app.log 里长得一模一样
                //（都以「SystemSettingsManager 已释放资源」收尾），出事时根本无从分辨。
                Logger.Info("消息循环已退出，进程正常收尾");

                // 消息循环自己结束（注销 / 关机，或托盘「退出」投来的 WM_CLOSE）时补一次下线。
                try { MspNotchBridge.Stop(); } catch (Exception ex) { Logger.Error("[MSP] 节点停止失败", ex); }
            }; // 离开作用域时，Mutex 的 Dispose() 被自动调用，绝无句柄泄露
        }
    }
}