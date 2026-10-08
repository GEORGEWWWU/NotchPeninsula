using System.Runtime.InteropServices;
using System.Text;

namespace NotchPeninsula
{
    public static partial class Win32
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_MOVE = 0x0003;
        public const int WM_SIZE = 0x0005;
        public const int WM_ACTIVATE = 0x0006;   // 窗口被激活 / 失活（wParam 低 16 位是 WA_*）
        public const int WA_INACTIVE = 0;
        public const int WA_ACTIVE = 1;
        public const int WA_CLICKACTIVE = 2;
        public const int WM_TIMER = 0x0113;
        public const int WM_SETTINGCHANGE = 0x001A; // 系统设置变化广播（含「应用模式」浅色/深色切换）
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONDBLCLK = 0x0206;

        public const uint CS_DBLCLKS = 0x0008;

        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        public static int Low32(IntPtr v) => unchecked((int)v.ToInt64());

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_SETCURSOR = 0x0020;
        public const int WM_CLOSE = 0x0010;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const int SIZE_MINIMIZED = 1;
        public const int WM_PAINT = 0x000F;
        public const int IDC_HAND = 32649; // Windows 原生手型指针常量

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x00000002;
        public const int DIB_RGB_COLORS = 0;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLIPBOARDUPDATE = 0x031D; // 剪贴板内容变化系统消息
        public const int WM_MOUSEHOVER = 0x02A1;
        public const int WM_CAPTURECHANGED = 0x0215;  // 鼠标捕获被抢占/释放
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_CHAR = 0x0102;            // 插件市场搜索框的字符输入
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int VK_BACK = 0x08;
        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;
        // 自绘搜索框的编辑键（移动光标 / 框选 / 删除）
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;              // Alt（左 / 右通用码）
        public const int VK_CAPITAL = 0x14;           // CapsLock
        public const int VK_END = 0x23;
        public const int VK_HOME = 0x24;
        public const int VK_LEFT = 0x25;
        public const int VK_UP = 0x26;
        public const int VK_RIGHT = 0x27;
        public const int VK_DOWN = 0x28;
        public const int VK_DELETE = 0x2E;
        public const int VK_SPACE = 0x20;
        public const int VK_PRIOR = 0x21;             // PageUp
        public const int VK_NEXT = 0x22;              // PageDown
        public const int VK_A = 0x41;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;

        public const int WM_IME_STARTCOMPOSITION = 0x010D;
        public const int WM_IME_ENDCOMPOSITION = 0x010E;
        public const int WM_IME_COMPOSITION = 0x010F;
        public const int WM_IME_SETCONTEXT = 0x0281;
        //   候选窗那一位保留（选词还得靠它）。
        public const int ISC_SHOWUICANDIDATEWINDOW = 0x0001;
        public const int ISC_SHOWUICOMPOSITIONWINDOW = unchecked((int)0x80000000);
        public const int GCS_COMPSTR = 0x0008;
        public const int GCS_RESULTSTR = 0x0800;
        public const int CFS_POINT = 0x0002;
        public const int CFS_CANDIDATEPOS = 0x0040;
        public const int CFS_EXCLUDE = 0x0080;
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;
        public const int SWP_SHOWWINDOW = 0x0040;

        public const int WM_TRAYMENU_CLOSE = 0x8000 + 0x101;       // 请求销毁菜单窗口
        public const uint CF_UNICODETEXT = 13;         // 剪贴板 Unicode 文本格式

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ACCENT_POLICY
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWCOMPOSITIONATTRIBDATA
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        public const int ACCENT_DISABLED = 0;
        public const int ACCENT_ENABLE_GRADIENT = 1;
        public const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
        public const int ACCENT_ENABLE_BLURBEHIND = 3;
        public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        public const int ACCENT_ENABLE_HOSTBACKDROP = 5;

