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
                if (visible[i] != ok) visible[i] = ok; // 只在变化时写：bool 写入原子，渲染线程不会看到中间态
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
                if (_pluginDrawn != null) Array.Clear(_pluginDrawn);
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
            lock (_pluginSlotLock)
            {
                _pluginWidgets = null;
                _pluginWidths = null;
                _pluginBroken = null;
                _pluginDrawn = null;
                _pluginVisible = null;
                _pluginWidgetsVersion = -1;   // 下一帧强制重建快照
                _pluginSlots.Clear();
                _pluginRowReserve = 0f;

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
                if (_pluginWidgetsVersion == version && _pluginWidths != null)
                    return _pluginWidgets!.Length > 0;

                _pluginWidgetsVersion = version;
                _pluginWidgets = host.Widgets as Plugins.IWidget[] ?? Array.Empty<Plugins.IWidget>();
                _pluginWidths = _pluginWidgets.Length > 0 ? new float[_pluginWidgets.Length] : Array.Empty<float>();
                _pluginBroken = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginDrawn = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginVisible = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();

                for (int i = 0; i < _pluginWidgets.Length; i++)
                {
                    float w = 0f;
                    try { w = Math.Max(_pluginWidgets[i].MeasureWidth(MEDIA_HEIGHT), 0f); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    _pluginWidths[i] = w;
                }
                RecomputePluginVisibility();
                return _pluginWidgets.Length > 0;
            }
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
