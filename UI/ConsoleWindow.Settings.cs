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
        private void UpdateValueString(int index)
        {
            _valStrCache[index] = index == 6 ? $"{_customValues[index]:F2} x" : $"{(int)_customValues[index]} px";
        }

        private void ApplyToastContentMode(int modeIndex)
        {
            if (modeIndex == 2) // 完整
            {
                if (_savedToastW < 0f) { _savedToastW = Renderer.TOAST_WIDTH; _savedToastH = Renderer.TOAST_HEIGHT; }

                if (Renderer.TOAST_WIDTH < Renderer.FULL_TOAST_MIN_WIDTH)
                {
                    Renderer.TOAST_WIDTH = Renderer.FULL_TOAST_MIN_WIDTH;
                    _customValues[4] = Renderer.TOAST_WIDTH;
                    Program.SaveSetting("Custom_ToastW", Renderer.TOAST_WIDTH);
                    UpdateValueString(4);
                }
                if (Renderer.TOAST_HEIGHT < Renderer.FULL_TOAST_MIN_HEIGHT)
                {
                    Renderer.TOAST_HEIGHT = Renderer.FULL_TOAST_MIN_HEIGHT;
                    _customValues[5] = Renderer.TOAST_HEIGHT;
                    Program.SaveSetting("Custom_ToastH", Renderer.TOAST_HEIGHT);
                    UpdateValueString(5);
                }
                Renderer.IsToastFullMode = true;
                Renderer.IsToastCompactMode = false;
                Program.SaveSetting("ToastContentMode", 2);
            }
            else // 0=缩略 或 1=紧凑：尺寸均恢复为用户设定的值
            {
                Renderer.IsToastFullMode = false;
                Renderer.IsToastCompactMode = modeIndex == 1;
                Program.SaveSetting("ToastContentMode", modeIndex);

                float w = _savedToastW >= 0f ? _savedToastW : _defaultCustomValues[4];
                float h = _savedToastH >= 0f ? _savedToastH : _defaultCustomValues[5];
                _savedToastW = -1f; _savedToastH = -1f;

                Renderer.TOAST_WIDTH = w;
                Renderer.TOAST_HEIGHT = h;
                _customValues[4] = w;
                _customValues[5] = h;
                Program.SaveSetting("Custom_ToastW", w);
                Program.SaveSetting("Custom_ToastH", h);
                UpdateValueString(4);
                UpdateValueString(5);
            }

            _selectedToastModeIndex = modeIndex;
        }

        // 玻璃卡滑轨的唯一落库口（参数是下标 + 档位）。
        // 悬停拖拽与单击走同一条路，免得两处各写一遍导致设置对不上。
        // 两张卡共用同一批格子，按当前材质分派到各自的参数表。
        private void ApplyGlassLevel(int paramIndex, int level)
        {
            if (Renderer.IslandLiquidGlass2)
            {
                if (paramIndex < 0 || paramIndex >= LiquidGlass2.ParamCount) return;
                LiquidGlass2.SetLevel(paramIndex, level);
                Program.SaveSetting(LiquidGlass2.ParamKeys[paramIndex], LiquidGlass2.GetLevel(paramIndex));
                return;
            }
            if (paramIndex < 0 || paramIndex >= LiquidGlass.ParamCount) return;
            LiquidGlass.SetLevel(paramIndex, level);
            Program.SaveSetting(LiquidGlass.ParamKeys[paramIndex], LiquidGlass.GetLevel(paramIndex));
        }

        // 一次性回到出厂默认。落库仍走 ApplyGlassLevel，
        // 保证「点按钮」和「拖滑轨」是同一套 SetLevel + SaveSetting。
        private void ResetGlassDefaults()
        {
            for (int i = 0; i < GlassParamCount; i++)
                ApplyGlassLevel(i, Renderer.IslandLiquidGlass2
                    ? LiquidGlass2.DefaultLevelOf(i)
                    : LiquidGlass.DefaultLevelOf(i));
        }

        /// <summary>当前玻璃卡上的滑轨根数（液态玻璃 12 根，液态玻璃 2 是 9 根）。</summary>
        private static int GlassParamCount => Renderer.IslandLiquidGlass2 ? LiquidGlass2.ParamCount : LiquidGlass.ParamCount;

        /// <summary>当前玻璃卡的滑轨行数（每行 3 根）。</summary>
        private static int GlassRowCount => (GlassParamCount + 2) / 3;

        /// <summary>当前玻璃卡上第 index 根滑轨的档位。</summary>
        private static int GlassLevelOf(int index)
            => Renderer.IslandLiquidGlass2 ? LiquidGlass2.GetLevel(index) : LiquidGlass.GetLevel(index);

        private const float THEME_CARD_Y = CARD_TOP_Y;

        private const float THEME_CARD_H = 112f;   // 两行：主题配色 + 背景材质

        private const float OPACITY_CARD_Y = THEME_CARD_Y + THEME_CARD_H + CARD_GAP;   // 134

        private const float OPACITY_CARD_H = 78f;

        // 液态玻璃模式下「背景透明度」整卡不显示：玻璃的着色由「玻璃雾度」负责，
        // 涂层恒为 0（见 Renderer.ApplyThemeColors），这条滑轨留着只会让人以为还能调。
        // 卡片链式排布，所以尺寸卡与玻璃卡要跟着上移，否则中间空出一整块。
        private static bool ShowOpacityCard => !Renderer.IslandAnyGlass;

        private static float SIZE_CARD_Y => ShowOpacityCard
            ? OPACITY_CARD_Y + OPACITY_CARD_H + CARD_GAP   // 222
            : OPACITY_CARD_Y;                              // 134

        private const float THEME_SEG_W = 150f;

        private const float THEME_SEG_X = WIDTH - CONTENT_TEXT_RM - THEME_SEG_W;   // 422

        private const float THEME_SEG_Y = 28f;

        // 第二行「背景材质」：位置全由 THEME_SEG_Y 派生，改一处其余自动跟。
        // 四档（实体 / 亚克力 / 液态玻璃 / 液态玻璃2.0）比原来三档宽，但卡片高度与行位一律不动 ——
        // 宽度取「卡片文字右缘 − 左侧标签与「实验性」徽标实测宽度 − 8px 呼吸」，
        // 即这一行能给出的最大宽度；仍放不下时由 DrawSegmented 自动缩字号兜底。
        // 呼吸从 14 收到 8：第四档是「液态玻璃2.0」，比原来多出「.0」两字宽，这 6px 是给它让的。
        private static float MAT_SEG_W =>
            (WIDTH - CONTENT_TEXT_RM) - (CONTENT_TEXT_X
                + _uiTextPaint.MeasureText("背景材质") + BADGE_GAP_X
                + _badgeTextPaint.MeasureText("实验性") + BADGE_PAD_X * 2f + 8f);

        private static float MAT_SEG_X => WIDTH - CONTENT_TEXT_RM - MAT_SEG_W;

        // 两行之间的行距：原 42 两行贴太近，2026-10-10 加大到 50（卡高同步 +8）
        private const float THEME_ROW_GAP = 50f;

        private const float MAT_SEG_Y = THEME_SEG_Y + THEME_ROW_GAP;   // 78

        // 「背景材质」两行基线：与分段控件同一套派生（标题 −2 / 副标题 +18）
        private const float MAT_TITLE_DY = MAT_SEG_Y - 2f;   // 76

        private const float MAT_SUB_DY = MAT_SEG_Y + 18f;    // 96

        // 尺寸卡：4 行 × 34 + 上下各留 12
        private const float SIZE_CARD_H = 148f;

        // ---- 「液态玻璃」卡：只在材质选到液态玻璃时出现，挂在尺寸卡下面 ----
        // 第一行：玻璃质量分段（左）+「恢复默认参数」按钮（右）—— 抓屏方式已改为自动，
        // 没有独立设置也没有状态行；下面 12 根滑轨排成 3 列 × 4 行（窗口就这么高，两排放不下）。
        private static float GLASS_CARD_Y => SIZE_CARD_Y + SIZE_CARD_H + CARD_GAP;   // 222 或 380

        private const float GLASS_CARD_H = 176f;

        private const float GLASS_SEG_Y = 12f;      // 第一行：质量分段 + 恢复默认按钮

        // 玻璃卡元素多、横向最挤：左右缩进比别的卡各放 8px（16 → 8），
        // 腾出来的 16px 全给滑轨（48 → 56），拖动时手感也更稳。
        private const float GLASS_TEXT_X = CONTENT_TEXT_X - 8f;            // 194

        private const float GLASS_TEXT_R = WIDTH - CONTENT_TEXT_RM + 8f;   // 580

        // 「玻璃质量」标签四字（约 54）从 194 起，分段必须让到 254 之后。
        // 四段（流畅/均衡/精细/极致）：每段 43，两字标签 26 + 左右各 8 的呼吸，够用也不挤。
        private const float GLASS_QUALITY_SEG_W = 172f;

        private const float GLASS_QUALITY_SEG_X = 254f;

        private const float GLASS_ROW0_Y = 46f;     // 滑轨区第一行（分段行 12~42，留 4 呼吸）

        private const float GLASS_ROW_H = 32f;

        private const float GLASS_ROWS = 4f;        // 12 项 / 3 列

        // 3 列：194 → 580（3 × 122 + 2 × 10 列间距）。
        // 每列 = 标签 26 + 8 + 滑轨 50 + 4 + 档位值 26（右对齐到列宽 −6）。
        private const float GLASS_COL_X = GLASS_TEXT_X;        // 194

        private const float GLASS_COL_W = 122f;

        private const float GLASS_COL_GAP = 10f;

        // 34 而不是 28：13px 的"倍率"两字宽 26，滑轨选中时圆点半径 5.5，
        // 起点再靠前圆点就会压到字上（截图里"倍率●"就是这个原因）。
        private const float GLASS_SLIDER_DX = 34f;             // 滑轨相对列左端

        private const float GLASS_SLIDER_W = 50f;

        private const float GLASS_VALUE_RIGHT = 116f;          // 档位值右缘（相对列左端，= 列宽 −6）

        // 「恢复默认参数」：与质量分段同一行，右对齐到卡片文字右缘；纵向在分段内居中
        private const float GLASS_RESET_W = 96f;

        private const float GLASS_RESET_H = 26f;

        private const float GLASS_RESET_X = GLASS_TEXT_R - GLASS_RESET_W;   // 484

        private const float OPACITY_SLIDER_DY = 44f;

        // 个性化中心各行控件的 Y 坐标（index → 行）：
        // 这四项合并成一张卡，卡高 = 12 + 行数×34。
        private float GetBtnY(int index)
        {
            return index switch
            {
                3 => TITLE_BAR_HEIGHT + SIZE_CARD_Y + 12,
                7 => TITLE_BAR_HEIGHT + SIZE_CARD_Y + 12 + 34,
                5 => TITLE_BAR_HEIGHT + SIZE_CARD_Y + 12 + 68,
                6 => TITLE_BAR_HEIGHT + SIZE_CARD_Y + 12 + 102,
                _ => 0
            };
        }

        private void PickCustomFont()
        {
            try
            {
                string? picked = ShowOpenFileDialog(_hwnd, "选择灵动岛字体文件",
                    "字体文件 (*.ttf;*.otf;*.ttc)\0*.ttf;*.otf;*.ttc\0所有文件 (*.*)\0*.*\0");
                if (picked == null) return;

                if (FontConfig.ApplyCustomFont(picked, out string error))
                {
                    Program.SaveSetting("CustomFontPath", picked); // 记忆化：下次启动自动恢复
                    _fontHint = "";
                }
                else
                {
                    _fontHint = error;
                    Logger.Error($"[FontCenter] 字体加载失败：{error}");
                }
                Render();
            }
            catch (Exception ex)
            {
                _fontHint = "打开字体选择框失败";
                Logger.Error("[FontCenter] 选择字体异常", ex);
                Render();
            }
        }

        private void ResetCustomFont()
        {
            FontConfig.ResetToSystemFont();
            Program.SaveSetting("CustomFontPath", "");
            _fontHint = "";
            Render();
        }

        // ---- 消息提示音 ----

        private void ApplyToastSound(int index)
        {
            if (index < 0 || index >= ToastSoundConfig.OptionCount) return;

            if (index == ToastSoundConfig.CustomIndex) { PickToastSound(); return; }

            ToastSoundConfig.SelectedIndex = index;

            if (index == 0)
            {
                ToastSoundConfig.CustomPath = "";
                ToastSoundConfig.SelectedKey = "";
                ToastSoundConfig.PersistSelection();
                _soundHint = "";
                ToastSoundPlayer.ClearQueue(); // 正在响的直接掐掉，别让「已选无」之后还响
            }
            else
            {
                // 记下这条内置音的文件名身份。只存位置索引的话，
                ToastSoundConfig.SelectedKey = ToastSoundConfig.Builtins[index - ToastSoundConfig.BuiltinOffset].FileName;
                ToastSoundConfig.PersistSelection();
                _soundHint = "";
                if (!ToastSoundConfig.IsEnabled)
                {
                    ToastSoundConfig.IsEnabled = true;
                    Program.SaveSetting("ToastSoundEnabled", 1);
                }
                PreviewToastSound();
            }
        }

        private void PickToastSound()
        {
            try
            {
                string? picked = ShowOpenFileDialog(_hwnd, "选择消息提示音", ToastSoundConfig.FileFilter);
                if (picked == null) return;

                if (ToastSoundConfig.IsUsableFile(picked, out string why))
                {
                    ToastSoundConfig.CustomPath = picked;
                    ToastSoundConfig.SelectedIndex = ToastSoundConfig.CustomIndex;
                    ToastSoundConfig.SelectedKey = ""; // 自定义项没有「内置文件名身份」
                    ToastSoundConfig.PersistSelection();
                    // 同上：选了音源就把开关打开，否则用户会以为功能坏了
                    if (!ToastSoundConfig.IsEnabled)
                    {
                        ToastSoundConfig.IsEnabled = true;
                        Program.SaveSetting("ToastSoundEnabled", 1);
                    }
                    _soundHint = "";
                    PreviewToastSound();
                }
                else
                {
                    _soundHint = why;
                    Logger.Warn($"[提示音] 音频不可用：{why} — {picked}");
                }
                Render();
            }
            catch (Exception ex)
            {
                _soundHint = "打开音频选择框失败";
                Logger.Error("[提示音] 选择音频异常", ex);
                Render();
            }
        }

        private void ResetToastSound()
        {
            ToastSoundConfig.IsEnabled = false;
            ToastSoundConfig.SelectedIndex = 0;
            ToastSoundConfig.CustomPath = "";
            ToastSoundConfig.SelectedKey = ""; // 内置音的文件名身份也要一起清
            ToastSoundConfig.VolumePercent = ToastSoundConfig.DefaultVolumePercent;
            Program.SaveSetting("ToastSoundEnabled", 0);
            ToastSoundConfig.PersistSelection(); // 索引 / 文件名身份 / 自定义路径三者一次写回
            Program.SaveSetting("ToastSoundVolume", ToastSoundConfig.VolumePercent);
            ToastSoundPlayer.ClearQueue();
            _soundHint = "";
            Render();
        }

        private void PreviewToastSound()
        {
            try
            {
                var src = ToastSoundConfig.ResolveCurrentSource();
                if (!src.IsValid)
                {
                    // 只有「显式点了试听」才值得提示；切到「无」时静默即可。
                    if (ToastSoundConfig.SelectedIndex != 0)
                        _soundHint = ToastSoundConfig.DescribeUnavailable();
                    Render();
                    return;
                }
                // 试听走同一个队列：连续点几次也是依次响，不会叠成噪音
                _soundHint = "";
                if (src.Path.Length > 0)
                    ToastSoundPlayer.Enqueue(src.Path, ToastSoundConfig.VolumePercent);
                else
                    ToastSoundPlayer.EnqueueResource(src.ResourceName, ToastSoundConfig.VolumePercent);
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[提示音] 试听异常", ex);
            }
        }

        private void ApplyToastSoundVolume(int index)
        {
            if (index < 0 || index >= ToastSoundConfig.VolumeOptions.Length) return;
            ToastSoundConfig.VolumePercent = ToastSoundConfig.VolumeOptions[index];
            Program.SaveSetting("ToastSoundVolume", ToastSoundConfig.VolumePercent);
            Render();
        }

        // ---- 媒体设置页「全局快捷键」的录制 ----

        // 钩子路径（KeyboardCapture）的入口：修饰键由钩子当场读好带过来，
        // 窗口自己再读 GetKeyState 已经没有意义 —— 那会儿手早松了。
        private bool HandleCapturedKey(int vk, uint mods)
        {
            if (_hotkeyRecordingIndex >= 0) return HandleHotkeyRecording(vk, mods);
            if (_islandHotkeyRecording) return HandleIslandHotkeyRecording(vk, mods);
            return false;
        }

        // 兜底路径（钩子装不上时的窗口键盘消息）：此刻按键状态还在本线程队列里。
        private static uint ReadModsNow()
        {
            uint mods = 0;
            if ((Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0) mods |= Win32.MOD_CONTROL;
            if ((Win32.GetKeyState(Win32.VK_MENU) & 0x8000) != 0) mods |= Win32.MOD_ALT;
            if ((Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0) mods |= Win32.MOD_SHIFT;
            if ((Win32.GetKeyState(Win32.VK_LWIN) & 0x8000) != 0
                || (Win32.GetKeyState(Win32.VK_RWIN) & 0x8000) != 0) mods |= Win32.MOD_WIN;
            return mods;
        }

        private bool HandleHotkeyRecording(int vk) => HandleHotkeyRecording(vk, ReadModsNow());

        private bool HandleHotkeyRecording(int vk, uint mods)
        {
            if (_hotkeyRecordingIndex < 0) return false;

            if (vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
                or Win32.VK_LWIN or Win32.VK_RWIN
                or Win32.VK_LSHIFT or Win32.VK_RSHIFT
                or Win32.VK_LCONTROL or Win32.VK_RCONTROL
                or Win32.VK_LMENU or Win32.VK_RMENU)
                return true;

            if (vk == Win32.VK_ESCAPE) { CancelHotkeyRecording(); return true; }

            if (vk == Win32.VK_BACK)
            {
                MediaHotkeys.ClearBinding(_hotkeyRecordingIndex);
                _hotkeyHint = "";
                EndHotkeyRecording();
                return true;
            }

            string error = MediaHotkeys.SetBinding(_hotkeyRecordingIndex, mods, vk);
            if (error.Length > 0)
            {
                // 不合法：留在录制态等用户换个组合，提示走卡片副标题
                _hotkeyHint = error;
                Render();
                return true;
            }

            _hotkeyHint = "";
            EndHotkeyRecording();
            return true;
        }

        private void EndHotkeyRecording()
        {
            int edited = _hotkeyRecordingIndex;
            _hotkeyRecordingIndex = -1;
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            // 键位存下来了 ≠ 立刻生效：总开关关着时一个都不注册。
            // 这正是「设好快捷键按下去没反应」最常见的成因，所以宁可挂一句提示。
            _hotkeyHint = edited >= 0 && MediaHotkeys.LastErrorIndex == edited ? MediaHotkeys.LastError
                : (edited >= 0 && !MediaHotkeys.IsEnabled && MediaHotkeys.IsBound(edited)
                    ? "已保存，打开右上角开关后生效" : "");
            Render();
        }

        private void CancelHotkeyRecording()
        {
            if (_hotkeyRecordingIndex < 0) return;
            _hotkeyRecordingIndex = -1;
            _hotkeyHint = "";
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            Render();
        }

        // ---- 交互设置页「唤出灵动岛」的录制（单键位，逻辑与上面那套同源） ----

        private bool HandleIslandHotkeyRecording(int vk) => HandleIslandHotkeyRecording(vk, ReadModsNow());

        private bool HandleIslandHotkeyRecording(int vk, uint mods)
        {
            if (!_islandHotkeyRecording) return false;

            // 只按修饰键不算「录了一个键」，留在录制态等主键。
            if (vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
                or Win32.VK_LWIN or Win32.VK_RWIN
                or Win32.VK_LSHIFT or Win32.VK_RSHIFT
                or Win32.VK_LCONTROL or Win32.VK_RCONTROL
                or Win32.VK_LMENU or Win32.VK_RMENU)
                return true;

            if (vk == Win32.VK_ESCAPE) { CancelIslandHotkeyRecording(); return true; }

            if (vk == Win32.VK_BACK)
            {
                IslandHotkey.ClearBinding();
                _islandHotkeyHint = "";
                EndIslandHotkeyRecording();
                return true;
            }

            string error = IslandHotkey.SetBinding(mods, vk);
            if (error.Length > 0)
            {
                // 不合法：留在录制态等用户换个组合，提示走卡片副标题
                _islandHotkeyHint = error;
                Render();
                return true;
            }

            _islandHotkeyHint = "";
            EndIslandHotkeyRecording();
            return true;
        }

        private void EndIslandHotkeyRecording()
        {
            _islandHotkeyRecording = false;
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            // 同媒体验收：存下来但开关没开时并不生效，得说一声。
            _islandHotkeyHint = IslandHotkey.LastError.Length > 0 ? IslandHotkey.LastError
                : (!IslandHotkey.IsEnabled && IslandHotkey.IsBound
                    ? "已保存，打开右上角开关后生效" : "");
            Render();
        }

        private void CancelIslandHotkeyRecording()
        {
            if (!_islandHotkeyRecording) return;
            _islandHotkeyRecording = false;
            _islandHotkeyHint = "";
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            Render();
        }
    }
}
