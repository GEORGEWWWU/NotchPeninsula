using System;
using System.Collections.Generic;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    /// <summary>
    /// MediaController 的「外部媒体源」半边（partial）：登记表、接管判定、位置墙钟推算。
    ///
    /// <para>
    /// <b>背景</b>：宿主的数据来自系统媒体会话（SMTC）。网易云音乐与酷狗音乐对 SMTC 并不友好 ——
    /// 歌名常是窗口标题拼出来的脏串、时长缺失、进度不动。这两家本身都基于 CEF，页面里有权威数据，
    /// 于是插件用 CDP 把它们读出来，实现 <see cref="IMediaSessionSource"/> 交给宿主
    /// （接口见 <c>Plugin/MediaSessionSource.cs</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>接管范围：只有时间轴</b>。宿主只取插件报的「时长 + 播放位置」，歌名、歌手、封面、
    /// 歌词匹配等元数据全部维持宿主原有逻辑（SMTC / 宿主自己的取词链）。原因是元数据一旦双源，
    /// 同一个会话的身份就会在「SMTC 脏串」与「插件净串」之间反复横跳 —— 接管前提依赖
    /// SMTC 的时长字段，而那个字段时有时无，每跳一次都会作废封面并重排歌词槽，切歌时尤其明显。
    /// </para>
    ///
    /// <para>
    /// <b>接管规则（有意保守）</b>：只有当<b>当前会话就是这两家</b>、<b>且 SMTC 给出不了时间轴</b>时，
    /// 才接受插件传入的时间轴；其余情况一律照旧走 SMTC（没装插件时登记表为空，永远返回 false，
    /// 内置的虚拟跑表行为逐字节不变）。
    /// </para>
    /// </summary>
    public partial class MediaController
    {
        private static readonly List<IMediaSessionSource> _externalSources = new();

        /// <summary>
        /// 插件登记外部媒体源（由 <c>PluginManager</c> 在 <c>plugin.Initialize</c> 之后调用）。
        /// 同 SourceId 视为热重载，新实例直接顶掉旧的。
        /// </summary>
        public static void RegisterExternalSource(IMediaSessionSource source)
        {
            if (source == null) return;

            lock (_externalSources)
            {
                for (int i = 0; i < _externalSources.Count; i++)
                {
                    if (!string.Equals(_externalSources[i].SourceId, source.SourceId, StringComparison.Ordinal)) continue;
                    _externalSources[i] = source;
                    return;
                }
                _externalSources.Add(source);
            }

            Logger.Info($"[外部媒体源] 已登记 {source.SourceId}（{source.SourceName}）");
        }

        /// <summary>
        /// 插件卸载时反登记。按<b>引用</b>摘除：热重载会同时存在新旧两个实例，
        /// 只按 SourceId 摘会把刚登记的新实例一起带走。
        /// </summary>
        public static void UnregisterExternalSource(IMediaSessionSource source)
        {
            if (source == null) return;

            lock (_externalSources)
            {
                for (int i = 0; i < _externalSources.Count; i++)
                {
                    if (!ReferenceEquals(_externalSources[i], source)) continue;
                    _externalSources.RemoveAt(i);
                    Logger.Info($"[外部媒体源] 已反登记 {source.SourceId}");
                    return;
                }
            }
        }

        /// <summary>
        /// 挑一个可以接管的外部源；不该接管时返回 null，调用方照旧用 SMTC。
        ///
        /// <para>
        /// 两个前提：① SMTC 没给出时间轴；② 当前会话是<b>某个已登记插件上报的播放器</b>。
        /// 白名单在插件侧（<see cref="IMediaSessionSource.AppIdKeywords"/>），宿主不再写死播放器名字 ——
        /// 新播放器只要写个插件，宿主不用跟着改。
        /// </para>
        ///
        /// <para>
        /// 多路同时可用时按 <see cref="IMediaSessionSource.Priority"/> 取最高；
        /// 每个源各自判断「当前这个 appId 归不归我管」。
        /// </para>
        /// </summary>
        private static IMediaSessionSource? PickExternalSource(string? appId, TimeSpan smtcDuration)
        {
            // ① SMTC 有时长 → 它说得清，就不夺权
            if (smtcDuration > TimeSpan.Zero) return null;
            if (string.IsNullOrEmpty(appId)) return null;

            IMediaSessionSource? best = null;
            lock (_externalSources)
            {
                for (int i = 0; i < _externalSources.Count; i++)
                {
                    var s = _externalSources[i];
                    if (!s.IsActive) continue;
                    if (s.Duration <= TimeSpan.Zero) continue;   // 连时长都没有，接管也画不出时间轴
                    if (!SourceDrivesApp(s, appId)) continue;     // 当前放的软件不归这个源管
                    if (best == null || s.Priority > best.Priority) best = s;
                }
            }
            return best;
        }

        /// <summary>
        /// 这个外部源是否负责当前会话的软件：只认插件上报的
        /// <see cref="IMediaSessionSource.AppIdKeywords"/>。没上报（空数组）的源不参与接管 ——
        /// 宿主不再按播放器名字猜，白名单的唯一来源是插件。
        /// </summary>
        private static bool SourceDrivesApp(IMediaSessionSource source, string appId)
        {
            IReadOnlyList<string> keywords;
            try { keywords = source.AppIdKeywords; }
            catch { keywords = Array.Empty<string>(); }   // 旧插件没实现时按「没上报」处理，不接管

            if (keywords is not { Count: > 0 }) return false;

            for (int i = 0; i < keywords.Count; i++)
            {
                string keyword = keywords[i];
                if (!string.IsNullOrEmpty(keyword)
                    && appId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 旧插件的推荐提示文案用：网易云（cloudmusic.exe / netease cloud music）与酷狗（KuGou.exe）。
        /// 只服务于 <c>NotchWindow.TickTimelinePluginTip()</c>；<b>不再</b>参与外部源接管判定
        /// （接管白名单由插件通过 <see cref="IMediaSessionSource.AppIdKeywords"/> 上报）。
        /// </summary>
        internal static bool IsNeteaseOrKugou(string? appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;

            return appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
                || appId.Contains("netease", StringComparison.OrdinalIgnoreCase)
                || appId.Contains("kugou", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 哔哩哔哩 PC 客户端判定（实测 SMTC appId 为 <c>com.bilibili.bilibiliPC</c>）。
        /// <b>只给插件推荐提示用</b>：外部源接管已改成读插件上报的
        /// <see cref="IMediaSessionSource.AppIdKeywords"/>，这里不参与接管判定。
        /// 浏览器里的网页版 appId 是 Edge / Chrome，不会命中。
        /// </summary>
        internal static bool IsBilibiliAppId(string? appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            return appId.Contains("bilibili", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>给用户看的播放器名；不是网易云 / 酷狗时返回空串。推荐提示的文案用它。</summary>
        internal static string NeteaseKugouDisplayName(string? appId)
        {
            if (string.IsNullOrEmpty(appId) || !IsNeteaseOrKugou(appId)) return "";
            return appId.Contains("kugou", StringComparison.OrdinalIgnoreCase) ? "酷狗音乐" : "网易云音乐";
        }

        // ---- 位置：快照 + 墙钟推算 ----
        //
        // 插件按 500ms 的节奏刷新位置，宿主若直接照搬，进度条上会看见 500ms 的台阶。
        // 做法与宿主自己对 SMTC 的 TryGetSmtcLivePosition 一致：只在「快照真的变了」时重锚，
        // 帧间用墙钟补足。
        private string _extAnchorKey = "";
        private TimeSpan _extAnchorPos;
        private DateTime _extAnchorAt;

        /// <summary>
        /// 本帧的时间轴是否由外部源驱动（<c>UpdateLyrics</c> 每帧刷新）。
        /// 下游的 AdvanceTimeline / AdvanceFreeTimeline 靠它短路掉 SMTC 外推 ——
        /// 那一步读的是 <c>_smtcPos</c>，而网易云/酷狗在 SMTC 里给的进度就是 0，
        /// 不短路的话「歌名对了、位置却钉在原点」。
        /// </summary>
        private volatile bool _externalDrive;

        private TimeSpan ExternalLivePosition(IMediaSessionSource source, DateTime now)
        {
            string key = source.SourceId + "|" + source.Title;
            TimeSpan snapshot = source.Position;

            // 换歌或插件给了新快照 → 重锚（顺带把暂停/拖动的跳变一次性吃掉）
            if (!string.Equals(key, _extAnchorKey, StringComparison.Ordinal) || snapshot != _extAnchorPos)
            {
                _extAnchorKey = key;
                _extAnchorPos = snapshot;
                _extAnchorAt = now;
                return snapshot;
            }

            if (!source.IsPlaying) return _extAnchorPos;   // 暂停：位置冻结在快照那一刻

            var live = _extAnchorPos + (now - _extAnchorAt);
            var duration = source.Duration;
            if (duration > TimeSpan.Zero && live > duration) live = duration;
            if (live < TimeSpan.Zero) live = TimeSpan.Zero;
            return live;
        }

        /// <summary>
        /// 外部源接管时的进度：拿到返回 true，调用方用 <paramref name="position"/> /
        /// <paramref name="duration"/>；返回 false 表示没接管，调用方照旧用 SMTC 的值。
        /// </summary>
        private bool TryGetExternalTimeline(string? appId, TimeSpan smtcDuration, DateTime now,
            out TimeSpan position, out TimeSpan duration)
        {
            position = TimeSpan.Zero;
            duration = TimeSpan.Zero;

            var source = PickExternalSource(appId, smtcDuration);
            if (source == null) return false;

            duration = source.Duration;
            position = ExternalLivePosition(source, now);
            return true;
        }
    }
}
