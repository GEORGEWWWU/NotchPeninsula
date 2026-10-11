namespace NotchPeninsula.Plugins;

/// <summary>
/// 外部媒体会话源：<b>插件实现、宿主读取</b>。
///
/// <para>
/// 宿主现有的媒体链路是「系统媒体会话（SMTC）→ MediaController → 岛体渲染」。
/// 这条路对网易云、酷狗这类「自己渲染歌词与进度」的播放器并不可靠：拿到的常常是窗口标题拼出来的
/// 脏数据、时长缺失、进度不动。插件把这个接口实现出来交给宿主，宿主就可以在
/// 「这一路的数据比 SMTC 更准」时改用它。
/// </para>
///
/// <para>
/// <b>接入方式</b>：插件在 <c>Initialize</c> 之后即可被识别 —— 宿主在实例化插件之后
/// <c>if (plugin is IMediaSessionSource src)</c> 就能拿到它（在 <c>PluginManager</c> 里登记、
/// 在注销插件时反登记）。插件侧不需要为本接口做任何额外注册。
/// </para>
///
/// <para>
/// <b>读取约定</b>：
/// <list type="bullet">
///   <item>所有属性都是「当前快照」，随取随用，线程安全；</item>
///   <item><b>不要每帧轮询</b>本接口做昂贵的取数 —— 实现方自己在后台按自己的节奏刷新，
///         宿主每帧只读缓存值；</item>
///   <item><see cref="Changed"/> 只在「歌曲变了 / 播放状态变了 / 有无会话变了」时触发，
///         <b>不</b>会在秒级进度推进时触发（进度请自己按 <see cref="Position"/> 与墙钟推算）；</item>
///   <item><see cref="IsActive"/> 为 false 时，其余字段一律视为无意义，宿主应回落到自己的数据源。</item>
/// </list>
/// </para>
/// </summary>
public interface IMediaSessionSource
{
    /// <summary>稳定标识，用于日志与去重，例如 "zzjjack.mediacdp"。插件卸载后同一个 id 可能重新出现。</summary>
    string SourceId { get; }

    /// <summary>展示名，例如「网易酷狗时间轴获取」。</summary>
    string SourceName { get; }

    /// <summary>多路外部源同时可用时谁优先（大的赢）。只用一路时可以给任意固定值。</summary>
    int Priority { get; }

    /// <summary>当前是否有会话（暂停也算有）。false 时宿主应当忽略本源的其余字段。</summary>
    bool IsActive { get; }

    /// <summary>是否正在播放（<see cref="IsActive"/> 为 false 时无意义）。</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// 歌名。可能为空串。<b>宿主不消费它</b>（元数据一律以 SMTC 为准，双源会让同一会话的歌名来回跳），
    /// 实现方自己用它（界面展示、换歌判定）即可。
    /// </summary>
    string Title { get; }

    /// <summary>歌手。<b>宿主同样不消费</b>，理由见 <see cref="Title"/>。</summary>
    string Artist { get; }

    /// <summary>当前播放位置。宿主可以按墙钟在这个值上继续推算（刷新周期之内不会更新）。</summary>
    TimeSpan Position { get; }

    /// <summary>总时长；未知时为 <see cref="TimeSpan.Zero"/>。<b>宿主只取这个与 <see cref="Position"/></b>。</summary>
    TimeSpan Duration { get; }

    /// <summary>封面图片 URL；没有时为 null。实现方自用，宿主不消费。</summary>
    string? CoverUrl { get; }

    /// <summary>播放 / 暂停。</summary>
    void TogglePlayPause();

    /// <summary>下一首。</summary>
    void Next();

    /// <summary>上一首。</summary>
    void Previous();

    /// <summary>把播放器窗口唤到前台。</summary>
    void OpenApp();

    /// <summary>歌曲身份或播放状态发生变化时触发。可能在任意线程回调，订阅方需自行切线程。</summary>
    event Action? Changed;
}
