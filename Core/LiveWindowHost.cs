using System.Diagnostics;
using Microsoft.Win32;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NotchPeninsula;

/// <summary>
/// 一帧的单个实况窗绘制数据。只在渲染线程被 <see cref="LiveWindowHost.Tick"/> 写入、
/// 由 <c>LiveWindowRenderer.Draw</c> 读取，所以不需要加锁。
/// </summary>
public struct LiveWindowDrawItem
{
    /// <summary>注销后的退出动画期间为 null —— 此时不要再回调插件，渲染器退化为画缓存文字。</summary>
    public ILiveWindow? Window;

    public string? Id;

    /// <summary>气泡外接方框（画布逻辑坐标，已含 topY；Renderer.Draw 末尾画布已回到基线）。</summary>
    public SKRect Rect;

    /// <summary>0..255，已乘淡入 / 退出透明度。</summary>
    public byte Alpha;

    /// <summary>平滑后的进度；小于 0 表示不确定态。</summary>
    public float Progress;

    public bool Indeterminate;

    /// <summary>内圈旋转角（弧度，宿主累加）。</summary>
    public float SpinAngle;

    /// <summary>没有可用进度时是否继续转（内容撑满气泡时用）。</summary>
    public bool SpinWhenNoProgress;

    public bool Completed;

    public SKColor ProgressColor;

    public string Status;
    public string Title;
    public string PercentText;

    public bool Hovered;

    /// <summary>生成动画进度 0..1，1 = 结束（液体从岛体里挤出来）。</summary>
    public float SpawnProgress;

    /// <summary>分裂来源中心 x（画布坐标）。</summary>
    public float SpawnFromX;

    /// <summary>「移回宿主」的终点 x：岛体左缘（画布坐标）。退出动画期间用来画液体颈。</summary>
    public float MergeTargetX;

    /// <summary>退出动画进度 0..1（只有 <see cref="Exiting"/> 时有效）。</summary>
    public float ExitProgress;

    /// <summary>统一缩放（生成弹入 / 退出缩小）。</summary>
    public float Scale;

    public bool Exiting;
}

/// <summary>本帧全部实况窗数据。复用同一份对象与数组，稳态零分配。</summary>
public sealed class LiveWindowFrame
{
    public int Count;
    public readonly LiveWindowDrawItem[] Items = new LiveWindowDrawItem[LiveWindowHost.MaxItems];

    /// <summary>本帧气泡直径（跟随折叠岛高度，用户要求「直径和宿主高一致」）。</summary>
    public float BubbleDiameter;

    public bool PanelVisible;
    public IDetailPage? PanelPage;

    /// <summary>正在收起（渲染器据此让内容晚一点再淡出）。</summary>
    public bool PanelClosing;

    /// <summary>面板目标矩形（画布逻辑坐标）。</summary>
    public SKRect PanelRect;

    /// <summary>正在从气泡长出来的当前矩形（画布逻辑坐标）。</summary>
    public SKRect PanelRevealRect;

    /// <summary>展开动画的起点：气泡矩形（细胞分裂的「母细胞」）。</summary>
    public SKRect PanelSourceRect;

    public byte PanelAlpha;

    /// <summary>0..1；达到 1 表示展开完成。</summary>
    public float PanelReveal;
}

/// <summary>
/// 实况窗宿主：注册、布局、动画、命中、右键展开态状态机。
///
/// <para>
/// 手稿要点：新→旧从右往左排列、细胞分裂生成、完成「回到右边」、独立于主岛、不重复进岛内组件行。
/// 右键复用插件原有的 <see cref="IDetailPage"/> 展开成浮动面板，主岛不参与变形。
/// </para>
///
/// <para>
/// 坐标口径：全部使用「画布逻辑坐标」= 岛体渲染里 <c>topY</c> 平移之后、DPI 缩放之前的坐标。
/// 输入侧 WM_MOUSEMOVE 的 <c>mx/my</c> 除以 DPI 后正好就是这个坐标系，不要再减 topY。
/// </para>
/// </summary>
public static class LiveWindowHost
{
    public const float BubbleGap = 10f;
    public const float IslandGap = 12f;
    public const float ScreenMargin = 10f;
    public const int MaxItems = 8;

    /// <summary>气泡直径跟随折叠岛高度（用户要求：直径 = 岛体高度，上下沿对齐）。</summary>
    public static float BubbleDiameter { get; private set; } = 40f;

    /// <summary>没显式传 SpinSpeed 时的默认转速（圈/秒）：约 8 秒一圈，慢速不抢视线。</summary>
    private const float DefaultSpinRevPerSecond = 0.12f;

    // 液滴的「出生 / 吞入」共用一条时间轴。0.44~0.46s 是吸附感最明显的区间：
    // 再慢就拖沓，再快看不清脖子被掐断那一下。
    private const float SpawnSeconds = 0.5f;

    /// <summary>挂起动画时长：原地缩小淡出 0.5s，与出生 / 岛缩放同一节奏。</summary>
    private const float SuppressSeconds = 0.5f;

    /// <summary>出现序计数器：每次气泡出生 / 恢复递增，排布按它把最新的排最左。</summary>
    private static int _appearanceSeq;
    private const float ExitSeconds = 0.44f;
    private const float PanelOpenSeconds = 0.34f;
    private const float PanelCloseSeconds = 0.30f;

    private static readonly SKColor DefaultProgressColor = new(0x32, 0xD7, 0x4B);

    private sealed class Entry
    {
        public string Owner = "";
        public string Id = "";
        public ILiveWindow? Window;

        public bool Exiting;
        public double ExitElapsed;
        /// <summary>开始被吸进去的那一刻的 x：吸入曲线要从这里起算，不能每帧跟着目标重算。</summary>
        public float ExitFromX;

        public double SpawnElapsed;

        public float X;
        public float TargetX;
        public float SpawnFromX;

        public float TargetProgress;
        public float DisplayProgress;
        public float SpinAngle;

        /// <summary>没有可用进度时是否继续转（内容撑满气泡的形态下用）。</summary>
        public bool SpinWhenNoProgress;

        public string Status = "";
        public string Title = "";
        public string PercentText = "";
        public string LastPercentKey = "";

        public SKColor ProgressColor = DefaultProgressColor;
        public bool Completed;
        public bool Hovered;
        public bool Hidden;
        public bool Suppressed;   // 插件级开关关闭：先播收起动画，播完后留在列表里但不再绘制
        public double SuppressElapsed;   // 挂起收起的动画进度（0..ExitSeconds）
        public bool Broken;
        /// <summary>挂起动画形态：true = 吸回岛体（实况窗管理开关禁用，岛会现身承接）；
        /// false = 原地缩小淡出（静默勾选撤下 / 媒体开关关闭，岛多半同时在缩没）。</summary>
        public bool SuckBack = true;
        /// <summary>吸回动画完成后要补发 OnHostEnabledChanged(false)：岛先承接气泡（吸完），再变形容纳内容。</summary>
        public bool PendingEnableNotify;
        /// <summary>出生动画完成后要补发 OnHostEnabledChanged(true)：气泡先出生站稳（先出），岛再变形（后变形）。</summary>
        public bool PendingResumeNotify;
        /// <summary>出现序（每次出生 / 恢复递增）：排布永远把最新出现的排在整组最左（用户定稿）。</summary>
        public int AppearanceSeq;

        public float Alpha = 255f;
        public float Scale = 1f;
        public float SpawnProgress = 1f;
    }

    private sealed class PanelState
    {
        public string EntryId = "";
        public IDetailPage? Page;
        public float Width;
        public float Height;
        public double OpenElapsed;
        public bool Closing;
        public double CloseElapsed;
        public SKRect Target;
        public SKRect Reveal;
        public SKRect Source;
        public float RevealProgress;
    }

