using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula;

/// <summary>
/// 灵动岛本体的 OLE 拖入目标 —— 让右键展开的插件详情页也能接文件拖放。
///
/// 为什么需要这一层：插件的独立窗口（IPluginWindow）本身就是自己的 HWND，挂拖放很直接；
/// 但右键展开的详情页画在灵动岛本体上，而岛体从来只处理鼠标与键盘消息，详情页里收不到拖入事件。
/// 这个类就是补上缺的那一环。三个必须踩准的点：
///
///   1. 命中区域远大于可见岛体：岛体窗口矩形是整个最大窗口尺寸（1000×480 量级），可见岛体只占
///      其中一小块。所以每次拖放都要把坐标交给 Renderer，由它判断落点是否在当前展开的详情页矩形里。
///   2. 坐标口径必须和鼠标点击完全一致：屏幕物理像素 → ScreenToClient → 除以 DPI → 再减 hitTopY。
///      任何一步漏掉，落点都会整体偏移（拖到卡片左边却删掉右边那种）。
///   3. 拖放期间收不到 WM_MOUSEMOVE：鼠标被 OLE 的拖放循环接管了。而详情页有「鼠标移开就收起」的
///      延迟计时，这时若不主动续一口「还悬停着」，面板会在拖放途中把自己收掉，拖放目标当场消失。
/// </summary>
internal sealed class IslandDropTarget : Win32.IDropTarget
{
    private readonly NotchWindow _window;

    /// <summary>本次拖放是否已被详情页接受。没接受就一路拒绝，不做任何转发。</summary>
    private bool _accepted;

    /// <summary>本次拖入的条目数（DragEnter 时解析出来存着，供 DragOver 重试接受时复用）。</summary>
    private int _dragItemCount;

    /// <summary>
    /// 本次拖放真正进入过的详情页实例。
    ///
    /// 单独记这一笔、而不是离场时去问「当前命中页是哪个」：拖放回调是跨线程进来的，
    /// 两次回调之间面板可能已经换页 / 被通知接管 / 收起，那时按当前命中区去取只会取到 null，
    /// 复位回调发不出去，插件的悬停高亮就永远留在详情页上（表现是「一直卡在拖入页面」，
    /// 且此后每次打开面板都还挂着）。记下实例后，只要这一轮进过某个详情页，离场时无条件还给它即可。
    /// </summary>
    private Plugins.IDetailPage? _hoverPage;

    internal IslandDropTarget(NotchWindow window) => _window = window;

        /// <summary>
        /// 光标压在一个「收起态也愿意收文件」的组件上时，把它的详情页展开。
        ///
        /// 为什么 DragEnter 里判一次不够、DragOver 里还得一直判：岛体窗口是整块超大透明窗口
        /// （WINDOW_WIDTH ≥ 1200），可见岛体只占中间一小块，而 OLE 只在「进入窗口」那一刻调一次
        /// DragEnter —— 用户从窗口边缘进来时，那一瞬间的落点离组件还远得很。所以必须靠 DragOver
        /// 在光标真正压到组件上时才动手。
        ///
        /// 展开不等于接受：详情页要下一帧才画出来、命中矩形也是那时才登记，所以调用方这次仍按原样走
        /// （多半被拒），由 DragOver 那套「持续重试接受」在一两帧后接上。
        /// </summary>
        private void TryExpandCollapsedDropWidget(float x, float y)
    {
        if (Renderer.HasActiveDetailPage) return;   // 已经有面板开着，不抢

        string? widgetId = Renderer.FindCollapsedFileDropWidget(x, y);
        if (widgetId == null) return;

        NotchWindow.ExpandPanel(widgetId);
        Logger.Info($"[岛体拖放] 拖到收起态的组件 {widgetId} 上，已自动展开它的详情页");
    }

    /// <summary>
    /// 标记 / 解除「正有文件被拖着经过岛体」。置位期间穿透模式的悬停淡出被压制
    ///（见 Renderer.FileDragInProgress）—— 岛体若淡到全透明，像素就从 OLE 命中测试里消失，
    /// 拖放目标会在拖动途中当场丢失，用户手里的文件再也放不进来。
    ///
    /// 只要这一轮拖放带文件路径就置位，与详情页收不收无关：拒收时同样不该让岛体在光标底下淡走，
    /// 否则用户看到的就是「拖过来，岛没了」。
    /// </summary>
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

        // 上一次拖放若没能正常收尾（拖放源被强杀、进程崩掉之类，DragLeave / Drop 都收不到），
        // 悬停态会一直留在详情页上。新一次拖入之前先清干净，免得「上一次的高亮」粘在这一回上。
        if (_accepted || _hoverPage != null) Renderer.DispatchDetailPageDragLeave(_hoverPage);
        _accepted = false;
        _hoverPage = null;
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

        // 一确认是「拖着文件过来」就先掐掉穿透淡出（不等详情页接不接受）：
        //    岛体一旦在光标底下淡到全透明，它就从 OLE 的命中测试里消失，
        //    这一轮拖放当场作废，而且不会再有第二次 DragEnter 把它接回来。
        SetFileDragInProgress(true);

