using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NotchPeninsula.Services;

namespace NotchPeninsula.ViewModels;

/// <summary>
/// 个性化中心：主题 / 背景透明度 / 八项尺寸微调。
/// 索引沿用原版 _customValues：0 待机宽、1 待机高、2 媒体宽、3 媒体高、
/// 4 通知宽、5 通知高、6 全局 DPI、7 底部圆角。
/// </summary>
public partial class PersonalizationViewModel : ViewModelBase
{
    private static readonly float[] Defaults = [130f, 34f, 250f, 35f, 260f, 55f, 1.0f, 12f];

    private const float HardwareModeMinWidth = 170f;
    private const float HardwareModeMinHeight = 34f;

    private DisplayViewModel? _display;

    [ObservableProperty] private int _themeMode = Renderer.ThemeMode;
    [ObservableProperty] private int _bgOpacityLevel = Renderer.BgOpacityLevel;
    [ObservableProperty] private bool _isOpacityEnabled = !Renderer.PassthroughModeEnabled;

    [ObservableProperty] private float _standbyWidth = Renderer.STANDBY_WIDTH;
    [ObservableProperty] private float _baseHeight = Renderer.BASE_HEIGHT;
    [ObservableProperty] private float _mediaWidth = Renderer.MEDIA_WIDTH;
    [ObservableProperty] private float _mediaHeight = Renderer.MEDIA_HEIGHT;
    [ObservableProperty] private float _toastWidth = Renderer.TOAST_WIDTH;
    [ObservableProperty] private float _toastHeight = Renderer.TOAST_HEIGHT;
    [ObservableProperty] private float _globalDpi = Renderer.GLOBAL_DPI;
    [ObservableProperty] private float _notchBottomRadius = Renderer.NOTCH_BOTTOM_RADIUS;

    public bool IsStandbyModified => IsModified(0) || IsModified(1) || IsModified(7);
    public bool IsMediaModified => IsModified(2) || IsModified(3);
    public bool IsToastModified => IsModified(4) || IsModified(5);
    public bool IsDpiModified => IsModified(6);

