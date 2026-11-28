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

    /// <summary>挂起动画时长：缩小淡出 0.5s，与出生 / 岛缩放同一节奏。</summary>
    private const float SuppressSeconds = 0.5f;

    /// <summary>超过 MaxItems 时那颗气泡的退场动画时长（右滑回岛缘 / 原地缩小），0.5s 同节奏。</summary>
    private const float OverflowSeconds = 0.5f;

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

        /// <summary>
        /// 出生起点只锁一次（0.5s 出生动画期间不再重算）。
        ///
        /// <para>以前每帧按「岛当前可见否」在 <c>SpawnFromX</c> / <c>TargetX</c> 之间二选一，
        /// 岛正好在放大（scale 越过 0.5 的那一帧）起点会整体跳一段 —— 与「全程无瞬移」冲突。</para>
        /// </summary>
        public bool SpawnAnchorLocked;
        public float SpawnAnchorX;

        /// <summary>
        /// 退场时是否「吸回岛缘」：注销那一刻岛在屏上有可见形态才吸；
        /// 岛已缩没（气泡组居中）时没有可回去的参照边 → 原地缩小淡出（用户定稿 U1A）。
        /// 在 <see cref="DetachLocked"/> 里一次性锁定，退场途中岛缩放变化也不会中途换动画。
        /// </summary>
        public bool ExitSuckBack = true;

        // ---- 排不下（超过 MaxItems）时的退场（用户定稿 U4）----
        // 以前直接把 TargetX 甩到 -10000 当场消失；现在走一段退场动画：
        // 岛在屏上 → 右滑回岛缘淡出（B）；岛已缩没 → 原地缩小淡出（A）。
        public bool Overflowing;
        public double OverflowElapsed;
        public bool OverflowSuckBack = true;
        public float OverflowFromX;

        /// <summary>下一帧直接把 X 落到槽位（切显示模式时用：状态瞬切，位置也不该再滑 0.3s）。</summary>
        public bool SnapPosition;
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

        /// <summary>
        /// 主人气泡最后已知的 X（画布逻辑坐标）。气泡播退场动画期间它还在列表里，直接跟；
        /// 气泡被移除后（0.44s 到点）还要靠这份缓存把面板收起动画走完 —— 否则面板会「啪」一下消失。
        /// </summary>
        public float AnchorX;
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
    // 记录已调用实况窗且仍加载的插件。即使登记因开关被拒绝，也要保留重新启用的入口。
    private static readonly HashSet<string> _liveWindowPluginOwners = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _usesLiveWindows;

    internal static bool UsesLiveWindows => _usesLiveWindows;

    private static void UpdateLiveWindowUsageLocked()
    {
        bool inUse = _entries.Count > 0 || _liveWindowPluginOwners.Count > 0;
        if (_usesLiveWindows == inUse) return;
        _usesLiveWindows = inUse;
        ConsoleWindow.RequestRerender();
    }

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

    /// <summary>
    /// 当前显示模式下这个 owner 是否被允许显示实况窗。
    ///
    /// <para>普通模式看「显示设置」里该插件有没有勾选；静默模式看「静默模式设置」里的勾选。
    /// 没勾选 = 这个插件的<b>内容</b>就不该出现，气泡也不能单独冒出来（用户要求），
    /// 同时「实况窗管理」里那一行置灰、点不动。</para>
    ///
    /// <para>内置 owner（媒体等）不参与：媒体走「媒体设置」里自己的开关。</para>
    /// </summary>
    public static bool IsOwnerAllowedByDisplay(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId)) return true;
        if (ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return true;

        var pm = PluginManager.Instance;
        var entry = pm.FindById(ownerId);
        if (entry == null || string.IsNullOrEmpty(entry.Key)) return true;   // 插件已卸载：交给卸载 / 清理流程
        return Renderer.SilentActive ? pm.IsSilentShown(entry.Key) : pm.IsDisplayed(entry.Key);
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
        //    「显示设置」里没勾选的插件在普通模式同理（内容不该出现，气泡也别冒头）。
        bool keepSuppressed = !IsOwnerAllowedByDisplay(ownerId);
        var windows = SetOwnerSuppressed(ownerId, suppressed: !enabled || keepSuppressed, out var deferredNotify, out var deferredResume);
        if (deferredNotify || deferredResume) windows.Clear();   // 动画完成后由 Tick 补发通知
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
        // 名单空了之后，侧栏「实况窗管理」那一页就该收起来。
        if (changed) ConsoleWindow.RequestRerender();
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
        // 名单变了 → 侧栏「实况窗管理」的显隐跟着变，让设置窗补一帧。
        ConsoleWindow.RequestRerender();
    }

    /// <summary>
    /// 挂起 / 恢复某插件的全部实况窗，返回需要通知的窗口对象。
    /// 退出动画中的窗已经没有插件对象，只标记不通知；已在目标状态的不重复处理。
    /// </summary>
    /// <param name="instant">
    /// true = 状态瞬切，不播任何动画（切换显示模式时用，用户定稿 U9）：
    /// 气泡要么直接消失、要么直接就位，避免「切模式时气泡又生又灭地呼吸一次」。
    /// </param>
    internal static List<ILiveWindow> SetOwnerSuppressed(string ownerId, bool suppressed, out bool deferredNotify, out bool deferredResume, bool instant = false)
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
                    e.ExitFromX = e.X;      // 吸入曲线从这个位置起算
                    e.Hovered = false;
                    // 正在显示这个气泡的详情面板要跟着收起（走正常收起动画，而不是下一帧被 Drop）。
                    if (_panel != null && !_panel.Closing
                        && string.Equals(_panel.EntryId, e.Id, StringComparison.OrdinalIgnoreCase))
                        ClosePanel();

                    if (instant)
                    {
                        // 切显示模式（U9）：直接落到「已收回」，不播动画；通知照发（立即）。
                        e.PendingEnableNotify = false;
                        e.SuppressElapsed = SuppressSeconds;
                        e.Scale = 0f;
                        e.Alpha = 0f;
                        if (e.Window != null) windows.Add(e.Window);
                        continue;
                    }

                    // 吸回场景（岛有东西）：OnHostEnabledChanged(false) 延迟到吸完再发 ——
                    // 插件内容回岛导致的岛体变形要等气泡吸完才开始（先吸，再变形）。
                    if (e.SuckBack) deferredNotify = true;
                    e.PendingEnableNotify = e.SuckBack;
                    // 和主动注销一模一样的「往右滑回岛体」吸附动画；播完由 Tick 留在列表里不绘制，
                    // 重新启用时再播一遍生成动画 —— 开关的手感和插件自己注销 / 注册完全一致。
                    e.SuppressElapsed = 0d;
                }
                else
                {
                    if (instant)
                    {
                        // 切显示模式（U9）：直接落到「已在屏上」，不播出生动画，位置下一帧直接落位。
                        e.SuppressElapsed = 0d;
                        e.SpawnElapsed = SpawnSeconds;
                        e.SpawnProgress = 1f;
                        e.SpawnAnchorLocked = false;
                        e.PendingEnableNotify = false;
                        e.PendingResumeNotify = false;
                        e.Scale = 1f;
                        e.Alpha = 255f;
                        e.SnapPosition = true;
                        e.DisplayProgress = e.TargetProgress;
                        e.AppearanceSeq = ++_appearanceSeq;
                        if (e.Window != null) windows.Add(e.Window);
                        continue;
                    }

                    // 重新启用：与「插件重新注册」完全同一套「岛体挤出水滴」的生成动画。
                    e.SuppressElapsed = 0d;
                    e.SpawnElapsed = 0d;
                    e.SpawnAnchorLocked = false;   // 新一次出生：起点重新锁一次
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
                    // ⚠️ 只对「媒体」这一个 owner 请求延迟出生：这套编排真跑起来只会「压媒体 + 提前激活媒体气泡」
                    // （见 MediaLiveWindowController.Tick）。以前任意插件恢复都会请求，媒体没在播时留下一个
                    // 悬空标志，等下次静默启用媒体时会误走编排 B，把岛上其他内容一起缩掉。
                    if (string.Equals(e.Owner, MediaLiveWindowController.Owner, StringComparison.OrdinalIgnoreCase)
                        && !SilentMode.HasIslandContentBesides(bubbleOwners))
                        MediaLiveWindowController.RequestDelayedSpawn();
                    e.DisplayProgress = e.TargetProgress;
                    // 出生起点与「插件自己注册」完全同口径：岛缘左侧完整位（不压岛体）。
                    // 以前这里是「岛体左缘内侧」（islandLeft + 2% 直径），恢复出来的气泡会从岛体里挤出来，
                    // 和普通模式那条「岛缘挤出」的出生动画对不上（用户定稿：生成动画与普通模式一致）。
                    e.SpawnFromX = _lastIslandLeft - IslandGap - BubbleDiameter * 0.5f;
                    e.X = e.SpawnFromX;
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

    /// <summary>
    /// 展开中的浮层面板需要的横向空间（面板宽 + 左右各 8px 贴边余量）。
    /// 窗口宽必须 ≥ 它，否则面板会被窗口边界裁掉（窗口外不渲染）。
    /// </summary>
    public static float RequiredWindowWidth
    {
        get
        {
            lock (_lock)
            {
                var p = _panel;
                return p == null ? 0f : p.Width + 16f;
            }
        }
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
        bool isPlugin = !ownerId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase);
        bool addedOwner = false;
        bool ownerDisabled = false;
        bool displayAllowed = true;
        if (isPlugin)
        {
            EnsureOwnerSettingsLoaded();
            lock (_ownerGate)
            {
                addedOwner = _knownOwners.Add(ownerId);
                if (addedOwner) SaveOwnerSettingsLocked();
                ownerDisabled = _disabledOwners.Contains(ownerId);
            }

            // 显示设置 / 静默清单里没勾选的插件：气泡注册即挂起（内容本来就不显示，
            // 气泡不能单独冒出来）。启动即静默、或勾选状态是启动前存下来的，
            // 这两种情况插件加载时都还没人同步，只能在这里兜住。
            if (!ownerDisabled) displayAllowed = IsOwnerAllowedByDisplay(ownerId);
            if (!displayAllowed)
                Logger.Info($"[实况窗] 插件 {ownerId} 未在{(Renderer.SilentActive ? "静默模式" : "显示")}设置中启用，气泡不展示");
        }

        if (addedOwner) ConsoleWindow.RequestRerender();

        Entry entry;
        lock (_lock)
        {
            if (isPlugin && _liveWindowPluginOwners.Add(ownerId))
                UpdateLiveWindowUsageLocked();
            if (ownerDisabled)
            {
                Logger.Info($"[实况窗] 插件 {ownerId} 已禁用实况窗，本次注册忽略（内容保留在主岛）");
                return null;
            }

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
                // 重新启用后插件重注册：解除挂起；显示设置里没勾选的话仍是挂起态
                //（SuppressElapsed 直接置满 = 收起动画已播完，不重播一遍）。
                existing.Suppressed = !displayAllowed;
                existing.SuppressElapsed = displayAllowed ? 0d : SuppressSeconds;
                existing.DisplayProgress = 0f;
                existing.LastPercentKey = "";
                return new Handle(existing, window);
            }

            int attached = AttachedCountLocked();
            // 每次出生都是「岛体挤出一颗水滴」：起点固定在岛体左缘，再滑到自己的槽位。
            // 岛不可见（气泡组居中）时改从岛体原区域中心起算，别让出生动画横穿半屏。
            // 判据与 Tick / SetOwnerSuppressed 统一「半程」（用户定稿 U8A）。
            bool regIslandVisible = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.5f
                                    && Renderer.FullHideAlpha >= 0.5f;
            float spawnFrom = regIslandVisible
                ? _lastIslandLeft - IslandGap - BubbleDiameter * 0.5f   // 岛缘左侧完整位：出生不压岛体（裁切已移除，避免重叠鼓包）
                : _lastIslandLeft + Renderer.STANDBY_WIDTH / 2f;

            entry = new Entry
            {
                Owner = ownerId,
                Id = id,
                Window = window,
                X = spawnFrom,
                SpawnFromX = spawnFrom,
                SpawnElapsed = 0d,
                // 不展示时直接落成「挂起且收起动画已播完」：既不画气泡，也不播一遍假的收起动画。
                Suppressed = !displayAllowed,
                SuppressElapsed = displayAllowed ? 0d : SuppressSeconds
            };
            _entries.Add(entry);
            UpdateLiveWindowUsageLocked();
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
            _liveWindowPluginOwners.Remove(ownerId);
            foreach (var e in _entries)
            {
                if (e.Owner != ownerId || e.Window == null) continue;
                var window = e.Window;
                DetachLocked(e);
                (removed ??= new List<ILiveWindow>(1)).Add(window);
            }
            UpdateSizingLocked();
            UpdateLiveWindowUsageLocked();
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
        // 与 Tick 里锚点判定同一口径（半程 0.5，用户定稿 U8A）：
        // 岛在屏上 → 吸回岛缘；岛已缩没 → 原地缩小淡出。
        e.ExitSuckBack = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.5f
                         && Renderer.FullHideAlpha >= 0.5f;

        // 面板跟着气泡一起走正常收起动画。以前这里直接 DropPanelLocked()，
        // 气泡明明还要滑 0.44s，面板却「啪」一下消失（静默模式下最扎眼）。
        if (_panel != null && string.Equals(_panel.EntryId, e.Id, StringComparison.OrdinalIgnoreCase))
            ClosePanelLocked();
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
            _liveWindowPluginOwners.Clear();
            UpdateLiveWindowUsageLocked();
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
        lock (_lock) ClosePanelLocked();
    }

    /// <summary>调用方已持有 <see cref="_lock"/>。</summary>
    private static void ClosePanelLocked()
    {
        if (_panel == null || _panel.Closing) return;
        _panel.Closing = true;
        _panel.CloseElapsed = 0d;
        _panelHitActive = false;
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

        /* ─────────── 动画总表（唯一口径 · 2026-11-28 定稿） ───────────
         * 改任何时长 / 曲线之前先看这张表：全站只有这一套数字，各处的注释都指向这里。
         *
         * ★ 时长与曲线
         *   岛体宽高弹簧（普通模式展开 / 收起 / 变形）0.45s（见 NotchWindow.SpringDurationSeconds）
         *   岛体出现 / 消失（静默缩放、toast 弹出）  0.50s SpringEase（放大允许 6% 过冲、缩小钳到 0）
         *   气泡出生（岛缘挤出 / 原地放大）          0.50s EaseOutBack(1.1)
         *   气泡挂起（原地缩小 + 淡出）              0.50s SmoothStep（同帧起跑，没有错峰）
         *   气泡退场（吸回岛缘 / 原地缩小）          0.44s LiquidSuck（45% 起淡出，到岛缘已全透明）
         *   气泡位置（让位 / 贴岛缘 ⇄ 居中）         τ=0.1s 指数跟随 ≈ 0.30s 归位
         *   详情面板 展开 / 收起                     0.34s / 0.30s
         *   侧栏折叠 / 页签选中块滑动                200ms / 200ms TabEase（先快后慢）
         *
         * ★ 阈值（全部「半程」口径）
         *   岛「在屏」= SilentScale ≥ 0.5 且 FullHideAlpha ≥ 0.5。锚点归属、退场方式、
         *   出生参照、OnHostEnabledChanged 的延迟发放，四处共用同一个 0.5，不会跨阈值错拍。
         *
         * ★ 形态语义
         *   出现 = 放大淡入；消失 = 缩小淡出；位置变化 = 指数平滑 —— 全程无瞬移。
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
         *    用户定稿 U1A：岛已缩没（气泡组居中）时的「插件注销」也走原地缩小 —— 没有可回去的参照边，
         *    不能往一片空白的岛缘吸（见 Entry.ExitSuckBack）。
         * ⑥ toast 出入（用户定稿 U5）：岛走「中心放大 / 缩回中心」，0.5s 弹簧，与静默出现 / 消失同一组参数
         *    （不再瞬间满尺寸）；气泡锚点仍在半程切换，前半程贴原锚点、后半程 0.3s 平滑移向新锚点。
         * ⑦ 出生起点三分支（用户定稿 U2）：
         *    a. 岛可见 + 已有气泡 → 与普通模式一致：**从岛缘挤出**再滑到最左（旧气泡不让位）；
         *    b. 岛缩没（居中组）+ 已有气泡 → 新气泡直接在**最左目标槽位原地放大**，已有气泡平滑右移半格；
         *    c. 岛缩没且一颗气泡都没有 → 直接**居中原地放大**。
         *    （起点在出生首帧锁一次，见 Entry.SpawnAnchorLocked，避免岛正好弹出时换参照跳一段。）
         * 锚点归属：静默下岛「该出现/已出现」（ShouldShowIsland / 压制中且岛在屏）→ 贴岛缘；
         * 岛缩没（无内容）→ 居中。排布：新气泡一律出生在整组最左边，已有气泡不让位。
         * ────────────────────────────────────────────────────────────────*/

        // 岛是否在屏上有可见形态：静默缩没 / 整层淡出都算「没了」——
        // 这时气泡不再贴岛体左缘，整组在岛体原区域中心居中（岛没了没有参照边，贴边看着像悬在半空）。
        // 锚点切换阈值统一取「半程」（SilentScale / FullHideAlpha ≥ 0.5，用户定稿 U8A）：
        // 与「启用 / 禁用通知延迟到动画后半程」(SetOwnerSuppressed) 同一口径，
        // 不再出现「气泡已贴岛缘但插件还没收到通知」这类跨阈值错拍。
        // 不锁压制态（SuppressMediaInIsland）：媒体退场时岛先缩没、播完才弹回，
        // 若压制锁岛缘、播完回落居中、岛弹回又切岛缘 —— 气泡会「移到一半又回来又过去」（用户实测）。
        bool islandVisible = (Renderer.SilentActive ? Renderer.SilentScale : 1f) >= 0.5f
                             && Renderer.FullHideAlpha >= 0.5f;

        // 岛可见性跳变沿（双向）：气泡组**平滑**移动到新锚点（贴岛缘 ⇄ 居中），不瞬移（用户定稿）。
        // 指数跟随 τ=0.1s（≈0.3s 归位，全站位移统一这个节奏），长距离也是温和滑过去。

        var deferredList = new List<ILiveWindow>(2);
        var resumeList = new List<ILiveWindow>(2);
        var layoutOrder = new List<Entry>(_entries.Count);
        lock (_lock)
        {
            _lastIslandLeft = islandLeft;
            float mouseX = _mouseX, mouseY = _mouseY;

            int attached = AttachedCountLocked();
            // 槽位口径（用户定稿 U3/U4）：正在播「收回 / 溢出退场」动画的气泡仍然占着槽位，
            // 动画播完（0.5s）才释放 —— 其余气泡要等这 0.5s 过去才补位，两段动画不再叠在一起。
            int totalSlots = 0;
            for (int i = 0; i < _entries.Count; i++)
                if (OccupiesSlot(_entries[i])) totalSlots++;
            int hiddenCount = Math.Max(0, totalSlots - MaxItems);
            int slotIndex = 0;
            int visibleSlots = Math.Min(totalSlots, MaxItems);
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
                    // 插件级开关关闭 / 静默勾选撤下 / 显示清单撤勾选。
                    // 用户定稿 U3：**有主岛就走和普通模式一样的右移吸回**（LiquidSuck 到岛缘，
                    // 45% 后开始淡出）；没有主岛（气泡组居中、岛已缩没）就原地向中间缩小隐藏。
                    // 这段动画期间它仍占着槽位（slotIndex++），其余气泡 0.5s 后才补位。
                    if (e.Window != null)
                    {
                        e.SuppressElapsed = Math.Min(SuppressSeconds, e.SuppressElapsed + dt);
                        float t = (float)Math.Clamp(e.SuppressElapsed / SuppressSeconds, 0d, 1f);
                        if (e.SuckBack)
                        {
                            e.X = e.ExitFromX + (edgeX - e.ExitFromX) * LiquidSuck(t);
                            e.TargetX = edgeX;
                            e.Scale = 1f;
                            e.Alpha = 255f * (1f - SmoothStep01((t - 0.45f) / 0.45f));
                        }
                        else
                        {
                            float k = SmoothStep01(t);
                            e.X = e.ExitFromX;
                            e.TargetX = e.ExitFromX;
                            e.Scale = MathF.Max(0f, 1f - k);
                            e.Alpha = 255f * (1f - k);
                        }
                        // 吸回/缩小动画播完：补发延迟的禁用通知，插件内容此刻才回岛 → 岛体开始变形。
                        if (t >= 1f && e.PendingEnableNotify && e.Window != null)
                        {
                            deferredList.Add(e.Window);
                            e.PendingEnableNotify = false;
                        }
                    }
                    else
                    {
                        e.TargetX = e.ExitFromX;
                    }
                    e.Hovered = false;
                    slotIndex++;   // 动画期间继续占位（U3）：别的气泡不提前补位
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
                    if (e.ExitSuckBack)
                    {
                        // 吸附：起步慢、越过一半后越来越快（真水滴是被表面张力「抽」进去的），
                        // 最后 16% 只剩一丝，透明度和半径同时收掉，不会在岛体边上留残影。
                        e.X = e.ExitFromX + (edgeX - e.ExitFromX) * LiquidSuck(t);
                        e.TargetX = edgeX;
                        e.Scale = 1f;
                        // 淡出提前到吸回途中（过半就开始）：到岛缘时已完全透明，
                        // 与岛内内容的出现衔接不留叠影（用户实测「不要闪」）。
                        e.Alpha = 255f * (1f - SmoothStep01((t - 0.45f) / 0.45f));
                    }
                    else
                    {
                        // 岛已缩没（气泡组居中）：没有可回去的参照边 —— 原地缩小淡出（用户定稿 U1A），
                        // 与「挂起」同一套缩小语义，不再往岛缘飘。
                        float k = SmoothStep01(t);
                        e.X = e.ExitFromX;
                        e.TargetX = e.ExitFromX;
                        e.Scale = MathF.Max(0f, 1f - k);
                        e.Alpha = 255f * (1f - k);
                    }
                    e.Hovered = false;
                    slotIndex++;   // 退场动画期间继续占位（U3）
                }
                else
                {
                    int slot = slotIndex++;
                    if (slot < hiddenCount)
                    {
                        // 排不下（超过 MaxItems）：以前直接把位置甩到 -10000 当场消失，
                        // 现在走 0.5s 退场动画（用户定稿 U4）：
                        //   主岛在 → 右滑回岛缘淡出（B）；主岛不在 → 原地缩小淡出（A）。
                        // 普通模式下主岛恒在，所以一律走 B。
                        if (!e.Overflowing)
                        {
                            e.Overflowing = true;
                            e.OverflowElapsed = 0d;
                            e.OverflowFromX = e.X;
                            e.OverflowSuckBack = islandVisible;
                            e.Hovered = false;
                        }
                        e.OverflowElapsed = Math.Min(OverflowSeconds, e.OverflowElapsed + dt);
                        float ot = (float)Math.Clamp(e.OverflowElapsed / OverflowSeconds, 0d, 1d);
                        if (e.OverflowSuckBack)
                        {
                            e.X = e.OverflowFromX + (edgeX - e.OverflowFromX) * LiquidSuck(ot);
                            e.TargetX = edgeX;
                            e.Scale = 1f;
                            e.Alpha = 255f * (1f - SmoothStep01((ot - 0.45f) / 0.45f));
                        }
                        else
                        {
                            float k = SmoothStep01(ot);
                            e.X = e.OverflowFromX;
                            e.TargetX = e.OverflowFromX;
                            e.Scale = MathF.Max(0f, 1f - k);
                            e.Alpha = 255f * (1f - k);
                        }
                        // 播完就不再绘制：不参与命中，也不占鼠标。
                        e.Hidden = e.OverflowElapsed >= OverflowSeconds;
                        continue;
                    }

                    // 又回到可见范围（前面有空位了）：撤销溢出态，重新走一遍出生动画。
                    if (e.Overflowing)
                    {
                        e.Overflowing = false;
                        e.OverflowElapsed = 0d;
                        e.Hidden = false;
                        e.Scale = 1f;
                        e.Alpha = 255f;
                        e.SpawnElapsed = 0d;
                        e.SpawnAnchorLocked = false;
                    }

                    int drawnSlot = slot - hiddenCount;
                    // 排布方向（用户定稿）：**新气泡一律出生在整组最左边**，已有气泡位置不变、不让位。
                    // drawnSlot 0 = 最早注册（贴岛缘），越大越新 → 越靠左。
                    e.TargetX = anchorRight - diameter / 2f
                                - drawnSlot * (diameter + BubbleGap);

                    // 切显示模式（U9）：状态瞬切，位置也直接落位，不再滑 0.3s。
                    if (e.SnapPosition)
                    {
                        e.SnapPosition = false;
                        e.SpawnElapsed = SpawnSeconds;
                        e.SpawnProgress = 1f;
                        e.SpawnAnchorLocked = false;
                        e.X = e.TargetX;
                        e.Alpha = 255f;
                        e.Scale = 1f;
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
                        // 起点只在这一帧锁定一次：岛正好在放大时（scale 越过 0.5）不会中途换参照跳一段。
                        if (!e.SpawnAnchorLocked)
                        {
                            e.SpawnAnchorLocked = true;
                            e.SpawnAnchorX = islandVisible ? e.SpawnFromX : e.TargetX;
                        }
                        float from = e.SpawnAnchorX;
                        e.X = from + (e.TargetX - from) * EaseOutBack(e.SpawnProgress, 1.1f);
                        e.Alpha = 255f * SmoothStep01(e.SpawnProgress / 0.6f);
                        e.Scale = 1f;
                    }
                    else
                    {
                        // 稳态：指数平滑跟随槽位与锚点（邻居让位 / 居中 ⇄ 贴岛缘），不瞬移。
                        // 时间常数 τ=0.1s → 约 0.3s 归位（用户定稿：所有位移统一 0.3s）。
                        float k = 1f - MathF.Exp(-10f * dt);
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

            UpdateLiveWindowUsageLocked();
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

        // 面板的几何锚点有三种来源：气泡正常在屏 / 被插件级开关挂起 / 正在播退场动画。
        // 三种都要能当锚点，面板才走得了正常收起动画（直接 Drop 就是「啪一下消失」）。
        var entry = FindAttachedLocked(panel.EntryId)
                    ?? FindSuppressedLocked(panel.EntryId)
                    ?? FindExitingLocked(panel.EntryId);

        if (entry != null)
            panel.AnchorX = entry.X;
        else if (!panel.Closing)
            ClosePanelLocked();   // 气泡已被移除（退场播完）：用缓存锚点把面板收完

        // 声明了「尺寸随内容变」的页面每帧重测（媒体面板有没有时间轴会换高度）：
        // 浮层要跟主岛展开态保持同一尺寸，不能只在展开那一刻量一次。
        if (!panel.Closing)
            RemeasurePanelIfNeededLocked(panel);

        float bubbleTop = MathF.Max(2f, islandCenterY - diameter / 2f);
        float bubbleBottom = bubbleTop + diameter;
        // 可见区间 = 屏幕可见范围 ∩ 窗口内容范围：
        // 「窗口比屏幕窄」时 _viewLeft 是负数（屏幕可见左缘落在窗口左侧之外），
        // 直接拿 _viewLeft + 8 当下界会把面板摆到窗口外的负坐标 —— 窗口外不渲染，
        // 宽详情面板的左缘就被窗口边界切掉。
        float visLeft = MathF.Max(0f, _viewLeft);
        float visRight = MathF.Min(Renderer.WINDOW_WIDTH, _viewRight);
        float anchorX = panel.AnchorX;
        float left = Math.Clamp(anchorX - panel.Width / 2f,
            visLeft + 8f, MathF.Max(visLeft + 8f, visRight - panel.Width - 8f));
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

        panel.Source = new SKRect(anchorX - diameter / 2f, bubbleTop, anchorX + diameter / 2f, bubbleBottom);
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
            if (e.Suppressed && !e.Exiting && e.SuppressElapsed >= SuppressSeconds) continue;
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
            // 岛已缩没时的退场不发布「吞入」态：渲染器一旦看到 Exiting，就会画一条指向岛缘的
            // 液体颈、并按岛缘位置把气泡右半裁掉 —— 岛都不在屏上，那条颈指向一片空白，
            // 裁切还会把气泡整颗吃掉。原地缩小只靠 Scale / Alpha 就够了（用户定稿 U1A）。
            bool inPlace = (e.Exiting && !e.ExitSuckBack)
                        || (e.Suppressed && !e.SuckBack)
                        || (e.Overflowing && !e.OverflowSuckBack);
            bool swallowing = !inPlace && (e.Exiting
                || (e.Suppressed && e.SuppressElapsed < SuppressSeconds)
                || (e.Overflowing && e.OverflowElapsed < OverflowSeconds));
            item.ExitProgress = !swallowing ? 0f
                : e.Exiting ? (float)Math.Clamp(e.ExitElapsed / ExitSeconds, 0.0, 1.0)
                : e.Overflowing ? (float)Math.Clamp(e.OverflowElapsed / OverflowSeconds, 0.0, 1.0)
                : (float)Math.Clamp(e.SuppressElapsed / SuppressSeconds, 0.0, 1.0);
            item.Scale = e.Scale;
            item.Exiting = swallowing;
            // 原地缩小期间不做出生形变（否则会出现「先涨回原尺寸再缩」的抖动）。
            if (inPlace) item.SpawnProgress = 1f;
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

    /// <summary>
    /// 这一帧是否仍占着排布槽位（用户定稿 U3）：正在播「收回 / 溢出退场」的气泡先站住 0.5s，
    /// 其余气泡等它播完再补位，两段位移动画不叠在一起。
    /// </summary>
    private static bool OccupiesSlot(Entry e)
    {
        if (e.Exiting) return e.ExitElapsed < ExitSeconds;
        if (e.Suppressed) return e.SuppressElapsed < SuppressSeconds;
        // 溢出淡出完成也不放槽位：放回去会立刻又被判定溢出，气泡来回闪。
        return true;
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

    /// <summary>
    /// 正在播退场动画的窗（插件已注销、气泡还在往岛缘吸）—— 同样要给面板当锚点，
    /// 否则「气泡滑回 + 面板同时消失」这条路上面板还是会被直接丢掉。
    /// </summary>
    private static Entry? FindExitingLocked(string id)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Exiting && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
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
