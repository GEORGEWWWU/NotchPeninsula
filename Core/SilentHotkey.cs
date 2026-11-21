namespace NotchPeninsula
{
    /// <summary>
    /// 「切换静默模式」的全局快捷键（单键位，交互设置页最下面那张卡）。
    ///
    /// <para>语义：非静默 → 静默；静默 → 回到「切走之前的那个模式」（待机还是普通由
    /// <see cref="Renderer.SilentReturnMode"/> 记着，从哪切走就回哪）。</para>
    ///
    /// <para>与 <see cref="IslandHotkey"/> 同一套实现骨架（注册 ID 错开 0x4E71 / 0x4E70，互不影响），
    /// 区别只有两点：<b>出厂关闭</b>、<b>出厂键位为空</b>（全局热键是抢按键的东西，得用户自己按一下）。</para>
    /// </summary>
    internal static class SilentHotkey
    {
        public const string Label = "切换静默模式";

        private const int Id = 0x4E71;

        private const string SettingKey = "SilentHotkey";

        private const string EnabledKey = "SilentHotkeyEnabled";

        // 出厂：空键位（不像 IslandHotkey 那样给 Alt+I，静默模式是更重的形态，必须用户主动设）
        private static uint _mods;

        private static int _vk;

        private static bool _live;

        private static IntPtr _hwnd;

        private static bool _suspended;

        private static bool _enabled;

        public static string LastError { get; private set; } = "";

        public static bool IsEnabled => _enabled;

        public static bool IsBound => _vk != 0;

        public static void Load(Microsoft.Win32.RegistryKey key)
        {
            _enabled = (int)key.GetValue(EnabledKey, 0) != 0;

            // 没存过 = 未设置：保持空键位，不塞默认值。
            string raw = key.GetValue(SettingKey, "") as string ?? "";
            if (!HotkeyText.TryParse(raw, out uint mods, out int vk)) return;
            if (vk != 0 && !HotkeyText.IsUsable(mods, vk)) return;
            _mods = mods;
            _vk = vk;
        }

        public static void Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;
            ApplyRegistration();
        }

        public static void Detach()
        {
            UnregisterAll();
            _hwnd = IntPtr.Zero;
        }

        public static void SetEnabled(bool on)
        {
            _enabled = on;
            Program.SaveSetting(EnabledKey, on ? 1 : 0);
            ApplyRegistration();
        }

        public static string SetBinding(uint mods, int vk)
        {
            if (vk == 0) { ClearBinding(); return ""; }
            if (!HotkeyText.IsUsable(mods, vk)) return "请至少搭配 Ctrl / Alt / Shift / Win";
            if (MediaHotkeys.FindConflict(mods, vk, out string label)) return $"已被「{label}」占用";
            if (HotkeyText.SameCombo(mods, vk, IslandHotkey.Binding.Mods, IslandHotkey.Binding.Vk))
                return $"已被「{IslandHotkey.Label}」占用";

            uint oldMods = _mods;
            int oldVk = _vk;
            _mods = mods;
            _vk = vk;
            Persist();

            // 没开启时只落盘不注册（先录好，开关一开就生效）。
            if (_suspended || !_enabled || _hwnd == IntPtr.Zero) { LastError = ""; return ""; }
            if (RegisterOne()) { LastError = ""; return ""; }

            // 新键位被别的程序占着：退回旧键位重新上岗。
            string msg = $"{HotkeyText.Format(mods, vk)} 已被其他程序占用";
            _mods = oldMods;
            _vk = oldVk;
            Persist();
            LastError = RegisterOne() ? "" : msg;
            return msg;
        }

        public static void ClearBinding()
        {
            _mods = 0;
            _vk = 0;
            LastError = "";

            if (_live && _hwnd != IntPtr.Zero)
            {
                Win32.UnregisterHotKey(_hwnd, Id);
                _live = false;
            }
            Persist();
        }

        public static void SuspendRegistration()
        {
            _suspended = true;
            UnregisterAll();
        }

        public static void ResumeRegistration()
        {
            _suspended = false;
            ApplyRegistration();
        }

        public static void Handle(int id)
        {
            if (id != Id || !_live) return;
            NotchWindow.ToggleSilentMode();
        }

        public static string FormatKey()
            => IsBound ? HotkeyText.Format(_mods, _vk) : "未设置";

        private static void ApplyRegistration()
        {
            UnregisterAll();
            LastError = "";
            if (_suspended || !_enabled || _hwnd == IntPtr.Zero) return;

            if (!RegisterOne())
                LastError = $"{FormatKey()} 被其他程序占用，切换静默模式不生效";
        }

        private static bool RegisterOne()
        {
            if (!IsBound) { _live = false; return true; }

            _live = Win32.RegisterHotKey(_hwnd, Id, _mods | Win32.MOD_NOREPEAT, (uint)_vk);

            if (!_live)
                Logger.Warn($"[全局快捷键] 注册失败：{Label} = {FormatKey()}");
            return _live;
        }

        private static void UnregisterAll()
        {
            if (!_live) return;
            if (_hwnd != IntPtr.Zero) Win32.UnregisterHotKey(_hwnd, Id);
            _live = false;
        }

        private static void Persist()
            => Program.SaveSetting(SettingKey, $"{_mods}|{_vk}");
    }
}
