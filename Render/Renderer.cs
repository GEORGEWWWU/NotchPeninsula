using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // 1. 布局核心参数 (改为无锁动态变量)
        private static volatile float _standbyWidth = 125f;

        private static volatile float _mediaWidth = 250f;

        private static volatile float _mediaHeight = 35f;

        private static volatile float _toastWidth = 260f;

        private static volatile float _toastHeight = 55f;

        private static volatile float _globalDpi = 1.0f;

        private static volatile float _notchBottomRadius = 12f;

        public static float STANDBY_WIDTH { get => _standbyWidth; set => _standbyWidth = value; }

        public static float MEDIA_WIDTH { get => _mediaWidth; set => _mediaWidth = value; }

        /// <summary>
        /// 全局折叠态高度：待机态、媒体折叠态、剪贴板链接面板共用同一个值。
        /// 原先「待机高度」(BASE_HEIGHT) 与它分开，2026-09-27 合并到本属性，
        /// 存储沿用注册表 Custom_MediaH（见 Program.LoadSettings 的兼容回落）。
        /// </summary>
        public static float MEDIA_HEIGHT { get => _mediaHeight; set => _mediaHeight = value; }

        public static float TOAST_WIDTH { get => _toastWidth; set => _toastWidth = value; }

        public static float TOAST_HEIGHT { get => _toastHeight; set => _toastHeight = value; }
        // 消息通知内容：false=缩略(默认，仅 icon+发送者+主体)，true=完整(icon+应用名+发送者+主体+右上角“现在”)

        public static bool IsToastFullMode = false;
        // 紧凑模式：尺寸与缩略一致，但右侧靠边显示双行信息（右上“现在”、右下应用名），左侧文本过长时用遮罩过渡

        public static bool IsToastCompactMode = false;
        // 紧凑模式下右侧双行信息预留宽度

        public static readonly float COMPACT_RIGHT_WIDTH = 90f;
        // 完整模式下的消息通知最小尺寸（默认缩略为 260x55，完整需更长更高以容纳应用名）

        public static readonly float FULL_TOAST_MIN_WIDTH = 300f;

        public static readonly float FULL_TOAST_MIN_HEIGHT = 72f;

        public static float GLOBAL_DPI { get => _globalDpi; set => _globalDpi = value; }

        public static float NOTCH_BOTTOM_RADIUS { get => _notchBottomRadius; set => _notchBottomRadius = value; }

        private static int _themeMode = 0; // 0=黑, 1=白, 2=跟随系统

        /// <summary>
        /// 主题模式（0=黑 / 1=白 / 2=跟随系统）。
        /// 
        /// 必须走属性而不是自动属性：主题一改，「跟随系统」下真实生效的明暗可能翻面，
        /// 缓存（<see cref="SystemIsLightTheme"/>）必须当场作废，否则设置窗口 / 岛体
        /// 会拿旧值画一整段时间，直到下一次 WM_SETTINGCHANGE 才纠正。
        /// 写入点见 UI/ConsoleWindow.Click.cs（主题选项）与 Core/Program.cs（启动读配置）。
        /// </summary>
        public static int ThemeMode
        {
            get => _themeMode;
            set
            {
                if (_themeMode == value) return;
                _themeMode = value;
                InvalidateSystemThemeCache();
            }
        }

        // ---- 系统「应用模式」（浅色 / 深色）读取缓存 ----
        //
        // 为什么要缓存：ThemeMode == 2（跟随系统）时，主题色要用
        // HKCU\...\Themes\Personalize\AppsUseLightTheme。而这个值过去是在
        // ▶ 渲染热路径 ◀ 上现读的 —— 设置窗口「显示设置」页里每个胶囊示意图
        // （显示形态 2 个、显示模式 2 个、待机场景 4 个）各读一次，等于每帧 8 次
        // CreateKey + RegQueryValueEx + RegCloseKey。滚轮翻页时每滚一格重绘一次，
        // 就是一秒几十次无谓的内核往返。岛体那边的 Renderer.ApplyThemeColors()
        // 虽然只在主题变更时调用，但同样白读。
        //
        // 缓存失效点（只有这三种，够了）：
        //   1. 系统广播 WM_SETTINGCHANGE → ConsoleWindow 调 ApplyAppearance()；
        //   2. SystemEvents.UserPreferenceChanged → Program.OnUserPreferenceChanged；
        //   3. 用户改「主题模式」本身（setter 里）—— 因为「跟随系统」的实时值
        //      必须现读一次，从 1/0 切回 2 时不能沿用更早的缓存。
        //
        // 这是进程级静态字段：两个读写方（ConsoleWindow / Renderer.ApplyThemeColors）
        // 靠它对齐明暗。失效必须由上面三个入口集中调 InvalidateSystemThemeCache()，
        // 别在别处直接写 _systemIsLightTheme（会漏掉「已缓存」标志的复位）。
        private static bool _systemIsLightTheme;

        /// <summary>系统「应用模式」是否已读过（false 时下一次读取会真的访问注册表）。</summary>
        private static bool _systemThemeCached;

        /// <summary>
        /// 取系统「应用模式」是否为浅色。首次（或失效后）读注册表，之后走缓存。
        /// 读不到（键不存在 / 权限不足）按深色 —— 与历史外观一致。
        /// </summary>
        public static bool SystemIsLightTheme
        {
            get
            {
                if (_systemThemeCached) return _systemIsLightTheme;

                bool light = false;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    light = key?.GetValue("AppsUseLightTheme") is int val && val == 1;
                }
                catch
                {
                    // 读不到就按深色，并且**照样标成已缓存** —— 否则每个绘制调用
                    // 都会去踩一次必然失败的注册表访问（异常是慢路径，更不能进渲染循环）。
                }

                _systemIsLightTheme = light;
                _systemThemeCached = true;
                return light;
            }
        }

        /// <summary>
        /// 作废系统主题缓存。系统主题变更、或「主题模式」被改写时调用；
        /// 下一次读 <see cref="SystemIsLightTheme"/> 会重新落地到注册表。
        /// </summary>
        public static void InvalidateSystemThemeCache()
        {
            _systemThemeCached = false;
        }

        public static int NotchStyle { get; set; } = 0; // 0=经典刘海, 1=灵动岛

        public static int StandbyDisplayMode { get; set; } = 0; // 旧「待机显示内容」（0=时间日期, 1=空白, 2=硬件占用）：已被显示设置的复选框取代，只用于老配置迁移

        /// <summary>
        /// 待机模式在岛上显示什么：1 = 只显示时间，2 = 空白，3 = 折叠媒体控制。
        /// 由「显示设置 → 待机模式」选择并持久化；进入 / 退出待机由 <see cref="StandbyActive"/> 单独表示。
        /// 与旧的 <see cref="StandbyDisplayMode"/> 是两个独立概念（那个是已被复选框取代的历史键）。
        /// </summary>
        public static int StandbyScene { get; set; } = 1;

        /// <summary>
        /// 当前是否处于待机模式。进入 / 退出由「双击空白」（<see cref="StandbyToggleByDoubleClick"/> 打开时）
        /// 或设置页手动切换驱动；这是运行时状态，不持久化 —— 重启后回到默认显示。
        ///
        /// 写入会触发 <see cref="StandbyActiveChanged"/>：设置窗口打开时并不参与这层交互
        /// （双击发生在岛体上），它靠这个事件把「显示模式」卡片的高亮刷过来。
        /// 直接改字段（绕过属性）就不会通知 UI，别这么写。
        /// </summary>
        public static bool StandbyActive
        {
            get => _standbyActive;
            set
            {
                if (_standbyActive == value) return;
                _standbyActive = value;
                try { StandbyActiveChanged?.Invoke(); }
                catch (Exception ex) { Logger.Error("[Renderer] StandbyActiveChanged 事件处理异常", ex); }
            }
        }
        private static bool _standbyActive;

        /// <summary>待机态变化（进入 / 退出）时触发。岛体双击切换后，设置窗口据此重绘。</summary>
        public static event Action? StandbyActiveChanged;

        /// <summary>开关：双击岛上的「空白」处进入 / 退出待机模式（默认关闭）。</summary>
        public static bool StandbyToggleByDoubleClick { get; set; } = false;

        public static int TargetMonitorIndex { get; set; } = 0; // 目标显示器索引

        public static int BgOpacityLevel { get; set; } = 4; // 透明度档位：0=0%, 1=25%, 2=50%, 3=75%, 4=100%

        // 组合模式已常开（2026-09-25 移除总开关）：灵动岛显示什么、按什么次序，
        //    完全由「显示设置 → 显示内容」那张复选框 + 上下排序列表决定 —— 只勾一个就等于旧的
        //    「待机显示内容」，勾多个就是多模块并排。
        //    之所以还留着这个「恒为 true」的属性，是因为渲染 / 布局 / 宽度计算里到处都在问
        //    「是不是组合模式」；非组合模式那套布局已成为不可达分支，但先不动它，
        //    避免连带改动岛体几何（宽度决策 / 插件行预留）而引入回归。
        public static bool CompositeModeEnabled => true;

        public static bool CompShowDateTime { get; set; } = true;  // 显示时间日期

        public static bool CompShowHardware { get; set; } = true;  // 显示硬件占用

        public static bool CompShowMedia { get; set; } = true;     // 显示媒体控制器(含频谱)

        public static bool PassthroughModeEnabled = false; // 穿透模式总开关

        public static float PassthroughAlpha = 1.0f; // 穿透动画平滑插值

        /// <summary>
        /// 是否正有一批文件被拖着经过岛体（由 IslandDropTarget 在 DragEnter / DragLeave / Drop 维护）。
        ///
        /// 置位期间穿透模式的「悬停即淡出到 0%」必须失效：岛体一旦降到全透明，它的像素就从 OLE 的命中测试里消失，
        /// 拖放目标会在拖动途中当场丢失 —— 表现就是「文件怎么都放不进详情页」。拖放一结束（离开 / 放下）自动恢复。
        ///
        /// 写方是 UI 线程上的 OLE 回调，读方是渲染计时器线程，所以必须 volatile。
        /// </summary>
        public static volatile bool FileDragInProgress = false;

        /// <summary>
        /// 岛体垂直基准位置（逻辑像素）：0 = 贴目标显示器顶部（默认，也是当前唯一的形态）。
        ///
        /// 位置自定义的唯一真源：窗口坐标（ptDst.y）、自动隐藏策略（上移出屏 / 完全隐藏）、
        /// 以及两处屏幕坐标轮询（穿透悬停、岛外点击兜底）全部从它派生 —— 将来开放「岛体位置自定义」
        /// （无论做在宿主设置里还是给插件 API），只需要写这一个值，其余自动跟着走。
        /// </summary>
        public static float IslandBaseY { get; set; } = 0f;

        /// <summary>
        /// 「完全隐藏」不透明度：1 = 正常显示，0 = 整块不可见。
        ///
        /// 岛体基准离开顶部时，上移出屏那套会在屏幕中间留下一条 4px 岛体残影（且岛体会从屏幕中间
        /// "飞"到顶部），所以改用原地淡出到 0% 透明 —— 全透明像素会被 Windows 判定为物理穿透，
        /// 唤醒入口复用岛体正中的唤醒按钮（见 Renderer.WakeButtonX）。
        /// 与 PassthroughAlpha 是两条独立通道（后者由穿透模式独占），渲染时取二者较小值。
        /// </summary>
        public static float FullHideAlpha = 1.0f;

        // 媒体交互状态：0=直接交互，1=展开交互(默认)
        public static int MediaInteractionMode = 1;

        // ---- 折叠态媒体区的展开入口（唯一真源） ----
        // 规则（2026-10-02 定下「展开走右键」，2026-10-03 用户细化为「入口跟着跳转开关走」）：
        //   · 「媒体交互方式」（MediaInteractionMode）= 展开功能总闸。关掉它就没有展开这一说，
        //     折叠态媒体区的右键照旧直达「媒体设置」，左键只剩（直接交互模式的）悬停播放控件。
        //   · 总闸开着时，入口由「双击封面跳转应用」（MediaController.IsAppLaunchEnabled）决定 ——
        //     开启：左键被双击跳转占用（双击折叠态左半边 / 展开态封面），展开让位给右键；
        //     关闭：左键空闲，恢复左键单击展开（2026-10-02 之前的老口径），右键直达「媒体设置」。
        //
        // 命中侧（NotchWindow 的 WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_RBUTTONDOWN）与设置页文案
        //    一律从这里取，不要再各自写 `MediaInteractionMode == 1` 之类的复合判断 —— 三处口径一分叉，
        //    就会出现「手型给了却点不动」或「设置里写着左键、实际要右键」。
        /// <summary>折叠态媒体区是否用右键展开（总闸开启 + 「双击封面跳转应用」开启）。</summary>
        public static bool MediaExpandByRightClick => MediaInteractionMode == 1 && MediaController.IsAppLaunchEnabled;

        /// <summary>折叠态媒体区是否用左键单击展开（总闸开启 + 「双击封面跳转应用」关闭）。</summary>
        public static bool MediaExpandByLeftClick => MediaInteractionMode == 1 && !MediaController.IsAppLaunchEnabled;

        /// <summary>
        /// 岛体总长度上限：Toast / 剪贴板面板的自适应宽度、组合模式总宽、以及插件行的取舍都以它封顶。
        ///
        /// 2026-09-20 由 800 放开到 1920：宁愿灵动岛超长溢出屏幕，也不要被裁切。
        /// 旧的 800 是「怕挤压到右边的插件」而设的，但实际效果是长歌词被裁切，
        /// 而且插件行预算（= 本值 − 原生内容宽度）被长歌词吃光后，插件会直接整帧不显示
        /// （不是被压缩，是彻底消失），体验很差 —— 这个顾虑被证明完全没必要。
        ///
        /// 1920 是本体的上限（≈ 106 个汉字，任何真实歌词行都远达不到）。
        /// 它同时也是窗口内容区的下限来源：WINDOW_WIDTH 必须 ≥ 本值，
        /// 否则岛体超出窗口的部分会被窗口边缘裁掉（那就又变成裁切了）。
        /// 岛体允许溢出屏幕 —— 窗口比屏幕宽是合法的，透明像素照常鼠标穿透。
        /// </summary>
        public const float MAX_ISLAND_WIDTH = 1920f;

        // 2026-09-20 媒体控制器的长度完全放开，因此删掉了两个上限常量：
        //    · MEDIA_TEXT_MAX_WIDTH（默认 480 ≈ 27 个汉字）—— 非组合模式的媒体文本区上限
        //    · CompositeMediaMaxWidth（默认 460 ≈ 21 个汉字）—— 组合模式媒体模块的占宽上限
        //    这两个才是「歌词一长就被裁切」的真正元凶（它们都比 MAX_ISLAND_WIDTH 小得多，长歌词先撞到它们），
        //    而且把原生内容宽度钉死/压低后，插件行预算（= MAX_ISLAND_WIDTH − 原生宽度）被吃光，
        //    装不下的插件会整帧不显示（不是压缩，是彻底消失）。
        //    不要再以「防止挤压插件」为由把它们加回来 —— 插件该不该显示由插件行预算决定，
        //       而岛体该多长就多长（上限见 MAX_ISLAND_WIDTH）。

        // 动态计算最大边界，防止因刘海变大导致出界
        // 将透明原生窗口的基础画布拓宽，给极长歌词预留充足的物理空间，防止被系统窗口边缘裁切
        // 插件详情页展开时，底层缓冲必须容得下详情页尺寸（+80 / +45 是原有的四周留白）
        // 2026-09-20：岛体总长上限放宽到 MAX_ISLAND_WIDTH(1920) 后，窗口内容区必须跟着 ≥ 它 ——
        //    岛体是水平居中画的（islandLeft = (WINDOW_WIDTH - currentWidth) / 2），
        //    只要 currentWidth > WINDOW_WIDTH，islandLeft 就变成负数，超出窗口的那部分会被窗口边缘硬裁，
        //    等于又绕回「被裁切」。所以这里把 MAX_ISLAND_WIDTH 也纳入下限。
        //    窗口比屏幕宽是允许的（岛体可以溢出屏幕）；透明像素照常鼠标穿透，
        //    位置换算（logX / ptDst.x）都已经带上了「窗口居中于显示器」的偏移量，无需另行处理。
        public static float WINDOW_WIDTH => Math.Max(1200f,
            Math.Max(MAX_ISLAND_WIDTH,
                Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))))) + 80f;

        public static float MAX_WINDOW_HEIGHT => Math.Max(220f, Math.Max(ActiveDetailHeight, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT)) + 45f);

        public const int OUTER_R = 14;

        public const int INNER_R = 12;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f, string? clipboardUrl = null)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                // 把本帧岛体高度同步给命中侧：双击跳转要用它区分「展开面板」与「折叠内联行」
                //    （判据必须与绘制分流同源，所以宁可每帧写一次快照，也不让命中侧另写一套高度条件）。
                SetHitTestHeight(currentHeight);

                // 每帧清空插件命中区，仅当本帧实际绘制插件行时才重新填充
                // （防止 Toast / 媒体激活等不绘制插件的状态下残留上一帧的过期命中矩形）
                InvalidatePluginHitAreas();

                // 时间轴几何登记表帧首作废：本帧不画就等于命中区不存在
                _tlBarX1 = _tlBarX2 = _tlBarY = 0f;

                // 剪贴板「打开」按钮热区帧首作废：本帧不画就等于命中区不存在
                _clipboardOpenHit = default;

                // 原生模块（时间/日期、CPU/RAM、媒体）右键命中区同样帧首作废
                InvalidateNativeHitZones();

                // 岛体物理左边界（背景形状 / 裁剪范围以它为准）
                float islandLeft = (WINDOW_WIDTH - currentWidth) / 2f;
                // 岛体物理右边界（背景形状 / 裁剪范围以它为准）
                float islandRight = islandLeft + currentWidth;
                // 插件行的位置：
                //   · 组合模式：插件已并入「内容顺序表」，与原生模块一起混排（宽度计在 GetCompositeWidth 内），
                //     不单独占用预留区；
                //   · 非组合模式：同样遵守这张顺序表 —— 排在「本帧原生模块」之前的插件画在原生内容左边，
                //     之后的画在右边。原生内容的左右边界据此内收，所以插件显示与否、排在哪一边，
                //     都不会影响原生功能本身。
                //     （2026-09-20 修复：此前非组合模式无条件把整行插件贴在岛体最右侧、完全不读顺序表，
                //       导致「插件中心」的 ← / → 只在组合模式下有效。）
                // 这里必须用未缩放的预留（GetPluginRowReserve，而不是 GetScaledPluginReserve）。
                //    缩放版把预留按「当前宽度 / 目标宽度」缩小，而岛体宽度是弹簧动画过来的：
                //    媒体控制器长度一变（换歌词 / 换标题 → 目标宽度变大），缩放系数立刻掉下来，
                //    原生内容边界与插件行就会整体挪一下再挪回去 —— 表现出来就是「闪现一下又闪回来」。
                //    （用户 2026-09-20 反馈；组合模式不走这条路径，所以只有媒体控制器会出现。）
                //    改用未缩放值后，插件行与原生内容的边界只跟着岛体边缘平滑移动，不再有跳变。
                float pluginReserve = toast == null && !CompositeModeEnabled ? GetPluginRowReserve() : 0f;
                // 原生内容本帧对应的内置模块（非组合模式同一时刻最多显示一个原生模块）
                string? nativeBuiltinId = CompositeModeEnabled ? null : GetNativeBuiltinId(media.IsActive);
                int nativeOrderIndex = OrderIndexOf(nativeBuiltinId);
                // 两侧插件组：内容宽度（各组件宽度 + 组内 16px 间距）与占位宽度（再加与原生内容之间的间距）
                float leftGroupW = 0f, rightGroupW = 0f;
                float leftPluginBlock = 0f;
                float rightPluginBlock = pluginReserve;
                if (pluginReserve > 0f)
                {
                    MeasurePluginSides(nativeOrderIndex, out leftGroupW, out rightGroupW);
                    leftPluginBlock = leftGroupW > 0f ? leftGroupW + PLUGIN_GAP : 0f;
                    rightPluginBlock = rightGroupW > 0f ? rightGroupW + PLUGIN_GAP : 0f;
                    // 兜底让位：岛体宽度还在弹簧动画途中时（currentWidth < 目标宽度），
                    // 本帧可能装不下「原生内容 + 两侧插件组」（典型：通知收起后插件行重新出现，
                    // 岛体才 260 而插件行要 400）。此时插件组按比例让位，给原生内容留出 MIN_NATIVE_AREA。
                    //
                    // 判定阈值用常量 MIN_NATIVE_AREA，不能换成「本帧原生内容所需宽度」——
                    //    后者与目标宽度同一刻跳变，而 currentWidth 还停在旧目标上，
                    //    换歌词那一帧就会误判「装不下」而把原生内容区一步拉宽（又是一次跳变）。
                    //    常量下限下：稳态（currentWidth == nativeWidth + reserve）永远不触发，
                    //    换歌词（reserve 不变、只有目标宽度变大）也不触发 —— 边界只跟着岛体边缘平滑移动。
                    // 让位系数 k 随 currentWidth 连续变化（totalBlock 在预留不变时是常量），
                    // 不会引入新的跳变；收窄后的区间由绘制侧裁剪落实（见下方 ClipRect），
                    // 于是左右两组绝不会在岛体中间叠在一起，插件是随岛体长大从两侧滑入的。
                    // 只在「本帧确有原生内容」时才介入：空白待机（nativeBuiltinId == null）时
                    //    原生内容区本来就是空的，没有东西会被负宽度翻面。
                    if (nativeBuiltinId != null)
                    {
                        float totalBlock = leftPluginBlock + rightPluginBlock;
                        float maxBlock = Math.Max(0f, currentWidth - MIN_NATIVE_AREA);
                        if (totalBlock > maxBlock + 0.5f) // 0.5px 容差：稳态下不会触发，别被浮点误差误判
                        {
                            float k = maxBlock / totalBlock;
                            leftPluginBlock *= k;
                            rightPluginBlock *= k;
                        }
                    }
                }
                // 原生内容的左右边界（扣除两侧插件组）；没有插件时与岛体边界完全相同
                float left = islandLeft + leftPluginBlock;
                float right = islandRight - rightPluginBlock;
                // 媒体模块右边界（每帧刷新，供 UI 线程判定媒体按钮 / 悬停命中）：
                // 组合模式与「非组合 + 有插件预留」都要用渲染时的真实值 —— 插件被排到原生内容左边时，
                // 媒体右边界就是岛体右边界，旧的换算公式会算出偏左的错误位置。
                _compositeMediaRight = CompositeModeEnabled || pluginReserve > 0f ? right : -1f;

                // 灵动岛悬浮距离顶部的 Y 轴高度 (随过渡进度平滑变化)
                float topY = 12f * styleProgress;

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中
                canvas.Translate(0, topY);

                // 开启一个硬件级透明图层，包裹本体所有元素，杜绝任何图层/阴影残留
                // 两条通道相乘：穿透睡眠（PassthroughAlpha→0）与完全隐藏（FullHideAlpha→0）都表现为整块不可见
                _layerPaint.Color = SKColors.White.WithAlpha((byte)(255 * PassthroughAlpha * FullHideAlpha));
                canvas.SaveLayer(_layerPaint);

                _bgPath.Rewind();

                // 自动把四个圆角调到最大，动态计算插值半径
                // 限制灵动岛展开后的最大圆角为 20f，防止变成大圆球
                float islandRadius = Math.Min(currentHeight / 2f, 20f);
                float rBottom = NOTCH_BOTTOM_RADIUS * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopY = OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopX = -OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;

                // 纯数学魔法：完美正圆形的 Conic 曲线权重 (Math.Sqrt(2) / 2)
                float w = 0.70710678f;

                // 纯数学变形算法：全部使用 ConicTo 替换 QuadTo 强制生成完美圆形弧度
                _bgPath.MoveTo(islandLeft + rTopX, 0);
                _bgPath.ConicTo(islandLeft, 0, islandLeft, rTopY, w);
                _bgPath.LineTo(islandLeft, currentHeight - rBottom);
                _bgPath.ConicTo(islandLeft, currentHeight, islandLeft + rBottom, currentHeight, w);
                _bgPath.LineTo(islandRight - rBottom, currentHeight);
                _bgPath.ConicTo(islandRight, currentHeight, islandRight, currentHeight - rBottom, w);
                _bgPath.LineTo(islandRight, rTopY);
                _bgPath.ConicTo(islandRight, 0, islandRight - rTopX, 0, w);
                _bgPath.Close();

                canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                byte alpha = (byte)(255 * startupProgress * transitionAlpha);
                float textOffsetY = 0f;

                // 仅恢复原版代码中软件刚启动时的位移，不影响状态切换
                if (!media.IsActive && startupProgress < 1f)
                {
                    textOffsetY = (1f - startupProgress) * 15f;
                }

                SKColor currentA = _currentTextColor.WithAlpha(alpha);
                SKColor subA = _currentSubTextColor.WithAlpha(alpha);

                _titlePaint.Color = currentA;
                _bodyPaint.Color = subA;
                _textPaint.Color = currentA;
                _timePaint.Color = currentA;
                _datePaint.Color = subA;
                _mediaIconPaint.Color = currentA;
                _clipboardLinkPaint.Color = currentA; // 剪贴板图标同为矢量：跟着主题色 + 透明度走
                _clipboardOpenPaint.Color = currentA;
                _barPaint.Color = currentA;
                _highQualitySampling.Color = SKColors.White.WithAlpha(alpha); // 同步作用于图片图标
                // ---------------- [ Toast 消息通知 ] ----------------
                if (toast != null)
                {
                    // 整块岛体被通知接管：详情页本帧不绘制，命中区必须显式作废
                    //（它已不再每帧清理 —— 理由见 InvalidatePluginHitAreas 的线程模型说明）
                    InvalidateDetailHitArea();
                    DrawToastLayer(canvas, toast, left, right, currentHeight);
                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ 剪贴板链接（已识别到链接） ] ----------------
                // 优先级：系统通知 > 剪贴板链接 > 媒体控制器。通知展示期间上层已把链接拦住排队，
                // 所以这里只要拿到链接，就把整块岛体交给剪贴板面板绘制。
                if (!string.IsNullOrEmpty(clipboardUrl))
                {
                    // 与 Toast 同理：整块岛体被剪贴板面板接管，详情页命中区显式作废
                    InvalidateDetailHitArea();
                    DrawClipboard(canvas, clipboardUrl, left, right, currentHeight, textOffsetY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // ---------------- [ 插件详情页（右键展开） ] ----------------
                // 详情页展开时整块岛体交给插件绘制：不再绘制原生内容，也不再绘制插件行。
                // 岛体尺寸由 NotchWindow 依据详情页 MeasureWidth/MeasureHeight 决定（这里同步消费一次状态即可）。
                // Toast 优先级高于详情页：通知到来时先显示通知，通知结束后详情页自动回来。
                var detailPage = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
                if (detailPage != null && !_detailBroken)
                {
                    DrawDetailPage(canvas, detailPage, left, currentHeight, currentWidth, alpha, textOffsetY, bars,
                        _pluginMouseX, _pluginMouseY - topY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // ---------------- [ 媒体控制与待机状态 ] ----------------
                if (media.IsActive)
                    UpdateMediaState(media);
                UpdateClockCache();

                // ---------------- [ 自定义组合模式 / 原生布局 ] ----------------
                // 媒体展开面板优先接管：组合模式与非组合模式一视同仁 —— 面板整块占满岛体，
                //    时钟 / 硬件 / 插件行本帧都不画（宿主已把插件行预算归零、岛体锁成面板尺寸）。
                //    判定与宿主尺寸决策共用 IsMediaExpanded，所以「组合模式也能展开」不需要额外分支。
                if (IsMediaPanelShowing(media, currentHeight))
                    DrawMediaControl(canvas, media, isHovered, bars,
                        new MediaBlockGeometry(left, left, right), currentHeight, textOffsetY, alpha);
                else if (CompositeModeEnabled)
                    DrawCompositeLayout(canvas, media, isHovered, bars, left, currentHeight, topY, textOffsetY, alpha);
                else
                    DrawNativeLayout(canvas, media, isHovered, bars, left, right,
                        currentHeight, textOffsetY, alpha);

                // ---- 插件组件行（非组合模式） ----
                // 组合模式下插件已并入「内容顺序表」跟原生模块混排（见上方组合模式分支），这里只处理其余模式：
                // 待机(时间日期/空白/硬件)、媒体激活、媒体展开……原生内容一律不感知插件，插件也不影响原生布局。
                // 与组合模式一样遵守顺序表：排在原生模块之前的插件画在原生内容左边，之后的画在右边。
                if (!CompositeModeEnabled && pluginReserve > 0f)
                {
                    if (leftPluginBlock > 0f)
                    {
                        // 左侧：从岛体左边缘的内边距起，按顺序表次序逐个插件向右排。
                        // 只画「排在原生模块之前」的插件 —— 原生模块本身在它自己的位置由上面的原生分支绘制。
                        // 起点直接锚在岛体左边缘（而不是从 nativeLeft 倒推），这样岛体宽度做动画时
                        // 插件行只跟着边缘一起平移，不会自己额外挪动。
                        // 裁剪到 [islandLeft, left]：稳态下这个区间恰好 == 「左侧插件组 + 与原生内容的间距」，
                        //    裁剪等于没裁（零视觉影响）；只有岛体还在变宽的瞬态（leftPluginBlock 被按比例
                        //    让位收窄）才真的切到 —— 于是左右两组绝不会在岛体中间叠在一起，
                        //    插件是随岛体长大从两侧滑入的。
                        var leftOrder = Plugins.PluginManager.Instance.Host.ContentOrder;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(islandLeft, 0f, left, currentHeight));
                        float lx = islandLeft + PLUGIN_GAP;
                        for (int i = 0; i < nativeOrderIndex && i < leftOrder.Count; i++)
                        {
                            string item = leftOrder[i];
                            if (Plugins.BuiltinWidgets.IsBuiltin(item)) continue;
                            lx = DrawPluginWidgets(canvas, item, lx, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        }
                        canvas.Restore();
                    }
                    // 右侧：整行兜底 —— 左侧已画过的组件带 drawn 标记不会重复，
                    // 还没进顺序表的组件（例如直接 RegisterWidget 注册的测试组件）也在这里补上。
                    // 同样锚在岛体右边缘（rightGroupW == 0 说明一个右侧组件都没有，直接跳过），
                    // 并同样裁剪到 [right, islandRight]（与左侧对称，理由同上）。
                    if (rightGroupW > 0f)
                    {
                        canvas.Save();
                        canvas.ClipRect(new SKRect(right, 0f, islandRight, currentHeight));
                        DrawPluginWidgets(canvas, null, islandRight - rightGroupW, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        canvas.Restore();
                    }
                }

                // === 下方原本旧版残留的 _wakePath 绘制代码已被彻底删除 ===

                canvas.Restore(); // 1. 恢复 ClipPath 裁切
                canvas.Restore(); // 2. 闭合 SaveLayer 透明层，本体内部渲染彻底完结！任何阴影、遮罩全部随之消失。
                // 独立于本体之外，绘制隐形物理热区与极速渐变唤醒按钮
                // 穿透睡眠态 与 完全隐藏态 共用同一颗按钮（位置/命中都在岛体正中）
                if ((PassthroughModeEnabled && PassthroughAlpha < 0.99f) || FullHideAlpha < 0.99f)
                    DrawWakeButton(canvas, currentHeight);

                canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
            }
            finally
            {
                Monitor.Exit(_renderLock);
            }
        }

    }
}
