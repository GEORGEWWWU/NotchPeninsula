using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

// 与 System.Drawing.Bitmap 消歧（NotchWindow.cs 托盘图标用到了 System.Drawing）
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace NotchPeninsula.ViewModels;

/// <summary>设置窗口主 ViewModel：标题栏 + 侧边栏导航 + 六个页面。</summary>
public partial class SettingsViewModel : ObservableObject
{
    // 侧边栏顺序对应的页面索引（与原 Skia 版 _selectedTab 语义一致）
    private const int TabPersonalization = 5;
    private const int TabGeneral = 0;
    private const int TabDisplay = 1;
    private const int TabMedia = 2;
    private const int TabInteraction = 3;
    private const int TabAbout = 4;

    public GeneralViewModel General { get; }
    public DisplayViewModel Display { get; }
    public MediaViewModel Media { get; }
    public InteractionViewModel Interaction { get; }
    public PersonalizationViewModel Personalization { get; }
    public AboutViewModel About { get; }

    [ObservableProperty] private int _selectedTabIndex = TabPersonalization;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private Bitmap? _appIcon;

    public string TitleWithVersion { get; }
    public bool HasAppIcon => AppIcon != null;

    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;

    public SettingsViewModel()
    {
        General = new GeneralViewModel();
        Display = new DisplayViewModel();
        Media = new MediaViewModel();
        Interaction = new InteractionViewModel();
        Personalization = new PersonalizationViewModel();
        About = new AboutViewModel();

        // 跨页面联动（与原生版一致）：组合模式会禁用交互页的媒体交互方式，
        // 穿透模式会禁用个性化页的背景透明度。
        Interaction.BindTo(Display);
        Personalization.BindTo(Interaction, Display);

        TitleWithVersion = BuildTitle();
        CurrentPage = Personalization;
    }

    partial void OnSelectedTabIndexChanged(int value) => CurrentPage = PageOf(value);

    partial void OnAppIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAppIcon));

    private ViewModelBase PageOf(int index) => index switch
    {
        TabGeneral => General,
        TabDisplay => Display,
        TabMedia => Media,
        TabInteraction => Interaction,
        TabAbout => About,
        _ => Personalization
    };

    [RelayCommand]
    private void SelectTab(string? indexText)
    {
        if (int.TryParse(indexText, out var index))
            SelectedTabIndex = index;
    }

    [RelayCommand]
    private void Minimize() => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>窗口图标加载完成后分发到标题栏与关于页。</summary>
    public void SetAppIcon(Bitmap? bitmap)
    {
        AppIcon = bitmap;
        About.AppIcon = bitmap;
    }

    /// <summary>从 Avalonia 的 Screens 刷新显示器下拉项。</summary>
    public void RefreshMonitors(Screens screens)
    {
        var names = new List<string>();
        for (int i = 0; i < screens.ScreenCount; i++)
            names.Add(screens.Primary == screens.All[i] ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}");

        Display.SetMonitors(names);
    }

    private static string BuildTitle()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null
            ? "NotchPeninsula"
            : $"NotchPeninsula {version.Major}.{version.Minor}.{version.Build}";
    }
}
