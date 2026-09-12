using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using NotchPeninsula.ViewModels;

// 与 System.Windows.Window（WPF）、System.Drawing.Bitmap 消歧
using Window = Avalonia.Controls.Window;
using WindowState = Avalonia.Controls.WindowState;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace NotchPeninsula.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        TryLoadIcon();

        // 显示器列表来自 Avalonia 的屏幕枚举（顺序与 Win32 EnumDisplayMonitors 一致）
        var screens = Screens;
        if (screens != null && DataContext is SettingsViewModel vm)
            vm.RefreshMonitors(screens);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel model)
            {
                model.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
                model.CloseRequested += (_, _) => ConsoleWindow.Hide();

                if (screens != null)
                    model.RefreshMonitors(screens);
            }
        };
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>加载任务栏 / 关于页共用的高清图标，失败时静默降级。</summary>
    private void TryLoadIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "NPS_NotchPeninsula-logo.ico");
            if (!File.Exists(iconPath)) return;

            var bitmap = new Bitmap(iconPath);
            Icon = new WindowIcon(bitmap);

            if (DataContext is SettingsViewModel vm)
                vm.SetAppIcon(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Error("解析高清图标失败", ex);
        }
    }
}
