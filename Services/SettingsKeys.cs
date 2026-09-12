namespace NotchPeninsula.Services;

/// <summary>
/// HKEY_CURRENT_USER\SOFTWARE\NotchPeninsula 下的键名常量。
/// 与原 Program.LoadSettings / SaveSetting 使用的字符串一一对应，避免各处硬编码拼错。
/// </summary>
public static class SettingsKeys
{
    public const string AutoHide = "AutoHide";
    public const string MediaControl = "MediaControl";
    public const string KaraokeEnabled = "KaraokeEnabled";
    public const string TargetPlatform = "TargetPlatform";
    public const string LyricsEnabled = "LyricsEnabled";
    public const string LyricDelayOffset = "LyricDelayOffset";
    public const string ToastEnabled = "ToastEnabled";
    public const string TopmostEnabled = "TopmostEnabled";

    public const string CustomStandbyW = "Custom_StandbyW";
    public const string CustomBaseH = "Custom_BaseH";
    public const string CustomMediaW = "Custom_MediaW";
    public const string CustomMediaH = "Custom_MediaH";
    public const string CustomToastW = "Custom_ToastW";
    public const string CustomToastH = "Custom_ToastH";
    public const string CustomDpi = "Custom_Dpi";
    public const string CustomNotchBottomR = "Custom_NotchBottomR";

    public const string ThemeMode = "ThemeMode";
    public const string NotchStyle = "NotchStyle";
    public const string MediaInteractionMode = "MediaInteractionMode";
    public const string StandbyDisplayMode = "StandbyDisplayMode";
    public const string TargetMonitorIndex = "TargetMonitorIndex";
    public const string BgOpacityLevel = "BgOpacityLevel";

    public const string CompositeEnabled = "CompositeMode_Enabled";
    public const string CompositeShowDateTime = "Composite_ShowDateTime";
    public const string CompositeShowHardware = "Composite_ShowHardware";
    public const string CompositeShowMedia = "Composite_ShowMedia";

    public const string PassthroughMode = "PassthroughMode";
}
