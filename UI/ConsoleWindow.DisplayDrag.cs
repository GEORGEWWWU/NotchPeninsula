using NotchPeninsula.Plugins;

namespace NotchPeninsula;

/// <summary>
/// 显示内容列表的长按拖动排序。两个页签共用这一套：
///   · tab 1「显示设置 → 显示内容」（常驻清单，待机 / 静默模式下禁用）
///   · tab 9「静默模式设置」（静默清单，任何时候可用）
///
/// <para>每条行在上下箭头左边多一个 ≡ 把手：短按不发生任何事，长按约 180ms 后进入拖动；
/// 拖动时按鼠标所在行实时重排，松手落定。原来的 ↑ / ↓ 按钮保留。</para>
///
/// <para>重排最终仍走 <see cref="PluginManager.MoveDisplay"/>：这样顺序落盘、推送给渲染侧、
/// 触发 Changed 的路径与点箭头完全一致，不会出现「拖完只是界面变了、岛上顺序没变」。
/// 两套清单共用同一份 _order，所以在哪一页拖都是改同一个顺序。</para>
/// </summary>
public partial class ConsoleWindow
{
    /// <summary>当前是不是静默模式设置页（tab 9）。</summary>
    private bool DragOnSilentPage => _selectedTab == 9;

    private IReadOnlyList<PluginManager.DisplayItem> DragList
        => DragOnSilentPage
            ? PluginManager.Instance.SilentDisplayItems
            : PluginManager.Instance.DisplayItems;

    private float DragRowHeight => DragOnSilentPage ? SILENT_LIST_ROW_H : DISPLAY_ROW_H;

    private float DragListTop => DragOnSilentPage
        ? TITLE_BAR_HEIGHT + SilentListCardY + SILENT_LIST_FIRST_ROW_Y
        : TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y - _displayPageScroll;

    private float DragListBottom => DragOnSilentPage
        ? TITLE_BAR_HEIGHT + SilentListCardY + SilentListCardH
        : TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H - _displayPageScroll;

    private int DragScroll
    {
        get => DragOnSilentPage ? _silentListScroll : _displayScroll;
        set
        {
            if (DragOnSilentPage) _silentListScroll = value;
            else _displayScroll = value;
        }
    }

    private void GetDragListLayout(out int visibleRows, out int maxFirstRow)
    {
        if (DragOnSilentPage)
        {
            int fit = SilentListVisibleRows();
            int total = PluginManager.Instance.SilentDisplayItems.Count;
            visibleRows = Math.Min(total, fit);
            maxFirstRow = Math.Max(0, total - visibleRows);
            return;
        }
        GetDisplayListLayout(out visibleRows, out maxFirstRow);
    }

    /// <summary>命中拖动把手时开始「等待长按」。返回 true = 这次点击已被把手吃掉。</summary>
    private bool TryStartDisplayDrag(int x, int y)
    {
        // tab 1 在待机 / 静默模式下整卡禁用；tab 9 任何时候都能拖。
        if (!DragOnSilentPage && (_selectedTab != 1 || Renderer.StandbyModeOnly || Renderer.SilentActive))
            return false;
        if (x < DISPLAY_DRAG_X || x > DISPLAY_DRAG_X + SORT_TRI_W) return false;
        if (!TryGetDisplayRowAt(x, y, out int row)) return false;

        var items = DragList;
        if (row < 0 || row >= items.Count) return false;
        // 内置项（时间日期 / 资源占用 / 媒体控制器）不参与排序。
        if (items[row].IsBuiltin) return false;

        _displayDragPendingRow = row;
        _displayDragRow = row;
        _displayDragTargetRow = row;
        _displayDragKey = items[row].Key;
        _displayDragActive = false;

        // 长按判定：180ms 内松开就只是一次普通点击（什么都不做），不会误触排序。
        if (_hwnd != IntPtr.Zero)
        {
            Win32.SetTimer(_hwnd, DISPLAY_DRAG_TIMER_ID, DISPLAY_DRAG_HOLD_MS, IntPtr.Zero);
            Win32.SetCapture(_hwnd);
        }
        Render();
        return true;
    }

    /// <summary>定时器到点：还按着就正式进入拖动。</summary>
    private void ActivateDisplayDrag()
    {
        if (_displayDragPendingRow < 0 || _displayDragActive) return;
        _displayDragActive = true;
        _displayDragRow = _displayDragPendingRow;
        _displayDragTargetRow = _displayDragRow;
        Render();
    }

    /// <summary>拖动中：按鼠标所在行实时重排（只在跨行时写一次设置）。</summary>
    private void UpdateDisplayDrag(int y)
    {
        if (!_displayDragActive || _displayDragKey == null) return;

        var items = DragList;
        if (items.Count == 0) return;

        GetDragListLayout(out int visibleRows, out int maxFirstRow);

        // 拖到列表上下边缘时自动滚一格，否则条目多时拖不到屏幕外的行。
        float top = DragListTop;
        float bottom = top + Math.Max(1, visibleRows) * DragRowHeight;
        int scroll = DragScroll;
        if (y < top + 6f) scroll = Math.Max(0, scroll - 1);
        else if (y > bottom - 6f) scroll = Math.Min(maxFirstRow, scroll + 1);
        if (scroll != DragScroll)
        {
            DragScroll = scroll;
            top = DragListTop;
        }

        int target = DragScroll + (int)MathF.Floor((y - top) / DragRowHeight);
        target = Math.Clamp(target, 0, items.Count - 1);
        if (target == _displayDragTargetRow) return;

        _displayDragTargetRow = target;

        int cur = IndexOfDisplayKey(_displayDragKey);
        int guard = 0;
        while (cur >= 0 && cur != target && guard++ < 64)
        {
            int delta = target > cur ? 1 : -1;
            if (!PluginManager.Instance.MoveDisplay(_displayDragKey, delta)) break;
            int next = IndexOfDisplayKey(_displayDragKey);
            if (next == cur) break;
            cur = next;
        }

        _displayDragRow = cur;
        Render();
    }

    /// <summary>松手 / 取消：清状态、放鼠标捕获、停长按定时器。</summary>
    private void EndDisplayDrag()
    {
        bool had = _displayDragPendingRow >= 0 || _displayDragActive;
        if (_hwnd != IntPtr.Zero)
        {
            Win32.KillTimer(_hwnd, DISPLAY_DRAG_TIMER_ID);
            Win32.ReleaseCapture();
        }

        _displayDragPendingRow = -1;
        _displayDragActive = false;
        _displayDragRow = -1;
        _displayDragTargetRow = -1;
        _displayDragKey = null;

        if (had) Render();
    }

    private int IndexOfDisplayKey(string key)
    {
        // 两套清单的顺序完全一致（共用 _order），用哪一个只影响缓存命中，值一样。
        var items = DragList;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>鼠标落在当前列表的哪一行（含滚动偏移）。</summary>
    private bool TryGetDisplayRowAt(int x, int y, out int row)
    {
        row = -1;
        if (!DragOnSilentPage && (_selectedTab != 1 || Renderer.StandbyModeOnly || Renderer.SilentActive))
            return false;

        GetDragListLayout(out int visibleRows, out _);
        float top = DragListTop;
        float bottom = DragListBottom;
        if (x < DISPLAY_ITEM_L || x > DISPLAY_ITEM_R || y < top || y > bottom) return false;

        int idx = DragScroll + (int)((y - top) / DragRowHeight);
        if (idx < DragScroll || idx >= DragScroll + visibleRows) return false;
        if (idx >= DragList.Count) return false;

        row = idx;
        return true;
    }
}