        public const int WCA_ACCENT_POLICY = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMWA_MICA_EFFECT = 1029;
        public const int DWMWCP_ROUND = 2;
        public const int DWMSBT_MAINWINDOW = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 核心修复2：API调用统一指定 Unicode
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpszClassName, string lpszWindowName, int style,
            int x, int y, int width, int height, IntPtr hwndParent, IntPtr hMenu, IntPtr hInst, IntPtr pvParam);

        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("msimg32.dll", SetLastError = true)]
        public static extern bool AlphaBlend(
            IntPtr hdcDest, int xoriginDest, int yoriginDest, int wDest, int hDest,
            IntPtr hdcSrc, int xoriginSrc, int yoriginSrc, int wSrc, int hSrc,
            BLENDFUNCTION blendFunction);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        public const uint TME_LEAVE = 0x00000002; // 订阅 WM_MOUSELEAVE（系统只发一次，进窗后需重新申请）

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_UPDATENOW = 0x0100;
        public const uint RDW_ERASE = 0x0004;

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
            [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        public const int IDC_ARROW = 32512;

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOMOVE_NOSIZE = 0x0001 | 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        // 媒体侧的「双击封面跳转对应应用」要用它们：

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

        public const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ---- 进程映像路径 ----

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
                                                              StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static string TryGetProcessImagePath(int pid)
        {
            if (pid <= 0) return "";

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                if (h == IntPtr.Zero) return "";

                int size = 1024;   // 单位是字符
                var sb = new StringBuilder(size);
                if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return "";
                return sb.ToString();
            }
            catch
            {
                return "";
            }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { CloseHandle(h); } catch { }
                }
            }
        }

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        // ---- 输入法（imm32）----
        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetContext(IntPtr hwnd);

        [DllImport("imm32.dll")]
        public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode, EntryPoint = "ImmGetCompositionStringW")]
        public static extern int ImmGetCompositionStringW(IntPtr himc, int dwIndex, byte[]? lpBuf, int dwBufLen);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCompositionWindow(IntPtr himc, ref COMPOSITIONFORM lpCompForm);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCandidateWindow(IntPtr himc, ref CANDIDATEFORM lpCandidate);

        [StructLayout(LayoutKind.Sequential)]
        public struct COMPOSITIONFORM
        {
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CANDIDATEFORM
        {
            public int dwIndex;
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        // 硬件监控原生 API
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        public const int WM_NCHITTEST = 0x0084;
        public const int HTCLIENT = 1;
        public const int HTTRANSPARENT = -1;

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // ---- 剪贴板监听 ----
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        public static extern bool GlobalUnlock(IntPtr hMem);

        // 传统打开文件对话框（comdlg32）

        public const uint OFN_HIDEREADONLY = 0x00000004;
        public const uint OFN_NOCHANGEDIR = 0x00000008;
        public const uint OFN_PATHMUSTEXIST = 0x00000800;
        public const uint OFN_FILEMUSTEXIST = 0x00001000;
        public const uint OFN_EXPLORER = 0x00080000;

        [StructLayout(LayoutKind.Sequential)]
        public struct OPENFILENAME
        {
            public uint lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;          // LPCWSTR
            public IntPtr lpstrCustomFilter;    // LPWSTR，这里不用
            public uint nMaxCustFilter;
            public uint nFilterIndex;
            public IntPtr lpstrFile;            // LPWSTR，输出缓冲区（字符数 = nMaxFile）
            public uint nMaxFile;
            public IntPtr lpstrFileTitle;
            public uint nMaxFileTitle;
            public IntPtr lpstrInitialDir;      // LPCWSTR
            public IntPtr lpstrTitle;           // LPCWSTR
            public uint Flags;
            public ushort nFileOffset;
            public ushort nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public uint dwReserved;
            public uint FlagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);

        [DllImport("comdlg32.dll")]
        public static extern uint CommDlgExtendedError();

        // ---- 全屏检测（「全屏自动隐藏」用） ----
        // 覆盖的场景正好是我们要的全部：
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int pquns);

        public const int QUNS_NOT_PRESENT = 1;             // 屏保 / 锁屏 / 非活动的快速用户切换会话
        public const int QUNS_BUSY = 2;                    // 全屏应用运行中，或应用了演示文稿设置
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // 独占模式的全屏 Direct3D 应用
        public const int QUNS_PRESENTATION_MODE = 4;       // 演示文稿模式
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;   // 无上述状态，可以自由发通知
        public const int QUNS_QUIET_TIME = 6;              // 新用户首次登录 / 升级后的静默期
        public const int QUNS_APP = 7;                     // Windows 应用商店应用运行中（与全屏无关）

        public const int WM_DROPFILES = 0x0233;

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        [DllImport("ole32.dll")]
        public static extern int OleInitialize(IntPtr pvReserved);

        public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

        public static bool EnsureOleInitialized()
        {
            if (_oleInitialized) return true;

            int hr = OleInitialize(IntPtr.Zero);
            if (hr == RPC_E_CHANGED_MODE)
            {
                // 但进程内 OLE 已就绪，拖放照样能用。
                _oleInitialized = true;
                return true;
            }
            if (hr < 0) return false;

            _oleInitialized = true;
            return true;
        }

        [ThreadStatic] private static bool _oleInitialized;

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int DoDragDrop(
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
            [MarshalAs(UnmanagedType.Interface)] IDropSource pDropSource,
            uint dwOKEffects,
            out uint pdwEffect);

        public const uint DROPEFFECT_COPY = 1;
        public const uint DROPEFFECT_MOVE = 2;
        public const uint DROPEFFECT_LINK = 4;

        public const int DRAGDROP_S_DROP = 0x00040100;              // 「可以放下了，结束拖放」
        public const int DRAGDROP_S_CANCEL = 0x00040101;            // 「取消这次拖放」
        public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102; // 「用系统默认的拖放光标」
        public const uint MK_LBUTTON = 0x0001;

        [ComImport, Guid("00000121-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropSource
        {
            [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);

            [PreserveSig] int GiveFeedback(uint dwEffect);
        }

        // OLE 拖入目标（IDropTarget）
        // 而且每次都带鼠标的实时坐标（屏幕物理像素）。

        public const uint DROPEFFECT_NONE = 0;
        public const uint CF_HDROP = 15;

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget pDropTarget);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM param);

        [ComImport, Guid("00000122-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropTarget
        {
            [PreserveSig] int DragEnter(
                [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
                uint grfKeyState, POINT pt, ref uint pdwEffect);

            [PreserveSig] int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect);

            [PreserveSig] int DragLeave();

            [PreserveSig] int Drop(
                [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
                uint grfKeyState, POINT pt, ref uint pdwEffect);
        }
    }
}