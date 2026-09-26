using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula;

/// <summary>
/// 灵动岛本体的 OLE 拖入目标 —— 让「右键展开的插件详情页」也能接文件拖放。
///
/// <para>
/// 为什么必须有这一层：插件的独立窗口（<c>IPluginWindow</c>）本身就是自己的 HWND，挂拖放很直接；
/// 但右键展开的详情页是画在<b>灵动岛本体</b>上的，而岛体从来只处理鼠标与键盘消息，
/// 所以详情页里根本收不到拖入事件。这个类就是补上缺的那一环。
/// </para>
///
/// <para>三个必须踩准的点：</para>
/// <list type="number">
///   <item>
///     <b>命中区域远大于可见岛体</b>：岛体窗口的矩形是整个最大窗口尺寸（1000×480 量级），
///     而真正可见的岛体只占其中一小块。所以每次拖放都要把坐标交给 Renderer，
///     由它判断落点是否落在「当前展开的详情页」矩形里，不在就一律拒绝。
///   </item>
///   <item>
///     <b>坐标口径必须和鼠标点击完全一致</b>：屏幕物理像素 → ScreenToClient → 除以 DPI → 再减 hitTopY。
///     任何一步漏掉，落点都会整体偏移（拖到卡片左边却删掉右边那种）。
///   </item>
///   <item>
///     <b>拖放期间收不到 WM_MOUSEMOVE</b>：鼠标被 OLE 的拖放循环接管了。
///     而详情页有「鼠标移开就收起」的延迟计时，这时若不主动续一口「还悬停着」，
///     面板会在拖放途中把自己收掉，拖放目标当场消失。
///   </item>
/// </list>
/// </summary>
internal sealed class IslandDropTarget : Win32.IDropTarget
{
    private readonly NotchWindow _window;

    /// <summary>本次拖放是否已被详情页接受。没接受就一路拒绝，不做任何转发。</summary>
    private bool _accepted;

    /// <summary>本次拖入的条目数（DragEnter 时解析出来存着，供 DragOver 重试接受时复用）。</summary>
    private int _dragItemCount;

    internal IslandDropTarget(NotchWindow window) => _window = window;

    public int DragEnter(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        // 上一次拖放若没能正常收尾（拖放源被强杀、进程崩掉之类，DragLeave / Drop 都收不到），
        // 悬停态会一直留在详情页上。新一次拖入之前先清干净，免得「上一次的高亮」粘在这一回上。
        if (_accepted) Renderer.DispatchDetailPageDragLeave();
        _accepted = false;
        _dragItemCount = 0;   // 条目数也重置，免得上一轮的值残留到这一轮的重试逻辑里

        // 岛体自己拖出去的东西，别接回来 —— 用户从详情页往岛外拖时，光标起点就在岛体上
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

        // 只认带文件系统路径的拖入：网页文字、位图流在这里就是空列表，直接拒绝
        var files = DropPayload.ReadFileDrop(dataObj);
        if (files.Count == 0)
        {
            Logger.Info($"[岛体拖放] 进入，但内容里没有文件路径（落点 {x:F0},{y:F0}），已拒绝");
            return 0;
        }

        // 记下条目数：万一这一下的落点不在详情页里，DragOver 还要靠它重试接受
        _dragItemCount = files.Count;

        bool accepted = Renderer.DispatchDetailPageDragEnter(x, y, files.Count);
        Logger.Info($"[岛体拖放] 进入：{files.Count} 项，落点 {x:F0},{y:F0}，详情页{(accepted ? "接受" : "拒绝")}");
        if (!accepted) return 0;

        // 被接受之后才续「鼠标还在岛上」：拖放期间 OLE 接管鼠标，窗口收不到
        // WM_MOUSEMOVE / WM_MOUSELEAVE，详情页的延迟折叠会把面板在中途收掉。
        // 放在接受之后而不是一进来就设，是为了不把「拖到岛体透明区、详情页并不接受」的悬停也算进来。
        _window.KeepAliveForDrop();

        _accepted = true;
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

            // 拖出了详情页矩形（比如滑到岛体另一头）→ 本次接受作废，光标给「不允许」
            if (!Renderer.DispatchDetailPageDragOver(x, y))
            {
                Renderer.DispatchDetailPageDragLeave();
                _accepted = false;
            }
            else
            {
                pdwEffect = Win32.DROPEFFECT_COPY;
            }
            return 0;
        }

        // ⚠️ 尚未被接受时，在这里持续重试接受 —— 这一步是必须的：
        //    OLE 只在鼠标「进入窗口」那一刻调一次 DragEnter。用户若是从岛体边缘、
        //    或者上方那段不属于详情页的区域滑进来，那一下的落点就不在详情页矩形里，
        //    于是被拒；之后鼠标再怎么移到详情页上，都不会有第二次 DragEnter，永远接不上。
        //    表现就是「有时候拖入判定有误：怎么拖都不接受」。
        if (_dragItemCount > 0 && Renderer.DispatchDetailPageDragEnter(x, y, _dragItemCount))
        {
            _window.KeepAliveForDrop();
            _accepted = true;
            pdwEffect = Win32.DROPEFFECT_COPY;
            Logger.Info($"[岛体拖放] 落点移入详情页，转为接受（{_dragItemCount} 项，{x:F0},{y:F0}）");
        }

        return 0;
    }

    public int DragLeave()
    {
        Logger.Info($"[岛体拖放] 离开（accepted={_accepted}）");

        if (_accepted) Renderer.DispatchDetailPageDragLeave();
        _accepted = false;

        // 拖放走了：补一次「鼠标离开岛体」的判定。
        // 拖放期间 OLE 接管鼠标，窗口收不到 WM_MOUSELEAVE，不补这一下，
        // 详情页会一直停在「正在拖入」的样子（高亮不灭、也不走折叠计时）—— 看起来就是卡住。
        _window.NotifyDragExit();
        return 0;
    }

    public int Drop(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        bool accepted = _accepted;
        _accepted = false;
        if (!accepted)
        {
            Logger.Info("[岛体拖放] 放下，但本轮拖放此前未被接受（可能落点一直在详情页之外），忽略");
            return 0;
        }

        bool dropped = false;
        if (_window.TryScreenToIslandLogical(pt, out float x, out float y))
        {
            var files = DropPayload.ReadFileDrop(dataObj);
            if (files.Count > 0)
                dropped = Renderer.DispatchDetailPageDrop(x, y, files.ToArray());
        }

        // ⚠️ 落点不在详情页矩形内时（最典型：用户在岛体边缘松手，或者拖到一半随手一放），
        //    DispatchDetailPageDrop 会直接返回 false，并且**不会**回调 OnFilesDragLeave ——
        //    详情页的悬停态就此没人清，高亮永远留在界面上。
        //    表现就是「没拖到位就松手 → 一直卡在拖入页面」。这里兜一刀：
        //    只要这一轮拖放曾被接受过，收尾就必须把悬停态复位。
        if (!dropped) Renderer.DispatchDetailPageDragLeave();
        else pdwEffect = Win32.DROPEFFECT_COPY;

        Logger.Info($"[岛体拖放] 放下：落点 {x:F0},{y:F0}，详情页{(dropped ? "收下了" : "没收（落点在详情页外），已补复位悬停态")}");
        return 0;
    }
}
