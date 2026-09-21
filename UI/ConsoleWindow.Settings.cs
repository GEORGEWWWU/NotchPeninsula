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
    }
}
