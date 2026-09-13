namespace NotchPeninsula;

/// <summary>媒体插件共享配置（设置面板读写，媒体插件读取并订阅变更）。</summary>
public static class MediaSettings
{
    public static string TargetPlatform = "other"; // 默认通用媒体
    public static bool IsMediaControlEnabled = true; // 媒体开关
    public static bool IsLyricsEnabled = true;
    public static bool IsKaraokeEnabled = true;
    public static float LyricDelayOffset = 0f;

    /// <summary>配置变更时触发（媒体插件订阅以即时刷新）。</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
