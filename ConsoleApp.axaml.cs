using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NotchPeninsula.ViewModels;
using NotchPeninsula.Views;

// 与 System.Windows.Forms.Application / System.Windows.Application 消歧
using Application = Avalonia.Application;

namespace NotchPeninsula;

/// <summary>
/// 设置面板专用的 Avalonia Application。
/// 宿主是一个 Win32 消息循环程序（NotchWindow.Run），因此本 Application 由
/// ConsoleWindow 在独立 STA 线程上启动，不接管进程主循环。
/// </summary>
public partial class ConsoleApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ConsoleApp>()
                     .UsePlatformDetect()
                     .LogToTrace();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 设置窗口关闭时只隐藏、不退出 Avalonia 生命周期，保证反复开关的低开销
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var vm = new SettingsViewModel();
            var window = new SettingsWindow
            {
                DataContext = vm
            };

            // 拦截真实关闭：Avalonia 一旦关停就无法在同进程二次启动，所以永远只隐藏
            window.Closing += (_, e) => { e.Cancel = true; window.Hide(); };

            desktop.MainWindow = window;
            ConsoleWindow.Attach(window, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
