using System;
using NotchPeninsula.Plugins;

namespace NotchPeninsula;

/// <summary>
/// 「显示设置 → 显示内容」列表的长按拖动排序。
///
/// <para>每条行在上下箭头左边多一个 ≡ 把手：短按不发生任何事，长按约 180ms 后进入拖动；
/// 拖动时按鼠标所在行实时重排，松手落定。原来的 ↑ / ↓ 按钮保留，键盘式逐格调整仍可用。</para>
///
/// <para>重排最终仍走 <see cref="PluginManager.MoveDisplay"/>：这样顺序落盘、推送给渲染侧、
/// 触发 Changed 的路径与点箭头完全一致，不会出现「拖完只是界面变了、岛上顺序没变」。</para>
/// </summary>
public partial class ConsoleWindow
{
    /// <summary>命中拖动把手时开始「等待长按」。返回 true = 这次点击已被把手吃掉。</summary>
    private bool TryStartDisplayDrag(int x, int y)
    {
        if (_selectedTab != 1 || Renderer.StandbyActive) return false;
        if (x < DISPLAY_DRAG_X || x > DISPLAY_DRAG_X + SORT_TRI_W) return false;
        if (!TryGetDisplayRowAt(x, y, out int row)) return false;

        var items = PluginManager.Instance.DisplayItems;
        if (row < 0 || row >= items.Count) return false;

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
    private void UpdateDisplayDrag(int x, int y)
    {
        if (!_displayDragActive || _displayDragKey == null) return;

        var items = PluginManager.Instance.DisplayItems;
        if (items.Count == 0) return;

        GetDisplayListLayout(out int visibleRows, out int maxFirstRow);

        // 拖到列表上下边缘时自动滚一格，否则条目多时拖不到屏幕外的行。
        float page = -_displayPageScroll;
        float top = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
        float bottom = top + Math.Max(1, visibleRows) * DISPLAY_ROW_H;
        int scroll = _displayScroll;
        if (y < top + 6f) scroll = Math.Max(0, scroll - 1);
        else if (y > bottom - 6f) scroll = Math.Min(maxFirstRow, scroll + 1);
        if (scroll != _displayScroll)
        {
            _displayScroll = scroll;
            page = -_displayPageScroll;
            top = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
        }

        int target = _displayScroll + (int)MathF.Floor((y - top) / DISPLAY_ROW_H);
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
        var items = PluginManager.Instance.DisplayItems;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>鼠标落在「显示内容」列表的哪一行（含滚动偏移）。</summary>
    private bool TryGetDisplayRowAt(int x, int y, out int row)
    {
        row = -1;
        if (_selectedTab != 1 || Renderer.StandbyActive) return false;

        GetDisplayListLayout(out int visibleRows, out _);
        float page = -_displayPageScroll;
        float top = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
        float bottom = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + page;
        if (x < DISPLAY_ITEM_L || x > DISPLAY_ITEM_R || y < top || y > bottom) return false;

        int idx = _displayScroll + (int)((y - top) / DISPLAY_ROW_H);
        if (idx < _displayScroll || idx >= _displayScroll + visibleRows) return false;
        if (idx >= PluginManager.Instance.DisplayItems.Count) return false;

        row = idx;
        return true;
    }
}
