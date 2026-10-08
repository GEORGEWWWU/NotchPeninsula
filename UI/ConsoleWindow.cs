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

        // ---- 内容区横向边界（窗口右侧那一列）----
        // 想各挪十几像素得改上百处）。
        private const float CONTENT_L = 186f;          // 卡片左边界（与侧栏之间留一点缝）
        private const float CONTENT_TEXT_X = 202f;     // 卡片内文字左缩进（= CONTENT_L + 16）
        private const float CONTENT_RM = 12f;          // 卡片右边界距窗口右边
        private const float CONTENT_TEXT_RM = 28f;     // 卡片内文字右缩进（距窗口右边）

        //   DeleteObject → DeleteDC
        // 也要先于 hBitmap 释放。
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _hBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _pBits = IntPtr.Zero;
        private SKSurface? _renderSurface;

        private readonly object _renderLock = new object();

        // 插件中心行内按钮（渲染与鼠标命中必须使用同一组坐标）

        private const float PLUGIN_BTN_RELOAD_X = 404f;  // 重载按钮

        private const float PLUGIN_BTN_REMOVE_X = 460f;  // 卸载按钮

        private const float PLUGIN_BTN_TOGGLE_X = 516f;  // 开关按钮

        private const float DISPLAY_ROW_H = 34f;

        private const float DISPLAY_ITEM_H = 30f;

        private const float DISPLAY_FIRST_ROW_Y = 13f;    // 列表卡片顶部到首件 item 的距离（卡片无标题行）

        private const int DISPLAY_MAX_ROWS = 8;

        private const float DISPLAY_CARD_Y = TOGGLE_ROW_Y + 64f;

        private const float DISPLAY_CARD_H = DISPLAY_FIRST_ROW_Y + DISPLAY_MAX_ROWS * DISPLAY_ROW_H;

        private const float DISPLAY_ITEM_L = CONTENT_L;

        private const int DISPLAY_WHEEL_STEP_ROWS = 3;

        private const float STYLE_CARD_Y = 12f;

        private const float STYLE_CARD_H = 118f;   // = 选项高 90 + 上下内边距各 14

        private const float STYLE_OPT_Y = STYLE_CARD_Y + 14f;

        private const float STYLE_OPT_W = 150f;

        private const float STYLE_OPT_H = 90f;

        private const float STYLE_OPT_GAP = 20f;

        private const float STYLE_OPT_X = 220f;

        private const float ROW_H = 38f;

        private const float ROW_LABEL_DY = 25f;

        private const float SEG_H = 30f;

        private const float MONITOR_ROW_Y = STYLE_CARD_Y + STYLE_CARD_H + 8f;   // 138

        private const float MONITOR_DD_W = 160f;

        private const float MONITOR_DD_H = 32f;

        private const float MODE_ROW_Y = MONITOR_ROW_Y + 44f;                   // 182

        private const float MODE_SEG_W = 184f;                                  // 2 段 × 92

        private const float MODE_SEG_X = WIDTH - CONTENT_RM - MODE_SEG_W;       // 404

        private const float SCENE_ROW_Y = MODE_ROW_Y + 44f;                     // 226

        private const float SCENE_SEG_W = 240f;                                 // 3 段 = 70 / 70 / 100

        private const float SCENE_SEG_X = WIDTH - CONTENT_RM - SCENE_SEG_W;     // 348

        private const float TOGGLE_ROW_Y = SCENE_ROW_Y + 33f;                   // 259

        private const float DISPLAY_PAGE_WHEEL_STEP = 48f;

        private const int DISPLAY_WHEEL_CARRY_STEPS = 2;

        private int _displayWheelCarry;

        private bool ScrollDisplayPage(float pageMax, float px)
        {
            if (pageMax <= 0f) return false;
            float target = Math.Clamp(_displayPageScroll - px, 0f, pageMax);
            if (Math.Abs(target - _displayPageScroll) <= 0.5f) return false;
            _displayPageScroll = target;
            return true;
        }

        private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        private const float DISPLAY_ITEM_R = WIDTH - CONTENT_RM;

        private const float DISPLAY_MOVE_PAD_R = 8f;

        private const float DISPLAY_MOVE_DOWN_X = DISPLAY_ITEM_R - DISPLAY_MOVE_PAD_R - SORT_TRI_W;          // 562（槽 562..580）

        private const float DISPLAY_MOVE_UP_X = DISPLAY_MOVE_DOWN_X - SORT_TRI_W - 8f;                      // 536（槽 536..554）

        private const float SORT_TRI_W = 18f;             // 上下箭头槽的点击宽度（箭头本身只占槽中心 11×8）

        // 15 而不是 16：系统默认 tick 是 15.625ms，请求 16ms 会被向上取整成 2 个 tick（≈31ms → 32FPS）。
        // 15 落在 1 个 tick 内，即使 timeBeginPeriod 没生效也只有 ~15.6ms；生效时就是 ~15ms。
        private const uint DISPLAY_HOVER_TICK_MS = 15;

        private const float DISPLAY_HOVER_EASE = 0.35f;   // 每拍向目标靠拢的比例（指数缓出）

        private static readonly IntPtr DISPLAY_HOVER_TIMER_ID = new IntPtr(0x4E51); // "NQ"

        private static readonly IntPtr SCROLLBAR_TIMER_ID = new IntPtr(0x4E52); // "NR"

        private const int SCROLLBAR_HOLD_MS = 800;    // 停手后保持满不透明度的时长
        private const int SCROLLBAR_FADE_MS = 260;    // 随后淡出的时长
        private const int SCROLLBAR_TICK_MS = 60;     // 淡出一拍的间隔

        // ---- 侧边栏页签：几何 + 滑动动画 ----

        /// <summary>页签行首（相对标题栏），**按页签号索引**——顺序与侧边栏视觉顺序不同，
        ///    绘制顺序见 RenderSidebar。绘制 / 命中 / 滑动动画三处都从这里取，别再写死数字。</summary>
        private static readonly float[] SidebarTabY = { 60f, 100f, 140f, 180f, 320f, 10f, 230f, 270f };

        private const float TAB_ROW_H = 36f;          // 页签行高

        private const float TAB_BAR_DY = 8f;          // 蓝色竖条相对行首的上下内缩（竖条高 = 36 - 2×8）

        private const float TAB_SLIDE_MS = 200f;      // 选中块滑动的时长（太长就不跟手）

        private const float TAB_HOVER_EASE = 0.45f;   // 悬停底的靠拢比例：比列表行(0.35)快一档，鼠标划过更跟手

        private const float TAB_SLIDE_STRETCH = 10f;  // 蓝条滑动途中两端各外扩的像素（中点最强、两端归零）→ 拉丝感

        private static float TabRowY(int index)
            => index >= 0 && index < SidebarTabY.Length ? SidebarTabY[index] : 0f;

        private long _scrollBarShownAt;

        private bool _scrollBarTimerOn;

        private void NotifyScrolled()
        {
            _scrollBarShownAt = Environment.TickCount64;
            if (_hwnd == IntPtr.Zero || _scrollBarTimerOn) return;
            if (Win32.SetTimer(_hwnd, SCROLLBAR_TIMER_ID, SCROLLBAR_TICK_MS, IntPtr.Zero) != IntPtr.Zero)
                _scrollBarTimerOn = true;
        }

        private float ScrollBarAlpha()
        {
            if (_scrollBarShownAt == 0) return 0f;
            long since = Environment.TickCount64 - _scrollBarShownAt;
            if (since <= SCROLLBAR_HOLD_MS) return 1f;
            return Math.Max(0f, 1f - (since - SCROLLBAR_HOLD_MS) / (float)SCROLLBAR_FADE_MS);
        }

        private bool TickScrollBarFade()
        {
            bool showing = ScrollBarAlpha() > 0f;
            if (showing) Render();
            return showing;
        }

        // 通用设置页卡片顺序（提示音并入通知卡之后）：

        private const float CARD_PAD_RIGHT = CONTENT_TEXT_RM;   // 卡片内右对齐控件的基准（与文字右边界同源）

        //   · 行 1「系统消息通知」行首 156
        //   · 分隔线放在每一对行之间：+222、+284

        private const float TOAST_ROW1_Y = 156f;

        private const float TOAST_ROW2_Y = TOAST_ROW1_Y + 62f;

        private const float TOAST_SEP_Y = TOAST_ROW2_Y + 4f;

        // 行内纵向锚点（全页唯一真源，第五次返工后定稿）
        //  ── 返工史（前四轮都错在「拿什么当对齐参照」）
        // 错：框内文字在框里本身偏下，把行外标签也拖下去了。
        // 错：26 是两行行（标题+副标题）的标题基线，

        private const float ROW_ANCHOR_Y = 30f;

        private const float TEXT_INK_MID_OFFSET = 5.5f;

        private const float TEXT_INK_ASCENT = 11f;

        private const float ROW_SUB_OFFSET = 20f;

        private const float ROW_TEXT_BASELINE_SINGLE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET;   // = 35.5

        private const float ROW_TEXT_BASELINE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET - ROW_SUB_OFFSET / 2f;   // = 25.5

        private const float ROW_DROPDOWN_TOP = 14f;

        private const float DROPDOWN_TEXT_BASELINE = 21f;

        private const float TOAST_ROW2_TITLE_Y = TOAST_ROW2_Y + ROW_TEXT_BASELINE;

        private const float TOAST_ROW2_DESC_Y = TOAST_ROW2_TITLE_Y + ROW_SUB_OFFSET;

        private const float TOAST_MODE_ROW_Y = TOAST_ROW2_Y + ROW_DROPDOWN_TOP;

        private const float TOAST_MODE_ROW_H = 32f;

        private const float TOAST_MODE_CTRL_W = 110f;

        private const float TOAST_MODE_CTRL_X = WIDTH - CARD_PAD_RIGHT - TOAST_MODE_CTRL_W;

        private const float SOUND_ROW3_Y = TOAST_ROW2_Y + 62f;

        private const float SOUND_SEP_Y = SOUND_ROW3_Y + 4f;

        private const float TOGGLE_TRACK_H = 20f;

        private const float SOUND_TOGGLE_ROW_Y = SOUND_ROW3_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f;

        private const float TOAST_TOGGLE_ROW_Y = TOAST_ROW1_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f;

        private const float SOUND_ROW_Y = SOUND_ROW3_Y + 62f;

        private const float SOUND_ROW_TITLE_Y = SOUND_ROW_Y + ROW_TEXT_BASELINE_SINGLE;

        private const float SOUND_ROW_H = 32f;

        private const float SOUND_BOX_Y = SOUND_ROW_Y + ROW_DROPDOWN_TOP;

        private const float TOAST_CARD_BOTTOM = SOUND_BOX_Y + SOUND_ROW_H + 16f;

        private const float SOUND_ROW_GAP = 12f;

        private const float SOUND_BTN_H = 26f;

        private const float SOUND_BTN_Y = SOUND_BOX_Y + (SOUND_ROW_H - SOUND_BTN_H) / 2f;

        private const float SOUND_BTN_GAP = 6f;

        private const float SOUND_BTN_W = 46f;

        private const float SOUND_RESET_X = WIDTH - CARD_PAD_RIGHT - SOUND_BTN_W;

        private const float SOUND_PREVIEW_X = SOUND_RESET_X - SOUND_BTN_GAP - SOUND_BTN_W;

        private const float SOUND_VOL_W = 58f;

        private const float SOUND_VOL_X = SOUND_PREVIEW_X - SOUND_ROW_GAP - SOUND_VOL_W;

        private const float SOUND_LABEL_X = CONTENT_TEXT_X;

        private const float SOUND_LABEL_GAP = 8f;

        private const float SOUND_CTRL_X = SOUND_LABEL_X + 40f + SOUND_LABEL_GAP;

        private const float SOUND_CTRL_W = SOUND_VOL_X - SOUND_ROW_GAP - SOUND_CTRL_X;

        // MSP 接入（实验性）：通知卡之后，与本页同一节奏（卡高 62 + 间隙 10）。
        // 本页内容底边 = FONT_CARD_Y + 62，必须 ≤ HEIGHT - TITLE_BAR_HEIGHT（现 548 ≤ 628）。
        private const float MSP_CARD_Y = TOAST_CARD_BOTTOM + 10f;

        // 标题后面的「实验性」小标签（目前只有 MSP 卡用）：位置跟着标题文字走，见 DrawInlineBadge
        private const float BADGE_H = 15f;

        private const float BADGE_PAD_X = 7f;

        private const float BADGE_RADIUS = 3f;

        private const float BADGE_GAP_X = 8f;

        private const float FONT_CARD_Y = MSP_CARD_Y + 62f + 10f;

        private const float FONT_BTN_H = 26f;          // 按钮高度

        private const float FONT_BTN_Y = FONT_CARD_Y + ROW_ANCHOR_Y - FONT_BTN_H / 2f;

        private const float FONT_RESET_W = 56f;        // [重置] 按钮宽度

        private const float FONT_PICK_W = 78f;         // [选择字体] 按钮宽度

        private const float FONT_RESET_X = WIDTH - CONTENT_TEXT_RM - FONT_RESET_W;

        private const float FONT_PICK_X = FONT_RESET_X - 10 - FONT_PICK_W;

        // 媒体设置页卡片顺序（合并后）：

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

        private const float HOTKEY_CARD_Y = LYRIC_CARD_Y + 188f;   // 歌词卡底（+176）再留 12px 间距

        private const float HOTKEY_CARD_H = 210f;

        private const float HOTKEY_HEAD_H = 44f;

        private const float HOTKEY_ROW_H = 32f;

        private const float HOTKEY_BOX_W = 148f;

        private const float HOTKEY_BOX_H = 24f;

        private const float HOTKEY_BOX_RIGHT = WIDTH - CONTENT_TEXT_RM;

        private const float HOTKEY_BOX_X = HOTKEY_BOX_RIGHT - HOTKEY_BOX_W;

        private bool _minHovered = false;

        private bool _closeHovered = false;

        private static SKBitmap? _appIconBitmap;

        private static Icon? _appSysIcon;

        private static string _appTitleWithVersion = "NotchPeninsula";

        // 侧边栏与通用设置状态

        private int _selectedTab = 0;

        private int _hoveredTab = -1;

        private bool _isAutoStartEnabled;

        private bool _toggleHovered = false;

        private bool _toastToggleHovered = false;

        private bool _topmostToggleHovered = false;

        private bool _mspToggleHovered = false; // 「MSP 接入（实验性）」

        private bool _fontPickHovered = false;

        private bool _fontResetHovered = false;

        private string _fontHint = ""; // 加载失败时在卡片副标题上直接提示，避免弹窗打断操作
        // 交互设置状态

        private bool _autoHideToggleHovered = false;

        private bool _focusHideToggleHovered = false; // 「当焦点离开时自动隐藏岛」——自动隐藏卡片的第二行

        private bool _pauseHideToggleHovered = false; // 「暂停播放后自动隐藏」——自动隐藏卡片的第三行

        private bool _fsHideToggleHovered = false;    // 「全屏自动隐藏」——自动隐藏卡片的第四行

        private bool _mediaExpToggleHovered = false;

        private bool _appLaunchToggleHovered = false;

        private bool _passToggleHovered = false;

        // 媒体设置状态

        private bool _mediaToggleHovered = false;

        private bool _dropdownOpen = false;

        private bool _dropdownHovered = false;

        private int _hoveredDropdownIndex = -1;

        private int _selectedPlatformIndex = 0;

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

        private bool _toastSoundDropdownOpen = false;

        private bool _toastSoundDropdownHovered = false;

        private int _hoveredToastSoundIndex = -1;

        private int _dropdownScroll = 0;

        private const float DROPDOWN_ROW_H = 26f;

        private void GetToastSoundMenuLayout(out float menuTop, out int visibleRows, out int maxFirstRow)
        {
            int total = ToastSoundConfig.OptionCount;
            menuTop = TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H + 2f;
            int maxRows = Math.Max(1, (int)((HEIGHT - 12 - menuTop) / DROPDOWN_ROW_H));
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        private void GetVolumeMenuLayout(out float menuTop, out float menuBottom, out int visibleRows)
        {
            menuBottom = TITLE_BAR_HEIGHT + SOUND_BOX_Y - 2f;   // 对应 RenderDropdownList 的 anchorY - 2
            int maxRows = Math.Max(1, (int)((menuBottom - TITLE_BAR_HEIGHT - 12f) / DROPDOWN_ROW_H));
            visibleRows = Math.Min(ToastSoundConfig.VolumeOptions.Length, maxRows);
            menuTop = menuBottom - visibleRows * DROPDOWN_ROW_H;
        }

        private void ScrollToastSoundMenuToSelected()
        {
            GetToastSoundMenuLayout(out _, out int visible, out int maxFirst);
            _dropdownScroll = maxFirst <= 0
                ? 0
                : Math.Clamp(ToastSoundConfig.SelectedIndex - visible / 2, 0, maxFirst);
        }

        private void SyncHoverFromCursor()
        {
            if (!Win32.GetCursorPos(out var pt) || !Win32.GetWindowRect(_hwnd, out var rect))
                return;
            OnMouseMove((int)((pt.x - rect.Left) / _dpiScale), (int)((pt.y - rect.Top) / _dpiScale), false);
        }

        private bool _soundVolumeDropdownOpen = false;

        private bool _soundVolumeDropdownHovered = false;

        private int _hoveredSoundVolumeIndex = -1;

        private bool _soundToggleHovered = false;

        private string _soundHint = "";

        private bool _soundResetHovered = false;

        private bool _soundPreviewHovered = false;

        private bool _lyricToggleHovered = false;

        private bool _transToggleHovered = false;

        private bool _scanToggleHovered = false;

        private bool _lyricMinusHovered = false;

        private bool _lyricPlusHovered = false;

        private bool _lyricResetHovered = false;

        // ---- 媒体设置页「全局快捷键」卡片 ----

        private bool _hotkeyToggleHovered = false;

        private int _hoveredHotkeyRow = -1;

        private int _hotkeyRecordingIndex = -1;

        private string _hotkeyHint = "";
        // 关于页交互状态

        private int _hoveredLinkIndex = -1;

        // 显示设置
        // 「显示内容」列表的悬停行：-1 = 没悬停任何行。
        private int _hoveredDisplayRow = -1;

        private int _hoveredDisplayMoveUp = -1;

        private int _hoveredDisplayMoveDown = -1;

        private int _displayScroll = 0;

        private int _displayHoverRow = -1;

        private readonly float[] _displayHoverAnim = new float[24];

        private bool _displayHoverTimerOn = false;

        private readonly float[] _hintAnim = new float[8];

        private int _hintRow = -1;

        private float GetHintAlpha(int row)
            => row >= 0 && row < _hintAnim.Length ? _hintAnim[row] : 0f;

        // 侧边栏：每项的悬停淡入，以及选中块（背景方块 + 蓝色竖条）的滑动位置。
        // 都用固定长度的小数组，每帧零分配。

        private readonly float[] _tabHoverAnim = new float[8];

        private float _tabSlideFromY, _tabSlideToY;   // 行首坐标（相对标题栏）的起点 / 终点

        private float _tabSlideT = 1f;                // 0 → 1

        private long _tabSlideStarted;

        private int _tabSlideDst = -1;                // 已经为哪个页签起过滑

        private byte _tabHoverBaseAlpha = 8;

        private float GetTabHoverProgress(int index)
            => index >= 0 && index < _tabHoverAnim.Length ? _tabHoverAnim[index] : 0f;

        private void GetDisplayListLayout(out int visibleRows, out int maxFirstRow)
        {
            int maxRows = Math.Max(1, DISPLAY_MAX_ROWS);
            int total = PluginManager.Instance.DisplayItems.Count;
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        private static float GetDisplayPageMaxScroll()
            => Math.Max(0f, TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 20f - HEIGHT);

        private static void GetPageScrollbarLayout(out float trackTop, out float trackH)
        {
            trackTop = TITLE_BAR_HEIGHT + 6f;
            trackH = HEIGHT - TITLE_BAR_HEIGHT - 12f;
        }

        private void GetListScrollbarLayout(out float trackTop, out float trackH)
        {
            GetDisplayListLayout(out int visibleRows, out _);
            float contentCardY = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y - _displayPageScroll;
            trackTop = contentCardY + DISPLAY_FIRST_ROW_Y - 4f;
            trackH = Math.Max(0f, visibleRows * DISPLAY_ROW_H - 4f);
        }

        private bool TryGetCursorClientPos(out int x, out int y)
        {
            x = 0; y = 0;
            if (!Win32.GetCursorPos(out var pt) || !Win32.GetWindowRect(_hwnd, out var rect))
                return false;
            x = (int)((pt.x - rect.Left) / _dpiScale);
            y = (int)((pt.y - rect.Top) / _dpiScale);
            return true;
        }

        private int _hoveredStyleIndex = -1;

        private float _displayPageScroll;

        private bool _wheelPriorityList;

        private bool _pageScrollbarHovered;

        private bool _listScrollbarHovered;

        private int _hoveredDisplayModeIndex = -1;

        private int _hoveredStandbySceneIndex = -1;

        private bool _standbyToggleHovered;

        private bool _monitorDropdownOpen = false;

        private bool _monitorDropdownHovered = false;

        private int _hoveredMonitorDropdownIndex = -1;

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

        private float[] _customValues = new float[8];

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
                // 窗口标题当成标题栏文字画在左上角。
                Win32.ShowWindow(_instance._hwnd, Win32.SW_RESTORE);
                _instance.ShowBackdrop();
                Win32.SetForegroundWindow(_instance._hwnd);
                _instance.ReapplyBackdropMaterial();
            }
        }

        public static void ShowTab(int tab)
        {
            if (_instance == null)
            {
                _instance = new ConsoleWindow();
                _instance.SelectTab(tab);
                return;
            }
            _instance.SelectTab(tab);
            Toggle();
        }

        private void SelectTab(int tab)
        {
            if (tab < 0 || tab > 6 || _selectedTab == tab) return;
            _selectedTab = tab;
            CloseAllDropdowns();
            Render();
        }

        private ConsoleWindow()
        {
            _instance = this;
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
            // 生命周期跟窗口走：WM_DESTROY 里退订。
            Renderer.StandbyActiveChanged += OnStandbyActiveChanged;
            _customValues[0] = Renderer.STANDBY_WIDTH;
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
                    _appSysIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (_appSysIcon != null) appIconHandle = _appSysIcon.Handle;

                    using (var iconStream = DataResources.OpenRead("NPS_NotchPeninsula-logo.ico"))
                        if (iconStream != null) _appIconBitmap = SKBitmap.Decode(iconStream);

                    // 兜底：如果外部文件丢失或解码失败，用系统图标转存
                    if (_appIconBitmap == null && _appSysIcon != null)
                    {
                        using var bmp = _appSysIcon.ToBitmap();
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
            _backdropHwnd = Win32.CreateWindowEx(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                "NotchConsoleClass", string.Empty,
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            ApplyRoundedRegion(_backdropHwnd);
            ApplyAppearance();
            TryEnableBackdropMaterial();
            ApplyBackdropPalette();

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_APPWINDOW,
                "NotchConsoleClass", "NotchPeninsula",
                Win32.WS_POPUP | Win32.WS_VISIBLE | Win32.WS_MINIMIZEBOX,
                left, top,
                _scaledWidth, _scaledHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            SyncBackdropToContent();

            SetupPluginDropTarget();

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

                var inst = _instance;
                if (inst != null && inst._hwnd != IntPtr.Zero)
                    Win32.PostMessage(inst._hwnd, WM_ASYNC_RERENDER, IntPtr.Zero, IntPtr.Zero);
            });

            Render();
        }

        private const int WM_ASYNC_RERENDER = 0x8000 + 0x52;

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (_instance != null)
                {
                    bool initializingContent = _instance._hwnd == IntPtr.Zero;
                    bool rebuildingBackdrop = _instance._backdropRebuilding && hwnd != _instance._hwnd;
                    if (initializingContent || rebuildingBackdrop
                        || hwnd == _instance._hwnd || hwnd == _instance._backdropHwnd)
                        return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
                }
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                // 记下来、吞掉，窗口继续活着：坏的顶多是这一次交互。
                Logger.Error($"窗口过程处理消息 0x{msg:X4} 时异常，已忽略", ex);
                return IntPtr.Zero;
            }
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 一律走材质窗分支。
            // 所以判据取两个句柄的并集，且重建期间只认内容窗：
            bool isBackdropWindow =
                (hwnd == _backdropHwnd && _backdropHwnd != IntPtr.Zero)
                || (_backdropRebuilding && hwnd != _hwnd);
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
                case Win32.WM_SYSCOMMAND:
                    if ((Win32.Low32(wParam) & 0xFFF0) == Win32.SC_MINIMIZE)
                    {
                        MinimizeToTaskbar();
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_SIZE:
                    if (Win32.Low32(wParam) == Win32.SIZE_MINIMIZED)
                    {
                        HideBackdrop();
                    }
                    else if (_hwnd != IntPtr.Zero)
                    {
                        ShowBackdrop();
                        ReapplyBackdropMaterial();
                        Render();
                    }
                    break;

                case Win32.WM_ACTIVATE:
                    if ((Win32.Low32(wParam) & 0xFFFF) != Win32.WA_INACTIVE && _hwnd != IntPtr.Zero)
                    {
                        ShowBackdrop();
                        ReapplyBackdropMaterial();
                        Win32.SetTimer(hwnd, BACKDROP_REFRESH_TIMER_ID, 150, IntPtr.Zero);
                    }
                    else if (_hotkeyRecordingIndex >= 0)
                    {
                        //    收工并保留原键位 —— 别把半截状态挂在那儿。
                        CancelHotkeyRecording();
                    }
                    break;

                case Win32.WM_TIMER:
                    if (wParam == BACKDROP_REFRESH_TIMER_ID)
                    {
                        Win32.KillTimer(hwnd, BACKDROP_REFRESH_TIMER_ID);
                        RepairBackdropAfterActivate();
                        return IntPtr.Zero;
                    }
                    if (wParam == DISPLAY_HOVER_TIMER_ID)
                    {
                        //    所以「没有动画在跑」时不会有任何空转的定时器。
                        if (!TickDisplayHoverAnim()) StopDisplayHoverAnim(hwnd);
                        return IntPtr.Zero;
                    }
                    if (wParam == SCROLLBAR_TIMER_ID)
                    {
                        if (!TickScrollBarFade())
                        {
                            Win32.KillTimer(hwnd, SCROLLBAR_TIMER_ID);
                            _scrollBarTimerOn = false;
                        }
                        return IntPtr.Zero;
                    }
                    if (TickMarketHint(wParam)) return IntPtr.Zero;
                    break;

                case Win32.WM_IME_SETCONTEXT:
                {
                    long ctx = lParam.ToInt64() & ~(long)unchecked((uint)Win32.ISC_SHOWUICOMPOSITIONWINDOW);
                    return Win32.DefWindowProc(hwnd, msg, wParam, (IntPtr)ctx);
                }

                case Win32.WM_IME_STARTCOMPOSITION:
                case Win32.WM_IME_COMPOSITION:
                case Win32.WM_IME_ENDCOMPOSITION:
                    if (HandleMarketIme((int)msg, lParam)) return IntPtr.Zero;
                    break;

                case Win32.WM_SETTINGCHANGE:
                {
                    bool wasLight = _isLightAppearance;
                    ApplyAppearance();
                    if (_isLightAppearance != wasLight)
                    {
                        ReapplyBackdropMaterial();
                        ApplyBackdropPalette();
                        Render();
                    }
                    break;
                }

                case Win32.WM_MOUSEMOVE:
                    OnMouseMove(
                        (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale),
                        (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale),
                        (Win32.Low32(wParam) & 0x0001) != 0);
                    break;

                case Win32.WM_LBUTTONDOWN:
                    OnLeftButtonDown(hwnd, (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale));
                    break;

                case Win32.WM_MOUSEWHEEL:
                    if (_toastSoundDropdownOpen)
                    {
                        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        GetToastSoundMenuLayout(out _, out _, out int maxFirst);
                        if (maxFirst > 0)
                        {
                            int target = Math.Clamp(_dropdownScroll - delta / 120 * 3, 0, maxFirst);
                            if (target != _dropdownScroll)
                            {
                                _dropdownScroll = target;
                                NotifyScrolled();   // 浮层侧那条滚动条也显形（停手后自动淡出）
                                SyncHoverFromCursor();
                                Render();
                            }
                        }
                        return IntPtr.Zero; // 吞掉，别让滚轮穿透到下层
                    }

                    // 光标在卡片外 → 只滚整页。
                    if (_selectedTab == 1)
                    {
                        float pageMax = GetDisplayPageMaxScroll();
                        _displayPageScroll = Math.Clamp(_displayPageScroll, 0f, pageMax);
                        GetDisplayListLayout(out _, out int displayMaxFirst);

                        int wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        int steps = wheelDelta / 120;            // 本格滚轮的方向与格数（向上为正）
                        int rows = steps * DISPLAY_WHEEL_STEP_ROWS;
                        float px = steps * DISPLAY_PAGE_WHEEL_STEP;

                        bool moved;

                        // 卡片里且列表还能滚：列表优先。滚得动就清掉接力累计。
                        if (_wheelPriorityList && displayMaxFirst > 0 && steps != 0)
                        {
                            int target = Math.Clamp(_displayScroll - rows, 0, displayMaxFirst);
                            if (target != _displayScroll)
                            {
                                _displayScroll = target;
                                _displayWheelCarry = 0;          // 列表自己动了，累计从头开始
                                moved = true;
                            }
                            else
                            {
                                //（用户改主意往下看了，重新从头计）。
                                _displayWheelCarry = Sign(_displayWheelCarry) == Math.Sign(steps)
                                    ? _displayWheelCarry + steps
                                    : steps;

                                if (Math.Abs(_displayWheelCarry) >= DISPLAY_WHEEL_CARRY_STEPS)
                                {
                                    // 越过阈值：接力给整页，并把这次滚轮的动量整个用掉。
                                    _displayWheelCarry = 0;
                                    moved = ScrollDisplayPage(pageMax, px);
                                }
                                else
                                {
                                    moved = false;           // 还在缓冲区内：什么都不动
                                }
                            }
                        }
                        else
                        {
                            _displayWheelCarry = 0;
                            moved = ScrollDisplayPage(pageMax, px);
                        }

                        if (moved)
                        {
                            // 滚动后光标下的行号与控件位置都变了，必须重算悬停，
                            // 否则紧接着的点击会拿旧下标命中错误的条目。
                            NotifyScrolled();   // 滚动条显形（停手后自动淡出）
                            SyncHoverFromCursor();
                            Render();
                        }
                        return IntPtr.Zero;
                    }

                    if (_selectedTab == 6 || _selectedTab == 7)
                    {
                        if (_marketDialog != MarketDialog.None) return IntPtr.Zero;   // 弹窗打开：吞掉滚轮

                        int wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        int steps = wheelDelta / 120 * 3;
                        bool moved;

                        if (_selectedTab == 7)
                        {
                            GetMarketListLayout(out _, out int marketMaxFirst);
                            int mTarget = Math.Clamp(_marketScroll - steps, 0, marketMaxFirst);
                            moved = mTarget != _marketScroll;
                            _marketScroll = mTarget;
                        }
                        else
                        {
                            GetPluginListLayout(out _, out int pluginMaxFirst);
                            int pTarget = Math.Clamp(_pluginScroll - steps, 0, pluginMaxFirst);
                            moved = pTarget != _pluginScroll;
                            _pluginScroll = pTarget;
                        }

                        if (moved)
                        {
                            NotifyScrolled();   // 滚动条显形（停手后自动淡出）
                            SyncHoverFromCursor();
                            Render();
                        }
                        return IntPtr.Zero; // 吞掉，别让滚轮穿透到下层
                    }
                    break;

                case Win32.WM_PAINT:
                    return IntPtr.Zero;

                case Win32.WM_CHAR:
                    if (HandleMarketSearchKey(-1, (char)(wParam.ToInt64() & 0xFFFF))) return IntPtr.Zero;
                    break;

                case Win32.WM_KEYDOWN:
                    if (HandleHotkeyRecording(Win32.Low32(wParam))) return IntPtr.Zero;
                    if (HandleMarketSearchKey(Win32.Low32(wParam), '\0')) return IntPtr.Zero;
                    break;

                case Win32.WM_SYSKEYDOWN:
                    if (HandleHotkeyRecording(Win32.Low32(wParam))) return IntPtr.Zero;
                    break;
                case WM_ASYNC_RERENDER:
                    Render();
                    return IntPtr.Zero;

                case Win32.WM_DESTROY:
                    RevokePluginDropTarget();
                    if (_backdropHwnd != IntPtr.Zero)
                    {
                        IntPtr backdrop = _backdropHwnd;
                        _backdropHwnd = IntPtr.Zero;
                        Win32.DestroyWindow(backdrop);
                    }
                    DisposeRenderBuffer();
                    // 定时器本身随窗口一起消失，只是把这个标志归位：
                    _displayHoverTimerOn = false;
                    _marketHintTimerOn = false;   // 同上：市场提示的自动消失表也要归位
                    _scrollBarTimerOn = false;    // 同上：滚动条的淡出表
                    _scrollBarShownAt = 0;        // 下次打开窗口时滚动条从隐藏开始
                    Renderer.StandbyActiveChanged -= OnStandbyActiveChanged;
                    _instance = null;
                    break;

                case Win32.WM_SETCURSOR:
                    if (_isHoveringDisabledArea && (Win32.Low32(lParam) & 0xFFFF) == 1) // 1 代表 HTCLIENT (客户区)
                    {
                        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, (int)32648)); // 强制注入系统 NO (禁止) 指针
                        return (IntPtr)1;
                    }
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void Render()
        {
            lock (_renderLock)
            {
                RenderCore();
            }
        }

        private unsafe void RenderCore()
        {
            var surface = _renderSurface;
            if (surface == null)
            {
                if (!EnsureRenderBuffer()) return;
                surface = _renderSurface!;
            }

            var canvas = surface.Canvas;
            canvas.ResetMatrix();          // surface 是复用的：必须把上一帧的矩阵 / 裁剪状态清干净
            canvas.Scale(_dpiScale);
            canvas.Clear(SKColors.Transparent);
            float cornerRadius = 8f;
            var windowRect = new SKRect(0, 0, WIDTH, HEIGHT);

            canvas.DrawRoundRect(windowRect, cornerRadius, cornerRadius, _bgPaint);

            canvas.Save();
            canvas.ClipPath(WindowClipPath, SKClipOperation.Intersect, true);

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

            if (_selectedTab == 0) RenderTabGeneral(canvas);
            else if (_selectedTab == 1) RenderTabDisplay(canvas);
            else if (_selectedTab == 2) RenderTabMedia(canvas);
            else if (_selectedTab == 3) RenderTabInteraction(canvas);
            else if (_selectedTab == 4) RenderTabAbout(canvas);
            else if (_selectedTab == 5) RenderTabPersonalize(canvas);
            else if (_selectedTab == 6) RenderTabPlugins(canvas);
            else if (_selectedTab == 7) RenderTabMarket(canvas);

            canvas.Restore();

            RenderDropdowns(canvas);

            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, HEIGHT - 0.5f), cornerRadius, cornerRadius, _globalBorderPaint);

            canvas.Flush();
            UpdateLayeredContentWindow();
        }

        private bool EnsureRenderBuffer()
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;

            try
            {
                _memDc = Win32.CreateCompatibleDC(screenDc);
                if (_memDc == IntPtr.Zero) return false;

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

                _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
                if (_hBitmap == IntPtr.Zero || _pBits == IntPtr.Zero)
                {
                    if (_hBitmap != IntPtr.Zero) { Win32.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
                    Win32.DeleteDC(_memDc);
                    _memDc = IntPtr.Zero;
                    return false;
                }

                _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);

                var info = new SKImageInfo(_scaledWidth, _scaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                _renderSurface = SKSurface.Create(info, _pBits, _scaledWidth * 4);
                if (_renderSurface == null)
                {
                    Win32.SelectObject(_memDc, _oldBitmap);
                    Win32.DeleteObject(_hBitmap);
                    Win32.DeleteDC(_memDc);
                    _hBitmap = IntPtr.Zero; _memDc = IntPtr.Zero; _oldBitmap = IntPtr.Zero; _pBits = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("[ConsoleWindow] 创建渲染缓冲失败", ex);
                return false;
            }
            finally
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void DisposeRenderBuffer()
        {
            _renderSurface?.Dispose();
            _renderSurface = null;

            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
                Win32.SelectObject(_memDc, _oldBitmap);

            if (_hBitmap != IntPtr.Zero) { Win32.DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { Win32.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            _oldBitmap = IntPtr.Zero;
            _pBits = IntPtr.Zero;
        }

        private void UpdateLayeredContentWindow()
        {
            if (_memDc == IntPtr.Zero) return;

            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;

            try
            {
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

                Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            }
            finally
            {
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

        private static void OnStandbyActiveChanged()
        {
            if (_instance == null || _instance._hwnd == IntPtr.Zero) return;
            if (_instance._selectedTab != 1) return;
            _instance.Render();
        }
    }
}
