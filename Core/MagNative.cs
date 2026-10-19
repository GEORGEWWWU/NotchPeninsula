using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    // Windows Magnification API（magnification.dll）的 P/Invoke 声明。
    //
    // 为什么要用它替代 BitBlt/StretchBlt 抓屏：
    //   · 它是 DWM 合成之后的抓取，**可以按 1:1 全分辨率**拿到屏幕像素（StretchBlt 只能顺带降采样，
    //     亚克力那套 1/12 缩略图对玻璃来说太糊了 —— 折射位移会被糊没）；
    //   · 它原生支持「排除窗口列表」（MW_FILTERMODE_EXCLUDE）：把岛体自己排掉之后，
    //     抓到的就是岛体**背后**的真实桌面，不用再像 IslandBackdrop.EraseIsland 那样
    //     拿左右邻像素横向插值去"抹掉自己"（那一步本身就是糊和错位的来源）。
    //
    // 代价：必须在 UI 线程建窗口、靠回调异步拿数据，且少数环境（老系统 / 远程桌面 / 捕获被拦）
    // 会拿不到画面 —— 所以 MagnifierBackdrop 有自检，失败一律回退 GDI 抓屏，用户无感。
    internal static class MagNative
    {
        // magnification.h：WC_MAGNIFIER / WC_MAGNIFIERRUNNER
        public const string MagnifierClass = "Magnifier";

        // magnifier window style：显示放大后的光标（我们窗口不可见，取哪个都行，保持与经典用法一致）
        public const int MS_SHOWMAGNIFIEDCURSOR = 0x0001;

        public const int WS_CHILD = 0x40000000;

        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int LWA_ALPHA = 0x00000002;

        // 渲染线程 → 放大镜宿主窗口：请求抓一帧（宿主窗口跑在 UI 线程，API 必须在那边调）
        public const uint WM_APP_MAG_CAPTURE = 0x8001u;

        // 任意线程 → 岛体窗口：把「放大镜初始化」补到 UI 线程上做
        public const uint WM_APP_ENSURE_MAG = 0x8002u;

        public const int SM_XVIRTUALSCREEN = 76;

        public const int SM_YVIRTUALSCREEN = 77;

        public enum MW_FILTERMODE
        {
            Exclude = 0,   // 列表里的窗口**不**出现在抓取结果中
            Include = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAGIMAGEHEADER
        {
            public uint width;

            public uint height;

            public uint stride;

            public uint offset;

            public IntPtr cbSize;
        }

        // srcdata = 源矩形的原始像素（未缩放）；destdata = 缩放后填给放大镜窗口的像素。
        // 返回 true 表示「我自己处理了」，系统不再做默认缩放拷贝。
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool MagImageScalingCallback(
            IntPtr hwnd,
            IntPtr srcdata,
            MAGIMAGEHEADER srcheader,
            IntPtr destdata,
            MAGIMAGEHEADER destheader,
            Win32.RECT unclipped,
            Win32.RECT clipped,
            IntPtr dirty);

        [DllImport("magnification.dll", SetLastError = true)]
        public static extern bool MagInitialize();

        [DllImport("magnification.dll", SetLastError = true)]
        public static extern bool MagUninitialize();

        [DllImport("magnification.dll", SetLastError = true)]
        public static extern bool MagSetWindowSource(IntPtr hwnd, Win32.RECT rect);

        [DllImport("magnification.dll", SetLastError = true)]
        public static extern bool MagSetWindowFilterList(IntPtr hwnd, MW_FILTERMODE mode, int count, IntPtr hwnds);

        [DllImport("magnification.dll", SetLastError = true)]
        public static extern bool MagSetImageScalingCallback(IntPtr hwnd, MagImageScalingCallback callback);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetrics(int index);
    }
}
