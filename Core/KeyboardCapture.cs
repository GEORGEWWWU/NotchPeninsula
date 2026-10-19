using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    // 快捷键录制用的低层键盘钩子。
    //
    // 为什么不用客户区 WM_KEYDOWN 录：Win 组合、Alt+Tab、Alt+Space、Alt+F4、
    // Ctrl+Alt+方向（显卡旋转）这些在系统 / 驱动那层就被消费掉了，窗口根本收不到 ——
    // 表现是「点了录制框，按下去毫无反应」，而副作用（切窗口、窗口贴边）照旧发生，
    // 看起来就像设置窗自己在那儿跳。钩子在系统分发之前拿到键，同时吞掉，副作用一并没了。
    //
    // 钩子必须装在有消息循环的 UI 线程上，收尾必须 UnhookWindowsHookEx，
    // 否则键盘会一直停在「按下没反应」的状态。
    internal static class KeyboardCapture
    {
        // 与 ConsoleWindow 现有的 0x8000 + 偏移系列错开
        public const uint WM_CAPTURED_KEY = 0x8000 + 0x61;

        // 录制态最长挂 20 秒：钩子是全进程键盘入口，卡住不放等于把用户键盘锁死。
        private const long MaxHoldMs = 20000;

        private static IntPtr _hook = IntPtr.Zero;

        private static IntPtr _target = IntPtr.Zero;

        private static Win32.HookProc? _proc;   // 托管侧必须持有，否则 GC 之后回调直接崩

        private static long _startedAt;

        public static bool IsActive => _hook != IntPtr.Zero;

        public static bool Start(IntPtr targetHwnd)
        {
            if (_hook != IntPtr.Zero) return true;
            if (targetHwnd == IntPtr.Zero) return false;

            _proc ??= HookProc;
            _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                // 装不上不算致命：窗口键盘消息那条路还在，只是录不到系统级组合。
                Logger.Warn($"[快捷键录制] 键盘钩子安装失败，回落到窗口键盘消息：{Marshal.GetLastWin32Error()}");
                return false;
            }

            _target = targetHwnd;
            _startedAt = Environment.TickCount64;
            return true;
        }

        public static void Stop()
        {
            _target = IntPtr.Zero;
            if (_hook == IntPtr.Zero) return;
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || _target == IntPtr.Zero)
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

            if (Environment.TickCount64 - _startedAt > MaxHoldMs)
            {
                Logger.Warn("[快捷键录制] 挂起超时，自动释放键盘钩子");
                // 先留住句柄再 Stop：Stop 会把 _hook 清零，这里要传的是本次回调所属的那一个。
                IntPtr expired = _hook;
                Stop();
                return Win32.CallNextHookEx(expired, nCode, wParam, lParam);
            }

            // 只认按下：按住不放的自动重复也按一次算，SetBinding 本来就幂等。
            int msg = (int)wParam;
            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & Win32.LLKHF_INJECTED) == 0)
                {
                    // 回调里只做投递，重活留给窗口线程（钩子超时会被系统摘掉）。
                    Win32.PostMessage(_target, WM_CAPTURED_KEY,
                        (IntPtr)(int)info.vkCode, (IntPtr)(int)CurrentModifiers());
                }
            }

            // 一律吞掉：放行就会顺带触发系统快捷键。修饰键的 keyup 也吞，
            // 免得半按状态漏进系统（比如 Alt 单独松开去激活菜单）。
            return (IntPtr)1;
        }

        // 钩子里当场读物理键态：消息 PostMessage 之后，窗口线程处理时键可能已经松开了。
        private static uint CurrentModifiers()
        {
            uint mods = 0;
            if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0) mods |= Win32.MOD_CONTROL;
            if ((Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0) mods |= Win32.MOD_ALT;
            if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) mods |= Win32.MOD_SHIFT;
            if ((Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0) mods |= Win32.MOD_WIN;
            return mods;
        }
    }
}
