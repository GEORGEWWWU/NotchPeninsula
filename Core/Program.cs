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
                    // 灵动岛字体：先恢复，保证 Renderer 首次初始化时拿到的就是记忆的字体。
                    // 未选择过自定义字体（默认）或记忆的文件已失效时，保持系统字体，不做任何改动。
                    FontConfig.Restore(key.GetValue("CustomFontPath", "") as string);

                    NotchWindow.IsAutoHideEnabled = (int)key.GetValue("AutoHide", 0) != 0;
                    // 🎵🖥 两个附属开关（暂停播放后 / 全屏时）都是自动隐藏的扩展，默认关，且**互斥**。
                    // 依赖关系：自动隐藏关掉时它们必须也是关的 —— 否则会出现「自动隐藏开关显示关闭、
                    // 岛体却因为暂停/全屏而躲起来」的矛盾状态。
                    // 这里顺手把注册表也修正掉（自愈），保证「内存态 / 注册表 / 面板显示」三者永远一致。
                    NotchWindow.IsPauseAutoHideEnabled = (int)key.GetValue("PauseAutoHide", 0) != 0;
                    NotchWindow.IsFullscreenAutoHideEnabled = (int)key.GetValue("FullscreenAutoHide", 0) != 0;
                    if (!NotchWindow.IsAutoHideEnabled)
                    {
                        // 父开关关着 → 两个附属一律清零
                        if (NotchWindow.IsPauseAutoHideEnabled) { NotchWindow.IsPauseAutoHideEnabled = false; key.SetValue("PauseAutoHide", 0); }
                        if (NotchWindow.IsFullscreenAutoHideEnabled) { NotchWindow.IsFullscreenAutoHideEnabled = false; key.SetValue("FullscreenAutoHide", 0); }
                    }
                    else if (NotchWindow.IsPauseAutoHideEnabled && NotchWindow.IsFullscreenAutoHideEnabled)
                    {
                        // 互斥自愈：两者同时为真（手改注册表 / 旧版本遗留）时只保留「暂停播放后」，
                        // 与面板上「先点谁留谁」的直觉一致，且结果是确定的、不会每次启动都变。
                        NotchWindow.IsFullscreenAutoHideEnabled = false;
                        key.SetValue("FullscreenAutoHide", 0);
                    }
                    MediaController.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaController.IsKaraokeEnabled = (int)key.GetValue("KaraokeEnabled", 1) != 0;
                    MediaController.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                    MediaController.IsManualSessionMatch = (int)key.GetValue("ManualSessionMatch", 0) != 0;
                    MediaController.ManualSessionAppId = (string)key.GetValue("ManualSessionAppId", "") ?? "";
                    MediaController.IsLyricsEnabled = (int)key.GetValue("LyricsEnabled", 1) != 0;
                    MediaController.IsTranslationEnabled = (int)key.GetValue("TranslationEnabled", 1) != 0;
                    MediaController.LyricDelayOffset = Convert.ToSingle(key.GetValue("LyricDelayOffset", 0f));
                    NotchWindow.IsToastEnabled = (int)key.GetValue("ToastEnabled", 1) != 0;
                    NotchWindow.IsClipboardEnabled = (int)key.GetValue("ClipboardEnabled", 1) != 0;
                    NotchWindow.IsTopmostEnabled = (int)key.GetValue("TopmostEnabled", 1) != 0;
                    int toastContentMode = (int)key.GetValue("ToastContentMode", 0); // 0=缩略, 1=紧凑, 2=完整
                    Renderer.IsToastFullMode = toastContentMode == 2;
                    Renderer.IsToastCompactMode = toastContentMode == 1;

                    // 🎵 通知提示音
                    //    ① 先扫目录 —— 下拉列表是**动态加载**的，列表内容取决于 data\sound 里实际有哪些 wav。
                    //       必须在 Restore 之前扫，否则恢复索引时 OptionCount 还是 0，会把有效索引误判成越界。
                    //    ② 再 Restore：与字体同一套「恢复 + 失效自动回落」语义。
                    //       提示音开关默认**关闭**、默认选「无」(index 0)，也就是默认完全安静；
                    //       自定义音频丢失时会自动退回「无」并把失效路径从注册表清掉，不会带着坏配置启动。
                    ToastSoundConfig.RefreshBuiltins();
                    ToastSoundConfig.Restore(
                        (int)key.GetValue("ToastSoundIndex", 0),
                        key.GetValue("ToastSoundPath", "") as string ?? "",
                        (int)key.GetValue("ToastSoundEnabled", 0) != 0,
                        (int)key.GetValue("ToastSoundVolume", 70));

                    // 读取个性化参数
                    Renderer.STANDBY_WIDTH = Convert.ToSingle(key.GetValue("Custom_StandbyW", 125f));
                    Renderer.BASE_HEIGHT = Convert.ToSingle(key.GetValue("Custom_BaseH", 29f));
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