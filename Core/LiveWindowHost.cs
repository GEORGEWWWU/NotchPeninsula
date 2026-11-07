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

    public bool Completed;

    public SKColor ProgressColor;

    public string Status;
    public string Title;
    public string PercentText;

    public bool Hovered;

    /// <summary>生成动画进度 0..1，1 = 结束（细胞分裂特效用）。</summary>
    public float SpawnProgress;

    /// <summary>两圆分裂进度 0..1（1 = 没有分裂动画）。母气泡与新气泡同时缩放到 R/√2 再长回来。</summary>
    public float SplitProgress;

    /// <summary>分裂来源中心 x（画布坐标）。</summary>
    public float SpawnFromX;

    /// <summary>「移回宿主」的终点 x：岛体左缘（画布坐标）。退出动画期间用来画收口桥。</summary>
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

    /// <summary>气泡直径跟随折叠岛高度；极端自定义高度下夹在可读范围内。</summary>
    public static float BubbleDiameter { get; private set; } = 40f;

    private const float MinBubbleDiameter = 28f;
    private const float MaxBubbleDiameter = 84f;

    /// <summary>没显式传 SpinSpeed 时的默认转速（圈/秒）：约 8 秒一圈，慢速不抢视线。</summary>
    private const float DefaultSpinRevPerSecond = 0.12f;

    private const float SpawnSeconds = 0.5f;
    private const float SpawnFadeSeconds = 0.12f;
    private const float SplitSeconds = 0.55f;
    private const float ExitSeconds = 0.5f;
    private const float PanelOpenSeconds = 0.34f;
    private const float PanelCloseSeconds = 0.30f;

    // 与岛体共用同一套弹簧曲线，动效才不会像两个软件拼的。
    private const double SpringFrequency = 2.65;
    private const double SpringDecay = 10.8;

    private static readonly SKColor DefaultProgressColor = new(0x32, 0xD7, 0x4B);

    private sealed class Entry
    {
        public string Owner = "";
        public string Id = "";
        public ILiveWindow? Window;

        public bool Exiting;
        public double ExitElapsed;

        public double SpawnElapsed;
        public double SplitElapsed = double.MaxValue;

        public float X;
        public float TargetX;
        public float SpawnFromX;

        public float TargetProgress;
        public float DisplayProgress;
        public float SpinAngle;

        public string Status = "";
        public string Title = "";
        public string PercentText = "";
        public string LastPercentKey = "";

        public SKColor ProgressColor = DefaultProgressColor;
        public bool Completed;
        public bool Hovered;
        public bool Hidden;
        public bool Broken;

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
        lock (_ownerGate)
        {
            if (enabled) _disabledOwners.Remove(ownerId);
            else _disabledOwners.Add(ownerId);
            SaveOwnerSettingsLocked();
        }
        Logger.Info($"[实况窗] 插件 {ownerId} 的实况窗已{(enabled ? "启用" : "禁用")}（禁用时内容只允许留在主岛）");
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
        }

        Entry entry;
        lock (_lock)
        {
            // 同 owner+Id 重复注册 = 原地替换（不重播分裂动画，插件想更新内容不必反复注销重注册）。
            var existing = _entries.Find(e =>
                !e.Exiting && e.Window != null && e.Owner == ownerId
                && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Window = window;
                existing.Broken = false;
                existing.Completed = false;
                existing.DisplayProgress = 0f;
                existing.LastPercentKey = "";
                return new Handle(existing, window);
            }

            int attached = AttachedCountLocked();
            float spawnFrom = attached > 0
                ? _lastIslandLeft - IslandGap - BubbleDiameter / 2f
                : _lastIslandLeft - IslandGap;

            // 母气泡：分裂动画要让「原有那个圆」一起参与（两个圆同时缩到 R/√2 再分开长大）。
            Entry? parent = null;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Window != null && !_entries[i].Exiting) { parent = _entries[i]; break; }
            }

            entry = new Entry
            {
                Owner = ownerId,
                Id = id,
                Window = window,
                X = spawnFrom,
                SpawnFromX = spawnFrom,
                SpawnElapsed = 0d,
                SplitElapsed = parent != null ? 0d : SplitSeconds   // 第一个气泡没有母圆可分裂，跳过缩圈
            };
            _entries.Add(entry);
            if (parent != null) parent.SplitElapsed = 0d;   // 母圆也重播一次分裂
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

    public static bool HitBubble(float x, float y, out string? id)
    {
        id = null;
        lock (_lock)
        {
            for (int i = 0; i < _hitCount; i++)
            {
                if (!HitRects[i].Contains(x, y)) continue;
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
            w = page.MeasureWidth();
            h = page.MeasureHeight();
        }
        catch (Exception ex)
        {
            Logger.Error($"[实况窗] 详情页尺寸测量异常: {id}", ex);
            return;
        }
        if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f)
        {
            Logger.Warn($"[实况窗] 详情页尺寸非法（{w} x {h}），已忽略右键展开");
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

        float islandCenterY = topY + MathF.Max(0f, MathF.Min(currentHeight, Renderer.MEDIA_HEIGHT)) / 2f;
        // 用户要求：气泡直径和宿主（折叠岛）高度一致；极端自定义高度下夹住，别撑爆或小到看不见。
        float diameter = Math.Clamp(Renderer.MEDIA_HEIGHT, MinBubbleDiameter, MaxBubbleDiameter);
        BubbleDiameter = diameter;
        float bubbleTop = MathF.Max(2f, islandCenterY - diameter / 2f);

        lock (_lock)
        {
            _lastIslandLeft = islandLeft;
            float mouseX = _mouseX, mouseY = _mouseY;

            int attached = AttachedCountLocked();
            int hiddenCount = Math.Max(0, attached - MaxItems);
            int attachedIndex = 0;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];

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

                // 分裂进度：母圆 / 新圆同步走一遍「缩到 R/√2 再长回来」。
                if (e.SplitElapsed < SplitSeconds)
                    e.SplitElapsed = Math.Min(SplitSeconds, e.SplitElapsed + dt);

                if (e.Exiting)
                {
                    e.ExitElapsed = Math.Min(ExitSeconds + 1d, e.ExitElapsed + dt);
                    float ep = (float)Math.Clamp(e.ExitElapsed / ExitSeconds, 0.0, 1.0);
                    // 「分裂合并回宿主」：气泡滑到岛体左缘，半径按两圆合并的数学缩到 R/√2 再收回。
                    e.TargetX = islandLeft + diameter * 0.02f;
                    e.Scale = 1f;
                    float fade = Math.Clamp((ep - 0.55f) / 0.45f, 0f, 1f);
                    e.Alpha = 255f * (1f - fade * fade);
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
                        e.TargetX = islandLeft - IslandGap - diameter / 2f
                                    - (Math.Min(attached, MaxItems) - 1 - drawnSlot) * (diameter + BubbleGap);
                    }

                    e.SpawnElapsed = Math.Min(SpawnSeconds, e.SpawnElapsed + dt);
                    e.SpawnProgress = (float)Math.Clamp(e.SpawnElapsed / SpawnSeconds, 0, 1);
                    e.Scale = 1f;   // 不再做「弹一下」的缩放，改由两圆分裂的半径曲线负责手感
                    float fade = (float)Math.Clamp(e.SpawnElapsed / SpawnFadeSeconds, 0, 1);
                    e.Alpha = 255f * fade;

                    e.Hovered = mouseX >= 0f && MathF.Abs(mouseX - e.X) <= diameter / 2f
                                && mouseY >= bubbleTop && mouseY <= bubbleTop + diameter;
                }

                // 位置指数平滑：注册瞬间与旧气泡重叠，几帧内散开 —— 这就是「细胞分裂」的观感来源。
                float k = 1f - MathF.Exp(-12f * dt);
                e.X += (e.TargetX - e.X) * k;

                // 「不出屏」：窗口比屏幕宽，content x=0 可能已在屏幕外；装不下的最旧气泡不画、不参与命中。
                e.Hidden = e.TargetX < -1000f
                           || (!e.Exiting && e.TargetX - diameter / 2f < _viewLeft + ScreenMargin);
            }

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].ExitElapsed > ExitSeconds) _entries.RemoveAt(i);
            }

            TickPanelLocked(topY, islandLeft, islandCenterY, diameter, dt);
            PublishFrameLocked(bubbleTop, islandLeft, diameter);
            UpdateSizingLocked();
        }
    }

    private static void TickPanelLocked(float topY, float islandLeft, float islandCenterY, float diameter, float dt)
    {
        var panel = _panel;
        if (panel == null) return;

        var entry = FindAttachedLocked(panel.EntryId);
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
    /// 页面声明「尺寸随内容变」时每帧重测一次宽高（普通插件详情页 reszie=false，仍只在展开时测一次）。
    /// 目前只有媒体详情页用：时间轴晚到 / 消失时，浮层高度要在 130 / 158 之间跟主岛展开面板同步。
    /// 调用方已持有 <see cref="_lock"/>。
    /// </summary>
    private static void RemeasurePanelIfNeededLocked(PanelState panel)
    {
        var page = panel.Page;
        if (page == null) return;
        try
        {
            if (!page.ResizeWithContent) return;

            float w = page.MeasureWidth();
            float h = page.MeasureHeight();
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return;

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
            if (frame.Count >= MaxItems) break;

            ref var item = ref frame.Items[frame.Count];
            item.Window = e.Window;
            item.Id = e.Id;
            item.Rect = new SKRect(e.X - diameter / 2f, bubbleTop, e.X + diameter / 2f, bubbleTop + diameter);
            item.Alpha = (byte)Math.Clamp((int)e.Alpha, 0, 255);
            item.Progress = e.DisplayProgress;
            item.Indeterminate = e.DisplayProgress < 0f;
            item.SpinAngle = e.SpinAngle;
            item.Completed = e.Completed;
            item.ProgressColor = e.ProgressColor;
            item.Status = e.Status;
            item.Title = e.Title;
            item.PercentText = e.PercentText;
            item.Hovered = e.Hovered;
            item.SpawnProgress = e.SpawnProgress;
            item.SplitProgress = (float)Math.Clamp(e.SplitElapsed / SplitSeconds, 0.0, 1.0);
            item.SpawnFromX = e.SpawnFromX;
            item.MergeTargetX = islandLeft + diameter * 0.02f;   // 「移回宿主」的终点：岛体左缘
            item.ExitProgress = e.Exiting ? (float)Math.Clamp(e.ExitElapsed / ExitSeconds, 0.0, 1.0) : 0f;
            item.Scale = e.Scale;
            item.Exiting = e.Exiting;
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
            if (item.Alpha < 16 || item.Id == null) continue;
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

        if (visual.SpinInner)
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
            if (_entries[i].Window != null && !_entries[i].Exiting) n++;
        return n;
    }

    private static Entry? FindAttachedLocked(string id)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Window != null && !e.Exiting && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
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

    private static double SpringEase(double elapsedSeconds)
        => 1.0 - Math.Cos(SpringFrequency * elapsedSeconds * 2.0 * Math.PI) * Math.Exp(-SpringDecay * elapsedSeconds);

    private static float EaseInOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }
}
