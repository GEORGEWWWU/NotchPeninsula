using System.Text;

namespace NotchPeninsula
{
    /// <summary>
    /// 全局快捷键的五个动作。
    /// 顺序 = 媒体设置页「全局快捷键」卡片里的行序 = 热键 id 的偏移，改顺序要连 UI 一起改。
    /// </summary>
    internal enum MediaHotkeyAction
    {
        PlayPause = 0,
        Previous = 1,
        Next = 2,
        SeekBackward = 3,
        SeekForward = 4,
    }

    /// <summary>
    /// 媒体全局快捷键：注册到岛主窗口上的一组系统级热键。
    ///
    /// 为什么挂在岛窗口而不是设置窗口：设置窗口是可以关掉的，而「全局」的含义正是
    /// 「不打开设置也能用」——注册句柄必须跟着常驻窗口走。窗口建好后 Attach，
    /// 窗口销毁前 Detach（句柄失效后系统里的注册就成了收不回的残留）。
    ///
    /// 出厂状态是关闭：不注册任何热键，绝不占用用户的按键。用户在设置页打开开关才装上。
    /// 键位存在注册表里，格式 "修饰键位|虚拟键码"。
    /// </summary>
    internal static class MediaHotkeys
    {
        public const int Count = 5;

        /// <summary>快进 / 快退的步进秒数。</summary>
        public const double SeekStepSeconds = 3.0;

        // 热键 id 基址（"NP"）。id 由系统在 WM_HOTKEY 的 wParam 里回传，全窗口内必须唯一。
        private const int IdBase = 0x4E60;

        private static readonly string[] _settingKeys =
        [
            "MediaHotkey_PlayPause", "MediaHotkey_Previous", "MediaHotkey_Next",
            "MediaHotkey_SeekBack", "MediaHotkey_SeekForward",
        ];

        private static readonly string[] _labels =
            ["播放 / 暂停", "上一首", "下一首", "快退 3 秒", "快进 3 秒"];

        /// <summary>出厂默认键位：Ctrl+Alt+空格播停 / Ctrl+Alt+←→ 切歌 / Alt+←→ 步进 3 秒。</summary>
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

        /// <summary>该条当前是否真的挂在系统里（注册失败 / 未启用时为 false）。</summary>
        private static readonly bool[] _live = new bool[Count];

        private static IntPtr _hwnd;
        private static bool _enabled;

        /// <summary>全局快捷键总开关（默认关闭 = 出厂不劫持任何按键）。</summary>
        public static bool IsEnabled => _enabled;

        /// <summary>最近一次注册失败的原因（空串 = 全部正常）。设置页拿它当提示文案。</summary>
        public static string LastError { get; private set; } = "";

        static MediaHotkeys() => ResetToDefaults();

        public static string Label(int index) => _labels[index];
        public static uint Modifiers(int index) => _mods[index];
        public static int VirtualKey(int index) => _vks[index];

        /// <summary>这条是否绑了键（录制时按 Backspace 清空后为 false，界面上显示「未设置」）。</summary>
        public static bool IsBound(int index) => _vks[index] != 0;

        private static void ResetToDefaults()
        {
            for (int i = 0; i < Count; i++)
            {
                _mods[i] = _defaultMods[i];
                _vks[i] = _defaultVks[i];
            }
        }

        /// <summary>
        /// 启动时从注册表读回配置。只读不注册 —— 那时宿主窗口还没建好，注册要等 Attach。
        /// 某一条读不出来（老版本没写过 / 值被写坏）就保留出厂默认，不留半个空键位。
        /// </summary>
        public static void Load(Microsoft.Win32.RegistryKey key)
        {
            _enabled = (int)key.GetValue("MediaHotkeyEnabled", 0) != 0;

            for (int i = 0; i < Count; i++)
            {
                string raw = key.GetValue(_settingKeys[i], "") as string ?? "";
                if (!TryParse(raw, out uint mods, out int vk)) continue;
                // vk = 0 是合法的「清空」态（用户按过 Backspace）；非 0 才需要校验组合是否可用，
                // 坏值一律忽略，别让它进内存后注册不上。
                if (vk != 0 && !IsUsable(mods, vk)) continue;
                _mods[i] = mods;
                _vks[i] = vk;
            }
        }

        /// <summary>绑定宿主窗口并按当前开关把热键装上。窗口重建时再调一次即可（内部先全撤）。</summary>
        public static void Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;
            ApplyRegistration();
        }

        /// <summary>宿主窗口销毁前调用：把系统里的注册全撤掉，避免句柄失效后残留。</summary>
        public static void Detach()
        {
            UnregisterAll();
            _hwnd = IntPtr.Zero;
        }

        /// <summary>总闸：开启时逐条注册，关闭时全部注销。立刻落盘。</summary>
        public static void SetEnabled(bool on)
        {
            _enabled = on;
            Program.SaveSetting("MediaHotkeyEnabled", on ? 1 : 0);
            ApplyRegistration();
        }

