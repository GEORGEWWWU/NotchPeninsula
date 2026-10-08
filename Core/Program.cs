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
                    // 「自动隐藏」总开关是下面三个模式的父开关（见 NotchWindow 的一组 Effective 属性）；
                    // 穿透模式不参与判定。
                    // 「焦点离开时自动隐藏岛」在没有媒体会话时接管岛体的显示 / 隐藏。老用户注册表里没有
                    // FocusAutoHide（新键），这里以旧的 AutoHide 值兜底，免得升级后自动隐藏悄悄失效。
                    NotchWindow.IsFocusAutoHideEnabled =
                        (int)key.GetValue("FocusAutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0) != 0;
                    // 三个模式互相独立、可任意组合，启动时不做任何级联 / 互斥自愈，三个值原样恢复，
                    // 与设置面板显示的状态一一对应。
                    NotchWindow.IsPauseAutoHideEnabled = (int)key.GetValue("PauseAutoHide", 0) != 0;
                    NotchWindow.IsFullscreenAutoHideEnabled = (int)key.GetValue("FullscreenAutoHide", 0) != 0;
                    MediaController.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaController.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                    MediaController.IsManualSessionMatch = (int)key.GetValue("ManualSessionMatch", 0) != 0;
                    MediaController.ManualSessionAppId = (string)key.GetValue("ManualSessionAppId", "") ?? "";
                    MediaController.IsLyricsEnabled = (int)key.GetValue("LyricsEnabled", 1) != 0;
                    MediaController.IsTranslationEnabled = (int)key.GetValue("TranslationEnabled", 1) != 0;
                    // 歌词扫光：一个总闸控制「歌词要不要随演唱进度扫光」。内部是一条链——逐字优先，
                    // 逐字数据不可用时自动回退整行均匀推进（见 MediaController.ComputeScanProgress），
                    // 所以不再有「卡拉 OK / 逐字」两个开关。老配置把它分在 KaraokeEnabled（整行扫光，
                    // 默认 1）与 WordByWordEnabled（逐字，默认 0）两个互斥的键上，这里取「任一为真」兜底，
                    // 开过逐字的用户不会因为卡拉 OK 键被置 0 而丢掉扫光。
                    bool legacyScan = (int)key.GetValue("KaraokeEnabled", 1) != 0
                                      || (int)key.GetValue("WordByWordEnabled", 0) != 0;
                    MediaController.IsLyricScanEnabled = (int)key.GetValue("LyricScanEnabled", legacyScan ? 1 : 0) != 0;
                    // 双击媒体控制跳回对应应用：默认关闭。老版本升级上来的用户注册表里没有
                    // MediaAppLaunchEnabled 这个键，取默认值即视为关闭——跳转会抢前台焦点，不该
                    // 不告而开；同时他们的折叠态展开入口因此保持在「左键单击展开」（跳转关掉时的
                    // 口径，见 Renderer.MediaExpandByLeftClick）。用户手动开过（1）或关过（0）的，
                    // 一律以注册表里的值为准。
                    MediaController.IsAppLaunchEnabled = (int)key.GetValue("MediaAppLaunchEnabled", 0) != 0;
                    MediaController.LyricDelayOffset = Convert.ToSingle(key.GetValue("LyricDelayOffset", 0f));
                    NotchWindow.IsToastEnabled = (int)key.GetValue("ToastEnabled", 1) != 0;
                    NotchWindow.IsClipboardEnabled = (int)key.GetValue("ClipboardEnabled", 1) != 0;
                    NotchWindow.IsTopmostEnabled = (int)key.GetValue("TopmostEnabled", 1) != 0;
                    int toastContentMode = (int)key.GetValue("ToastContentMode", 1); // 0=缩略, 1=紧凑, 2=完整
                    Renderer.IsToastFullMode = toastContentMode == 2;
                    Renderer.IsToastCompactMode = toastContentMode == 1;

                    // 通知提示音：先扫目录——下拉列表是动态加载的，内容取决于 data\sound 里实际有哪些 wav。
                    // 必须在 Restore 之前扫，否则恢复索引时 OptionCount 还是 0，会把有效索引误判成越界。
                    // 再 Restore：与字体同一套「恢复 + 失效自动回落」语义。提示音开关默认关闭、默认选
                    // 「无」(index 0)，即完全安静；自定义音频丢失时自动退回「无」并清掉注册表里的失效路径，
                    // 不会带着坏配置启动。
                    ToastSoundConfig.RefreshBuiltins();
                    ToastSoundConfig.Restore(
                        (int)key.GetValue("ToastSoundIndex", 0),
                        // 内置音的文件名身份（老版本注册表没有这个值 → 空串，会按索引一次性迁移）
                        key.GetValue("ToastSoundKey", "") as string ?? "",
                        key.GetValue("ToastSoundPath", "") as string ?? "",
                        (int)key.GetValue("ToastSoundEnabled", 0) != 0,
                        (int)key.GetValue("ToastSoundVolume", ToastSoundConfig.DefaultVolumePercent));

                    // 读取个性化参数
                    Renderer.STANDBY_WIDTH = Convert.ToSingle(key.GetValue("Custom_StandbyW", 125f));
                    Renderer.MEDIA_WIDTH = Convert.ToSingle(key.GetValue("Custom_MediaW", 250f));
                    // 全局折叠态高度：原「待机高度」与「媒体激活时高度」已合并，唯一真源 = MEDIA_HEIGHT，
                    // 存储沿用原媒体控制的 Custom_MediaH（老用户的媒体高度照常生效）。
                    // 向下兼容：老版本两个高度分开存，用户可能只调过待机高度（Custom_BaseH）而没碰过
                    // 媒体高度——那样 Custom_MediaH 键压根不存在，直接取默认值会把他调过的高度抹掉。
                    // 所以 Custom_MediaH 缺席时回落到 Custom_BaseH，都没有才用默认 35。
                    // 迁移是只读的：Custom_BaseH 留在注册表里不动（回退老版本仍能读到），用户下次调高度
                    // 时把新值写进 Custom_MediaH，此后一律以它为准。
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

                    // 待机模式：显示内容（1=只显示时间 / 2=空白 / 3=折叠媒体控制）与「双击空白切换」开关。
                    // 用新键，与上面那个已被复选框取代的历史键互不干扰；待机的进入 / 退出是运行时状态，不持久化。
                    Renderer.StandbyScene = (int)key.GetValue("StandbyScene", 1);
                    Renderer.StandbyToggleByDoubleClick =
                        (int)key.GetValue("StandbyToggleByDoubleClick", 0) != 0;
                    Renderer.TargetMonitorIndex = (int)key.GetValue("TargetMonitorIndex", 0);
                    Renderer.BgOpacityLevel = (int)key.GetValue("BgOpacityLevel", 4);

                    // 组合模式已常开（总开关已移除），这里只负责把老配置迁移成复选框初值。老版本没开过
                    // 组合模式的用户，其「待机显示内容」三选一正是「复选框只勾一个」，直接按它换算；
                    // 本来就开着的，沿用注册表里的勾选状态。
                    // 媒体控制器一律勾上：旧的非组合模式下「媒体一激活就显示」，与待机显示内容无关，
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

                        // 迁移只做一次：把 CompositeMode_Enabled 置 1 并落盘，否则下次启动又会走这条
                        // 分支、拿 StandbyDisplayMode 重新推导，把用户在新版本里新勾的选项覆盖掉
                        //（StandbyDisplayMode 已不再被任何 UI 修改）。
                        Program.SaveSetting("CompositeMode_Enabled", 1);
                    }

                    Renderer.PassthroughModeEnabled = (int)key.GetValue("PassthroughMode", 0) != 0;

                    // 媒体全局快捷键：这里只把「开关 + 键位」读回内存，注册要等宿主窗口建好
                    //（NotchWindow 里 Attach）—— 注册得有个有效句柄当宿主。
                    MediaHotkeys.Load(key);

                    Renderer.ApplyThemeColors(); // 启动时注入颜色
                }

                // 监听 Windows 系统偏好设置（如深浅色主题）变更事件。
                // 必须是具名静态方法 + 幂等订阅：SystemEvents 的委托挂在进程级静态表上，每次
                // Subscribe 都会累加一条，用 lambda 则连退订都无从下手。现在 LoadSettings 只在启动时
                // 调一次所以不会漏，但它是 public static——将来加一个「重新载入配置」入口就会静默累积
                //（每次系统主题变化触发 N 次重绘）。
                SubscribeSystemPreferenceChanged();
            }
            catch (Exception ex)
            {
                Logger.Error("加载注册表配置失败，将使用默认值", ex);
            }
        }

        /// <summary>是否已订阅系统偏好变更（幂等闸门，见调用点的说明）。</summary>
        private static bool _systemPreferenceSubscribed;

        /// <summary>订阅 Windows 系统偏好设置变更（深浅色主题等），重复调用只会生效一次。</summary>
        private static void SubscribeSystemPreferenceChanged()
        {
            if (_systemPreferenceSubscribed) return;
            _systemPreferenceSubscribed = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 系统偏好变了，主题缓存先作废（含「跟随系统」下真实明暗已翻面的情况），
            //    否则 ApplyThemeColors 拿到的是缓存里的旧值。作废放在判断之前：
            //    当前是手动黑 / 手动白时也要作废 —— 用户随时可能切回「跟随系统」，
            //    那时读的必须是切回之后的实时值。
            Renderer.InvalidateSystemThemeCache();

            // 当前设为「跟随系统(2)」时，系统主题改变即重新渲染颜色
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

        /// <summary>
        /// 装一个全局崩溃钩子，把「进程为什么没了」写进 app.log。
        ///
        /// 没有它的时候，一次 0xC0000005 会让进程当场消失、日志里一行都不留，事后只能靠猜。
        /// 这里只记不拦：崩溃照旧让进程退出，但至少留下异常类型、消息与调用栈。
        /// 局限：原生访问违例属于「损坏状态异常」，运行时可能根本不派发这个事件，所以它不能保证
        /// 每次都记到；主要覆盖托管未处理异常与渲染 / UI 线程里的托管异常。
        /// </summary>
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

                // 线程池下限兜底：本进程有一批「会阻塞的 COM 调用」（SMTC 的 GetTimelineProperties /
                // GetPlaybackInfo / TryGetMediaPropertiesAsync），部分播放器在切歌、弹会员窗这类时刻
                // 能把调用线程挂住好几秒。而通知轮询 / 音量看门狗 / 插件定时器 / 托盘菜单都跑在线程池上 ——
                // 一旦被几笔卡住的调用占满，它们就集体迟滞。把下限抬到 2×CPU（至少 16），
                // 让「几笔卡住的调用」再也吃不掉整池。
                // 注意：这是兜底、不是修复 —— 真正的修复是「渲染节拍不再依赖线程池」
                //（见 NotchWindow._renderThread）与「接管重挑的合并闸」（见 MediaController.UpdateSession）。
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