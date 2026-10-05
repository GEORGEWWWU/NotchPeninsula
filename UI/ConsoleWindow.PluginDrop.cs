using System.IO;
using SkiaSharp;
using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ---- 插件中心：把 DLL 拖进来即导入 ----
        //
        // 交互：从资源管理器把 *.dll 拖到「插件中心」右侧的内容区，松手即走 PluginManager.Import()。
        // 拖动经过时内容区立刻亮起一层蓝色反馈 + 提示「松开鼠标以导入插件 DLL」，让用户明确知道「这里能放」。
        // 反馈是直接亮 / 直接灭的静态高亮，不带任何淡入淡出或呼吸动画（也没有定时器）。
        //
        // 为什么要 IDropTarget 而不是 WM_DROPFILES：后者只在松手那一刻投递一次消息，
        // 拖动过程中窗口收不到任何通知，做不了「拖过来就高亮」这类悬停反馈。两者的取舍与
        // PluginWindow 里那套完全一致（挂上 IDropTarget 后 WM_DROPFILES 就不再投递，二选一）。

        /// <summary>本轮拖放中解析出的 DLL 路径（DragEnter 解析一次，Drop 时直接取用）。</summary>
        private readonly List<string> _pluginDropDlls = new();

        /// <summary>光标是否正停在右侧拖放区内 —— 为 true 时内容区整体亮起蓝色反馈。</summary>
        private bool _pluginDropHovering;

        /// <summary>OLE 那边持有的引用：不存一份就会被 GC 掉，拖放回调随之失效。</summary>
        private Win32.IDropTarget? _pluginDropTarget;

        /// <summary>最近一次拖入导入的结果（空串 = 没有可提示的）。画在列表卡右上角。</summary>
        private string _pluginHint = "";

        private bool _pluginHintIsError;

        /// <summary>
        /// 右侧拖放区（DIP 坐标）= 插件页两张卡片的整体范围。
        /// 左侧 0..200 是页签栏，不属于「右边区域」，不参与拖放。
        /// 渲染高亮与命中判定共用本方法，改一处即两处同时生效。
        /// </summary>
        private static SKRect GetPluginDropZone()
            => new SKRect(200f, TITLE_BAR_HEIGHT + 12f, WIDTH - 20f, HEIGHT - 20f);

        // 登记 / 注销

        /// <summary>
        /// 把设置窗口登记成 OLE 拖入目标。只登记一次；失败也只是「插件中心不能拖入」，
        /// 不影响窗口的任何既有功能，所以整段包在 try 里（与 NotchWindow 的做法一致）。
        /// </summary>
        private void SetupPluginDropTarget()
        {
            try
            {
                if (_pluginDropTarget != null || _hwnd == IntPtr.Zero) return;

                // RegisterDragDrop 的硬性前提：本线程已完成 OLE 初始化
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

        /// <summary>窗口销毁前必须注销，否则 OLE 还捏着一个指向已死窗口的接口。</summary>
        private void RevokePluginDropTarget()
        {
            if (_pluginDropTarget == null) return;
            _pluginDropTarget = null;
            try { Win32.RevokeDragDrop(_hwnd); } catch { /* 窗口已销毁 */ }
        }

        // 拖放回调（由 ConsoleDropTarget 转发）

        /// <summary>
        /// 拖入项第一次进入窗口。只认「带文件系统路径的 *.dll」—— 其它内容（网页文字、位图流、
        /// exe/zip 之类）在这里直接拒绝，回 DROPEFFECT_NONE 让系统显示禁止光标。
        /// </summary>
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

        /// <summary>
        /// 鼠标在窗口内移动（高频）。OLE 只在「进入窗口」那一刻调一次 DragEnter ——
        /// 用户若从侧边栏或窗口边缘滑进来，那一下的落点还不在右侧内容区里；之后鼠标再怎么移过来，
        /// 都不会有第二次 DragEnter。所以这里必须持续重判，否则表现就是「怎么拖都不接受」。
        /// </summary>
        internal bool HandlePluginDragOver(Win32.POINT screenPt)
        {
            if (_pluginDropDlls.Count == 0) return false;
            return UpdatePluginDropHover(screenPt);
        }

        /// <summary>鼠标拖出窗口 / 拖放被取消：收起高亮，避免「上一次的蓝色」粘在这一回上。</summary>
        internal void HandlePluginDragLeave()
        {
            _pluginDropDlls.Clear();
            SetPluginDropHovering(false);
        }

        /// <summary>用户在窗口内松手。落点在右侧内容区且拖的是 DLL 才真正导入。</summary>
        internal bool HandlePluginDrop(Win32.POINT screenPt)
        {
            // 补一次命中：OLE 只在鼠标移动或修饰键变化时才调 DragOver，
            //    挪到位就立刻松手的话可能一次 DragOver 都没有 —— 那样即便落点明明在内容区里，
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

        /// <summary>把拖放的屏幕物理坐标换算成窗口客户区 DIP 坐标（与鼠标点击同一套口径）。</summary>
        private bool TryScreenToClientDips(Win32.POINT screenPt, out float x, out float y)
        {
            x = 0; y = 0;
            if (_hwnd == IntPtr.Zero) return false;
            if (!Win32.GetWindowRect(_hwnd, out var rect)) return false;
            x = (screenPt.x - rect.Left) / _dpiScale;
            y = (screenPt.y - rect.Top) / _dpiScale;
            return true;
        }

        /// <summary>按当前落点重算悬停态：只有「插件页 + 落在右侧内容区」才算进入拖放区。</summary>
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

        /// <summary>把拖入的 DLL 逐个交给 PluginManager.Import()，并把结果记成列表卡右上角的提示。</summary>
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

                // 提示文案：
                // · 全成功 → 「（☑️）已成功导入 X 个插件」。前面那颗勾选框是彩色 Emoji，得走 DrawTextWithEmoji 逐段回退才画得出来。
                //     以前只贴最后一条 Import() 的返回串（「已导入并加载：中文名」），拖三个也只看得到一个名字。
                // · 有失败 → 在结论后面补「N 个失败：<原因>」。颜色随结果走（见 _pluginHintIsError），
                //     所以半个成功也会是绿的 —— 这是「至少成了一些」的语义，失败明细照样在提示里和日志里。
                // · 全失败 → 保留 Bot 返回的具体原因，那才是排查要的信息，统一成「导入失败」反而抹掉线索。
                if (okCount > 0 && okCount == dlls.Count)
                    _pluginHint = $"（☑️）已成功导入 {okCount} 个插件";
                else if (okCount > 0)
                    _pluginHint = $"（☑️）已成功导入 {okCount} 个插件，{dlls.Count - okCount} 个失败：{lastMsg}";
                else
                    _pluginHint = lastMsg;

                _pluginHintIsError = okCount == 0;

                ResetPluginHover();
                RefreshPluginView();
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 拖入导入插件异常", ex);
            }
        }
    }

/// <summary>
/// 设置窗口的 OLE 拖入目标（IDropTarget）：把系统发来的四个拖放回调转给 ConsoleWindow 处理。
///
/// 为什么单独拆一个类而不让 ConsoleWindow 直接实现：接口方法必须是 public，
/// 塞进 ConsoleWindow 会让它表面上看多出一堆拖放公开 API；这个类是 internal，
/// 那些方法也就只在本程序集内可见。做法与 PluginWindow 的 WindowDropTarget 完全一致。
/// </summary>
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