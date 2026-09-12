using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia;

using WindowState = Avalonia.Controls.WindowState;
using NotchPeninsula.ViewModels;
using NotchPeninsula.Views;

namespace NotchPeninsula;

public static class ConsoleWindow
{
    private static readonly object _gate = new();
    private static Thread? _uiThread;
    private static SettingsWindow? _window;
    private static SettingsViewModel? _viewModel;
    private static ManualResetEventSlim? _ready;
    private static volatile bool _pendingShow;

    /// <summary>打开（或唤醒）设置窗口。可从任意线程调用。</summary>
    public static void Toggle()
    {
        EnsureStarted();

        // 窗口已就绪则立刻显示；Avalonia 还在冷启动时挂起请求，
        // 由 Attach() 在初始化完成后补上，避免阻塞调用方（托盘点击会卡住主线程）
        var window = _window;
        if (window != null)
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowCore);
        else
            _pendingShow = true;
    }

    /// <summary>隐藏设置窗口（关闭按钮走这里，不销毁窗口与 Avalonia 生命周期）。</summary>
    public static void Hide()
    {
        if (_window == null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _window.Hide());
    }

    /// <summary>托盘侧改动开机自启后，反向同步设置面板开关。</summary>
    public static void UpdateAutoStartState(bool enable)
    {
        if (_viewModel == null) return; // 面板从未打开过，无需同步
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _viewModel.General.SyncAutoStart(enable));
    }

    /// <summary>在独立 STA 线程上启动 Avalonia（宿主主线程被 Win32 GetMessage 循环占用）。</summary>
    private static void EnsureStarted()
    {
        if (_uiThread != null) return;

        lock (_gate)
        {
            if (_uiThread != null) return;

            var ready = new ManualResetEventSlim(false);
            _ready = ready;
            Exception? bootError = null;

            _uiThread = new Thread(() =>
            {
                try
                {
                    ConsoleApp.BuildAvaloniaApp()
                               .StartWithClassicDesktopLifetime(Array.Empty<string>(),
                                                                ShutdownMode.OnExplicitShutdown);
                }
                catch (Exception ex)
                {
                    bootError = ex; // 由 EnsureStarted 统一记录，避免重复落盘
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "AvaloniaUI"
            };

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();

            // Attach() 在 OnFrameworkInitializationCompleted 中调用，作为「初始化完成」信号
            // 注意：Logger 只暴露了 Error(string, Exception) 与 Debug(string) 两种签名，
            // 这里用 Debug 记录超时，避免引用不存在的重载
            if (!ready.Wait(TimeSpan.FromSeconds(5)))
                Logger.Debug("设置窗口(Avalonia)启动超时：5 秒内未完成初始化");

            if (bootError != null)
                Logger.Error("设置窗口(Avalonia)启动失败", bootError);
        }
    }

    internal static void Attach(SettingsWindow window, SettingsViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _ready?.Set(); // 通知调用方：Avalonia 已就绪，可以安全 Show/Activate

        // 补全冷启动期间挂起的打开请求
        if (_pendingShow)
        {
            _pendingShow = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowCore);
        }
    }

    internal static void Detach()
    {
        _window = null;
        _viewModel = null;
        // 注意：_uiThread 必须保留。Avalonia 生命周期常驻，
        // 清空它会导致下一次 Toggle 试图重复 StartWithClassicDesktopLifetime 而抛异常。
    }

    private static void ShowCore()
    {
        if (_window == null) return;

        if (!_window.IsVisible)
            _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }
}
