using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NotchPeninsula
{
    /// <summary>
    /// 极简的「弹个框告诉用户」：Win32 MessageBoxW，跑在线程池上。
    ///
    /// 为什么不用 WPF / 自绘窗口：调用点全在渲染线程（Draw 里），
    /// 那里既不能阻塞（一阻塞整个岛体连同动画一起停），也不该去建窗口类。
    /// 所以这里只做两件事：把消息丢进队列、由一个后台线程弹出来。
    /// 同一个 key 一次会话只弹一次 —— 否则连续失败会每秒弹一个框，比不弹还糟。
    /// </summary>
    internal static class UiAlert
    {
        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_SETFOREGROUND = 0x00010000;
        private const uint MB_TOPMOST = 0x00040000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static readonly ConcurrentDictionary<string, byte> Shown = new();
        private static int _workerStarted;

        /// <summary>弹一次提示；同一个 key 本会话只弹一次。</summary>
        public static void ShowOnce(string key, string message, string caption = "NotchPeninsula")
        {
            if (string.IsNullOrEmpty(key)) key = message.GetHashCode().ToString();
            if (!Shown.TryAdd(key, 0)) return;

            Logger.Warn($"[提示] {caption}: {message.Replace('\n', ' ')}");

            // 只为弹框起一个后台线程，弹完就退：不占常驻线程，也不受渲染线程阻塞影响
            var t = new Thread(() =>
            {
                try { MessageBoxW(IntPtr.Zero, message, caption, MB_OK | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST); }
                catch { /* 弹不出来也不能让进程挂掉 */ }
            })
            { IsBackground = true, Name = "NPS-Alert" };
            Interlocked.Exchange(ref _workerStarted, 1);
            t.Start();
        }

        /// <summary>人话的系统版本，例如 “Windows 11 24H2 (build 26100)”。</summary>
        public static string WindowsVersion()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                // Windows 11 的 build 号 >= 22000，但 Environment.OSVersion 在没加 manifest 时会谎报 6.2
                string name;
                if (v.Build >= 22000) name = "Windows 11";
                else if (v.Build >= 10240) name = "Windows 10";
                else name = "Windows " + v.Major + "." + v.Minor;

                string tag = v.Build switch
                {
                    >= 26100 => "24H2+",
                    >= 22631 => "23H2",
                    >= 22621 => "22H2",
                    >= 22000 => "21H2",
                    >= 19045 => "22H2",
                    >= 19044 => "21H2",
                    >= 19043 => "21H1",
                    _ => ""
                };
                return tag.Length > 0
                    ? $"{name} {tag} (build {v.Build})"
                    : $"{name} (build {v.Build})";
            }
            catch (Exception ex)
            {
                return "未知（" + ex.GetType().Name + "）";
            }
        }
    }
}
