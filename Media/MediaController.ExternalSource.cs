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
    /// <b>接管规则（有意保守）</b>：只有当<b>当前会话就是这两家</b>、<b>且 SMTC 给出不了时间轴</b>时，
    /// 才接受插件传入的数据；其余情况一律照旧走 SMTC。这样即使插件读错了，也不会把本来正常的
    /// 会话带坏 —— 插件的价值本来就只体现在「SMTC 说不清」的那一小片场景里。
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
        /// 两个前提缺一不可：① SMTC 没给出时间轴；② 当前会话是网易云或酷狗。
        /// 多路同时可用时按 <see cref="IMediaSessionSource.Priority"/> 取最高。
        /// </summary>
        private static IMediaSessionSource? PickExternalSource(string? appId, TimeSpan smtcDuration)
        {
            // ① SMTC 有时长 → 它说得清，就不夺权
            if (smtcDuration > TimeSpan.Zero) return null;

            // ② 当前会话不是这两家 → 不接管
            if (!IsNeteaseOrKugou(appId)) return null;

            IMediaSessionSource? best = null;
            lock (_externalSources)
            {
                for (int i = 0; i < _externalSources.Count; i++)
                {
                    var s = _externalSources[i];
                    if (!s.IsActive) continue;
                    if (s.Duration <= TimeSpan.Zero) continue;   // 连时长都没有，接管也画不出时间轴
                    if (best == null || s.Priority > best.Priority) best = s;
                }
            }
            return best;
        }

        /// <summary>网易云（cloudmusic.exe / netease cloud music）与酷狗（KuGou.exe）。</summary>
        private static bool IsNeteaseOrKugou(string? appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;

            return appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
                || appId.Contains("netease", StringComparison.OrdinalIgnoreCase)
                || appId.Contains("kugou", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// 外部源接管时的歌名 / 歌手，供 <c>UpdateMediaMode</c> 使用。
        /// 走宿主原有的 Title / Artist 链路，歌词、封面、最近播放自然跟着一起变准。
        /// </summary>
        private bool TryGetExternalTrack(string? appId, TimeSpan smtcDuration,
            out string title, out string artist)
        {
            title = "";
            artist = "";

            var source = PickExternalSource(appId, smtcDuration);
            if (source == null) return false;

            // 歌名还没读出来：宁可用 SMTC 的脏串，也不用空值把界面清空
            if (source.Title.Length == 0) return false;

            title = source.Title;
            artist = source.Artist;
            return true;
        }
    }
}
