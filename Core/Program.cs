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
                    // 实况窗展示媒体（默认关）：启动只落开关状态，真正的注册等检测到媒体后由控制器走 3 秒预览。
                    MediaLiveWindowController.SetEnabled((int)key.GetValue("MediaLiveWindow", 0) != 0);
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

                    // 读取个性化参数（注册表可能被旧版本/手改写坏：一律夹到安全范围。
                    // 视觉比例原本只有下限没有上限，点到 5x 时画布 10000×1100、每帧约 44MB，
                    // 常驻合成负载被放大一到两个数量级 —— 这里和设置页的加减按钮一起封顶。）
                    Renderer.STANDBY_WIDTH = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_StandbyW", 125f)), 20f, 1200f);
                    Renderer.MEDIA_WIDTH = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_MediaW", 250f)), 20f, 1200f);
                    Renderer.MEDIA_HEIGHT = Math.Clamp(
                        Convert.ToSingle(key.GetValue("Custom_MediaH", key.GetValue("Custom_BaseH", 35f))), 20f, 600f);
                    Renderer.TOAST_WIDTH = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_ToastW", 260f)), 20f, 1200f);
                    Renderer.TOAST_HEIGHT = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_ToastH", 55f)), 20f, 600f);
                    Renderer.GLOBAL_DPI = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_Dpi", 1.0f)), 0.5f, 3.0f);
                    Renderer.NOTCH_BOTTOM_RADIUS = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_NotchBottomR", 12f)), 0f, 28f);
                    Renderer.ThemeMode = (int)key.GetValue("ThemeMode", 0);
                    Renderer.NotchStyle = (int)key.GetValue("NotchStyle", 0);
                    Renderer.MediaInteractionMode = (int)key.GetValue("MediaInteractionMode", 1);
                    Renderer.StandbyDisplayMode = (int)key.GetValue("StandbyDisplayMode", 2);
                    // 液态玻璃 2.0 已下线：旧配置里选着它的一律迁回液态玻璃 1
                    if ((int)key.GetValue("IslandLiquidGlass2", 0) != 0)
                    {
                        Program.SaveSetting("IslandLiquidGlass2", 0);
                        Program.SaveSetting("IslandLiquidGlass", 1);
                    }
                    Renderer.IslandLiquidGlass2 = false;

                    // 背景模糊模式：0 = 原模糊（档位半径），1 = 高斯模糊（档位半径 + 厚高斯）。
                    // 两档的背板都是隐身后的真实桌面 —— 隐不隐身已经跟着「液态玻璃」材质走，
                    // 旧的「玻璃下显示真实桌面」开关值只用来推断默认档，不再单独读取。
                    Renderer.GlassGaussBackdrop =
                        (int)key.GetValue("GlassBackdropBlur", key.GetValue("HideIslandInCapture", 0)) != 0;

                    // 抓屏后端：0 = 默认（DXGI 优先、判死后退 GDI），1 = 强制 DXGI，2 = 只用 GDI。
                    IslandBackdrop.CaptureBackend = (int)key.GetValue("GlassCaptureBackend", 0);

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
                    // 显示器拔插过之后注册表里可能留着越界下标（原来选的是第 2 块，现在只剩 1 块）。
                    // 旧版会就地夹紧并把 0 写回 TargetMonitorIndex —— 但显示器经常只是暂时不在
                    //（KVM/显示器 OSD 切走了输入源、重启后副屏还没唤醒），写回 0 会把用户选的屏
                    // 永久冲掉，显示器回来后岛就永远落在主屏了。
                    // 现在：偏好原样保留在 TargetMonitorIndex 里；本次启动先用记忆值
                    //（TargetMonitorIndex_Remembered，仍越界则 0）兜底，显示器回来后
                    // NotchWindow.UpdateMonitorBounds 会自动恢复。
                    //
                    // 更关键的是：下标不是身份。注销再登录 / KVM 切输入源之后 Screen.AllScreens
                    // 的枚举顺序会整体重排，同一个下标会落到另一块物理屏上（用户看到的就是
                    // 「岛自己跑去别的显示器，选回来下次登录又漂走」）。所以这里优先按
                    // TargetMonitorHardwareId 还原用户真正选的那块屏。
                    int loadedMonitor = (int)key.GetValue("TargetMonitorIndex", 0);
                    int monitorCount = System.Windows.Forms.Screen.AllScreens.Length;
                    int rememberedMonitor = (int)key.GetValue("TargetMonitorIndex_Remembered", -1);
                    string savedMonitorId = key.GetValue("TargetMonitorHardwareId") as string ?? "";
                    int byHardwareId = MonitorIdentity.IndexOfHardwareId(savedMonitorId);
                    if (byHardwareId >= 0)
                    {
                        Renderer.TargetMonitorIndex = byHardwareId;
                        Renderer.RememberedMonitorIndex = byHardwareId;
                        // 顺序重排过：把下标改写成当前枚举里的位置。插件（灵动岛调校）
                        // 读的就是这个下标，跟着变即可，不需要宿主额外通知它。
                        if (byHardwareId != loadedMonitor)
                        {
                            Program.SaveSetting("TargetMonitorIndex", byHardwareId);
                            if (rememberedMonitor != byHardwareId)
                                Program.SaveSetting("TargetMonitorIndex_Remembered", byHardwareId);
                            Logger.Info($"[显示器] 按硬件 ID 还原目标显示器：下标 {loadedMonitor} → {byHardwareId}（{savedMonitorId}）");
                        }
                    }
                    else if (string.IsNullOrEmpty(savedMonitorId)
                             && monitorCount > 0 && loadedMonitor >= 0 && loadedMonitor < monitorCount)
                    {
                        // 老配置（还没记过硬件 ID）：只能先按旧下标走，
                        // 同时把当前这块屏的身份补记下来，之后就靠硬件 ID 认屏了。
                        Renderer.TargetMonitorIndex = loadedMonitor;
                        Renderer.RememberedMonitorIndex = loadedMonitor;
                        if (rememberedMonitor != loadedMonitor)
                            Program.SaveSetting("TargetMonitorIndex_Remembered", loadedMonitor);
                        string? hwid = MonitorIdentity.HardwareIdOfIndex(loadedMonitor);
                        if (!string.IsNullOrEmpty(hwid)) Program.SaveSetting("TargetMonitorHardwareId", hwid);
                    }
                    else
                    {
                        // 有硬件 ID 但此刻解析不到（目标屏还没唤醒 / 被 KVM 切走了）：
                        // 绝不能退回去用注册表里的裸下标 —— 它在这个进程里可能指向完全
                        // 另一块屏。先落到记忆值或主屏，只在内存里兜底，不写注册表；
                        // 目标屏一上线，UpdateMonitorBounds 会按硬件 ID 自己纠正回来。
                        int fallback = (monitorCount > 0 && rememberedMonitor >= 0 && rememberedMonitor < monitorCount)
                            ? rememberedMonitor
                            : MonitorIdentity.PrimaryIndex();
                        if (fallback < 0) fallback = 0;
                        Renderer.TargetMonitorIndex = fallback;
                        Renderer.RememberedMonitorIndex = rememberedMonitor;
                        Logger.Info($"[显示器] 目标屏暂时解析不到（{savedMonitorId}），本次先兜底到下标 {fallback}");
                    }

                    // 枚举顺序在各次进程之间会浮动，把这份清单打出来，排查时才有据可依。
                    var allScreens = System.Windows.Forms.Screen.AllScreens;
                    for (int mi = 0; mi < allScreens.Length; mi++)
                    {
                        var sc = allScreens[mi];
                        Logger.Info($"[显示器] [{mi}] {sc.DeviceName} primary={sc.Primary} bounds={sc.Bounds} hwid={MonitorIdentity.HardwareIdOfDevice(sc.DeviceName)}");
                    }
                    // 背景透明度分材质各存一份：纯色沿用老键 BgOpacityLevel，亚克力 / 液态玻璃各用新键
                    // （液态玻璃只是记忆位，默认 100%）。越界档位会让滑轨画到轨道外、且没有选中圆点，必须夹回。
                    Renderer.SolidBgOpacity = Math.Clamp((int)key.GetValue("BgOpacityLevel", 4), 0, 4);
                    Renderer.AcrylicBgOpacity = Math.Clamp((int)key.GetValue("BgOpacityAcrylic", 4), 0, 4);
                    Renderer.GlassBgOpacity = Math.Clamp((int)key.GetValue("BgOpacityGlass", 4), 0, 4);
                    Renderer.IslandAcrylic = (int)key.GetValue("IslandAcrylic", 0) != 0;
                    Renderer.IslandLiquidGlass = (int)key.GetValue("IslandLiquidGlass", 0) != 0;
                    // 液态玻璃 2.0 已下线（字段留着是为了不改动渲染侧的分支），永远不选它
                    Renderer.IslandLiquidGlass2 = false;
                    // 两种材质互斥：玻璃优先于亚克力
                    if (Renderer.IslandLiquidGlass) Renderer.IslandAcrylic = false;
                    // 捕获隐身现在是抓屏线程按需瞬时打开的（见 Renderer.SetCaptureHide），
                    // 这里只保证启动时是"不隐身"，窗口建好后再落实一次亲和性。
                    Renderer.ResetCaptureAffinity();

                    // 液态玻璃：质量档 + 12 根滑轨（越界一律夹回，老配置/手改注册表都不怕）
                    // 抓屏方式不读配置：优先 DXGI，不可用自动回退 GDI。
                    LiquidGlass.Quality = (int)key.GetValue("LiquidGlassQuality", 1);
                    for (int i = 0; i < LiquidGlass.ParamCount; i++)
                        LiquidGlass.SetLevel(i, (int)key.GetValue(LiquidGlass.ParamKeys[i], LiquidGlass.DefaultLevelOf(i)));

                    // 液态玻璃 2：11 根滑轨（9 根材质参数 + 与液态玻璃 1 共用的「抓屏刷新」「岛体帧率」）
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

        /// <summary>
        /// 把「用户选中的显示器」一次性写全：当前下标、记忆值、硬件 ID（身份）。
        /// 任何入口改显示器都该走这里，只写 TargetMonitorIndex 会在枚举顺序重排后指向别的屏。
        /// </summary>
        public static void SaveTargetMonitor(int index)
        {
            Renderer.TargetMonitorIndex = index;
            Renderer.RememberedMonitorIndex = index;
            SaveSetting("TargetMonitorIndex", index);
            SaveSetting("TargetMonitorIndex_Remembered", index);
            string? hwid = MonitorIdentity.HardwareIdOfIndex(index);
            // 必须覆盖写：万一这次没解析出身份，留着上一块屏的硬件 ID 会让用户的新选择
            // 被旧屏拉回去（下一次解析仍认旧屏）。
            SaveSetting("TargetMonitorHardwareId", hwid ?? "");
            Logger.Info($"[显示器] 用户选择目标显示器 → 下标 {index}（{hwid}）");
            // 关键：拓扑没变，NotchWindow 里按拓扑签名缓存的硬件 ID 解析结果不会自动失效。
            // 不作废就会在下一帧把 TargetMonitorIndex 改回旧屏，表现成「选完瞬间跳回」。
            NotchWindow.OnTargetMonitorPreferenceChanged();
        }

        /// <summary>读回用户选中的那块屏的硬件 ID（身份），没存过返回空串。</summary>
        public static string SavedTargetMonitorHardwareId()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula");
                return key?.GetValue("TargetMonitorHardwareId") as string ?? "";
            }
            catch { return ""; }
        }

        /// <summary>当前下标指向的屏，是否就是用户选中的那一块（以硬件 ID 为准）。</summary>
        public static bool IsTargetMonitor(int index)
        {
            string saved = SavedTargetMonitorHardwareId();
            if (string.IsNullOrEmpty(saved)) return true;   // 老配置没有身份，无从校验，按兼容处理
            string? cur = MonitorIdentity.HardwareIdOfIndex(index);
            return !string.IsNullOrEmpty(cur) && string.Equals(cur, saved, StringComparison.OrdinalIgnoreCase);
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
                                var toast = window.CurrentToast;

                                // 通知自带启动协议时优先交给应用自己（QQ 是 ntqq-notification://…）。
                                // 后台那趟扫描可能还没跑到，点击时现补一刀，几十毫秒。
                                if (string.IsNullOrEmpty(toast.LaunchUri))
                                {
                                    string? late = ToastAvatarProbe.TryResolveLaunchNow(toast.Title, toast.Body);
                                    if (!string.IsNullOrEmpty(late)) toast.LaunchUri = late;
                                }

                                var (ok, msg) = await AppActivator.active_app(
                                    toast.Aumid, toast.AppName, toast.Title, toast.LaunchUri,
                                    toast.ConversationName);

                                if (ok) window.clicked_info = true;
                                else Logger.Warn($"[AppActivator] 唤醒失败：{msg}");
                            }
                        catch (Exception ex)
                        {
                            Logger.Error("[AppActivator] 异步唤醒异常", ex);
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

#if DEBUG
                // 实况窗视觉验收开关：Release 不编译演示代码，正常用户永远看不到这些气泡。
                if (Environment.GetEnvironmentVariable("NPS_LIVE_DEMO") == "1") LiveWindowDemo.Start();
#endif

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