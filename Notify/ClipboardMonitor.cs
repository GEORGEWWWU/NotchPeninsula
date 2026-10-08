using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NotchPeninsula
{
    public sealed class ClipboardMonitor
    {
        private static readonly Regex UrlRegex = new(
            @"^(?:https?://|ftp://|www\.)[^\s]+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public event Action<string>? OnUrlDetected;

        private IntPtr _hwnd;

        public void Attach(IntPtr hwnd)
        {
            if (_hwnd != IntPtr.Zero) return;
            _hwnd = hwnd;
            try { Win32.AddClipboardFormatListener(hwnd); }
            catch (Exception ex) { Logger.Error("[剪贴板] 监听注册失败", ex); }
        }

        public void Detach()
        {
            if (_hwnd == IntPtr.Zero) return;
            try { Win32.RemoveClipboardFormatListener(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }

        public void HandleClipboardUpdate()
        {
            string text = ReadClipboardText();
            if (string.IsNullOrWhiteSpace(text)) return;

            text = text.Trim();
            if (!UrlRegex.IsMatch(text)) return; // 非链接：直接忽略，不弹面板

            // 不做去重：即使复制的是同一个链接，也要每次都弹出
            OnUrlDetected?.Invoke(text);
        }

        private static string ReadClipboardText()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!Win32.OpenClipboard(IntPtr.Zero))
                {
                    System.Threading.Thread.Sleep(10);
                    continue;
                }
                try
                {
                    if (!Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT)) return "";
                    IntPtr handle = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return "";
                    IntPtr ptr = Win32.GlobalLock(handle);
                    if (ptr == IntPtr.Zero) return "";
                    try { return Marshal.PtrToStringUni(ptr) ?? ""; }
                    finally { Win32.GlobalUnlock(handle); }
                }
                catch { return ""; }
                finally { Win32.CloseClipboard(); }
            }
            return "";
        }
    }
}
