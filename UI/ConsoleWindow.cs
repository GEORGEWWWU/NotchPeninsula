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
        // 📐 通用设置页卡片顺序（2026-09-22 提示音并入通知卡之后）：
        //    开机自启 12 | 窗口置顶 84 | 系统消息通知卡 156..390（三行）| 剪贴板链接检测 400 | 切换灵动岛字体 474
        //
        //  🔔 系统消息通知卡 = **一张三行卡 + 一行提示音设置**，把通知本体与它的两个附属设置放在一起：
        //     行1「系统消息通知」总开关（开关热区 +176..+196）    ← 主体
        //     行2「消息通知内容」下拉（+230..+262）              ← 附属（管内容）
        //     行3「消息提示音」开关（开关热区 +300..+320）        ← 附属（管声音）
        //     行4「提示音」下拉 + 音量下拉 + [试听][重置]（+354..+386）← 行3 的设置行，无开关
        //     ⚠️ 提示音**是通知的附属设置**，必须和通知在同一张卡里 —— 拆成两张独立卡会让层级关系丢失。
        //     ⚠️ 卡片下沿必须贴合内容（现距内容底 374 留 30px），别撑高。
        //     ⚠️ 行 4 是全页唯一「4 控件并排」的行，控件加间隙正好占满整个内容区；
        //        因此左侧标签须单独预留空间（SOUND_CTRL_X 由标签宽度派生），且该行不放描述文字。
        //     ⚠️ 所有控件右边界一律 `WIDTH - 36`（卡片内右侧留白），横向绝不铺满整卡。
        //     ⚠️ 改这里的数值时必须同步改 OnMouseMove 的 tab 0 段与 RenderDropdowns 的浮层锚点。

        /// <summary>卡片内右侧内边距：所有右对齐控件的右边界都锚到这里。</summary>
        private const float CARD_PAD_RIGHT = 36f;

        // ---- ① 系统消息通知卡（三行 + 一行附属设置）----
        //
        // 布局节奏：**行距恒为 62px**，与全页所有单行卡同一节奏。
        //   · 行 1「系统消息通知」行首 156
        //   · 行 2「消息通知内容」行首 218 = 行 1 + 62
        //   · 行 3「消息提示音」  行首 280 = 行 2 + 62
        //   · 行 4「提示音设置」  行首 342 = 行 3 + 62
        //   · 分隔线放在每一对行之间：+222、+284
        //   · 卡片 156..404（248 = 4 × 62）
        //
        // ⚠️ 历史坑：卡片曾被撑到 320 而内容只用到 292 —— 多出的 28px 先表现为「分隔线到行 2
        //    之间一大块空白」，把分隔线往下挪之后空白又跑到行 1 下面。
        //    **空白总量不变，挪分割线是治不好的** —— 唯一正解是让卡片贴合内容。
        //    判据：`卡片下沿 - 内容底` 必须落在 [8, 20]。

        /// <summary>「系统消息通知」总开关（行 1）行首偏移。</summary>
        private const float TOAST_ROW1_Y = 156f;

        /// <summary>「消息通知内容」行 2 行首偏移（= 行 1 行首 + 行距 62）。</summary>
        private const float TOAST_ROW2_Y = TOAST_ROW1_Y + 62f;

        /// <summary>行 1 与行 2 之间的分隔线（= 行 2 行首 + 4，落在行 1 内容底 202 与行 2 控件顶 230 之间）。</summary>
        private const float TOAST_SEP_Y = TOAST_ROW2_Y + 4f;

        // ============================================================
        //  ★ 行内纵向锚点（全页唯一真源，2026-09-22 第五次返工后定稿）
        //
        //  目标：**「左侧文字块」与「右侧控件」同心对齐** —— 文字块的光学中心
        //        和右排控件（开关轨道 / 下拉框 / 按钮）的中心落在同一条水平线上。
        //
        //  ── 返工史（前四轮都错在「拿什么当对齐参照」）────────────────
        //    第 1 轮：四行各写各的基线偏移 —— +26 / +33 / +26 / +21。
        //    第 2 轮：改成「标签基线 = 框顶 + h/2 + 5」，即跟着**框内文字**走。
        //             ❌ 错：框内文字在框里本身偏下，把行外标签也拖下去了。
        //    第 3 轮：改成「所有行统一基线 = 行首 + 26」。
        //             ❌ 错：26 是**两行行**（标题+副标题）的标题基线，
        //                单行行（行 4「提示音」）拿它当基线就飘到下拉框上面去了 —— 用户「现在太靠上了」。
        //    第 4 轮：改成「单行墨迹中线 == 控件中心」，偏移 = 30 + 5.5 = 35.5。
        //             ❌ 错：35.5 只对**单行行**成立。两行行照抄之后，整个文字块
        //                （标题墨迹顶 → 副标题墨迹底）比控件中心低了 10px —— 用户
        //                「开机自启、窗口置顶、系统消息通知、剪贴板链接检测的文字全部向下偏移」。
        //    第 5 轮（本版）：**按「本行有几行文字」分别反解**，两个偏移都让
        //                「文字块的光学中心」落在同一个锚点上（见下面两个常量）。
        //
        //  ── 为什么锚点能同时适配「20px 轨道」和「32px 框」────────────────
        //    因为 ROW_DROPDOWN_TOP 已经取 14，使**框中心**（14+16）恰好等于
        //    **开关轨道中心**（20+10），两者都 = 行首 + 30 = ROW_ANCHOR_Y。
        //    所以「控件中心」这个参照在两类行里是同一个数，文字只需要按行数选偏移。
        //
        //  ⚠️ 直接把 `ROW_ANCHOR_Y`(30) 当基线是错的 —— 基线与墨迹中线差 5.5px。
        //  ⚠️ 单行行与两行行**必须用不同的基线常量**，这是第 3/4 轮反复翻车的根因。
        //  ⚠️ 改字号 / 改字体族必须重新标定 TEXT_INK_MID_OFFSET 与 TEXT_INK_ASCENT。
        // ============================================================

        /// <summary>行内纵向锚点：每行「右侧控件中心 / 左侧文字块光学中心」的相对偏移。</summary>
        private const float ROW_ANCHOR_Y = 30f;

        /// <summary>
        /// 13px 字号的墨迹几何（实测标定，Microsoft YaHei UI）：
        /// 绘制基线 y 之上 11px 到基线处是墨迹，即 `top = 基线-11`、`bot = 基线`、`中线 = 基线-5.5`。
        /// ⚠️ 换字号 / 换字体族必须重新标定这两个数。
        /// </summary>
        private const float TEXT_INK_MID_OFFSET = 5.5f;

        /// <summary>墨迹在基线上方的高度（13px YaHei UI 实测 11px）。</summary>
        private const float TEXT_INK_ASCENT = 11f;

        /// <summary>行内「标题 → 副标题」的行距（两行文字之间的基线差）。</summary>
        private const float ROW_SUB_OFFSET = 20f;

        /// <summary>
        /// **单行行**（只有标题、没有副标题，如通知卡行 4 的「提示音」）的标题基线偏移。
        ///
        /// 反解：墨迹中线 = 基线 - 5.5，令它 = 行首 + ROW_ANCHOR_Y(30)
        /// → 基线 = 行首 + 30 + 5.5 = **行首 + 35.5**。
        /// </summary>
        private const float ROW_TEXT_BASELINE_SINGLE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET;   // = 35.5

        /// <summary>
        /// **两行行**（标题 + 副标题，如「开机自启」「系统消息通知」）的**标题**基线偏移。
        ///
        /// 反解：文字块的墨迹范围 = [基线 - 11, 基线 + ROW_SUB_OFFSET]，
        ///       块中线 = 基线 + (ROW_SUB_OFFSET - 11) / 2 = 基线 + 4.5，
        ///       令块中线 = 行首 + ROW_ANCHOR_Y(30)
        /// → 标题基线 = 行首 + 30 + 5.5 - 20 / 2 = **行首 + 25.5**，副标题 = 行首 + 45.5。
        ///
        /// ⚠️ 曾经把它和单行行合并成 35.5：那是拿「标题那一行的墨迹中线」去对控件中心，
        ///    整个两行文字块因此整体下移 10px（用户 2026-09-22 点名的「文字全部向下偏移」）。
        /// ⚠️ 也别写成 `ROW_ANCHOR_Y - 4`（= 26）：那是把「基线」当「视觉中心」，
        ///    虽然只差 0.5px 看着没事，但语义是错的，下次改字号就会崩。
        /// </summary>
        private const float ROW_TEXT_BASELINE = ROW_ANCHOR_Y + TEXT_INK_MID_OFFSET - ROW_SUB_OFFSET / 2f;   // = 25.5

        /// <summary>
        /// 下拉框（h=32）的框顶偏移：要让框中心落在 `行首 + ROW_ANCHOR_Y`，
        /// 即 `框顶 + 16 = 30` → **框顶 = 行首 + 14**（与开关轨道同中心）。
        /// ⚠️ 不是 +12（旧值，中心 28，比开关低 2px）也不是 0（旧值，中心 16，比开关高 14px）。
        /// </summary>
        private const float ROW_DROPDOWN_TOP = 14f;

        /// <summary>
        /// 下拉行「框内文字」相对框顶的基线偏移 = `DrawDropdownBox` 的 `h/2 + 5`。
        /// ⚠️ 这是**框自己内部**的排版参数，只用于把框内文字摆正在框里，
        ///     **绝不可拿它当「框外标签的对齐口径」**（2026-09-22 就是这么治错的）。
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
        /// ⚠️ 行 4 只有「提示音」三个字、**没有副标题**，所以走**单行行**的口径：
        ///    墨迹中线对齐行内锚点（= 行 4 下拉框 / 按钮中心，实测均为 404.0）。
        ///    这里**不能**用两行行的 `ROW_TEXT_BASELINE`(25.5)，否则文字会飘到框上方。
        ///    两个常量的差别就是「这一行有几行文字」，见文件头部锚点说明。
        /// 行 4 只有左侧一个短标签、无描述（空间被 4 个控件占满，放不下第二行文字）。
        /// </summary>
        private const float SOUND_ROW_TITLE_Y = SOUND_ROW_Y + ROW_TEXT_BASELINE_SINGLE;

        private const float SOUND_ROW_H = 32f;

        /// <summary>
        /// 行 4 三个下拉框 / 两个按钮共用的**框顶**偏移 = 行首 + ROW_DROPDOWN_TOP（= 行 4 行首 + 14）。
        ///
        /// ⚠️ 不要再把框顶直接写成 `SOUND_ROW_Y`（行首本身）：那会让框中心落在行首 + 16，
        ///    比同一行的标签墨迹中心（行首 + 30）高 14px，视觉上就是「提示音三个字和右边按钮不齐」。
        ///    2026-09-22 用户点名的「子卡片顶部再加 5px padding」本质就是要把这一段往下压。
        /// ✅ 所有「框/按钮的顶」都走本常量，「行首」只用来说明行从哪儿起（命中判定、浮层锚点用行首）。
        /// </summary>
        private const float SOUND_BOX_Y = SOUND_ROW_Y + ROW_DROPDOWN_TOP;

        /// <summary>系统消息通知卡底部偏移。
        ///
        /// ⚠️ **不能用「行数 × 62」硬套**：62 是「行首到行首」的行距，不是「行首到卡底」的间距。
        ///    卡片底 = 行 4 控件底 + 收尾留白。行 4 控件占 +14..+46（框顶 14 + 高 32），
        ///    所以底 = 342 + 46 + 16 = 404。
        ///    收尾留白取 16px，与单行卡「开关轨底 40 → 卡底 62」的 22px 观感相当
        ///    （控件比文字矮，留白可以略小）。
        ///    ⚠️ 曾经写成 404（硬套 4×62 = 248）时是巧合相等，后来框顶上移才暴露不对；
        ///       现在这一版的 404 是**从 SOUND_BOX_Y 推出来的**，不是硬套。
        ///    判据：`TOAST_CARD_BOTTOM - (SOUND_BOX_Y + SOUND_ROW_H)` 应落在 [12, 20]。</summary>
        private const float TOAST_CARD_BOTTOM = SOUND_BOX_Y + SOUND_ROW_H + 16f;

        /// <summary>
        /// 提示音行「从右往左」排版时用的横向间隙。**整行必须刚好塞进卡片内容区**
        /// （216 .. WIDTH-36，共 348px），所以每个宽度都是按实测文本宽度抠出来的：
        /// 最长选项「手表提示（watchOS）」132.9px + 左右内边距与箭头 ≈ 156。
        /// ⚠️ 改任一宽度都要重算总和，加起来超过 348 就会像上一版那样怼出卡片左边界。
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
        /// ⚠️ 这一行是**全页唯一 4 个控件并排**的行（下拉 + 音量 + 试听 + 重置，共 340px），
        ///    而内容区只有 348px（216..564）。所以它**不能**像其它行那样从 216 起排 ——
        ///    那样会把左侧标签区挤成负数（216 - 8 = 208 &lt; 216），文字直接叠到下拉框上。
        ///    这里给标签留出实测宽度（「提示音」3 字 13.5px ≈ 39px）+ 8px 间隙。
        ///    ⚠️ 改这里要同步 `SOUND_CTRL_W`，并确认 `SOUND_LABEL_X + 标签宽 + GAP == SOUND_CTRL_X`。
        /// </summary>
        private const float SOUND_CTRL_X = SOUND_LABEL_X + 40f + SOUND_LABEL_GAP;

        /// <summary>「提示音」下拉宽度：右边界正好贴住音量下拉。</summary>
        private const float SOUND_CTRL_W = SOUND_VOL_X - SOUND_ROW_GAP - SOUND_CTRL_X;

        // ---- ② 剪贴板链接检测（通知卡之后，行首 = 通知卡底 + 标准卡片间隙 10）----

        /// <summary>剪贴板链接检测卡行首 = 通知卡底 + 10。
        /// ⚠️ 必须由 TOAST_CARD_BOTTOM 派生：通知卡高度一改（本页改过三次），这里跟着自动走。
        ///    2026-09-22 就是因为剪贴板卡写死 400，而通知卡底从 390 长到 404，两卡直接叠在一起。</summary>
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

        /// <summary>下拉浮层的行高。绘制、命中、滚轮三处必须共用这一个数。</summary>
        private const float DROPDOWN_ROW_H = 26f;

        /// <summary>
        /// 提示音下拉浮层的**唯一布局真源**：把「浮层顶 / 可视行数 / 最大首行」算成一套，
        /// 供绘制（RenderDropdownList）、悬停命中（OnMouseMove）、滚轮（WM_MOUSEWHEEL）三处共用。
        ///
        /// ⚠️ 以前这三处各写一份，而且滚轮那份把浮层顶写成了 `SOUND_ROW_Y + SOUND_ROW_H + 2`
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
        /// 展开提示音下拉时把滚动位置定到「当前选中项可见」处 —— **只在这一刻做一次**。
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
        /// 紧接着的点击就会选错音源（用户说的「断触」）。滚动完必须补这一下。
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
                //    每格 120 → 滚动 3 行；可滚范围与绘制 / 命中共用 GetToastSoundMenuLayout。
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
