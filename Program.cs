using System;
using System.Runtime.InteropServices;
using Microsoft.Win32; // ★ 添加注册表命名空间

namespace NotchPeninsula
{
    class Program
    {
        public static bool _isDebugMode = false;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        // ★ 核心1：启动时极速加载配置，只在栈上操作，不产生多余GC
        public static void LoadSettings()
        {
            try
            {
                // CreateSubKey 保证了如果键不存在会自动创建，并以写入/读取权限打开
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
                if (key != null)
                {
                    // 使用 int 来存 bool，读取速度极快
                    NotchWindow.IsAutoHideEnabled = (int)key.GetValue("AutoHide", 0) != 0;
                    MediaController.IsMediaControlEnabled = (int)key.GetValue("MediaControl", 1) != 0;
                    MediaController.TargetPlatform = (string)key.GetValue("TargetPlatform", "other") ?? "other";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载注册表配置失败，将使用默认值", ex);
            }
        }

        // ★ 核心2：暴露出保存配置的方法，供控制台UI调用
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
            SetProcessDPIAware();
            if (args.Length > 0 && args[0] == "-debug")
            {
                _isDebugMode = true;
                Logger.Debug("调试模式已启用");
            }

            // ★ 核心3：在实例化任何窗口和媒体控制器之前，先将配置注入内存
            LoadSettings();

            var window = new NotchWindow();
            window.Run();
        }
    }
}