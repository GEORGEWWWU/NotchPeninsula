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
        // 双击：只有窗口类带 CS_DBLCLKS 时系统才会派发它（同一位置的第二次按下由它取代普通
        // WM_LBUTTONDOWN）。岛体类已在 NotchWindow 里声明该样式，媒体控制的双击跳转靠它。
        public const int WM_LBUTTONDBLCLK = 0x0203;
        // 右键双击同理（同一位置第二次按下取代普通 WM_RBUTTONDOWN）。
        // 岛体用它实现「插件注册接收双击后的右键透传」：插件的详情页想在面板里吃右键，
        // 就只能等这一条 —— 第一下 RBUTTONDOWN 先挂待定，这一条到了才通知插件（见 NotchWindow）。
        public const int WM_RBUTTONDBLCLK = 0x0206;

        // 窗口类样式：注册时声明「本类窗口要收双击消息」，否则系统永不派发 WM_LBUTTONDBLCLK
        public const uint CS_DBLCLKS = 0x0008;

        /// <summary>
        /// 系统的「双击判定间隔」（毫秒）。
        ///
        /// 岛体用它把一次左键按下与紧随其后的 WM_LBUTTONDBLCLK 认成同一次手势：折叠态左半边单击
        /// 会展开媒体面板（见 NotchWindow 的高度折叠态分支），若第二下也被当成独立点击，它就会落在
        /// 刚铺开的展开面板封面上、被双击跳转吃掉 —— 用户看到的正是「点一下左半边，应用被打开了」。
        /// 取系统值而不是写死 300ms，是为了与「系统肯把第二下升格成双击消息」的那个窗口严格同源。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();

        /// <summary>
        /// 调用线程消息队列里最新一条按键消息的状态（高位 0x8000 = 按下）。
        /// 自绘搜索框判 Shift / Ctrl 组合键用它：组合键是「按住时按别的键」，
        /// 自己记按下/抬起容易被焦点切换、Alt+Tab 弄脏状态，系统这份最准。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        /// <summary>
        /// 消息参数取低 32 位。窗口过程里读 wParam / lParam 一律走这里，别直接 ToInt32()。
        ///
        /// IntPtr.ToInt32() 只在「值正好塞得进 int」时才不抛：64 位下这两个参数的高位并不总是 0 ——
        /// WM_IME_SETCONTEXT / WM_IME_COMPOSITION 的高位挂着 IME 上下文句柄，坐标类消息的打包值在
        /// 坐标为负时 bit31 也是 1。碰到这种值它直接抛 OverflowException，异常从窗口过程逃出去
        /// 就是整个进程崩掉（WndProc 没有调用方能接住）。
        /// </summary>
        public static int Low32(IntPtr v) => unchecked((int)v.ToInt64());

        /// <summary>
        /// 注册一条系统级热键：无论前台是谁，按下组合键都会向 hWnd 投一条 WM_HOTKEY，
        /// wParam = id（比 lParam 里的键位可靠得多，直接按 id 分派即可）。
        /// 同一个组合同一时刻全系统只能注册一次，被别的程序占用时返回 false（GetLastError = 1409）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        /// <summary>注销一条热键。必须与注册时的 hWnd / id 成对，否则会一直占着那个组合。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>滚轮消息：wParam 高字是 ±120 的整数倍，低字是按键状态；lParam 是屏幕坐标。</summary>
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
        /// <summary>按住 Alt 时后续按键走的是这条（不是 WM_KEYDOWN）——录制 Alt 组合键必须接它。</summary>
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_CHAR = 0x0102;            // 插件市场搜索框的字符输入
        /// <summary>热键被按下：wParam = 注册时给的 id（高位字还带修饰键状态，别整个拿去用）。</summary>
        public const int WM_HOTKEY = 0x0312;

        // 全局热键的修饰键位（RegisterHotKey 的 fsModifiers）
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        /// <summary>按住不放时不重复触发（长按 Alt+← 不会连跳十几首）。</summary>
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
        // 左右分身的修饰键（左/右 Shift、Ctrl、Alt）——录制时要按「修饰键」识别，不能当主键收下
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;

        // ---- 输入法（IMM32）：搜索框要能打中文，必须接这几条 ----
        public const int WM_IME_STARTCOMPOSITION = 0x010D;
        public const int WM_IME_ENDCOMPOSITION = 0x010E;
        public const int WM_IME_COMPOSITION = 0x010F;
        public const int WM_IME_SETCONTEXT = 0x0281;
        // WM_IME_SETCONTEXT 的 lParam 位：告诉 IME 哪些自带 UI 要显示。
        //   ISC_SHOWUICOMPOSITIONWINDOW 抹掉后 IME 不再画组字窗（我们自己在搜索框里画），
        //   候选窗那一位保留（选词还得靠它）。
        public const int ISC_SHOWUICANDIDATEWINDOW = 0x0001;
        public const int ISC_SHOWUICOMPOSITIONWINDOW = unchecked((int)0x80000000);
        public const int GCS_COMPSTR = 0x0008;
        public const int GCS_RESULTSTR = 0x0800;
        // IMM 的候选窗位置：CFS_POINT（相对窗口客户区）/ CFS_EXCLUDE
        public const int CFS_POINT = 0x0002;
        public const int CFS_CANDIDATEPOS = 0x0040;
        public const int CFS_EXCLUDE = 0x0080;
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;
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
        //
        // 字段顺序必须与 Win32 的 WNDCLASS 完全一致 —— 这是纯内存布局的结构体，
        //    少一个字段后面全体错位（历史坑：以前缺 `style`，于是 cbWndExtra 实际落在 cbClsExtra 的位置上，
        //    类的样式也永远为 0，系统因此从不派发 WM_LBUTTONDBLCLK）。
        //    `style` 后来补上，岛体类借此声明 CS_DBLCLKS 以接收双击消息。
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

        // HBITMAP 的头部信息，GetObject 用它回读宽高与位深（应用图标提取用，见 Media/AppIconProvider.cs）
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

        // 应用图标提取专用（见 Media/AppIconProvider.cs）：从 HBITMAP 回读尺寸 / 取回 32bpp 像素
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

        // 一次性延迟回调：材质（accent）需要在窗口「显示 + 激活」之后再补一次，
        // 而 DWM 的合成初始化是异步的，所以用一个短定时器做兜底重贴。
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

        /// <summary>把键盘焦点交给指定窗口（插件市场搜索框点击时用，保证 WM_CHAR 能到达）。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        // ---- 窗口查询（媒体会话 → 应用窗口 的定位 / 前台激活） ----
        // 媒体侧的「双击封面跳转对应应用」要用它们：
        //   · GetForegroundWindow 在「会话刚被接管」那一刻顺手抓住应用的主窗口句柄；
        //   · GetWindowThreadProcessId / IsWindow / IsWindowVisible / GetWindowLongPtr 做归属与可用性校验
        //     （同时也是排除本程序自己窗口的手段 —— 岛体 / 设置窗 / 通知窗都同属本进程）；
        //   · ShowWindow / IsIconic / SetForegroundWindow / AttachThreadInput 负责把窗口还原并切到前台。
        //
        // 注：这里以前还有 EnumWindows + EnumWindowsProc（用来按进程号枚举窗口），曾经删除过一次；
        //     兜底路径不再无条件相信 shell:AppsFolder（它解析不出来时会打开资源管理器，
        //     表现就是「跳转跳到了文件资源管理器」），改成「先在已知进程里精确找窗口，找不到才考虑 Shell 激活」。
        //     这里的枚举是精确按进程号挑窗口，不是按进程名猜应用，和当初被放弃的模糊匹配不是一回事。

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>EnumWindows 的回调（返回 true 继续枚举；返回 false 立即停止）。</summary>
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>取窗口标题长度（字符数，不含结尾的 '\0'）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        /// <summary>GetWindowLongPtr 在 32 位系统上叫 GetWindowLong，所以按位数分派。</summary>
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        /// <summary>按位数取窗口扩展样式（x86 下包一层，调用方不必关心平台）。</summary>
        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

        /// <summary>GWL_EXSTYLE：用来排除 WS_EX_TOOLWINDOW（提示窗、托盘气泡之类的非主窗口）。</summary>
        public const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ---- 进程映像路径 ----
        //
        // 为什么需要这一组：`Process.MainModule` 在 .NET 上要为目标进程开 PROCESS_VM_READ 并
        // **枚举它的模块表**，遇到被保护 / 繁忙 / 正在退出的进程会长时间阻塞 ——
        // 实测「把系统里所有进程都过一遍」要 4 秒左右（有杀软时更久），
        // 而这个动作在「AUMID 匹配不上」时会跑两遍（见 AppIconProvider 的诊断日志），
        // 于是每换一首歌就冻住 8 秒（2026-10-08 用户实测的「切歌必卡」就是这个）。
        //
        // QueryFullProcessImageName 只读映像路径本身，句柄只要 PROCESS_QUERY_LIMITED_INFORMATION，
        // 对绝大多数进程（含受保护进程）都能拿到，且是微秒级、不阻塞。

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
                                                              StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 取某个进程的映像完整路径；取不到（进程已退出 / 权限不足）返回空串。
        /// 绝不阻塞：不用 Process.MainModule，理由见上面那段注释。
        /// </summary>
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

        /// <summary>
        /// 把自己的输入队列临时挂到另一个线程上 —— 前台锁（foreground lock）会拒绝跨线程的
        /// SetForegroundWindow，挂上之后再调用就能通过。用完必须立刻解挂（调用方用 finally 保证）。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        // ---- 输入法（imm32）----
        // 插件市场搜索框要支持系统输入法：窗口客户区里没有原生编辑框，WM_CHAR 只能拿到
        // 「非 IME 的」按键；中文靠下面这几条取「已上屏的结果串 / 正在组字的串」。
        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetContext(IntPtr hwnd);

        [DllImport("imm32.dll")]
        public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

        /// <summary>取组字串 / 结果串；返回字节数（UTF-16 时需 /2）。</summary>
        [DllImport("imm32.dll", CharSet = CharSet.Unicode, EntryPoint = "ImmGetCompositionStringW")]
        public static extern int ImmGetCompositionStringW(IntPtr himc, int dwIndex, byte[]? lpBuf, int dwBufLen);

        /// <summary>把候选窗/组字窗钉到搜索框附近，否则 IME 默认弹在窗口左上角。</summary>
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

        // ---- 多显示器工作区（自绘托盘菜单防止出屏） ----
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
        //
        // 为什么不用 System.Windows.Forms.OpenFileDialog：
        //   WinForms 的 OpenFileDialog 在 .NET Core+ 上走的是 Vista「通用项对话框」
        //   （CLSID_FileOpenDialog），它会在本进程内拉起 ExplorerBrowser + 外壳命名空间
        //   + 图标/缩略图缓存。这些是进程级 DLL 与缓存，第一次打开就常驻 20~30MB，
        //   并且 Dispose 对话框、关闭资源管理器都不会归还（Windows 不会卸载已加载的外壳组件）。
        //   传统对话框只是 comdlg32 的一个普通模态窗口，完全不碰 ExplorerBrowser。

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

        // ---- 全屏检测（「全屏自动隐藏」用） ----
        // SHQueryUserNotificationState 是系统自己的「现在该不该打扰用户」判定，一次调用就拿到答案，
        // 不用自己 GetForegroundWindow + GetWindowRect + 比对显示器矩形（那套还要处理多显示器与边界误差）。
        // 而且它和「Windows 要不要压掉 Toast」用的是同一套标准，不会出现两套判据打架。
        //
        // 覆盖的场景正好是我们要的全部：
        //   · 全屏视频（浏览器全屏 / 播放器全屏）→ QUNS_BUSY
        //   · 全屏游戏（无边框全屏）           → QUNS_BUSY
        //   · 全屏游戏（独占模式 D3D）         → QUNS_RUNNING_D3D_FULL_SCREEN（矩形比对会漏掉这类）
        //   · 演示文稿模式                     → QUNS_BUSY / QUNS_PRESENTATION_MODE
        // 返回的是 HRESULT（0 = S_OK），非 0 时 out 值不可信，调用方必须先看返回值。
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int pquns);

        public const int QUNS_NOT_PRESENT = 1;             // 屏保 / 锁屏 / 非活动的快速用户切换会话
        public const int QUNS_BUSY = 2;                    // 全屏应用运行中，或应用了演示文稿设置
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // 独占模式的全屏 Direct3D 应用
        public const int QUNS_PRESENTATION_MODE = 4;       // 演示文稿模式
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;   // 无上述状态，可以自由发通知
        public const int QUNS_QUIET_TIME = 6;              // 新用户首次登录 / 升级后的静默期
        public const int QUNS_APP = 7;                     // Windows 应用商店应用运行中（与全屏无关）

        // 插件窗口的拖放：拖入（WM_DROPFILES）与拖出（DoDragDrop）
        //
        // 拖入：DragAcceptFiles(hwnd, true) 会同时给窗口加上 WS_EX_ACCEPTFILES 扩展样式，
        //       之后用户从资源管理器把文件拖到窗口上松手，系统投递一次 WM_DROPFILES，
        //       wParam 就是 HDROP —— 用 DragQueryFile 逐条取路径，最后必须 DragFinish 归还。
        // 拖出：DoDragDrop 发起系统拖放，需要一个 IDataObject（装在 STGMEDIUM 里的 CF_HDROP）
        //       和一个 IDropSource（回答「继续 / 放下 / 取消」）—— 后者就是下面的 IDropSource 接口。

        public const int WM_DROPFILES = 0x0233;

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        /// <summary>
        /// 查 HDROP 里的路径。iFile 传 0xFFFFFFFF 时返回条目数量；
        /// 传 0..n-1 时： 为 null 则返回该路径的字符数（不含结尾 '\0'），
        /// 否则把路径拷进缓冲区并返回实际拷贝的字符数。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        /// <summary>释放 HDROP。处理完 WM_DROPFILES 后必须调用，否则这块由系统分配的内存不会归还。</summary>
        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        /// <summary>
        /// OLE 初始化。DoDragDrop 的硬性前提，未初始化时调用会直接失败。
        /// 返回值：0 (S_OK) = 本次初始化成功；1 (S_FALSE) = 之前已初始化过（引用计数 +1）；负数 = 失败。
        /// 0x80010106 (RPC_E_CHANGED_MODE) 表示本线程已按另一种套间模式初始化过 —— 此时不该再初始化，
        /// 但 OLE 本身是可用的，照常继续即可。
        /// </summary>
        [DllImport("ole32.dll")]
        public static extern int OleInitialize(IntPtr pvReserved);

        public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

        /// <summary>
        /// 确保当前线程完成过 OLE 初始化 —— RegisterDragDrop 与 DoDragDrop 的共同前提。
        ///
        /// 主程序走的是自定义 GetMessage 循环（不是 Application.Run），从来没有初始化过 OLE，
        /// 所以任何要用拖放的地方，第一次使用前都得先调一次它。
        ///
        /// 刻意不配对 OleUninitialize：这个引用会活到进程结束，而 OLE 初始化本身是引用计数式的，
        /// 多留一个引用不影响任何东西，却省掉了「谁负责收回」的记账。
        ///
        /// OLE 初始化是线程级的，所以状态用 ThreadStatic 存。
        /// </summary>
        public static bool EnsureOleInitialized()
        {
            if (_oleInitialized) return true;

            int hr = OleInitialize(IntPtr.Zero);
            if (hr == RPC_E_CHANGED_MODE)
            {
                // 本线程已经按另一种套间模式初始化过：这时不能再 OleInitialize（会失败），
                // 但进程内 OLE 已就绪，拖放照样能用。
                _oleInitialized = true;
                return true;
            }
            if (hr < 0) return false;

            _oleInitialized = true;
            return true;
        }

        [ThreadStatic] private static bool _oleInitialized;

        /// <summary>
        /// 发起一次系统拖放。会阻塞到用户松手或取消（内部自建消息循环并接管鼠标）。
        ///  返回目标最终接受的效果，0 表示没被接受（取消 / 拖到了不接收的地方）。
        /// </summary>
        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int DoDragDrop(
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
            [MarshalAs(UnmanagedType.Interface)] IDropSource pDropSource,
            uint dwOKEffects,
            out uint pdwEffect);

        // DROPEFFECT_*：与 System.Windows.Forms.DragDropEffects 的取值一一对应
        public const uint DROPEFFECT_COPY = 1;
        public const uint DROPEFFECT_MOVE = 2;
        public const uint DROPEFFECT_LINK = 4;

        // IDropSource.QueryContinueDrag / GiveFeedback 的应答码（HRESULT 形态，用 int 承载）
        public const int DRAGDROP_S_DROP = 0x00040100;              // 「可以放下了，结束拖放」
        public const int DRAGDROP_S_CANCEL = 0x00040101;            // 「取消这次拖放」
        public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102; // 「用系统默认的拖放光标」
        public const uint MK_LBUTTON = 0x0001;

        /// <summary>
        /// 拖放源接口（oleidl.h 的 IDropSource）。系统在拖放过程中反复回调它：
        /// QueryContinueDrag 问「继续 / 放下 / 取消」，GiveFeedback 问「用什么光标」。
        /// 实现类见 PluginWindow.FileDropSource。
        /// </summary>
        [ComImport, Guid("00000121-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropSource
        {
            /// <summary>返回 S_OK 继续拖、DRAGDROP_S_DROP 放下、DRAGDROP_S_CANCEL 取消。</summary>
            [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);

            /// <summary>返回 DRAGDROP_S_USEDEFAULTCURSORS 表示用系统默认光标。</summary>
            [PreserveSig] int GiveFeedback(uint dwEffect);
        }

        // OLE 拖入目标（IDropTarget）
        //
        // 为什么要有它：WM_DROPFILES 只在用户松手那一刻投递一次消息，拖动过程中窗口完全收不到通知，
        // 所以做不了「拖到窗口上时高亮」这类悬停反馈。IDropTarget 则在拖动的整个过程中持续回调 ——
        // DragEnter 一次、DragOver 每次鼠标移动、DragLeave 离开时、Drop 放下时，
        // 而且每次都带鼠标的实时坐标（屏幕物理像素）。
        //
        // 代价：RegisterDragDrop 要求窗口线程已完成 OleInitialize，且窗口销毁前必须 RevokeDragDrop。
        //
        // 注意：一个窗口同时挂了 IDropTarget 和 WS_EX_ACCEPTFILES 时，OLE 拖放会走 IDropTarget，
        //       WM_DROPFILES 不再投递 —— 所以两者不能并存当两条路径用，只能二选一（见 PluginWindow）。

        public const uint DROPEFFECT_NONE = 0;
        public const uint CF_HDROP = 15;

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget pDropTarget);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        /// <summary>归还 STGMEDIUM（GetData 取到的数据由它负责释放，漏掉就是内存泄漏）。</summary>
        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM param);

        /// <summary>
        /// 拖入目标接口（oleidl.h 的 IDropTarget）。
        /// 参数里的 POINT 就是原生 POINTL（两个 32 位 LONG，布局与 POINT 相同），
        /// 坐标是屏幕物理像素 —— 要自己 ScreenToClient 再除以 DPI 才是窗口内的逻辑坐标。
        /// 实现类见 PluginWindow 里的 WindowDropTarget。
        /// </summary>
        [ComImport, Guid("00000122-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDropTarget
        {
            /// <summary>拖入项第一次进入窗口。pdwEffect 里写上你愿意接受的效果（NONE = 不接受，光标会变禁止）。</summary>
            [PreserveSig] int DragEnter(
                [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
                uint grfKeyState, POINT pt, ref uint pdwEffect);

            /// <summary>鼠标在窗口内移动，高频调用（每次移动一次），实现里别做重活。</summary>
            [PreserveSig] int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect);

            /// <summary>鼠标离开了窗口，或这次拖放被取消。用来自行复位悬停态。</summary>
            [PreserveSig] int DragLeave();

            /// <summary>用户在窗口内松手。这里的 pDataObj 才是「真正要落下的数据」。</summary>
            [PreserveSig] int Drop(
                [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj,
                uint grfKeyState, POINT pt, ref uint pdwEffect);
        }
    }
}