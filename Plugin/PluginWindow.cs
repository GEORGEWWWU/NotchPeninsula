using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula.Plugins;

public sealed class PluginWindow : IPluginWindow
{
    private const uint WM_APP_REDRAW = 0x8000 + 1;
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

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

    private Action<int>? _dragEnter;
    private Action<float, float>? _dragOver;
    private Action? _dragLeave;

    private WindowDropTarget? _dropTarget;

    private List<string>? _dragFiles;

    private bool _dragHovering;

    private bool _dragging;
    private bool _closePending;

    private IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
    private SKSurface? _surface;

    private SKPath? _roundRectPath;

    private bool _closing;
    private int _posX, _posY;

    // 归属信息：宿主 + 打开它的插件 Id。
    private readonly PluginHost? _owner;
    private readonly string? _ownerPluginId;

    private int _ownerThreadId;

    public PluginWindow(string title, int width, int height)
        : this(null, null, title, width, height) { }

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
        if (_hwnd != IntPtr.Zero && HasDropCallback()) EnsureDropTarget();
    }

    public void SetDragHover(Action<int>? onEnter, Action<float, float>? onOver, Action? onLeave)
    {
        _dragEnter = onEnter;
        _dragOver = onOver;
        _dragLeave = onLeave;
        if (_hwnd != IntPtr.Zero && HasDropCallback()) EnsureDropTarget();
    }

    private bool HasDropCallback() =>
        _filesDrop != null || _dragEnter != null || _dragOver != null || _dragLeave != null;

    internal void DetachPluginCallbacks()
    {
        _draw = null;
        _mouseDown = null; _mouseMove = null; _mouseUp = null;
        _key = null;
        _filesDrop = null;
        _dragEnter = null; _dragOver = null; _dragLeave = null;
        _closing = true;
    }

    internal bool IsDestroyed => _hwnd == IntPtr.Zero;

    private void EnsureDropTarget()
    {
        if (_dropTarget != null || _hwnd == IntPtr.Zero) return;

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
            Logger.Warn($"[PluginWindow] RegisterDragDrop 失败：0x{hr:X8}，退回 WM_DROPFILES 拖入（无悬停反馈）");
            Win32.DragAcceptFiles(_hwnd, true);
            return;
        }
        _dropTarget = target;
    }

    private void RevokeDropTarget()
    {
        if (_dropTarget == null) return;
        _dropTarget = null;
        if (_hwnd != IntPtr.Zero) Win32.RevokeDragDrop(_hwnd);
    }

    public bool StartDragFiles(IReadOnlyList<string> paths, bool allowMove = false)
    {
        if (_hwnd == IntPtr.Zero || _closing) return false;
        if (paths == null || paths.Count == 0) return false;

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
            if (_closePending) { _closePending = false; Close(); }
        }
        return accepted;
    }

    internal int HandleDragEnter(ComTypes.IDataObject? dataObj, Win32.POINT screenPt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;
        if (_closing) return 0;
        if (DragOutState.IsSelfDrop(_hwnd)) return 0;

        _dragFiles = DropPayload.ReadFileDrop(dataObj);
        if (_dragFiles.Count == 0)
        {
            _dragFiles = null;
            return 0;
        }

        pdwEffect = Win32.DROPEFFECT_COPY;
        _dragHovering = true;

        try { _dragEnter?.Invoke(_dragFiles.Count); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入进入回调异常", ex); }

        InvokeDragOver(screenPt);
        return 0;
    }

    internal int HandleDragOver(Win32.POINT screenPt, ref uint pdwEffect)
    {
        if (!_dragHovering) { pdwEffect = Win32.DROPEFFECT_NONE; return 0; }
        pdwEffect = Win32.DROPEFFECT_COPY;
        InvokeDragOver(screenPt);
        return 0;
    }

    internal int HandleDragLeave()
    {
        if (!_dragHovering) return 0;
        _dragHovering = false;
        _dragFiles = null;
        try { _dragLeave?.Invoke(); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入离开回调异常", ex); }
        return 0;
    }

    internal int HandleDrop(ComTypes.IDataObject? dataObj, Win32.POINT screenPt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;

        var files = _dragFiles;
        _dragHovering = false;
        _dragFiles = null;

        if ((files == null || files.Count == 0) && dataObj != null)
            files = DropPayload.ReadFileDrop(dataObj);

        if (files == null || files.Count == 0) return 0;
        pdwEffect = Win32.DROPEFFECT_COPY;

        InvokeDragOver(screenPt);

        try { _dragLeave?.Invoke(); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入离开回调异常", ex); }

        if (_filesDrop == null) return 0;
        try { _filesDrop(files.ToArray()); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件文件拖入回调异常", ex); }
        return 0;
    }

    private void InvokeDragOver(Win32.POINT screenPt)
    {
        if (_dragOver == null) return;

        var p = new Win32.POINT(screenPt.x, screenPt.y);
        Win32.ScreenToClient(_hwnd, ref p);
        float lx = p.x / _dpiScale, ly = p.y / _dpiScale;

        try { _dragOver(lx, ly); }
        catch (Exception ex) { Logger.Error("[PluginWindow] 插件拖入悬停回调异常", ex); }
    }

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

        _dpiScale = Win32.GetDpiForSystem() / 96f;
        _scaledWidth = (int)(_width * _dpiScale);
        _scaledHeight = (int)(_height * _dpiScale);
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, _scaledWidth, _scaledHeight);
        int x = area.Left + (area.Width - _scaledWidth) / 2;
        int y = area.Top + (area.Height - _scaledHeight) / 2;

        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST,
            "NPSPluginWindow", _title,
            Win32.WS_POPUP | Win32.WS_VISIBLE,
            x, y, _scaledWidth, _scaledHeight,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;
        lock (_windows) _windows[_hwnd] = this;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        if (_owner != null && _ownerPluginId != null)
            _owner.AttachWindow(_ownerPluginId, this);
        _posX = x;
        _posY = y;
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
                if (_dragging) { _closePending = true; return IntPtr.Zero; }
                _closing = true;
                RevokeDropTarget();   // 必须在 DestroyWindow 之前：OLE 那边还捏着指向本窗口的接口
                CleanupBuffer();
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                RevokeDropTarget();   // 兜底：窗口被外部销毁时也把拖入目标摘掉
                lock (_windows) _windows.Remove(hwnd);
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

        var rrPath = _roundRectPath;
        if (rrPath == null) return;

        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_dpiScale); // 让插件按逻辑坐标绘制

        rrPath.Rewind();
        rrPath.AddRoundRect(new SKRect(0, 0, _width, _height), 14f, 14f);
        canvas.DrawPath(rrPath, _bgPaint);

        // 插件内容裁剪到圆角内，避免四角溢出
        canvas.Save();
        canvas.ClipPath(rrPath, SKClipOperation.Intersect, antialias: true);
        _draw(canvas, _width, _height);
        canvas.Restore();

        rrPath.Rewind();
        rrPath.AddRoundRect(new SKRect(0.75f, 0.75f, _width - 0.75f, _height - 0.75f), 13f, 13f);
        canvas.DrawPath(rrPath, _borderPaint);

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
        _roundRectPath = new SKPath();
        Win32.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void CleanupBuffer()
    {
        _surface?.Dispose();
        _surface = null;
        _roundRectPath?.Dispose();
        _roundRectPath = null;
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
        if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
        _hBitmap = _memDc = _oldBitmap = _pBits = IntPtr.Zero;
    }
}

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
