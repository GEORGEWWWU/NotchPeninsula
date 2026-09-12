using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NotchPeninsula.Services;

namespace NotchPeninsula.ViewModels;

/// <summary>交互设置：自动隐藏 / 媒体交互方式 / 穿透模式。</summary>
public partial class InteractionViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isAutoHideEnabled = NotchWindow.IsAutoHideEnabled;
    [ObservableProperty] private bool _isExpandInteraction = Renderer.MediaInteractionMode == 1;
    [ObservableProperty] private bool _isPassthroughEnabled = Renderer.PassthroughModeEnabled;
    [ObservableProperty] private bool _isMediaInteractionLocked = Renderer.CompositeModeEnabled;

    /// <summary>组合模式锁定时的副标题文案。</summary>
    public string MediaInteractionDescription =>
        IsMediaInteractionLocked ? "组合模式下固定为直接交互" : "开启为展开交互，关闭为直接交互";

    public double MediaInteractionOpacity => IsMediaInteractionLocked ? 0.45 : 1.0;

    partial void OnIsAutoHideEnabledChanged(bool value)
    {
        NotchWindow.IsAutoHideEnabled = value;
        Program.SaveSetting(SettingsKeys.AutoHide, value ? 1 : 0);
    }

    partial void OnIsExpandInteractionChanged(bool value)
    {
        Renderer.MediaInteractionMode = value ? 1 : 0;

        // 关闭时强制收起已展开的媒体区
        if (!value) Renderer.IsMediaExpanded = false;

        Program.SaveSetting(SettingsKeys.MediaInteractionMode, Renderer.MediaInteractionMode);
    }

    partial void OnIsMediaInteractionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(MediaInteractionDescription));
        OnPropertyChanged(nameof(MediaInteractionOpacity));
    }

    partial void OnIsPassthroughEnabledChanged(bool value)
    {
        Renderer.PassthroughModeEnabled = value;
        Program.SaveSetting(SettingsKeys.PassthroughMode, value ? 1 : 0);
        Renderer.ApplyThemeColors(); // 立刻刷新基底色
    }

    /// <summary>组合模式开启时，媒体交互方式被锁定为「直接交互」。</summary>
    public void BindTo(DisplayViewModel display)
    {
        display.PropertyChanged += OnDisplayPropertyChanged;
        IsMediaInteractionLocked = display.CompositeModeEnabled;
    }

    private void OnDisplayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DisplayViewModel.CompositeModeEnabled)) return;
        if (sender is DisplayViewModel display)
            IsMediaInteractionLocked = display.CompositeModeEnabled;
    }
}
