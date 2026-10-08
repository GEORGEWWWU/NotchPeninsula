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
                    NotchWindow.IsClipboardEnabled = (int)key.GetValue("ClipboardEnabled", 1) != 0;
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

                    // 用新键，与上面那个已被复选框取代的历史键互不干扰。
                    Renderer.StandbyScene = (int)key.GetValue("StandbyScene", 1);
                    Renderer.StandbyToggleByDoubleClick =
                        (int)key.GetValue("StandbyToggleByDoubleClick", 0) != 0;
                    Renderer.StandbyActive = (int)key.GetValue("StandbyActive", 0) != 0;
                    Renderer.TargetMonitorIndex = (int)key.GetValue("TargetMonitorIndex", 0);
                    Renderer.BgOpacityLevel = (int)key.GetValue("BgOpacityLevel", 4);

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
                    string detail = e.ExceptionObject switch
                    {
                        Exception ex => $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}",
                        var other => other?.ToString() ?? "(null)",
                    };
                    Logger.Error($"[崩溃] 未处理异常，进程即将退出（IsTerminating={e.IsTerminating}）{Environment.NewLine}{detail}");
                }
                catch
                {
                    // 记日志本身绝不能再抛
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
                Logger.Error("[崩溃] 未观察的 Task 异常", e.Exception);
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
                                if (!string.IsNullOrWhiteSpace(window.CurrentToast.Aumid))
                                {
                                    var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(window.CurrentToast.Aumid);
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
                window.Run();
            }; // 离开作用域时，Mutex 的 Dispose() 被自动调用，绝无句柄泄露
        }
    }
}