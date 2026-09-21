using System.Runtime.InteropServices;

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
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
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
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLIPBOARDUPDATE = 0x031D; // 剪贴板内容变化系统消息
        public const int WM_MOUSEHOVER = 0x02A1;
        public const int WM_CAPTURECHANGED = 0x0215;  // 鼠标捕获被抢占/释放
        public const int WM_KEYDOWN = 0x0100;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_RBUTTON = 0x02;
        public const int VK_LBUTTON = 0x01;
        public const int SWP_SHOWWINDOW = 0x0040;

        // 自绘托盘菜单的内部私有消息（WM_APP 之后的自定义区间，绝不会和系统消息撞号）
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

        // 核心修复1：指定 CharSet.Unicode 让字符串正确传递给 Windows
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
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        // 32512 是 Windows 系统底层的标准箭头指针常量
        public const int IDC_ARROW = 32512;

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        // 拖动进度条期间把鼠标消息锁到本窗口：鼠标移出岛体也能继续收到 WM_MOUSEMOVE / WM_LBUTTONUP
        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

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

        // ==================== 多显示器工作区（自绘托盘菜单防止出屏） ====================
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

        // ==================== 剪贴板监听 ====================
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

        // ====================================================================
        // 传统打开文件对话框（comdlg32）
        //
        // 为什么不用 System.Windows.Forms.OpenFileDialog：
        //   WinForms 的 OpenFileDialog 在 .NET Core+ 上走的是 Vista「通用项对话框」
        //   （CLSID_FileOpenDialog），它会在**本进程内**拉起 ExplorerBrowser + 外壳命名空间
        //   + 图标/缩略图缓存。这些是进程级 DLL 与缓存，第一次打开就常驻 20~30MB，
        //   并且 Dispose 对话框、关闭资源管理器都不会归还（Windows 不会卸载已加载的外壳组件）。
        //   传统对话框只是 comdlg32 的一个普通模态窗口，完全不碰 ExplorerBrowser。
        // ====================================================================

        public const uint OFN_HIDEREADONLY = 0x00000004;
        public const uint OFN_NOCHANGEDIR = 0x00000008;
        public const uint OFN_PATHMUSTEXIST = 0x00000800;
        public const uint OFN_FILEMUSTEXIST = 0x00001000;
        public const uint OFN_EXPLORER = 0x00080000;

        // 注意：所有字符串字段一律用 IntPtr，刻意不用 string / StringBuilder。
        // 原因是 .NET 10 的 Marshal.SizeOf 对「含托管引用字段的结构体」会直接抛
        // ArgumentException("no meaningful size or offset can be computed")，泛型与非泛型重载都一样
        // （.NET Framework 时代可以，属于行为变更）。而 lStructSize 必须精确等于原生结构体大小，
        // 否则 comdlg32 会拒绝调用。只有全 IntPtr 的纯 blittable 结构体才能算出尺寸（x64 下为 152）。
        // 字符串由调用方 Marshal.StringToHGlobalUni 手工分配、finally 里释放。
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

        /// <summary>对话框出错时的扩展错误码；用户正常取消时返回 0。</summary>
        [DllImport("comdlg32.dll")]
        public static extern uint CommDlgExtendedError();

        // ==================== 全屏检测（「全屏自动隐藏」用） ====================
        // SHQueryUserNotificationState 是**系统自己的**「现在该不该打扰用户」判定，一次调用就拿到答案，
        // 不用自己 GetForegroundWindow + GetWindowRect + 比对显示器矩形（那套还要处理多显示器与边界误差）。
        // 而且它和「Windows 要不要压掉 Toast」用的是同一套标准，不会出现两套判据打架。
        //
        // 覆盖的场景正好是我们要的全部：
        //   · 全屏视频（浏览器全屏 / 播放器全屏）→ QUNS_BUSY
        //   · 全屏游戏（无边框全屏）           → QUNS_BUSY
        //   · 全屏游戏（独占模式 D3D）         → QUNS_RUNNING_D3D_FULL_SCREEN（矩形比对会漏掉这类）
        //   · 演示文稿模式                     → QUNS_BUSY / QUNS_PRESENTATION_MODE
        // ⚠️ 返回的是 **HRESULT（0 = S_OK）**，非 0 时 out 值不可信，调用方必须先看返回值。
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int pquns);

        public const int QUNS_NOT_PRESENT = 1;             // 屏保 / 锁屏 / 非活动的快速用户切换会话
        public const int QUNS_BUSY = 2;                    // 全屏应用运行中，或应用了演示文稿设置
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // 独占模式的全屏 Direct3D 应用
        public const int QUNS_PRESENTATION_MODE = 4;       // 演示文稿模式
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;   // 无上述状态，可以自由发通知
        public const int QUNS_QUIET_TIME = 6;              // 新用户首次登录 / 升级后的静默期
        public const int QUNS_APP = 7;                     // Windows 应用商店应用运行中（与全屏无关）
    }
}