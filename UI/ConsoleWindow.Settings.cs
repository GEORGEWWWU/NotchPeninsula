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

        // 应用“消息通知内容”模式（0=缩略默认，1=紧凑，2=完整）
        // 完整模式：强制消息通知尺寸不小于容纳应用名的最小值，并把当前用户尺寸快照保存，便于切回时恢复；
        // 缩略/紧凑模式：尺寸均恢复为用户设定的值（紧凑的弹窗尺寸与缩略一致）。

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

        private float GetBtnY(int index)
        {
            return index switch
            {
                -1 => TITLE_BAR_HEIGHT + 35,
                0 => TITLE_BAR_HEIGHT + 147 + 40,
                1 => TITLE_BAR_HEIGHT + 147 + 40 + 34,
                7 => TITLE_BAR_HEIGHT + 147 + 40 + 68,
                2 => TITLE_BAR_HEIGHT + 299 + 40,
                3 => TITLE_BAR_HEIGHT + 299 + 40 + 34,
                4 => TITLE_BAR_HEIGHT + 417 + 40,
                5 => TITLE_BAR_HEIGHT + 417 + 40 + 34,
                6 => TITLE_BAR_HEIGHT + 535 + 40,
                _ => 0
            };
        }

        /// <summary>
        /// 弹出文件对话框挑选字体文件，选中后热替换灵动岛全部文本字体，并把路径写入注册表实现记忆化。
        /// 加载失败时不做任何改动，只在卡片副标题上提示原因。
        /// 走的是传统 Win32 对话框（见 <see cref="ShowOpenFileDialog"/>），不会把外壳组件拉进进程。
        /// </summary>
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

        /// <summary>恢复系统字体（等价于从未选择过自定义字体），并清空注册表里的记忆。</summary>

        private void ResetCustomFont()
        {
            FontConfig.ResetToSystemFont();
            Program.SaveSetting("CustomFontPath", "");
            _fontHint = "";
            Render();
        }

        // ================= 🎵 消息提示音 =================

        /// <summary>
        /// 应用提示音选项。索引 0 = 无；1..N = data\sound 里的第 i 个音频；
        /// <see cref="ToastSoundConfig.CustomIndex"/> = 「浏览音频…」（弹文件对话框挑自定义文件）。
        ///
        /// 选中即持久化。只有在**不处于静音档**时才试听一下 —— 否则用户每次切换都白响一声很烦。
        /// </summary>
        private void ApplyToastSound(int index)
        {
            if (index < 0 || index >= ToastSoundConfig.OptionCount) return;

            // 「浏览音频…」不是一次「选择」，而是打开文件对话框；挑完由 PickToastSound 自己收尾
            if (index == ToastSoundConfig.CustomIndex) { PickToastSound(); return; }

            ToastSoundConfig.SelectedIndex = index;
            Program.SaveSetting("ToastSoundIndex", index);

            if (index == 0)
            {
                // 切到「无」时把自定义路径也清掉：避免残留一条指向旧文件的记忆
                ToastSoundConfig.CustomPath = "";
                Program.SaveSetting("ToastSoundPath", "");
                _soundHint = "";
                ToastSoundPlayer.ClearQueue(); // 正在响的直接掐掉，别让「已选无」之后还响
            }
            else
            {
                _soundHint = "";
                // 顺手把提示音开关打开 —— 用户主动选了音源，意图就是要听，
                // 不然会陷入「选了但没声、还得自己去找那个开关」的迷惑。
                if (!ToastSoundConfig.IsEnabled)
                {
                    ToastSoundConfig.IsEnabled = true;
                    Program.SaveSetting("ToastSoundEnabled", 1);
                }
                PreviewToastSound();
            }
        }

        /// <summary>弹文件对话框挑选自定义提示音。校验不过就只在副标题上红字提示，不改动当前选择。</summary>
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
                    Program.SaveSetting("ToastSoundPath", picked);
                    Program.SaveSetting("ToastSoundIndex", ToastSoundConfig.CustomIndex);
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
                    // 失败时保持原选择不动，只提示原因（与字体选择的失败语义一致）
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

        /// <summary>重置提示音：关掉开关、回到「无」、音量回默认、清掉注册表里的自定义路径并停掉队列。</summary>
        private void ResetToastSound()
        {
            ToastSoundConfig.IsEnabled = false;
            ToastSoundConfig.SelectedIndex = 0;
            ToastSoundConfig.CustomPath = "";
            // ⚠️ 必须用 DefaultVolumePercent，不能写 VolumeOptions[1]（那是 10，与出厂默认是两回事）
            ToastSoundConfig.VolumePercent = ToastSoundConfig.DefaultVolumePercent;
            Program.SaveSetting("ToastSoundEnabled", 0);
            Program.SaveSetting("ToastSoundIndex", 0);
            Program.SaveSetting("ToastSoundPath", "");
            Program.SaveSetting("ToastSoundVolume", ToastSoundConfig.VolumePercent);
            ToastSoundPlayer.ClearQueue();
            _soundHint = "";
            Render();
        }

        /// <summary>试听当前选中的提示音。路径失效时不响，只把原因写到副标题。</summary>
        private void PreviewToastSound()
        {
            try
            {
                string? path = ToastSoundConfig.ResolveCurrentPath();
                if (path == null)
                {
                    // 只有「用户明确点了试听」才值得提示；切到「无」时静默即可
                    if (ToastSoundConfig.SelectedIndex != 0)
                        _soundHint = ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out string why) ? "" : why;
                    Render();
                    return;
                }
                // 试听走同一个队列：连续点几次也是依次响，不会叠成噪音
                ToastSoundPlayer.Enqueue(path, ToastSoundConfig.VolumePercent);
                Render();
            }
            catch (Exception ex)
            {
                Logger.Error("[提示音] 试听异常", ex);
            }
        }

        /// <summary>设置播放音量档位并持久化（不试听，避免连点下拉时连续响个不停）。</summary>
        private void ApplyToastSoundVolume(int index)
        {
            if (index < 0 || index >= ToastSoundConfig.VolumeOptions.Length) return;
            ToastSoundConfig.VolumePercent = ToastSoundConfig.VolumeOptions[index];
            Program.SaveSetting("ToastSoundVolume", ToastSoundConfig.VolumePercent);
            Render();
        }
    }
}
