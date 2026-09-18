using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NotchPeninsula
{
    /// <summary>
    /// 轻量剪贴板监听：基于 Win32 AddClipboardFormatListener 事件驱动（非轮询），
    /// 仅在剪贴板内容变化时读取一次文本，只有识别到链接才向上抛事件，稳态零 CPU / 零额外内存占用。
    /// </summary>
    public sealed class ClipboardMonitor
    {
        // 仅把「整段文本就是一个链接」视作有效链接，避免把正文里的半截 URL 也弹出来
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

        /// <summary>由 WndProc 在收到 WM_CLIPBOARDUPDATE 时调用。每次剪贴板内容变化（即每次复制/剪切）都会触发。</summary>
        public void HandleClipboardUpdate()
        {
            string text = ReadClipboardText();
            if (string.IsNullOrWhiteSpace(text)) return;

            text = text.Trim();
            if (!UrlRegex.IsMatch(text)) return; // 非链接：直接忽略，不弹面板

            // 不做去重：即使复制的是同一个链接，也要每次都弹出
            OnUrlDetected?.Invoke(text);
        }

        // 直接走 Win32 读取剪贴板：可在任意线程调用，且剪贴板被其它进程短暂占用时有轻量重试
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
