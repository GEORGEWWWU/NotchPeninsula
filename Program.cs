using System.Runtime.InteropServices;
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
                    MediaController.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaController.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                    NotchWindow.IsToastEnabled = (int)key.GetValue("ToastEnabled", 1) != 0;

                    // 读取个性化参数
                    Renderer.STANDBY_WIDTH = Convert.ToSingle(key.GetValue("Custom_StandbyW", 130f));
                    Renderer.BASE_HEIGHT = Convert.ToSingle(key.GetValue("Custom_BaseH", 34f));
                    Renderer.MEDIA_WIDTH = Convert.ToSingle(key.GetValue("Custom_MediaW", 260f));
                    Renderer.MEDIA_HEIGHT = Convert.ToSingle(key.GetValue("Custom_MediaH", 34f));
                    Renderer.TOAST_WIDTH = Convert.ToSingle(key.GetValue("Custom_ToastW", 260f));
                    Renderer.TOAST_HEIGHT = Convert.ToSingle(key.GetValue("Custom_ToastH", 55f));
                    Renderer.GLOBAL_DPI = Convert.ToSingle(key.GetValue("Custom_Dpi", 1.0f));
                    Renderer.ThemeMode = (int)key.GetValue("ThemeMode", 0);
                    Renderer.NotchStyle = (int)key.GetValue("NotchStyle", 0);
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
                window.Run();
            } // 离开作用域时，Mutex 的 Dispose() 被自动调用，绝无句柄泄露
        }
    }
}