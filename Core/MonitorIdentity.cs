using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    /// <summary>
    /// 显示器的稳定身份。
    ///
    /// <c>Screen.AllScreens</c> 的下标只是「本次枚举顺序」，不是身份：
    /// 注销再登录、KVM/显示器 OSD 切换输入源、扩展坞热插拔之后，同一个下标会整体重排到
    /// 另一块物理屏上。于是「用户选的第 2 块屏」会在线一次登录后指向别处 —— 岛体看起来
    /// 自己跑去了一块不认识的显示器，设置里选回来、下次登录又漂走。
    ///
    /// 硬件 ID（如 <c>MONITOR\AOCB206\{4d36e96e-...}\0002</c>）跟着物理显示器走，
    /// 所以持久化用硬件 ID，运行时再解析成当前枚举下标。
    /// </summary>
    internal static class MonitorIdentity
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        /// <summary>取某个显示适配器（<c>\\.\DISPLAYn</c>）当前挂着的显示器硬件 ID，取不到返回 null。</summary>
        public static string? HardwareIdOfDevice(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return null;
            try
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(deviceName, 0, ref mon, 0)) return null;
                return string.IsNullOrWhiteSpace(mon.DeviceID) ? null : mon.DeviceID;
            }
            catch (Exception ex)
            {
                Logger.Error($"读取显示器硬件 ID 失败（{deviceName}）", ex);
                return null;
            }
        }

        /// <summary>当前下标对应的显示器硬件 ID（用于持久化）。</summary>
        public static string? HardwareIdOfIndex(int index)
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (index < 0 || index >= screens.Length) return null;
                return HardwareIdOfDevice(screens[index].DeviceName);
            }
            catch (Exception ex)
            {
                Logger.Error("按下标读取显示器硬件 ID 失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 主显示器在**当前**枚举顺序里的下标。不要再用 0 充当主屏：
        /// 顺序重排之后 0 指向哪块屏完全看运气，兜底落到 0 就是「岛自己跑去别的显示器」。
        /// </summary>
        public static int PrimaryIndex()
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                for (int i = 0; i < screens.Length; i++)
                {
                    if (screens[i].DeviceName == primary?.DeviceName) return i;
                }
                return screens.Length > 0 ? 0 : -1;
            }
            catch (Exception ex)
            {
                Logger.Error("解析主显示器下标失败", ex);
                return -1;
            }
        }

        /// <summary>
        /// 按硬件 ID 找到它在**当前**枚举顺序里的下标；该显示器不在线时返回 -1。
        /// 找不到不猜、不兜底 —— 调用方拿 -1 自己决定怎么退让。
        /// </summary>
        public static int IndexOfHardwareId(string? hardwareId)
        {
            if (string.IsNullOrWhiteSpace(hardwareId)) return -1;
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    string? id = HardwareIdOfDevice(screens[i].DeviceName);
                    if (id != null && string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("按硬件 ID 解析显示器下标失败", ex);
            }
            return -1;
        }
    }
}
