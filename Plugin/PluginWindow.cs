using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 插件自有窗口：Win32 分层窗口 + SkiaSharp 绘制 + 鼠标/键盘输入路由。
/// DPI 感知居中显示，右上角带关闭按钮，Esc 可关闭，置顶以阻止下方交互。
/// 消息通过静态 WndProc + 字典按 hwnd 路由到对应实例，支持多窗口、可重复开关。
/// </summary>
public sealed class PluginWindow : IPluginWindow
{
    private const uint WM_APP_REDRAW = 0x8000 + 1;
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    // WndProc 必须是静态方法（避免委托被 GC 后回调悬空），用字典按 hwnd 找回实例
    private static readonly Dictionary<IntPtr, PluginWindow> _windows = new();
    private static readonly Win32.WndProc _wndProc = WndProc;

    // 对话框外观：圆角背景 + 边框
    private static readonly SKPaint _bgPaint = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true };
    private static readonly SKPaint _borderPaint = new SKPaint { Color = new SKColor(255, 255, 255, 95), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
    private static readonly SKPaint _closeBtnPaint = new SKPaint { Color = new SKColor(255, 255, 255, 30), IsAntialias = true };
    private static readonly SKPaint _closeXPaint = new SKPaint { Color = new SKColor(255, 255, 255, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

    private readonly string _title;
    private readonly int _width, _height;
    private float _dpiScale = 1f;
    private int _scaledWidth, _scaledHeight;
    private IntPtr _hwnd;
    private Action<SKCanvas, int, int>? _draw;
    private Action<float, float>? _mouseDown, _mouseMove, _mouseUp;
    private Action<char>? _key;
    private Action<string[]>? _filesDrop;

    // 拖入悬停：onEnter 点亮高亮 / onOver 跟坐标 / onLeave 复位。与 _filesDrop 各管一头（过程 vs 松手）。
    private Action<int>? _dragEnter;
    private Action<float, float>? _dragOver;
    private Action? _dragLeave;

    /// <summary>
    /// 挂在窗口上的 OLE 拖入目标。非 null 表示走 IDropTarget 这条路径（有悬停回调、有坐标）；
    /// 为 null 且窗口接受了拖放，说明注册失败、退回了 WM_DROPFILES（只有松手回调，没有悬停）。
    /// 同时这个字段是 CCW 的强引用持有者 —— 掉了就可能被 GC 回收。
    /// </summary>
    private WindowDropTarget? _dropTarget;

    /// <summary>本次拖入的文件列表。DragEnter 时解析一次缓存下来，Drop 时直接用，避免重复解析。</summary>
    private List<string>? _dragFiles;

    /// <summary>当前是否有拖入项悬停在窗口上（用来忽略不该来的 DragOver）。</summary>
    private bool _dragHovering;

    /// <summary>DoDragDrop 进行中：此时 HWND 正被拖放循环使用，销毁它会让 OLE 直接踩空。</summary>
    private bool _dragging;
    /// <summary>拖放期间收到过关闭请求 —— 拖放结束后补做，否则窗口就永远关不掉了。</summary>
    private bool _closePending;

    private IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
    private SKSurface? _surface;
    private bool _closing;
    private int _posX, _posY;

    // 归属信息：宿主 + 打开它的插件 Id。
    // 存在的唯一目的是「插件卸载时能被宿主主动关掉」—— 本窗口的 _draw/_mouseDown/_key 是
    // 插件实例方法的委托，会直接引用插件类型；只要窗口还活着，承载它的可回收 ALC 就回收不掉
    // （热重载会持续泄漏旧版本程序集）。所以 Show() 时向宿主登记、销毁时注销。
    private readonly PluginHost? _owner;
    private readonly string? _ownerPluginId;

    /// <summary>创建窗口的线程 Id（同步销毁只能在这个线程上执行）。</summary>
    private int _ownerThreadId;

    /// <summary>无归属窗口（宿主内部 / 测试用）。这类窗口不会被插件卸载路径自动关闭。</summary>
    public PluginWindow(string title, int width, int height)
        : this(null, null, title, width, height) { }

    /// <summary>带回属主的窗口：<paramref name="owner"/> 为宿主，<paramref name="ownerPluginId"/> 为打开它的插件。</summary>
    internal PluginWindow(PluginHost? owner, string? ownerPluginId, string title, int width, int height)
    {
        _owner = owner;
        _ownerPluginId = ownerPluginId;
        _title = title;
        _width = width;
        _height = height;
    }

    public void SetDraw(Action<SKCanvas, int, int>? draw)
    {
        _draw = draw;
        RequestRedraw();
    }

    public void SetMouse(Action<float, float>? down, Action<float, float>? move, Action<float, float>? up)
    {
        _mouseDown = down;
        _mouseMove = move;
        _mouseUp = up;
    }

    public void SetKey(Action<char>? key) => _key = key;

    public void SetFilesDrop(Action<string[]>? onFiles)
    {
        _filesDrop = onFiles;
        // 窗口还没创建时先把回调记着，Show() 里再补注册（那里 hwnd 才有效）
        if (_hwnd != IntPtr.Zero && HasDropCallback()) EnsureDropTarget();
    }

    public void SetDragHover(Action<int>? onEnter, Action<float, float>? onOver, Action? onLeave)
    {
        _dragEnter = onEnter;
        _dragOver = onOver;
        _dragLeave = onLeave;
        if (_hwnd != IntPtr.Zero && HasDropCallback()) EnsureDropTarget();
    }

    /// <summary>是否订阅了任意一个拖入回调。全空时不去注册拖入目标 —— 免得白白让窗口接受拖放。</summary>
    private bool HasDropCallback() =>
        _filesDrop != null || _dragEnter != null || _dragOver != null || _dragLeave != null;

    /// <summary>
    /// 把窗口登记成 OLE 拖入目标（只需要登记一次）。
    ///
    /// 两条路径二选一，<b>不能并存</b>：挂了 IDropTarget 之后，OLE 拖放会走 IDropTarget，
    /// WM_DROPFILES 就不会再投递了（一个窗口同时挂两个只会让「拖入回调」来源变得不可预期）。
    /// 所以这里的策略是：优先 IDropTarget（有悬停反馈），注册失败才退回 DragAcceptFiles（至少还能拖入）。
    /// </summary>
    private void EnsureDropTarget()
    {
        if (_dropTarget != null || _hwnd == IntPtr.Zero) return;

        // RegisterDragDrop 的硬性前提：本线程已完成 OLE 初始化
        if (!Win32.EnsureOleInitialized())
        {
            Logger.Warn("[PluginWindow] OLE 不可用，退回 WM_DROPFILES 拖入（无悬停反馈）");
            Win32.DragAcceptFiles(_hwnd, true);
            return;
        }

        var target = new WindowDropTarget(this);
        int hr = Win32.RegisterDragDrop(_hwnd, target);
        if (hr != 0)
        {
            // 极罕见（OLE 不可用 / 该 hwnd 已被注册过）。退回老路径：能拖入，但没有拖拽过程回调。
            Logger.Warn($"[PluginWindow] RegisterDragDrop 失败：0x{hr:X8}，退回 WM_DROPFILES 拖入（无悬停反馈）");
            Win32.DragAcceptFiles(_hwnd, true);
            return;
        }
        _dropTarget = target;
    }

    /// <summary>窗口销毁前必须注销，否则 OLE 还捏着一个指向已死窗口的接口。</summary>
    private void RevokeDropTarget()
    {
        if (_dropTarget == null) return;
        _dropTarget = null;
        if (_hwnd != IntPtr.Zero) Win32.RevokeDragDrop(_hwnd);
    }

    /// <summary>
    /// 发起一次系统拖放（拖出）。同步阻塞到用户松手 / 取消 —— 由 ole32 的 DoDragDrop 内部接管鼠标与消息循环。
    /// </summary>
    public bool StartDragFiles(IReadOnlyList<string> paths, bool allowMove = false)
    {
        if (_hwnd == IntPtr.Zero || _closing) return false;
        if (paths == null || paths.Count == 0) return false;

        // 过滤掉已不存在的路径：把一条硬盘上已经没有的路径丢进拖放，目标只会报错或者什么都不发生，
        // 不如提前剔除，还能让「全空」这种明显无效的调用短路掉。
        var valid = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                if (File.Exists(p) || Directory.Exists(p)) valid.Add(p);
            }
            catch
            {
                // 含非法字符之类的路径，跳过这一条即可
            }
        }
        if (valid.Count == 0) return false;

        if (!Win32.EnsureOleInitialized()) return false;

        // CF_HDROP 的封装交给 WinForms 的 DataObject（SetData(FileDrop, string[]) 就是它的标准用法），
        // 它实现了 ComTypes.IDataObject，可以直接 marshal 成 DoDragDrop 需要的第一个参数。
        var data = new System.Windows.Forms.DataObject();
        data.SetData(System.Windows.Forms.DataFormats.FileDrop, valid.ToArray());

        uint allowed = allowMove
            ? Win32.DROPEFFECT_COPY | Win32.DROPEFFECT_MOVE
            : Win32.DROPEFFECT_COPY;

        bool accepted = false;
        _dragging = true;
        DragOutState.Enter(_hwnd);   // 标记「从本窗口发起」，免得刚拖出去又被自己接回来
        try
        {
            int hr = Win32.DoDragDrop(data, new FileDropSource(), allowed, out uint effect);
            if (hr != 0 /* S_OK */)
            {
                Logger.Warn($"[PluginWindow] DoDragDrop 失败：0x{hr:X8}");
                return false;
            }
            accepted = effect != 0;
        }
        catch (Exception ex)
        {
            Logger.Error("[PluginWindow] DoDragDrop 异常", ex);
            return false;
        }
        finally
        {
            DragOutState.Exit();
            _dragging = false;
            // 拖放期间若有人请求关窗（插件被卸载 / 热重载），现在补做 —— 否则窗口永远关不掉
            if (_closePending) { _closePending = false; Close(); }
        }
        return accepted;
    }

    // ---- 拖入（IDropTarget 回调，由 WindowDropTarget 转发进来） ----

    /// <summary>
    /// 拖入项第一次进入窗口。只接受「带文件系统路径」的拖入 —— 其他内容（网页文字、画图工具的位图）
    /// 在这里直接拒绝，回 DROPEFFECT_NONE 让系统显示禁止光标，插件也收不到任何回调。
    /// </summary>
    internal int HandleDragEnter(ComTypes.IDataObject? dataObj, Win32.POINT screenPt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;
        if (_closing) return 0;
        // 自己拖出去的东西路过自己，不接 —— 否则用户原地松手会被当成又拖进来一份
        if (DragOutState.IsSelfDrop(_hwnd)) return 0;

        // 顺手解析一次并缓存：Drop 时直接用，省掉第二次解析；条目数也在这里给 onEnter
        _dragFiles = DropPayload.ReadFileDrop(dataObj);
        if (_dragFiles.Count == 0)
        {
            _dragFiles = null;
            return 0;
        }

        pdwEffect = Win32.DROPEFFECT_COPY;
        _dragHovering = true;

        // 插件代码不可信：回调抛异常不能穿透到 OLE 的拖放循环里，否则整个拖放会卡死在半路
        try { _dragEnter?.Invoke(_dragFiles.Count); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入进入回调异常", ex); }

        InvokeDragOver(screenPt);
        return 0;
    }

    /// <summary>鼠标在窗口内移动（高频）。这里只做坐标换算转发，插件端也别做重活。</summary>
    internal int HandleDragOver(Win32.POINT screenPt, ref uint pdwEffect)
    {
        if (!_dragHovering) { pdwEffect = Win32.DROPEFFECT_NONE; return 0; }
        pdwEffect = Win32.DROPEFFECT_COPY;
        InvokeDragOver(screenPt);
        return 0;
    }

    /// <summary>鼠标拖出窗口 / 拖放被取消 —— 让插件把悬停态（高亮之类）复位。</summary>
    internal int HandleDragLeave()
    {
        if (!_dragHovering) return 0;
        _dragHovering = false;
        _dragFiles = null;
        try { _dragLeave?.Invoke(); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入离开回调异常", ex); }
        return 0;
    }

    /// <summary>用户在窗口内松手。先复位悬停态、再通知插件「放下了什么」。</summary>
    internal int HandleDrop(ComTypes.IDataObject? dataObj, Win32.POINT screenPt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        var files = _dragFiles;
        _dragHovering = false;
        _dragFiles = null;

        // DragEnter 没缓存到（比如窗口是在拖放中途才挂上目标的）时补解析一次
        if ((files == null || files.Count == 0) && dataObj != null)
            files = DropPayload.ReadFileDrop(dataObj);

        if (files == null || files.Count == 0) return 0;
        pdwEffect = Win32.DROPEFFECT_COPY;

        // 松手前最后发一次 onOver 报出准确落点：插件只要把 onOver 的坐标记在字段里，
        // 就能在紧接着的 onFiles 里知道「用户是在哪个位置松的手」（比如按落点决定插到第几项）。
        // 依赖「Drop 之前系统一定先发过 DragOver」是个时序假设，这里主动补一次，插件就不用赌。
        InvokeDragOver(screenPt);

        // 顺序很重要：先把悬停态收掉，再报「放下了什么」。反过来的话插件在 onLeave 里复位高亮，
        // 会把 Drop 时刚设好的状态一起抹掉。顺带一提，拖到一半按 Esc 取消是不会有 Drop 的，只有 DragLeave。
        try { _dragLeave?.Invoke(); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入离开回调异常", ex); }

        if (_filesDrop == null) return 0;
        try { _filesDrop(files.ToArray()); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件文件拖入回调异常", ex); }
        return 0;
    }

    /// <summary>
    /// 把 OLE 给的屏幕物理坐标转成窗口内的逻辑坐标再转发（坐标原点、缩放都和 SetMouse 一致，
    /// 插件可以拿它直接把落点画出来）。
    /// </summary>
    private void InvokeDragOver(Win32.POINT screenPt)
    {
        if (_dragOver == null) return;

        var p = new Win32.POINT(screenPt.x, screenPt.y);
        Win32.ScreenToClient(_hwnd, ref p);
        float lx = p.x / _dpiScale, ly = p.y / _dpiScale;

        try { _dragOver(lx, ly); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入悬停回调异常", ex); }
    }

    /// <summary>
    /// 处理 WM_DROPFILES —— 只在 IDropTarget 注册失败时的**回退路径**上才会收到。
    /// ⚠️ 无论有没有订阅回调、中途是否抛异常，都必须 DragFinish，否则系统分配的那块内存不会归还。
    /// </summary>
    private void HandleFilesDrop(IntPtr hDrop)
    {
        List<string> files;
        try { files = DropPayload.ReadDropPaths(hDrop); }
        finally { Win32.DragFinish(hDrop); }

        if (files.Count == 0 || _filesDrop == null) return;
        try { _filesDrop(files.ToArray()); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件文件拖入回调异常", ex); }
    }

    public void RequestRedraw()
    {
        if (_hwnd != IntPtr.Zero && !_closing)
            Win32.PostMessage(_hwnd, WM_APP_REDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 同步销毁窗口（走与 WM_CLOSE 完全相同的清理路径：释放 DIB/SKSurface → DestroyWindow → 摘除登记表）。
    ///
    /// 为什么需要它：插件卸载时要立刻切断「窗口 → 插件方法委托 → 插件类型 → ALC」这条引用链，
    /// 而 <see cref="Close"/> 只是 PostMessage，消息要等宿主回到消息循环才处理 ——
    /// 卸载路径随后马上就做的那几轮同步 GC 会因此判定「加载上下文仍未被回收」。
    ///
    /// 只能在创建窗口的那个线程上调用（DestroyWindow 的硬性要求）。非同线程返回 false，
    /// 由调用方回退到 <see cref="Close"/>。
    /// </summary>
    internal bool TryDestroyNow()
    {
        if (_hwnd == IntPtr.Zero) return true;
        if (_dragging) return false;   // 拖放循环还在用这个 HWND，调用方会回退到 Close()（走延迟关闭）
        if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;

        var hwnd = _hwnd;
        _closing = true;
        RevokeDropTarget();       // 同 WM_CLOSE：销毁之前先摘掉 OLE 那边的登记
        CleanupBuffer();          // 先放掉 SKSurface + DIB，再让 WM_DESTROY 摘登记表
        Win32.DestroyWindow(hwnd);
        if (_hwnd != IntPtr.Zero) _hwnd = IntPtr.Zero;
        return true;
    }

    /// <summary>创建窗口并注册到消息路由表。</summary>
    public void Show()
    {
        var wc = new Win32.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
            lpszClassName = "NPSPluginWindow",
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
        };
        if (Win32.RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* CLASS_ALREADY_EXISTS */)
            return;

        // DPI 感知：尺寸按系统 DPI 缩放，居中于主屏工作区（物理像素），并兼容无主屏场景
        _dpiScale = Win32.GetDpiForSystem() / 96f;
        _scaledWidth = (int)(_width * _dpiScale);
        _scaledHeight = (int)(_height * _dpiScale);
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, _scaledWidth, _scaledHeight);
        int x = area.Left + (area.Width - _scaledWidth) / 2;
        int y = area.Top + (area.Height - _scaledHeight) / 2;

        // 置顶（阻止下方交互）+ 工具窗口（不进任务栏）+ 分层（透明绘制）
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST,
            "NPSPluginWindow", _title,
            Win32.WS_POPUP | Win32.WS_VISIBLE,
            x, y, _scaledWidth, _scaledHeight,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;
        lock (_windows) _windows[_hwnd] = this;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        // 向宿主登记归属：插件被卸载时宿主会把这些窗口逐个关掉（见 PluginHost.UnregisterPlugin）
        if (_owner != null && _ownerPluginId != null)
            _owner.AttachWindow(_ownerPluginId, this);
        _posX = x;
        _posY = y;
        // 插件可能在 Show() 之前就订阅过拖放回调，此时 hwnd 才有效，补上注册
        if (HasDropCallback()) EnsureDropTarget();
        Win32.SetForegroundWindow(_hwnd); // 激活窗口，让 Esc/键盘输入立即生效
        InitBuffer();
        Redraw();
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        lock (_windows)
        {
            if (_windows.TryGetValue(hwnd, out var self))
                return self.HandleMessage(hwnd, msg, wParam, lParam);
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REDRAW:
                Redraw();
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                _mouseMove?.Invoke(LogicalX(lParam), LogicalY(lParam));
                return IntPtr.Zero;
            case Win32.WM_LBUTTONDOWN:
            {
                float lx = LogicalX(lParam), ly = LogicalY(lParam);
                if (IsCloseButtonHit(lx, ly)) { Close(); return IntPtr.Zero; }
                _mouseDown?.Invoke(lx, ly);
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONUP:
                _mouseUp?.Invoke(LogicalX(lParam), LogicalY(lParam));
                return IntPtr.Zero;
            case Win32.WM_DROPFILES:
                HandleFilesDrop(wParam); // wParam 就是 HDROP 句柄
                return IntPtr.Zero;
            case WM_CHAR:
                _key?.Invoke((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            case WM_KEYDOWN:
                if (wParam.ToInt64() == VK_ESCAPE) { Close(); return IntPtr.Zero; }
                break;

            case Win32.WM_CLOSE:
                // 拖放循环正在用这个 HWND，此时销毁会让 OLE 踩空崩溃；先记下来，等 DoDragDrop 返回再关。
                if (_dragging) { _closePending = true; return IntPtr.Zero; }
                _closing = true;
                RevokeDropTarget();   // 必须在 DestroyWindow 之前：OLE 那边还捏着指向本窗口的接口
                CleanupBuffer();
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                RevokeDropTarget();   // 兜底：窗口被外部销毁时也把拖入目标摘掉
                lock (_windows) _windows.Remove(hwnd);
                // 从宿主的归属表中摘除，避免宿主列表随「用户手动关窗」无限增长
                if (_owner != null && _ownerPluginId != null)
                    _owner.DetachWindow(_ownerPluginId, this);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private float LogicalX(IntPtr lParam) => Lo(lParam) / _dpiScale;
    private float LogicalY(IntPtr lParam) => Hi(lParam) / _dpiScale;
    private bool IsCloseButtonHit(float x, float y)
    {
        float dx = x - (_width - 18), dy = y - 16;
        return dx * dx + dy * dy <= 12f * 12f; // 圆形命中区域
    }

    private static int Lo(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int Hi(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private void Redraw()
    {
        if (_surface == null || _draw == null) return;
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_dpiScale); // 让插件按逻辑坐标绘制

        // 圆角背景
        var bg = new SKRoundRect(new SKRect(0, 0, _width, _height), 14f);
        canvas.DrawRoundRect(bg, _bgPaint);

        // 插件内容裁剪到圆角内，避免四角溢出
        canvas.Save();
        canvas.ClipRoundRect(bg, antialias: true);
        _draw(canvas, _width, _height);
        canvas.Restore();

        // 边框绘制在内容之上，始终可见（内缩半线宽避免被窗口边缘裁掉）
        var border = new SKRoundRect(new SKRect(0.75f, 0.75f, _width - 0.75f, _height - 0.75f), 13f);
        canvas.DrawRoundRect(border, _borderPaint);

        DrawCloseButton(canvas);
        canvas.Restore();

        var screenDc = Win32.GetDC(IntPtr.Zero);
        var ptSrc = new Win32.POINT(0, 0);
        var ptDst = new Win32.POINT { x = _posX, y = _posY };
        var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA
        };
        Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void DrawCloseButton(SKCanvas canvas)
    {
        float cx = _width - 18, cy = 16, r = 10;
        canvas.DrawCircle(cx, cy, r, _closeBtnPaint);
        canvas.DrawLine(cx - 4, cy - 4, cx + 4, cy + 4, _closeXPaint);
        canvas.DrawLine(cx + 4, cy - 4, cx - 4, cy + 4, _closeXPaint);
    }

    private void InitBuffer()
    {
        var screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                biWidth = _scaledWidth,
                biHeight = -_scaledHeight,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
        var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _pBits, _scaledWidth * 4);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void CleanupBuffer()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
        if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
    }
}

/// <summary>
/// 最小的拖放源实现：只回答系统两个问题 —— 这次拖放何时结束、拖拽时显示什么光标。
/// 之所以必须有它，是因为 DoDragDrop 的第二个参数就是 IDropSource，系统完全靠它判断拖放的生命周期。
/// 我们不做自定义拖拽缩略图，一律让系统用默认光标（观感就和资源管理器拖文件一样）。
/// </summary>
internal sealed class FileDropSource : Win32.IDropSource
{
    public int QueryContinueDrag(bool fEscapePressed, uint grfKeyState)
    {
        if (fEscapePressed) return Win32.DRAGDROP_S_CANCEL;                      // 用户按了 Esc
        if ((grfKeyState & Win32.MK_LBUTTON) == 0) return Win32.DRAGDROP_S_DROP; // 左键已松开 = 用户放下了
        return 0; // S_OK → 继续拖
    }

    public int GiveFeedback(uint dwEffect) => Win32.DRAGDROP_S_USEDEFAULTCURSORS;
}

/// <summary>
/// 窗口的 OLE 拖入目标（IDropTarget）：把系统发来的四个拖放回调转给 <see cref="PluginWindow"/> 处理。
///
/// 为什么单独拆一个类，不直接让 PluginWindow 实现：接口方法的实现必须是 public，
/// 塞进 PluginWindow 会让它表面上看多出一堆拖放公开 API；而这个类是 internal，
/// 那些方法也就只在本程序集内可见，插件拿到的仍然只有 IPluginWindow 那几个成员。
/// </summary>
internal sealed class WindowDropTarget : Win32.IDropTarget
{
    private readonly PluginWindow _window;

    internal WindowDropTarget(PluginWindow window) => _window = window;

    public int DragEnter(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
        => _window.HandleDragEnter(dataObj, pt, ref pdwEffect);

    public int DragOver(uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
        => _window.HandleDragOver(pt, ref pdwEffect);

    public int DragLeave() => _window.HandleDragLeave();

    public int Drop(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
        => _window.HandleDrop(dataObj, pt, ref pdwEffect);
}
