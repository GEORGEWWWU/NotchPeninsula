using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace NotchPeninsula
{
    // 封面策略：Always = 始终使用站标；Fallback = 仅当无 SMTC 封面时用站标兜底
    public enum CoverStrategy
    {
        Always,
        Fallback,
    }

    // 平台封面规则：通过 AppID 关键字确定应用，再决定封面来源
    public sealed record PlatformCoverRule(
        string Name,     // 平台/应用名（便于日志排查）
        string[] AppIds, // 匹配 SourceAppUserModelId 的关键字（小写）
        string LogoPath, // 站标封面路径（相对程序根目录）
        CoverStrategy Strategy);

    // 统一封面管理器：根据媒体源 AppUserModelId 匹配平台，返回对应站标封面
    public static class MediaLogoProvider
    {
        // 平台规则表：新增平台只需在此追加一条规则
        private static readonly PlatformCoverRule[] PlatformRules =
        {
            new("PotPlayer", new[] { "potplayer", "daum" }, "data\\image\\potplayer-logo.jpg", CoverStrategy.Always),
            new("Bilibili",  new[] { "bilibili" },          "data\\image\\bilibili-logo.png", CoverStrategy.Always),
            new("Chrome",    new[] { "chrome" },            "data\\image\\chrome-logo.png",   CoverStrategy.Fallback),
            new("Edge",      new[] { "edge" },              "data\\image\\edge-logo.png",     CoverStrategy.Fallback),
        };

        // 路径 -> 已解码位图缓存，全进程共享，避免重复 IO + 解码
        private static readonly Dictionary<string, SKBitmap> _cache = new(StringComparer.OrdinalIgnoreCase);

        // 根据会话 AppUserModelId 返回对应平台站标封面副本；未命中返回 null
        // hasThumbnail 表示会话是否提供了 SMTC 封面，Fallback 平台仅在其为空时才使用站标
        public static SKBitmap? GetLogo(string? sourceAppUserModelId, bool hasThumbnail)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return null;

            var id = sourceAppUserModelId.ToLowerInvariant();
            foreach (var rule in PlatformRules)
            {
                if (!MatchesAppId(rule.AppIds, id)) continue;

                // 一旦 AppID 命中即确定应用：Always 始终用站标；Fallback 仅无封面时兜底
                if (rule.Strategy == CoverStrategy.Always || !hasThumbnail)
                    return LoadAndCache(rule.LogoPath);
                return null;
            }
            return null;
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

        // 读取并缓存平台站标封面，返回副本避免被调用方 Dispose 时误伤缓存
        private static SKBitmap? LoadAndCache(string relativePath)
        {
            try
            {
                if (!_cache.TryGetValue(relativePath, out var bmp))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, relativePath);
                    using var stream = File.OpenRead(path);
                    bmp = SKBitmap.Decode(stream);
                    _cache[relativePath] = bmp;
                }
                return bmp?.Copy();
            }
            catch (Exception ex)
            {
                Logger.Error($"加载平台封面 {relativePath} 失败", ex);
                return null;
            }
        }
    }
}
