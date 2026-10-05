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

        // ---- 持久化渲染缓冲（与 Core/NotchWindow 同一套做法）----
        //
        // 为什么必须有：设置窗口的 Render() 由交互驱动，悬停 / 滚轮 / 拖滑块每动一下就是
        // 一帧（OnMouseMove 里那一整串 newXxx != _xxx 比对通过就 Render()，见 WndProc），
        // 而滚动条拖拽 + 16ms 悬停动画期间就是 60fps 连续刷。
        //
        // 原实现（UpdateLayeredContentWindow）每一帧都：
        //   SKSurface.Create(整窗) → CreateCompatibleDC → CreateDIBSection →
        //   Buffer.MemoryCopy(约 2MB @150% DPI) → SelectObject → UpdateLayeredWindow →
        //   DeleteObject → DeleteDC
        // 一次性位图 + 一次整缓冲拷贝 + 一对内核对象创建/销毁，全是每帧的固定开销。
        //
        // 现在改成：DIB / memDC / SKSurface 全部按 _scaledWidth × _scaledHeight 建一次并常驻，
        // 每帧只做「Skia 画进常驻 surface → 拷进 DIB → 一次 UpdateLayeredWindow」。
        // _scaledWidth / _scaledHeight 是编译期常量派生（WIDTH/HEIGHT × 创建时的 DPI），
        // 且 _hwnd 是 readonly、窗口不重建 —— 所以这份缓冲的生命周期就是窗口本身，无需重建逻辑。
        //
        // 释放顺序必须严格照抄 Core/NotchWindow.DisposeRenderBuffer 的注释：
        // SelectObject 把旧位图选回去 → DeleteObject → DeleteDC。SKSurface 绑在 pBits 上，
        // 也要先于 hBitmap 释放。
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _hBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _pBits = IntPtr.Zero;
        private SKSurface? _renderSurface;

        // 渲染互斥锁：Render() 会被两个线程调 —— UI 线程（WndProc 里的鼠标事件、定时器、
        // 窗口初始化）和线程池线程（构造函数末尾 Task.Run 里那次异步刷新）。SkiaSharp 的
        // SKCanvas / SKSurface 不是线程安全的，两个线程同时进去会把 native 侧的内部状态踩坏，
        // 表现为随机的访问冲突（0xc0000005）：崩溃栈每次都不一样（reset_matrix / draw_round_rect /
        // draw_text_blob 都见过），调用点却都是 ConsoleWindow.Render()。
        //
        // 光靠「把调用点都搬到 UI 线程」不够稳：这个类将来任何新增的异步刷新都会重新引入同一个坑。
        // 所以渲染入口统一加锁，让「并发渲染」在结构上不可能发生 —— 后到的一方等前一方画完再画。
        private readonly object _renderLock = new object();

        // 插件中心行内按钮（渲染与鼠标命中必须使用同一组坐标）
        //    名称独占上行，按钮全在下行：从左到右 [重载] [移除] [开关]
        //    排序小三角（← / →）已移除 —— 显示与排序统一收敛到
        //       「显示设置 → 显示内容」那一张列表，插件中心只留启用/禁用这一件事。

        private const float PLUGIN_BTN_RELOAD_X = 404f;  // 重载按钮

        private const float PLUGIN_BTN_REMOVE_X = 460f;  // 移除按钮

        private const float PLUGIN_BTN_TOGGLE_X = 516f;  // 开关按钮

        // 显示设置页「显示内容」列表（渲染与鼠标命中必须使用同一组坐标）
        //    每行 = 复选框（勾选显示 / 隐藏）+ 名称 + ∧ ∨（调整在岛上的先后次序）
        //    行高与首行偏移是一对渲染/命中同源的常量，改一个必须两个一起改。

        private const float DISPLAY_ROW_H = 34f;

        private const float DISPLAY_FIRST_ROW_Y = 56f;    // 首行顶部相对卡片顶部的偏移

        /// <summary>
        /// 「显示内容」列表最多显示几行 —— 列表高度、可视行数、可滚范围的唯一真源。
        /// 条目多于这个数就走列表自己的滚动（滚轮 / 拖右侧滚动条），卡片高度不再跟着条目数变。
        /// 卡片正好卡在最后一行底部，不留提示余量：溢出与否由右侧那条滚动条表达，
        /// 不再另写一行「滚轮可滚动查看其余 N 项」。
        /// </summary>
        private const int DISPLAY_MAX_ROWS = 8;

        /// <summary>「显示内容」卡片顶部相对标题栏的偏移（渲染与命中必须同源；紧随待机模式卡之后）。</summary>
        private const float DISPLAY_CARD_Y = STANDBY_CARD_Y + STANDBY_CARD_H + 12f;

        /// <summary>
        /// 「显示内容」卡片高度：由 DISPLAY_MAX_ROWS 反推，正好放下约定的行数，底部不留空。
        /// 固定高度（不随条目数变），条目更多时由列表自身滚动查看。
        /// </summary>
        private const float DISPLAY_CARD_H = DISPLAY_FIRST_ROW_Y + DISPLAY_MAX_ROWS * DISPLAY_ROW_H;

        /// <summary>滚轮一格（120）滚动几行。</summary>
        private const int DISPLAY_WHEEL_STEP_ROWS = 3;

        // ---- 显示设置页整页滚动 + 「显示模式」/「待机模式」卡片（渲染与鼠标命中必须同源）----
        //    卡片自上而下：显示形态(12) → 目标显示器(172) → 显示模式(246) → 待机模式(500) → 显示内容(754)，
        //    相邻卡之间留 12px；页面内容高于窗口，靠 _displayPageScroll 整页滚动查看。
        //    「目标显示器」从最底一张（待机模式之后）提到「显示形态」正下方：
        //      它决定整块岛画在哪块屏上，属于「先选屏幕、再谈样式/模式」的前置项。

        /// <summary>整页滚轮一格（120）滚动的像素。</summary>
        private const float DISPLAY_PAGE_WHEEL_STEP = 48f;

        // 「列表滚到头之后接力滚整页」的触发阈值，单位是滚轮格数（一格 = 120）。
        // 语义：光标在「显示内容」卡片里、列表已经顶到上 / 下边界，用户还继续朝同一方向滚 ——
        //   累计满 2 格之后，这一层才把滚轮让给整页，页面接管继续滚。
        // 为什么要这个阈值：滚到边界就立刻把滚动传出（曾经的行为）在触控板 / 高分辨率滚轮下
        //   几乎必然误触发 —— 列表刚好停在最后一格时，手指多蹭一点，整页就跟着跳一大截。
        //   给两格缓冲，边界区变成一个「必须明显继续滚」的动作，误触基本消失。
        // 阈值以「格」而不是像素为单位，是为了与触控板的小步长滚动解耦（小步长会累积）。
        private const int DISPLAY_WHEEL_CARRY_STEPS = 2;

        /// <summary>
        /// 列表顶到边界后，朝同一方向继续滚动所累积的格数。
        /// 达到 DISPLAY_WHEEL_CARRY_STEPS 就转去滚整页，并清零。
        /// 方向反转、滚轮去了别的层、或列表本身又滚动了，都要清零（见 WM_MOUSEWHEEL 分支）。
        /// </summary>
        private int _displayWheelCarry;

        /// <summary>整页滚动一程（沿整页滚动轴移动 px 像素）。返回是否真的动了。</summary>
        private bool ScrollDisplayPage(float pageMax, float px)
        {
            if (pageMax <= 0f) return false;
            float target = Math.Clamp(_displayPageScroll - px, 0f, pageMax);
            if (Math.Abs(target - _displayPageScroll) <= 0.5f) return false;
            _displayPageScroll = target;
            return true;
        }

        /// <summary>取符号（-1 / 0 / 1）。累计量只是用来比方向，用不着真值。</summary>
        private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        /// <summary>目标显示器卡顶部相对标题栏的偏移（紧接「显示形态」卡之后）。</summary>
        private const float MONITOR_CARD_Y = 172f;

        /// <summary>目标显示器卡高度（标题 + 副标题 + 右侧下拉框）。</summary>
        private const float MONITOR_CARD_H = 62f;

        /// <summary>「显示模式」卡（待机 / 普通切换 + 双击开关）顶部相对标题栏的偏移。</summary>
        private const float MODE_CARD_Y = MONITOR_CARD_Y + MONITOR_CARD_H + 12f;

        /// <summary>
        /// 「显示模式」卡高度 = 开关行行首（MODE_TOGGLE_ROW_Y）+ 该行两行文字块高（约 48）+ 底部留白 16。
        /// 留白只给一个卡片内边距的量（同页目标显示器卡 13、待机模式卡 20），
        /// 不能让开关行下面拖出半行空档。开关行位置变动时这里自动跟随。
        /// </summary>
        private const float MODE_CARD_H = MODE_TOGGLE_ROW_Y - MODE_CARD_Y + 64f;

        /// <summary>两个显示模式选项的顶部与尺寸（相对标题栏；与「显示形态」选项同款 150×90）。</summary>
        private const float MODE_OPT_Y = MODE_CARD_Y + 56f;

        private const float MODE_OPT_W = 150f;

        private const float MODE_OPT_H = 90f;

        private const float MODE_OPT_GAP = 20f;

        private const float MODE_OPT_X = 220f;

        /// <summary>「双击空白切换待机模式」开关行的 yOffset（喂给 DrawToggleRow）。</summary>
        private const float MODE_TOGGLE_ROW_Y = MODE_CARD_Y + 154f;

        /// <summary>「待机模式」卡（三个场景选项）顶部相对标题栏的偏移。</summary>
        private const float STANDBY_CARD_Y = MODE_CARD_Y + MODE_CARD_H + 12f;

        private const float STANDBY_CARD_H = 168f;

        /// <summary>三个待机场景选项的顶部与尺寸（相对标题栏，横向排列）。</summary>
        private const float STANDBY_OPT_Y = STANDBY_CARD_Y + 56f;

        private const float STANDBY_OPT_W = 112f;

        private const float STANDBY_OPT_H = 92f;

        private const float STANDBY_OPT_GAP = 8f;

        private const float STANDBY_OPT_X = 208f;

        private const float DISPLAY_MOVE_UP_X = 486f;     // ∧ 槽左边界（槽宽 = SORT_TRI_W）

        private const float DISPLAY_MOVE_DOWN_X = 504f;   // ∨ 槽左边界（与 ∧ 只隔 2px，视觉上是同一组控件）

        private const float SORT_TRI_W = 16f;             // 排序三角形的点击槽宽

        // 行悬停底色动画：鼠标压到某一行时，行底由浅入深淡入，移开再淡出（与托盘菜单同款 16ms 节拍）。
        //    —— 只是把「指针在哪一行」这个离散状态补上过渡，避免硬切造成的闪烁感。
        private const uint DISPLAY_HOVER_TICK_MS = 16;

        private const float DISPLAY_HOVER_EASE = 0.35f;   // 每拍向目标靠拢的比例（指数缓出）

        // 行悬停动画的窗口定时器 id（与 BACKDROP_REFRESH_TIMER_ID 各自独立）
        private static readonly IntPtr DISPLAY_HOVER_TIMER_ID = new IntPtr(0x4E51); // "NQ"

        // 通用设置页「切换灵动岛字体」卡片（渲染与鼠标命中必须使用同一组坐标）
        // 通用设置页卡片顺序（提示音并入通知卡之后）：
        //    开机自启 12 | 窗口置顶 84 | 系统消息通知卡 156..390（三行）| 剪贴板链接检测 400 | 切换灵动岛字体 474
        //
        //  系统消息通知卡 = 一张三行卡 + 一行提示音设置，把通知本体与它的两个附属设置放在一起：
        //     行1「系统消息通知」总开关（开关热区 +176..+196）    ← 主体
        //     行2「消息通知内容」下拉（+230..+262）              ← 附属（管内容）
        //     行3「消息提示音」开关（开关热区 +300..+320）        ← 附属（管声音）
        //     行4「提示音」下拉 + 音量下拉 + [试听][重置]（+354..+386）← 行3 的设置行，无开关
        //     提示音是通知的附属设置，必须和通知在同一张卡里 —— 拆成两张独立卡会让层级关系丢失。
        //     卡片下沿必须贴合内容（现距内容底 374 留 30px），别撑高。
        //     行 4 是全页唯一「4 控件并排」的行，控件加间隙正好占满整个内容区；
        //        因此左侧标签须单独预留空间（SOUND_CTRL_X 由标签宽度派生），且该行不放描述文字。
        //     所有控件右边界一律 `WIDTH - 36`（卡片内右侧留白），横向绝不铺满整卡。
        //     改这里的数值时必须同步改 OnMouseMove 的 tab 0 段与 RenderDropdowns 的浮层锚点。

        /// <summary>卡片内右侧内边距：所有右对齐控件的右边界都锚到这里。</summary>
        private const float CARD_PAD_RIGHT = 36f;

        // ---- ① 系统消息通知卡（三行 + 一行附属设置）----
        //
        // 布局节奏：行距恒为 62px，与全页所有单行卡同一节奏。
        //   · 行 1「系统消息通知」行首 156
        //   · 行 2「消息通知内容」行首 218 = 行 1 + 62
        //   · 行 3「消息提示音」  行首 280 = 行 2 + 62
        //   · 行 4「提示音设置」  行首 342 = 行 3 + 62
        //   · 分隔线放在每一对行之间：+222、+284
        //   · 卡片 156..404（248 = 4 × 62）
        //
        // 历史坑：卡片曾被撑到 320 而内容只用到 292 —— 多出的 28px 先表现为「分隔线到行 2
        //    之间一大块空白」，把分隔线往下挪之后空白又跑到行 1 下面。
        //    空白总量不变，挪分割线是治不好的 —— 唯一正解是让卡片贴合内容。
        //    判据：`卡片下沿 - 内容底` 必须落在 [8, 20]。

        /// <summary>「系统消息通知」总开关（行 1）行首偏移。</summary>
        private const float TOAST_ROW1_Y = 156f;

        /// <summary>「消息通知内容」行 2 行首偏移（= 行 1 行首 + 行距 62）。</summary>
        private const float TOAST_ROW2_Y = TOAST_ROW1_Y + 62f;

        /// <summary>行 1 与行 2 之间的分隔线（= 行 2 行首 + 4，落在行 1 内容底 202 与行 2 控件顶 230 之间）。</summary>
        private const float TOAST_SEP_Y = TOAST_ROW2_Y + 4f;

        // 行内纵向锚点（全页唯一真源，第五次返工后定稿）
        //
        //  目标：「左侧文字块」与「右侧控件」同心对齐 —— 文字块的光学中心
        //        和右排控件（开关轨道 / 下拉框 / 按钮）的中心落在同一条水平线上。
        //
        //  ── 返工史（前四轮都错在「拿什么当对齐参照」）
        //    第 1 轮：四行各写各的基线偏移 —— +26 / +33 / +26 / +21。
        //    第 2 轮：改成「标签基线 = 框顶 + h/2 + 5」，即跟着框内文字走。
        // 错：框内文字在框里本身偏下，把行外标签也拖下去了。
        //    第 3 轮：改成「所有行统一基线 = 行首 + 26」。
        // 错：26 是两行行（标题+副标题）的标题基线，
        //                单行行（行 4「提示音」）拿它当基线就飘到下拉框上面去了 —— 「现在太靠上了」。
        //    第 4 轮：改成「单行墨迹中线 == 控件中心」，偏移 = 30 + 5.5 = 35.5。
        // 错：35.5 只对单行行成立。两行行照抄之后，整个文字块
        //                （标题墨迹顶 → 副标题墨迹底）比控件中心低了 10px —— 就是
        //                「开机自启、窗口置顶、系统消息通知、剪贴板链接检测的文字全部向下偏移」。
        //    第 5 轮（本版）：按「本行有几行文字」分别反解，两个偏移都让
        //                「文字块的光学中心」落在同一个锚点上（见下面两个常量）。
        //
        //  ── 为什么锚点能同时适配「20px 轨道」和「32px 框」
        //    因为 ROW_DROPDOWN_TOP 已经取 14，使框中心（14+16）恰好等于
        //    开关轨道中心（20+10），两者都 = 行首 + 30 = ROW_ANCHOR_Y。
        //    所以「控件中心」这个参照在两类行里是同一个数，文字只需要按行数选偏移。
        //
        //  直接把 `ROW_ANCHOR_Y`(30) 当基线是错的 —— 基线与墨迹中线差 5.5px。
        //  单行行与两行行必须用不同的基线常量，这是第 3/4 轮反复翻车的根因。
        //  改字号 / 改字体族必须重新标定 TEXT_INK_MID_OFFSET 与 TEXT_INK_ASCENT。

        /// <summary>行内纵向锚点：每行「右侧控件中心 / 左侧文字块光学中心」的相对偏移。</summary>
        private const float ROW_ANCHOR_Y = 30f;

        /// <summary>
        /// 13px 字号的墨迹几何（实测标定，Microsoft YaHei UI）：
        /// 绘制基线 y 之上 11px 到基线处是墨迹，即 `top = 基线-11`、`bot = 基线`、`中线 = 基线-5.5`。
        /// 换字号 / 换字体族必须重新标定这两个数。
        /// </summary>
        private const float TEXT_INK_MID_OFFSET = 5.5f;

        /// <summary>墨迹在基线上方的高度（13px YaHei UI 实测 11px）。</summary>
        private const float TEXT_INK_ASCENT = 11f;

        /// <summary>行内「标题 → 副标题」的行距（两行文字之间的基线差）。</summary>
        private const float ROW_SUB_OFFSET = 20f;

        /// <summary>
        /// 单行行（只有标题、没有副标题，如通知卡行 4 的「提示音」）的标题基线偏移。
        ///
        /// 反解：墨迹中线 = 基线 - 5.5，令它 = 行首 + ROW_ANCHOR_Y(30)
        /// → 基线 = 行首 + 30 + 5.5 = 行首 + 35.5。
        /// </summary>
        private const float ROW_TEXT_BASELINE_SINGLE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET;   // = 35.5

        /// <summary>
        /// 两行行（标题 + 副标题，如「开机自启」「系统消息通知」）的标题基线偏移。
        ///
        /// 反解：文字块的墨迹范围 = [基线 - 11, 基线 + ROW_SUB_OFFSET]，
        ///       块中线 = 基线 + (ROW_SUB_OFFSET - 11) / 2 = 基线 + 4.5，
        ///       令块中线 = 行首 + ROW_ANCHOR_Y(30)
        /// → 标题基线 = 行首 + 30 + 5.5 - 20 / 2 = 行首 + 25.5，副标题 = 行首 + 45.5。
        ///
        /// 曾经把它和单行行合并成 35.5：那是拿「标题那一行的墨迹中线」去对控件中心，
        ///    整个两行文字块因此整体下移 10px（就是「文字全部向下偏移」那个现象）。
        /// 也别写成 `ROW_ANCHOR_Y - 4`（= 26）：那是把「基线」当「视觉中心」，
        ///    虽然只差 0.5px 看着没事，但语义是错的，下次改字号就会崩。
        /// </summary>
        private const float ROW_TEXT_BASELINE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET - ROW_SUB_OFFSET / 2f;   // = 25.5

        /// <summary>
        /// 下拉框（h=32）的框顶偏移：要让框中心落在 `行首 + ROW_ANCHOR_Y`，
        /// 即 `框顶 + 16 = 30` → 框顶 = 行首 + 14（与开关轨道同中心）。
        /// 不是 +12（旧值，中心 28，比开关低 2px）也不是 0（旧值，中心 16，比开关高 14px）。
        /// </summary>
        private const float ROW_DROPDOWN_TOP = 14f;

        /// <summary>
        /// 下拉行「框内文字」相对框顶的基线偏移 = `DrawDropdownBox` 的 `h/2 + 5`。
        /// 这是框自己内部的排版参数，只用于把框内文字摆正在框里，
        ///     绝不可拿它当「框外标签的对齐口径」（曾经就是这么治错的）。
        /// </summary>
        private const float DROPDOWN_TEXT_BASELINE = 21f;

        /// <summary>「消息通知内容」行 2 标题基线（= 行首 + 统一文字基线偏移，不再跟随框顶）。</summary>
        private const float TOAST_ROW2_TITLE_Y = TOAST_ROW2_Y + ROW_TEXT_BASELINE;

        /// <summary>「消息通知内容」行 2 描述基线（= 标题下移一个行内行距 ROW_SUB_OFFSET）。</summary>
        private const float TOAST_ROW2_DESC_Y = TOAST_ROW2_TITLE_Y + ROW_SUB_OFFSET;

        /// <summary>「消息通知内容」下拉框顶偏移（= 行 2 行首 + 14，与开关轨道同中心）。</summary>
        private const float TOAST_MODE_ROW_Y = TOAST_ROW2_Y + ROW_DROPDOWN_TOP;

        private const float TOAST_MODE_ROW_H = 32f;

        /// <summary>通知内容下拉：贴着卡片右边界，宽 110。</summary>
        private const float TOAST_MODE_CTRL_W = 110f;

        private const float TOAST_MODE_CTRL_X = WIDTH - CARD_PAD_RIGHT - TOAST_MODE_CTRL_W;

        // ---- 行 3 / 行 4：消息提示音（通知卡的附属设置，不是独立卡片）----

        /// <summary>「消息提示音」行 3 行首偏移（= 行 2 行首 + 62）。
        /// 行 3 的标题 +26 / 副标题 +46 / 标题文本由 `DrawToggleRow` 按相对偏移自行计算，无需额外常量。</summary>
        private const float SOUND_ROW3_Y = TOAST_ROW2_Y + 62f;

        /// <summary>行 2 与行 3 之间的分隔线（= 行 3 行首 + 4，落在行 2 内容底 262 与行 3 控件顶 300 之间）。</summary>
        private const float SOUND_SEP_Y = SOUND_ROW3_Y + 4f;

        /// <summary>开关轨道的几何：高 20，中心即行内锚点。开关行用它画轨道，命中判定也用它。</summary>
        private const float TOGGLE_TRACK_H = 20f;

        /// <summary>「消息提示音」开关（行 3）的轨道顶（= 行 3 行首 + ROW_ANCHOR_Y - 轨道半高）。</summary>
        private const float SOUND_TOGGLE_ROW_Y = SOUND_ROW3_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f;

        /// <summary>「系统消息通知」总开关（行 1）的轨道顶。</summary>
        private const float TOAST_TOGGLE_ROW_Y = TOAST_ROW1_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f;

        /// <summary>提示音设置行（行 4）行首偏移（= 行 3 行首 + 62）。
        /// 行 4 没有开关，是行 3 的附属设置：左起标签，右起「提示音」下拉 + 音量下拉 + [试听][重置]。</summary>
        private const float SOUND_ROW_Y = SOUND_ROW3_Y + 62f;

        /// <summary>行 4 标题基线（= 行首 + 单行行基线偏移 ROW_TEXT_BASELINE_SINGLE = 行首 + 35.5）。
        ///
        /// 行 4 只有「提示音」三个字、没有副标题，所以走单行行的口径：
        ///    墨迹中线对齐行内锚点（= 行 4 下拉框 / 按钮中心，实测均为 404.0）。
        ///    这里不能用两行行的 `ROW_TEXT_BASELINE`(25.5)，否则文字会飘到框上方。
        ///    两个常量的差别就是「这一行有几行文字」，见文件头部锚点说明。
        /// 行 4 只有左侧一个短标签、无描述（空间被 4 个控件占满，放不下第二行文字）。
        /// </summary>
        private const float SOUND_ROW_TITLE_Y = SOUND_ROW_Y + ROW_TEXT_BASELINE_SINGLE;

        private const float SOUND_ROW_H = 32f;

        /// <summary>
        /// 行 4 三个下拉框 / 两个按钮共用的框顶偏移 = 行首 + ROW_DROPDOWN_TOP（= 行 4 行首 + 14）。
        ///
        /// 不要再把框顶直接写成 `SOUND_ROW_Y`（行首本身）：那会让框中心落在行首 + 16，
        ///    比同一行的标签墨迹中心（行首 + 30）高 14px，视觉上就是「提示音三个字和右边按钮不齐」。
        ///    曾经点名的「子卡片顶部再加 5px padding」本质就是要把这一段往下压。
        /// 所有「框/按钮的顶」都走本常量，「行首」只用来说明行从哪儿起（命中判定、浮层锚点用行首）。
        /// </summary>
        private const float SOUND_BOX_Y = SOUND_ROW_Y + ROW_DROPDOWN_TOP;

        /// <summary>系统消息通知卡底部偏移。
        ///
        /// 不能用「行数 × 62」硬套：62 是「行首到行首」的行距，不是「行首到卡底」的间距。
        ///    卡片底 = 行 4 控件底 + 收尾留白。行 4 控件占 +14..+46（框顶 14 + 高 32），
        ///    所以底 = 342 + 46 + 16 = 404。
        ///    收尾留白取 16px，与单行卡「开关轨底 40 → 卡底 62」的 22px 观感相当
        ///    （控件比文字矮，留白可以略小）。
        ///    曾经写成 404（硬套 4×62 = 248）时是巧合相等，后来框顶上移才暴露不对；
        ///       现在这一版的 404 是从 SOUND_BOX_Y 推出来的，不是硬套。
        ///    判据：`TOAST_CARD_BOTTOM - (SOUND_BOX_Y + SOUND_ROW_H)` 应落在 [12, 20]。</summary>
        private const float TOAST_CARD_BOTTOM = SOUND_BOX_Y + SOUND_ROW_H + 16f;

        /// <summary>
        /// 提示音行「从右往左」排版时用的横向间隙。整行必须刚好塞进卡片内容区
        /// （216 .. WIDTH-36，共 348px），所以每个宽度都是按实测文本宽度抠出来的：
        /// 最长选项「手表提示（watchOS）」132.9px + 左右内边距与箭头 ≈ 156。
        /// 改任一宽度都要重算总和，加起来超过 348 就会像上一版那样怼出卡片左边界。
        /// </summary>
        private const float SOUND_ROW_GAP = 12f;

        /// <summary>[重置] 与 [试听] 按钮（从右往左排，右边界锚卡片内边界）。
        /// 按钮高 26，要让中心也落在 `行首 + ROW_ANCHOR_Y`，则顶 = 框顶 + (框高 32 - 按钮高 26) / 2 = 框顶 + 3。</summary>
        private const float SOUND_BTN_H = 26f;

        private const float SOUND_BTN_Y = SOUND_BOX_Y + (SOUND_ROW_H - SOUND_BTN_H) / 2f;

        private const float SOUND_BTN_GAP = 6f;

        private const float SOUND_BTN_W = 46f;

        private const float SOUND_RESET_X = WIDTH - CARD_PAD_RIGHT - SOUND_BTN_W;

        private const float SOUND_PREVIEW_X = SOUND_RESET_X - SOUND_BTN_GAP - SOUND_BTN_W;

        /// <summary>「音量」下拉：接在按钮组左边。58px 足够放「100%」+箭头，再多就是浪费。</summary>
        private const float SOUND_VOL_W = 58f;

        private const float SOUND_VOL_X = SOUND_PREVIEW_X - SOUND_ROW_GAP - SOUND_VOL_W;

        /// <summary>行 4 左侧标签「提示音」的起始 x（与其它行一致，锚卡片左内边距）。</summary>
        private const float SOUND_LABEL_X = 216f;

        /// <summary>标签与「提示音」下拉之间的间隙。</summary>
        private const float SOUND_LABEL_GAP = 8f;

        /// <summary>
        /// 「提示音」下拉左边界。
        ///
        /// 这一行是全页唯一 4 个控件并排的行（下拉 + 音量 + 试听 + 重置，共 340px），
        ///    而内容区只有 348px（216..564）。所以它不能像其它行那样从 216 起排 ——
        ///    那样会把左侧标签区挤成负数（216 - 8 = 208，小于 216），文字直接叠到下拉框上。
        ///    这里给标签留出实测宽度（「提示音」3 字 13.5px ≈ 39px）+ 8px 间隙。
        ///    改这里要同步 `SOUND_CTRL_W`，并确认 `SOUND_LABEL_X + 标签宽 + GAP == SOUND_CTRL_X`。
        /// </summary>
        private const float SOUND_CTRL_X = SOUND_LABEL_X + 40f + SOUND_LABEL_GAP;

        /// <summary>「提示音」下拉宽度：右边界正好贴住音量下拉。</summary>
        private const float SOUND_CTRL_W = SOUND_VOL_X - SOUND_ROW_GAP - SOUND_CTRL_X;

        // ---- ② 剪贴板链接检测（通知卡之后，行首 = 通知卡底 + 标准卡片间隙 10）----

        /// <summary>剪贴板链接检测卡行首 = 通知卡底 + 10。
        /// 必须由 TOAST_CARD_BOTTOM 派生：通知卡高度一改（本页改过三次），这里跟着自动走。
        ///    曾经就是因为剪贴板卡写死 400，而通知卡底从 390 长到 404，两卡直接叠在一起。</summary>
        private const float CLIPBOARD_CARD_Y = TOAST_CARD_BOTTOM + 10f;

        // ---- ③ 切换灵动岛字体（剪贴板卡之后，间隙 12）----

        /// <summary>切换字体卡行首 = 剪贴板卡行首 + 62 + 12。</summary>
        private const float FONT_CARD_Y = CLIPBOARD_CARD_Y + 62f + 12f;

        private const float FONT_BTN_H = 26f;          // 按钮高度

        /// <summary>按钮顶 = 卡片行首 + 行内锚点 - 按钮半高 —— 与卡片内文字块同心（不再手写 18）。</summary>
        private const float FONT_BTN_Y = FONT_CARD_Y + ROW_ANCHOR_Y - FONT_BTN_H / 2f;

        private const float FONT_RESET_W = 56f;        // [重置] 按钮宽度

        private const float FONT_PICK_W = 78f;         // [选择字体] 按钮宽度

        private const float FONT_RESET_X = WIDTH - 36 - FONT_RESET_W;

        private const float FONT_PICK_X = FONT_RESET_X - 10 - FONT_PICK_W;

        // 媒体设置页「目标媒体平台 + 匹配方式」合并卡片（渲染与鼠标命中必须使用同一组坐标）
        // 媒体设置页卡片顺序（合并后）：
        //    媒体控制 12..74 | 合并卡片（两行）84..208 | 歌词设置 222..398
        //    合并卡片：第 1 行「目标媒体平台」行首 84、分隔线 142、第 2 行「匹配方式」行首 146（行距 62）
        // 第 1 行下拉框 +96..+128 的命中判定写在 WM_MOUSEMOVE 的 tab 2 段里（+98..+128），
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

        // 窗口类注册时那个 HICON 的托管宿主。窗口类里的 hIcon 要活到进程结束，
        // 所以这里必须持有 Icon 实例（原实现只取了 .Handle 就把 Icon 丢掉，
        // 等于靠"Icon 没有终结器"这一隐式假设在保活那个句柄）。
        // 与 _appIconBitmap 一样属于进程级常驻资源，故意不在窗口销毁时释放。
        private static Icon? _appSysIcon;

        private static string _appTitleWithVersion = "NotchPeninsula";

        // 侧边栏与通用设置状态

        private int _selectedTab = 0;

        private int _hoveredTab = -1;

        private bool _isAutoStartEnabled;

        private bool _toggleHovered = false;

        private bool _toastToggleHovered = false;

        private bool _topmostToggleHovered = false;

        private bool _clipboardToggleHovered = false; // 「剪贴板链接检测」（从交互设置搬到通用设置）
        // 灵动岛字体切换状态（字体本身由 FontConfig 统一持有）

        private bool _fontPickHovered = false;

        private bool _fontResetHovered = false;

        private string _fontHint = ""; // 加载失败时在卡片副标题上直接提示，避免弹窗打断操作
        // 交互设置状态

        private bool _autoHideToggleHovered = false;

        private bool _focusHideToggleHovered = false; // 「当焦点离开时自动隐藏岛」——自动隐藏卡片的第二行

        private bool _pauseHideToggleHovered = false; // 「暂停播放后自动隐藏」——自动隐藏卡片的第三行

        private bool _fsHideToggleHovered = false;    // 「全屏自动隐藏」——自动隐藏卡片的第四行

        private bool _mediaExpToggleHovered = false;

        /// <summary>「双击媒体控制跳转应用」开关（交互设置页，排在「媒体交互方式」下面一格，默认开启）。</summary>
        private bool _appLaunchToggleHovered = false;

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

        // 消息提示音状态（值本身存在 ToastSoundConfig 静态类里，这里只放 UI 交互态）
        private bool _toastSoundDropdownOpen = false;

        private bool _toastSoundDropdownHovered = false;

        private int _hoveredToastSoundIndex = -1;

        /// <summary>
        /// 提示音下拉浮层的滚动首行。列表是动态扫目录来的（可能 40 项），
        /// 浮层高度被 RenderDropdownList 钳制在窗口内，超出的行靠这个偏移滚动查看。
        /// </summary>
        private int _dropdownScroll = 0;

        /// <summary>下拉浮层的行高。绘制、命中、滚轮三处必须共用这一个数。</summary>
        private const float DROPDOWN_ROW_H = 26f;

        /// <summary>
        /// 提示音下拉浮层的唯一布局真源：把「浮层顶 / 可视行数 / 最大首行」算成一套，
        /// 供绘制（RenderDropdownList）、悬停命中（OnMouseMove）、滚轮（WM_MOUSEWHEEL）三处共用。
        ///
        /// 以前这三处各写一份，而且滚轮那份把浮层顶写成了 `SOUND_ROW_Y + SOUND_ROW_H + 2`
        ///    （漏了 `ROW_DROPDOWN_TOP` = 14）—— 与绘制侧差 14px，可滚范围因此对不上，
        ///    表现就是「滚两下就滚不动了」。改这里即三处同时生效。
        /// </summary>
        private void GetToastSoundMenuLayout(out float menuTop, out int visibleRows, out int maxFirstRow)
        {
            int total = ToastSoundConfig.OptionCount;
            menuTop = TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H + 2f;
            int maxRows = Math.Max(1, (int)((HEIGHT - 12 - menuTop) / DROPDOWN_ROW_H));
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        /// <summary>
        /// 「音量」下拉浮层的唯一布局真源（向上展开：底边贴住音量框上沿）。
        /// 三个出参 = 浮层顶 / 浮层底 / 可视行数，绘制与命中都只认它。
        ///
        /// 算式必须与 RenderDropdownList 的 `upward: true` 分支逐字同源
        ///    （`availFrom = anchorY - 2`、`maxRows = (availFrom - TITLE_BAR_HEIGHT - 12) / 行高`）。
        ///    以前绘制侧减了那个 2、命中侧没减，两边靠巧合算出同一个行数（都是 13），
        ///    只要 SOUND_BOX_Y 挪动十几像素就会立刻错位 —— 与「提示音列表滚不动」
        ///    是同一种病：同一份布局在多处各写一份。绘制与命中现在都调本方法。
        /// </summary>
        private void GetVolumeMenuLayout(out float menuTop, out float menuBottom, out int visibleRows)
        {
            menuBottom = TITLE_BAR_HEIGHT + SOUND_BOX_Y - 2f;   // 对应 RenderDropdownList 的 anchorY - 2
            int maxRows = Math.Max(1, (int)((menuBottom - TITLE_BAR_HEIGHT - 12f) / DROPDOWN_ROW_H));
            visibleRows = Math.Min(ToastSoundConfig.VolumeOptions.Length, maxRows);
            menuTop = menuBottom - visibleRows * DROPDOWN_ROW_H;
        }

        /// <summary>
        /// 展开提示音下拉时把滚动位置定到「当前选中项可见」处 —— 只在这一刻做一次。
        /// 之后滚动位置完全由滚轮决定：曾经在绘制与命中里每帧「抢回选中项」，
        /// 结果滚轮刚滚下去、下一帧就被拉回顶部，用户看到的就是「根本滚不动」。
        /// </summary>
        private void ScrollToastSoundMenuToSelected()
        {
            GetToastSoundMenuLayout(out _, out int visible, out int maxFirst);
            _dropdownScroll = maxFirst <= 0
                ? 0
                : Math.Clamp(ToastSoundConfig.SelectedIndex - visible / 2, 0, maxFirst);
        }

        /// <summary>
        /// 按当前光标位置重算一次悬停态。滚轮不产生 WM_MOUSEMOVE ——
        /// 滚动后光标下的行号变了，但 hover 索引还停在「滚动前」那一项，
        /// 紧接着的点击就会选错音源（表现就是「断触」）。滚动完必须补这一下。
        /// </summary>
        private void SyncHoverFromCursor()
        {
            if (!Win32.GetCursorPos(out var pt) || !Win32.GetWindowRect(_hwnd, out var rect))
                return;
            OnMouseMove((int)((pt.x - rect.Left) / _dpiScale), (int)((pt.y - rect.Top) / _dpiScale), false);
        }

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

        private bool _scanToggleHovered = false;

        private bool _lyricMinusHovered = false;

        private bool _lyricPlusHovered = false;

        private bool _lyricResetHovered = false;
        // 关于页交互状态

        private int _hoveredLinkIndex = -1;

        // 显示设置
        // 「显示内容」列表的悬停行：-1 = 没悬停任何行。
        // 三处分开记，是因为同一行里复选框与 ∧ / ∨ 的悬停反馈互不相同；
        // _displayHoverRow 是「指针压在这一行的哪个部位都算」的可视槽位，专门驱动行底动画。
        private int _hoveredDisplayRow = -1;

        private int _hoveredDisplayMoveUp = -1;

        private int _hoveredDisplayMoveDown = -1;

        /// <summary>
        /// 「显示内容」列表的滚动首行（绝对条目下标）。条目数（插件可能很多）会超过卡片
        /// 能放下的行数，超出的部分靠这个偏移滚动查看；滚轮是唯一的改动入口。
        /// 渲染、命中、滚轮三处都通过 GetDisplayListLayout 取可滚范围。
        /// </summary>
        private int _displayScroll = 0;

        /// <summary>
        /// 悬停行在可视窗口里的槽位（0 = 当前首行），-1 = 没悬停任何行。
        /// 存槽位而不是绝对下标，是因为行底动画数组按槽位索引（一屏最多十来行）。
        /// </summary>
        private int _displayHoverRow = -1;

        // 每行的悬停进度（0 = 没悬停，1 = 完全悬停）：由 16ms 定时器逐拍逼近目标值，
        // 渲染时按它算行底透明度。长度按「卡片最多能放下的行数」给足余量，越界一律当 0。
        private readonly float[] _displayHoverAnim = new float[24];

        private bool _displayHoverTimerOn = false;

        /// <summary>
        /// 「显示内容」列表的唯一布局真源：可视行数 / 最大首行。
        /// 绘制（RenderTabDisplay）、悬停命中（OnMouseMove）、滚轮
        /// （WM_MOUSEWHEEL）三处共用 —— 以前这类算式在各处各写一份，
        /// 卡片高度或行高一改就会出现「滚不动 / 滚过头」。
        /// </summary>
        private void GetDisplayListLayout(out int visibleRows, out int maxFirstRow)
        {
            // 可视行数直接取 DISPLAY_MAX_ROWS（卡片高度就是按它反推的，别再自己算一遍 ——
            // 以前用 (卡片高 - 首行偏移 - 20) / 行高 反推，改了常量容易和卡片高度不同步）
            int maxRows = Math.Max(1, DISPLAY_MAX_ROWS);
            int total = PluginManager.Instance.DisplayItems.Count;
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        /// <summary>
        /// 显示设置页整页可滚的最大偏移：内容总高减窗口高，不足一屏返回 0。
        /// 渲染偏移、滚轮上限、命中坐标换算三处共用这一个真源。
        /// </summary>
        private static float GetDisplayPageMaxScroll()
            => Math.Max(0f, TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 20f - HEIGHT);

        /// <summary>整页滚动条的轨道（相对窗口顶部）——渲染与命中必须同源。</summary>
        private static void GetPageScrollbarLayout(out float trackTop, out float trackH)
        {
            trackTop = TITLE_BAR_HEIGHT + 6f;
            trackH = HEIGHT - TITLE_BAR_HEIGHT - 12f;
        }

        /// <summary>「显示内容」列表滚动条的轨道（相对窗口顶部，已含整页滚动偏移）——渲染与命中必须同源。</summary>
        private void GetListScrollbarLayout(out float trackTop, out float trackH)
        {
            GetDisplayListLayout(out int visibleRows, out _);
            float contentCardY = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y - _displayPageScroll;
            trackTop = contentCardY + DISPLAY_FIRST_ROW_Y - 4f;
            trackH = Math.Max(0f, visibleRows * DISPLAY_ROW_H - 4f);
        }

        /// <summary>当前光标位置换算成窗口客户区坐标（DIP，已除 DPI 缩放）；取不到返回 false。</summary>
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

        /// <summary>显示设置页整页滚动的纵向偏移（像素）：内容高于窗口时才可滚。</summary>
        private float _displayPageScroll;

        /// <summary>
        /// 滚轮先滚哪一层：false = 整页，true = 「显示内容」列表。
        /// 不由「点击滚动条」决定 —— 光标在「显示内容」卡片里就先滚列表、在卡片外就滚整页
        /// （每次 WM_MOUSEMOVE 按光标位置刷新，见 OnMouseMove 的 tab 1 段）。
        /// 列表滚到边界后，继续朝同方向滚满 DISPLAY_WHEEL_CARRY_STEPS 格才接力给整页
        /// （见 _displayWheelCarry 的说明）；不是一碰边界就传出去。
        /// </summary>
        private bool _wheelPriorityList;

        /// <summary>两条滚动条的悬停态（点击它们用于切换滚轮优先级）。</summary>
        private bool _pageScrollbarHovered;

        private bool _listScrollbarHovered;

        /// <summary>「显示模式」两个选项（0 = 待机模式 / 1 = 普通模式）的悬停下标（-1 = 无）。</summary>
        private int _hoveredDisplayModeIndex = -1;

        /// <summary>「待机模式」三个场景选项的悬停下标（-1 = 无）。</summary>
        private int _hoveredStandbySceneIndex = -1;

        /// <summary>「双击空白切换待机模式」开关的悬停态。</summary>
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
        // 「恢复默认」用的出厂值，顺序 = [待机宽, ~~待机高~~(已废弃), 媒体宽, 全局折叠态高, 通知宽, 通知高, DPI, 底部圆角]。
        // 数组按下标取值，废弃项也不能删，只能留位（29f 已不再被任何行引用）。
        // 这三个地方必须同步改，否则「恢复默认」和首次安装会给出不同的值：
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

        /// <summary>
        /// 打开设置窗口并直达指定页签（岛内右键按区域调用：媒体控制器 → 2 媒体设置、时间/硬件 → 1 显示设置）。
        /// 窗口还没创建过就先创建（构造里会显示），再落地页签；已创建则切页签后走 Toggle 的显示流程。
        /// </summary>
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

        /// <summary>切换左侧页签：与点击页签完全同一套动作（关掉浮层下拉 + 重绘）。</summary>
        private void SelectTab(int tab)
        {
            if (tab < 0 || tab > 6 || _selectedTab == tab) return;
            _selectedTab = tab;
            CloseAllDropdowns();
            Render();
        }

        private ConsoleWindow()
        {
            // 先挂到静态实例上：CreateWindowEx 期间系统可能立刻发 WM_PAINT/WM_CREATE，
            // 如果此时 StaticWndProc 还看不到实例，初次打开就只会看到“空的模糊底板”。
            _instance = this;
            _isAutoStartEnabled = NotchWindow.IsAutoStartEnabled();
            // 岛体双击空白切换待机态时刷新本窗口的「显示模式」卡片（见 OnStandbyActiveChanged）。
            // 生命周期跟窗口走：WM_DESTROY 里退订。
            Renderer.StandbyActiveChanged += OnStandbyActiveChanged;
            _customValues[0] = Renderer.STANDBY_WIDTH;
            // index 1「垂直高度」已随「全局折叠态高度」合并删除：height 现在统一是 index 3
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

            // 提示音：列表与选中值已由 Program.LoadSettings（RefreshBuiltins → Restore）恢复过，
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
                    // 留住引用而不是只取句柄：这个 HICON 要随窗口类活到进程结束。
                    _appSysIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (_appSysIcon != null) appIconHandle = _appSysIcon.Handle;

                    // 使用 SkiaSharp 直接解码 ICO，绕过 System.Drawing 的低质缩放
                    // SKBitmap.Decode 对 ICO 会自动选取容器中最大/最匹配的帧，且支持 256px PNG 压缩帧
                    // 磁盘优先、exe 内嵌兜底（单文件发布时这个 ico 可能不在磁盘上）
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
            // 1) 背景窗：普通 DWM HWND，只负责 Acrylic / Mica 材质；
            // 2) 内容窗：继续使用 layered + UpdateLayeredWindow，负责 Skia 前景 UI。
            // 这样既能拿到真实背景材质，又能保留前景的 per-pixel alpha，不会再把历史帧叠进客户区造成残影。
            // 背景窗标题必须留空：它用 DwmExtendFrameIntoClientArea 把整个客户区做成了玻璃，
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
            // 先定明暗外观（前景 / 叠加层的基准色），因为 TryEnableBackdropMaterial 要按它决定
            // DWMWA_USE_IMMERSIVE_DARK_MODE 与亚克力 tint；底色最后交给 ApplyBackdropPalette 收口。
            ApplyAppearance();
            TryEnableBackdropMaterial();
            ApplyBackdropPalette();

            // 内容窗刻意用 WS_EX_APPWINDOW 而不是 WS_EX_TOOLWINDOW：
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

            // 插件中心支持把 DLL 直接拖进来导入（见 ConsoleWindow.PluginDrop.cs）
            SetupPluginDropTarget();

            for (int i = 0; i < 8; i++)
            {
                UpdateValueString(i);
            }

            // 显示器列表的枚举（Screen.AllScreens）走后台线程算，避免开窗时卡一下；
            // 但更新完必须回到 UI 线程再渲染 —— 这不是可有可无的讲究：
            //   · UpdateLayeredWindow / Skia canvas 都应当由持有窗口的线程驱动；
            //   · 原来那句 `_instance.Render()` 直接写在 Task.Run 的 lambda 里，
            //     那个 lambda 就跑在线程池线程上，于是它和构造函数末尾的 Render() 并发执行，
            //     两个线程同时进 SKCanvas（非线程安全）→ native 侧访问冲突、进程闪退。
            // 现在把「算数据」留在后台，「画一帧」用 PostMessage 请 UI 线程做。
            System.Threading.Tasks.Task.Run(() => {
                var screens = Screen.AllScreens;
                string[] opts = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                    opts[i] = screens[i].Primary ? $"显示器 {i + 1} (主)" : $"显示器 {i + 1}";
                _monitorOptions = opts;
                if (Renderer.TargetMonitorIndex >= screens.Length) Renderer.TargetMonitorIndex = 0;

                // 回到 UI 线程重绘：窗口还在就投一条自定义消息，由 WndProc 在主线程里 Render。
                var inst = _instance;
                if (inst != null && inst._hwnd != IntPtr.Zero)
                    Win32.PostMessage(inst._hwnd, WM_ASYNC_RERENDER, IntPtr.Zero, IntPtr.Zero);
            });

            Render();
        }

        // 后台任务完成后请 UI 线程重绘的自定义消息（避免跨线程直接碰渲染缓冲）。
        // 必须用 const：下面 switch 里要拿它做 case 标签（case 只接受编译期常量）。
        // 0x8000 之后是 WM_APP 起点的自定义区间，不会撞系统消息；偏移取 0x52 避开托盘菜单的 0x8101。
        private const int WM_ASYNC_RERENDER = 0x8000 + 0x52;

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instance != null)
            {
                bool initializingContent = _instance._hwnd == IntPtr.Zero;
                // 重建材质窗期间（_backdropRebuilding）旧材质窗的句柄已被摘掉，但它的销毁消息
                // 还会同步回来 —— 这里一并发给 InstanceWndProc，由它按「非内容窗」处理（见那里的说明）。
                bool rebuildingBackdrop = _instance._backdropRebuilding && hwnd != _instance._hwnd;
                if (initializingContent || rebuildingBackdrop
                    || hwnd == _instance._hwnd || hwnd == _instance._backdropHwnd)
                    return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 窗口归属判定：只有内容窗才会进入下面那套消息逻辑，材质窗与「正在被重建掉的旧材质窗」
            // 一律走材质窗分支。
            //
            // 为什么不能只判 `hwnd == _backdropHwnd`：RebuildBackdropWindow 是先 `_backdropHwnd = Zero`
            // 再 `DestroyWindow(old)`（那是有意为之 —— 销毁期间回来的消息不该再被当成材质窗），
            // 但 DestroyWindow 会同步投递 WM_DESTROY / WM_NCDESTROY。这几条消息到达时
            // `_backdropHwnd` 已经是 0，若只按句柄比对就会落进「内容窗」分支，把旧材质窗的销毁
            // 当成内容窗自己在销毁：误摘拖放目标、释放内容窗还在用的渲染缓冲、把 _instance 置空，
            // 之后重建流程继续用这个实例、下一帧又去访问已释放的缓冲 —— 直接访问冲突（0xc0000005）。
            //
            // 所以判据取两个句柄的并集，且重建期间只认内容窗：
            // 凡 `hwnd != _hwnd` 的消息，只要处于重建流程中，就不是内容窗的消息。
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
                        // Win10 上真正起作用的是定时器里那次「重建材质窗」，别把这里删掉。
                        Win32.SetTimer(hwnd, BACKDROP_REFRESH_TIMER_ID, 150, IntPtr.Zero);
                    }
                    break;

                case Win32.WM_TIMER:
                    if (wParam == BACKDROP_REFRESH_TIMER_ID)
                    {
                        Win32.KillTimer(hwnd, BACKDROP_REFRESH_TIMER_ID);
                        // 激活流程彻底走完之后再补：Win11 重贴一次 accent；Win10 的 accent 重贴无效，
                        // 这里会走「重建材质窗」那条路（见 RepairBackdropAfterActivate）。
                        RepairBackdropAfterActivate();
                        return IntPtr.Zero;
                    }
                    if (wParam == DISPLAY_HOVER_TIMER_ID)
                    {
                        // 行悬停动画：逐拍把每行进度推向目标值；全部到位就自己停表，
                        //    所以「没有动画在跑」时不会有任何空转的定时器。
                        if (!TickDisplayHoverAnim()) StopDisplayHoverAnim(hwnd);
                        return IntPtr.Zero;
                    }
                    break;

                // 系统「应用模式」（浅色 / 深色）切换时系统会广播 WM_SETTINGCHANGE。
                // 只有明暗真的变了才重刷：亚克力 tint / 材质窗深色标题栏 / 全套底色都要跟着换。
                // （WM_SETTINGCHANGE 也用于很多其它设置，白刷一遍整帧没必要，所以先比对再动。）
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
                        (int)((short)(lParam.ToInt32() & 0xFFFF) / _dpiScale),
                        (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale),
                        (wParam.ToInt32() & 0x0001) != 0);
                    break;

                case Win32.WM_LBUTTONDOWN:
                    OnLeftButtonDown(hwnd, (int)((short)((lParam.ToInt32() >> 16) & 0xFFFF) / _dpiScale));
                    break;

                // 滚轮：服务于两张条目数不封顶的长列表 —— 提示音下拉浮层、以及
                //    「显示设置 → 显示内容」（插件一多就会超出卡片高度）。
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
                                // 滚轮不产生 WM_MOUSEMOVE：光标下的行号变了、hover 却还停在旧项上，
                                // 紧接着点下去就会选错音源。这里按当前光标位置补一次命中。
                                SyncHoverFromCursor();
                                Render();
                            }
                        }
                        return IntPtr.Zero; // 吞掉，别让滚轮穿透到下层
                    }

                    // 显示设置页滚轮分两层：整页平移与「显示内容」列表内滚动。
                    // 光标在「显示内容」卡片里 → 先滚列表；列表顶到边界后继续朝同一方向滚，
                    //   累计满 DISPLAY_WHEEL_CARRY_STEPS 格就交给整页（带阈值的接力）。
                    // 光标在卡片外 → 只滚整页。
                    // 判据来自 _wheelPriorityList（每次 WM_MOUSEMOVE 刷新）。
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
                                // 列表已经顶到边界：朝同方向继续滚才累计。反向滚动一律清零
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
                            // 卡片外（或列表本来就不需要滚）：只动整页。接力累计清零。
                            _displayWheelCarry = 0;
                            moved = ScrollDisplayPage(pageMax, px);
                        }

                        if (moved)
                        {
                            // 滚动后光标下的行号与控件位置都变了，必须重算悬停，
                            // 否则紧接着的点击会拿旧下标命中错误的条目。
                            SyncHoverFromCursor();
                            Render();
                        }
                        return IntPtr.Zero;
                    }
                    break;

                case Win32.WM_PAINT:
                    return IntPtr.Zero;

                // 后台任务（显示器枚举等）完成后请求的一次重绘 —— 在这里（UI 线程）执行，
                // 而不是在投递它的线程池线程里直接 Render（见构造函数里 Task.Run 的说明）。
                case WM_ASYNC_RERENDER:
                    Render();
                    return IntPtr.Zero;

                case Win32.WM_DESTROY:
                    // 拖放目标必须在下层窗口销毁前摘掉，否则 OLE 还捏着一个指向已死窗口的接口。
                    RevokePluginDropTarget();
                    if (_backdropHwnd != IntPtr.Zero)
                    {
                        IntPtr backdrop = _backdropHwnd;
                        _backdropHwnd = IntPtr.Zero;
                        Win32.DestroyWindow(backdrop);
                    }
                    // 常驻渲染缓冲（memDC + DIB + SKSurface）不归 GC 管，必须在这里显式释放。
                    // 放在 _instance = null 之前：之后就没入口能拿到这份缓冲了。
                    DisposeRenderBuffer();
                    // 定时器本身随窗口一起消失，只是把这个标志归位：
                    // 否则万一在动画途中销毁窗口，标志会一直停在 true，下次开表会被自己挡掉。
                    _displayHoverTimerOn = false;
                    // 静态事件必须跟着窗口退订：不退的话窗口关掉后 _instance 虽为 null，
                    // 但订阅列表里还挂着这个方法，下次打开会重复订阅（静态事件是进程级的）。
                    Renderer.StandbyActiveChanged -= OnStandbyActiveChanged;
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

        private void Render()
        {
            // 并发渲染会让 SkiaSharp native 侧踩空（见 _renderLock 的说明）。
            // 锁包住整个「画 + 提交」过程：中间任何一步被另一个线程插进来都是坏状态。
            lock (_renderLock)
            {
                RenderCore();
            }
        }

        private unsafe void RenderCore()
        {
            // 常驻缓冲按窗口尺寸建一次（见字段声明处的说明）。首次渲染时 _scaledWidth/Height
            // 已在构造函数里由 DPI 算好，所以这里第一次进来就会建出来。
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
            // 复用进程级静态裁剪路径（尺寸只由常量 WIDTH/HEIGHT 决定，见 ConsoleWindow.Paint.cs）
            canvas.ClipPath(WindowClipPath, SKClipOperation.Intersect, true);

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

            // 把常驻 surface 的像素拷进常驻 DIB，再把 DIB 提交给分层窗口。
            // Flush 不能省：Skia 的绘制是延迟光栅化的，这里不 Flush 就读 pBits 会拿到半成品。
            canvas.Flush();
            UpdateLayeredContentWindow();
        }

        /// <summary>
        /// 建常驻渲染缓冲（DIB + 兼容 DC + 绑在 pBits 上的 SKSurface）。失败返回 false。
        /// 顺序：先取 screen DC → 建 memDC → 建 DIB → 选入 memDC → 拿 pBits → 最后建 surface 绑上去。
        /// </summary>
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
                        // 负高度 = 自上而下的 DIB，与 Skia 的像素行序一致（省掉一次翻转）
                        biHeight = -_scaledHeight,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                _hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
                if (_hBitmap == IntPtr.Zero || _pBits == IntPtr.Zero)
                {
                    // 建了一半：把已建的对象逐个回滚，别留给下一次重试重复创建。
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
                    // DIB 有了但 surface 建不出来（内存不足）：同样整体回滚。
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

        /// <summary>
        /// 释放常驻渲染缓冲。顺序不能改（见字段声明处）：
        /// 先把旧位图选回 DC 解锁，再删 hBitmap（memDC 正选着它时删不掉），然后 surface、最后 memDC。
        /// </summary>
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

        /// <summary>
        /// 把常驻 DIB 提交给分层窗口。缓冲已常驻，所以这里没有任何 Create / Delete ——
        /// 只剩一次 UpdateLayeredWindow。像素早已在 Render 里由 Skia 直接画进 pBits。
        /// </summary>
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

        /// <summary>
        /// 待机态在别处（岛体双击空白 / 频谱）被切换后，把设置窗口的「显示模式」卡片刷新过来。
        /// 设置窗口不参与那层交互，靠 Renderer.StandbyActiveChanged 事件回调到这里。
        /// 只在「显示模式」那张卡真的可见时重绘，避免开在别的页签也白刷一帧。
        /// </summary>
        private static void OnStandbyActiveChanged()
        {
            if (_instance == null || _instance._hwnd == IntPtr.Zero) return;
            if (_instance._selectedTab != 1) return;
            _instance.Render();
        }
    }
}
