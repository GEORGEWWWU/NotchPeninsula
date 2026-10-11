namespace NotchPeninsula
{
    internal enum MediaHotkeyAction
    {
        PlayPause = 0,
        Previous = 1,
        Next = 2,
        SeekBackward = 3,
        SeekForward = 4,
    }

    internal static class MediaHotkeys
    {
        public const int Count = 5;

        public const double SeekStepSeconds = 3.0;

        private const int IdBase = 0x4E60;

        private static readonly string[] _settingKeys =
        [
            "MediaHotkey_PlayPause", "MediaHotkey_Previous", "MediaHotkey_Next",
            "MediaHotkey_SeekBack", "MediaHotkey_SeekForward",
        ];

        private static readonly string[] _labels =
            ["播放 / 暂停", "上一首", "下一首", "快退 3 秒", "快进 3 秒"];

        private static readonly uint[] _defaultMods =
        [
            Win32.MOD_CONTROL | Win32.MOD_ALT,
            Win32.MOD_CONTROL | Win32.MOD_ALT,
            Win32.MOD_CONTROL | Win32.MOD_ALT,
            Win32.MOD_ALT,
            Win32.MOD_ALT,
        ];

        private static readonly int[] _defaultVks =
            [Win32.VK_SPACE, Win32.VK_LEFT, Win32.VK_RIGHT, Win32.VK_LEFT, Win32.VK_RIGHT];

        private static readonly uint[] _mods = new uint[Count];
        private static readonly int[] _vks = new int[Count];

        private static readonly bool[] _live = new bool[Count];

        private static IntPtr _hwnd;
        private static bool _enabled;

        private static bool _suspended;

        public static bool IsEnabled => _enabled;

        public static string LastError { get; private set; } = "";

        public static int LastErrorIndex { get; private set; } = -1;

        static MediaHotkeys() => ResetToDefaults();

        public static string Label(int index) => _labels[index];
        public static uint Modifiers(int index) => _mods[index];
        public static int VirtualKey(int index) => _vks[index];

        public static bool IsBound(int index) => _vks[index] != 0;

        // 别的全局键位（交互设置页的「唤出灵动岛」）录制时用它躲开已有组合，
        // 否则两边绑同一组键，先注册的那个会把后者顶掉。
        public static bool FindConflict(uint mods, int vk, out string label)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_vks[i] != vk || _mods[i] != mods) continue;
                label = _labels[i];
                return true;
            }
            label = "";
            return false;
        }

        private static void ResetToDefaults()
        {
            for (int i = 0; i < Count; i++)
            {
                _mods[i] = _defaultMods[i];
                _vks[i] = _defaultVks[i];
            }
        }

        public static void Load(Microsoft.Win32.RegistryKey key)
        {
            _enabled = (int)key.GetValue("MediaHotkeyEnabled", 0) != 0;

            for (int i = 0; i < Count; i++)
            {
                string raw = key.GetValue(_settingKeys[i], "") as string ?? "";
                if (!HotkeyText.TryParse(raw, out uint mods, out int vk)) continue;
                // 坏值一律忽略，别让它进内存后注册不上。
                if (vk != 0 && !HotkeyText.IsUsable(mods, vk)) continue;
                _mods[i] = mods;
                _vks[i] = vk;
            }
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
            Program.SaveSetting("MediaHotkeyEnabled", on ? 1 : 0);
            ApplyRegistration();
        }

        public static string SetBinding(int index, uint mods, int vk)
        {
            if (index < 0 || index >= Count) return "无效的快捷键项";
            if (vk == 0) { ClearBinding(index); return ""; }
            if (!HotkeyText.IsUsable(mods, vk)) return "请至少搭配 Ctrl / Alt / Shift / Win";

            for (int i = 0; i < Count; i++)
            {
                if (i == index) continue;
                if (_vks[i] == vk && _mods[i] == mods) return $"已被「{_labels[i]}」占用";
            }

            uint oldMods = _mods[index];
            int oldVk = _vks[index];
            _mods[index] = mods;
            _vks[index] = vk;
            Persist(index);

            if (!_suspended && _enabled && _hwnd != IntPtr.Zero && !RegisterOne(index))
            {
                _mods[index] = oldMods;
                _vks[index] = oldVk;
                Persist(index);
                RegisterOne(index);   // 旧键位重新上岗
                return $"{FormatKey(mods, vk)} 已被其他程序占用";
            }
            return "";
        }

        public static void ClearBinding(int index)
        {
            if (index < 0 || index >= Count) return;

            _mods[index] = 0;
            _vks[index] = 0;

            // 已经在系统里注册的要撤掉，否则这个动作还占着那组键
            if (_live[index] && _hwnd != IntPtr.Zero)
            {
                Win32.UnregisterHotKey(_hwnd, IdBase + index);
                _live[index] = false;
            }
            Persist(index);
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
            int index = id - IdBase;
            if (index < 0 || index >= Count || !_live[index]) return;

            var media = MediaController.Instance;
            if (media == null) return;

            switch ((MediaHotkeyAction)index)
            {
                case MediaHotkeyAction.PlayPause: media.TogglePlayPause(); break;
                case MediaHotkeyAction.Previous: media.Previous(); break;
                case MediaHotkeyAction.Next: media.Next(); break;
                case MediaHotkeyAction.SeekBackward: media.SeekBy(-SeekStepSeconds); break;
                case MediaHotkeyAction.SeekForward: media.SeekBy(SeekStepSeconds); break;
            }
        }

        public static string FormatKey(uint mods, int vk) => HotkeyText.Format(mods, vk);

        public static string FormatKey(int index)
            => IsBound(index) ? FormatKey(_mods[index], _vks[index]) : "未设置";

        private static void ApplyRegistration()
        {
            UnregisterAll();
            LastError = "";
            LastErrorIndex = -1;
            if (_suspended || !_enabled || _hwnd == IntPtr.Zero) return;

            for (int i = 0; i < Count; i++)
            {
                if (RegisterOne(i)) continue;
                if (LastError.Length == 0)
                {
                    LastError = $"{FormatKey(i)} 被其他程序占用，{_labels[i]} 不生效";
                    LastErrorIndex = i;
                }
            }
        }

        private static bool RegisterOne(int index)
        {
            if (!IsBound(index)) { _live[index] = false; return true; }

            _live[index] = Win32.RegisterHotKey(
                _hwnd, IdBase + index, _mods[index] | Win32.MOD_NOREPEAT, (uint)_vks[index]);

            if (!_live[index])
                Logger.Warn($"[全局快捷键] 注册失败：{_labels[index]} = {FormatKey(index)}");
            return _live[index];
        }

        private static void UnregisterAll()
        {
            for (int i = 0; i < Count; i++)
            {
                if (!_live[i]) continue;
                if (_hwnd != IntPtr.Zero) Win32.UnregisterHotKey(_hwnd, IdBase + i);
                _live[i] = false;
            }
        }

        private static void Persist(int index)
            => Program.SaveSetting(_settingKeys[index], $"{_mods[index]}|{_vks[index]}");
    }
}