        // 落点正好压在某个「收起态收文件」的组件上（直接从岛上方向下滑进来的情形）→ 先把它的详情页展开
        TryExpandCollapsedDropWidget(x, y);

        bool accepted = Renderer.DispatchDetailPageDragEnter(x, y, files.Count);
        Logger.Info($"[岛体拖放] 进入：{files.Count} 项，落点 {x:F0},{y:F0}，详情页{(accepted ? "接受" : "拒绝")}");
        if (!accepted) return 0;

        // 被接受之后才续「鼠标还在岛上」：拖放期间 OLE 接管鼠标，窗口收不到
        // WM_MOUSEMOVE / WM_MOUSELEAVE，详情页的延迟折叠会把面板在中途收掉。
        // 放在接受之后而不是一进来就设，是为了不把「拖到岛体透明区、详情页并不接受」的悬停也算进来。
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

            // 拖出了详情页矩形（比如滑到岛体另一头）→ 本次接受作废，光标给「不允许」
            if (!Renderer.DispatchDetailPageDragOver(x, y))
            {
                // 按记下的实例复位 —— 这一下判定为假也可能是「面板已被收起 / 被通知接管」，
                // 那时命中区里已经取不到详情页，退回参数为 null 的旧写法会把高亮留在界面上。
                Renderer.DispatchDetailPageDragLeave(_hoverPage);
                _accepted = false;
            }
            else
            {
                pdwEffect = Win32.DROPEFFECT_COPY;
            }
            return 0;
        }

        // 尚未被接受时，在这里持续重试接受 —— 这一步是必须的：
        //    OLE 只在鼠标「进入窗口」那一刻调一次 DragEnter。用户若是从岛体边缘、
        //    或者上方那段不属于详情页的区域滑进来，那一下的落点就不在详情页矩形里，
        //    于是被拒；之后鼠标再怎么移到详情页上，都不会有第二次 DragEnter，永远接不上。
        //    表现就是「有时候拖入判定有误：怎么拖都不接受」。

        // 同一趟扫描里顺手判一次「光标是不是压到了某个收起态收文件的组件」：
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

        // 判据不能只看 _accepted：中途面板被通知接管 / 收起时，DragOver 的落点判定会失败
        //    并把 _accepted 置回 false，而那一刻命中区里已经没有详情页了 —— 只按 _accepted
        //    决定发不发复位回调，插件的高亮就会永远留在详情页上（「一直卡在拖入页面」就是这么来的）。
        //    所以只要这一轮拖放进过某个详情页（记在 _hoverPage），离场就必须把它复位。
        if (_accepted || _hoverPage != null) Renderer.DispatchDetailPageDragLeave(_hoverPage);
        _accepted = false;
        _hoverPage = null;

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
        var hoverPage = _hoverPage;      // 这一轮进过的详情页（可能已经被收起 / 换页，仍要复位它）
        _accepted = false;
        _hoverPage = null;

        SetFileDragInProgress(false);   // 松手即收官：穿透淡出立刻恢复，不必等下一次状态轮询

        if (!_window.TryScreenToIslandLogical(pt, out float x, out float y))
        {
            if (accepted || hoverPage != null) Renderer.DispatchDetailPageDragLeave(hoverPage);
            return 0;
        }

        // 补一次「进入」尝试：OLE 只在鼠标移动或修饰键变化时才调 DragOver，
        //    用户挪到位就立刻松手的话，两次回调之间可能一次 DragOver 都没有 ——
        //    那样即便落点明明在详情页里，也会因为「这一轮从没被接受过」而白扔。
        //    最典型的新场景：拖到收起态组件上自动展开，面板下一帧才画出来，手快就赶不上。
        //    判定条件和别处完全一致（落点必须在详情页矩形内），所以不会凭空接受。
        if (!accepted && _dragItemCount > 0)
        {
            accepted = Renderer.DispatchDetailPageDragEnter(x, y, _dragItemCount);

            // 走到这里说明这次接受是「补」出来的，前面没有 DragOver 替我们续过悬停 —— 补一口，
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

        // 落点不在详情页矩形内时（最典型：用户在岛体边缘松手，或者拖到一半随手一放），
        //    DispatchDetailPageDrop 会直接返回 false，并且不会回调 OnFilesDragLeave ——
        //    详情页的悬停态就此没人清，高亮永远留在界面上。
        //    表现就是「没拖到位就松手 → 一直卡在拖入页面」。这里兜一刀：
        //    只要这一轮拖放曾被接受过，收尾就必须把悬停态复位（按记下的实例发，面板已收起也照样发）。
        if (!dropped) Renderer.DispatchDetailPageDragLeave(hoverPage);
        else pdwEffect = Win32.DROPEFFECT_COPY;

        Logger.Info($"[岛体拖放] 放下：落点 {x:F0},{y:F0}，详情页{(dropped ? "收下了" : "没收（落点在详情页外），已补复位悬停态")}");
        return 0;
    }
}