    private sealed class Handle : IDisposable
    {
        private readonly Entry _entry;
        private readonly ILiveWindow _window;
        private int _disposed;

        public Handle(Entry entry, ILiveWindow window)
        {
            _entry = entry;
            _window = window;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Remove(_entry, _window);
        }
    }

    private static readonly object _lock = new();
    private static readonly List<Entry> _entries = new(4);
    private static readonly LiveWindowFrame _frame = new();
    private static PanelState? _panel;

    /// <summary>插件点名要求重测浮动面板尺寸（<c>host.InvalidateDetailPageSize</c>），下一帧消费一次。</summary>
    private static volatile bool _panelResizeRequested;

    // ---- 按插件开关实况窗（显示设置页「实况窗」一栏）----
    // 默认全部插件启用；媒体不在这里控制（媒体默认关，去「媒体设置」里开）。
    private static readonly HashSet<string> _knownOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _disabledOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _ownerGate = new();
    private static bool _ownerSettingsLoaded;

    public static IReadOnlyList<string> KnownPluginOwners
    {
        get
        {
            EnsureOwnerSettingsLoaded();
            lock (_ownerGate) return _knownOwners.ToArray();
        }
    }

    public static bool IsOwnerEnabled(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId) || ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return true;
        EnsureOwnerSettingsLoaded();
        lock (_ownerGate) return !_disabledOwners.Contains(ownerId);
    }

    public static void SetOwnerEnabled(string ownerId, bool enabled)
    {
        if (string.IsNullOrEmpty(ownerId) || ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return;
        EnsureOwnerSettingsLoaded();

        bool changed;
        lock (_ownerGate)
        {
            changed = enabled ? _disabledOwners.Remove(ownerId) : _disabledOwners.Add(ownerId);
            if (changed) SaveOwnerSettingsLocked();
        }
        if (!changed) return;

        // 先把新状态挂到该插件的每一个活窗上（本帧起不再绘制 / 命中），再逐个通知插件：
        // 禁用 → 插件把内容挪回主岛组件行；恢复 → 内容可以重新交给实况窗。
        // 通知放在所有锁之外：插件回调里可能拿自己的锁，甚至重新注册。
        // ⚠️ 静默模式下「禁用插件重要信息」（静默页撤勾选）优先级更高：
        //    撤勾选的插件不管实况窗开关怎么切，气泡都保持挂起、岛体也不出现。
        bool keepSuppressed = Renderer.SilentActive && !SilentMode.IsOwnerAllowed(ownerId);
        var windows = SetOwnerSuppressed(ownerId, suppressed: !enabled || keepSuppressed, out var deferredNotify, out var deferredResume);
        if (deferredNotify || deferredResume) windows.Clear();   // 动画完成后由 Tick 补发通知
        if (deferredNotify) windows.Clear();   // 吸回动画完成后由 Tick 补发禁用通知
        foreach (var window in windows)
        {
            try { window.OnHostEnabledChanged(enabled); }
            catch (Exception ex) { Logger.Error($"[实况窗] 插件 {ownerId} 处理实况窗开关通知异常", ex); }
        }

        Logger.Info($"[实况窗] 插件 {ownerId} 的实况窗已{(enabled ? "启用" : "禁用")}（禁用时内容只允许留在主岛）");
    }

    /// <summary>
    /// 屏上挂着的重要气泡 owner（已过滤 Exiting / Suppressed / Broken，并按静默白名单过滤：
    /// 媒体看「媒体作为重要信息」开关，插件看静默页是否勾选 —— 没勾选不展示）。
    /// </summary>
    internal static List<string> AllowedImportantOwnerIds()
    {
        var result = new List<string>(2);
        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Window == null || e.Exiting || e.Suppressed || e.Broken) continue;
                try
                {
                    if (!e.Window.CountsAsImportantInfo) continue;
                    if (!SilentMode.IsOwnerAllowed(e.Owner)) continue;
                    result.Add(e.Owner);
                }
                catch { /* 插件属性异常：当它不声明重要信息 */ }
            }
        }
        return result;
    }

    /// <summary>屏上是否挂着「被静默白名单允许」的重要气泡。</summary>
    public static bool HasImportantLiveWindow => AllowedImportantOwnerIds().Count > 0;

    /// <summary>
    /// 插件被真正卸载时调用：把它的开关记录从「实况窗管理」名单里删掉，那一行才会跟着消失。
    /// <para>
    /// **只在真卸载时调**：更新安装走的是「移除旧副本 → 加载同 Id 新版」的替换路径，
    /// 记录必须保留，否则用户关掉的开关会被一次更新悄悄打开。气泡本身的注销由 PluginHost 的
    /// 卸载流程统一做（先 <see cref="RemoveOwner"/>，之后渲染线程再也不会回调插件对象）。
    /// </para>
    /// </summary>
    public static void ForgetOwner(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId) || ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return;

        EnsureOwnerSettingsLoaded();
        bool changed;
        lock (_ownerGate)
        {
            changed = _knownOwners.Remove(ownerId);
            changed |= _disabledOwners.Remove(ownerId);
            if (changed) SaveOwnerSettingsLocked();
        }
        if (changed) Logger.Info($"[实况窗] 插件 {ownerId} 已卸载，它的开关记录已删除");
    }

    /// <summary>
    /// 启动加载收尾：名单里只保留这次真正装起来的插件。
    /// 以前卸载不掉的历史记录（旧版本没清 / 用户直接删了 plugins 目录）会永远在「实况窗管理」
    /// 里挂一行没有气泡的插件，这里一次性收敛掉。
    /// <para>
    /// 只清显示名单、**不清禁用名单**：禁用中的插件这次没加载，行会暂时消失，
    /// 但用户当初关掉它的选择还在 —— 重新启用插件、它再注册时会重新出现且保持关闭。
    /// </para>
    /// </summary>
    /// <param name="liveOwnerIds">本次加载成功的插件 Id（owner 就是插件 Id）。</param>
    public static void PruneKnownOwners(IEnumerable<string> liveOwnerIds)
    {
        EnsureOwnerSettingsLoaded();

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (liveOwnerIds != null)
            foreach (var id in liveOwnerIds)
                if (!string.IsNullOrEmpty(id)) live.Add(id);

        List<string>? stale = null;
        lock (_ownerGate)
        {
            foreach (var id in _knownOwners)
                if (!live.Contains(id)) (stale ??= new List<string>(1)).Add(id);

            if (stale == null) return;
            foreach (var id in stale) _knownOwners.Remove(id);
            SaveOwnerSettingsLocked();
        }

        Logger.Info($"[实况窗] 已清理 {stale.Count} 条失效的插件开关记录：{string.Join(", ", stale)}");
    }

    /// <summary>
    /// 挂起 / 恢复某插件的全部实况窗，返回需要通知的窗口对象。
    /// 退出动画中的窗已经没有插件对象，只标记不通知；已在目标状态的不重复处理。
    /// </summary>
    internal static List<ILiveWindow> SetOwnerSuppressed(string ownerId, bool suppressed, out bool deferredNotify, out bool deferredResume)
    {
        deferredNotify = false;
        // 岛体此刻是否有可见形态（与 Tick 的锚点半程阈值同口径）：决定通知是否延迟到动画后。
        bool islandVisNow = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.5f
                            && Renderer.FullHideAlpha >= 0.5f;
        deferredResume = false;
        var windows = new List<ILiveWindow>(2);
        // 实况窗管理里被禁用的 owner：保持挂起，不允许被「静默清单同步」复活 ——
        // 否则关媒体开关时 SyncLiveWindowGates 会把用户明确禁用的实况窗重新拉出来。
        if (!suppressed)
        {
            lock (_ownerGate)
            {
                if (_disabledOwners.Contains(ownerId))
                {
                    Logger.Info("[实况窗] 插件 " + ownerId + " 已被实况窗管理禁用，忽略恢复请求");
                    deferredNotify = false;
                    deferredResume = false;
                    return windows;
                }
            }
        }
        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Suppressed == suppressed) continue;
                if (!string.Equals(e.Owner, ownerId, StringComparison.OrdinalIgnoreCase)) continue;
                e.Suppressed = suppressed;
                if (suppressed)
                {
                    // 「已经显示了主岛才需要吸回动画」（用户定稿）：
                    // 岛此刻在屏上有可见形态 → 吸回岛缘，岛现身承接内容；
                    // 岛隐藏（如静默媒体开关关闭，岛已缩没）→ 气泡原地缩小，主岛随后按需弹出（放大）。
                    e.SuckBack = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.5f
                                 && Renderer.FullHideAlpha >= 0.5f;
                    // 吸回场景（岛有东西）：OnHostEnabledChanged(false) 延迟到吸完再发 ——
                    // 插件内容回岛导致的岛体变形要等气泡吸完才开始（先吸，再变形）。
                    deferredNotify = e.SuckBack;
                    e.PendingEnableNotify = e.SuckBack;
                    // 和主动注销一模一样的「往右滑回岛体」吸附动画；播完由 Tick 留在列表里不绘制，
                    // 重新启用时再播一遍生成动画 —— 开关的手感和插件自己注销 / 注册完全一致。
                    e.SuppressElapsed = 0d;
                    e.ExitFromX = e.X;      // 吸入曲线从这个位置起算
                    e.Hovered = false;
                    // 正在显示这个气泡的详情面板要跟着收起（走正常收起动画，而不是下一帧被 Drop）。
                    if (_panel != null && !_panel.Closing
                        && string.Equals(_panel.EntryId, e.Id, StringComparison.OrdinalIgnoreCase))
                        ClosePanel();
                }
                else
                {
                    // 重新启用：与「插件重新注册」完全同一套「岛体挤出水滴」的生成动画。
                    e.SuppressElapsed = 0d;
                    e.SpawnElapsed = 0d;
                    e.PendingEnableNotify = false;
                    // 恢复（先出后变形，用户定稿）：岛体有东西时 OnHostEnabledChanged(true) 延迟到
                    // 出生动画完成（气泡站稳）再补发；岛已隐藏（编排 B / 居中态）则立即通知。
                    e.PendingResumeNotify = islandVisNow;
                    deferredResume = islandVisNow;
                    e.AppearanceSeq = ++_appearanceSeq;   // 恢复 = 重新出现：排到整组最左（用户定稿）
                    // 媒体气泡：出生动画重新走了，压岛计时也要重置 —— 否则 _activatedAtMs 是
                    // 旧值，压岛立即发生，气泡还在出生动画中就被新锚点硬贴（瞬移）。
                    if (string.Equals(e.Owner, MediaLiveWindowController.Owner, StringComparison.OrdinalIgnoreCase))
                        MediaLiveWindowController.NotifySpawnRestarted();

                    // 「只有一个东西」的启用（用户定稿）：恢复后岛内除它之外没有其他内容
                    // → 岛会缩没 —— 先压岛（岛带内容缩小），缩完气泡再从岛中心放大出生（编排 B）。
                    var bubbleOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int k = 0; k < _entries.Count; k++)
                    {
                        var o = _entries[k];
                        if (o.Window == null || o.Exiting || o.Suppressed || o.Broken) continue;
                        if (string.Equals(o.Owner, MediaLiveWindowController.Owner, StringComparison.OrdinalIgnoreCase)) continue;
                        bubbleOwners.Add(o.Owner);
                    }
                    bubbleOwners.Add(e.Owner);
                    if (!SilentMode.HasIslandContentBesides(bubbleOwners))
                        MediaLiveWindowController.RequestDelayedSpawn();
                    e.DisplayProgress = e.TargetProgress;
                    e.X = e.SpawnFromX = _lastIslandLeft + BubbleDiameter * 0.02f;
                }
                if (e.Window != null && !e.Exiting) windows.Add(e.Window);
            }
            UpdateSizingLocked();
        }
        deferredNotify = deferredNotify && windows.Count > 0;
        return windows;
    }

    private static void EnsureOwnerSettingsLoaded()
    {
        lock (_ownerGate)
        {
            if (_ownerSettingsLoaded) return;
            _ownerSettingsLoaded = true;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
                foreach (var id in SplitOwnerIds(key?.GetValue("LiveWindowOwners") as string)) _knownOwners.Add(id);
                foreach (var id in SplitOwnerIds(key?.GetValue("LiveWindowDisabledOwners") as string)) _disabledOwners.Add(id);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[实况窗] 读取插件实况窗开关失败，按默认（全部启用）处理：{ex.Message}");
            }
        }
    }

    private static string[] SplitOwnerIds(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void SaveOwnerSettingsLocked()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
            key?.SetValue("LiveWindowOwners", string.Join(";", _knownOwners));
            key?.SetValue("LiveWindowDisabledOwners", string.Join(";", _disabledOwners));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[实况窗] 保存插件实况窗开关失败：{ex.Message}");
        }
    }

    private static float _lastIslandLeft = 490f;
    private static long _lastTickTimestamp;
    private static float _reservedLeftWidth;
    private static float _requiredWindowHeight;

    private static readonly string?[] HitIds = new string?[MaxItems];
    private static readonly SKRect[] HitRects = new SKRect[MaxItems];
    private static int _hitCount;
    private static bool _panelHitActive;
    private static SKRect _panelHitRect;

    private static volatile float _mouseX = -1f;
    private static volatile float _mouseY = -1f;

    // 屏幕在「画布内容坐标」里的可见范围：窗口比屏幕宽，content x=0 可能已经在屏幕外。
    private static float _viewLeft = -100000f;
    private static float _viewRight = 100000f;

    // ---- 只读查询（Renderer / NotchWindow）----

    public static LiveWindowFrame Frame => _frame;

    public static float ReservedLeftWidth
    {
        get { lock (_lock) return _reservedLeftWidth; }
    }

    public static float RequiredWindowHeight
    {
        get { lock (_lock) return _requiredWindowHeight; }
    }

    public static bool HasOpenPanel
    {
        get { lock (_lock) return _panel != null; }
    }

    /// <summary>
    /// 该 owner 是否还有气泡在播退场动画（已注销、窗口对象已断开，但「滑回岛体」还没播完）。
    /// 媒体侧用它排队：气泡收完之前，岛体不要展开 —— 用户要求「气泡退场后岛体再展开」。
    /// </summary>
    public static bool HasExiting(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId)) return false;
        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Exiting && string.Equals(e.Owner, ownerId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>-1 = 永不自动收起；否则建议的自动收起毫秒数。</summary>
    public static int PanelCollapseDelayMs
    {
        get
        {
            lock (_lock)
            {
                var page = _panel?.Page;
                if (page == null) return 900;
                try
                {
                    var delay = page.AutoCollapseDelay;
                    if (delay < TimeSpan.Zero) return -1;
                    if (delay == TimeSpan.Zero) return 900;
                    return (int)Math.Clamp(delay.TotalMilliseconds, 500d, 60000d);
                }
                catch (Exception ex)
                {
                    Logger.Error("[实况窗] 读取详情页自动收起时长异常", ex);
                    return 900;
                }
            }
        }
    }

    // ---- 注册 / 注销 ----

    public static IDisposable? Register(string ownerId, ILiveWindow window)
    {
        if (window == null) return null;
        if (string.IsNullOrEmpty(ownerId)) ownerId = "builtin";

        string id;
        try { id = window.Id ?? ""; }
        catch (Exception ex)
        {
            Logger.Error("[实况窗] 读取 Id 异常，已拒绝注册", ex);
            return null;
        }
        if (id.Length == 0) id = "live-" + Environment.TickCount64;

        // 插件级开关：默认启用；在「显示设置 → 实况窗」里关掉后，注册直接失败（内容只留在主岛）。
        if (!ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase))
        {
            EnsureOwnerSettingsLoaded();
            lock (_ownerGate)
            {
                bool added = _knownOwners.Add(ownerId);
                if (added) SaveOwnerSettingsLocked();
                if (_disabledOwners.Contains(ownerId))
                {
                    Logger.Info($"[实况窗] 插件 {ownerId} 已禁用实况窗，本次注册忽略（内容保留在主岛）");
                    return null;
                }
            }

            // 静默模式：未勾选插件的重要信息气泡注册即挂起（静默页勾选是重要信息总开关）。
            // 启动即静默的场景在这里兜住 —— Program 读回注册表时插件还没加载。
            if (Renderer.SilentActive && !SilentMode.IsOwnerAllowed(ownerId))
                SetOwnerSuppressed(ownerId, true, out _, out _);
        }

        Entry entry;
        lock (_lock)
        {
            int newSeq = ++_appearanceSeq;   // 新注册：排到整组最左（用户定稿「新的永远在最左」）
            // 同 owner+Id 重复注册 = 原地替换（不重播分裂动画，插件想更新内容不必反复注销重注册）。
            var existing = _entries.Find(e =>
                !e.Exiting && e.Window != null && e.Owner == ownerId
                && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.AppearanceSeq = newSeq;   // 原地替换也算重新出现：排到最左
                existing.Window = window;
                existing.Broken = false;
                existing.Completed = false;
                existing.Suppressed = false;   // 重新启用后插件重注册：解除挂起
                existing.DisplayProgress = 0f;
                existing.LastPercentKey = "";
                return new Handle(existing, window);
            }

            int attached = AttachedCountLocked();
            // 每次出生都是「岛体挤出一颗水滴」：起点固定在岛体左缘，再滑到自己的槽位。
            // 岛不可见（气泡组居中）时改从岛体原区域中心起算，别让出生动画横穿半屏。
            bool regIslandVisible = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.02f
                                    && Renderer.FullHideAlpha >= 0.02f;
            float spawnFrom = regIslandVisible
                ? _lastIslandLeft + BubbleDiameter * 0.02f
                : _lastIslandLeft + Renderer.STANDBY_WIDTH / 2f;

            entry = new Entry
            {
                Owner = ownerId,
                Id = id,
                Window = window,
                X = spawnFrom,
                SpawnFromX = spawnFrom,
                SpawnElapsed = 0d
            };
            _entries.Add(entry);
            UpdateSizingLocked();
        }
        return new Handle(entry, window);
    }

    public static void RemoveOwner(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId)) return;

        List<ILiveWindow>? removed = null;
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                if (e.Owner != ownerId || e.Window == null) continue;
                var window = e.Window;
                DetachLocked(e);
                (removed ??= new List<ILiveWindow>(1)).Add(window);
            }
            UpdateSizingLocked();
        }
        NotifyRemoved(removed);
    }

    private static void Remove(Entry entry, ILiveWindow window)
    {
        ILiveWindow? removed = null;
        lock (_lock)
        {
            if (!_entries.Contains(entry)) return;
            if (!ReferenceEquals(entry.Window, window)) return; // 已被新注册替换，旧 handle 作废
            removed = entry.Window;
            DetachLocked(entry);
            UpdateSizingLocked();
        }
        if (removed != null) NotifyRemoved(new List<ILiveWindow>(1) { removed });
    }

    /// <summary>把窗与插件对象彻底断开：缓存一帧视觉数据，之后只走退出动画。</summary>
    private static void DetachLocked(Entry e)
    {
        if (e.Window == null) return;
        e.Window = null;
        e.Exiting = true;
        e.ExitElapsed = 0d;
        e.ExitFromX = e.X;          // 吸入曲线从当前位置起算
        e.Completed = false;

        if (_panel != null && string.Equals(_panel.EntryId, e.Id, StringComparison.OrdinalIgnoreCase))
            DropPanelLocked();
    }

    private static void NotifyRemoved(List<ILiveWindow>? windows)
    {
        if (windows == null) return;
        foreach (var w in windows)
        {
            try { w.OnRemoved(); }
            catch (Exception ex) { Logger.Error("[实况窗] OnRemoved 回调异常", ex); }
        }
    }

    /// <summary>程序退出 / 窗口销毁：不走动画、不再回调插件。</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _entries.Clear();
            DropPanelLocked();
            _hitCount = 0;
            _panelHitActive = false;
            _reservedLeftWidth = 0f;
            _requiredWindowHeight = 0f;
            _frame.Count = 0;
            _frame.PanelVisible = false;
            _frame.PanelPage = null;
        }
    }

    // ---- 输入 ----

    public static void SetMouse(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
    }

    /// <summary>屏幕在内容坐标里的左右边界（由 NotchWindow 每帧按窗口/显示器算好传进来）。</summary>
    public static void SetViewport(float contentLeft, float contentRight)
    {
        _viewLeft = contentLeft;
        _viewRight = contentRight;
    }

    public static bool HitAny(float x, float y) => HitBubble(x, y, out _) || HitPanel(x, y);

    /// <summary>点击余量（逻辑像素）：贴在圆边上点也能算命中，别让用户「点到了却差半个像素」。</summary>
    private const float BubbleHitSlop = 2f;

    /// <summary>
    /// 点是否落在某颗气泡上。命中区 = **整颗气泡圆**（半径再放 2px），不是气泡里的某个元素：
    /// 气泡现在只有「进度环 + 内容」、没有整块底，用户点在透明的内容区、进度环、甚至封面外的空白
    /// 都必须算「点这颗气泡」，宿主据此统一展开 / 收起详情面板。
    /// </summary>
    public static bool HitBubble(float x, float y, out string? id)
    {
        id = null;
        lock (_lock)
        {
            for (int i = 0; i < _hitCount; i++)
            {
                var rect = HitRects[i];
                float radius = rect.Width * 0.5f + BubbleHitSlop;
                float dx = x - rect.MidX, dy = y - rect.MidY;
                if (dx * dx + dy * dy > radius * radius) continue;
                id = HitIds[i];
                return true;
            }
        }
        return false;
    }

    public static bool HitPanel(float x, float y)
    {
        lock (_lock)
            return _panelHitActive && _panelHitRect.Contains(x, y);
    }

    /// <summary>
    /// 落点是否在展开面板当前显示的区域上（含展开动画中、还没进入正式命中的那一段）。
    /// 双击这类「不该落到主岛」的宿主手势用它：面板从气泡长出来的过程里也不能被当成岛内空白。
    /// </summary>
    public static bool ContainsPanelPoint(float x, float y)
    {
        lock (_lock)
        {
            var p = _panel;
            if (p == null) return false;
            if (_panelHitActive && _panelHitRect.Contains(x, y)) return true;
            return p.Reveal.Width > 1f && p.Reveal.Height > 1f && p.Reveal.Contains(x, y);
        }
    }

    // ---- 展开态（左右键共用同一个入口）----

    public static void TogglePanel(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        ILiveWindow? window = null;
        IDetailPage? page = null;
        string owner = "";
        bool sameOpen = false;
        lock (_lock)
        {
            var entry = FindAttachedLocked(id);
            if (entry == null) return;
            window = entry.Window;
            owner = entry.Owner;
            sameOpen = _panel != null && string.Equals(_panel.EntryId, entry.Id, StringComparison.OrdinalIgnoreCase);
            if (!sameOpen)
            {
                try { page = window?.DetailPage; }
                catch (Exception ex) { Logger.Error($"[实况窗] 读取详情页异常: {id}", ex); }
            }
        }

        if (sameOpen)
        {
            ClosePanel();
            return;
        }

        // 用户要求：插件不必再为实况窗注册一套新详情页 —— 没给就自动复用该插件组件原有的详情页。
        if (page == null && owner.Length > 0)
        {
            try { page = Plugins.PluginManager.Instance.Host.FindPrimaryDetailPage(owner); }
            catch (Exception ex) { Logger.Error($"[实况窗] 自动复用插件详情页失败: {owner}", ex); }
        }

        if (page == null)
        {
            // 没有详情页：交给注册方自己打开「原有的」界面（比如市场页）。
            try { window?.OnExpand(); }
            catch (Exception ex) { Logger.Error($"[实况窗] OnExpand 回调异常: {id}", ex); }
            return;
        }

        float w, h;
        try
        {
            // 「不指定尺寸」的详情页用宿主默认（与岛内详情页同一套），不再拒绝展开。
            Renderer.MeasureDetailPage(page, out w, out h);
        }
        catch (Exception ex)
        {
            Logger.Error($"[实况窗] 详情页尺寸测量异常: {id}", ex);
            return;
        }
        w = Math.Clamp(w, 120f, 1000f);
        h = Math.Clamp(h, 48f, 480f);

        lock (_lock)
        {
            var entry = FindAttachedLocked(id);
            if (entry == null) return;

            // 已经在展开另一个：直接替换，不做两套动画叠加。
            _panel = new PanelState
            {
                EntryId = entry.Id,
                Page = page,
                Width = w,
                Height = h,
                OpenElapsed = 0d,
                Closing = false,
                RevealProgress = 0f
            };
            _panelHitActive = false;
            UpdateSizingLocked();
        }
        Logger.Info($"[实况窗] 右键展开「原有展开态」: {id}（{w:0}x{h:0}）");
    }

    public static void ClosePanel()
    {
        lock (_lock)
        {
            if (_panel == null || _panel.Closing) return;
            _panel.Closing = true;
            _panel.CloseElapsed = 0d;
            _panelHitActive = false;
        }
    }

    public static void DispatchPanelMouseDown(float x, float y)
    {
        var (page, rect) = GetPanelForInput();
        if (page == null) return;
        try { page.OnMouseDown(x - rect.Left, y - rect.Top); }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板鼠标按下异常", ex); }
    }

    public static void DispatchPanelMouseMove(float x, float y)
    {
        var (page, rect) = GetPanelForInput();
        if (page == null) return;
        try { page.OnMouseMove(x - rect.Left, y - rect.Top); }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板鼠标移动异常", ex); }
    }

    public static void DispatchPanelMouseUp(float x, float y)
    {
        var (page, rect) = GetPanelForInput();
        if (page == null) return;
        try { page.OnMouseUp(x - rect.Left, y - rect.Top); }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板鼠标抬起异常", ex); }
    }

    public static void DispatchPanelClick(float x, float y)
    {
        var (page, rect) = GetPanelForInput();
        if (page == null) return;
        try
        {
            var hit = page.HitTest(x - rect.Left, y - rect.Top, rect);
            if (hit.IsHit) page.OnAction(hit.Action, x - rect.Left, y - rect.Top);
        }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板点击异常", ex); }
    }

    /// <summary>
    /// 双击透传：面板复用的是插件原有 <see cref="IDetailPage"/>，声明了
    /// <see cref="IDetailPage.AcceptsDoubleClick"/> 的页面应当和岛内详情页一样收到回调。
    /// </summary>
    public static bool DispatchPanelDoubleClick(bool isRight, float x, float y)
    {
        var (page, rect) = GetPanelForInput();
        if (page == null) return false;
        try
        {
            if (!page.AcceptsDoubleClick) return false;
            if (isRight) page.OnRightDoubleClick(x - rect.Left, y - rect.Top);
            else page.OnLeftDoubleClick(x - rect.Left, y - rect.Top);
        }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板双击异常", ex); return false; }
        return true;
    }

    public static void DispatchPanelMouseLeave()
    {
        var (page, _) = GetPanelForInput();
        if (page == null) return;
        try { page.OnMouseLeave(); }
        catch (Exception ex) { Logger.Error("[实况窗] 展开面板鼠标离开异常", ex); }
    }

    private static (IDetailPage?, SKRect) GetPanelForInput()
    {
        lock (_lock)
        {
            var p = _panel;
            if (p?.Page == null || p.RevealProgress < 0.85f) return (null, default);
            return (p.Page, p.Reveal);
        }
    }

    // ---- 渲染线程：每帧一次 ----

    /// <summary>
    /// 屏上有可见气泡（attached、非 exiting / suppressed / broken）。
    ///
    /// <para>岛隐藏但气泡还在时，窗口**不能**鼠标穿透（气泡要接受鼠标操作）；
    /// 主岛和气泡全部隐藏了才穿透。</para>
    /// </summary>
    public static bool HasVisibleBubble
    {
        get
        {
            lock (_lock)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.Window == null || e.Exiting || e.Suppressed || e.Broken) continue;
                    return true;
                }
            }
            return false;
        }
    }

    /// <param name="topY">12 * styleProgress（岛体内边距）。</param>
    /// <param name="islandLeft">岛体物理左边界（画布逻辑坐标）。</param>
    /// <param name="islandWidth">当前岛体宽度。</param>
    /// <param name="currentHeight">当前岛体高度。</param>
    public static void Tick(float topY, float islandLeft, float islandWidth, float currentHeight)
    {
        long nowTs = Stopwatch.GetTimestamp();
        float dt = 1f / 60f;
        if (_lastTickTimestamp != 0 && nowTs > _lastTickTimestamp)
            dt = (float)Math.Clamp((nowTs - _lastTickTimestamp) / (double)Stopwatch.Frequency, 0.001, 0.1);
        _lastTickTimestamp = nowTs;

        // 用户要求：实况窗直径 = 岛体高度，而且上下沿都要和岛体对齐。
        // 展开态 / 通知弹高时岛体更高，气泡仍按「折叠态高度」对齐（否则气泡会被这两个临时状态撑大）。
        float islandBodyH = MathF.Max(0f, MathF.Min(currentHeight, Renderer.MEDIA_HEIGHT));
        float islandCenterY = topY + islandBodyH / 2f;
        // 不夹在 28..84：自定义「全局折叠态高度」可能超出这个范围，夹了就会出现「气泡比岛体高/矮」。
        float diameter = MathF.Max(4f, islandBodyH);
        BubbleDiameter = diameter;
        float bubbleTop = topY + (islandBodyH - diameter) * 0.5f;   // 正常情况就 = topY：上下都贴岛体边

        /* ─────────── 静默模式动画总方案（用户定稿，全部 0.5s 统一节奏） ───────────
         * 形态语义：出现 = 放大淡入；消失 = 缩小淡出；位置变化 = 指数平滑（~0.4s），全程无瞬移。
         *
         * ① 启用媒体实况窗（岛无其他内容，编排 B）：
         *    主岛展示 3s 媒体 → 岛带媒体缩小消失（0.5s）→ 缩完气泡从岛中心（=居中位）原地放大出生。
         * ② 启用媒体实况窗（岛有其他内容，编排 A）：
         *    气泡从媒体态岛缘出生（排在原有气泡最左边，不让位）→ 站稳 0.8s 后压岛
         *    → 岛变形/保持 → 气泡组平滑移到新锚点。
         * ③ 启用插件实况窗（岛体有东西 = 完整可见）：气泡从岛缘吸出（EaseOutBack 挤出，
         *    先落贴岛缘位，站稳后稳态平滑移到最左槽位）→ 岛再变形。
         * ④ 禁用实况窗（岛体有东西）：气泡先吸入岛缘（LiquidSuck，途中淡出过半开始），
         *    吸完才补发 OnHostEnabledChanged(false)（PendingEnableNotify）→ 插件内容回岛 → 岛再变形。
         * ⑤ 禁用（岛体没东西 / 静默撤勾选 / 关媒体开关）：气泡原地缩小淡出（不吸回），
         *    岛按需缩没或变形；其余气泡平滑补位。
         * ⑥ toast 出入：岛 instantShow 弹出 / 弹簧缩没；气泡锚点在半程（scale≥0.5）切换，
         *    前半程贴原锚点、后半程平滑移向新锚点，与岛缩放同步，无瞬移无停顿。
         * 锚点归属：静默下岛「该出现/已出现」（ShouldShowIsland / 压制中且岛在屏）→ 贴岛缘；
         * 岛缩没（无内容）→ 居中。排布：新气泡一律出生在整组最左边，已有气泡不让位。
         * ────────────────────────────────────────────────────────────────*/

        // 岛是否在屏上有可见形态：静默缩没 / 整层淡出都算「没了」——
        // 这时气泡不再贴岛体左缘，整组在岛体原区域中心居中（岛没了没有参照边，贴边看着像悬在半空）。
        // 锚点切换阈值取「彻底缩没」（<0.02 才切，用户定稿）：启用编排里气泡出生移到最左、站稳、
        // 压岛，**岛彻底缩没后**整组才开始平滑右移居中；岛弹出方向 scale 一过 0.02 气泡即贴向岛缘。
        // 不锁压制态（SuppressMediaInIsland）：媒体退场时岛先缩没、播完才弹回，
        // 若压制锁岛缘、播完回落居中、岛弹回又切岛缘 —— 气泡会「移到一半又回来又过去」（用户实测）。
        bool islandVisible = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.08f
                             && Renderer.FullHideAlpha >= 0.5f;

        // 岛可见性跳变沿（双向）：气泡组**平滑**移动到新锚点（贴岛缘 ⇄ 居中），不瞬移（用户定稿）。
        // 指数跟随已放慢（~0.4s），长距离也是温和滑过去，配合岛体的放大 / 缩小同步完成。

        var deferredList = new List<ILiveWindow>(2);
        var resumeList = new List<ILiveWindow>(2);
        var layoutOrder = new List<Entry>(_entries.Count);
        lock (_lock)
        {
            _lastIslandLeft = islandLeft;
            float mouseX = _mouseX, mouseY = _mouseY;

            int attached = AttachedCountLocked();
            int hiddenCount = Math.Max(0, attached - MaxItems);
            int attachedIndex = 0;
            int visibleSlots = Math.Min(attached, MaxItems);
            float centerX = islandLeft + islandWidth / 2f;
            float groupW = visibleSlots * diameter + MathF.Max(0, visibleSlots - 1) * BubbleGap;
            float anchorRight = islandVisible
                ? islandLeft - IslandGap
                : centerX + groupW / 2f;

            // 排布顺序（用户定稿「无论什么时候新的都到最左」）：按出现序升序遍历 ——
            // 最早的先分配（贴岛缘），最新出现的最后分配（slot 最大 → 整组最左）；
            // 已有气泡的 slot 与相对顺序都不变，新气泡不挤占任何人的位置。
            layoutOrder.Clear();
            for (int i = 0; i < _entries.Count; i++) layoutOrder.Add(_entries[i]);
            layoutOrder.Sort((a, b) => a.AppearanceSeq.CompareTo(b.AppearanceSeq));

            foreach (var e in layoutOrder)
            {
                // 所有「液体转移」的终点都是岛体左缘（画布坐标）。
                float edgeX = islandLeft + diameter * 0.02f;

                if (e.Suppressed && !e.Exiting)
                {
                    // 插件级开关关闭 / 静默勾选撤下：**原地**缩小淡出。
                    // ⚠️ 不吸回岛体 —— 触发挂起时主岛多半已在缩没（如静默媒体开关关闭），
                    //    吸过去会把岛「变回折叠态」，编排就乱了。气泡自己缩掉，
                    //    它不占槽位（AttachedCountLocked 排除），其余气泡自动右移补位。
                    if (e.Window != null)
                    {
                        // 串行编排（用户定稿）：气泡动画**立即起步**（先吸 / 先出），
                        // 岛体变形由压制维持或内容回岛的宽度弹簧自然滞后（后变形）。
                        e.SuppressElapsed = Math.Min(SuppressSeconds, e.SuppressElapsed + dt);
                        float t = (float)Math.Clamp(e.SuppressElapsed / SuppressSeconds, 0d, 1f);
                        // 统一「缩小」语义：原地缩到 0 + 淡出，0.5s（错峰 0.15s 后起步）。
                        // 岛是否现身承接都一样 —— 气泡自己缩掉，不吸回岛缘（岛缩没时吸回会变回折叠态）。
                        e.X = e.ExitFromX;
                        e.TargetX = e.ExitFromX;
                        e.Alpha = 255f * (1f - SmoothStep01(t));
                        e.Scale = MathF.Max(0f, 1f - SmoothStep01(t));
                        // 吸回/缩小动画播完：补发延迟的禁用通知，插件内容此刻才回岛 → 岛体开始变形。
                        if (t >= 1f && e.PendingEnableNotify && e.Window != null)
                        {
                            deferredList.Add(e.Window);
                            e.PendingEnableNotify = false;
                        }
                    }
                    e.TargetX = e.ExitFromX;
                    e.Hovered = false;
                    continue;
                }

                // 未退出、未断开的窗取一帧视觉快照。插件抛异常 = 熔断该窗（走退出动画）。
                if (e.Window != null && !e.Exiting)
                {
                    try
                    {
                        ApplyVisualLocked(e, e.Window.GetVisual(), dt);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[实况窗] {e.Id} 取视觉快照异常，已熔断", ex);
                        e.Broken = true;
                        DetachLocked(e);
                    }
                }

                if (e.Exiting)
                {
                    e.ExitElapsed = Math.Min(ExitSeconds, e.ExitElapsed + dt);
                    float t = (float)Math.Clamp(e.ExitElapsed / ExitSeconds, 0d, 1d);
                    // 吸附：起步慢、越过一半后越来越快（真水滴是被表面张力「抽」进去的），
                    // 最后 16% 只剩一丝，透明度和半径同时收掉，不会在岛体边上留残影。
                    e.X = e.ExitFromX + (edgeX - e.ExitFromX) * LiquidSuck(t);
                    e.TargetX = edgeX;
                    e.Scale = 1f;
                    // 淡出提前到吸回途中（过半就开始）：到岛缘时已完全透明，
                    // 与岛内内容的出现衔接不留叠影（用户实测「不要闪」）。
                    e.Alpha = 255f * (1f - SmoothStep01((t - 0.45f) / 0.45f));
                    e.Hovered = false;
                }
                else
                {
                    int slot = attachedIndex++;
                    if (slot < hiddenCount)
                    {
                        e.TargetX = -10000f;
                    }
                    else
                    {
                        // 新的在右、旧的在左；退出中的不占槽位。
                        int drawnSlot = slot - hiddenCount;
                        // 排布方向（用户定稿）：**新气泡一律出生在整组最左边**，已有气泡位置不变、不让位。
                        // drawnSlot 0 = 最早注册（贴岛缘），越大越新 → 越靠左。
                        e.TargetX = anchorRight - diameter / 2f
                                    - drawnSlot * (diameter + BubbleGap);
                    }

                    bool budding = e.SpawnElapsed < SpawnSeconds;
                    // 出生动画刚站稳：补发延迟的启用通知（插件内容此刻才进气泡 → 岛开始变形）。
                    if (!budding && e.PendingResumeNotify && e.Window != null)
                    {
                        resumeList.Add(e.Window);
                        e.PendingResumeNotify = false;
                    }
                    e.SpawnElapsed = Math.Min(SpawnSeconds, e.SpawnElapsed + dt);
                    e.SpawnProgress = (float)Math.Clamp(e.SpawnElapsed / SpawnSeconds, 0d, 1d);
                    e.Scale = 1f;

                    if (budding)
                    {
                        // 岛体有东西（用户定稿）：**先创建在岛缘 → 平滑移到整组最左**（EaseOutBack 带过冲），
                        // 移动期间其他气泡位置不变；站稳压岛后再与整组一起平滑右移居中。
                        // 岛没东西（隐藏 / 居中态）：原地放大淡入（编排 B）。
                        float from = islandVisible ? e.SpawnFromX : e.TargetX;
                        e.X = from + (e.TargetX - from) * EaseOutBack(e.SpawnProgress, 1.1f);
                        e.Alpha = 255f * SmoothStep01(e.SpawnProgress / 0.6f);
                        e.Scale = 1f;
                    }
                    else
                    {
                        // 稳态：指数平滑跟随槽位与锚点（邻居让位 / 居中 ⇄ 贴岛缘），不瞬移。
                        float k = 1f - MathF.Exp(-6f * dt);   // 放慢：~0.4s 温和归位
                        e.X += (e.TargetX - e.X) * k;
                        e.Alpha = 255f;
                    }

                    e.Hovered = mouseX >= 0f && MathF.Abs(mouseX - e.X) <= diameter / 2f
                                && mouseY >= bubbleTop && mouseY <= bubbleTop + diameter;
                }

                // 「不出屏」：窗口比屏幕宽，content x=0 可能已在屏幕外；装不下的最旧气泡不画、不参与命中。
                e.Hidden = e.TargetX < -1000f
                           || (!e.Exiting && e.TargetX - diameter / 2f < _viewLeft + ScreenMargin);
            }

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].ExitElapsed >= ExitSeconds) _entries.RemoveAt(i);
            }

            TickPanelLocked(islandCenterY, diameter, dt);
            PublishFrameLocked(bubbleTop, islandLeft, diameter);
            UpdateSizingLocked();
        }

        // 锁外补发：禁用通知（吸回完成 → 内容回岛 → 岛变形）/ 启用通知（出生站稳 → 内容进气泡 → 岛变形）。
        foreach (var w in deferredList)
        {
            try { w.OnHostEnabledChanged(false); }
            catch (Exception ex) { Logger.Error("[实况窗] 延迟禁用通知异常", ex); }
        }
        foreach (var w in resumeList)
        {
            try { w.OnHostEnabledChanged(true); }
            catch (Exception ex) { Logger.Error("[实况窗] 延迟启用通知异常", ex); }
        }
    }

    private static void TickPanelLocked(float islandCenterY, float diameter, float dt)
    {
        var panel = _panel;
        if (panel == null) return;

        var entry = FindAttachedLocked(panel.EntryId);
        if (entry == null)
        {
            // 面板的主人被插件级开关挂起：气泡已经不画了，但已经展开的面板要照常收完 ——
            // 直接 DropPanelLocked 会让它「啪」一下消失，与其它收起路径的动画不一致。
            entry = panel.Closing ? FindSuppressedLocked(panel.EntryId) : null;
        }
        if (entry == null)
        {
            DropPanelLocked();
            return;
        }

        // 声明了「尺寸随内容变」的页面每帧重测（媒体面板有没有时间轴会换高度）：
        // 浮层要跟主岛展开态保持同一尺寸，不能只在展开那一刻量一次。
        if (!panel.Closing)
            RemeasurePanelIfNeededLocked(panel);

        float bubbleTop = MathF.Max(2f, islandCenterY - diameter / 2f);
        float bubbleBottom = bubbleTop + diameter;
        float left = Math.Clamp(entry.X - panel.Width / 2f,
            _viewLeft + 8f, MathF.Max(_viewLeft + 8f, _viewRight - panel.Width - 8f));
        panel.Target = new SKRect(left, bubbleBottom + 10f, left + panel.Width, bubbleBottom + 10f + panel.Height);

        if (panel.Closing)
        {
            panel.CloseElapsed = Math.Min(PanelCloseSeconds, panel.CloseElapsed + dt);
            float t = (float)(panel.CloseElapsed / PanelCloseSeconds);
            // 收回也走 ease-in-out：起步和落点都慢，不会像「啪一下消失」。
            panel.RevealProgress = Math.Clamp(1f - EaseInOutCubic(t), 0f, 1f);
            if (panel.CloseElapsed >= PanelCloseSeconds)
            {
                DropPanelLocked();
                return;
            }
        }
        else
        {
            panel.OpenElapsed = Math.Min(PanelOpenSeconds, panel.OpenElapsed + dt);
            float t = (float)(panel.OpenElapsed / PanelOpenSeconds);
            // ease-out-cubic 而不是弹簧：弹簧在 0.1s 内冲到 1 再抖动，视觉上就是「闪一下就好」。
            // 这里连续减速展开，配合渲染器的收口桥 / 内容延迟淡入，才是「细胞分裂后长成面板」的丝滑感。
            float inv = 1f - t;
            panel.RevealProgress = Math.Clamp(1f - inv * inv * inv, 0f, 1f);
        }

        panel.Source = new SKRect(entry.X - diameter / 2f, bubbleTop, entry.X + diameter / 2f, bubbleBottom);
        var bubbleRect = panel.Source;
        float p = panel.RevealProgress;
        panel.Reveal = new SKRect(
            bubbleRect.Left + (panel.Target.Left - bubbleRect.Left) * p,
            bubbleRect.Top + (panel.Target.Top - bubbleRect.Top) * p,
            bubbleRect.Right + (panel.Target.Right - bubbleRect.Right) * p,
            bubbleRect.Bottom + (panel.Target.Bottom - bubbleRect.Bottom) * p);

        // 展开完成才吃鼠标，避免动画期误点。
        _panelHitActive = !panel.Closing && panel.RevealProgress >= 0.85f && panel.Page != null;
        _panelHitRect = panel.Target;
    }

    /// <summary>
    /// 尺寸重测：插件点名要求（<c>host.InvalidateDetailPageSize</c>）时强制测一次，
    /// 否则只有声明「尺寸随内容变」的页面才每帧重测。
    /// 调用方已持有 <see cref="_lock"/>。
    /// </summary>
    private static void RemeasurePanelIfNeededLocked(PanelState panel)
    {
        var page = panel.Page;
        if (page == null) return;
        try
        {
            bool forced = _panelResizeRequested;
            if (!forced && !page.ResizeWithContent) return;
            _panelResizeRequested = false;

            // 不指定尺寸 → 宿主默认（与岛内详情页同一套），不再当成非法值忽略。
            Renderer.MeasureDetailPage(page, out float w, out float h);

            w = Math.Clamp(w, 120f, 1000f);
            h = Math.Clamp(h, 48f, 480f);
            if (MathF.Abs(w - panel.Width) < 0.5f && MathF.Abs(h - panel.Height) < 0.5f) return;

            panel.Width = w;
            panel.Height = h;
            UpdateSizingLocked();
        }
        catch (Exception ex)
        {
            Logger.Error("[实况窗] 详情页尺寸重测异常", ex);
        }
    }

    /// <summary>插件改了内容、尺寸该跟着变：下一次 Tick 强制重测一次浮动面板尺寸。</summary>
    public static void RequestPanelResize() => _panelResizeRequested = true;

    private static void PublishFrameLocked(float bubbleTop, float islandLeft, float diameter)
    {
        var frame = _frame;
        frame.Count = 0;
        frame.BubbleDiameter = diameter;
        frame.Items.AsSpan().Clear();

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Hidden) continue;
            // 挂起收起的动画要照常画完；播完之后才彻底不绘制。
            if (e.Suppressed && !e.Exiting && e.SuppressElapsed >= ExitSeconds) continue;
            if (frame.Count >= MaxItems) break;

            ref var item = ref frame.Items[frame.Count];
            item.Window = e.Window;
            item.Id = e.Id;
            item.Rect = new SKRect(e.X - diameter / 2f, bubbleTop, e.X + diameter / 2f, bubbleTop + diameter);
            item.Alpha = (byte)Math.Clamp((int)e.Alpha, 0, 255);
            item.Progress = e.DisplayProgress;
            item.Indeterminate = e.DisplayProgress < 0f;
            item.SpinAngle = e.SpinAngle;
            item.SpinWhenNoProgress = e.SpinWhenNoProgress;
            item.Completed = e.Completed;
            item.ProgressColor = e.ProgressColor;
            item.Status = e.Status;
            item.Title = e.Title;
            item.PercentText = e.PercentText;
            item.Hovered = e.Hovered;
            item.SpawnProgress = e.SpawnProgress;
            item.SpawnFromX = e.SpawnFromX;
            item.MergeTargetX = islandLeft + diameter * 0.02f;   // 「移回宿主」的终点：岛体左缘
            // 挂起收起也走同一套「被宿主吞进去」的动画：渲染器只认 Exiting / ExitProgress，
            // 所以这里把挂起前段也当成退出态发布。
            bool swallowing = e.Exiting || (e.Suppressed && e.SuppressElapsed < ExitSeconds);
            item.ExitProgress = swallowing
                ? (float)Math.Clamp((e.Exiting ? e.ExitElapsed : e.SuppressElapsed) / ExitSeconds, 0.0, 1.0)
                : 0f;
            item.Scale = e.Scale;
            item.Exiting = swallowing;
            frame.Count++;
        }

        frame.PanelVisible = _panel != null && _panel.RevealProgress > 0.001f;
        frame.PanelPage = _panel?.Page;
        if (_panel != null)
        {
            frame.PanelRect = _panel.Target;
            frame.PanelRevealRect = _panel.Reveal;
            frame.PanelSourceRect = _panel.Source;
            frame.PanelReveal = _panel.RevealProgress;
            frame.PanelClosing = _panel.Closing;
            frame.PanelAlpha = (byte)Math.Clamp((int)(255f * _panel.RevealProgress), 0, 255);
        }
        else
        {
            frame.PanelRect = default;
            frame.PanelRevealRect = default;
            frame.PanelSourceRect = default;
            frame.PanelReveal = 0f;
            frame.PanelClosing = false;
            frame.PanelAlpha = 0;
        }

        _hitCount = 0;
        for (int i = 0; i < frame.Count && _hitCount < MaxItems; i++)
        {
            ref var item = ref frame.Items[i];
            // 淡入 / 退场的过程里也要能点：以前按 Alpha>=16 过滤，气泡刚出来或快收完的那几帧
            // 「看得见却点不动」，观感就是「只有点到中间的文字才展开」。
            if (item.Id == null) continue;
            HitIds[_hitCount] = item.Id;
            HitRects[_hitCount] = item.Rect;
            _hitCount++;
        }
    }

    private static void ApplyVisualLocked(Entry e, LiveWindowVisual visual, float dt)
    {
        if (visual.Status != null) e.Status = visual.Status;
        if (visual.Title != null) e.Title = visual.Title;
        e.Completed = visual.Completed;

        float target = visual.Progress;
        e.TargetProgress = target;
        if (target < 0f)
        {
            e.DisplayProgress = -1f;
            if (visual.PercentText != null) e.PercentText = visual.PercentText;
        }
        else
        {
            target = Math.Clamp(target, 0f, 1f);
            if (e.DisplayProgress < 0f) e.DisplayProgress = 0f;
            float k = 1f - MathF.Exp(-10f * dt);
            e.DisplayProgress += (target - e.DisplayProgress) * k;
            if (target >= 0.999f && e.DisplayProgress > 0.985f) e.DisplayProgress = 1f;
            if (e.DisplayProgress > target + 0.002f) e.DisplayProgress = target; // 回退（重下 / 重置）立即跟手

            if (visual.PercentText != null)
            {
                e.PercentText = visual.PercentText;
                e.LastPercentKey = "x";
            }
            else
            {
                int pct = Math.Clamp((int)Math.Round(e.DisplayProgress * 100.0, MidpointRounding.AwayFromZero), 0, 100);
                string key = pct.ToString();
                if (e.LastPercentKey != key) { e.PercentText = key + "%"; e.LastPercentKey = key; }
            }
        }

        e.ProgressColor = visual.ProgressColor ?? DefaultProgressColor;

        // 「没有进度时要不要转」走带默认实现的接口成员（旧插件不实现也有兜底），
        // 单独 try/catch：插件这个覆盖写崩了只当 false，不能连累整个窗。
        try { e.SpinWhenNoProgress = e.Window?.SpinWhenNoProgress ?? false; }
        catch (Exception ex)
        {
            e.SpinWhenNoProgress = false;
            Logger.Warn($"[实况窗] {e.Id} 读取 SpinWhenNoProgress 异常，按 false 处理: {ex.Message}");
        }

        // 没有可用进度时内容会撑满整个气泡；这时默认不转，只有注册方显式打开才继续转。
        bool noProgress = e.TargetProgress < 0f;
        if (visual.SpinInner && (!noProgress || e.SpinWhenNoProgress))
        {
            // 转速由注册方传入（圈/秒），没传就走默认慢速；别再写死 0.5 圈/秒那种抢视线的速度。
            float speed = visual.SpinSpeed > 0.001f ? visual.SpinSpeed : DefaultSpinRevPerSecond;
            e.SpinAngle = (e.SpinAngle + dt * MathF.PI * 2f * speed) % (MathF.PI * 2f);
        }
    }

    private static void UpdateSizingLocked()
    {
        float diameter = BubbleDiameter;
        int attached = AttachedCountLocked();
        int drawn = Math.Min(attached, MaxItems);
        _reservedLeftWidth = drawn <= 0
            ? 0f
            : IslandGap + drawn * diameter + (drawn - 1) * BubbleGap + ScreenMargin;

        float h = 0f;
        if (_panel != null)
        {
            // 保守估算：topY 最大 12 + 气泡中心最多到折叠高度的一半 + 半径 + 间距 + 面板高。
            float bubbleBottom = 12f + MathF.Max(0f, Renderer.MEDIA_HEIGHT) / 2f + diameter / 2f;
            h = bubbleBottom + 10f + _panel.Height + 12f;
        }
        _requiredWindowHeight = h;
    }

    private static int AttachedCountLocked()
    {
        int n = 0;
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Window != null && !_entries[i].Exiting && !_entries[i].Suppressed) n++;
        return n;
    }

    private static Entry? FindAttachedLocked(string id)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Window != null && !e.Exiting && !e.Suppressed
                && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// <summary>被插件级开关挂起、但仍持有插件对象的窗 —— 给面板收起动画做几何锚点。</summary>
    private static Entry? FindSuppressedLocked(string id)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Suppressed && e.Window != null && !e.Exiting
                && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    private static void DropPanelLocked()
    {
        _panel = null;
        _panelHitActive = false;
        _frame.PanelVisible = false;
        _frame.PanelPage = null;
        _frame.PanelClosing = false;
    }

    /// <summary>
    /// 吸附曲线：起步慢、后段加速（水滴是被表面张力越抽越快的），末端一拍收住。
    /// 三条动画（位置、半径、颈的粗细）都从同一个 t 推，彼此才不会错拍。
    /// </summary>
    private static float LiquidSuck(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (1.75f - 0.75f * t);
    }

    /// <summary>脱离曲线：先快后慢，落点带一点过冲（水滴甩出去再回稳）。</summary>
    private static float EaseOutBack(float t, float overshoot)
    {
        float u = Math.Clamp(t, 0f, 1f) - 1f;
        return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
    }

    private static float SmoothStep01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private static float EaseInOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }
}
