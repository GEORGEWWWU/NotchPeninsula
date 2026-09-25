using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // 插件中心交互状态
        private int _hoveredPluginAction = -1;  // 0=导入 DLL, 1=打开目录, 2=插件市场

        private int _hoveredPluginToggle = -1;  // 行索引：启用/禁用开关

        private int _hoveredPluginReload = -1;  // 行索引：热重载

        private int _hoveredPluginRemove = -1;  // 行索引：移除

        private List<PluginEntry> _pluginView = new();

        // 行内副标题（"运行中 · v1.2.0 · 作者" 之类）：与 _pluginView 同序、同版本。
        // 原来是在 Render 里对每一行每帧拼一遍（含 $-插值），现在跟着列表一起只在变更时重建。
        private readonly List<string> _pluginSubTexts = new();

        // 缓存判据：PluginManager 的变更序号
        private int _pluginViewVersion = -1;

        // ================= 插件中心辅助逻辑 =================
        private void RefreshPluginView()
        {
            int version = PluginManager.Instance.ChangeVersion;
            if (_pluginViewVersion == version) return;   // 插件页每帧都会调；没变就直接复用
            _pluginViewVersion = version;

            var mgr = PluginManager.Instance;
            _pluginView = mgr.Entries.ToList();

            // 副标题（状态 / 版本 / 作者）随插件状态变化，所以在这里跟列表一起重建，渲染路径只负责取用。
            // ⚠️ 不再带「#N/M」位置序号 —— 位置改由「显示设置 → 显示内容」统一展示与调整（2026-09-25）。
            _pluginSubTexts.Clear();
            for (int i = 0; i < _pluginView.Count; i++)
            {
                var entry = _pluginView[i];
                string sub;
                if (entry.State == PluginState.Failed)
                    sub = "加载失败：" + (entry.Error ?? "未知错误");
                else if (entry.State == PluginState.Loaded)
                {
                    // 运行中 · v1.0.0 · 作者（作者缺失时只留前两段，免得出现一个孤零零的分隔点）
                    sub = string.IsNullOrEmpty(entry.Version) ? "运行中" : $"运行中 · v{entry.Version}";
                    if (!string.IsNullOrEmpty(entry.Author)) sub += $" · {entry.Author}";
                }
                else
                    sub = "已禁用 · " + entry.Key;

                _pluginSubTexts.Add(sub);
            }
        }

        private PluginEntry? GetPluginAt(int index)
            => index >= 0 && index < _pluginView.Count ? _pluginView[index] : null;

        /// <summary>列表变化后清空行内悬停索引，避免指向错行。</summary>
        private void ResetPluginHover()
        {
            _hoveredPluginToggle = -1;
            _hoveredPluginReload = -1;
            _hoveredPluginRemove = -1;
        }

        /// <summary>
        /// 弹出传统 Win32 打开文件对话框（comdlg32!GetOpenFileNameW），返回选中路径；用户取消返回 null。
        ///
        /// 为什么不用 System.Windows.Forms.OpenFileDialog：
        ///   WinForms 的 OpenFileDialog 走 Vista「通用项对话框」，会在本进程内加载 ExplorerBrowser
        ///   + 外壳命名空间 + 图标/缩略图缓存 —— 首次打开就常驻 20~30MB，而且这是 Windows 的
        ///   进程级外壳组件，Dispose 对话框、关闭资源管理器都不会归还，看起来就像"内存泄漏"。
        ///   传统对话框是 comdlg32 的普通模态窗口，不碰 ExplorerBrowser，开销可以忽略。
        /// </summary>

        private static string? ShowOpenFileDialog(IntPtr owner, string title, string filter)
        {
            // OPENFILENAME 里的字符串字段在 Core/Win32.cs 中被声明成 IntPtr，所以要手工分配/释放原生内存。
            // 不能改成 string / StringBuilder 字段：.NET 10 的 Marshal.SizeOf 遇到含托管引用字段的
            // 结构体会抛 ArgumentException，而 lStructSize 又必须精确，两者冲突，只能手工封送。
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pFile = IntPtr.Zero;
            try
            {
                const int maxFile = 1024; // 单位是「字符」而非字节，故下面的缓冲区要 ×2
                pFilter = Marshal.StringToHGlobalUni(filter);
                pTitle = Marshal.StringToHGlobalUni(title);
                pFile = Marshal.AllocHGlobal(maxFile * 2);
                Marshal.WriteInt16(pFile, 0); // 首字符置 0：不预填文件名，对话框沿用上次访问的目录

                var ofn = new Win32.OPENFILENAME
                {
                    lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAME>(),
                    hwndOwner = owner,
                    lpstrFilter = pFilter,
                    lpstrFile = pFile,
                    nMaxFile = maxFile,
                    lpstrTitle = pTitle,
                    // NOCHANGEDIR：传统对话框默认会把进程当前目录改成用户选的目录，必须禁掉
                    Flags = Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_NOCHANGEDIR
                };

                if (Win32.GetOpenFileNameW(ref ofn))
                {
                    string? result = Marshal.PtrToStringUni(pFile);
                    return string.IsNullOrEmpty(result) ? null : result;
                }

                // 返回 false 时可能是"用户取消"（CommDlgExtendedError == 0），也可能是真出错
                uint err = Win32.CommDlgExtendedError();
                if (err != 0) Logger.Warn($"[文件对话框] GetOpenFileNameW 失败，CommDlgExtendedError=0x{err:X}");
                return null;
            }
            finally
            {
                if (pFilter != IntPtr.Zero) Marshal.FreeHGlobal(pFilter);
                if (pTitle != IntPtr.Zero) Marshal.FreeHGlobal(pTitle);
                if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
            }
        }

        /// <summary>弹出文件选择框导入插件 DLL。</summary>

        private void ImportPluginDll()
        {
            try
            {
                string? picked = ShowOpenFileDialog(_hwnd, "选择 NotchPeninsula 插件 DLL",
                    "插件动态库 (*.dll)\0*.dll\0所有文件 (*.*)\0*.*\0");
                if (picked == null) return;

                var (ok, msg) = PluginManager.Instance.Import(picked);
                Logger.Info($"[PluginCenter] 导入结果: {(ok ? "成功" : "失败")} — {msg}");
                ResetPluginHover();
                RefreshPluginView();
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 导入插件异常", ex);
            }
        }

        /// <summary>按像素宽度截断文本并追加省略号（零 GC 不敏感，交互时才调用）。</summary>
        private static string TruncateText(string? text, SKPaint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (paint.MeasureText(text) <= maxWidth) return text;
            for (int len = text.Length - 1; len > 0; len--)
            {
                var candidate = text[..len] + "…";
                if (paint.MeasureText(candidate) <= maxWidth) return candidate;
            }
            return "…";
        }

        /// <summary>收起所有下拉浮窗（同一时刻只允许展开一个）。</summary>

        private void CloseAllDropdowns()
        {
            _dropdownOpen = false;
            _monitorDropdownOpen = false;
            _toastModeDropdownOpen = false;
            _toastSoundDropdownOpen = false;
            _soundVolumeDropdownOpen = false;
            _matchModeDropdownOpen = false;
            _appDropdownOpen = false;
        }
    }
}
