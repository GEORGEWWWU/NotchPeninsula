using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ================= 🧩 插件组件渲染接线 =================
        // 设计目标：稳态 60FPS 零 GC 分配。
        //   · 组件数组只在注册表版本变化时拷贝一次（_pluginWidgets）；
        //   · 每帧的宽度写入复用数组（_pluginWidths）；
        //   · 命中矩形复用同一个 List（_pluginSlots），绘制与鼠标分发共用；
        //   · 帧上下文 WidgetFrame / RenderTheme 均为 struct，栈上传递不进堆。
        // 插件 Draw / MeasureWidth 抛异常会被熔断（_pluginBroken），只记一次日志，绝不拖死渲染循环。
        private static Plugins.IWidget[]? _pluginWidgets;

        private static int _pluginWidgetsVersion = -1;

        private static float[]? _pluginWidths;

        private static bool[]? _pluginBroken;
        // 本帧绘制标记：组合模式下每个组件只在「内容顺序表」里它自己的位置画一次

        private static bool[]? _pluginDrawn;
        // 组合模式下媒体模块的右边界（供 UI 线程判定媒体按钮/悬停命中，避免窗口宽度换算误差）

        private static float _compositeMediaRight = -1f;

        private static readonly List<Plugins.WidgetLayout.Slot> _pluginSlots = new(8);

        // 🧩 插件行的宽度预算：本帧插件行最多能用多少宽度（含与原生内容之间的 16px 间距）。
        //    由 NotchWindow 每帧按「岛体总长上限 − 原生内容本帧占用宽度」算出后写入；
        //    组合模式由 GetCompositeWidth 内部按同一规则设置（那里才知道原生模块总宽）。
        //    预算内放不下的组件本帧整体不显示 —— 不压缩、不截断，杜绝文字被省略号砍掉半截。
        //    默认 +∞：宿主还没跑到判定逻辑时（启动首帧等），插件照常按自身所需宽度显示。

        private static float _pluginRowBudget = float.PositiveInfinity;
        // 预算判定结果，与 _pluginWidths 同序、同版本：true = 本帧给了这个组件它要的完整宽度。

        private static bool[]? _pluginVisible;
        // 本帧实际放行的插件行总宽（含与原生内容的 16px 间距）；0 = 一个组件都没放行。

        private static float _pluginRowReserve;

        /// <summary>插件行组件间距，也是插件行与原生内容之间的间距（与 Draw 里 <c>right + 16f</c> 对齐）。</summary>

        private const float PLUGIN_GAP = 16f;

        /// <summary>
        /// 原生内容区在任何情况下都要保住的最小宽度（**常量**，故意不跟原生内容实际所需宽度挂钩）。
        ///
        /// 只在「岛体宽度动画途中装不下两侧插件组」时用来给插件组按比例让位 ——
        /// 不设这个下限的话会算出负的原生内容区宽度，把文字遮罩与播放按钮翻到文字左边。
        ///
        /// ⚠️ 为什么必须是常量、不能换成「本帧原生内容真实所需宽度」：
        ///    原生内容所需宽度（换歌词 / 换标题时）与目标宽度是**同一刻跳变**的，而 currentWidth
        ///    还停在旧目标上。拿它当下限 → 换歌词那一帧立刻判定「装不下」→ 原生内容区从 320
        ///    一步拉到 512、插件整行被裁掉 —— 这就是又一次跳变（实测首帧 18px、全程 24px）。
        ///    常量下限则只在「岛体比插件行还窄」的极端瞬态才介入，换歌词时完全不介入，
        ///    边界只跟着岛体边缘平滑移动。用户 2026-09-20 反馈的「闪现」正是要保证这一点。
        /// </summary>

        private const float MIN_NATIVE_AREA = 80f;

        /// <summary>
        /// 设置本帧插件行的宽度预算（含与原生内容之间的 16px 间距）。
        ///
        /// 判定权在 NotchWindow：只有它知道原生内容（媒体控制器 / 长歌词自适应 / 硬件占用）
        /// 本帧要占多宽，用岛体总长上限减掉之后，剩下的才是插件行能用的空间。
        ///
        /// <para>
        /// 组件宽度是各自声明的（<see cref="Plugins.IWidget.MeasureWidth"/> 的语义 =
        /// <b>完整显示内容所需的宽度</b>）。本方法按注册顺序贪心分配：
        /// 所需宽度能完整落进剩余预算的组件才显示，装不下的组件本帧整体不显示 ——
        /// 宿主绝不替它压缩或截断（那才是「内容显示不全」）。
        /// 于是每个组件要么完整显示、要么完全不显示，不存在半截内容。
        /// </para>
        ///
        /// 传 0 即整行隐藏（通知 / 剪贴板 / 详情页接管岛体时）。
        /// 只影响「插件行贴在原生内容右侧」的非组合模式；组合模式由 <c>GetCompositeWidth</c>
        /// 内部按同一预算规则处理，不需要外部调用。
        /// </summary>

        public static void SetPluginRowBudget(float availableWidth)
        {
            lock (_pluginSnapshotLock)
            {
                _pluginRowBudget = float.IsNaN(availableWidth) || availableWidth < 0f ? 0f : availableWidth;
            }
            RefreshPluginWidgets(); // 版本变化时内部会重算一次；没变化则由下面这行重算
            lock (_pluginSnapshotLock) RecomputePluginVisibility();
        }

        /// <summary>
        /// <b>兼容旧调用</b>：等价于「给整行插件 +∞ 宽度」或「一点宽度都不给」。
        ///
        /// 判定逻辑在 <see cref="SetPluginRowBudget"/> 改成了按「组件所需宽度能否完整落进剩余空间」
        /// 逐个放行，本方法只是给旧的布尔口径留一个等价出口，宿主自身已不再调用。
        /// </summary>

        public static void SetPluginRowVisible(bool visible)
            => SetPluginRowBudget(visible ? float.PositiveInfinity : 0f);

        /// <summary>
        /// 按当前预算重算每个组件的放行情况（调用方须持有 <see cref="_pluginSnapshotLock"/>）。
        ///
        /// 贪心：按注册顺序逐个体判断「已用宽度 + 16px 间距 + 它要的宽度」是否还在预算内；
        /// 放不下就跳过它（不占宽度、不绘制、无命中区），后面的组件仍可继续尝试。
        /// 稳态（预算与内容都不变）下只在「值真的变了」时才写，零分配、零无效写入。
        /// </summary>

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
        // 组件快照/测量的锁：渲染线程与 UI 线程（鼠标命中路径会查询预留宽度）都可能访问

        private static readonly object _pluginSnapshotLock = new();
        // 鼠标逻辑坐标（相对窗口左上角），-1 表示鼠标不在灵动岛上

        private static float _pluginMouseX = -1f;

        private static float _pluginMouseY = -1f;

        /// <summary>NotchWindow 在 WM_MOUSEMOVE 中记录鼠标逻辑坐标；鼠标离开时传 (-1,-1)。</summary>

        public static void UpdatePluginMouse(float x, float y)
        {
            _pluginMouseX = x;
            _pluginMouseY = y;
        }

        /// <summary>
        /// 插件组件行独立占据岛体最右侧所需的预留宽度（含与原生内容的 16px 间距）。
        /// 返回 0 表示本帧没有任何组件被放行（没有可显示的插件，或预算放不下任何一个）。
        ///
        /// 放行结果由 <see cref="SetPluginRowBudget"/> 按「组件声明的所需宽度能否完整落进剩余空间」判定：
        /// 装不下的组件直接不出现在这一帧，而不是被压缩显示。
        ///
        /// 原生内容据此内收右边界，因此插件显示与否、排序如何，都完全不影响任何原生功能。
        /// </summary>

        public static float GetPluginRowReserve()
        {
            if (!RefreshPluginWidgets()) return 0f;
            return _pluginRowReserve;
        }

        /// <summary>
        /// 本帧插件行的可用宽度（含与原生内容之间的 16px 间距）——即「岛体总长上限 − 原生内容本帧占用宽度」。
        ///
        /// 这是**整行**的总预算。某个插件实际能用多少还要看它排在第几位，
        /// 见 <see cref="GetPluginRowRemaining"/>。
        ///
        /// 返回 <c>0</c> 表示本帧插件行被完全接管（通知 / 剪贴板 / 详情页）；
        /// 返回 <see cref="float.PositiveInfinity"/> 表示宿主尚未算过（启动首帧）。
        /// </summary>

        public static float GetPluginRowBudget() => _pluginRowBudget;

        /// <summary>
        /// 某个插件处的**剩余**可用宽度：按组件从左到右的放行优先级，
        /// 累加排在它前面的插件已经占掉的宽度（含间距），从整行预算里减掉，剩下的就是它的。
        ///
        /// 语义就是「不显示这个插件时，它所在位置还剩多少长度」——插件拿它来判断
        /// 「我这条内容放不放得下」：放不下就别报那个宽度上来（宿主会把整个组件隐藏），
        /// 换一条短的更划算。
        ///
        /// 说明：
        /// · <paramref name="pluginId"/> 为 null（宿主自身无插件上下文）时退回整行预算。
        /// · 本插件自己的组件不参与扣减——「不显示它时」的剩余，自然不该被它自己占掉。
        /// · 只统计**本帧被放行**的组件；没放行的组件本来就不占宽度，不该算在别人头上。
        /// · 不改动任何状态、不触发重测（快照由 NotchWindow 每帧刷新），渲染线程与后台线程都可安全调用。
        /// </summary>

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
                    // 走到本插件的第一个组件：它左边（更高优先级）占掉的就是别人的，剩下的全归它
                    if (host.TryGetWidgetPlugin(widgets[i].Id, out var pid)
                        && string.Equals(pid, pluginId, StringComparison.OrdinalIgnoreCase))
                        return Math.Max(0f, budget - used);

                    if (broken[i] || widths[i] <= 0f) continue;
                    if (visible == null || i >= visible.Length || !visible[i]) continue; // 没放行的不占宽
                    used += PLUGIN_GAP + widths[i];
                }

                // 快照里找不到本插件的组件（刚注册还没刷新等）→ 退回整行预算，宁可给宽也别给 0
                return Math.Max(0f, budget - used);
            }
        }

        /// <summary>把岛内逻辑坐标 (x,y) 的左键事件分发给插件组件；命中并处理返回 true。</summary>

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

        /// <summary>把岛内逻辑坐标 (x,y) 的右键事件广播给命中的插件组件（具体行为由插件决定）。</summary>
        /// <returns>
        /// 命中且提供详情页的组件 Id（供宿主展开详情页）；没有这种情况返回 null。
        /// 调用方（NotchWindow）拿到非 null 就展开详情页并消费掉这次右键，否则继续走原右键逻辑。
        /// </returns>

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

        /// <summary>
        /// 找出「把文件拖到它身上就该自动展开详情页」的那个<b>收起态</b>组件；没有则返回 null。
        ///
        /// <para>
        /// 命中来源是本帧绘制时登记的 <see cref="_pluginSlots"/> —— 也就是<b>只有这一帧真的画出来了的组件</b>
        /// 才算数。通知 / 剪贴板面板 / 已有详情页接管岛体期间，插件行本来就没绘制，自然不会命中。
        /// </para>
        ///
        /// <para>
        /// 三条判定：落点在该组件矩形内、组件声明了 <c>IWidget.AcceptsFileDropWhenCollapsed</c>、
        /// 且它确实提供详情页（没有详情页就谈不上「展开」）。
        /// 调用方（<c>IslandDropTarget</c>）负责在展开前再确认一次「当前没有别的详情页开着」。
        /// </para>
        /// </summary>
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

        /// <summary>每帧绘制前清空插件命中区；只有本帧实际绘制了插件行才会重新填充。</summary>

        private static void InvalidatePluginHitAreas()
        {
            lock (_pluginSlotLock)
            {
                _pluginSlots.Clear();
                // 每帧重置「已绘制」标记，让组合模式的顺序表混排能重新按位置分组绘制
                if (_pluginDrawn != null) Array.Clear(_pluginDrawn);
                // 详情页命中区同理：只有本帧真的画了详情页才重新登记
                _detailHitPage = null;
                _detailHitRect = default;
            }
        }

        /// <summary>
        /// 立即失效渲染侧持有的**全部插件快照**（组件数组 / 宽度 / 命中区 / 详情页）。
        ///
        /// 专供插件卸载路径调用：这些静态字段平时要到「下一帧发现版本号变了」才重建，
        /// 而卸载方法紧接着就会做几轮同步 GC 来确认可回收 ALC 是否释放 —— 那时这些字段
        /// 还钉着插件对象，GC 必然判定「加载上下文未被回收」。先在这里切断引用，
        /// 同步 GC 才有机会真正回收。
        /// </summary>
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

        /// <summary>
        /// 组合模式下媒体模块的右边界（-1 表示当前不在组合模式绘制）。
        /// UI 线程用它来判定媒体按钮 / 悬停区域，保证插件被排到媒体左边或右边时命中依然准确。
        /// </summary>

        public static float CompositeMediaRight => _compositeMediaRight;

        /// <summary>
        /// 媒体模块右边界：优先用渲染时记下的真实值（<c>_compositeMediaRight</c>），
        /// 它同时覆盖「组合模式」与「非组合模式 + 有插件预留」两种情况 —— 插件被排到原生内容左边时，
        /// 媒体右边界就是岛体右边界，下面的换算公式会算出偏左的错误位置。
        /// 还没渲染过（启动首帧 / 整块岛体被通知接管）时才退回按「岛体右边界 − 插件预留」推算。
        /// </summary>

        public static float GetMediaRight(float windowWidth, float currentWidth, bool toastActive)
        {
            if (_compositeMediaRight > 0f) return _compositeMediaRight;
            return (windowWidth + currentWidth) / 2f - (toastActive ? 0f : GetPluginRowReserve());
        }

        // ================= 🧩 非组合模式下的插件左右分组 =================
        // 背景：组合模式靠「内容顺序表」把原生模块与插件混排；非组合模式同一时刻只显示一个原生模块
        //       （媒体激活 → 媒体；否则按待机显示模式 → 时间日期 / 硬件占用 / 空），所以插件相对它
        //       只有「排左边」和「排右边」两种位置，同样是查同一张顺序表得出。

        /// <summary>
        /// 非组合模式下「本帧原生内容」对应的内置模块 Id（同一时刻最多一个）；没有原生内容时返回 null。
        /// </summary>

        private static string? GetNativeBuiltinId(bool mediaActive)
        {
            if (mediaActive) return Plugins.BuiltinWidgets.Media;
            if (StandbyDisplayMode == 0) return Plugins.BuiltinWidgets.Clock;
            if (StandbyDisplayMode == 2) return Plugins.BuiltinWidgets.Hardware;
            return null; // 空白待机：没有原生内容，插件行独占整岛（沿用「整行贴右侧」）
        }

        /// <summary>某个 Id 在「内容显示顺序表」里的位置；-1 表示尚未登记。</summary>

        private static int OrderIndexOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            var order = Plugins.PluginManager.Instance.Host.ContentOrder;
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// 某个组件相对原生模块的左右归属：-1 = 排在原生内容左边，+1 = 右边。
        /// 没有原生内容、或组件所属插件还没进顺序表时一律返回 +1（贴右侧），与引入顺序表之前的行为一致。
        /// </summary>

        private static int GetWidgetSide(string widgetId, int nativeOrderIndex)
        {
            if (nativeOrderIndex < 0) return 1;
            var host = Plugins.PluginManager.Instance.Host;
            if (!host.TryGetWidgetPlugin(widgetId, out var pid) || string.IsNullOrEmpty(pid)) return 1;
            int idx = OrderIndexOf(pid);
            return idx >= 0 && idx < nativeOrderIndex ? -1 : 1;
        }

        /// <summary>
        /// 按「内容显示顺序表」把本帧可见的插件组件分成左右两组，返回两组的**内容宽度**
        /// （各组件宽度之和 + 组内 16px 间距，不含与原生内容之间的间距）。
        /// 只统计被预算放行的组件 —— 与绘制、宽度累加的口径严格一致，宽度才不会与绘制脱节。
        /// </summary>

        private static void MeasurePluginSides(int nativeOrderIndex, out float leftWidth, out float rightWidth)
        {
            leftWidth = 0f;
            rightWidth = 0f;

            Plugins.IWidget[]? widgets;
            float[]? widths;
            bool[]? broken;
            bool[]? visible;
            // 在同一把锁内取齐快照，避免 UI 线程正好重建快照时读到长度不一致的数组
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

        /// <summary>刷新插件组件快照（版本变化时才分配 + 测量一次），返回是否存在可渲染组件。</summary>

        private static bool RefreshPluginWidgets()
        {
            lock (_pluginSnapshotLock)
            {
                var host = Plugins.PluginManager.Instance.Host;
                int version = host.WidgetsVersion;
                if (_pluginWidgetsVersion == version && _pluginWidths != null)
                    return _pluginWidgets!.Length > 0;

                _pluginWidgetsVersion = version;
                // Widgets getter 返回的是加锁下的全新数组（已按插件顺序排好），as 转换零拷贝直接持有
                _pluginWidgets = host.Widgets as Plugins.IWidget[] ?? Array.Empty<Plugins.IWidget>();
                _pluginWidths = _pluginWidgets.Length > 0 ? new float[_pluginWidgets.Length] : Array.Empty<float>();
                _pluginBroken = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginDrawn = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();
                _pluginVisible = _pluginWidgets.Length > 0 ? new bool[_pluginWidgets.Length] : Array.Empty<bool>();

                // 宽度与快照同版本一起算好：稳态 60FPS 下不再触碰插件代码，保持零额外开销
                for (int i = 0; i < _pluginWidgets.Length; i++)
                {
                    float w = 0f;
                    try { w = Math.Max(_pluginWidgets[i].MeasureWidth(BASE_HEIGHT), 0f); }
                    catch (Exception ex) { MarkPluginBroken(i, ex); }
                    _pluginWidths[i] = w;
                }
                // 用新宽度按当前预算重算放行情况（新组件默认不显示，必须立刻算一次）
                RecomputePluginVisibility();
                return _pluginWidgets.Length > 0;
            }
        }

        /// <summary>
        /// 按缓存宽度求和（组件间 16px 间距）。pluginIdFilter 不为 null 时只统计该插件的组件。
        /// 只累计「本帧被预算放行」的组件 —— 与 Draw 的过滤规则严格一致，宽度与绘制才不会脱节。
        /// 返回 0 表示该范围内没有可显示的组件。
        /// </summary>

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

        /// <summary>
        /// 兜底累加「不在内容顺序表里」的插件宽度，与 Draw 底部按 drawn 标记只补未绘制组件的行为一一对应。
        /// 已在顺序表里的插件主循环已累计过一次，这里绝不重复累加 —— 否则组合模式右侧会多一整版插件宽度的死空白。
        /// </summary>

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

        /// <summary>
        /// 绘制插件组件并缓存命中矩形（供鼠标分发复用）。
        /// pluginIdFilter 为 null 表示绘制「本帧尚未画过」的全部组件（非组合模式的整行绘制）；
        /// 不为 null 时只画属于该插件的组件 —— 组合模式据此把插件摆到顺序表指定的位置。
        /// 两种模式都只画 <see cref="SetPluginRowBudget"/> 放行的组件：没放行的既不绘制也不登记命中区。
        /// 返回推进后的游标 X（下一个内容块的起点，已含 16px 间距）。
        /// mouseX/mouseY 为扣除 topY 平移后的岛内逻辑坐标。
        /// </summary>

        private static float DrawPluginWidgets(SKCanvas canvas, string? pluginIdFilter, float startX, float currentHeight,
            byte alpha, float textOffsetY, float[]? bars, float mouseX, float mouseY)
        {
            Plugins.IWidget[] widgets;
            float[] widths;
            bool[] broken;
            bool[]? drawn;
            bool[]? visible;
            // 在同一把锁内取齐快照，避免 UI 线程正好重建快照时读到长度不一致的数组
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
                    // 预算没放行（所需宽度装不进剩余空间）→ 本帧整体不画它，也不登记命中区
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

        /// <summary>熔断持续抛异常的插件组件：停用其绘制/命中，整个生命周期只记一次日志防刷屏。</summary>

        private static void MarkPluginBroken(int index, Exception ex)
        {
            if (index < 0 || _pluginBroken == null || index >= _pluginBroken.Length || _pluginBroken[index]) return;
            _pluginBroken[index] = true;
            Logger.Error($"[Renderer] 插件组件 {(_pluginWidgets != null && index < _pluginWidgets.Length ? _pluginWidgets[index].Id : "?")} 渲染异常，已停用其绘制", ex);
        }

        // ================= 🧩 插件详情页（右键展开） =================
        // 详情页把整个岛体内容整块换掉：尺寸完全由插件通过 MeasureWidth / MeasureHeight 决定，
        // 宿主只做上下限裁剪（防止插件把岛体撑到屏幕外），并负责把岛内左键交给详情页处理。
        // 与组件一致：Measure/Draw 抛异常一律熔断，只记一次日志，绝不拖死渲染循环。

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

        /// <summary>详情页展开时岛体应采用的宽度（0 = 未展开 / 详情页已熔断）。</summary>
        public static float ActiveDetailWidth
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailWidth : 0f; }
        }

        /// <summary>详情页展开时岛体应采用的高度（0 = 未展开 / 详情页已熔断）。</summary>
        public static float ActiveDetailHeight
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken ? _detailHeight : 0f; }
        }

        /// <summary>是否正处于详情页展开状态（渲染侧 / NotchWindow 尺寸决策依据）。</summary>
        public static bool HasActiveDetailPage
        {
            get { lock (_pluginSlotLock) return _detailPage != null && !_detailBroken; }
        }

        /// <summary>
        /// <see cref="ActiveDetailCollapseDelayMs"/> 返回它时，表示详情页要求「鼠标离开也不收起」。
        /// </summary>
        public const int CollapseNever = -1;

        /// <summary>
        /// 当前详情页是否声明了「鼠标离开也不收起」（<c>AutoCollapseDelay</c> 返回负值）。
        ///
        /// <para>
        /// 岛外点击要不要顺手把它收掉，就看这个 —— 正在从资源管理器往面板里拖文件的用户，
        /// 鼠标必然要经过岛外，那种「点了别处」不能算「想关面板」。
        /// </para>
        /// </summary>
        public static bool ActiveDetailKeepsOpen => ActiveDetailCollapseDelayMs == CollapseNever;

        /// <summary>
        /// 当前详情页要求的「鼠标离开后自动收起」时长（毫秒）。三种返回：
        /// <list type="bullet">
        ///   <item><c>null</c> —— 未展开、详情页没指定、或取值抛异常 → 调用方沿用宿主内置时长。</item>
        ///   <item><see cref="CollapseNever"/> —— 详情页明确要求鼠标离开也别收 → 调用方连计时都不用挂。</item>
        ///   <item>正数 —— 自定义时长，已夹在 0.5 秒 ~ 60 秒之间。</item>
        /// </list>
        /// </summary>
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

                        // 负值（约定用 Timeout.InfiniteTimeSpan）= 永不收起
                        if (delay < TimeSpan.Zero) return CollapseNever;

                        if (delay == TimeSpan.Zero) return null;   // 没指定 → 用宿主默认

                        // 有效值夹在 0.5s ~ 60s：上限防止插件把面板钉死在屏幕上收不掉，下限防止设得过短没法用
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

        /// <summary>
        /// 同步宿主详情页状态并测量尺寸。必须在读取 WINDOW_WIDTH / MAX_WINDOW_HEIGHT 之前调用
        /// （NotchWindow 每帧第一件事就是它），因为底层缓冲尺寸依赖详情页大小。
        /// 尺寸只在详情页实例变化时测量一次，稳态 60FPS 下不触碰插件代码。
        /// </summary>

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

        /// <summary>详情页展开时的岛体尺寸（已裁剪）；未展开或已熔断时返回 false。</summary>

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

        /// <summary>取走「详情页熔断，请宿主收起」的请求（一次性）。NotchWindow 每帧调用。</summary>

        public static bool ConsumeDetailCloseRequest()
        {
            lock (_pluginSlotLock)
            {
                if (!_detailCloseRequested) return false;
                _detailCloseRequested = false;
                return true;
            }
        }

        /// <summary>
        /// 绘制详情页：整块岛体交给插件绘制，并登记命中矩形供左键分发。
        /// mouseX/mouseY 为已扣除 topY 平移后的岛内逻辑坐标（与组件行一致）。
        /// </summary>

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

        /// <summary>把岛内逻辑坐标 (x,y) 的左键事件交给详情页（HitTest + OnAction）；未展开或无命中返回 false。</summary>

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

        // ---- 详情页的鼠标事件（比 HitTest/OnAction 更细：有按下 / 移动 / 抬起 / 离开）----
        // 老那套是「宿主做命中、只回调一个动作名」，一次点击只有一个回调，做不了「按住拖动」；
        // 这一组把完整的鼠标消息转发给详情页，由插件自己判断命中了什么。
        // 坐标口径与上面完全一致：传入的 x/y 是岛内逻辑坐标，这里减掉 rect 左上角就是详情页内坐标。

        /// <summary>调用方必须已持有 _pluginSlotLock。落点不在详情页内时返回 false。</summary>
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

        /// <summary>左键按下（落点在详情页内才转发）。返回 true 表示这次按下归详情页。</summary>
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

        /// <summary>
        /// 鼠标移动。
        ///
        /// <para>
        /// ⚠️ <b>刻意不做落点判定</b>（和上面几个方法不一样）：按住拖动时鼠标<b>一定会</b>离开详情页矩形 ——
        /// 详情页只有一两百像素高，而拖动是个大幅度动作，两下就划出去了。
        /// 一旦在这里因为它出界就返回 false，插件就再也收不到移动，
        /// 「按下后位移超过阈值再发起拖出」这套逻辑永远触发不了，表现就是<b>完全拖不动</b>。
        /// </para>
        ///
        /// <para>坐标照实往下传（可能为负 / 超出尺寸），要不要理会由详情页自己决定。</para>
        /// </summary>
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

        /// <summary>
        /// 左键抬起。与移动不同：即使落点已经不在详情页内（按下后拖到岛体别处再松手）
        /// 也要转发一次，否则插件那边「按住」的状态就永远复位不了。
        /// </summary>
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

                // 落在详情页外：仍然通知一次「抬起」，坐标按越界处理（详情页自己复位的时机）
                var fallback = _detailHitPage;
                if (fallback == null) return false;
                try { fallback.OnMouseUp(x - _detailHitRect.Left, y - _detailHitRect.Top); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页鼠标抬起回调异常", ex); }
                return true;
            }
        }

        /// <summary>鼠标离开灵动岛 —— 详情页用它复位「按住」之类的状态（这条一定会来，抬起则不一定）。</summary>
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

        // ---- 详情页的文件拖放（岛体 IDropTarget → 这里 → 插件）----
        // 这一组与上面的 DispatchDetailPageClick 同源：都靠绘制时登记的 _detailHitPage / _detailHitRect，
        // 坐标口径也完全一样（调用方给的 x/y 已是「岛内逻辑坐标」，这里再减 rect 左上角就是详情页内坐标）。

        /// <summary>
        /// 拖入项进入岛体：落点不在当前展开的详情页里就拒绝。
        /// 返回 true 表示详情页接受了这次拖放（调用方据此给「可放入」光标）。
        /// </summary>
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

        /// <summary>拖放过程中鼠标移动。返回 false = 已经拖出详情页范围（调用方据此作废本次拖放）。</summary>
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

        /// <summary>
        /// 拖出岛体 / 拖放被取消：让详情页把悬停态收掉。
        /// 这条回调一定会来（包括用户中途按 Esc），所以它是复位高亮的唯一可靠时机。
        /// </summary>
        public static void DispatchDetailPageDragLeave()
        {
            lock (_pluginSlotLock)
            {
                var page = _detailHitPage;
                if (page == null) return;

                try { page.OnFilesDragLeave(); }
                catch (Exception ex) { Logger.Error("[Renderer] 详情页拖入离开回调异常", ex); }
            }
        }

        /// <summary>
        /// 用户在详情页里松手。返回 true 表示这次拖放被接受（调用方回 COPY 效果）。
        /// 顺序是「先收悬停态、再报放下了什么」—— 反过来的话插件在 OnFilesDragLeave 里
        /// 复位高亮，会把 OnFilesDrop 刚设好的状态一起抹掉。
        /// </summary>
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

        /// <summary>熔断抛异常的详情页：停用绘制并请求宿主收起，整个生命周期只记一次日志。</summary>

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
