using System.Text;

namespace NotchPeninsula
{
    // 全局快捷键的键名格式化与合法性校验：MediaHotkeys 与 IslandHotkey 共用同一张表。
    // 分开写会出现「一边补了 VK、另一边还显示 0x??」的不一致。
    internal static class HotkeyText
    {
        // 注册前必须至少带一个修饰键，否则全局键会把裸键从所有程序手里抢走。
        public static bool IsUsable(uint mods, int vk)
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

        public static string Format(uint mods, int vk)
        {
            var sb = new StringBuilder(24);
            if ((mods & Win32.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & Win32.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & Win32.MOD_SHIFT) != 0) sb.Append("Shift + ");
            if ((mods & Win32.MOD_WIN) != 0) sb.Append("Win + ");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(int vk) => vk switch
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

        // 组合串形如「修饰位|虚拟键码」，空串与坏值都返回 false（调用方保留原值）。
        public static bool TryParse(string raw, out uint mods, out int vk)
        {
            mods = 0;
            vk = 0;
            if (raw.Length == 0) return false;

            string[] parts = raw.Split('|');
            if (parts.Length != 2) return false;
            return uint.TryParse(parts[0], out mods) && int.TryParse(parts[1], out vk) && vk >= 0;
        }
    }
}
