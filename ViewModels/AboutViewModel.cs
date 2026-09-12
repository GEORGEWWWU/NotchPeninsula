using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace NotchPeninsula.ViewModels;

/// <summary>关于软件：图标、版本与三个外链。</summary>
public partial class AboutViewModel : ViewModelBase
{
    private static readonly string[] Urls =
    [
        "https://github.com/GEORGEWWWU/NotchPeninsula/releases", // 0 检测更新
        "https://github.com/GEORGEWWWU/NotchPeninsula",          // 1 项目仓库
        "https://georgewu.top/"                                  // 2 开发者
    ];

    [ObservableProperty] private string _versionText = "NPS v0.0.0";
    [ObservableProperty] private Bitmap? _appIcon;

    public bool HasAppIcon => AppIcon != null;

    public AboutViewModel()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            VersionText = $"NPS v{version.Major}.{version.Minor}.{version.Build}";
    }

    partial void OnAppIconChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAppIcon));

    [RelayCommand]
    private void OpenLink(string? indexText)
    {
        if (!int.TryParse(indexText, out var index) || index < 0 || index >= Urls.Length) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Urls[index],
                UseShellExecute = true
            });
        }
        catch
        {
            // 极端环境（无浏览器）下静默忽略
        }
    }
}
