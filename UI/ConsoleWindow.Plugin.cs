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

        // 逐行「是否禁用」标记（与 _pluginView / _pluginSubTexts 同序）：
        // 渲染侧据此把副标题开头的状态词（「已禁用」）染成强调蓝，其余部分保持常规灰。
        private readonly List<bool> _pluginSubDisabled = new();

        // 缓存判据：PluginManager 的变更序号
        private int _pluginViewVersion = -1;

        /// <summary>
        /// 插件中心列表的滚动首行（绝对条目下标）。插件数超过卡片能放下的行数时，
        /// 超出的部分靠这个偏移滚动查看；滚轮是唯一的改动入口（WM_MOUSEWHEEL 的 tab 6 分支）。
        /// 渲染、命中、滚轮三处都通过 GetPluginListLayout 取可滚范围。
        /// </summary>
        private int _pluginScroll = 0;

        /// <summary>
        /// 插件中心列表的唯一布局真源：可视行数 / 最大首行。
        /// 绘制（RenderTabPlugins）、悬停命中（OnMouseMove 的 tab 6 段）、
        /// 滚轮（WM_MOUSEWHEEL）三处共用 —— 与「显示内容」列表的 GetDisplayListLayout 同一套约定，
        /// 避免卡片高度或行高一改就出现「滚不动 / 滚过头」。
        /// </summary>
        private void GetPluginListLayout(out int visibleRows, out int maxFirstRow)
        {
            // 与 RenderTabPlugins 的布局严格同源：topY = TITLE_BAR_HEIGHT + 12，listY = topY + 110，
            // 行起点 listY + 44，行高 56；卡片底边是 HEIGHT - 20，底部再留 8px 呼吸。
            float listY = TITLE_BAR_HEIGHT + 12 + 110;
            const float FirstRowY = 44f, RowH = 56f;
            int maxRows = Math.Max(1, (int)((HEIGHT - 20 - (listY + FirstRowY) - 8) / RowH));
            int total = _pluginView.Count;
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        // ---- 插件中心辅助逻辑 ----
        private void RefreshPluginView()
        {
            int version = PluginManager.Instance.ChangeVersion;
            if (_pluginViewVersion == version) return;   // 插件页每帧都会调；没变就直接复用
            _pluginViewVersion = version;

            var mgr = PluginManager.Instance;
            // 插件中心把所有已发现的插件都列出来（含禁用 / 加载失败的），用户才能在原地重新启用它们。
            //    「不参与排序、不要显示」那条规矩只针对「显示设置 → 显示内容」那张排序列表
            //    （见 PluginManager.DisplayItems），插件中心照常列全。
            _pluginView = mgr.Entries.ToList();

            // 副标题（状态 / 版本 / 作者）随插件状态变化，所以在这里跟列表一起重建，渲染路径只负责取用。
            // 不再带「#N/M」位置序号 —— 位置改由「显示设置 → 显示内容」统一展示与调整。
            _pluginSubTexts.Clear();
            _pluginSubDisabled.Clear();
            for (int i = 0; i < _pluginView.Count; i++)
            {
                var entry = _pluginView[i];
                // 禁用：「运行中」换成「已禁用」，其余（版本号 / 作者）保留；
                // 渲染侧会把「已禁用」这一小段染成强调蓝，后面照旧常规灰。
                bool disabled = entry.State == PluginState.NotLoaded;
                string tail = string.IsNullOrEmpty(entry.Version) ? "" : $" · v{entry.Version}";
                if (!string.IsNullOrEmpty(entry.Author)) tail += $" · {entry.Author}";
                _pluginSubDisabled.Add(disabled);
                _pluginSubTexts.Add((disabled ? "已禁用" : "运行中") + tail);
            }

            // 列表内容变了：滚动位置钳回可滚范围（插件被移除后别停在一片空白上），
            // 行内悬停索引一并清空，避免指向错行。
            GetPluginListLayout(out _, out int maxFirst);
            _pluginScroll = Math.Clamp(_pluginScroll, 0, maxFirst);
            ResetPluginHover();
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

        /// <summary>
        /// 按像素宽度截断文本并追加省略号。
        ///
        /// 这是渲染热路径（设置窗口每帧、每个下拉项 / 插件行 / 卡片副标题都要过一遍），
        /// 所以刻意用二分定位截断点，而不是原来那种从末尾逐个字符往回试的线性扫。
        /// 线性扫的最坏情况是「整串都放不下」——比如超长的插件副标题撞上 140px 的信息区，
        /// 每个字符都要一次 MeasureText，一次渲染里几个地方叠起来就是上百次字形度量。
        /// 二分把次数压到 log2(n)（40 个字符 ≈ 6 次），而且每轮只在候选串上量一次。
        ///
        /// 判据「量出来的宽度 ≤ maxWidth」关于长度单调 —— 前缀越长越宽，所以二分成立。
        /// 省略号本身占宽，必须把它算进候选串再量（不能量前缀、再单独比 ellipsis），
        /// 否则边界上会出现「截完还是超宽」。
        ///
        /// 本方法不做跨帧缓存：调用点传入的 text / paint / maxWidth 组合很杂，
        /// 缓存键的构造成本比省下的度量还高。真正的大头在别处（见 UpdateLayeredContentWindow）。
        /// </summary>
        private static string TruncateText(string? text, SKPaint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (paint.MeasureText(text) <= maxWidth) return text;   // 快路径：整串放得下（绝大多数情况）

            // 连一个字符加省略号都放不下 —— 直接退化成省略号，省掉整段二分。
            if (maxWidth <= paint.MeasureText("…")) return "…";

            // 二分找「最长的、量出来仍 ≤ maxWidth 的前缀」。
            //   lo 恒为「已知放得下的长度」，hi 恒为「已知放不下的长度」。
            //   终态 hi == lo + 1，答案就是 lo。
            int lo = 0, hi = text.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (paint.MeasureText(string.Concat(text.AsSpan(0, mid), "…")) <= maxWidth) lo = mid;
                else hi = mid;
            }

            // lo == 0 时退化成纯省略号（与上面那个提前返回语义一致）。
            return lo == 0 ? "…" : string.Concat(text.AsSpan(0, lo), "…");
        }

        /// <summary>
        /// 设置窗口专用的 Windows 自带 Emoji 字体。只用于给缺字的单行文案兜底，不是全局字体替换。
        /// </summary>
        private static readonly SKTypeface _hintEmojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");

        /// <summary>
        /// 画一行可能含 Emoji / 特殊符号的文案，缺字的码点自动改用 Segoe UI Emoji。
        ///
        /// 为什么设置窗口必须自带这一层：渲染器（Renderer）内部有逐码点的字体回退
        /// （缺字 → Emoji → 多语言兜底），但设置窗口的画笔固定是 Microsoft YaHei UI，
        /// 单独 DrawText 一个 YaHei 没有的字形只会画出豆腐块。
        /// 例如「」（U+2611 + U+FE0F）两码点都不在 YaHei 里，而 seguiemj.ttf 两个都有
        /// （已核对 cmap 表）。
        ///
        /// 做法：把文本切成「YaHei 画得出来」与「要交给 Emoji 字体」的若干段，逐段 set_typeface 绘制。
        /// 变体选择符（U+FE0F / U+FE0E）跟着前一个字符走，不单独成段 —— 否则会画出一个空框。
        /// 只支持单行；本方法只服务于这一行提示文案，不做换行、不做双向文字。
        /// </summary>
        private static void DrawTextWithEmoji(SKCanvas canvas, string text, SKPaint paint, float maxWidth, float rightEdge, float baselineY, bool rightAlign = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            var baseTypeface = paint.Typeface;
            var emoji = _hintEmojiTypeface;
            bool emojiUsable = emoji != null && !ReferenceEquals(emoji, baseTypeface);

            // 截断先用基础字体量（与改动前口径一致，宽度略有偏差也只影响"是否省略"）
            string shown = maxWidth > 0 ? TruncateText(text, paint, maxWidth) : text;
            if (shown.Length == 0) return;

            // 逐码点决定归属：true = 这段交给 Emoji 字体。变体选择符 / 零宽连字 / 组合用圈跟着前一个字符走
            // （单独立段会画成一个空框）。
            var useEmoji = new bool[shown.Length];
            bool prev = false;
            for (int i = 0; i < shown.Length; i++)
            {
                int cp = shown[i];
                bool surrogatePair = false;
                if (char.IsHighSurrogate(shown[i]) && i + 1 < shown.Length)
                {
                    cp = char.ConvertToUtf32(shown[i], shown[i + 1]);
                    surrogatePair = true;
                }

                if (cp is 0xFE0E or 0xFE0F or 0x200D or 0x20E3)
                {
                    useEmoji[i] = prev;   // 跟随前一个字符
                }
                else if (emojiUsable)
                {
                    bool missingInBase = baseTypeface == null || baseTypeface.GetGlyph(cp) == 0;
                    prev = missingInBase && emoji!.GetGlyph(cp) != 0;
                    useEmoji[i] = prev;
                }

                // 代理对的第二个 char 与高代理同属一段（它自己不参与字体判断）
                if (surrogatePair) { useEmoji[i + 1] = prev; i++; }
            }

            // 起点：按每段实际字体的推进宽度求和，不能直接用基础字体量整串 ——
            // Emoji 段的宽和 YaHei 量的不一样，右对齐时会整体偏出去。
            float total = 0f;
            for (int i = 0; i < shown.Length; i++)
            {
                int j = i;
                while (j < shown.Length && useEmoji[j] == useEmoji[i]) j++;
                total += MeasureSegment(shown, i, j, paint, useEmoji[i] ? emoji! : baseTypeface);
                i = j - 1;
            }

            float x = rightAlign ? rightEdge - total : rightEdge;
            for (int i = 0; i < shown.Length; i++)
            {
                int j = i;
                while (j < shown.Length && useEmoji[j] == useEmoji[i]) j++;
                DrawSegment(canvas, shown, i, j, x, baselineY, paint, useEmoji[i] ? emoji! : baseTypeface, baseTypeface);
                x += MeasureSegment(shown, i, j, paint, useEmoji[i] ? emoji! : baseTypeface);
                i = j - 1;
            }
        }

        /// <summary>逐段绘制：临时换字体，画完立刻还原（画笔是共用的静态对象，绝不能把字体留在上面）。</summary>
        private static void DrawSegment(SKCanvas canvas, string text, int start, int end, float x, float y,
            SKPaint paint, SKTypeface? typeface, SKTypeface? baseTypeface)
        {
            if (end <= start) return;
            string seg = text[start..end];
            if (typeface != null) paint.Typeface = typeface;
            canvas.DrawText(seg, x, y, paint);
            paint.Typeface = baseTypeface;
        }

        /// <summary>量一段的宽度（同样临时换字体，量完还原）。</summary>
        private static float MeasureSegment(string text, int start, int end, SKPaint paint, SKTypeface? typeface)
        {
            if (end <= start) return 0f;
            var baseTypeface = paint.Typeface;
            if (typeface != null) paint.Typeface = typeface;
            float w = paint.MeasureText(text[start..end]);
            paint.Typeface = baseTypeface;
            return w;
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
