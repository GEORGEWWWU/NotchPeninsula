using CommunityToolkit.Mvvm.ComponentModel;
using NotchPeninsula.Services;

namespace NotchPeninsula.ViewModels;

/// <summary>通用设置：开机自启 / 系统消息通知 / 窗口置顶。</summary>
public partial class GeneralViewModel : ViewModelBase
{
    private bool _suppressWrite;

    [ObservableProperty] private bool _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
    [ObservableProperty] private bool _isToastEnabled = NotchWindow.IsToastEnabled;
    [ObservableProperty] private bool _isTopmostEnabled = NotchWindow.IsTopmostEnabled;

    partial void OnIsAutoStartEnabledChanged(bool value)
    {
        if (_suppressWrite) return;
        NotchWindow.ToggleAutoStart(value, false);
    }

    partial void OnIsToastEnabledChanged(bool value)
    {
        NotchWindow.IsToastEnabled = value;
        Program.SaveSetting(SettingsKeys.ToastEnabled, value ? 1 : 0);
    }

    partial void OnIsTopmostEnabledChanged(bool value)
    {
        NotchWindow.IsTopmostEnabled = value;
        Program.SaveSetting(SettingsKeys.TopmostEnabled, value ? 1 : 0);

        // 直接调用底层 API 热重载层级，无需重启与重建画布
        Win32.SetWindowPos(NotchWindow.InstanceHandle,
                           value ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
                           0, 0, 0, 0,
                           Win32.SWP_NOMOVE_NOSIZE);
    }

    /// <summary>托盘侧已经写好了注册表，这里只同步 UI，避免回写。</summary>
    public void SyncAutoStart(bool enable)
    {
        if (IsAutoStartEnabled == enable) return;

        _suppressWrite = true;
        IsAutoStartEnabled = enable;
        _suppressWrite = false;
    }
}
