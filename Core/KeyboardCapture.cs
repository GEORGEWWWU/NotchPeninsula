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

        // 只按了修饰键（Ctrl / Shift / Alt / Win 的按下或松开）：不改键位，只让录制框把
        // 已按住的修饰键显示出来 —— 否则用户按了 Ctrl 毫无反应，会以为「根本不识别 Ctrl」。
        public const uint WM_CAPTURE_STATE = 0x8000 + 0x62;

        // 钩子到期自动释放：通知设置窗收掉录制态（否则框里一直停在「按下按键…」，按什么都没反应）。
        public const uint WM_CAPTURE_ABORT = 0x8000 + 0x63;

        // 录制态最长挂 20 秒：钩子是全进程键盘入口，卡住不放等于把用户键盘锁死。
        private const long MaxHoldMs = 20000;

        private static IntPtr _hook = IntPtr.Zero;

        private static IntPtr _target = IntPtr.Zero;

        private static Win32.HookProc? _proc;   // 托管侧必须持有，否则 GC 之后回调直接崩

        private static long _startedAt;

        // 自己记的修饰键状态。
        // 为什么不用 GetAsyncKeyState：它读的是「系统最新一次采样」，在钩子里跟修饰键的
        // 按下 / 松开是有时序竞争面的；而且我们为了不让组合键漏到系统里，把每个键都吞掉了
        // （return 1），修饰键的按下 / 松开事件只能由我们自己记，这样主键到达那一刻的组合
        // 一定是确定的，不依赖任何采样时机。
        private static uint _heldMods;

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
            _heldMods = 0;      // 上一轮可能停在半按状态，重新开始一律清零
            return true;
        }

        public static void Stop()
        {
            _target = IntPtr.Zero;
            _heldMods = 0;
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
                IntPtr target = _target;
                Stop();
                // 让设置窗自己收掉录制态：不然框里一直停在「按下按键…」，用户以为界面卡了。
                if (target != IntPtr.Zero) Win32.PostMessage(target, WM_CAPTURE_ABORT, IntPtr.Zero, IntPtr.Zero);
                return Win32.CallNextHookEx(expired, nCode, wParam, lParam);
            }

            int msg = (int)wParam;
            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & Win32.LLKHF_INJECTED) == 0)
                {
                    int vk = (int)info.vkCode;
                    uint bit = ModifierBit(vk);

                    if (bit != 0)
                    {
                        // 只按修饰键：记进状态，并通知界面把「Ctrl + …」画出来。
                        // 这里**不**投递 WM_CAPTURED_KEY —— 那会被当成「录了一个键」。
                        _heldMods |= bit;
                        Win32.PostMessage(_target, WM_CAPTURE_STATE,
                            IntPtr.Zero, (IntPtr)(int)_heldMods);
                    }
                    else
                    {
                        // 回调里只做投递，重活留给窗口线程（钩子超时会被系统摘掉）。
                        // 组合取「自己记的」并上「系统采样到的」：前者保证修饰键一定在内，
                        // 后者兜住录制开始前就按住的键（那种情况我们没收到过按下事件）。
                        uint mods = _heldMods | CurrentModifiers();
                        Win32.PostMessage(_target, WM_CAPTURED_KEY, (IntPtr)vk, (IntPtr)(int)mods);
                    }
                }
            }
            else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
            {
                // 修饰键松开：清位并同步给界面，否则录制框会一直挂着「Ctrl + …」。
                var up = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                uint bit = ModifierBit((int)up.vkCode);
                if (bit != 0 && (_heldMods & bit) != 0)
                {
                    _heldMods &= ~bit;
                    Win32.PostMessage(_target, WM_CAPTURE_STATE, IntPtr.Zero, (IntPtr)(int)_heldMods);
                }
            }

            // 一律吞掉：放行就会顺带触发系统快捷键。修饰键的 keyup 也吞，
            // 免得半按状态漏进系统（比如 Alt 单独松开去激活菜单）。
            return (IntPtr)1;
        }

        // 修饰键 → 修饰位。物理键在低层钩子里报的是 L/R 专用码，通用码一并认下。
        private static uint ModifierBit(int vk) => vk switch
        {
            Win32.VK_CONTROL or Win32.VK_LCONTROL or Win32.VK_RCONTROL => Win32.MOD_CONTROL,
            Win32.VK_MENU or Win32.VK_LMENU or Win32.VK_RMENU => Win32.MOD_ALT,
            Win32.VK_SHIFT or Win32.VK_LSHIFT or Win32.VK_RSHIFT => Win32.MOD_SHIFT,
            Win32.VK_LWIN or Win32.VK_RWIN => Win32.MOD_WIN,
            _ => 0u,
        };

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
