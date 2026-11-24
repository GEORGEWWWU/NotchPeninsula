using SkiaSharp;

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

        private static bool GlassParameterAvailable(int index) => (uint)index < GlassMaterial.Keys.Length;

        private static void ApplyGlassLevel(int index, int level)
        {
            if (!GlassParameterAvailable(index)) return;
            GlassMaterial.SetLevel(index, level);
            Program.SaveSetting(GlassMaterial.Keys[index], GlassMaterial.GetLevel(index));
        }

        private static void ResetGlassDefaults()
        {
            for (int i = 0; i < GlassMaterial.Keys.Length; i++)
                ApplyGlassLevel(i, GlassMaterial.DefaultLevel(i));
        }

        private static readonly string[] PassthroughLabels = ["可交互", "完全穿透"];
        private bool _passthroughDropdownOpen;
        private bool _passthroughDropdownHovered;
        private int _hoveredPassthroughOption = -1;

        private SKRect PassthroughDropdownBox
        {
            get
            {
                float centerY = TITLE_BAR_HEIGHT + PASS_CARD_Y + ROW_ANCHOR_Y;
                return SKRect.Create(LeftColumnRight - 16f - 110f, centerY - 14f, 110f, 28f);
            }
        }

        private SKRect PassthroughToggleBox
        {
            get
            {
                var box = PassthroughDropdownBox;
                return SKRect.Create(box.Left - 10f - 42f, box.MidY - TOGGLE_TRACK_H / 2f, 42f, TOGGLE_TRACK_H);
            }
        }

        private int HitPassthroughOption(int x, int y)
        {
            var box = PassthroughDropdownBox;
            return x >= box.Left && x < box.Right
                ? DropdownRowAt(box.Bottom + 2f, PassthroughLabels.Length, y)
                : -1;
        }

        private const float THEME_CARD_Y = CARD_TOP_Y;
        private const float THEME_CARD_H = 112f;
        private const float IMMERSIVE_CARD_Y = THEME_CARD_Y + THEME_CARD_H + CARD_GAP;
        private const float OPACITY_CARD_Y = IMMERSIVE_CARD_Y + TOGGLE_CARD_H + CARD_GAP;

        private bool _immersiveToggleHovered;

        private bool HitImmersiveToggle(int x, int y)
        {
            float right = LeftColumnRight - 16f;
            float centerY = TITLE_BAR_HEIGHT + IMMERSIVE_CARD_Y + ROW_ANCHOR_Y;
            return y < ContentBottom
                && new SKRect(right - 42f, centerY - TOGGLE_TRACK_H / 2f, right, centerY + TOGGLE_TRACK_H / 2f).Contains(x, y);
        }
        private const float OPACITY_CARD_H = 48f;
        private static bool ShowOpacityCard => !Renderer.IslandLiquidGlass && !Renderer.IslandPluginBackground;
        private const float GLASS_CARD_Y = SIZE_CARD_Y + 150f + CARD_GAP;

        private const float PERSONALIZE_DD_W = 110f;
        private const float PERSONALIZE_DD_H = 28f;
        private const float MAT_TITLE_DY = 76f;
        private const float MAT_SUB_DY = 96f;

        private static readonly string[] ThemeLabels = ["黑", "白", "系统"];
        private static readonly string[] MaterialLabels = ["实体", "亚克力", "液态玻璃", "插件背景"];
        private static readonly string[] OpacityLabels = ["0%", "25%", "50%", "75%", "100%"];
        private static readonly string[] GlassRefreshLabels = LiquidGlass.ValueTexts(LiquidGlass.P_CaptureHz);

        private DropdownKind _personalizeDropdownOpen;
        private DropdownKind _personalizeDropdownHovered;
        private int _hoveredPersonalizeOption = -1;
        private int _draggedGlassParam = -1;

        private SKRect PersonalizeDropdownBox(DropdownKind kind)
        {
            float right = LeftColumnRight - 16f;
            float y = TITLE_BAR_HEIGHT + (kind switch
            {
                DropdownKind.Theme => THEME_CARD_Y + 16f,
                DropdownKind.Material => THEME_CARD_Y + 66f,
                _ => OPACITY_CARD_Y + (OPACITY_CARD_H - PERSONALIZE_DD_H) / 2f
            });
            return new SKRect(right - PERSONALIZE_DD_W, y, right, y + PERSONALIZE_DD_H);
        }

        private static (string[] Options, int Selected) PersonalizeDropdownOptions(DropdownKind kind) => kind switch
        {
            DropdownKind.Theme => (ThemeLabels, Renderer.ThemeMode),
            DropdownKind.Material => (MaterialLabels, Renderer.IslandMaterialIndex),
            _ => (OpacityLabels, Renderer.IslandImmersiveMode ? Renderer.ImmersiveCoverOpacity : Renderer.BgOpacityLevel)
        };

        private int HitPersonalizeOption(int x, int y)
        {
            var box = PersonalizeDropdownBox(_personalizeDropdownOpen);
            return x >= box.Left && x < box.Right
                ? DropdownRowAt(box.Bottom + 2f, PersonalizeDropdownOptions(_personalizeDropdownOpen).Options.Length, y)
                : -1;
        }

        private const float SIZE_CARD_Y = CARD_TOP_Y;

        private const float GLASS_ROW0_Y = 42f;
        private const float GLASS_ROW_H = 32f;
        private const float GLASS_COL_GAP = 12f;
        private const int GlassColumnCount = 2;
        private float GLASS_COL_W =>
            (WIDTH - CONTENT_TEXT_RM - RightColumnText - GLASS_COL_GAP) / GlassColumnCount;
        private const float GLASS_LABEL_W = 38f;
        private const float GLASS_VALUE_W = 52f;
        private const float GLASS_TRACK_HIT_PAD = 6f;

        private SKRect GlassTrackBounds(int index)
        {
            int column = index % GlassColumnCount;
            int row = index / GlassColumnCount;
            float x = RightColumnText + column * (GLASS_COL_W + GLASS_COL_GAP);
            float y = TITLE_BAR_HEIGHT + GLASS_CARD_Y + GLASS_ROW0_Y + row * GLASS_ROW_H + 12.5f;
            return new SKRect(x + GLASS_LABEL_W, y - 12f, x + GLASS_COL_W - GLASS_VALUE_W, y + 12f);
        }

        private int HitGlassTrack(int x, int y)
        {
            if (!Renderer.IslandLiquidGlass || y >= ContentBottom) return -1;
            for (int i = 0; i < GlassMaterial.Keys.Length; i++)
            {
                var hit = GlassTrackBounds(i);
                hit.Inflate(GLASS_TRACK_HIT_PAD, 0f);
                if (hit.Contains(x, y)) return i;
            }
            return -1;
        }

        private int GlassLevelAt(int index, int x)
        {
            var track = GlassTrackBounds(index);
            int maxLevel = GlassMaterial.MaxLevel(index);
            float progress = Math.Clamp((x - track.Left) / track.Width, 0f, 1f);
            return (int)MathF.Round(progress * maxLevel);
        }

        private const float GLASS_RESET_W = 96f;
        private const float GLASS_RESET_H = 26f;
        private float GLASS_RESET_X => WIDTH - CONTENT_TEXT_RM - GLASS_RESET_W;
        // 「恢复默认参数」上移到卡片右上角、和说明文字同一行：
        // 原来压在最后一行滑轨下面，既多占一条卡高，又容易在拖完最后一根滑轨时顺手点到。
        private const float GLASS_RESET_TOP_Y = 8f;
        private int GlassRowCount => (GlassMaterial.Keys.Length + GlassColumnCount - 1) / GlassColumnCount;
        private static float GlassResetTopY => GLASS_RESET_TOP_Y;
        private float GlassCardH => GLASS_ROW0_Y + GlassRowCount * GLASS_ROW_H + 12f;

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

        // 进入录制态的统一入口：挂低层钩子 + 把键盘焦点抢到设置窗。
        // 钩子装不上时只剩「窗口键盘消息」这条路，而那条路要求本窗口是前台并持有焦点——
        // 否则按键全落到别的窗口上，观感就是「点了录制框，按什么都没反应」。
        private void BeginKeyCapture()
        {
            bool hooked = KeyboardCapture.Start(_hwnd);

            if (_hwnd != IntPtr.Zero)
            {
                Win32.SetForegroundWindow(_hwnd);   // 前台被锁定时会失败，失败也无害
                Win32.SetFocus(_hwnd);
            }

            if (!hooked)
            {
                const string fallback = "键盘钩子不可用：只能在设置窗内录制，系统组合（Win / Alt+Tab）录不到";
                if (_hotkeyRecordingIndex >= 0) _hotkeyHint = fallback;
                else if (_silentHotkeyRecording) _silentHotkeyHint = fallback;
                else _islandHotkeyHint = fallback;
            }
        }

        // 钩子路径（KeyboardCapture）的入口：修饰键由钩子当场读好带过来，
        // 窗口自己再读 GetKeyState 已经没有意义 —— 那会儿手早松了。
        private bool HandleCapturedKey(int vk, uint mods)
        {
            if (_hotkeyRecordingIndex >= 0) return HandleHotkeyRecording(vk, mods);
            if (_islandHotkeyRecording) return HandleIslandHotkeyRecording(vk, mods);
            if (_silentHotkeyRecording) return HandleSilentHotkeyRecording(vk, mods);
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
            {
                // 只按修饰键不算「录了一个键」，留在录制态等主键；
                // 但界面必须立刻有反应（框里显示「Ctrl + …」），否则用户会以为修饰键没被识别。
                _captureLiveMods = mods;
                Render();
                return true;
            }

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
            _captureLiveMods = 0;
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
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
            _captureLiveMods = 0;
            _hotkeyHint = "";
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
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
            {
                _captureLiveMods = mods;
                Render();
                return true;
            }

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
            _captureLiveMods = 0;
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
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
            _captureLiveMods = 0;
            _islandHotkeyHint = "";
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
            Render();
        }

        // ---- 交互设置页「静默模式」的录制（单键位，逻辑与上面那套同源） ----

        private bool HandleSilentHotkeyRecording(int vk) => HandleSilentHotkeyRecording(vk, ReadModsNow());

        private bool HandleSilentHotkeyRecording(int vk, uint mods)
        {
            if (!_silentHotkeyRecording) return false;

            if (vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
                or Win32.VK_LWIN or Win32.VK_RWIN
                or Win32.VK_LSHIFT or Win32.VK_RSHIFT
                or Win32.VK_LCONTROL or Win32.VK_RCONTROL
                or Win32.VK_LMENU or Win32.VK_RMENU)
            {
                _captureLiveMods = mods;
                Render();
                return true;
            }

            if (vk == Win32.VK_ESCAPE) { CancelSilentHotkeyRecording(); return true; }

            if (vk == Win32.VK_BACK)
            {
                SilentHotkey.ClearBinding();
                _silentHotkeyHint = "";
                EndSilentHotkeyRecording();
                return true;
            }

            string error = SilentHotkey.SetBinding(mods, vk);
            if (error.Length > 0)
            {
                _silentHotkeyHint = error;
                Render();
                return true;
            }

            _silentHotkeyHint = "";
            EndSilentHotkeyRecording();
            return true;
        }

        private void EndSilentHotkeyRecording()
        {
            _silentHotkeyRecording = false;
            _captureLiveMods = 0;
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
            // 存下来但开关没开时并不生效，得说一声。
            _silentHotkeyHint = SilentHotkey.LastError.Length > 0 ? SilentHotkey.LastError
                : (!SilentHotkey.IsEnabled && SilentHotkey.IsBound
                    ? "已保存，打开右上角开关后生效" : "");
            Render();
        }

        private void CancelSilentHotkeyRecording()
        {
            if (!_silentHotkeyRecording) return;
            _silentHotkeyRecording = false;
            _captureLiveMods = 0;
            _silentHotkeyHint = "";
            KeyboardCapture.Stop();
            MediaHotkeys.ResumeRegistration();
            IslandHotkey.ResumeRegistration();
            SilentHotkey.ResumeRegistration();
            Render();
        }
    }
}
