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

        private static volatile float _baseHeight = 29f;

        private static volatile float _mediaWidth = 250f;

        private static volatile float _mediaHeight = 35f;

        private static volatile float _toastWidth = 260f;

        private static volatile float _toastHeight = 55f;

        private static volatile float _globalDpi = 1.0f;

        private static volatile float _notchBottomRadius = 12f;

        public static float STANDBY_WIDTH { get => _standbyWidth; set => _standbyWidth = value; }

        public static float BASE_HEIGHT { get => _baseHeight; set => _baseHeight = value; }

        public static float MEDIA_WIDTH { get => _mediaWidth; set => _mediaWidth = value; }

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

        public static int ThemeMode { get; set; } = 0; // 0=黑, 1=白, 2=跟随系统

        public static int NotchStyle { get; set; } = 0; // 0=经典刘海, 1=灵动岛

        public static int StandbyDisplayMode { get; set; } = 0; // 0=时间日期, 1=空白

        public static int TargetMonitorIndex { get; set; } = 0; // 目标显示器索引

        public static int BgOpacityLevel { get; set; } = 4; // 透明度档位：0=0%, 1=25%, 2=50%, 3=75%, 4=100%

        public static bool CompositeModeEnabled { get; set; } = false; // 自定义组合模式总开关

        public static bool CompShowDateTime { get; set; } = true;  // 显示时间日期

        public static bool CompShowHardware { get; set; } = true;  // 显示硬件占用

        public static bool CompShowMedia { get; set; } = true;     // 显示媒体控制器(含频谱)

        public static bool PassthroughModeEnabled = false; // 穿透模式总开关

        public static float PassthroughAlpha = 1.0f; // 穿透动画平滑插值

        // 媒体交互状态：0=直接交互，1=展开交互(默认)
        public static int MediaInteractionMode = 1;

        /// <summary>
        /// 岛体总长度上限：Toast / 剪贴板面板的自适应宽度、组合模式总宽、以及插件行的取舍都以它封顶。
        ///
        /// <para>
        /// <b>2026-09-20 由 800 放开到 1920（用户要求）</b>：用户原话「必须放开最大长度，灵动岛本体哪怕
        /// 宽度 max=1920 都无所谓，宁愿灵动岛超长溢出屏幕都不要被裁切」。
        /// 旧的 800 是「怕挤压到右边的插件」而设的，但实际效果是**长歌词被裁切**，
        /// 而且插件行预算（= 本值 − 原生内容宽度）被长歌词吃光后，插件会**直接整帧不显示**
        /// （不是被压缩，是彻底消失），体验很差 —— 这个顾虑被证明完全没必要。
        /// </para>
        ///
        /// <para>
        /// 1920 是**本体**的上限（≈ 106 个汉字，任何真实歌词行都远达不到）。
        /// 它同时也是窗口内容区的下限来源：<see cref="WINDOW_WIDTH"/> 必须 ≥ 本值，
        /// 否则岛体超出窗口的部分会被窗口边缘裁掉（那就又变成裁切了）。
        /// 岛体允许溢出屏幕 —— 窗口比屏幕宽是合法的，透明像素照常鼠标穿透。
        /// </para>
        /// </summary>
        public const float MAX_ISLAND_WIDTH = 1920f;

        // ⛔ 2026-09-20 用户明确要求「媒体控制器的长度也放开，多长都无所谓」，因此删掉了两个上限常量：
        //    · MEDIA_TEXT_MAX_WIDTH（默认 480 ≈ 27 个汉字）—— 非组合模式的媒体文本区上限
        //    · CompositeMediaMaxWidth（默认 460 ≈ 21 个汉字）—— 组合模式媒体模块的占宽上限
        //    这两个才是「歌词一长就被裁切」的真正元凶（它们都比 MAX_ISLAND_WIDTH 小得多，长歌词先撞到它们），
        //    而且把原生内容宽度钉死/压低后，插件行预算（= MAX_ISLAND_WIDTH − 原生宽度）被吃光，
        //    装不下的插件会**整帧不显示**（不是压缩，是彻底消失）。
        //    ⚠️ 不要再以「防止挤压插件」为由把它们加回来 —— 插件该不该显示由插件行预算决定，
        //       而岛体该多长就多长（上限见 MAX_ISLAND_WIDTH）。

        // 动态计算最大边界，防止因刘海变大导致出界
        // 将透明原生窗口的基础画布拓宽，给极长歌词预留充足的物理空间，防止被系统窗口边缘裁切
        // 🧩 插件详情页展开时，底层缓冲必须容得下详情页尺寸（+80 / +45 是原有的四周留白）
        // ⚠️ 2026-09-20：岛体总长上限放宽到 MAX_ISLAND_WIDTH(1920) 后，窗口内容区**必须**跟着 ≥ 它 ——
        //    岛体是水平居中画的（islandLeft = (WINDOW_WIDTH - currentWidth) / 2），
        //    只要 currentWidth > WINDOW_WIDTH，islandLeft 就变成负数，超出窗口的那部分会被窗口边缘硬裁，
        //    等于又绕回「被裁切」。所以这里把 MAX_ISLAND_WIDTH 也纳入下限。
        //    窗口比屏幕宽是允许的（岛体可以溢出屏幕，用户明确接受）；透明像素照常鼠标穿透，
        //    位置换算（logX / ptDst.x）都已经带上了「窗口居中于显示器」的偏移量，无需另行处理。
        public static float WINDOW_WIDTH => Math.Max(1200f,
            Math.Max(MAX_ISLAND_WIDTH,
                Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))))) + 80f;

        public static float MAX_WINDOW_HEIGHT => Math.Max(220f, Math.Max(ActiveDetailHeight, Math.Max(BASE_HEIGHT, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT))) + 45f);

        public const int OUTER_R = 14;

        public const int INNER_R = 12;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f, string? clipboardUrl = null)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                canvas.Clear(SKColors.Transparent);

                // 🧩 每帧清空插件命中区，仅当本帧实际绘制插件行时才重新填充
                // （防止 Toast / 媒体激活等不绘制插件的状态下残留上一帧的过期命中矩形）
                InvalidatePluginHitAreas();

                // 🎵 时间轴几何登记表帧首作废：本帧不画就等于命中区不存在
                _tlBarX1 = _tlBarX2 = _tlBarY = 0f;

                // 📋 剪贴板「打开」按钮热区帧首作废：本帧不画就等于命中区不存在
                _clipboardOpenHit = default;

                // 🖱️ 原生模块（时间/日期、CPU/RAM、媒体）右键命中区同样帧首作废
                InvalidateNativeHitZones();

                // 岛体物理左边界（背景形状 / 裁剪范围以它为准）
                float islandLeft = (WINDOW_WIDTH - currentWidth) / 2f;
                // 岛体物理右边界（背景形状 / 裁剪范围以它为准）
                float islandRight = islandLeft + currentWidth;
                // 🧩 插件行的位置：
                //   · 组合模式：插件已并入「内容顺序表」，与原生模块一起混排（宽度计在 GetCompositeWidth 内），
                //     不单独占用预留区；
                //   · 非组合模式：同样遵守这张顺序表 —— 排在「本帧原生模块」之前的插件画在原生内容**左边**，
                //     之后的画在右边。原生内容的左右边界据此内收，所以插件显示与否、排在哪一边，
                //     都不会影响原生功能本身。
                //     （2026-09-20 修复：此前非组合模式无条件把整行插件贴在岛体最右侧、完全不读顺序表，
                //       导致「插件中心」的 ← / → 只在组合模式下有效。）
                // ⚠️ 这里必须用**未缩放**的预留（GetPluginRowReserve，而不是 GetScaledPluginReserve）。
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
                    // ⚠️ 判定阈值用**常量** MIN_NATIVE_AREA，不能换成「本帧原生内容所需宽度」——
                    //    后者与目标宽度同一刻跳变，而 currentWidth 还停在旧目标上，
                    //    换歌词那一帧就会误判「装不下」而把原生内容区一步拉宽（又是一次跳变）。
                    //    常量下限下：稳态（currentWidth == nativeWidth + reserve）永远不触发，
                    //    换歌词（reserve 不变、只有目标宽度变大）也不触发 —— 边界只跟着岛体边缘平滑移动。
                    // 让位系数 k 随 currentWidth 连续变化（totalBlock 在预留不变时是常量），
                    // 不会引入新的跳变；收窄后的区间由绘制侧裁剪落实（见下方 ClipRect），
                    // 于是左右两组绝不会在岛体中间叠在一起，插件是随岛体长大从两侧滑入的。
                    // ⚠️ 只在「本帧确有原生内容」时才介入：空白待机（nativeBuiltinId == null）时
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
                int btnPrevX = (int)right - 90;
                int btnPlayX = (int)right - 60;
                int btnNextX = (int)right - 30;

                // 灵动岛悬浮距离顶部的 Y 轴高度 (随过渡进度平滑变化)
                float topY = 12f * styleProgress;

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中
                canvas.Translate(0, topY);

                // 开启一个硬件级透明图层，包裹本体所有元素，杜绝任何图层/阴影残留
                _layerPaint.Color = SKColors.White.WithAlpha((byte)(255 * PassthroughAlpha));
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
                _clipboardLinkPaint.Color = currentA; // 📋 剪贴板图标同为矢量：跟着主题色 + 透明度走
                _clipboardOpenPaint.Color = currentA;
                _barPaint.Color = currentA;
                _highQualitySampling.Color = SKColors.White.WithAlpha(alpha); // 同步作用于图片图标
                // ---------------- [ Toast 消息通知 ] ----------------
                if (toast != null)
                {
                    DrawToastLayer(canvas, toast, left, right, currentHeight);
                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // ---------------- [ 📋 剪贴板链接（已识别到链接） ] ----------------
                // 优先级：系统通知 > 剪贴板链接 > 媒体控制器。通知展示期间上层已把链接拦住排队，
                // 所以这里只要拿到链接，就把整块岛体交给剪贴板面板绘制。
                if (!string.IsNullOrEmpty(clipboardUrl))
                {
                    DrawClipboard(canvas, clipboardUrl, left, right, currentHeight, textOffsetY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // ---------------- [ 🧩 插件详情页（右键展开） ] ----------------
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

                // ---------------- [ 自定义组合模式 ] ----------------
                if (CompositeModeEnabled)
                    DrawCompositeLayout(canvas, media, isHovered, bars, left, currentHeight, topY, textOffsetY, alpha);
                else
                    DrawNativeLayout(canvas, media, isHovered, bars, left, right, btnPrevX, btnPlayX, btnNextX,
                        currentHeight, textOffsetY, alpha);

                // ================= 🧩 插件组件行（非组合模式） =================
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
                        // ✂️ 裁剪到 [islandLeft, left]：稳态下这个区间恰好 == 「左侧插件组 + 与原生内容的间距」，
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
                if (PassthroughModeEnabled && PassthroughAlpha < 0.99f)
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
