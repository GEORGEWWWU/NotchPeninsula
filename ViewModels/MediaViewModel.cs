using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NotchPeninsula.Services;

namespace NotchPeninsula.ViewModels;

/// <summary>媒体设置：媒体控制 / 目标媒体平台 / 歌词与延迟补偿。</summary>
public partial class MediaViewModel : ViewModelBase
{
    public ObservableCollection<PlatformOption> Platforms { get; } =
    [
        new PlatformOption("other", "通用媒体"),
        new PlatformOption("netease", "网易云音乐"),
        new PlatformOption("qqmusic", "QQ音乐"),
        new PlatformOption("kugou", "酷狗音乐"),
        new PlatformOption("spotify", "Spotify"),
        new PlatformOption("applemusic", "Apple Music"),
        new PlatformOption("echomusic", "Echo Music"),
        new PlatformOption("lxmusic", "LX Music")
    ];

    [ObservableProperty] private bool _isMediaControlEnabled = MediaController.IsMediaControlEnabled;
    [ObservableProperty] private int _selectedPlatformIndex;
    [ObservableProperty] private bool _isLyricsEnabled = MediaController.IsLyricsEnabled;
    [ObservableProperty] private bool _isKaraokeEnabled = MediaController.IsKaraokeEnabled;
    [ObservableProperty] private float _lyricDelayOffset = MediaController.LyricDelayOffset;

    public MediaViewModel()
    {
        _selectedPlatformIndex = IndexOfPlatform(MediaController.TargetPlatform);
    }

    partial void OnIsMediaControlEnabledChanged(bool value)
    {
        MediaController.IsMediaControlEnabled = value;
        Program.SaveSetting(SettingsKeys.MediaControl, value ? 1 : 0);
        _ = MediaController.Instance?.ForceRefresh();
    }

    partial void OnSelectedPlatformIndexChanged(int value)
    {
        if (value < 0 || value >= Platforms.Count) return;

        MediaController.TargetPlatform = Platforms[value].Id;
        Program.SaveSetting(SettingsKeys.TargetPlatform, MediaController.TargetPlatform);
        _ = MediaController.Instance?.ForceRefresh();
    }

    partial void OnIsLyricsEnabledChanged(bool value)
    {
        MediaController.IsLyricsEnabled = value;
        Program.SaveSetting(SettingsKeys.LyricsEnabled, value ? 1 : 0);
    }

    partial void OnIsKaraokeEnabledChanged(bool value)
    {
        MediaController.IsKaraokeEnabled = value;
        Program.SaveSetting(SettingsKeys.KaraokeEnabled, value ? 1 : 0);
    }

    /// <summary>延迟补偿步进：参数格式 "minus" / "plus" / "reset"。</summary>
    [RelayCommand]
    private void AdjustDelay(string? action)
    {
        float next = action switch
        {
            "reset" => 0f,
            "minus" => LyricDelayOffset - 0.1f,
            "plus" => LyricDelayOffset + 0.1f,
            _ => LyricDelayOffset
        };

        // 固定一位小数，避免浮点精度爆炸
        next = (float)Math.Round(next, 1);

        LyricDelayOffset = next;
        MediaController.LyricDelayOffset = next;
        Program.SaveSetting(SettingsKeys.LyricDelayOffset, next);
    }

    private int IndexOfPlatform(string id)
    {
        for (int i = 0; i < Platforms.Count; i++)
        {
            if (Platforms[i].Id == id) return i;
        }
        return 0;
    }
}

public record PlatformOption(string Id, string Name)
{
    public override string ToString() => Name;
}
