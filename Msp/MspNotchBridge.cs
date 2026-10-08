// MSP（Model Sensing Protocol）原生接入 —— 把灵动岛暴露成本机节点 notchpeninsula。
// 三向都通：被调用（ping / notify / media_status / media_control）、广播本机事件（4 条信号）、
// 订阅 notify.** 把对端通知显示在岛上。默认关闭（设置 → 通用设置开关）。
using System.Text.Json;
using Msp.Core;
using NotchPeninsula.Plugins;

namespace NotchPeninsula;

public static class MspNotchBridge
{
    public const string NodeId = "notchpeninsula";

    public static bool IsEnabled;

    // 为什么不用内置 LocalTool / LocalAi：
    //   LocalTool 只宣告不发现 → 看不见别人 → 收不到广播；
    //   LocalAi 多宣告一个 msp/multi-round，而本节点只会同步回一个结果，宣告不实现的模式等于撒谎。
    // 为什么不用 lazy 躲开连接去重：两侧 eager 时协议层会去重，且「连接出现」早于「连接稳定」，
    //   对端首调可能撞上被关掉的那条 → 报「连接断开」。对策在对端（等稳定 + 首调重试），
    //   改成 lazy 会让本节点退化成「只接受拨入」，不符合「本机软件」这个角色。
    private static readonly Preset LocalSoftware = new(
        Name: "LOCAL_SOFTWARE",
        Discovery: new HashSet<string> { "directory" },   // 扫描共享目录，看得见别人
        Presence: new HashSet<string> { "directory" },   // 把自己写进共享目录
        Transports: new HashSet<string> { "tcp" },
        Patterns: new HashSet<string> { "msp/request-reply", "msp/publish-subscribe" },
        PrimaryTransport: "tcp",
        Capabilities: new HashSet<string> { "pub", "sub", "req", "rep" },   // 四项齐全
        ConnectPolicy: "eager");   // 发现即连，不然收不到广播

    public const string SignalToastRaised = "notify.toast.raised";
    public const string SignalReminderRaised = "notify.notchpeninsula.reminder.raised";
    public const string SignalTrackChanged = "media.track.changed";
    public const string SignalPlaybackChanged = "media.playback.changed";

    private const string NotifySubscription = "notify.**";

    private const string AnnotationsKey = "annotations";
    private const string RoleKey = "role";
    private const string IconRole = "icon";

    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(6);

    private static MspNode? _node;
    private static DateTime _startedAt;

    private static Action<ToastNotice>? _onSystemNotification;
    private static Action<ToastData>? _onReminderPosted;
    private static Action<MediaSnapshot>? _onMediaChanged;

    private static readonly object _stateGate = new();
    private static long _lastNoticeId = -1;
    private static string _lastTrackKey = "";
    private static bool _lastPlaying;
    private static bool _lastActive;
    private static bool _mediaBaselineSet;

    // 「这条提醒别广播出去」。线程静态：只在发起 PostReminder 的那次同步 Invoke 里有效。
    [ThreadStatic] private static bool _suppressBroadcast;

    public static MspNode? Node => _node;

    public static void Start(string nodesDir = "~/.msp/nodes")
    {
        if (_node != null) return;
        _startedAt = DateTime.UtcNow;

        var node = new MspNode(NodeId, LocalSoftware, nodesDir);
        try
        {
            RegisterTools(node);
            DeclareSignals(node);

            node.On(NotifySubscription, OnPeerNotify);

            AttachHostEvents();
            node.Start();

            _node = node;
            Logger.Info($"[MSP] 原生接入已启动：节点 {NodeId}（预设 {LocalSoftware.Name}，"
                        + $"发现+宣告 + 两模式 + pub/sub/req/rep，{node.Endpoint?.ToString() ?? "未宣告"}）");
        }
        catch
        {
            DetachHostEvents();
            try { node.Stop(); } catch { /* 清理失败无所谓，反正要往上抛 */ }
            throw;
        }
    }

