namespace NotchPeninsula
{
    // 「隐藏 / 唤出灵动岛」的全局快捷键（单键位，交互设置页）。
    // 独立于 MediaHotkeys：媒体那套的总开关关掉不能连带把岛也藏不回去；
    // 注册 ID 也错开（0x4E70 vs 0x4E60），两边互不影响。
    // 出厂不注册（总开关默认关）—— 全局热键是抢人家按键的东西，得用户自己点头。
    internal static class IslandHotkey
    {
        public const string Label = "唤出灵动岛";

        private const int Id = 0x4E70;

        private const string SettingKey = "IslandHotkey";

        private const string EnabledKey = "IslandHotkeyEnabled";

        private const uint DefaultMods = Win32.MOD_ALT;

        private const int DefaultVk = 0x49;   // I

        private static uint _mods = DefaultMods;

        private static int _vk = DefaultVk;

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

            string raw = key.GetValue(SettingKey, "") as string ?? "";
            // 没存过 / 坏值一律保留出厂默认（Alt + I），别让它变成「未设置」。
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

            uint oldMods = _mods;
            int oldVk = _vk;
            _mods = mods;
            _vk = vk;
            Persist();

            // 没开启时只落盘不注册（和媒体那套一样：先录好，开关一开就生效）。
            if (_suspended || !_enabled || _hwnd == IntPtr.Zero) { LastError = ""; return ""; }
            if (RegisterOne()) { LastError = ""; return ""; }

            // 新键位被别的程序占着：退回旧键位重新上岗，别让用户白丢一组能用的键。
            // 旧键位本身也注册不上（罕见）就把原因挂住，Esc 之后卡片上仍看得见。
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

            // 已经在系统里注册的要撤掉，否则这个键位还占着。
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
            NotchWindow.ToggleIslandVisibility();
        }

        public static string FormatKey()
            => IsBound ? HotkeyText.Format(_mods, _vk) : "未设置";

        private static void ApplyRegistration()
        {
            UnregisterAll();
            LastError = "";
            if (_suspended || !_enabled || _hwnd == IntPtr.Zero) return;

            if (!RegisterOne())
                LastError = $"{FormatKey()} 被其他程序占用，唤出灵动岛不生效";
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
