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

        private int _hoveredPluginRemove = -1;  // 行索引：卸载

        private List<PluginEntry> _pluginView = new();

        private readonly List<string> _pluginSubTexts = new();

        private readonly List<bool> _pluginSubDisabled = new();

        // 缓存判据：PluginManager 的变更序号
        private int _pluginViewVersion = -1;

        private int _pluginScroll = 0;

        private void GetPluginListLayout(out int visibleRows, out int maxFirstRow)
        {
            GetPluginListCardTop(out float listY);
            int maxRows = Math.Max(1, (int)((HEIGHT - 20 - (listY + 44) - 8) / PluginListRowH));
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
            _pluginView = mgr.Entries.ToList();

            _pluginSubTexts.Clear();
            _pluginSubDisabled.Clear();
            for (int i = 0; i < _pluginView.Count; i++)
            {
                var entry = _pluginView[i];
                bool disabled = entry.State == PluginState.NotLoaded;
                string tail = string.IsNullOrEmpty(entry.Version) ? "" : $" · v{entry.Version}";
                if (!string.IsNullOrEmpty(entry.Author)) tail += $" · {entry.Author}";
                _pluginSubDisabled.Add(disabled);
                _pluginSubTexts.Add((disabled ? "已禁用" : "运行中") + tail);
            }

            // 行内悬停索引一并清空，避免指向错行。
            GetPluginListLayout(out _, out int maxFirst);
            _pluginScroll = Math.Clamp(_pluginScroll, 0, maxFirst);
            ResetPluginHover();
        }

        private PluginEntry? GetPluginAt(int index)
            => index >= 0 && index < _pluginView.Count ? _pluginView[index] : null;

        private void ResetPluginHover()
        {
            _hoveredPluginToggle = -1;
            _hoveredPluginReload = -1;
            _hoveredPluginRemove = -1;
            _hoveredMarketInstall = -1;
            _hoveredMarketUninstall = -1;
            _hoveredMarketDetail = -1;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _rateStars = 0;
            _hoveredMarketChk = false;
        }

        private static string? ShowOpenFileDialog(IntPtr owner, string title, string filter)
        {
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
                    Flags = Win32.OFN_EXPLORER | Win32.OFN_FILEMUSTEXIST | Win32.OFN_PATHMUSTEXIST | Win32.OFN_NOCHANGEDIR
                };

                if (Win32.GetOpenFileNameW(ref ofn))
                {
                    string? result = Marshal.PtrToStringUni(pFile);
                    return string.IsNullOrEmpty(result) ? null : result;
                }

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
                if (!ok) ShowPluginLoadFailedDialog(render: false);
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[PluginCenter] 导入插件异常", ex);
            }
        }

        private static string TruncateText(string? text, SKPaint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (paint.MeasureText(text) <= maxWidth) return text;   // 快路径：整串放得下（绝大多数情况）

            if (maxWidth <= paint.MeasureText("…")) return "…";

            //   终态 hi == lo + 1，答案就是 lo。
            int lo = 0, hi = text.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (paint.MeasureText(string.Concat(text.AsSpan(0, mid), "…")) <= maxWidth) lo = mid;
                else hi = mid;
            }

            return lo == 0 ? "…" : string.Concat(text.AsSpan(0, lo), "…");
        }

        private static readonly SKTypeface _hintEmojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");

        private static string StripInvisible(string s)
        {
            int hit = -1;
            for (int i = 0; i < s.Length; i++)
                if (IsInvisibleFormat(s[i])) { hit = i; break; }
            if (hit < 0) return s;   // 绝大多数文案干净，不做无谓的拷贝

            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (!IsInvisibleFormat(s[i])) sb.Append(s[i]);
            return sb.ToString();
        }

        private static bool IsInvisibleFormat(char c) => c switch
        {
            '\u00AD' => true,   // 软连字符
            '\u061C' => true,   // 阿拉伯字母标记
            '\u180E' => true,   // 蒙古文元音分隔符
            '\u200B' => true,   // 零宽空格
            '\u200C' => true,   // 零宽非连接符
            '\u200E' => true,   // 从左到右标记
            '\u200F' => true,   // 从右到左标记
            '\u2028' => true,   // 行分隔符
            '\u2029' => true,   // 段分隔符
            '\u2060' => true,   // 单词连接符
            '\uFEFF' => true,   // 零宽不换行空格
            _ => false,
        };

        private static void DrawTextWithEmoji(SKCanvas canvas, string text, SKPaint paint, float maxWidth, float rightEdge, float baselineY, bool rightAlign = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            text = StripInvisible(text);
            if (text.Length == 0) return;

            var baseTypeface = paint.Typeface;
            var emoji = _hintEmojiTypeface;
            bool emojiUsable = emoji != null && !ReferenceEquals(emoji, baseTypeface);

            string shown = maxWidth > 0 ? TruncateText(text, paint, maxWidth) : text;
            if (shown.Length == 0) return;

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

                if (surrogatePair) { useEmoji[i + 1] = prev; i++; }
            }

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

        private static void DrawSegment(SKCanvas canvas, string text, int start, int end, float x, float y,
            SKPaint paint, SKTypeface? typeface, SKTypeface? baseTypeface)
        {
            if (end <= start) return;
            string seg = text[start..end];
            if (typeface != null) paint.Typeface = typeface;
            canvas.DrawText(seg, x, y, paint);
            paint.Typeface = baseTypeface;
        }

        private static float MeasureSegment(string text, int start, int end, SKPaint paint, SKTypeface? typeface)
        {
            if (end <= start) return 0f;
            var baseTypeface = paint.Typeface;
            if (typeface != null) paint.Typeface = typeface;
            float w = paint.MeasureText(text[start..end]);
            paint.Typeface = baseTypeface;
            return w;
        }

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