    public void BindTo(InteractionViewModel interaction, DisplayViewModel display)
    {
        _display = display;
        IsOpacityEnabled = !interaction.IsPassthroughEnabled;

        interaction.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InteractionViewModel.IsPassthroughEnabled))
                IsOpacityEnabled = !interaction.IsPassthroughEnabled;
        };

        display.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DisplayViewModel.StandbyDisplayMode))
                RefreshFromRenderer();
        };
    }

    partial void OnThemeModeChanged(int value)
    {
        Renderer.ThemeMode = value;
        Renderer.ApplyThemeColors(); // 立即反转刘海配色
        Program.SaveSetting(SettingsKeys.ThemeMode, value);
    }

    partial void OnBgOpacityLevelChanged(int value)
    {
        Renderer.BgOpacityLevel = value;
        Renderer.ApplyThemeColors();
        Program.SaveSetting(SettingsKeys.BgOpacityLevel, value);
    }

    partial void OnStandbyWidthChanged(float value) => Apply(0, value, SettingsKeys.CustomStandbyW, v => Renderer.STANDBY_WIDTH = v);
    partial void OnBaseHeightChanged(float value) => Apply(1, value, SettingsKeys.CustomBaseH, v => Renderer.BASE_HEIGHT = v);
    partial void OnMediaWidthChanged(float value) => Apply(2, value, SettingsKeys.CustomMediaW, v => Renderer.MEDIA_WIDTH = v);
    partial void OnMediaHeightChanged(float value) => Apply(3, value, SettingsKeys.CustomMediaH, v => Renderer.MEDIA_HEIGHT = v);
    partial void OnToastWidthChanged(float value) => Apply(4, value, SettingsKeys.CustomToastW, v => Renderer.TOAST_WIDTH = v);
    partial void OnToastHeightChanged(float value) => Apply(5, value, SettingsKeys.CustomToastH, v => Renderer.TOAST_HEIGHT = v);
    partial void OnGlobalDpiChanged(float value) => Apply(6, value, SettingsKeys.CustomDpi, v => Renderer.GLOBAL_DPI = v);
    partial void OnNotchBottomRadiusChanged(float value) => Apply(7, value, SettingsKeys.CustomNotchBottomR, v => Renderer.NOTCH_BOTTOM_RADIUS = v);

    [RelayCommand]
    private void SetTheme(string? themeText)
    {
        if (int.TryParse(themeText, out var theme))
            ThemeMode = theme;
    }

    /// <summary>
    /// 尺寸微调。参数格式 "索引|动作"，动作为 minus / plus / reset。
    /// </summary>
    [RelayCommand]
    private void Adjust(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter)) return;

        var parts = parameter.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int index)) return;

        if (index < 0 || index >= Defaults.Length) return;

        float next = parts[1] switch
        {
            "reset" => ResetValue(index),
            "minus" => Step(index, -1),
            "plus" => Step(index, +1),
            _ => ValueOf(index)
        };

        if (Math.Abs(next - ValueOf(index)) < 0.0001f) return;

        switch (index)
        {
            case 0: StandbyWidth = next; break;
            case 1: BaseHeight = next; break;
            case 2: MediaWidth = next; break;
            case 3: MediaHeight = next; break;
            case 4: ToastWidth = next; break;
            case 5: ToastHeight = next; break;
            case 6: GlobalDpi = next; break;
            case 7: NotchBottomRadius = next; break;
        }

        RaiseModifiedFlags();
    }

    /// <summary>外部（显示页切换硬件检测模式）改动了 Renderer 后，把值拉回界面。</summary>
    public void RefreshFromRenderer()
    {
        StandbyWidth = Renderer.STANDBY_WIDTH;
        BaseHeight = Renderer.BASE_HEIGHT;
        MediaWidth = Renderer.MEDIA_WIDTH;
        MediaHeight = Renderer.MEDIA_HEIGHT;
        ToastWidth = Renderer.TOAST_WIDTH;
        ToastHeight = Renderer.TOAST_HEIGHT;
        GlobalDpi = Renderer.GLOBAL_DPI;
        NotchBottomRadius = Renderer.NOTCH_BOTTOM_RADIUS;

        RaiseModifiedFlags();
    }

    private float ResetValue(int index)
    {
        float value = Defaults[index];

        // 重置时若处于硬件监控，拦截至最小限制
        if (Renderer.StandbyDisplayMode == 2)
        {
            if (index == 0 && value < HardwareModeMinWidth) value = HardwareModeMinWidth;
            if (index == 1 && value < HardwareModeMinHeight) value = HardwareModeMinHeight;
        }

        // 重置待机宽度时清掉显示页的快照，防止切回时恢复到旧值
        if (index == 0)
            _display?.ClearStandbySnapshot();

        return value;
    }

    private float Step(int index, int direction)
    {
        float delta = direction > 0
            ? (index == 6 ? 0.05f : 5f)
            : (index == 6 ? -0.05f : -5f);

        if (index == 7)
            return Math.Clamp(ValueOf(index) + delta, 0f, 28f);

        float minLimit = index == 6 ? 0.5f : 20f;

        // 硬件检测模式下的保护墙
        if (Renderer.StandbyDisplayMode == 2)
        {
            if (index == 0) minLimit = HardwareModeMinWidth;
            if (index == 1) minLimit = HardwareModeMinHeight;
        }

        return Math.Max(minLimit, ValueOf(index) + delta);
    }

    private void Apply(int index, float value, string key, Action<float> writeToRenderer)
    {
        writeToRenderer(value);
        Program.SaveSetting(key, value);
        RaiseModifiedFlags();
    }

    private void RaiseModifiedFlags()
    {
        OnPropertyChanged(nameof(IsStandbyModified));
        OnPropertyChanged(nameof(IsMediaModified));
        OnPropertyChanged(nameof(IsToastModified));
        OnPropertyChanged(nameof(IsDpiModified));
    }

    private bool IsModified(int index)
        => Math.Abs(ValueOf(index) - Defaults[index]) > 0.001f;

    private float ValueOf(int index) => index switch
    {
        0 => StandbyWidth,
        1 => BaseHeight,
        2 => MediaWidth,
        3 => MediaHeight,
        4 => ToastWidth,
        5 => ToastHeight,
        6 => GlobalDpi,
        7 => NotchBottomRadius,
        _ => 0f
    };
}
