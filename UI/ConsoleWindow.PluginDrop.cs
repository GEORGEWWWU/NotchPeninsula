using System.IO;
using SkiaSharp;
using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ---- 插件中心：把 DLL 拖进来即导入 ----

        private readonly List<string> _pluginDropDlls = new();

        private bool _pluginDropHovering;

        private Win32.IDropTarget? _pluginDropTarget;

        private string _pluginHint = "";

        private bool _pluginHintIsError;

        private static SKRect GetPluginDropZone()
            => new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + CARD_TOP_Y, WIDTH - CONTENT_RM, HEIGHT - 20f);

        // 登记 / 注销

        private void SetupPluginDropTarget()
        {
            try
            {
                if (_pluginDropTarget != null || _hwnd == IntPtr.Zero) return;

                if (!Win32.EnsureOleInitialized())
                {
                    Logger.Warn("[PluginCenter] OLE 不可用，拖入 DLL 已禁用");
                    return;
                }

                var target = new ConsoleDropTarget(this);
                int hr = Win32.RegisterDragDrop(_hwnd, target);
                if (hr != 0)
                {
                    Logger.Warn($"[PluginCenter] RegisterDragDrop 失败：0x{hr:X8}，拖入 DLL 已禁用");
                    return;
                }

                _pluginDropTarget = target;
                Logger.Info("[PluginCenter] 拖放目标已就绪（插件中心可拖入 DLL）");
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 拖放登记异常", ex);
            }
        }

        private void RevokePluginDropTarget()
        {
            if (_pluginDropTarget == null) return;
            _pluginDropTarget = null;
            try { Win32.RevokeDragDrop(_hwnd); } catch { /* 窗口已销毁 */ }
        }

        internal bool HandlePluginDragEnter(ComTypes.IDataObject? dataObj, Win32.POINT screenPt)
        {
            _pluginHint = "";          // 新一次拖入开始，上一次的结果提示先清掉
            _pluginDropDlls.Clear();

            var files = DropPayload.ReadFileDrop(dataObj);
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) _pluginDropDlls.Add(f);
            }
            if (_pluginDropDlls.Count == 0) return false;

            return UpdatePluginDropHover(screenPt);
        }

        internal bool HandlePluginDragOver(Win32.POINT screenPt)
        {
            if (_pluginDropDlls.Count == 0) return false;
            return UpdatePluginDropHover(screenPt);
        }

        internal void HandlePluginDragLeave()
        {
            _pluginDropDlls.Clear();
            SetPluginDropHovering(false);
        }

        internal bool HandlePluginDrop(Win32.POINT screenPt)
        {
            //    也会因为「这一轮从没被接受过」而白扔。
            bool accepted = _pluginDropDlls.Count > 0 && UpdatePluginDropHover(screenPt);

            var dlls = _pluginDropDlls.ToList();
            _pluginDropDlls.Clear();
            SetPluginDropHovering(false);   // 松手即收官：高亮立刻开始淡出，不必等下一次状态轮询

            if (!accepted) return false;
            ImportPluginDlls(dlls);
            return true;
        }

        // 悬停判定与动画

        private bool TryScreenToClientDips(Win32.POINT screenPt, out float x, out float y)
        {
            x = 0; y = 0;
            if (_hwnd == IntPtr.Zero) return false;
            if (!Win32.GetWindowRect(_hwnd, out var rect)) return false;
            x = (screenPt.x - rect.Left) / _dpiScale;
            y = (screenPt.y - rect.Top) / _dpiScale;
            return true;
        }

        private bool UpdatePluginDropHover(Win32.POINT screenPt)
        {
            bool hover = false;
            if (_selectedTab == 6 && TryScreenToClientDips(screenPt, out float x, out float y))
                hover = GetPluginDropZone().Contains(x, y);

            SetPluginDropHovering(hover);
            return hover;
        }

        private void SetPluginDropHovering(bool hover)
        {
            if (_pluginDropHovering == hover) return;
            _pluginDropHovering = hover;
            Render();   // 静态反馈：状态一变就立刻重绘，没有动画、也不需要定时器
        }

        // 导入

        private void ImportPluginDlls(List<string> dlls)
        {
            try
            {
                var mgr = PluginManager.Instance;
                int okCount = 0;
                string lastMsg = "";

                foreach (var path in dlls)
                {
                    var (ok, msg) = mgr.Import(path);
                    lastMsg = msg;
                    if (ok) okCount++;
                    Logger.Info($"[PluginCenter] 拖入导入 {Path.GetFileName(path)}：{(ok ? "成功" : "失败")} — {msg}");
                }

                if (okCount == dlls.Count)
                    _pluginHint = "";
                else if (okCount > 0)
                    _pluginHint = $"{dlls.Count - okCount} 个导入失败：{lastMsg}";
                else
                    _pluginHint = $"导入失败：{lastMsg}";

                _pluginHintIsError = okCount != dlls.Count;

                ResetPluginHover();
                RefreshPluginView();
                if (okCount != dlls.Count) ShowPluginLoadFailedDialog(render: false);
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 拖入导入插件异常", ex);
            }
        }
    }

internal sealed class ConsoleDropTarget : Win32.IDropTarget
{
    private readonly ConsoleWindow _window;

    internal ConsoleDropTarget(ConsoleWindow window) => _window = window;

    public int DragEnter(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;
        if (_window.HandlePluginDragEnter(dataObj, pt)) pdwEffect = Win32.DROPEFFECT_COPY;
        return 0;
    }

    public int DragOver(uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;
        if (_window.HandlePluginDragOver(pt)) pdwEffect = Win32.DROPEFFECT_COPY;
        return 0;
    }

    public int DragLeave()
    {
        _window.HandlePluginDragLeave();
        return 0;
    }

    public int Drop(ComTypes.IDataObject dataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect)
    {
        pdwEffect = Win32.DROPEFFECT_NONE;
        if (_window.HandlePluginDrop(pt)) pdwEffect = Win32.DROPEFFECT_COPY;
        return 0;
    }
}
}