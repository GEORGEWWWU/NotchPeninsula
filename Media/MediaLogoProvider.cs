namespace NotchPeninsula
{
    // 平台规则：通过 AppID 关键字确定应用身份。
    //
    // 这里只负责「这个会话属于哪个平台」的判定（歌词拦截、文本显示策略），
    //    不再携带任何封面路径 —— 封面从 2026-10-01 起改为按会话动态取：
    //      视频模式 → 该程序自己的应用图标（AppIconProvider）
    //      音乐模式 → 网络封面 → SMTC 自带缩略图 → 应用图标
    //    data\image 下的平台站标资源仍然保留在工程里，只是不再被引用。
    public sealed record PlatformRule(
        string Name,     // 平台/应用名（便于日志排查，也是 IsPlatform 的查询键）
        string[] AppIds); // 匹配 SourceAppUserModelId 的关键字（小写）

    /// <summary>平台身份判定：把 SMTC 会话的 AUMID 归类到已知平台 / 浏览器。</summary>
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

        // 浏览器 SMTC 会话的 AppID 关键字（Chromium 系 + Firefox 系），新增浏览器只需在此追加。
        // 「浏览器媒体」模式靠它过滤会话，浏览器标题清理策略也复用同一份判定，避免两处规则漂移。
        private static readonly string[] BrowserAppIds =
        [
            "chrome", "edge", "firefox", "brave", "opera", "vivaldi",
            "qqbrowser", "360se", "360chrome", "sogou",
        ];

        // 判断会话是否来自浏览器。刻意用 OrdinalIgnoreCase 直接比较，
        // 不做 ToLowerInvariant 拷贝 —— 该方法在会话扫描的循环体内调用，必须零分配。
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

        // 判断 AppUserModelId 是否命中指定平台（平台名为 PlatformRules 中的 Name，单一数据源）
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

        // 判断 AppUserModelId 是否命中该平台的关键字
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
