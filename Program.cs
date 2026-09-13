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
                    NotchWindow.IsAutoHideEnabled = (int)key.GetValue("AutoHide", 0) != 0;
                    MediaSettings.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaSettings.IsKaraokeEnabled = (int)key.GetValue("KaraokeEnabled", 1) != 0;
                    MediaSettings.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                    MediaSettings.IsLyricsEnabled = (int)key.GetValue("LyricsEnabled", 1) != 0;
                    MediaSettings.LyricDelayOffset = Convert.ToSingle(key.GetValue("LyricDelayOffset", 0f));
                    NotchWindow.IsToastEnabled = (int)key.GetValue("ToastEnabled", 1) != 0;
                    NotchWindow.IsTopmostEnabled = (int)key.GetValue("TopmostEnabled", 1) != 0;
                    NotchWindow.IsClipboardLinkEnabled = (int)key.GetValue("ClipboardLinkEnabled", 1) != 0;

                    // 读取个性化参数
                    Renderer.STANDBY_WIDTH = Convert.ToSingle(key.GetValue("Custom_StandbyW", 130f));
                    Renderer.BASE_HEIGHT = Convert.ToSingle(key.GetValue("Custom_BaseH", 34f));
                    Renderer.MEDIA_WIDTH = Convert.ToSingle(key.GetValue("Custom_MediaW", 250f));
                    Renderer.MEDIA_HEIGHT = Convert.ToSingle(key.GetValue("Custom_MediaH", 35f));
                    Renderer.TOAST_WIDTH = Convert.ToSingle(key.GetValue("Custom_ToastW", 260f));
                    Renderer.TOAST_HEIGHT = Convert.ToSingle(key.GetValue("Custom_ToastH", 55f));
                    Renderer.GLOBAL_DPI = Convert.ToSingle(key.GetValue("Custom_Dpi", 1.0f));
                    Renderer.NOTCH_BOTTOM_RADIUS = Math.Clamp(Convert.ToSingle(key.GetValue("Custom_NotchBottomR", 12f)), 0f, 28f);
                    Renderer.ThemeMode = (int)key.GetValue("ThemeMode", 0);
                    Renderer.NotchStyle = (int)key.GetValue("NotchStyle", 0);
                    Renderer.MediaInteractionMode = (int)key.GetValue("MediaInteractionMode", 1);
                    Renderer.StandbyDisplayMode = (int)key.GetValue("StandbyDisplayMode", 2);
                    Renderer.TargetMonitorIndex = (int)key.GetValue("TargetMonitorIndex", 0);
                    Renderer.BgOpacityLevel = (int)key.GetValue("BgOpacityLevel", 4);
                    Renderer.CompositeModeEnabled = (int)key.GetValue("CompositeMode_Enabled", 0) != 0;
                    Renderer.CompShowDateTime = (int)key.GetValue("Composite_ShowDateTime", 1) != 0;
                    Renderer.CompShowHardware = (int)key.GetValue("Composite_ShowHardware", 1) != 0;
                    Renderer.CompShowMedia = (int)key.GetValue("Composite_ShowMedia", 1) != 0;
                    Renderer.PassthroughModeEnabled = (int)key.GetValue("PassthroughMode", 0) != 0;

                    Renderer.ApplyThemeColors(); // 启动时注入颜色
                }

                // 监听 Windows 系统偏好设置（如深浅色主题）变更事件
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    // 如果当前设置了“跟随系统(2)”，当系统主题改变时立即重新渲染颜色
                    if (Renderer.ThemeMode == 2)
                    {
                        Renderer.ApplyThemeColors();
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Error("加载注册表配置失败，将使用默认值", ex);
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

        [STAThread]
        static void Main(string[] args)
        {
            // 全局未处理异常日志（诊断闪退）
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Logger.Error("未处理异常(UnhandledException)", e.ExceptionObject as Exception); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { Logger.Error("未观察任务异常(UnobservedTaskException)", e.Exception); e.SetObserved(); } catch { }
            };

            // 使用 using 包裹 Mutex，确保底层系统句柄被严格释放
            using (Mutex mutex = new Mutex(true, "Local\\NotchPeninsula_SingleInstanceMutex", out bool createdNew))
            {
                // 如果 createdNew 为 false，说明内核中已经存在同名 Mutex（已有实例在运行）
                if (!createdNew)
                {
                    return; // 极速退出，不分配任何多余内存，不执行任何初始化
                }

                // 支持多屏幕不同缩放自动适应
                SetProcessDpiAwarenessContext(new IntPtr(-4));

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
                                // fallback: try to bring a running process forward by app name
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