        /// <summary>
        /// 改一条键位。返回空串表示成功，非空是给用户看的失败原因。
        ///
        /// 注册失败（组合被别的程序占用）时把旧键位原样还回去 —— 不让用户落到
        /// 「改完这条就再也没快捷键了」的状态。
        /// </summary>
        public static string SetBinding(int index, uint mods, int vk)
        {
            if (index < 0 || index >= Count) return "无效的快捷键项";
            if (vk == 0) { ClearBinding(index); return ""; }
            if (!IsUsable(mods, vk)) return "请至少搭配 Ctrl / Alt / Shift / Win";

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

            if (_enabled && _hwnd != IntPtr.Zero && !RegisterOne(index))
            {
                _mods[index] = oldMods;
                _vks[index] = oldVk;
                Persist(index);
                RegisterOne(index);   // 旧键位重新上岗
                return $"{FormatKey(mods, vk)} 已被其他程序占用";
            }
            return "";
        }

        /// <summary>
        /// 清空一条绑定（录制时按 Backspace）：留空 = 这个动作没有全局快捷键。
        /// 键位写成 0|0 落盘，下次启动仍是空 —— 不能靠「删键」表达，那样会被当成未配置而回落到默认值。
        /// </summary>
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

        /// <summary>
        /// 录制期间把热键全部撤下。否则用户按下的那一组恰好就是当前键位时，
        /// 系统会直接触发动作（「按一下看看录成什么」变成「真的切了一首歌」）。
        /// </summary>
        public static void SuspendRegistration() => UnregisterAll();

        /// <summary>录制结束（成功或取消）后按开关重新装上。</summary>
        public static void ResumeRegistration() => ApplyRegistration();

        /// <summary>WM_HOTKEY 分派：按 id 找到动作交给媒体控制器。管它是谁在前台。</summary>
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

        /// <summary>屏显文本，顺序固定 Ctrl → Alt → Shift → Win（与用户按下的先后无关）。</summary>
        public static string FormatKey(uint mods, int vk)
        {
            var sb = new StringBuilder(24);
            if ((mods & Win32.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & Win32.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & Win32.MOD_SHIFT) != 0) sb.Append("Shift + ");
            if ((mods & Win32.MOD_WIN) != 0) sb.Append("Win + ");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        /// <summary>某一行的屏显文本（读当前键位）。清空过的显示「未设置」而不是空白。</summary>
        public static string FormatKey(int index)
            => IsBound(index) ? FormatKey(_mods[index], _vks[index]) : "未设置";

        /// <summary>
        /// 主键名。常用键走自建表（英文缩写，不受输入法 / 语言环境影响），字母数字直接取字符。
        /// 认不出来的退回 0xNN，总比显示空白强。
        /// </summary>
        private static string KeyName(int vk) => vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => "小键盘" + (vk - 0x60),
            0x6A => "小键盘 *",
            0x6B => "小键盘 +",
            0x6D => "小键盘 -",
            0x6E => "小键盘 .",
            0x6F => "小键盘 /",
            >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "0x" + vk.ToString("X2"),
        };

        /// <summary>
        /// 一条键位是否可用：必须带至少一个修饰键。
        /// 裸键（单独的 F5、单独的字母）会全局吃掉那个按键，风险太大，一律不收。
        /// </summary>
        private static bool IsUsable(uint mods, int vk)
        {
            const uint anyMod = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_WIN;
            if ((mods & anyMod) == 0) return false;
            if (vk <= 0) return false;
            // 主键不能又是修饰键（Ctrl+Alt+Ctrl 这种）
            return vk is not (Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
                or Win32.VK_LWIN or Win32.VK_RWIN
                or Win32.VK_LSHIFT or Win32.VK_RSHIFT or Win32.VK_LCONTROL
                or Win32.VK_RCONTROL or Win32.VK_LMENU or Win32.VK_RMENU);
        }

        /// <summary>先全撤再按开关重建。幂等 —— 重复调用不会留下重复注册。</summary>
        private static void ApplyRegistration()
        {
            UnregisterAll();
            LastError = "";
            if (!_enabled || _hwnd == IntPtr.Zero) return;

            for (int i = 0; i < Count; i++)
            {
                if (RegisterOne(i)) continue;
                if (LastError.Length == 0)
                    LastError = $"{FormatKey(i)} 被其他程序占用，{_labels[i]} 不生效";
            }
        }

        private static bool RegisterOne(int index)
        {
            // 清空过的条目不上报系统，也不算失败 —— 它是用户明确要求的「这个动作没有快捷键」
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

        /// <summary>解析 "修饰键位|虚拟键码"。格式不对就返回 false，调用方保留原值。</summary>
        private static bool TryParse(string raw, out uint mods, out int vk)
        {
            mods = 0;
            vk = 0;
            if (raw.Length == 0) return false;

            string[] parts = raw.Split('|');
            if (parts.Length != 2) return false;
            // vk = 0 是合法的「清空」态（见 ClearBinding），所以这里只排除负数
            return uint.TryParse(parts[0], out mods) && int.TryParse(parts[1], out vk) && vk >= 0;
        }
    }
}
