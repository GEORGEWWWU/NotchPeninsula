using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ---- 插件组件渲染接线 ----
        // 设计目标：稳态 60FPS 零 GC 分配。
        private static Plugins.IWidget[]? _pluginWidgets;

        private static int _pluginWidgetsVersion = -1;

        private static float[]? _pluginWidths;

        private static bool[]? _pluginBroken;

        private static bool[]? _pluginDrawn;

        private static float _compositeMediaRight = -1f;

        private static readonly List<Plugins.WidgetLayout.Slot> _pluginSlots = new(8);

        private static float _pluginRowBudget = float.PositiveInfinity;

        private static bool[]? _pluginVisible;

        private static float _pluginRowReserve;

        // ---- AutoWidth 动态宽度：量化 + 迟滞 ----
        // 只对声明了 WidthVaries 的组件生效；其余组件测出多少就是多少，一个字节都不加工。
        private static float[]? _pluginPendingWidth;

        private static long[]? _pluginPendingSince;

        // 换字体时字宽全变，宿主自己重测（以前把这件事推给插件订阅 FontConfig.Changed，漏订一个就永远错）。
        private static string? _pluginFontKey;

        private static bool _pluginFontCustom;

        // 宽度栅格：只用来吸收字宽的浮点抖动（15.3 / 15.7 这类），不能把窄组件凑大 ——
        // 以前是 4px，26 宽的图标会被凑成 28，视觉上就是「图标两侧各多一条白边」。
        private const float WIDTH_BUCKET = 1f;

        private const long WIDTH_SHRINK_HOLD_MS = 600;

        private const float WIDTH_GROW_EPS = 0.5f;

        private const float WIDTH_SHRINK_EPS = 6f;

        private const float PLUGIN_GAP = 16f;

        private const float MIN_NATIVE_AREA = 80f;

        public static void SetPluginRowBudget(float availableWidth)
        {
            lock (_pluginSnapshotLock)
            {
                _pluginRowBudget = float.IsNaN(availableWidth) || availableWidth < 0f ? 0f : availableWidth;
            }
            RefreshPluginWidgets(); // 版本变化时内部会重算一次；没变化则由下面这行重算
            lock (_pluginSnapshotLock) RecomputePluginVisibility();
        }

        public static void SetPluginRowVisible(bool visible)
            => SetPluginRowBudget(visible ? float.PositiveInfinity : 0f);

        private static void RecomputePluginVisibility()
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null || visible == null) return;
            if (visible.Length != widgets.Length || widths.Length != widgets.Length || broken.Length != widgets.Length) return;

            float budget = _pluginRowBudget;
            float used = 0f;

            for (int i = 0; i < widgets.Length; i++)
            {
                bool ok = !broken[i] && widths[i] > 0f && used + PLUGIN_GAP + widths[i] <= budget;
                if (ok) used += PLUGIN_GAP + widths[i];
                if (visible[i] != ok)
                {
                    visible[i] = ok; // 只在变化时写：bool 写入原子，渲染线程不会看到中间态
                    // 只在翻转时留痕，不会刷屏 —— 「一会显示一会不显示」这类问题靠这条日志定位。
                    Logger.Debug($"[插件行] {widgets[i].Id} {(ok ? "放行" : "挡掉")} w={widths[i]:0.##} used={used:0.##} budget={budget:0.##}");
                }
            }

            if (_pluginRowReserve != used) _pluginRowReserve = used;
        }

        private static readonly object _pluginSlotLock = new();

        private static readonly object _pluginSnapshotLock = new();

        private static float _pluginMouseX = -1f;

        private static float _pluginMouseY = -1f;

        public static void UpdatePluginMouse(float x, float y)
        {
            _pluginMouseX = x;
            _pluginMouseY = y;
        }

        public static float GetPluginRowReserve()
        {
            if (!RefreshPluginWidgets()) return 0f;
            return _pluginRowReserve;
        }

        public static float GetPluginRowBudget() => _pluginRowBudget;

        public static float GetPluginRowRemaining(string? pluginId)
        {
            if (pluginId == null) return _pluginRowBudget;

            lock (_pluginSnapshotLock)
            {
                var widgets = _pluginWidgets;
                var widths = _pluginWidths;
                var broken = _pluginBroken;
                var visible = _pluginVisible;
                if (widgets == null || widths == null || broken == null) return _pluginRowBudget;
                if (widths.Length != widgets.Length || broken.Length != widgets.Length) return _pluginRowBudget;

                float budget = _pluginRowBudget;
                if (float.IsInfinity(budget)) return budget;

                var host = Plugins.PluginManager.Instance.Host;
                float used = 0f;

                for (int i = 0; i < widgets.Length; i++)
                {
                    if (host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                        && string.Equals(pid, pluginId, StringComparison.OrdinalIgnoreCase))
                        return Math.Max(0f, budget - used);

                    if (broken[i] || widths[i] <= 0f) continue;
                    if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽
                    used += PLUGIN_GAP + widths[i];
                }

                return Math.Max(0f, budget - used);
            }
        }

        /// <summary>
        /// AutoWidth 组件的额度：<c>budget − 排在我前面的占用 − 我自己的槽间距</c>。
        ///
        /// <para>
        /// 与 <see cref="GetPluginRowRemaining"/> 的差别有两处：按 <b>widgetId</b> 精确定位（一个插件可能注册多个组件），
        /// 并且再扣掉一个 <see cref="PLUGIN_GAP"/> —— 那个间距是「我这一格与前一格之间」的，本来就该算在我头上，
        /// 插件拿去做宽度判断时不必再自己减 16。
        /// </para>
        ///
        /// <para>
        /// 预算本身是正无穷（插件行还没被安排）时原样返回正无穷，让调用方自己决定要不要量化。
        /// 组件不在快照里、或已经被预算挡掉时返回 0。
        /// </para>
        /// </summary>
        public static float GetPluginRowRemainingForWidget(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId)) return 0f;

            lock (_pluginSnapshotLock)
            {
                var widgets = _pluginWidgets;
                var widths = _pluginWidths;
                var broken = _pluginBroken;
                var visible = _pluginVisible;
                if (widgets == null || widths == null || broken == null) return 0f;

                float budget = _pluginRowBudget;
                if (float.IsPositiveInfinity(budget)) return float.PositiveInfinity;

                float used = 0f;
                int n = Math.Min(widgets.Length, widths.Length);
                for (int i = 0; i < n; i++)
                {
                    if (string.Equals(widgets[i].Id, widgetId, StringComparison.Ordinal))
                        return Math.Max(0f, budget - used - PLUGIN_GAP);

                    if (broken[i] || widths[i] <= 0f) continue;
                    if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽
                    used += PLUGIN_GAP + widths[i];
                }
                return 0f;
            }
        }

        public static bool HitPluginZone(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var r = _pluginSlots[i].Rect;
                    if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom) return true;
                }
            }
            return false;
        }

        public static bool DispatchPluginLeftClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;

                    Plugins.WidgetHit hit;
                    try { hit = slot.Widget.HitTest(x - r.Left, y - r.Top, r); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件命中检测异常", ex); continue; }
                    if (!hit.IsHit) continue;

                    try { slot.Widget.OnLeftClick(hit.Action, x - r.Left, y - r.Top); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件点击回调异常", ex); }
                    return true;
                }
            }
            return false;
        }

        public static string? DispatchPluginRightClick(float x, float y)
        {
            string? detailWidgetId = null;
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;
                    try { slot.Widget.OnRightClick(); }
                    catch (Exception ex) { Logger.Error("[Renderer] 插件组件右键回调异常", ex); }

                    if (detailWidgetId == null)
                    {
                        try { if (slot.Widget.DetailPage != null) detailWidgetId = slot.Widget.Id; }
                        catch (Exception ex) { Logger.Error("[Renderer] 读取组件详情页异常", ex); }
                    }
                }
            }
            return detailWidgetId;
        }

        // 两档互相独立，都不注册就完全走宿主原有行为：
        //（那会给每一次普通点击都加半个双击窗口的迟滞）。

        public static bool TryHitRightClickWidget(float x, float y, out string widgetId,
            out float lx, out float ly, out bool acceptsRightClick, out bool acceptsDoubleClick)
        {
            widgetId = "";
            lx = ly = 0f;
            acceptsRightClick = acceptsDoubleClick = false;
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;

                    bool right, dbl;
                    try { right = slot.Widget.AcceptsRightClick; dbl = slot.Widget.AcceptsDoubleClick; }
                    catch (Exception ex) { Logger.Error("[Renderer] 读取组件右键 / 双击标记异常", ex); continue; }
                    if (!right && !dbl) continue;   // 两档都没开：不归本方法管，让调用方走默认行为

                    widgetId = slot.Widget.Id;
                    lx = x - r.Left;
                    ly = y - r.Top;
                    acceptsRightClick = right;
                    acceptsDoubleClick = dbl;
                    return true;
                }
            }
            return false;
        }

        private static Plugins.IWidget? FindWidgetById(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId)) return null;
            lock (_pluginSnapshotLock)
            {
                var widgets = _pluginWidgets;
                if (widgets == null) return null;
                for (int i = 0; i < widgets.Length; i++)
                {
                    if (string.Equals(widgets[i].Id, widgetId, StringComparison.OrdinalIgnoreCase)) return widgets[i];
                }
            }
            return null;
        }

        public static bool DispatchWidgetRightClick(string widgetId)
        {
            var target = FindWidgetById(widgetId);
            if (target == null) return false;

            try
            {
                if (!target.AcceptsRightClick) return false;
                target.OnRightClick();
            }
            catch (Exception ex) { Logger.Error("[Renderer] 组件右键回调异常", ex); return false; }
            return true;
        }

        public static bool DispatchWidgetDoubleClick(bool isRight, string widgetId, float lx, float ly)
        {
            var target = FindWidgetById(widgetId);
            if (target == null) return false;

            try
            {
                if (!target.AcceptsDoubleClick) return false;
                if (isRight) target.OnRightDoubleClick(lx, ly);
                else target.OnLeftDoubleClick(lx, ly);
            }
            catch (Exception ex) { Logger.Error("[Renderer] 组件双击回调异常", ex); return false; }
            return true;
        }

        public static bool DispatchPluginDoubleClick(bool isRight, float x, float y)
        {
            if (!TryHitRightClickWidget(x, y, out var id, out float lx, out float ly, out _, out bool dbl) || !dbl)
                return false;
            return DispatchWidgetDoubleClick(isRight, id, lx, ly);
        }

        public static bool DetailPageAcceptsRightClick
        {
            get
            {
                lock (_pluginSlotLock)
                {
                    var page = _detailPage;
                    if (page == null || _detailBroken) return false;
                    try { return page.AcceptsRightClick; }
                    catch (Exception ex) { Logger.Error("[Renderer] 读取详情页「右键单击透传」标记异常", ex); return false; }
                }
            }
        }

        public static bool DetailPageAcceptsDoubleClick
        {
            get
            {
                lock (_pluginSlotLock)
                {
                    var page = _detailPage;
                    if (page == null || _detailBroken) return false;
                    try { return page.AcceptsDoubleClick; }
                    catch (Exception ex) { Logger.Error("[Renderer] 读取详情页「接收双击」标记异常", ex); return false; }
                }
            }
        }

        public static bool DispatchDetailPageDoubleClick(bool isRight, float x, float y)
        {
            lock (_pluginSlotLock)
            {
                if (!TryDetailLocalLocked(x, y, out var page, out float lx, out float ly)) return false;
                try
                {
                    if (!page!.AcceptsDoubleClick) return false;
                    if (isRight) page.OnRightDoubleClick(lx, ly);
                    else page.OnLeftDoubleClick(lx, ly);
                }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页双击回调异常", ex); return false; }
                return true;
            }
        }

        public static bool DispatchDetailPageRightClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                if (!TryDetailLocalLocked(x, y, out var page, out float lx, out float ly)) return false;
                try
                {
                    if (!page!.AcceptsRightClick) return false;
                    page.OnRightClick(lx, ly);
                }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页右键回调异常", ex); return false; }
                return true;
            }
        }

        public static bool DispatchPassthroughRightDoubleClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page != null && !_detailBroken)
                {
                    var r = _detailHitRect;
                    if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                    {
                        try
                        {
                            if (page.AcceptsRightClick && page.AcceptsDoubleClick)
                            {
                                page.OnRightDoubleClick(x - r.Left, y - r.Top);
                                return true;
                            }
                        }
                        catch (Exception ex) { Logger.Error("[Renderer] 详情页右键双击回调异常", ex); }
                        return false;   // 详情页接管着岛体：不满足条件就是「没人要」，不再往下找组件
                    }
                }
            }

            if (TryHitRightClickWidget(x, y, out var id, out float lx, out float ly, out bool right, out bool dbl)
                && right && dbl)
                return DispatchWidgetDoubleClick(true, id, lx, ly);

            return false;
        }

        public static string? FindCollapsedFileDropWidget(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                for (int i = 0; i < _pluginSlots.Count; i++)
                {
                    var slot = _pluginSlots[i];
                    var r = slot.Rect;
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;

                    try
                    {
                        if (!slot.Widget.AcceptsFileDropWhenCollapsed) continue;
                        if (slot.Widget.DetailPage == null) continue;   // 没详情页就没有「展开」这回事
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[Renderer] 读取组件「收起态可接文件」标记异常", ex);
                        continue;
                    }

                    return slot.Widget.Id;
                }
            }
            return null;
        }

        private static void InvalidatePluginHitAreas()
        {
            lock (_pluginSlotLock)
            {
                _pluginSlots.Clear();
                // 先拷到局部再清：_pluginDrawn 会被 InvalidatePluginSnapshot 置 null，
                // 「判空之后再用」的窗口在这里同样会 NRE。
                var drawn = _pluginDrawn;
                if (drawn != null) Array.Clear(drawn);
            }
        }

        public static void InvalidateDetailHitArea()
        {
            lock (_pluginSlotLock)
            {
                _detailHitPage = null;
                _detailHitRect = default;
            }
        }

        public static Plugins.IDetailPage? ActiveDetailPageOrNull
        {
            get { lock (_pluginSlotLock) return _detailBroken ? null : _detailPage; }
        }

        public static void InvalidatePluginSnapshot()
        {
            // 快照字段与 RefreshPluginWidgets 同一把锁：否则渲染线程刚判完空、还没用上，
            // 这边就把数组置 null —— 卸载插件时渲染线程 NRE 闪退就是这么来的（两把锁互不互斥）。
            // 只在 UI 线程调用（插件卸载/停用路径），不会与渲染线程的死锁。
            lock (_pluginSnapshotLock)
            {
                _pluginWidgets = null;
                _pluginWidths = null;
                _pluginBroken = null;
                _pluginDrawn = null;
                _pluginVisible = null;
                _pluginPendingWidth = null;
                _pluginPendingSince = null;
                _pluginWidgetsVersion = -1;   // 下一帧强制重建快照
                _pluginRowReserve = 0f;
            }

            lock (_pluginSlotLock)
            {
                _pluginSlots.Clear();

                _detailPage = null;
                _detailHitPage = null;
                _detailHitRect = default;
                _detailWidth = 0f;
                _detailHeight = 0f;
                _detailBroken = false;

                _compositeMediaRight = -1f;
            }
        }

        public static float CompositeMediaRight => _compositeMediaRight;

        public static float GetMediaRight(float windowWidth, float currentWidth, bool toastActive)
        {
            if (_compositeMediaRight > 0f) return _compositeMediaRight;
            return (windowWidth + currentWidth) / 2f - (toastActive ? 0f : GetPluginRowReserve());
        }

        // ---- 非组合模式下的插件左右分组 ----

        private static string? GetNativeBuiltinId(bool mediaActive)
        {
            if (mediaActive) return Plugins.BuiltinWidgets.Media;
            if (StandbyDisplayMode == 0) return Plugins.BuiltinWidgets.Clock;
            if (StandbyDisplayMode == 2) return Plugins.BuiltinWidgets.Hardware;
            return null; // 空白待机：没有原生内容，插件行独占整岛（沿用「整行贴右侧」）
        }

        private static int OrderIndexOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            var order = Plugins.PluginManager.Instance.Host.ContentOrder;
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int GetWidgetSide(string widgetId, int nativeOrderIndex)
        {
            if (nativeOrderIndex < 0) return 1;
            var host = Plugins.PluginManager.Instance.Host;
            if (!host.TryGetWidgetPlugin(widgetId, out var pid) || string.IsNullOrEmpty(pid)) return 1;
            int idx = OrderIndexOf(pid);
            return idx >= 0 && idx < nativeOrderIndex ? -1 : 1;
        }

        private static void MeasurePluginSides(int nativeOrderIndex, out float leftWidth, out float rightWidth)
        {
            leftWidth = 0f;
            rightWidth = 0f;

            Plugins.IWidget[]? widgets;
            float[]? widths;
            bool[]? broken;
            bool[]? visible;
            lock (_pluginSnapshotLock)
            {
                widgets = _pluginWidgets;
                widths = _pluginWidths;
                broken = _pluginBroken;
                visible = _pluginVisible;
            }
            if (widgets == null || widths == null || broken == null) return;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return;

            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽，也不绘制
                if (GetWidgetSide(widgets[i].Id, nativeOrderIndex) < 0)
                    leftWidth = leftWidth > 0f ? leftWidth + PLUGIN_GAP + widths[i] : widths[i];
                else
                    rightWidth = rightWidth > 0f ? rightWidth + PLUGIN_GAP + widths[i] : widths[i];
            }
        }

        private static bool RefreshPluginWidgets()
        {
            lock (_pluginSnapshotLock)
            {
                var host = Plugins.PluginManager.Instance.Host;
                int version = host.WidgetsVersion;

                // 换字体 = 字宽全变，必须重测。以前这件事推给插件自己订阅 FontConfig.Changed，
                // 漏订一个就永远停在旧宽度上（文字被裁）—— 宿主管字体，就该宿主管重测。
                string fontKey = FontConfig.CustomFontPath;
                bool fontCustom = FontConfig.HasCustomFont;
                bool fontChanged = !ReferenceEquals(fontKey, _pluginFontKey) || fontCustom != _pluginFontCustom;

                if (_pluginWidgetsVersion == version && _pluginWidths != null && !fontChanged)
                {
                    FlushPendingWidths();   // 变窄的迟滞到期在这里收口，每帧一次 O(n)
                    return _pluginWidgets!.Length > 0;
                }

                _pluginFontKey = fontKey;
                _pluginFontCustom = fontCustom;
                _pluginWidgetsVersion = version;

                var next = host.Widgets as Plugins.IWidget[] ?? Array.Empty<Plugins.IWidget>();
                int n = next.Length;

                // 组件集合没变（只是某个插件改了内容、请求重测）：**原地复用**宽度 / 可见性数组。
                // 以前这里一律 new 一套数组，宽度先全归零再逐个填回 —— 岛宽在那一拍会塌一下再撑开，
                // 插件还会因为宽度短暂为 0 被判「本帧不显示」，肉眼就是「一会收进去一会出来」。
                if (_pluginWidgets != null && _pluginWidths != null && _pluginBroken != null
                    && _pluginWidgets.Length == n && SameWidgetIds(_pluginWidgets, next))
                {
                    for (int i = 0; i < n; i++)
                        CommitWidgetWidth(i, _pluginWidths[i], MeasureWidgetWidth(next[i], i, _pluginWidths[i]));
                    RecomputePluginVisibility();
                    return n > 0;
                }

                var oldWidgets = _pluginWidgets;
                var oldWidths = _pluginWidths;

                _pluginWidgets = next;
                _pluginWidths = n > 0 ? new float[n] : Array.Empty<float>();
                _pluginBroken = n > 0 ? new bool[n] : Array.Empty<bool>();
                _pluginDrawn = n > 0 ? new bool[n] : Array.Empty<bool>();
                _pluginVisible = n > 0 ? new bool[n] : Array.Empty<bool>();
                _pluginPendingWidth = n > 0 ? new float[n] : Array.Empty<float>();
                _pluginPendingSince = n > 0 ? new long[n] : Array.Empty<long>();

                for (int i = 0; i < n; i++)
                {
                    // 必须沿用上一次已提交的宽度当基准：否则每次重测都被当成「首次测量」直接生效，迟滞等于没做。
                    // 它同时兼作 AutoWidth 布局异常时的兜底宽度 —— 一次异常不该让组件凭空消失。
                    float current = FindOldWidth(oldWidgets, oldWidths, _pluginWidgets[i].Id);
                    CommitWidgetWidth(i, current, MeasureWidgetWidth(_pluginWidgets[i], i, current));
                }

                RecomputePluginVisibility();
                return n > 0;
            }
        }

        /// <summary>组件集合是否与旧快照同序同 Id（只比较 Id，不比较实例）。</summary>
        private static bool SameWidgetIds(Plugins.IWidget[] a, Plugins.IWidget[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// 把一次测量结果落到 <c>_pluginWidths</c>。三种组件三种待遇：
        ///
        /// <list type="bullet">
        /// <item>老协议 / AutoWidth 但宽度固定：直接落定，宿主不加工。</item>
        /// <item>AutoWidth 且 <c>WidthVaries</c>：内容变宽立即生效（等一帧就露裁切），
        ///       变窄先进 pending，连续 <see cref="WIDTH_SHRINK_HOLD_MS"/> 都这么窄才收 —— 长句短句来回换时岛体不该跟着抽。</item>
        /// <item>变化幅度在 <see cref="WIDTH_SHRINK_EPS"/> 以内：当抖动，不动。</item>
        /// </list>
        /// </summary>
        private static void CommitWidgetWidth(int index, float current, float raw)
        {
            if (_pluginWidths == null || index < 0 || index >= _pluginWidths.Length) return;

            string id = _pluginWidgets != null && index < _pluginWidgets.Length ? _pluginWidgets[index].Id : "?";
            float before = _pluginWidths[index];

            try
            {
                bool varies = _pluginWidgets != null && index < _pluginWidgets.Length
                    && _pluginWidgets[index] is Plugins.IAutoWidthWidget { WidthVaries: true };

                if (!varies)
                {
                    _pluginWidths[index] = raw;
                    ClearPendingWidth(index);
                    return;
                }

                float next = QuantizeWidth(raw);

                // 0 宽是「本帧不显示」的明确语义，不是「变窄」—— 挂 600ms 迟滞只会让消失晚一拍，
                // 恢复时还要跟残留的 pending 打架，所以直接落定。
                if (!(next > 0f))
                {
                    _pluginWidths[index] = 0f;
                    ClearPendingWidth(index);
                    return;
                }

                if (current <= 0f || next > current + WIDTH_GROW_EPS)
                {
                    _pluginWidths[index] = next;
                    ClearPendingWidth(index);
                    return;
                }

                if (next >= current - WIDTH_SHRINK_EPS)
                {
                    // 抖动带内不动，多出来的几像素当留白。但数组刚重建时下标里是 0，
                    // 这里必须把值落下去，否则组件会带着 0 宽被判「本帧不显示」。
                    if (!(_pluginWidths[index] > 0f)) _pluginWidths[index] = next;
                    ClearPendingWidth(index);
                    return;
                }

                var pending = _pluginPendingWidth;
                var since = _pluginPendingSince;
                if (pending == null || since == null || index >= pending.Length || index >= since.Length) return;

                // 目标宽度变了就重新计时：只有「一直想收窄」才算数。
                if (since[index] == 0 || Math.Abs(pending[index] - next) > 0.5f)
                {
                    pending[index] = next;
                    since[index] = Environment.TickCount64;
                    Logger.Debug($"[插件测宽] {id} 记下待收窄 {current:0.##} → {next:0.##}（{WIDTH_SHRINK_HOLD_MS}ms 后确认）");
                }
            }
            finally
            {
                float after = _pluginWidths[index];
                if (Math.Abs(after - before) > 0.5f)
                    Logger.Debug($"[插件测宽] {id} 提交 {before:0.##} → {after:0.##}（本次测量 {raw:0.##}）");
            }
        }

        /// <summary>收口到期的变窄请求。只在版本未变的那条路径上跑（每帧一次）。</summary>
        private static void FlushPendingWidths()
        {
            var pending = _pluginPendingWidth;
            var since = _pluginPendingSince;
            var widths = _pluginWidths;
            if (pending == null || since == null || widths == null) return;

            long now = Environment.TickCount64;
            bool changed = false;

            int n = Math.Min(since.Length, Math.Min(pending.Length, widths.Length));
            for (int i = 0; i < n; i++)
            {
                if (since[i] == 0) continue;
                if (now - since[i] < WIDTH_SHRINK_HOLD_MS) continue;
                widths[i] = pending[i];
                since[i] = 0;
                pending[i] = 0f;
                changed = true;
                if (_pluginWidgets != null && i < _pluginWidgets.Length)
                    Logger.Debug($"[插件测宽] {_pluginWidgets[i].Id} 迟滞到期，收窄到 {widths[i]:0.##}");
            }

            if (changed) RecomputePluginVisibility();
        }

        private static float QuantizeWidth(float w)
            => w > 0f ? MathF.Ceiling(w / WIDTH_BUCKET) * WIDTH_BUCKET : 0f;

        private static void ClearPendingWidth(int index)
        {
            if (_pluginPendingSince != null && index >= 0 && index < _pluginPendingSince.Length) _pluginPendingSince[index] = 0;
            if (_pluginPendingWidth != null && index >= 0 && index < _pluginPendingWidth.Length) _pluginPendingWidth[index] = 0f;
        }

        private static float FindOldWidth(Plugins.IWidget[]? oldWidgets, float[]? oldWidths, string id)
        {
            if (oldWidgets == null || oldWidths == null) return 0f;
            int n = Math.Min(oldWidgets.Length, oldWidths.Length);
            for (int i = 0; i < n; i++)
                if (string.Equals(oldWidgets[i].Id, id, StringComparison.Ordinal)) return oldWidths[i];
            return 0f;
        }

        /// <summary>
        /// 量一个组件的折叠态槽位宽度。两条路，按接口判定：
        ///
        /// <para>
        /// <b>AutoWidth</b>（<see cref="Plugins.IAutoWidthWidget"/>）：宿主从 <c>Layout</c> 返回的盒子算宽度，
        /// 口径全在 <see cref="Plugins.WidgetKit"/>（向上取整 + 统一留白），插件不再上报宽度。
        /// </para>
        ///
        /// <para>
        /// <b>老协议</b>：原样用插件的 <c>MeasureWidth</c>，<b>不做任何加工</b>。这是兼容性红线 ——
        /// 这里加一个 ceil 或 2px 余量，现存插件的岛体宽度就会集体变化，连带影响预算放行结果。
        /// </para>
        ///
        /// AutoWidth 的 <c>Layout</c> 抛异常时先退回它自己可能还实现的 <c>MeasureWidth</c>，
        /// 最后才熔断：新协议的一次布局 bug 不该让整个组件永久消失。
        /// </summary>
        private static float MeasureWidgetWidth(Plugins.IWidget widget, int index, float fallbackWidth)
        {
            if (widget is Plugins.IAutoWidthWidget auto)
            {
                try
                {
                    float w = Plugins.WidgetKit.Measure(auto.Layout(MEDIA_HEIGHT));
                    // 0 宽 = 宿主这一帧不画它。间歇为 0 就是「一会显示一会不显示」的直接来源，
                    // 所以这里必须留痕（含 null 盒子 / 内容为空两种情形）。
                    if (!(w > 0f))
                        Logger.Debug($"[插件测宽] {widget.Id} 量到 0 宽 → 本帧不显示（Layout 返回 null 或内容为空）");
                    return w;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Renderer] 插件组件 {widget.Id} 的 AutoWidth 布局异常，沿用上次有效宽度", ex);
                    // 新协议插件不实现 MeasureWidth（默认 0），拿它兜底等于把组件变没；
                    // 所以先试它自报的值（万一还实现着），再退回上一次有效宽度。
                    try
                    {
                        float self = Math.Max(widget.MeasureWidth(MEDIA_HEIGHT), 0f);
                        if (self > 0f) return self;
                    }
                    catch (Exception ex2) { MarkPluginBroken(index, ex2); return 0f; }
                    return fallbackWidth > 0f ? fallbackWidth : 0f;
                }
            }

            try
            {
                float w = Math.Max(widget.MeasureWidth(MEDIA_HEIGHT), 0f);
                if (!(w > 0f)) Logger.Debug($"[插件测宽] {widget.Id} 自报 0 宽 → 本帧不显示");
                return w;
            }
            catch (Exception ex) { MarkPluginBroken(index, ex); return 0f; }
        }

        private static float SumPluginRowWidth(string? pluginIdFilter)
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 预算没放行 → 与绘制一起缺席
                if (pluginIdFilter != null)
                {
                    if (!host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                        || !string.Equals(pid, pluginIdFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                total = total > 0f ? total + 16f + widths[i] : widths[i];
            }
            return total;
        }

        private static float SumPluginRowWidthNotIn(HashSet<string> orderSet)
        {
            var widgets = _pluginWidgets;
            var widths = _pluginWidths;
            var broken = _pluginBroken;
            var visible = _pluginVisible;
            if (widgets == null || widths == null || broken == null) return 0f;
            if (widths.Length != widgets.Length || broken.Length != widgets.Length) return 0f;

            var host = Plugins.PluginManager.Instance.Host;
            float total = 0f;
            for (int i = 0; i < widgets.Length; i++)
            {
                if (broken[i] || widths[i] <= 0f) continue;
                if (visible == null || i >= visible.Length || !visible[i]) continue; // 预算没放行 → 与绘制一起缺席
                bool inOrder = false;
                if (host.TryGetWidgetPlugin(widgets[i].Id, out var pid) && pid != null)
                    inOrder = orderSet.Contains(pid);
                if (inOrder) continue; // 已在顺序表 → 主循环已累计，跳过，防重复计宽
                total = total > 0f ? total + 16f + widths[i] : widths[i];
            }
            return total;
        }

        private static float DrawPluginWidgets(SKCanvas canvas, string? pluginIdFilter, float startX, float currentHeight,
            byte alpha, float textOffsetY, float[]? bars, float mouseX, float mouseY)
        {
            Plugins.IWidget[] widgets;
            float[] widths;
            bool[] broken;
            bool[]? drawn;
            bool[]? visible;
            lock (_pluginSnapshotLock)
            {
                if (_pluginWidgets == null || _pluginWidths == null || _pluginBroken == null) return startX;
                widgets = _pluginWidgets;
                widths = _pluginWidths;
                broken = _pluginBroken;
                drawn = _pluginDrawn;
                visible = _pluginVisible;
                if (widths.Length != widgets.Length || broken.Length != widgets.Length) return startX;
            }

            var theme = GetCurrentTheme();
            var host = Plugins.PluginManager.Instance.Host;
            float x = startX;

            lock (_pluginSlotLock)
            {
                for (int i = 0; i < widgets.Length; i++)
                {
                    if (drawn == null || i >= drawn.Length) break;
                    if (drawn[i]) continue;
                    float w = widths[i];
                    if (broken[i] || w <= 0f) continue;
                    if (visible == null || i >= visible.Length || !visible[i]) continue;

                    if (pluginIdFilter != null)
                    {
                        if (!host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                            || !string.Equals(pid, pluginIdFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    drawn[i] = true;
                    var r = new SKRect(x, 0f, x + w, currentHeight);
                    _pluginSlots.Add(new Plugins.WidgetLayout.Slot(widgets[i], r));

                    bool hovered = mouseX >= r.Left && mouseX <= r.Right && mouseY >= r.Top && mouseY <= r.Bottom;
                    var frame = new Plugins.WidgetFrame(theme, alpha, textOffsetY, bars, hovered);
                    canvas.Save();
                    try { widgets[i].Draw(canvas, r, frame); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    finally { canvas.Restore(); }

                    x += w + 16f;
                }
            }
            return x;
        }

        private static void MarkPluginBroken(int index, Exception ex)
        {
            if (index < 0 || _pluginBroken == null || index >= _pluginBroken.Length || _pluginBroken[index]) return;
            _pluginBroken[index] = true;
            Logger.Error($"[Renderer] 插件组件 {(_pluginWidgets != null && index < _pluginWidgets.Length ? _pluginWidgets[index].Id : "?")} 渲染异常，已停用其绘制", ex);
        }

        // ---- 插件详情页（右键展开） ----

        private const float MIN_DETAIL_WIDTH = 180f;

        private const float MAX_DETAIL_WIDTH = 1000f;

        private const float MIN_DETAIL_HEIGHT = 48f;

        private const float MAX_DETAIL_HEIGHT = 480f;

        private static Plugins.IDetailPage? _detailPage;      // 缓存的详情页实例（与宿主 ActiveDetailPage 同步）

        private static float _detailWidth;                    // 裁剪后的详情页宽度

        private static float _detailHeight;                   // 裁剪后的详情页高度

        private static volatile bool _detailBroken;           // 详情页抛异常 → 熔断（岛体退回原尺寸）

        private static bool _detailCloseRequested;            // 熔断后请求宿主收起（由 NotchWindow 消费）

        private static SKRect _detailHitRect;                 // 本帧详情页命中矩形（岛内逻辑坐标）

        private static Plugins.IDetailPage? _detailHitPage;   // 本帧详情页命中目标

        public static float ActiveDetailWidth
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailWidth : 0f; }
        }

        public static float ActiveDetailHeight
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailHeight : 0f; }
        }

        public static bool HasActiveDetailPage
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken; }
        }

        public const int CollapseNever = -1;

        public static bool ActiveDetailKeepsOpen => ActiveDetailCollapseDelayMs == CollapseNever;

        public static int? ActiveDetailCollapseDelayMs
        {
            get
            {
                lock (_pluginSlotLock)
                {
                    var page = _detailPage;
                    if (page == null || _detailBroken) return null;

                    try
                    {
                        var delay = page.AutoCollapseDelay;

                        if (delay < TimeSpan.Zero) return CollapseNever;

                        if (delay == TimeSpan.Zero) return null;   // 没指定 → 用宿主默认

                        return (int)Math.Clamp(delay.TotalMilliseconds, 500d, 60000d);
                    }
                    catch (Exception ex)
                    {
                        // 插件实现抛异常不该连累面板时序，记一条日志就够
                        Logger.Error("[Renderer] 读取详情页自动收起时长异常", ex);
                        return null;
                    }
                }
            }
        }

        public static void RefreshDetailPageState()
        {
            var page = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
            lock (_pluginSlotLock)
            {
                if (page == null)
                {
                    _detailPage = null;
                    _detailWidth = _detailHeight = 0f;
                    _detailBroken = false;
                    _detailHitPage = null;
                    _detailHitRect = default;
                    return;
                }
                if (ReferenceEquals(page, _detailPage)) return; // 同一个详情页：尺寸已算好，不重复触碰插件代码

                _detailPage = page;
                _detailBroken = false;
                _detailWidth = _detailHeight = 0f;

                _detailHitPage = null;
                _detailHitRect = default;
                try
                {
                    float w = page.MeasureWidth();
                    float h = page.MeasureHeight();
                    if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f)
                    {
                        Logger.Warn($"[Renderer] 详情页尺寸非法（{w} x {h}），已按最小尺寸兜底");
                        w = Math.Max(w, MIN_DETAIL_WIDTH);
                        h = Math.Max(h, MIN_DETAIL_HEIGHT);
                    }
                    _detailWidth = Math.Clamp(w, MIN_DETAIL_WIDTH, MAX_DETAIL_WIDTH);
                    _detailHeight = Math.Clamp(h, MIN_DETAIL_HEIGHT, MAX_DETAIL_HEIGHT);
                }
                catch (Exception ex)
                {
                    _detailBroken = true;
                    _detailWidth = _detailHeight = 0f;
                    _detailCloseRequested = true;
                    Logger.Error("[Renderer] 详情页尺寸测量异常，已熔断该详情页", ex);
                }
            }
        }

        public static bool TryGetDetailPageSize(out float width, out float height)
        {
            lock (_pluginSlotLock)
            {
                if (_detailPage == null || _detailBroken || _detailWidth <= 0f || _detailHeight <= 0f)
                {
                    width = height = 0f;
                    return false;
                }
                width = _detailWidth;
                height = _detailHeight;
                return true;
            }
        }

        public static bool ConsumeDetailCloseRequest()
        {
            lock (_pluginSlotLock)
            {
                if (!_detailCloseRequested) return false;
                _detailCloseRequested = false;
                return true;
            }
        }

        private static void DrawDetailPage(SKCanvas canvas, Plugins.IDetailPage page, float left, float currentHeight,
            float currentWidth, byte alpha, float textOffsetY, float[]? bars, float mouseX, float mouseY)
        {
            var rect = new SKRect(left, 0f, left + currentWidth, currentHeight);
            lock (_pluginSlotLock)
            {
                _detailHitPage = page;
                _detailHitRect = rect;
            }

            bool hovered = mouseX >= rect.Left && mouseX <= rect.Right && mouseY >= rect.Top && mouseY <= rect.Bottom;
            var frame = new Plugins.WidgetFrame(GetCurrentTheme(), alpha, textOffsetY, bars, hovered);
            canvas.Save();
            try { page.Draw(canvas, rect, frame); }
            catch (Exception ex) { MarkDetailBroken(ex); }
            finally { canvas.Restore(); }
        }

        public static bool DispatchDetailPageClick(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;
                var r = _detailHitRect;
                if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

                Plugins.WidgetHit hit;
                try { hit = page.HitTest(x - r.Left, y - r.Top, r); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页命中检测异常", ex); return false; }
                if (!hit.IsHit) return false;

                try { page.OnAction(hit.Action, x - r.Left, y - r.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页动作回调异常", ex); }
                return true;
            }
        }

        private static bool TryDetailLocalLocked(float x, float y, out Plugins.IDetailPage? page, out float lx, out float ly)
        {
            lx = ly = 0f;
            var p = _detailHitPage;
            page = p;
            if (p == null) return false;

            var r = _detailHitRect;
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

            lx = x - r.Left;
            ly = y - r.Top;
            return true;
        }

        public static bool DispatchDetailPageMouseDown(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                if (!TryDetailLocalLocked(x, y, out var page, out float lx, out float ly)) return false;
                try { page!.OnMouseDown(lx, ly); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标按下回调异常", ex); }
                return true;
            }
        }

        public static bool DispatchDetailPageMouseMove(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;

                var r = _detailHitRect;
                try { page.OnMouseMove(x - r.Left, y - r.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标移动回调异常", ex); }
                return true;
            }
        }

        public static bool DispatchDetailPageMouseUp(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                if (TryDetailLocalLocked(x, y, out var page, out float lx, out float ly))
                {
                    try { page!.OnMouseUp(lx, ly); }
                    catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标抬起回调异常", ex); }
                    return true;
                }

                var fallback = _detailHitPage;
                if (fallback == null) return false;
                try { fallback.OnMouseUp(x - _detailHitRect.Left, y - _detailHitRect.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标抬起回调异常", ex); }
                return true;
            }
        }

        public static void DispatchDetailPageMouseLeave()
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return;
                try { page.OnMouseLeave(); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标离开回调异常", ex); }
            }
        }

        public static bool DispatchDetailPageDragEnter(float x, float y, int count)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;

                var r = _detailHitRect;
                if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

                try { return page.OnFilesDragEnter(count); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入进入回调异常", ex); return false; }
            }
        }

        public static bool DispatchDetailPageDragOver(float x, float y)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;

                var r = _detailHitRect;
                if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

                try { page.OnFilesDragOver(x - r.Left, y - r.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入悬停回调异常", ex); }
                return true;
            }
        }

        public static void DispatchDetailPageDragLeave() => DispatchDetailPageDragLeave(null);

        public static void DispatchDetailPageDragLeave(Plugins.IDetailPage? page)
        {
            lock (_pluginSlotLock)
            {
                var target = page ?? _detailHitPage;
                if (target == null) return;

                try { target.OnFilesDragLeave(); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入离开回调异常", ex); }
            }
        }

        public static bool DispatchDetailPageDrop(float x, float y, string[] paths)
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return false;

                var r = _detailHitRect;
                if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) return false;

                try { page.OnFilesDragLeave(); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入离开回调异常", ex); }

                try { page.OnFilesDrop(paths); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入放下回调异常", ex); return false; }
                return true;
            }
        }

        private static void MarkDetailBroken(Exception ex)
        {
            if (_detailBroken) return;
            _detailBroken = true;
            _detailWidth = _detailHeight = 0f;
            _detailHitPage = null;
            _detailCloseRequested = true;
            Logger.Error("[Renderer] 详情页绘制异常，已熔断并收起", ex);
        }

    }
}
