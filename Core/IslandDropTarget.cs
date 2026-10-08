using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula;

internal sealed class IslandDropTarget : Win32.IDropTarget
{
    private readonly NotchWindow _window;

    private bool _accepted;

    private int _dragItemCount;

    private Plugins.IDetailPage? _hoverPage;

    internal IslandDropTarget(NotchWindow window) => _window = window;

        private void TryExpandCollapsedDropWidget(float x, float y)
    {
        if (Renderer.HasActiveDetailPage) return;   // 已经有面板开着，不抢

        string? widgetId = Renderer.FindCollapsedFileDropWidget(x, y);
        if (widgetId == null) return;

        NotchWindow.ExpandPanel(widgetId);
        Logger.Info($"[岛体拖放] 拖到收起态的组件 {widgetId} 上，已自动展开它的详情页");
    }

    private static void SetFileDragInProgress(bool active)
    {
        if (Renderer.FileDragInProgress == active) return;
        Renderer.FileDragInProgress = active;
        Logger.Info(active
            ? "[岛体拖放] 检测到文件拖入，已临时禁用穿透模式（岛体不再悬停淡出）"
            : "[岛体拖放] 本轮拖放结束，穿透模式恢复");
    }

    public int DragEnter(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        if (_accepted || _hoverPage != null) Renderer.DispatchDetailPageDragLeave(_hoverPage);
        _accepted = false;
        _hoverPage = null;
        _dragItemCount = 0;   // 条目数也重置，免得上一轮的值残留到这一轮的重试逻辑里

        if (DragOutState.IsSelfDrop(NotchWindow.InstanceHandle))
        {
            Logger.Info("[岛体拖放] 拒绝：这次拖放是岛体自己发起的（self-drop）");
            return 0;
        }

        if (!_window.TryScreenToIslandLogical(pt, out float x, out float y))
        {
            Logger.Warn("[岛体拖放] 坐标换算失败，已拒绝");
            return 0;
        }

        var files = DropPayload.ReadFileDrop(dataObj);
        if (files.Count == 0)
        {
            Logger.Info($"[岛体拖放] 进入，但内容里没有文件路径（落点 {x:F0},{y:F0}），已拒绝");
            return 0;
        }

        _dragItemCount = files.Count;

        SetFileDragInProgress(true);

        TryExpandCollapsedDropWidget(x, y);

        bool accepted = Renderer.DispatchDetailPageDragEnter(x, y, files.Count);
        Logger.Info($"[岛体拖放] 进入：{files.Count} 项，落点 {x:F0},{y:F0}，详情页{(accepted ? "接受" : "拒绝")}");
        if (!accepted) return 0;

        _window.KeepAliveForDrop();

        _accepted = true;
        _hoverPage = Renderer.ActiveDetailPageOrNull;   // 记住是谁高亮的，离场时按它复位
        pdwEffect = Win32.DROPEFFECT_COPY;
        return 0;
    }

    public int DragOver(uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        if (!_window.TryScreenToIslandLogical(pt, out float x, out float y)) return 0;

        if (_accepted)
        {
            _window.KeepAliveForDrop();

            if (!Renderer.DispatchDetailPageDragOver(x, y))
            {
                Renderer.DispatchDetailPageDragLeave(_hoverPage);
                _accepted = false;
            }
            else
            {
                pdwEffect = Win32.DROPEFFECT_COPY;
            }
            return 0;
        }

        //    表现就是「有时候拖入判定有误：怎么拖都不接受」。

        // 是的话把它的详情页展开，下一帧起上面那句重试就会成立。
        TryExpandCollapsedDropWidget(x, y);

        if (_dragItemCount > 0 && Renderer.DispatchDetailPageDragEnter(x, y, _dragItemCount))
        {
            _window.KeepAliveForDrop();
            _accepted = true;
            _hoverPage = Renderer.ActiveDetailPageOrNull;   // 与 DragEnter 一致：记住高亮对象
            pdwEffect = Win32.DROPEFFECT_COPY;
            Logger.Info($"[岛体拖放] 落点移入详情页，转为接受（{_dragItemCount} 项，{x:F0},{y:F0}）");
        }

        return 0;
    }

    public int DragLeave()
    {
        Logger.Info($"[岛体拖放] 离开（accepted={_accepted}）");

        SetFileDragInProgress(false);   // 拖放走了，穿透淡出照旧

        if (_accepted || _hoverPage != null) Renderer.DispatchDetailPageDragLeave(_hoverPage);
        _accepted = false;
        _hoverPage = null;

        // 拖放走了：补一次「鼠标离开岛体」的判定。
        _window.NotifyDragExit();
        return 0;
    }

    public int Drop(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        bool accepted = _accepted;
        var hoverPage = _hoverPage;      // 这一轮进过的详情页（可能已经被收起 / 换页，仍要复位它）
        _accepted = false;
        _hoverPage = null;

        SetFileDragInProgress(false);   // 松手即收官：穿透淡出立刻恢复，不必等下一次状态轮询

        if (!_window.TryScreenToIslandLogical(pt, out float x, out float y))
        {
            if (accepted || hoverPage != null) Renderer.DispatchDetailPageDragLeave(hoverPage);
            return 0;
        }

        if (!accepted && _dragItemCount > 0)
        {
            accepted = Renderer.DispatchDetailPageDragEnter(x, y, _dragItemCount);

            // 免得详情页的延迟折叠在松手这一瞬间正好到点。
            if (accepted)
            {
                _window.KeepAliveForDrop();
                hoverPage = Renderer.ActiveDetailPageOrNull;
            }
        }

        if (!accepted)
        {
            Logger.Info("[岛体拖放] 放下，但本轮拖放此前未被接受（落点一直不在详情页内），忽略");
            if (hoverPage != null) Renderer.DispatchDetailPageDragLeave(hoverPage);
            return 0;
        }

        bool dropped = false;
        var files = DropPayload.ReadFileDrop(dataObj);
        if (files.Count > 0)
            dropped = Renderer.DispatchDetailPageDrop(x, y, files.ToArray());

        //    详情页的悬停态就此没人清，高亮永远留在界面上。
        if (!dropped) Renderer.DispatchDetailPageDragLeave(hoverPage);
        else pdwEffect = Win32.DROPEFFECT_COPY;

        Logger.Info($"[岛体拖放] 放下：落点 {x:F0},{y:F0}，详情页{(dropped ? "收下了" : "没收（落点在详情页外），已补复位悬停态")}");
        return 0;
    }
}
