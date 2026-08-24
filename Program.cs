using System;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            // 必须调用！否则在高分屏（如4K显示器）上画面会模糊糊糊
            SetProcessDPIAware();

            var window = new NotchWindow();
            window.Run();
        }
    }
}