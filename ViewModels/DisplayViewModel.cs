using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NotchPeninsula.Services;

namespace NotchPeninsula.ViewModels;

/// <summary>
/// 显示设置：刘海形态 / 目标显示器 / 待机显示内容 / 自定义组合模式。
/// 索引语义沿用原版：0=时间日期，1=空白，2=硬件占用。
/// </summary>
public partial class DisplayViewModel : ViewModelBase
{
    private const float HardwareModeMinWidth = 170f;
    private const float HardwareModeMinHeight = 34f;
    private const float DefaultStandbyWidth = 130f;

    // 进入硬件检测前的待机宽度快照，-1 表示未记录
    private float _savedStandbyWidth = -1f;

    [ObservableProperty] private int _notchStyle = Renderer.NotchStyle;
    [ObservableProperty] private ObservableCollection<string> _monitorNames = ["显示器 1 (主)"];
    [ObservableProperty] private int _targetMonitorIndex = Renderer.TargetMonitorIndex;
    [ObservableProperty] private int _standbyDisplayMode = Renderer.StandbyDisplayMode;
    [ObservableProperty] private bool _compositeModeEnabled = Renderer.CompositeModeEnabled;
    [ObservableProperty] private bool _compShowDateTime = Renderer.CompShowDateTime;
    [ObservableProperty] private bool _compShowHardware = Renderer.CompShowHardware;
    [ObservableProperty] private bool _compShowMedia = Renderer.CompShowMedia;
    [ObservableProperty] private Avalonia.Media.IBrush _previewFill = ResolvePreviewFill();

    /// <summary>形态预览图的填充色，跟随主题模式（含「跟随系统」）。</summary>
    private static Avalonia.Media.IBrush ResolvePreviewFill()
    {
        bool isLight = Renderer.ThemeMode == 1 ||
                       (Renderer.ThemeMode == 2 && IsSystemUsingLightTheme());

        return isLight ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Black;
    }

    private static bool IsSystemUsingLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    public DisplayViewModel()
    {
        // 启动即处于硬件检测模式时，快照标记为未记录，切走时回退到默认 130px
        if (Renderer.StandbyDisplayMode == 2)
            _savedStandbyWidth = -1f;

        // 跟随系统主题时，系统深浅色切换后同步刷新预览图
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPreview);
    }

    public void RefreshPreview() => PreviewFill = ResolvePreviewFill();

    /// <summary>个性化页重置待机宽度时调用，防止切回时恢复到旧快照。</summary>
    public void ClearStandbySnapshot() => _savedStandbyWidth = -1f;

    partial void OnNotchStyleChanged(int value)
    {
        Renderer.NotchStyle = value;
        Program.SaveSetting(SettingsKeys.NotchStyle, value);
    }

    partial void OnTargetMonitorIndexChanged(int value)
    {
        Renderer.TargetMonitorIndex = value;
        Program.SaveSetting(SettingsKeys.TargetMonitorIndex, value);
    }

    partial void OnStandbyDisplayModeChanged(int value)
    {
        int previous = Renderer.StandbyDisplayMode;
        Renderer.StandbyDisplayMode = value;

        if (value == 2 && previous != 2)
        {
            // 进入硬件检测：先快照，再强制拉宽到最小可用尺寸
            if (_savedStandbyWidth < 0f)
                _savedStandbyWidth = Renderer.STANDBY_WIDTH;

            if (Renderer.STANDBY_WIDTH < HardwareModeMinWidth)
                SaveStandbyWidth(HardwareModeMinWidth);

            if (Renderer.BASE_HEIGHT < HardwareModeMinHeight)
                SaveBaseHeight(HardwareModeMinHeight);
        }
        else if (previous == 2 && value != 2)
        {
            // 离开硬件检测：恢复用户之前的宽度
            SaveStandbyWidth(_savedStandbyWidth > 0f ? _savedStandbyWidth : DefaultStandbyWidth);
            _savedStandbyWidth = -1f;
        }

        Program.SaveSetting(SettingsKeys.StandbyDisplayMode, value);
    }

    partial void OnCompositeModeEnabledChanged(bool value)
    {
        Renderer.CompositeModeEnabled = value;
        Program.SaveSetting(SettingsKeys.CompositeEnabled, value ? 1 : 0);
    }

    partial void OnCompShowDateTimeChanged(bool value)
    {
        Renderer.CompShowDateTime = value;
        Program.SaveSetting(SettingsKeys.CompositeShowDateTime, value ? 1 : 0);
    }

    partial void OnCompShowHardwareChanged(bool value)
    {
        Renderer.CompShowHardware = value;
        Program.SaveSetting(SettingsKeys.CompositeShowHardware, value ? 1 : 0);
    }

    partial void OnCompShowMediaChanged(bool value)
    {
        Renderer.CompShowMedia = value;
        Program.SaveSetting(SettingsKeys.CompositeShowMedia, value ? 1 : 0);
    }

    [RelayCommand]
    private void SelectStyle(string? styleText)
    {
        if (int.TryParse(styleText, out var style))
            NotchStyle = style;
    }

    [RelayCommand]
    private void SelectStandbyMode(string? modeText)
    {
        if (int.TryParse(modeText, out var mode))
            StandbyDisplayMode = mode;
    }

    /// <summary>由 SettingsViewModel 在窗口就绪后注入显示器列表。</summary>
    public void SetMonitors(System.Collections.Generic.IList<string> names)
    {
        MonitorNames = new ObservableCollection<string>(names);

        if (Renderer.TargetMonitorIndex >= MonitorNames.Count)
            TargetMonitorIndex = 0;

        OnPropertyChanged(nameof(TargetMonitorIndex));
    }

    private static void SaveStandbyWidth(float width)
    {
        Renderer.STANDBY_WIDTH = width;
        Program.SaveSetting(SettingsKeys.CustomStandbyW, width);
    }

    private static void SaveBaseHeight(float height)
    {
        Renderer.BASE_HEIGHT = height;
        Program.SaveSetting(SettingsKeys.CustomBaseH, height);
    }
}
