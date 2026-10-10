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

        private const float THEME_CARD_Y = 12f;

        private const float THEME_CARD_H = 104f;   // 两行：主题配色 + 背景材质

        private const float OPACITY_CARD_Y = THEME_CARD_Y + THEME_CARD_H + 14f;   // 130

        private const float OPACITY_CARD_H = 78f;

        private const float SIZE_CARD_Y = OPACITY_CARD_Y + OPACITY_CARD_H + 14f;  // 222

        private const float THEME_SEG_W = 150f;

        private const float THEME_SEG_X = WIDTH - CONTENT_TEXT_RM - THEME_SEG_W;   // 422

        private const float THEME_SEG_Y = 28f;

        // 第二行「背景材质」：位置全由 THEME_SEG_Y 派生，改一处其余自动跟
        private const float MAT_SEG_W = THEME_SEG_W;

        private const float MAT_SEG_X = THEME_SEG_X;

        private const float MAT_SEG_Y = THEME_SEG_Y + 42f;   // 70

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

        private bool HandleHotkeyRecording(int vk)
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

            uint mods = 0;
            if ((Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0) mods |= Win32.MOD_CONTROL;
            if ((Win32.GetKeyState(Win32.VK_MENU) & 0x8000) != 0) mods |= Win32.MOD_ALT;
            if ((Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0) mods |= Win32.MOD_SHIFT;
            if ((Win32.GetKeyState(Win32.VK_LWIN) & 0x8000) != 0
                || (Win32.GetKeyState(Win32.VK_RWIN) & 0x8000) != 0) mods |= Win32.MOD_WIN;

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
            MediaHotkeys.ResumeRegistration();
            _hotkeyHint = MediaHotkeys.LastErrorIndex == edited ? MediaHotkeys.LastError : "";
            Render();
        }

        private void CancelHotkeyRecording()
        {
            if (_hotkeyRecordingIndex < 0) return;
            _hotkeyRecordingIndex = -1;
            _hotkeyHint = "";
            MediaHotkeys.ResumeRegistration();
            Render();
        }
    }
}
