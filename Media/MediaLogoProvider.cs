namespace NotchPeninsula
{
    // 平台规则：通过 AppID 关键字确定应用身份。
    public sealed record PlatformRule(
        string Name,     // 平台/应用名（便于日志排查，也是 IsPlatform 的查询键）
        string[] AppIds); // 匹配 SourceAppUserModelId 的关键字（小写）

    public static class MediaLogoProvider
    {
        // 平台规则表：新增平台只需在此追加一条规则
        private static readonly PlatformRule[] PlatformRules =
        [
            new("PotPlayer", ["potplayer", "daum"]),
            new("Bilibili",  ["bilibili"]),
            new("Chrome",    ["chrome"]),
            new("Edge",      ["edge"]),
        ];

        private static readonly string[] BrowserAppIds =
        [
            "chrome", "edge", "firefox", "brave", "opera", "vivaldi",
            "qqbrowser", "360se", "360chrome", "sogou",
        ];

        public static bool IsBrowser(string? sourceAppUserModelId)
        {
            if (string.IsNullOrEmpty(sourceAppUserModelId)) return false;

            foreach (var appId in BrowserAppIds)
            {
                if (sourceAppUserModelId.Contains(appId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsPlatform(string? sourceAppUserModelId, string platformName)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return false;

            var id = sourceAppUserModelId.ToLowerInvariant();
            foreach (var rule in PlatformRules)
            {
                if (rule.Name.Equals(platformName, StringComparison.OrdinalIgnoreCase) && MatchesAppId(rule.AppIds, id))
                    return true;
            }
            return false;
        }

        private static bool MatchesAppId(string[] appIds, string lowerId)
        {
            foreach (var appId in appIds)
            {
                if (lowerId.Contains(appId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