    public static void Stop()
    {
        var node = _node;
        _node = null;

        DetachHostEvents();

        try { node?.Stop(); }
        catch (Exception ex) { Logger.Warn($"[MSP] 节点停止失败: {ex.Message}"); }

        if (node != null) Logger.Info("[MSP] 原生接入已停止");

        lock (_stateGate)
        {
            _lastNoticeId = -1;
            _lastTrackKey = "";
            _lastPlaying = false;
            _lastActive = false;
            _mediaBaselineSet = false;
        }
    }

    public static void Enable(bool on)
    {
        if (on == IsEnabled) return;

        if (on)
        {
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Logger.Error("[MSP] 打开失败，开关已拨回关闭", ex);
                IsEnabled = false;
                return;
            }
        }
        else
        {
            Stop();
        }

        IsEnabled = on;
        Logger.Info($"[MSP] 用户{(on ? "开启" : "关闭")}了 MSP 接入");
    }

    private static void RegisterTools(MspNode node)
    {
        node.Tool("ping", () => Ping(node), "健康检查：返回节点标识、预设、运行时长与已连接对端");

        node.Tool("notify", (Func<string, string, string, List<JsonElement>, object>)Notify,
            "在灵动岛顶部弹出一条通知。点对点，不广播到 notify 域。"
            + "title/body 是文本；content 可选，是 MSP/MCP 的 content block 数组，"
            + "其中 annotations.role == \"icon\" 的 image / resource_link 块会被当作图标（不传就用默认图标）");

        node.Tool("media_status", () => MediaStatus(), "只读当前媒体播放状态（活动/播放/标题/艺术家/进度）");
        node.Tool("media_control", (string action) => MediaControl(action),
            "控制媒体：toggle / play / pause / next / prev");
    }

    private static void DeclareSignals(MspNode node)
    {
        node.DeclareSignal(SignalToastRaised, description: "系统通知（Windows Toast）到达");
        node.DeclareSignal(SignalReminderRaised, description: "宿主内部提醒（插件 PostReminder）");
        node.DeclareSignal(SignalTrackChanged, description: "媒体曲目变化（标题 / 艺术家 / 应用）");
        node.DeclareSignal(SignalPlaybackChanged, description: "媒体播放状态变化（播放 / 暂停 / 会话有无）");
    }

    private static void AttachHostEvents()
    {
        _onSystemNotification = OnSystemNotification;
        PluginDataBridge.SystemNotification += _onSystemNotification;

        _onReminderPosted = OnReminderPosted;
        PluginManager.Instance.Host.ReminderPosted += _onReminderPosted;

        _onMediaChanged = OnMediaChanged;
        PluginDataBridge.MediaChanged += _onMediaChanged;
    }

    private static void DetachHostEvents()
    {
        if (_onSystemNotification != null)
        {
            try { PluginDataBridge.SystemNotification -= _onSystemNotification; } catch { }
            _onSystemNotification = null;
        }

        if (_onReminderPosted != null)
        {
            try { PluginManager.Instance.Host.ReminderPosted -= _onReminderPosted; } catch { }
            _onReminderPosted = null;
        }

        if (_onMediaChanged != null)
        {
            try { PluginDataBridge.MediaChanged -= _onMediaChanged; } catch { }
            _onMediaChanged = null;
        }
    }

    private static void OnSystemNotification(ToastNotice notice)
    {
        // 同一条通知会发两帧（先兜底图标、后真实图标），NoticeId 相同、Version 递增，只广播首帧。
        if (notice.Version != 1) return;

        lock (_stateGate)
        {
            if (notice.NoticeId == _lastNoticeId) return;
            _lastNoticeId = notice.NoticeId;
        }

        var icon = ImageBlock(notice.IconPng, IconRole);

        Publish(SignalToastRaised, new
        {
            kind = notice.AppName,
            title = notice.Title,
            body = notice.Body,
            content = icon == null ? null : new[] { icon.Value },
            aumid = notice.Aumid,
            receivedAt = notice.ReceivedUtc.ToString("o"),
        });
    }

    private static void OnReminderPosted(ToastData toast)
    {
        // 从对端收到的通知也走 PostReminder 显示，绝不能再广播回去 —— 否则两个都接了
        // 通知的节点会互相无限转发（回声）。见 ShowOnIsland / PostReminderSafely。
        if (_suppressBroadcast) return;

        Publish(SignalReminderRaised, new
        {
            kind = toast.AppName,
            title = toast.Title,
            body = toast.Body,
        });
    }

    private static void OnMediaChanged(MediaSnapshot m)
    {
        // 这个回调跑在宿主 250ms 采样线程上，签名里含歌词与进度 —— 只认「换曲」和「播放态」
        // 两种事件级跃迁；进度一滴一滴发会把广播刷屏（要当前状态请调 media_status 工具）。
        string trackKey = string.Concat(m.Title, "\u0001", m.Artist, "\u0001", m.AppId);

        bool trackChanged;
        bool playbackChanged;
        bool isBaseline;

        lock (_stateGate)
        {
            isBaseline = !_mediaBaselineSet;
            trackChanged = !isBaseline && trackKey != _lastTrackKey;
            playbackChanged = !isBaseline && (m.IsPlaying != _lastPlaying || m.IsActive != _lastActive);

            _lastTrackKey = trackKey;
            _lastPlaying = m.IsPlaying;
            _lastActive = m.IsActive;
            _mediaBaselineSet = true;
        }

        if (isBaseline) return;

        if (trackChanged)
        {
            Publish(SignalTrackChanged, new
            {
                title = m.Title,
                artist = m.Artist,
                app = m.AppId,
                playing = m.IsPlaying,
            });
        }

        if (playbackChanged)
        {
            Publish(SignalPlaybackChanged, new
            {
                active = m.IsActive,
                playing = m.IsPlaying,
                title = m.Title,
                artist = m.Artist,
            });
        }
    }

    private static void OnPeerNotify(Signal sig)
    {
        if (string.Equals(sig.Source, NodeId, StringComparison.Ordinal)) return;

        var n = ParseNotify(sig.Payload);
        if (string.IsNullOrWhiteSpace(n.Title) && string.IsNullOrWhiteSpace(n.Body)) return;

        string peer = string.IsNullOrWhiteSpace(sig.Source) ? "MSP" : sig.Source!.Trim();
        string source = string.IsNullOrWhiteSpace(n.Kind) ? peer : n.Kind;

        string title = n.Title.Length > 0 ? n.Title : n.Body;

        ShowOnIsland(title, n.Body, n.IconSpec, source, n.Duration);

        Logger.Info($"[MSP] 对端通知已受理：{sig.Path} ← {peer}（{title}）"
                    + $"，来源: {source}"
                    + $"，图标: {ToastIconProvider.Describe(n.IconSpec)}"
                    + (n.ExtraBlocks > 0 ? $"，另有 {n.ExtraBlocks} 个块未渲染" : ""));
    }

    private readonly record struct NotifyPayload(
        string Kind, string Title, string Body, string? IconSpec, TimeSpan Duration, int ExtraBlocks);

    private static NotifyPayload ParseNotify(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new NotifyPayload("", "", "", null, DefaultDuration, 0);

        string kind = ReadString(payload, "kind", "Kind", "source", "app", "AppName");
        string title = ReadString(payload, "title", "Title", "name");
        string body = ReadString(payload, "body", "Body", "text", "message");
        var duration = ReadDuration(payload);

        string? icon = null;
        string textFallback = "";
        int extra = 0;

        if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            (icon, textFallback, extra) = ParseBlocks(content.EnumerateArray());

        if (string.IsNullOrWhiteSpace(body)) body = textFallback;

        return new NotifyPayload(kind, title, body, icon, duration, extra);
    }

    // 图标两级判定：优先第一个 annotations.role=="icon" 的 image / resource_link 块，
    // 都没标就取第一个图像块 —— 一条通知载荷里的图，唯一合理读法就是图标。
    // ⚠️ 角色标记必须从**原始块**读：Msp.Core.ResourceLink 没有 Annotations 成员，
    //    ContentBlock.From → ToElement 这一趟往返会把 resource_link 的 role=icon 悄悄丢掉。
    // 认不出的 type（audio / resource / blob_ref / 未知）只计数跳过，绝不让整条通知失败。
    private static (string? IconSpec, string TextFallback, int ExtraBlocks) ParseBlocks(
        IEnumerable<JsonElement> rawBlocks)
    {
        string? markedIcon = null;   // 显式标了 role=icon 的第一个
        string? firstImageish = null;   // 没标记，但长得像图标的第一个
        int imageishCount = 0;

        string textFallback = "";
        int extra = 0;

        foreach (var raw in rawBlocks)
        {
            if (raw.ValueKind != JsonValueKind.Object) { extra++; continue; }

            bool marked = IsIconBlock(raw);
            JsonElement el = NormalizeBlock(raw);
            string type = ReadString(el, "type");

            string? spec = type switch
            {
                "image" => ImageSpecOf(el),
                "resource_link" => ReadString(el, "uri"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(spec))
            {
                imageishCount++;
                if (marked && markedIcon == null) markedIcon = spec;
                firstImageish ??= spec;
                continue;
            }

            if (type == "text" && string.IsNullOrWhiteSpace(textFallback))
            {
                textFallback = ReadString(el, "text");
                continue;
            }

            extra++;
        }

        string? icon = markedIcon ?? firstImageish;

        if (icon != null && imageishCount > 1) extra += imageishCount - 1;

        return (icon, textFallback, extra);
    }

    private static JsonElement NormalizeBlock(JsonElement raw)
    {
        try
        {
            return ContentBlock.From(raw) is IContentBlock cb ? cb.ToElement() : raw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[MSP] 内容块解析失败，按原始 JSON 读: {ex.GetType().Name}: {ex.Message}");
            return raw;
        }
    }

    private static bool IsIconBlock(JsonElement block)
    {
        if (!block.TryGetProperty(AnnotationsKey, out var ann) || ann.ValueKind != JsonValueKind.Object)
            return false;

        return string.Equals(ReadString(ann, RoleKey), IconRole, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ImageSpecOf(JsonElement block)
    {
        string data = ReadString(block, "data");
        if (string.IsNullOrWhiteSpace(data)) return null;

        string mime = ReadString(block, "mimeType");
        if (string.IsNullOrWhiteSpace(mime)) mime = "image/png";

        return $"data:{mime};base64,{data}";
    }

    private static JsonElement? ImageBlock(byte[]? png, string role)
    {
        if (png == null || png.Length == 0) return null;

        try
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "image",
                data = Convert.ToBase64String(png),
                mimeType = "image/png",
                annotations = new Dictionary<string, string> { [RoleKey] = role },
            });
        }
        catch (Exception ex)
        {
            Logger.Debug($"[MSP] 图标块构造失败: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static TimeSpan ReadDuration(JsonElement payload)
    {
        if (!payload.TryGetProperty("duration", out var d)) return DefaultDuration;

        double seconds;
        if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var num)) seconds = num;
        else if (d.ValueKind == JsonValueKind.String && double.TryParse(d.GetString(), out var parsed)) seconds = parsed;
        else return DefaultDuration;

        if (double.IsNaN(seconds) || seconds <= 0) return DefaultDuration;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    private static string ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        }
        return "";
    }

    private static object Ping(MspNode node) => new
    {
        node = node.NodeId,
        preset = node.Preset?.Name,
        patterns = node.Preset?.Patterns?.ToList(),
        capabilities = node.Preset?.Capabilities?.ToList(),
        uptime_sec = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
        peers = node.Connections(),
    };

    private static object Notify(string kind, string title, string body, List<JsonElement>? content = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)
            && (content == null || content.Count == 0))
        {
            return new { ok = false, error = "title / body / content 至少要给一个" };
        }

        string? icon = null;
        string textFallback = "";
        int extra = 0;

        if (content is { Count: > 0 })
            (icon, textFallback, extra) = ParseBlocks(content);

        var kindLabel = string.IsNullOrWhiteSpace(kind) ? "MSP" : kind.Trim();
        string effectiveBody = string.IsNullOrWhiteSpace(body) ? textFallback : body;
        string effectiveTitle = string.IsNullOrWhiteSpace(title) ? effectiveBody : title;

        ShowOnIsland(effectiveTitle, effectiveBody, icon, kindLabel, DefaultDuration);

        return new
        {
            ok = true,
            kind = kindLabel,
            source = kindLabel,
            title = effectiveTitle,
            body = effectiveBody,
            icon = ToastIconProvider.Describe(icon),
            blocks = extra,
        };
    }

    private static object MediaStatus()
    {
        var c = MediaController.Instance;
        if (c == null)
            return new { active = false, playing = false, title = "", artist = "", elapsed = "0:00", progress = 0f };

        return new
        {
            active = c.IsActive,
            playing = c.IsPlaying,
            title = c.Title,
            artist = c.Artist,
            elapsed = c.TimelineElapsed,
            progress = c.TimelineProgress,
        };
    }

    private static object MediaControl(string action)
    {
        var c = MediaController.Instance;
        if (c == null)
            return new { ok = false, error = "媒体引擎未初始化" };

        var act = action?.Trim().ToLowerInvariant();
        switch (act)
        {
            case "toggle":
                c.TogglePlayPause();
                break;
            case "play":
                if (!c.IsPlaying) c.TogglePlayPause();
                break;
            case "pause":
                if (c.IsPlaying) c.TogglePlayPause();
                break;
            case "next":
                c.Next();
                break;
            case "prev":
            case "previous":
                c.Previous();
                break;
            default:
                return new { ok = false, error = $"未知动作 '{action}'，可用：toggle / play / pause / next / prev" };
        }

        return new { ok = true, action = act };
    }

    private static Task _showChain = Task.CompletedTask;
    private static readonly object _showGate = new();

    // 绝不在调用线程上同步跑宿主的展示链路：PostReminder 只是把撤下时间往未来推（没有 Sleep），
    // 但它会**同步** Invoke ReminderPosted —— 任何订阅方在里面阻塞都会顺着 RPC 变成对端眼里的超时。
    // 所以回执语义是「已受理」。用 ContinueWith 串成一条链（不是裸 Task.Run）以保证先到先显示。
    private static void ShowOnIsland(string title, string body, string? iconSpec, string source, TimeSpan duration)
    {
        var reminder = new ReminderData
        {
            Title = title,
            Body = body,
            Source = string.IsNullOrWhiteSpace(source) ? "MSP" : source,
            IconPath = string.IsNullOrWhiteSpace(iconSpec) ? null : iconSpec,
            Duration = duration,
        };

        lock (_showGate)
        {
            _showChain = _showChain.ContinueWith(_ => PostReminderSafely(reminder), TaskScheduler.Default);
        }
    }

    private static void PostReminderSafely(ReminderData reminder)
    {
        _suppressBroadcast = true;
        try
        {
            PluginManager.Instance.Host.PostReminder(reminder);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[MSP] 通知投递失败（对端已收到受理回执）: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _suppressBroadcast = false; }
    }

    private static void Publish(string path, object payload)
    {
        var node = _node;
        if (node == null) return;

        try { node.Publish(path, payload); }
        catch (Exception ex) { Logger.Warn($"[MSP] 广播 {path} 失败: {ex.Message}"); }
    }
}
