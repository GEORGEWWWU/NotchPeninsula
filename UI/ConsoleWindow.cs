using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private static ConsoleWindow? _instance;

        private readonly IntPtr _hwnd;

        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;

        private static bool _classRegistered = false;

        private const int WIDTH = 600;

        private const int HEIGHT = 660;

        private const int TITLE_BAR_HEIGHT = 32;

        // 🧩 插件行排序小三角（渲染与鼠标命中必须使用同一组坐标）
        //    所有按钮均在下行（名称独占上行），按钮从左到右：← → [重载] [移除] [开关]

        private const float SORT_TRI_W = 16f;

        private const float PLUGIN_SORT_LEFT_X = 364f;   // ← 左移

        private const float PLUGIN_SORT_RIGHT_X = 382f;  // → 右移

        private const float PLUGIN_BTN_RELOAD_X = 404f;  // 重载按钮

        private const float PLUGIN_BTN_REMOVE_X = 460f;  // 移除按钮

        private const float PLUGIN_BTN_TOGGLE_X = 516f;  // 开关按钮

        // 🔤 通用设置页「切换灵动岛字体」卡片（渲染与鼠标命中必须使用同一组坐标）
        // 📐 通用设置页卡片顺序（2026-09-22 重构为「两张提示音子卡片」后）：
        //    开机自启 12 | 窗口置顶 84 | 系统消息通知卡 156..300 | 消息提示音卡 310..426
        //    | 剪贴板链接检测 436 | 切换灵动岛字体 510
        //
        //  ① 系统消息通知卡（高 144）：行1 开关（开关热区 +176..+196）、分隔线 +226、
        //     行2「消息通知内容」标题 +245 / 描述 +265 / 下拉 +272..+304。
        //     ⚠️ 卡片底部必须 ≥ 下拉底部 +8，否则下拉会越过卡片下沿、压到下一张卡的标题上。
        //  ② 消息提示音卡（高 116）：行1 提示音开关（开关热区 +330..+350）；
        //     行2「提示音」下拉 +370..+402 + 音量下拉 + [试听][重置]；行3 说明 +426。
        //     ⚠️ 提示音卡的两行共用同一个纵向区间（+358..+390）：
        //        「音量」下拉必须**接在「提示音」下拉右边**，绝不能顶到卡片左边界去和标签重叠 ——
        //        上一版就是因为下拉框没算宽度、x 停在 330 而和左侧文字叠在一起，被判定为「按钮全部错乱」。
        //     ⚠️ 所有控件右边界一律 `WIDTH - 36`（卡片内右侧留白），横向绝不铺满整卡。
        //    ⚠️ 改这里的数值时必须同步改 OnMouseMove 的 tab 0 段与 RenderDropdowns 的浮层锚点。

        /// <summary>卡片内右侧内边距：所有右对齐控件的右边界都锚到这里。</summary>
        private const float CARD_PAD_RIGHT = 36f;

        // ---- ① 系统消息通知卡 ----

        /// <summary>「消息通知内容」下拉顶部偏移（相对标题栏）。</summary>
        private const float TOAST_MODE_ROW_Y = 272f;

        private const float TOAST_MODE_ROW_H = 32f;

        /// <summary>通知内容下拉：贴着卡片右边界，宽 110。</summary>
        private const float TOAST_MODE_CTRL_W = 110f;

        private const float TOAST_MODE_CTRL_X = WIDTH - CARD_PAD_RIGHT - TOAST_MODE_CTRL_W;

        /// <summary>系统消息通知卡底部偏移（= 卡片顶 156 + 高度 144）。
        /// ⚠️ 必须 ≥ TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H + 8，否则通知内容下拉会越过卡片下沿。</summary>
        private const float TOAST_CARD_BOTTOM = 300f;

        // ---- ② 消息提示音卡 ----

        /// <summary>提示音卡顶部偏移（相对标题栏）。= 通知卡底 + 10 间隙。</summary>
        private const float SOUND_CARD_Y = 310f;

        /// <summary>提示音卡高度（两行内容 + 说明）。</summary>
        private const float SOUND_CARD_H = 116f;

        /// <summary>提示音卡第 1 行开关中心的纵向偏移。</summary>
        private const float SOUND_TOGGLE_ROW_Y = 334f;

        /// <summary>
        /// 提示音两行的纵向区间（提示音下拉 / 音量下拉 / 按钮共用，保证视觉齐平）。
        /// </summary>
        private const float SOUND_ROW_Y = 374f;

        private const float SOUND_ROW_H = 32f;

        /// <summary>
        /// 提示音行「从右往左」排版时用的横向间隙。**整行必须刚好塞进卡片内容区**
        /// （216 .. WIDTH-36，共 348px），所以每个宽度都是按实测文本宽度抠出来的：
        /// 最长选项「手表提示（watchOS）」132.9px + 左右内边距与箭头 ≈ 156。
        /// ⚠️ 改任一宽度都要重算总和，加起来超过 348 就会像上一版那样怼出卡片左边界。
        /// </summary>
        private const float SOUND_ROW_GAP = 12f;

        /// <summary>[重置] 与 [试听] 按钮（从右往左排，右边界锚卡片内边界）。</summary>
        private const float SOUND_BTN_H = 26f;

        private const float SOUND_BTN_Y = SOUND_ROW_Y + 3f;

        private const float SOUND_BTN_GAP = 6f;

        private const float SOUND_BTN_W = 46f;

        private const float SOUND_RESET_X = WIDTH - CARD_PAD_RIGHT - SOUND_BTN_W;

        private const float SOUND_PREVIEW_X = SOUND_RESET_X - SOUND_BTN_GAP - SOUND_BTN_W;

        /// <summary>「音量」下拉：接在按钮组左边。60px 足够放「100%」+箭头，再多就是浪费。</summary>
        private const float SOUND_VOL_W = 58f;

        private const float SOUND_VOL_X = SOUND_PREVIEW_X - SOUND_ROW_GAP - SOUND_VOL_W;

        /// <summary>「提示音」下拉：从卡片左内边距起排，右边界正好贴住音量下拉。</summary>
        private const float SOUND_CTRL_X = 216f;

        private const float SOUND_CTRL_W = SOUND_VOL_X - SOUND_ROW_GAP - SOUND_CTRL_X;

        /// <summary>第 3 行的小字说明（音频来源 / 时长体积上限）的纵向偏移。</summary>
        private const float SOUND_HINT_Y = SOUND_ROW_Y + SOUND_ROW_H + 24f;

        private const float FONT_CARD_Y = 510f;        // 卡片相对标题栏的纵向偏移

        private const float FONT_BTN_H = 26f;          // 按钮高度

        private const float FONT_BTN_Y = FONT_CARD_Y + 18f;

        private const float FONT_RESET_W = 56f;        // [重置] 按钮宽度

        private const float FONT_PICK_W = 78f;         // [选择字体] 按钮宽度

        private const float FONT_RESET_X = WIDTH - 36 - FONT_RESET_W;

        private const float FONT_PICK_X = FONT_RESET_X - 10 - FONT_PICK_W;

        // 🎚 媒体设置页「目标媒体平台 + 匹配方式」合并卡片（渲染与鼠标命中必须使用同一组坐标）
        // 📐 媒体设置页卡片顺序（2026-09-20 合并后）：
        //    媒体控制 12..74 | 合并卡片（两行）84..208 | 歌词设置 222..398
        //    合并卡片：第 1 行「目标媒体平台」行首 84、分隔线 142、第 2 行「匹配方式」行首 146（行距 62）
        // ⚠️ 第 1 行下拉框 +96..+128 的命中判定写在 WM_MOUSEMOVE 的 tab 2 段里（+98..+128），
        //    第 2 行选项框/下拉菜单命中直接读下面的 MATCH_ROW_Y / MATCH_MENU_Y —— 改这里即两侧同时生效。

        private const float PLATFORM_CARD_Y = TITLE_BAR_HEIGHT + 84f;    // 合并卡片顶部

        private const float PLATFORM_ROW2_Y = TITLE_BAR_HEIGHT + 146f;   // 第 2 行「匹配方式」行首

        private const float LYRIC_CARD_Y = TITLE_BAR_HEIGHT + 222f;      // 歌词设置卡片顶部

        private const float MATCH_BOX_W = 110f;        // 两个选项框宽度

        private const float MATCH_BOX_H = 32f;

        private const float MATCH_MODE_X = 340f;       // 左框：自动匹配 / 手动选择软件

        private const float MATCH_APP_X = 460f;        // 右框：手动模式下的目标软件

        private const float MATCH_ROW_Y = TITLE_BAR_HEIGHT + 158f;   // 选项框顶部（= 第 2 行行首 +12）

        private const float MATCH_MENU_Y = TITLE_BAR_HEIGHT + 192f;  // 下拉菜单顶部（= 选项框底 +2）

        private const float MATCH_MENU_RIGHT = 570f;   // 软件菜单右边界

        private const float APP_MENU_W = 280f;         // 软件菜单宽度

        private bool _minHovered = false;

        private bool _closeHovered = false;

        private static SKBitmap? _appIconBitmap;

        private static string _appTitleWithVersion = "NotchPeninsula";

        // 侧边栏与通用设置状态

        private int _selectedTab = 0;

        private int _hoveredTab = -1;

        private bool _isAutoStartEnabled;

        private bool _toggleHovered = false;

        private bool _toastToggleHovered = false;

        private bool _topmostToggleHovered = false;

        private bool _clipboardToggleHovered = false; // 📋「剪贴板链接检测」（2026-09-20 从交互设置搬到通用设置）
        // 灵动岛字体切换状态（字体本身由 FontConfig 统一持有）

        private bool _fontPickHovered = false;

        private bool _fontResetHovered = false;

        private string _fontHint = ""; // 加载失败时在卡片副标题上直接提示，避免弹窗打断操作
        // 交互设置状态

        private bool _autoHideToggleHovered = false;

        private bool _pauseHideToggleHovered = false; // 「暂停播放后自动隐藏」——自动隐藏卡片的第二行

        private bool _fsHideToggleHovered = false;    // 「全屏自动隐藏」——自动隐藏卡片的第三行

        private bool _mediaExpToggleHovered = false;

        private bool _passToggleHovered = false;

        // 媒体设置状态

        private bool _mediaToggleHovered = false;

        private bool _dropdownOpen = false;

        private bool _dropdownHovered = false;

        private int _hoveredDropdownIndex = -1;

        private int _selectedPlatformIndex = 0;
        // 通用媒体匹配方式（左：自动匹配/手动选择软件；右：手动模式下的目标软件，直接显示 AppID）

        private bool _matchModeDropdownOpen = false;

        private bool _matchModeDropdownHovered = false;

        private int _hoveredMatchModeIndex = -1;

        private bool _appDropdownOpen = false;

        private bool _appDropdownHovered = false;

        private int _hoveredAppIndex = -1;

        private string[] _appOptions = [];

        private static readonly string[] _matchModeOptions = ["自动匹配", "手动选择软件"];
        // 消息通知内容状态（缩略/完整）

        private bool _toastModeDropdownOpen = false;

        private bool _toastModeDropdownHovered = false;

        private int _hoveredToastModeIndex = -1;

        private int _selectedToastModeIndex = 0; // 0=缩略, 1=紧凑, 2=完整

        private static readonly string[] _toastModeOptions = ["缩略", "紧凑", "完整"];

        private float _savedToastW = -1f; // 切到完整模式前的用户消息宽度快照

        private float _savedToastH = -1f; // 切到完整模式前的用户消息高度快照

        // 🎵 消息提示音状态（值本身存在 ToastSoundConfig 静态类里，这里只放 UI 交互态）
        private bool _toastSoundDropdownOpen = false;

        private bool _toastSoundDropdownHovered = false;

        private int _hoveredToastSoundIndex = -1;

        /// <summary>
        /// 提示音下拉浮层的**滚动首行**。列表是动态扫目录来的（可能 40 项），
        /// 浮层高度被 RenderDropdownList 钳制在窗口内，超出的行靠这个偏移滚动查看。
        /// </summary>
        private int _dropdownScroll = 0;

        /// <summary>
        /// 提示音下拉浮层「本次绘制」的可视行数与首行（由命中检测每帧刷新）。
        /// 点击时要靠它把屏幕行号换算成真实索引，别直接用 (y-menuTop)/26。
        /// </summary>
        internal int _dropdownVisibleRows = 0;
        internal int _dropdownFirstRow = 0;

        /// <summary>「音量」下拉的展开 / 悬停 / 命中项（与提示音下拉互斥，见 CloseAllDropdowns）。</summary>
        private bool _soundVolumeDropdownOpen = false;

        private bool _soundVolumeDropdownHovered = false;

        private int _hoveredSoundVolumeIndex = -1;

        /// <summary>提示音开关（真正的布尔值存在 ToastSoundConfig.IsEnabled，这里只是镜像 + 悬停态）。</summary>
        private bool _soundToggleHovered = false;

        /// <summary>提示音文件失效时的红色提示（空串表示无错）。</summary>
        private string _soundHint = "";

        private bool _soundResetHovered = false;

        private bool _soundPreviewHovered = false;

        private bool _lyricToggleHovered = false;

        private bool _transToggleHovered = false;

        private bool _karaokeToggleHovered = false;

        private bool _lyricMinusHovered = false;

        private bool _lyricPlusHovered = false;

        private bool _lyricResetHovered = false;
        // 关于页交互状态

        private int _hoveredLinkIndex = -1;

        // 显示设置
        private int _selectedDisplayIndex = 0;

        private int _hoveredDisplayOptionIndex = -1;

        private static readonly string[] _displayOptions = ["时间日期", "空白"];

        private int _hoveredStyleIndex = -1;

        private bool _monitorDropdownOpen = false;

        private bool _monitorDropdownHovered = false;

        private int _hoveredMonitorDropdownIndex = -1;
        // 组合模式 UI 状态

        private bool _compositeToggleHovered = false;

        private bool _compDateTimeHovered = false;

        private bool _compHardwareHovered = false;

        private bool _compMediaHovered = false;

        private bool _isHoveringDisabledArea = false;

        private static string[] _monitorOptions = GetInitialMonitorOptions();

        private static string[] GetInitialMonitorOptions()
        {
            try
            {
                var screens = Screen.AllScreens;
                string[] opts = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                    opts[i] = screens[i].Primary ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}";
                return opts;
            }
            catch
            {
                return ["显示器 1 (主)"];
            }
        }
        // 个性化中心状态

        private int _hoveredMinusIndex = -1;

        private int _hoveredPlusIndex = -1;

        private int _hoveredResetIndex = -1;
        // 硬件检测模式切换前的待机宽度快照（用于切回时恢复）

        private float _savedStandbyWidth = -1f;

        private float[] _customValues = new float[8];
        // 「恢复默认」用的出厂值，顺序 = [待机宽, 待机高, 媒体宽, 媒体高, 通知宽, 通知高, DPI, 底部圆角]。
        // ⚠️ 这三个地方必须同步改，否则「恢复默认」和首次安装会给出不同的值：
        //    ① 本数组 ② Program.LoadSettings 里 key.GetValue 的兜底值 ③ Renderer 的字段初值

        private static readonly float[] _defaultCustomValues = [125f, 29f, 250f, 35f, 260f, 55f, 1.0f, 12f];

        private readonly string[] _valStrCache = new string[8];

        private int _hoveredThemeIndex = -1; // -1:无, 0:黑, 1:白, 2:系统

        private int _hoveredOpacityIndex = -1;
        // DPI 缩放相关

        private float _dpiScale = 1f;

        private int _scaledWidth;

        private int _scaledHeight;
        // 预设媒体平台数组

        private static readonly (string Id, string Name)[] _platforms = [
            ("other", "自动媒体"),
            ("browser", "浏览器媒体"),
            ("netease", "网易云音乐"),
            ("qqmusic", "QQ音乐"),
            ("kugou", "酷狗音乐"),
            ("spotify", "Spotify"),
            ("applemusic", "Apple Music"),
            ("echomusic", "Echo Music"),
            ("lxmusic", "LX Music")
        ];

        public static void Toggle()
        {
            if (_instance == null)
                _instance = new ConsoleWindow();
            else
            {
                _instance._isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
                _instance.Render();
                // 先把内容窗从任务栏还原回前台，再把材质窗重新亮出来并对齐到内容窗后面。
                // 材质窗不能走 SW_RESTORE —— 它是无标题 popup，被「还原」过一次就会把
                // 窗口标题当成标题栏文字画在左上角。
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                _instance.ShowBackdrop();
                Win32.SetForegroundWindow(_instance._hwnd);
                // 显示 / 激活会让 DWM 重新初始化这扇窗口的合成，把之前贴上的 accent 冲掉，
                // 所以「Show → Activate」之后必须再补一次材质（详见 ReapplyBackdropMaterial）。
                // 随后的 WM_ACTIVATE 还会补一次，并挂一个延迟兜底。
                _instance.ReapplyBackdropMaterial();
            }
        }

        private ConsoleWindow()
        {
            // 先挂到静态实例上：CreateWindowEx 期间系统可能立刻发 WM_PAINT/WM_CREATE，
            // 如果此时 StaticWndProc 还看不到实例，初次打开就只会看到“空的模糊底板”。
            _instance = this;
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
            _customValues[0] = Renderer.STANDBY_WIDTH;
            _customValues[1] = Renderer.BASE_HEIGHT;
            _customValues[2] = Renderer.MEDIA_WIDTH;
            _customValues[3] = Renderer.MEDIA_HEIGHT;
            _customValues[4] = Renderer.TOAST_WIDTH;
            _customValues[5] = Renderer.TOAST_HEIGHT;
            _customValues[6] = Renderer.GLOBAL_DPI;
            _customValues[7] = Renderer.NOTCH_BOTTOM_RADIUS;

            // 匹配目前加载的媒体平台索引
            for (int i = 0; i < _platforms.Length; i++)
            {
                if (_platforms[i].Id == MediaController.TargetPlatform)
                {
                    _selectedPlatformIndex = i; break;
                }
            }

            // 消息通知内容模式（0=缩略, 1=完整）
            _selectedToastModeIndex = Renderer.IsToastFullMode ? 2 : (Renderer.IsToastCompactMode ? 1 : 0);

            // 🎵 提示音：列表与选中值已由 Program.LoadSettings（RefreshBuiltins → Restore）恢复过，
            //    这里只需把「自定义路径失效」的原因取出来显示在卡片上。
            //    RefreshBuiltins 幂等且只扫顶层 wav，这里再调一次是为了让「先开设置窗口、
            //    再往 data\sound 丢文件」的场景也能在打开窗口时就看到新文件。
            ToastSoundConfig.RefreshBuiltins();
            if (ToastSoundConfig.SelectedIndex == ToastSoundConfig.CustomIndex)
                ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out _soundHint);

            if (!_classRegistered)
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    _appTitleWithVersion = $"NotchPeninsula {version.Major}.{version.Minor}.{version.Build}";
                }

                IntPtr appIconHandle = IntPtr.Zero;
                try
                {
                    // 提取系统级小图标 (专供窗口注册和任务栏底层使用)
                    var sysIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (sysIcon != null) appIconHandle = sysIcon.Handle;

                    string iconPath = Path.Combine(AppContext.BaseDirectory, "NPS_NotchPeninsula-logo.ico");

                    // 使用 SkiaSharp 直接解码 ICO，绕过 System.Drawing 的低质缩放
                    // SKBitmap.Decode 对 ICO 会自动选取容器中最大/最匹配的帧，且支持 256px PNG 压缩帧
                    if (File.Exists(iconPath))
                    {
                        _appIconBitmap = SKBitmap.Decode(iconPath);
                    }

                    // 兜底：如果外部文件丢失或解码失败，用系统图标转存
                    if (_appIconBitmap == null && sysIcon != null)
                    {
                        using var bmp = sysIcon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        ms.Position = 0;
                        _appIconBitmap = SKBitmap.Decode(ms);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("解析高清图标失败", ex);
                }

                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc,
                    hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero,
                    lpszClassName = "NotchConsoleClass",
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
                    hIcon = appIconHandle
                };
                Win32.RegisterClass(ref wc);
                _classRegistered = true;
            }

            _dpiScale = Win32.GetDpiForSystem() / 96f;
            _scaledWidth = (int)(WIDTH * _dpiScale);
            _scaledHeight = (int)(HEIGHT * _dpiScale);

            int screenWidth = Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            int left = (screenWidth - _scaledWidth) / 2;
            int top = (screenHeight - _scaledHeight) / 2;
            IntPtr hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;

            // 采用“双窗口”结构：
            // 1) 背景窗：普通 DWM HWND，只负责 Acrylic / Mica 材质；
            // 2) 内容窗：继续使用 layered + UpdateLayeredWindow，负责 Skia 前景 UI。
            // 这样既能拿到真实背景材质，又能保留前景的 per-pixel alpha，不会再把历史帧叠进客户区造成残影。
            // ⚠️ 背景窗标题必须留空：它用 DwmExtendFrameIntoClientArea 把整个客户区做成了玻璃，
            //    DWM 会把它当成「有标题栏的窗口」，最小化再还原时会把窗口标题直接画在客户区左上角
            //    （就是那个 "NotchPeninsulaBackdrop" 残影）。标题为空 → 无字可画。
            //    并且它永远不要走 SW_MINIMIZE，只走 SW_HIDE / SW_SHOWNOACTIVATE（见 HideBackdrop / ShowBackdrop）。
            _backdropHwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                "NotchConsoleClass", string.Empty,
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            ApplyRoundedRegion(_backdropHwnd);
            TryEnableBackdropMaterial();
            ApplyBackdropPalette();

            // ⚠️ 内容窗刻意用 WS_EX_APPWINDOW 而不是 WS_EX_TOOLWINDOW：
            //    工具窗（TOOLWINDOW）没有任务栏按钮，最小化时 Windows 只会把它画成
            //    「桌面左下角、浮在任务栏之上的小标题条」——既进不了任务栏，也没有入口点回来。
            //    换成 APPWINDOW 后最小化就是正常进任务栏，点任务栏按钮即可还原。
            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_APPWINDOW,
                "NotchConsoleClass", "NotchPeninsula",
                Win32.WS_POPUP | Win32.WS_VISIBLE | Win32.WS_MINIMIZEBOX,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            SyncBackdropToContent();

            for (int i = 0; i < 8; i++)
            {
                UpdateValueString(i);
            }

            System.Threading.Tasks.Task.Run(() => {
                var screens = Screen.AllScreens;
                string[] opts = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                    opts[i] = screens[i].Primary ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}";
                _monitorOptions = opts;
                if (Renderer.TargetMonitorIndex >= screens.Length) Renderer.TargetMonitorIndex = 0;

                // 异步加载完成后，主线程安全触发一次UI重绘
                if (_instance != null)
                {
                    _instance.Render();
                }
            });

            _selectedDisplayIndex = Renderer.StandbyDisplayMode; // 初始化时同步当前选择

            // 如果启动时就是硬件检测模式，标记快照为未记录（-1），
            // 这样切走时会回退到默认 130px
            if (Renderer.StandbyDisplayMode == 2)
                _savedStandbyWidth = -1f;

            Render();
        }

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instance != null)
            {
                bool initializingContent = _instance._hwnd == IntPtr.Zero;
                if (initializingContent || hwnd == _instance._hwnd || hwnd == _instance._backdropHwnd)
                    return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            bool isBackdropWindow = hwnd == _backdropHwnd && _backdropHwnd != IntPtr.Zero;
            if (isBackdropWindow)
            {
                switch (msg)
                {
                    case Win32.WM_PAINT:
                        IntPtr backdropDc = Win32.BeginPaint(hwnd, out var backdropPs);
                        if (backdropDc != IntPtr.Zero)
                            Win32.EndPaint(hwnd, ref backdropPs);
                        return IntPtr.Zero;
                    case Win32.WM_NCHITTEST:
                        return (IntPtr)Win32.HTTRANSPARENT;
                }
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            }

            switch (msg)
            {
                case Win32.WM_MOVE:
                    SyncBackdropToContent();
                    break;

                // 最小化 / 还原：材质窗跟着内容窗一起藏 / 亮。
                // 任务栏按钮的「点击最小化」走 WM_SYSCOMMAND(SC_MINIMIZE)，这里自己兜住，
                // 免得 DefWindowProc 在某些样式组合下把它吞掉。
                case Win32.WM_SYSCOMMAND:
                    if ((wParam.ToInt32() & 0xFFF0) == Win32.SC_MINIMIZE)
                    {
                        MinimizeToTaskbar();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_SIZE:
                    if (wParam.ToInt32() == Win32.SIZE_MINIMIZED)
                    {
                        HideBackdrop();
                    }
                    else if (_hwnd != IntPtr.Zero)
                    {
                        // 从任务栏还原回来：材质窗重新亮出来并对齐，补贴一次材质，再重绘一帧前景。
                        // （还原同样会让 DWM 重建合成、冲掉 accent，见 ReapplyBackdropMaterial）
                        ShowBackdrop();
                        ReapplyBackdropMaterial();
                        Render();
                    }
                    break;

                // 重新激活（点任务栏、Alt+Tab、从别的程序切回来、SetForegroundWindow 拉前台）：
                // 材质窗重新亮出来 + 重新贴一次材质，否则背景会变成全透明（亚克力丢失）。
                case Win32.WM_ACTIVATE:
                    if ((wParam.ToInt32() & 0xFFFF) != Win32.WA_INACTIVE && _hwnd != IntPtr.Zero)
                    {
                        ShowBackdrop();
                        ReapplyBackdropMaterial();
                        // DWM 的合成初始化是异步的，紧贴 WM_ACTIVATE 补的这一次仍可能被随后的
                        // 初始化覆盖，所以再挂一个短定时器，等激活流程彻底走完再补一次兜底。
                        Win32.SetTimer(hwnd, BACKDROP_REFRESH_TIMER_ID, 150, IntPtr.Zero);
                    }
                    break;

                case Win32.WM_TIMER:
                    if (wParam == BACKDROP_REFRESH_TIMER_ID)
                    {
                        Win32.KillTimer(hwnd, BACKDROP_REFRESH_TIMER_ID);
                        ReapplyBackdropMaterial();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_MOUSEMOVE:
                    OnMouseMove(
                        (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale),
                        (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale),
                        (wParam.ToInt32() & 0x0001) != 0);
                    break;

                case Win32.WM_LBUTTONDOWN:
                    OnLeftButtonDown(hwnd, (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale));
                    break;

                // 🖱 滚轮：只服务于提示音下拉浮层（列表是动态扫目录来的，条目数不封顶）。
                //    每格 120 → 滚动 3 行；滚动后立即重绘，并与命中检测共用同一套钳制算法。
                case Win32.WM_MOUSEWHEEL:
                    if (_toastSoundDropdownOpen)
                    {
                        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        int totalOpts = ToastSoundConfig.OptionCount;
                        int menuTop = (int)(TITLE_BAR_HEIGHT + SOUND_ROW_Y + SOUND_ROW_H + 2);
                        int maxRows = Math.Max(1, (HEIGHT - 12 - menuTop) / 26);
                        int visible = Math.Min(totalOpts, maxRows);
                        int maxFirst = Math.Max(0, totalOpts - visible);
                        if (maxFirst > 0)
                        {
                            _dropdownScroll = Math.Clamp(_dropdownScroll - delta / 120 * 3, 0, maxFirst);
                            Render();
                        }
                        return IntPtr.Zero; // 吞掉，别让滚轮穿透到下层
                    }
                    break;

                case Win32.WM_PAINT:
                    return IntPtr.Zero;

                case Win32.WM_DESTROY:
                    if (_backdropHwnd != IntPtr.Zero)
                    {
                        IntPtr backdrop = _backdropHwnd;
                        _backdropHwnd = IntPtr.Zero;
                        Win32.DestroyWindow(backdrop);
                    }
                    _instance = null;
                    break;

                case Win32.WM_SETCURSOR:
                    if (_isHoveringDisabledArea && (lParam.ToInt32() & 0xFFFF) == 1) // 1 代表 HTCLIENT (客户区)
                    {
                        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, (int)32648)); // 强制注入系统 NO (禁止) 指针
                        return (IntPtr)1;
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private unsafe void Render()
        {
            var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Scale(_dpiScale);
            canvas.Clear(SKColors.Transparent);
            float cornerRadius = 8f;
            var windowRect = new SKRect(0, 0, WIDTH, HEIGHT);

            canvas.DrawRoundRect(windowRect, cornerRadius, cornerRadius, _bgPaint);

            canvas.Save();
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(windowRect, cornerRadius, cornerRadius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            // 标题栏区：纯暗色模式仍保留传统顶栏；材质模式下不再额外盖一整块底色，让亚克力/云母连续透过。
            if (_backdropMode == BackdropMaterialMode.SolidDark)
                canvas.DrawRect(0, 0, WIDTH, TITLE_BAR_HEIGHT, _titleBarPaint);

            float textX = 14f;
            if (_appIconBitmap != null)
            {
                var iconRect = new SKRect(14, 8, 14 + 16, 8 + 16);
                canvas.DrawBitmap(_appIconBitmap, iconRect, _hqSamplingOpts);
                textX += 24f;
            }

            canvas.DrawText(_appTitleWithVersion, textX, 21.2f, _titleTextPaint);

            if (_minHovered) canvas.DrawRect(WIDTH - 92, 0, 46, TITLE_BAR_HEIGHT, _hoverMinPaint);
            if (_closeHovered) canvas.DrawRect(WIDTH - 46, 0, 46, TITLE_BAR_HEIGHT, _hoverClosePaint);

            canvas.DrawLine(WIDTH - 92 + 18, 16, WIDTH - 92 + 28, 16, _iconPaint);
            float cx = WIDTH - 46 + 23; float cy = 16;
            canvas.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, _iconPaint);
            canvas.DrawLine(cx + 5, cy - 5, cx - 5, cy + 5, _iconPaint);

            RenderSidebar(canvas);

            // 右侧卡片内容区（每个页签一个 RenderTabXxx，见 ConsoleWindow.Render.cs）
            if (_selectedTab == 0) RenderTabGeneral(canvas);
            else if (_selectedTab == 1) RenderTabDisplay(canvas);
            else if (_selectedTab == 2) RenderTabMedia(canvas);
            else if (_selectedTab == 3) RenderTabInteraction(canvas);
            else if (_selectedTab == 4) RenderTabAbout(canvas);
            else if (_selectedTab == 5) RenderTabPersonalize(canvas);
            else if (_selectedTab == 6) RenderTabPlugins(canvas);

            canvas.Restore();

            RenderDropdowns(canvas);

            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f), cornerRadius, cornerRadius, _globalBorderPaint);
            UpdateLayeredContentWindow(surface.PeekPixels());
        }

        private unsafe void UpdateLayeredContentWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return;

            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            try
            {
                var bmi = new Win32.BITMAPINFO
                {
                    bmiHeader = new Win32.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                        biWidth = _scaledWidth,
                        biHeight = -_scaledHeight,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
                    return;

                IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);
                try
                {
                    long bytes = (long)_scaledWidth * _scaledHeight * 4;
                    Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), bytes, bytes);

                    var ptSrc = new Win32.POINT(0, 0);
                    var ptDst = new Win32.POINT(0, 0);
                    Win32.GetWindowRect(_hwnd, out var rect);
                    ptDst.x = rect.Left;
                    ptDst.y = rect.Top;

                    var size = new Win32.SIZE(_scaledWidth, _scaledHeight);
                    var blend = new Win32.BLENDFUNCTION
                    {
                        BlendOp = Win32.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = Win32.AC_SRC_ALPHA
                    };

                    Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
                }
                finally
                {
                    Win32.SelectObject(memDc, hOldBitmap);
                    Win32.DeleteObject(hBitmap);
                }
            }
            finally
            {
                Win32.DeleteDC(memDc);
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static void UpdateAutoStartState(bool enable)
        {
            if (_instance != null && _instance._isAutoStartEnabled != enable)
            {
                _instance._isAutoStartEnabled = enable;
                _instance.Render();
            }
        }
    }
}
