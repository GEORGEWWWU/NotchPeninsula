using System.IO;
using System.IO.Compression;   // ZipFile.ExtractToDirectory：UseWPF 工程的隐式 using 里没有它
using System.Text.Json;
using SkiaSharp;
using NotchPeninsula.Plugins;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ---- 插件中心：把 DLL 或插件包（ZIP）拖进来即安装 ----
        //
        // 两种来源分开收：dll 走既有的 Import（单个文件复制到 plugins 根目录），
        // zip 当「插件包」装进 plugins\<名字>\ 子目录 —— 带附属文件的插件
        //（比如 NpsMediaCdp 需要 dll + helper exe）只能这样整包装。

        private readonly List<string> _pluginDropDlls = new();

        private readonly List<string> _pluginDropZips = new();

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
                    Logger.Warn("[PluginCenter] OLE 不可用，拖入安装插件已禁用");
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
                Logger.Info("[PluginCenter] 拖放目标已就绪（插件中心可拖入 DLL / ZIP）");
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
            _pluginDropZips.Clear();

            var files = DropPayload.ReadFileDrop(dataObj);
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;

                if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) _pluginDropDlls.Add(f);
                else if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) _pluginDropZips.Add(f);
            }
            if (_pluginDropDlls.Count == 0 && _pluginDropZips.Count == 0) return false;

            // 任意页签拖入都自动跳到插件中心：拖 DLL 进来就是奔着导入去的，
            // 不然用户得先松手、再自己切页签。切页签的收尾跟点侧栏走同一套。
            if (_selectedTab != 6)
            {
                _selectedTab = 6;
                CloseMarketDialog();
                _marketCategoryOpen = false;
                _marketSearchFocused = false;
                CloseAllDropdowns();
                Render();   // 悬停态可能不变（false→false），SetPluginDropHovering 不会替这里重绘
            }

            return UpdatePluginDropHover(screenPt);
        }

        internal bool HandlePluginDragOver(Win32.POINT screenPt)
        {
            if (_pluginDropDlls.Count == 0 && _pluginDropZips.Count == 0) return false;
            return UpdatePluginDropHover(screenPt);
        }

        internal void HandlePluginDragLeave()
        {
            _pluginDropDlls.Clear();
            _pluginDropZips.Clear();
            SetPluginDropHovering(false);
        }

        internal bool HandlePluginDrop(Win32.POINT screenPt)
        {
            //    也会因为「这一轮从没被接受过」而白扔。
            bool accepted = (_pluginDropDlls.Count > 0 || _pluginDropZips.Count > 0)
                            && UpdatePluginDropHover(screenPt);

            var dlls = _pluginDropDlls.ToList();
            var zips = _pluginDropZips.ToList();
            _pluginDropDlls.Clear();
            _pluginDropZips.Clear();
            SetPluginDropHovering(false);   // 松手即收官：高亮立刻开始淡出，不必等下一次状态轮询

            if (!accepted) return false;

            if (dlls.Count > 0) ImportPluginDlls(dlls);
            if (zips.Count > 0) ImportPluginZips(zips);
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

        // ---- 拖入 ZIP：当「插件包」整包装进 plugins\<名字>\ ----

        private void ImportPluginZips(List<string> zips)
        {
            try
            {
                int okCount = 0;
                string lastMsg = "";

                foreach (string path in zips)
                {
                    var (ok, msg) = InstallPluginPackage(path);
                    lastMsg = msg;
                    if (ok) okCount++;
                    Logger.Info($"[PluginCenter] 拖入安装 {Path.GetFileName(path)}：{(ok ? "成功" : "失败")} — {msg}");
                }

                if (okCount == zips.Count)
                    _pluginHint = "";
                else if (okCount > 0)
                    _pluginHint = $"{zips.Count - okCount} 个安装失败：{lastMsg}";
                else
                    _pluginHint = $"安装失败：{lastMsg}";

                _pluginHintIsError = okCount != zips.Count;

                ResetPluginHover();
                RefreshPluginView();
                if (okCount != zips.Count) ShowPluginLoadFailedDialog(render: false);
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 拖入安装插件包异常", ex);
            }
        }

        /// <summary>
        /// 把一个 zip 当插件包装进 <c>plugins\&lt;名字&gt;\</c>。
        /// 与「拖单个 dll」的区别：包里的<b>所有文件</b>（dll、附属 exe、说明）一起落进同一个子目录 ——
        /// 宿主按「与目录同名」或 plugin.json 指定的 dll 加载它，插件要用的附属文件就躺在旁边。
        /// </summary>
        private (bool Ok, string Message) InstallPluginPackage(string zipPath)
        {
            string? extractDir = null;

            try
            {
                extractDir = Path.Combine(Path.GetTempPath(), "NotchPeninsula", "drop",
                    "x_" + Guid.NewGuid().ToString("N")[..8]);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // 压缩时多套一层文件夹很常见（选中文件夹右键压缩），所以要往下找真正装着 dll 的那层
                string root = FindPackageRoot(extractDir);
                if (root.Length == 0) return (false, "包内没有找到 .dll");

                string name = ResolvePackageName(root, zipPath);
                string target = Path.Combine(PluginManager.Instance.PluginsRoot, name);

                // 同名旧版本先卸掉：不卸会一直占着文件，覆盖会失败
                var old = PluginManager.Instance.Entries.FirstOrDefault(e =>
                    string.Equals(e.RootDir, target, StringComparison.OrdinalIgnoreCase));
                if (old != null) PluginManager.Instance.Remove(old);

                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);

                foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(target, Path.GetFileName(f));
                    if (!string.Equals(f, dst, StringComparison.OrdinalIgnoreCase)) File.Copy(f, dst, true);
                }

                PluginManager.Instance.Refresh();
                var fresh = PluginManager.Instance.Entries.FirstOrDefault(e =>
                    string.Equals(e.RootDir, target, StringComparison.OrdinalIgnoreCase));
                if (fresh == null) return (false, "包内未找到可加载的插件");

                return PluginManager.Instance.Load(fresh)
                    ? (true, "")
                    : (false, "已安装但加载失败：" + fresh.Error);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                try
                {
                    if (extractDir != null && Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                }
                catch { /* 临时目录删不掉不影响安装结果 */ }
            }
        }

        /// <summary>找到包内真正放着 dll 的那一层（先看根，再看一层子目录）。</summary>
        private static string FindPackageRoot(string dir)
        {
            if (Directory.GetFiles(dir, "*.dll").Length > 0) return dir;

            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Directory.GetFiles(sub, "*.dll").Length > 0) return sub;
            }
            return "";
        }

        /// <summary>装进哪个目录名：优先 plugin.json 的 id，其次 dll 文件名，最后退回 zip 名。</summary>
        private static string ResolvePackageName(string root, string zipPath)
        {
            string manifest = Path.Combine(root, "plugin.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("id", out var idEl)
                        && idEl.GetString() is { Length: > 0 } id)
                        return SanitizeDirName(id);
                }
                catch { /* 清单坏了不致命，后面还有两条退路 */ }
            }

            var dlls = Directory.GetFiles(root, "*.dll");
            if (dlls.Length > 0) return Path.GetFileNameWithoutExtension(dlls[0]);

            return SanitizeDirName(Path.GetFileNameWithoutExtension(zipPath));
        }

        private static string SanitizeDirName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 0 ? name : "Plugin";
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