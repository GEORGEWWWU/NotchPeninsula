using System;
using SkiaSharp;

namespace NotchPeninsula
{
    // 液态玻璃背景（Liquid Glass）—— C++ / HLSL 版 Liquid Glass Demo 的纯 C# + SkiaSharp 移植。
    //
    // 管线与 demo 一一对应：
    //   1. 抓屏      ：优先 MagnifierBackdrop（放大镜 API，1:1 全分辨率 + 排除岛体自身），
    //                  不可用时回退 IslandBackdrop 的 GDI 抓屏
    //   2. 可分离高斯 ：ComputeGaussianWeights + 水平/垂直两趟（对应 BlurH_PS / BlurV_PS）
    //   3. 玻璃本体   ：SDF 圆角矩形 → 边缘折射位移 + RGB 色散 + 饱和度提升 + 玻璃本色
    //                  （对应 GlassDispersionPS 的 sdRoundedRect / gradSdRoundedRect / circleMap）
    //
    // 为什么不用 SKRuntimeEffect（SKSL）+ GPU 上下文：
    //   1. 岛体画布是 CPU 光栅面（SKSurface.Create 直写位图），没有 GRContext 就没 GPU 后端，
    //      此时 SKSL 走的仍是 SkRasterPipeline 逐像素，不比手写循环快，还要多付一次编译与 uniform 开销。
    //   2. 要真拿到 GPU 后端，Windows 上得先有原生 3D 上下文，三条路都不便宜：
    //      · ANGLE（libEGL/libGLESv2）：SkiaSharp 官方随包的那份在非 MSIX 打包应用里有已知缺陷
    //        （两个 DLL 需要互相冲突的构建标志，见 SkiaSharp issue #2968），社区的解法是自己
    //        从源码编两遍 ANGLE —— 对「单文件 exe」分发的项目是灾难。
    //      · D3D12（GRContext.CreateDirect3D）：要先建 ID3D12Device / CommandQueue，
    //        得额外引入 Vortice.Direct3D12 之类的包，且 SkiaSharp 的 D3D 后端在 2.88 仍属实验性。
    //      · Vulkan：同样要额外的原生封装。
    //   3. 即便有了上下文，玻璃图层算完还得 readback 回 CPU（岛体最终走 UpdateLayeredWindow
    //      上屏，必须是 CPU 位图），这块回读又会吃掉一部分收益。
    //   结论：收益（玻璃层毫秒级）配不上代价（+依赖 +发布复杂度 +崩溃面）。
    //   真正拿到手的性能提升走的是另一条路：下面的按行并行（多核）+ 图层缓存与节流。
    internal static unsafe class LiquidGlass
    {
        // ==================== 固定参数（逻辑像素，与岛体其他尺寸同口径） ====================

        // 边缘高光的衰减宽度（越小越贴边）
        public const float RimWidth = 3.2f;

        // ==================== 可调参数（设置页「液态玻璃」卡，9 项） ====================

        // 参数下标：设置页与注册表都按这个顺序走，加参数只需往后追加
        public const int P_Blur = 0;          // 背景模糊（最影响"清不清晰"）
        public const int P_Refraction = 1;    // 折射强度：边缘把背景掰弯多少
        public const int P_Dispersion = 2;    // 边缘色散：彩虹边
        public const int P_Rim = 3;           // 边缘高光
        public const int P_Thickness = 4;     // 玻璃厚度：折射带宽度
        public const int P_Depth = 5;         // 透镜感：法线外扩
        public const int P_Saturation = 6;    // 饱和度
        public const int P_Frost = 7;         // 玻璃雾度：掺白比例
        public const int P_Brightness = 8;    // 明度
        public const int P_CaptureHz = 9;     // 抓屏刷新间隔（多久读一次屏幕）
        public const int P_FrameRate = 10;    // 岛体渲染帧率上限（影响整个岛，不只是玻璃）
        public const int P_Zoom = 11;         // 放大倍率：玻璃整体上把背景放大多少

        public const int ParamCount = 12;

        public static readonly string[] ParamNames =
        {
            "背景模糊", "折射强度", "边缘色散", "边缘高光", "玻璃厚度", "透镜感", "饱和度", "玻璃雾度", "明度",
            "抓屏刷新", "岛体帧率", "放大倍率"
        };

        public static readonly string[] ParamKeys =
        {
            "LiquidGlassBlur", "LiquidGlassRefraction", "LiquidGlassDispersion", "LiquidGlassRim",
            "LiquidGlassThickness", "LiquidGlassDepth", "LiquidGlassSaturation", "LiquidGlassFrost",
            "LiquidGlassBrightness", "LiquidGlassCaptureMs", "LiquidGlassFps", "LiquidGlassZoom"
        };

        // 设置页用的两字简写。一列只有 123px（标签 24 + 滑轨 52 + 档位值 27），
        // 四字标签会把滑轨挤回 36 以内 —— 那样比排版拥挤更糟。
        // ParamNames 保留完整名：诊断日志要能和注册表键对得上。
        public static readonly string[] ParamLabels =
        {
            "模糊", "折射", "色散", "高光", "厚度", "透镜", "饱和", "雾度", "明度",
            "刷新", "帧率", "倍率"
        };

        // 抓屏刷新间隔（ms）：越短越跟手，代价是更频繁地读屏幕
        private static readonly int[] CaptureMsTable = { 1000, 500, 250, 160, 60 };

        // 岛体渲染帧率（fps）→ 帧间隔 ms
        private static readonly int[] FrameFpsTable = { 30, 60, 90, 120, 144 };

        /// <summary>后台抓屏线程的刷新节拍（ms）。</summary>
        public static int RefreshMs => CaptureMsTable[EffectiveLevel(P_CaptureHz)];

        private static volatile int _glassLum;

        /// <summary>
        /// 最近一次玻璃成品的中心平均亮度（0..255，BT.601）。玻璃下文字自适应黑白的判据。
        /// 命中缓存的帧不会重算 —— 内容没变，亮度也不会变，停在旧值正合适。
        /// </summary>
        public static int GlassLuminance => _glassLum;

        /// <summary>渲染线程的帧间隔（ms）。渲染循环每帧读一次，所以改完立刻生效。</summary>
        public static int FrameIntervalMs => 1000 / FrameFpsTable[EffectiveLevel(P_FrameRate)];

        // 「极致」档把「刷新」「帧率」两根滑轨锁到最高档：这个档的定位就是"能喂多快就喂多快"，
        // 玻璃再被低刷新拖成半拍前的画面，超采样和 1:1 抓屏就白做了。
        // ⚠️ 只锁「读取」，不改 _levels —— 切回别的档位时用户原来的设定原样回来。
        private static bool IsLockedByQuality(int index)
            => _quality == 3 && (index == P_CaptureHz || index == P_FrameRate);

        /// <summary>该滑轨是否被当前质量档锁在固定值（设置页据此把标签画灰、数值画成强调色）。</summary>
        public static bool IsLevelLocked(int index)
            => index >= 0 && index < ParamCount && IsLockedByQuality(index);

        private static int EffectiveLevel(int index) => IsLockedByQuality(index) ? MaxLevel : _levels[index];

        /// <summary>某根滑轨 5 个档位各自的显示文本（数值型参数显示具体值，其余显示强弱）。</summary>
        public static string[] ValueTexts(int index)
        {
            switch (index)
            {
                case P_Blur: return ["无", "2", "4", "6", "10"];
                case P_CaptureHz: return ["1s", "0.5s", "250ms", "160ms", "60ms"];
                case P_FrameRate: return ["30", "60", "90", "120", "144"];
                case P_Zoom: return ["1.0x", "1.08x", "1.18x", "1.32x", "1.5x"];
                default: return ["最低", "偏低", "标准", "偏高", "最高"];
            }
        }

        // 所有滑块统一 5 档（0..4），与背景透明度那根滑轨同口径
        public const int MaxLevel = 4;

        // 各档默认值（下标 = 上面的 P_*）：刷新默认 250ms、帧率默认 60fps，都是表中下标 2。
        // 倍率默认 0（1.0x）：放大是把背板内容整体撑开，边缘会被拉得更假，默认不放大。
        private static readonly int[] DefaultLevels = { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0 };

        private static readonly int[] _levels = (int[])DefaultLevels.Clone();

        /// <summary>出厂默认档位（设置页「恢复默认参数」用）。</summary>
        public static int DefaultLevelOf(int index)
            => index >= 0 && index < ParamCount ? DefaultLevels[index] : 0;

        // 设置页读的是「实际生效值」：被质量档锁住的两根滑轨直接显示最高档，
        // 用户拖它也会立刻弹回最高 —— 比"能拖但没反应"清楚。
        public static int GetLevel(int index) => index >= 0 && index < ParamCount ? EffectiveLevel(index) : 0;

        public static void SetLevel(int index, int value)
        {
            if (index < 0 || index >= ParamCount) return;
            int v = value < 0 ? 0 : (value > MaxLevel ? MaxLevel : value);
            if (v == _levels[index]) return;
            _levels[index] = v;
            if (index == P_Blur) ApplyBlur();   // 改模糊 = 换高斯核，权重表要重建
            Invalidate();
        }

        // ---- 由档位派生的实际数值 ----

        // 背景模糊半径 → 0/2/4/6/10。设成 0 就是"完全不糊"，玻璃最锐利
        private static readonly int[] BlurRadiusTable = { 0, 2, 4, 6, 10 };

        private static readonly float[] ThicknessTable = { 6f, 10f, 14f, 18f, 24f };

        private static readonly float[] SaturationTable = { 0.60f, 0.90f, 1.22f, 1.50f, 1.80f };

        private static readonly float[] BrightnessTable = { 0.80f, 0.90f, 1.00f, 1.08f, 1.15f };

        // 放大倍率：1.0 = 原样（不放大），越大越像一块凸透镜。默认 1.18x
        private static readonly float[] ZoomTable = { 1.00f, 1.08f, 1.18f, 1.32f, 1.50f };

        public static float Zoom => ZoomTable[_levels[P_Zoom]];

        public static float RefractionAmount => _levels[P_Refraction] * 6f;      // 0 / 6 / 12 / 18 / 24

        public static float Dispersion => _levels[P_Dispersion] * 0.09f;         // 0 .. 0.36

        public static float RimStrength => _levels[P_Rim] * 0.10f;               // 0 .. 0.40

        public static float RefractionHeight => ThicknessTable[_levels[P_Thickness]];

        public static float DepthEffect => _levels[P_Depth] * 0.2f;              // 0 .. 0.80

        public static float Saturation => SaturationTable[_levels[P_Saturation]];

        public static float TintAlpha => _levels[P_Frost] * 0.04f;               // 0 .. 0.16

        public static float Darkening => BrightnessTable[_levels[P_Brightness]];

        // ---- 高斯模糊 ----
        // ⚠️ _radius 是**背板像素**口径，不是逻辑像素：抓屏被降采样 1/2、1/3 时抽头数不变，
        // 屏幕上的模糊半径就跟着放大同样倍数（均衡档 4px 实际是 8px），观感是"糊成一团、像折射过头"。
        // Compose 里按「背板 px / 逻辑 px」把档位半径换算回来，这里只存换算后的值。
        private static int _radius = 4;

        // 档位原始半径（逻辑像素口径 0/2/4/6/10）。初值取默认档下标 2 的表值：
        // 注册表值恰好等于默认时 SetLevel 会早退，ApplyBlur 根本不会被调用。
        private static int _levelRadius = 4;

        private static float _sigma = 2.2f;

        public static int BlurRadius => _radius;

        private static void SetRadius(int r)
        {
            // ⚠️ 判"算过没有"必须用 _weightsValid，**不能**用 _weights.Length > 0：
            //    _weights 的声明初始化就是 new float[7]（全 0），而 _radius 初值 4 又正好等于默认档半径，
            //    于是静态构造里的 SetRadius(4) 会命中"看起来已经算过"的分支直接返回 —— 权重一项都没赋值。
            //    BuildBlur 拿全 0 的抽头一卷，整张模糊图被乘成纯黑，玻璃恒等于 tint(0)=45.9（抖动后 45/46），
            //    且与桌面内容无关；点一次质量档把 imgPerX 换成 0.5/0.33、半径变成 2/3 才会真正重算 ⇒
            //    「每次启动都是死灰、点一下就好」。这就是那个 bug 的根。
            if (r == _radius && _weightsValid) return;
            _radius = r;
            _sigma = r <= 0 ? 0f : r / 2.7f;
            _weights = new float[Math.Max(1, r + 1)];
            if (r > 0) ComputeGaussianWeights(_sigma, r, _weights);
            else _weights[0] = 1f;   // 半径 0 时虽然走 skipKernel，也留一个"原样通过"的权重兜底
            _weightsValid = true;
        }

        private static void ApplyBlur()
        {
            _levelRadius = BlurRadiusTable[_levels[P_Blur]];
            SetRadius(_levelRadius);
        }

        // ---- 质量档：抓屏倍率 / 色散采样数 / 玻璃层上限 / 刷新间隔 ----

        private static int _quality = 1;      // 0=流畅 1=均衡 2=精细 3=极致

        public static int Quality
        {
            get => _quality;
            set
            {
                int q = value < 0 ? 0 : (value > 3 ? 3 : value);
                if (q == _quality) return;
                _quality = q;
                ApplyQuality();
            }
        }

        public static string QualityName => _quality switch { 0 => "流畅", 1 => "均衡", 2 => "精细", _ => "极致" };

        // GDI 抓屏降采样（放大镜抓屏固定 1:1，不受它影响）：
        // 折射位移要看得出来，不能像亚克力那样糊到 1/12
        private static float _downscale = 2f;

        public static float CaptureDownscale => _downscale;

        // 玻璃图层分辨率上限：超出后按面积等比降采样（内容本来就模糊，看不出来）
        // 玻璃图层分辨率上限：超出后按面积等比降采样。
        // ⚠️ 这个上限一超，玻璃图就比胶囊小 → 贴图时被放大 → 观感是"展开态整个背景被放大、还糊"。
        // 展开态 900×400 逻辑 @125% = 1125×500 ≈ 56 万像素，所以默认档必须给到 60 万。
        // （折叠态 570×40 ≈ 2.8 万，怎么都够 —— 这也是折叠态一直正常的原因。）
        private static int _maxGlassPixels = 600000;

        // 玻璃图层超采样倍数（只在「极致」档开 2）：SDF 边缘、色散与边缘高光都按双倍分辨率算，
        // 贴图时再由 Skia 降采样回逻辑尺寸 —— 边缘那圈 1px 的硬折线和色散采样的台阶会一起消失。
        // 代价是玻璃层像素 ×4（模糊那一步不变），所以只给最高档。
        private static int _supersample = 1;

        // 色散采样数：7 次（R/G/B 各一整套）或 3 次（每通道只采一次）
        private static int _samples = 7;

        /// <summary>
        /// 重新套一遍当前质量档（= 设置页点一次「玻璃质量」）。
        /// 启动自愈走这条：手动刷新有效、单靠 Invalidate+Expire 无效，差别就在这一步。
        /// </summary>
        public static void ReapplyQuality() => ApplyQuality();

        // 质量档只管「抓屏倍率 / 玻璃层上限 / 色散采样数 / 超采样」——
        // 刷新间隔交给 P_CaptureHz 单独控制，免得两个控件抢同一个数。
        private static void ApplyQuality()
        {
            switch (_quality)
            {
                case 0:                       // 流畅：抓屏更糊、色散只采 3 次
                    _downscale = 3f;
                    _maxGlassPixels = 200000;
                    _samples = 3;
                    _supersample = 1;
                    break;
                case 2:                       // 精细：1:1 抓屏 + 全分辨率玻璃层
                    _downscale = 1f;
                    _maxGlassPixels = 900000;
                    _samples = 7;
                    _supersample = 1;
                    break;
                case 3:                       // 极致：1:1 抓屏 + 2× 超采样玻璃层（不设实用上限）
                    _downscale = 1f;
                    _maxGlassPixels = 3200000;
                    _samples = 7;
                    _supersample = 2;
                    break;
                default:                      // 均衡
                    _downscale = 2f;
                    _maxGlassPixels = 600000;
                    _samples = 7;
                    _supersample = 1;
                    break;
            }

            Invalidate();
            IslandBackdrop.Expire();   // 抓屏倍率变了 → 后台那张背板作废重抓
        }

        // ==================== 性能护栏 ====================

        // 模糊区上限：超出行就跳过高斯，直接用抓屏的盒式降采样结果（已经够糊）
        private const int MaxBlurPixels = 220000;

        // 裁切区对齐粒度：胶囊小幅抖动时不必重算模糊
        private const int CropGrid = 32;

        // 玻璃图层小于这个像素数就串行算（起线程不划算）
        private const int ParallelMinPixels = 12000;

        // ==================== 从 C++ demo 搬过来的「防卡顿」措施 ====================

        // ① 缓动跟随：demo 的 g_smoothSrc += (g_targetSrc - g_smoothSrc) * g_easeSpeed，g_easeSpeed = 0.5。
        //    作用不是"更准"，而是"更顺"：抓屏天然比画面滞后一帧到几帧，玻璃源矩形要是硬跟，
        //    每次新抓屏落地都会跳一下；缓动之后读起来是玻璃的惯性，卡顿感就没了。
        //    岛体不动，靠它吃的是胶囊宽度变化（居中，所以左右边缘会移）与滑出/滑入。
        private const float EaseSpeed = 0.5f;

        // ② 突变对齐：对应 demo 在 WM_SIZE 里把 smooth 重置成 target。
        //    换显示器 / DPI 变化 / 从隐藏恢复时源矩形是"跳"过去的，这时候再缓动会露出一帧错位的玻璃。
        private const float EaseSnapPx = 64f;

        private static float _easeSx = float.NaN, _easeSy = float.NaN;

        // ③ 内容未变就不重算：demo 用 g_texUpdated 门控全屏纹理上传（否则每帧 8MB 白传）。
        //    岛体的等价问题反过来 —— 默认 250ms 一抓，version 每 4 秒 +16，
        //    而桌面大多数时候是静止的，每次都重算整层玻璃纯属浪费。
        //    所以对裁切区做个抽样哈希：内容没变就继续用上一帧的玻璃。
        private static ulong _keyHash;

        private static int _keyCropX, _keyCropY, _keyCropW, _keyCropH;

        // 诊断：上次打日志时的 src 与玻璃宽（变了才打，免得刷屏）
        private static float _diagSx = float.NaN, _diagSy = float.NaN;
        private static int _diagW = -1;

        // 命中统计：诊断里能直接看到「缓存命中 / 内容未变跳过 / 真重算」的比例
        private static int _statCache, _statHash, _statBuild;

        public static string Stats => $"重算 {_statBuild} / 内容未变跳过 {_statHash} / 缓存命中 {_statCache}";

        public static void ResetStats() { _statCache = _statHash = _statBuild = 0; }

        private static float[] _weights = new float[7];

        // 权重表是否真的算过（详见 SetRadius 的注释）。
        // ⚠️ 不能用 _weights.Length 判断：声明初始化就是长度 7 的全 0 数组。
        private static bool _weightsValid;

        static LiquidGlass()
        {
            ApplyBlur();
            ApplyQuality();
        }

        // 高斯权重（C++ 版 ComputeGaussianWeights 直译）
        public static void ComputeGaussianWeights(float sigma, int radius, float[] weights)
        {
            float sum = 0f;
            for (int i = 0; i <= radius; i++)
            {
                weights[i] = MathF.Exp(-0.5f * (i * i) / (sigma * sigma));
                sum += i == 0 ? weights[i] : 2f * weights[i];
            }
            for (int i = 0; i <= radius; i++) weights[i] /= sum;
        }

        // ---- 模糊工作区（只覆盖胶囊那一小块，不是整张抓屏）----
        // _pad / _rowMap 是内层循环的预夹取缓冲，见 BuildBlur 里的说明
        // _down 是"降采样后再模糊"的中转：模糊半径够大时，先 2×2 盒式降采样再糊，
        // 抽头数与像素数同时减少，等效模糊半径不变 —— 见 BuildBlur。
        private static byte[] _down = Array.Empty<byte>();

        private static int _blurScale = 1;

        private static int _blurScaleKey = 1;

        // 半径也要进缓存键：换抓屏倍率时 bs 可能不变（3px→4px 都在 bs=1），但抽头数变了
        private static int _blurRadiusKey = -1;

        private static byte[] _pad = Array.Empty<byte>();

        private static int[] _rowMap = Array.Empty<int>();

        private static byte[] _blurA = Array.Empty<byte>();

        private static byte[] _blurB = Array.Empty<byte>();

        private static bool _blurValid;

        private static int _blurX, _blurY, _blurW, _blurH, _blurVersion;

        // ---- 玻璃图层缓存 ----
        private static SKBitmap? _glass;

        private static int _keyVersion = -1;

        private static int _keyW, _keyH;

        private static float _keySx, _keySy, _keyLw, _keyLh, _keyRt, _keyRb;

        // 材质切走 / 岛体重建时调用：放掉缓存位图，别白占内存
        public static void Invalidate()
        {
            _blurValid = false;
            _blurVersion = -1;
            _keyVersion = -1;
            if (_glass != null)
            {
                _glass.Dispose();
                _glass = null;
            }
        }

        /// <summary>
        /// 生成胶囊的液态玻璃图层。
        /// </summary>
        /// <param name="backdrop">抓屏背板（IslandBackdrop 产出，只允许渲染线程访问）</param>
        /// <param name="version">抓屏版本号：背板换了新的才需要重算</param>
        /// <param name="src">背板上「当前胶囊」对应的源矩形（图像像素坐标）</param>
        /// <param name="logicalW">玻璃图层总宽（逻辑像素）：刘海模式下 = 胶囊宽 + 2×flareX</param>
        /// <param name="logicalH">胶囊逻辑高度</param>
        /// <param name="rTop">顶部圆角（逻辑像素）；刘海凹角时应传 0 —— 凹角由 flareX/flareY 描述</param>
        /// <param name="rBottom">底部圆角（逻辑像素）</param>
        /// <param name="flareX">刘海外扩量（逻辑像素，水平）：&gt;0 表示顶角是「向外张开」的凹角</param>
        /// <param name="flareY">外扩带厚度（逻辑像素，竖直）= 路径里那条贝塞尔的竖直跨度</param>
        /// <param name="flushTop">顶边是否贴合屏幕边缘（刘海模式）：贴合时顶边不算边界</param>
        /// <returns>玻璃图层；null 表示这台机器读不到像素，调用方应退回普通模糊背板</returns>
        public static SKBitmap? Compose(SKImage backdrop, int version, SKRect src,
                                       float logicalW, float logicalH, float rTop, float rBottom,
                                       float flareX = 0f, float flareY = 0f, bool flushTop = false)
        {
            if (logicalW < 8f || logicalH < 4f) return null;
            if (src.Width < 4f || src.Height < 2f) return null;

            // 超采样：极致档按 2× 分辨率算玻璃层（SDF 边缘 / 色散 / 高光都翻倍），
            // 贴图时 Skia 再降回逻辑尺寸 —— 几何全部走「玻璃层 px / 逻辑 px」换算，所以只改这里就够。
            int gw = (int)MathF.Round(src.Width * _supersample);
            int gh = (int)MathF.Round(src.Height * _supersample);
            if (gw < 1) gw = 1;
            if (gh < 1) gh = 1;

            float area = (float)gw * gh;
            if (area > _maxGlassPixels)
            {
                float q = MathF.Sqrt(_maxGlassPixels / area);
                gw = Math.Max(1, (int)(gw * q));
                gh = Math.Max(1, (int)(gh * q));
            }

            // ── ① 缓动跟随（demo 的 g_smoothSrc*）──
            ApplyEase(ref src);

            // 命中缓存：抓屏没换、几何没动 → 直接复用上一帧的玻璃
            if (_glass != null && _keyVersion == version && _keyW == gw && _keyH == gh
                && MathF.Abs(_keySx - src.Left) < 0.5f && MathF.Abs(_keySy - src.Top) < 0.5f
                && MathF.Abs(_keyLw - logicalW) < 0.5f && MathF.Abs(_keyLh - logicalH) < 0.5f
                && MathF.Abs(_keyRt - rTop) < 0.5f && MathF.Abs(_keyRb - rBottom) < 0.5f)
            {
                _statCache++;
                return _glass;
            }

            using (var pixmap = backdrop.PeekPixels())
            {
                if (pixmap == null) return null;                 // GPU 后端读不到像素 → 交给调用方兜底
                IntPtr pxs = pixmap.GetPixels();
                if (pxs == IntPtr.Zero) return null;

                int cB, cG, cR;
                SKColorType ct = pixmap.ColorType;
                if (ct == SKColorType.Bgra8888) { cB = 0; cG = 1; cR = 2; }
                else if (ct == SKColorType.Rgba8888) { cR = 0; cG = 1; cB = 2; }
                else return null;

                int iw = pixmap.Width, ih = pixmap.Height, istride = pixmap.RowBytes;

                // 只需要「胶囊 + 折射位移 + 模糊半径」这一小块，别把整张抓屏都糊一遍
                float imgPerX = src.Width / logicalW, imgPerY = src.Height / logicalH;

                // 模糊半径换算成背板像素：抓屏降采样 1/2 时半径也要 ×2，否则视觉上糊一倍。
                // 这一条是「只有精细档不糊」的根治 —— 抓屏倍率变了，抽头数不能原地不动。
                SetRadius(Math.Max(0, (int)MathF.Round(_levelRadius * MathF.Max(imgPerX, imgPerY))));

                float maxShift = RefractionAmount * (1f + Dispersion) + 4f;
                int margin = (int)MathF.Ceiling(maxShift * MathF.Max(imgPerX, imgPerY)) + _radius + 2;

                int cx0 = AlignDown((int)MathF.Floor(src.Left) - margin, CropGrid);
                int cy0 = AlignDown((int)MathF.Floor(src.Top) - margin, CropGrid);
                int cx1 = AlignUp((int)MathF.Ceiling(src.Right) + margin, CropGrid);
                int cy1 = AlignUp((int)MathF.Ceiling(src.Bottom) + margin, CropGrid);
                if (cx0 < 0) cx0 = 0;
                if (cy0 < 0) cy0 = 0;
                if (cx1 > iw) cx1 = iw;
                if (cy1 > ih) cy1 = ih;
                int cw = cx1 - cx0, ch = cy1 - cy0;
                if (cw < 4 || ch < 2) return null;

                // ── ③ 内容未变 → 连模糊带折射全省掉 ──
                // 抽样哈希（每 4 行、每 4 像素采一次）比糊一遍 + 算一遍折射便宜一到两个数量级，
                // 而桌面绝大多数时候是静止的：默认 250ms 一抓，这一条能把稳态开销打到接近 0。
                ulong hash = HashCrop((byte*)pxs, istride, cx0, cy0, cw, ch);
                if (_glass != null && _keyHash == hash && _keyW == gw && _keyH == gh
                    && _keyCropX == cx0 && _keyCropY == cy0 && _keyCropW == cw && _keyCropH == ch
                    && MathF.Abs(_keySx - src.Left) < 0.5f && MathF.Abs(_keySy - src.Top) < 0.5f
                    && MathF.Abs(_keyLw - logicalW) < 0.5f && MathF.Abs(_keyLh - logicalH) < 0.5f
                    && MathF.Abs(_keyRt - rTop) < 0.5f && MathF.Abs(_keyRb - rBottom) < 0.5f)
                {
                    _keyVersion = version;   // 内容一致 → 认下这个版本，下一帧连哈希都不用比
                    _statHash++;
                    return _glass;
                }

                BuildBlur((byte*)pxs, istride, iw, ih, cx0, cy0, cw, ch, cB, cG, cR, version,
                    out int bcw, out int bch);

                var bmp = new SKBitmap(new SKImageInfo(gw, gh, SKColorType.Bgra8888, SKAlphaType.Premul));
                byte* dstBase = (byte*)bmp.GetPixels();
                int dstStride = bmp.RowBytes;
                // 模糊缓冲可能是降采样后的尺寸，采样端统一用它自己的宽高与缩放比
                int blurStride = bcw * 4;
                float invBs = 1f / _blurScale;

                fixed (byte* blur = _blurB)
                {
                    if (blur == null) { bmp.Dispose(); return null; }

                    float hsx = logicalW * 0.5f, hsy = logicalH * 0.5f;
                    float pxX = gw / logicalW, pxY = gh / logicalH;   // 玻璃图层 px / 逻辑 px

                    // ── 刘海凹角 ──
                    // hsx 是整张玻璃图层的半宽（含左右外扩），本体只到 ±halfW。
                    // SDF 的「矩形」必须用本体半宽，否则外扩带会被算成玻璃内部 —— 那正是
                    // 「切到刘海、再开液态玻璃，顶角还是灵动岛的凸圆角」的成因。
                    float halfW = hsx - flareX;
                    if (halfW < 1f) halfW = 1f;
                    bool notch = flareX > 0.5f && flareY > 0.5f;

                    // 刘海贴合屏幕顶边：顶边不是玻璃的边界（不该淡出、也不该有一条横向高光）。
                    // 做法是把 SDF 的中心整体下移 K，等价于把顶边推到无穷远 —— 底边位置不变。
                    float sdfDy = flushTop ? 4096f : 0f;
                    float sdfHy = hsy + sdfDy;

                    float minHs = MathF.Min(halfW, hsy);

                    // ── 折射自适应：按元素短边限制折射带与位移 ──
                    // 这是"边缘白字变成整条渐变亮带"的根治。
                    // demo 里元素 800x600（半高 300），refractionAmount=50、refractionHeight=20，
                    // 相对半高只有 16.7% / 6.7%；而折叠态胶囊半高只有 20，若照搬 12px 位移就是 60%
                    // —— 玻璃内部会被"搬进"外侧半屏的内容，于是边缘外的一行白字
                    //    被整条拉成从边缘向中心渐变的亮带（看着就是条纹，不像玻璃）。
                    // 所以位移与折射带都必须按短边缩放，让任何尺寸下比例都落在合理区间。
                    float rhEff = RefractionHeight, raEff = RefractionAmount;
                    float cap = minHs * 0.35f;
                    if (cap < rhEff) rhEff = cap;
                    cap = minHs * 0.20f;
                    if (cap < raEff) raEff = cap;

                    // 放大：以玻璃中心为原点收缩采样坐标（>1 = 背景被放大）。
                    // 中心固定，所以放大不会让画面整体漂移，只是内容变"胖"。
                    float invZoom = 1f / Zoom;
                    float zoomCx = (src.Left + hsx * imgPerX - cx0) * invBs;
                    float zoomCy = (src.Top + hsy * imgPerY - cy0) * invBs;

                    // ⚠️ 两个指针都必须先转成 IntPtr 才能进并行：
                    //    · fixed 指针不能被 lambda / 局部函数捕获（生命周期受 fixed 块限制）；
                    //    · 闭包是普通 class，**字段不能是指针类型**，直接捕获 dstBase 编译不过。
                    //    换来的是多核：玻璃区典型几万~26 万像素，并行能省一半以上时间。
                    IntPtr blurPtr = (IntPtr)blur;
                    IntPtr dstPtr = (IntPtr)dstBase;

                    void RenderRows(int y0, int y1)
                    {
                        byte* blurLocal = (byte*)blurPtr;
                        byte* dstBaseL = (byte*)dstPtr;

                        for (int gy = y0; gy < y1; gy++)
                        {
                            float ly = (gy + 0.5f) / pxY;
                            float ccy = ly - hsy;
                            byte* row = dstBaseL + gy * dstStride;

                            for (int gx = 0; gx < gw; gx++)
                            {
                                int p = gx * 4;
                                float lx = (gx + 0.5f) / pxX;
                                float ccx = lx - hsx;

                                float cyS = ccy + sdfDy;
                                float r = RadiusAt(ccx, cyS, rTop, rTop, rBottom, rBottom);
                                float sd = SdRoundedRect(ccx, cyS, halfW, sdfHy, r);

                                // ── 刘海凹角（外扩带）──
                                // 外扩带的边界就是 Renderer 里那条二次贝塞尔：
                                //     x(t) = L - F(1-t)²,  y(t) = f·t²      （t∈[0,1]）
                                // 它跟「半径 F 的圆弧」差得很远（中点差 6~7px），所以按圆弧算会明显偏胖。
                                // 这里直接按贝塞尔逐像素求边界，只有顶角那一小条会走这段（几十行×几十列）。
                                bool bandWin = false;
                                float bnx = 0f, bny = 0f;
                                if (notch && ccy < -hsy + flareY)
                                {
                                    float acx = ccx < 0f ? -ccx : ccx;
                                    float bandY = ccy + hsy;                    // 0 .. flareY，从顶边往下量
                                    if (bandY < 0f) bandY = 0f;
                                    float tp = MathF.Sqrt(bandY / flareY);      // 曲线参数 t = sqrt(y/f)
                                    if (tp > 1f) tp = 1f;
                                    float w1 = 1f - tp;
                                    float xb = halfW + flareX * w1 * w1;        // 该行的边界 |x|
                                    float tgx = flareX * w1, tgy = flareY * tp; // 切向
                                    float tgl = MathF.Sqrt(tgx * tgx + tgy * tgy);
                                    float nxv = tgl > 1e-4f ? tgy / tgl : 1f;   // 法线（|x| 方向分量）
                                    float nyv = tgl > 1e-4f ? tgx / tgl : 0f;   // 法线（y 分量，正 = 朝下）
                                    // 水平距离 × 法线 x 分量 ≈ 垂直距离；顶边附近曲线接近水平，
                                    // nxv→0 会把整行算成"刚好在边界上"（整条顶边半透明），所以夹个下限。
                                    float kx = nxv < 0.35f ? 0.35f : nxv;
                                    float fb = (acx - xb) * kx;
                                    if (!flushTop)
                                    {
                                        float topEdge = -hsy - ccy;             // 顶边约束（非贴合时才存在）
                                        if (topEdge > fb) fb = topEdge;
                                    }
                                    if (fb < sd)
                                    {
                                        sd = fb;
                                        bandWin = true;
                                        bnx = ccx < 0f ? -nxv : nxv;
                                        bny = nyv;
                                    }
                                }

                                // 边缘羽化：对齐 demo 的 edgeAA = 1 - smoothstep(-1, 1, sd)。
                                // 原来是 1 像素线性过渡，比 demo 窄一半且是硬折线 —— 边缘显硬，
                                // 动画时那圈锯齿还会一跳一跳的。换成 demo 同款 2 像素 smoothstep。
                                float edge = 1f - SmoothStep(-1f, 1f, sd * pxY);
                                if (edge <= 0.001f)
                                {
                                    row[p] = row[p + 1] = row[p + 2] = row[p + 3] = 0;
                                    continue;
                                }
                                if (edge > 1f) edge = 1f;

                                // 深度：边缘 0，往里越深越负
                                float sdi = sd < 0f ? sd : 0f;
                                float t = 1f + sdi / rhEff;
                                if (t < 0f) t = 0f;
                                // circleMap：边缘位移最大，往里平滑收敛到 0
                                float d = (1f - MathF.Sqrt(1f - t * t)) * raEff;

                                // ── 中心衰减：去掉中部的「拉丝」──
                                // circleMap 的尾巴很长：深度到 2 倍折射带时 d 仍有峰值的百分之几，
                                // 而中部的法线几乎全靠 DepthEffect 的径向分量撑着 —— 这点位移虽小，
                                // 却被整片中部均匀共享，看起来就是一层横向拉丝/水波纹。
                                // 折射本来就只该发生在边缘那圈；往里超过 1.6 个折射带宽度就彻底归零。
                                // ⚠️ 用折射带宽度（玻璃厚度）为尺度，所以调「玻璃厚度」时衰减范围跟着走。
                                float depth = -sdi;

                                // ── 中部快路径 ──
                                // centerFade 在 depth >= rhEff 时精确为 0：折射位移、色散、边缘高光全是 0，
                                // 再算法线（GradSd + 两次 sqrt + 归一化）纯属白算。中部占面积九成以上，
                                // 跳过它能把每像素的数学开销砍掉大半，观感完全一致。
                                if (depth >= rhEff)
                                {
                                    float bx = (src.Left + lx * imgPerX - cx0) * invBs;
                                    float by = (src.Top + ly * imgPerY - cy0) * invBs;
                                    if (invZoom != 1f)
                                    {
                                        bx = zoomCx + (bx - zoomCx) * invZoom;
                                        by = zoomCy + (by - zoomCy) * invZoom;
                                    }
                                    float cr = 0f, cg = 0f, cb = 0f;
                                    AccumSample(blurLocal, bcw, bch, blurStride, bx, by, 1f, 1f, 1f, ref cr, ref cg, ref cb);
                                    FinishColor(ref cr, ref cg, ref cb, 0f);
                                    WritePixel(row, p, cr, cg, cb, edge, DitherAt(gx, gy));
                                    continue;
                                }
                                float centerFade = 1f - SmoothStep(rhEff * 0.3f, rhEff * 1.0f, depth);
                                d *= centerFade;

                                float disp = (ccx * ccy) / (halfW * hsy);

                                // 法线：外扩带用凹角自己的（沿曲线法向），其余走圆角矩形的梯度。
                                // 凹角的法线在顶边附近指向"屏幕外"，所以边缘高光会自然衰减到 0，
                                // 不会在贴着屏幕的那条边上刷出一道横贯全宽的亮线。
                                float ggx, ggy;
                                if (bandWin)
                                {
                                    ggx = bnx;
                                    ggy = bny;
                                }
                                else
                                {
                                    GradSdRoundedRect(ccx, cyS, halfW, sdfHy,
                                        MathF.Min(r * 1.5f, minHs), out ggx, out ggy);
                                }
                                float ln = MathF.Sqrt(ccx * ccx + ccy * ccy) + 0.000001f;
                                ggx += DepthEffect * (ccx / ln);
                                ggy += DepthEffect * (ccy / ln);
                                float gl = MathF.Sqrt(ggx * ggx + ggy * ggy) + 0.000001f;
                                ggx /= gl;
                                ggy /= gl;

                                // ⚠️ invBs 不能少：模糊缓冲可能是 2 倍降采样后的，
                                //    少了这一步会采到两倍远的位置（玻璃内容整体错位）。
                                float baseX = (src.Left + (lx + d * ggx) * imgPerX - cx0) * invBs;
                                float baseY = (src.Top + (ly + d * ggy) * imgPerY - cy0) * invBs;
                                float dxi = d * ggx * disp * Dispersion * imgPerX * invBs;
                                float dyi = d * ggy * disp * Dispersion * imgPerY * invBs;

                                // 放大：采样点朝中心收缩 → 采到的范围变小 → 视觉上被放大。
                                // 中心固定，所以放大不会让画面整体漂移。
                                if (invZoom != 1f)
                                {
                                    baseX = zoomCx + (baseX - zoomCx) * invZoom;
                                    baseY = zoomCy + (baseY - zoomCy) * invZoom;
                                    dxi *= invZoom;
                                    dyi *= invZoom;
                                }

                                float ar = 0f, ag = 0f, ab = 0f;

                                // 内部（位移≈0）时多次采样完全重合 → 只采一次，省下其余双线性
                                if (dxi > -0.25f && dxi < 0.25f && dyi > -0.25f && dyi < 0.25f)
                                {
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX, baseY, 1f, 1f, 1f, ref ar, ref ag, ref ab);
                                }
                                else if (_samples < 7)
                                {
                                    // 流畅档：每通道只采一次（R 前 / G 中 / B 后），色散更糙但省 4 次采样
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX + dxi, baseY + dyi,
                                        1f, 0f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX, baseY,
                                        0f, 1f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX - dxi, baseY - dyi,
                                        0f, 0f, 1f, ref ar, ref ag, ref ab);
                                }
                                else
                                {
                                    // 与 GlassDispersionPS 完全同权：R 1.0 / G 1.0 / B 1.0
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX + dxi, baseY + dyi,
                                        1f / 3.5f, 0f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX + dxi * (2f / 3f), baseY + dyi * (2f / 3f),
                                        1f / 3.5f, 1f / 7f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX + dxi / 3f, baseY + dyi / 3f,
                                        1f / 3.5f, 1f / 3.5f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX, baseY,
                                        0f, 1f / 3.5f, 0f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX - dxi / 3f, baseY - dyi / 3f,
                                        0f, 1f / 3.5f, 1f / 3f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX - dxi * (2f / 3f), baseY - dyi * (2f / 3f),
                                        0f, 0f, 1f / 3f, ref ar, ref ag, ref ab);
                                    AccumSample(blurLocal, bcw, bch, blurStride, baseX - dxi, baseY - dyi,
                                        1f / 7f, 0f, 1f / 3f, ref ar, ref ag, ref ab);
                                }

                                // 边缘反光：上边缘最亮（光从上方来），下边缘几乎不亮
                                float rim = RimStrength * MathF.Exp(sdi / RimWidth) * MathF.Max(0f, 0.55f - 0.75f * ggy);
                                FinishColor(ref ar, ref ag, ref ab, rim);
                                WritePixel(row, p, ar, ag, ab, edge, DitherAt(gx, gy));
                            }
                        }
                    }

                    // 图太小就别起线程了：Parallel.For 自身的调度开销比算这点像素还贵
                    if (gw * gh >= ParallelMinPixels && Environment.ProcessorCount > 1)
                    {
                        int chunks = Math.Min(Environment.ProcessorCount, Math.Max(1, gh / 4));
                        System.Threading.Tasks.Parallel.For(0, chunks,
                            ci => RenderRows(gh * ci / chunks, gh * (ci + 1) / chunks));
                    }
                    else RenderRows(0, gh);
                }

                if (_glass != null) _glass.Dispose();
                _glass = bmp;
                _keyVersion = version;
                _keyW = gw;
                _keyH = gh;

                // 玻璃中心区平均亮度：每帧重算（很便宜），供「玻璃下文字自适应黑白」用，
                // 下面那条诊断日志复用同一组值。命中缓存的帧不进这里，亮度停在旧值 —— 内容没变，亮度也不会变。
                long sr = 0, sg = 0, sb = 0;
                int n = 0;
                {
                    byte* bp0 = (byte*)bmp.GetPixels();
                    int smpX0 = gw / 3, smpY0 = gh / 3, smpX1 = gw * 2 / 3, smpY1 = gh * 2 / 3;
                    for (int j = smpY0; j < smpY1; j += 2)
                        for (int i = smpX0; i < smpX1; i += 2)
                        {
                            int p = (j * gw + i) * 4;
                            sb += bp0[p]; sg += bp0[p + 1]; sr += bp0[p + 2];
                            n++;
                        }
                    if (n == 0) n = 1;
                    _glassLum = (int)(((sb / n) * 29 + (sg / n) * 150 + (sr / n) * 77) >> 8);
                }

                // 诊断：玻璃内容到底黑没黑，一行定位 —— src 错位 / 背板黑 / 合成黑 / 核被乘成 0，一眼分清
                if (float.IsNaN(_diagSx) || MathF.Abs(_diagSx - src.Left) > 1f
                    || MathF.Abs(_diagSy - src.Top) > 1f || _diagW != gw)
                {
                    _diagSx = src.Left; _diagSy = src.Top; _diagW = gw;

                    // 模糊缓冲的平均色。和"玻璃平均"并排看：
                    //   模糊≈0 + 玻璃≈45 → 高斯核把整块图乘成了 0（权重表没算过，见 SetRadius）
                    //   模糊有内容 + 玻璃≈45 → 合成/着色那一段的问题
                    long fr = 0, fg = 0, fb = 0;
                    int fn = 0;
                    int fstepX = Math.Max(1, bcw / 16), fstepY = Math.Max(1, bch / 8);
                    fixed (byte* fblur = _blurB)
                    {
                        if (fblur != null)
                            for (int j = 0; j < bch; j += fstepY)
                                for (int i = 0; i < bcw; i += fstepX)
                                {
                                    byte* q = fblur + j * blurStride + i * 4;
                                    fb += q[0]; fg += q[1]; fr += q[2];
                                    fn++;
                                }
                    }
                    if (fn == 0) fn = 1;

                    Logger.Info($"[玻璃] src=({src.Left:F0},{src.Top:F0} {src.Width:F0}x{src.Height:F0})"
                        + $" glass={gw}x{gh} 中心平均=({sr / n},{sg / n},{sb / n})"
                        + $" 模糊平均=({fr / fn},{fg / fn},{fb / fn}) 核半径={_radius} 权重={(_weightsValid ? "有效" : "空")}");
                }
                _keySx = src.Left;
                _keySy = src.Top;
                _keyLw = logicalW;
                _keyLh = logicalH;
                _keyRt = rTop;
                _keyRb = rBottom;
                _keyHash = hash;
                _keyCropX = cx0;
                _keyCropY = cy0;
                _keyCropW = cw;
                _keyCropH = ch;
                _statBuild++;
                return bmp;
            }
        }

        // ---- ① 缓动跟随（对应 demo 的 g_smoothSrc 插值）----
        // 只缓动【源矩形的位置】，不动几何尺寸：玻璃层最终是按当前胶囊尺寸拉伸贴进去的，
        // 尺寸要是缓动，折射带（refractionHeight）会被一起拉伸 —— 胶囊从 35 展开到 300 时
        // 那条 14px 的折射带会被拉成 100 多 px，直接穿帮。位置缓动则完全安全。
        private static void ApplyEase(ref SKRect src)
        {
            float tx = src.Left, ty = src.Top;

            if (float.IsNaN(_easeSx) || float.IsNaN(_easeSy)
                || MathF.Abs(tx - _easeSx) > EaseSnapPx || MathF.Abs(ty - _easeSy) > EaseSnapPx)
            {
                // 首帧 / 突变（换显示器、DPI 变化、从隐藏恢复）：立刻对齐，别缓上去
                _easeSx = tx;
                _easeSy = ty;
                return;
            }

            _easeSx += (tx - _easeSx) * EaseSpeed;
            _easeSy += (ty - _easeSy) * EaseSpeed;

            src = new SKRect(_easeSx, _easeSy, _easeSx + src.Width, _easeSy + src.Height);
        }

        // ---- ③ 裁切区抽样哈希（FNV-1a 64）----
        // 每 4 行、每 4 个像素取一次：既不会漏掉桌面内容的变化，又比真算一遍便宜得多。
        private static ulong HashCrop(byte* src, int stride, int x0, int y0, int w, int h)
        {
            const ulong prime = 1099511628211UL;
            ulong acc = 1469598103934665603UL;
            for (int j = 0; j < h; j += 4)
            {
                byte* row = src + (y0 + j) * stride + x0 * 4;
                for (int i = 0; i < w; i += 4)
                {
                    int p = i * 4;
                    acc = (acc ^ row[p]) * prime;
                    acc = (acc ^ row[p + 1]) * prime;
                    acc = (acc ^ row[p + 2]) * prime;
                }
            }
            return acc;
        }

        // ---- demo 的 smoothstep（HLSL 内建，C# 没有）----
        private static float SmoothStep(float e0, float e1, float x)
        {
            float t = (x - e0) / (e1 - e0);
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return t * t * (3f - 2f * t);
        }

        private static int AlignDown(int v, int grid) => (int)MathF.Floor(v / (float)grid) * grid;

        private static int AlignUp(int v, int grid) => (int)MathF.Ceiling(v / (float)grid) * grid;

        // ---- 可分离高斯：只糊裁切区，边缘夹取（对应 demo 的水平/垂直两趟）----
        private static void BuildBlur(byte* src, int srcStride, int iw, int ih,
                                     int cx0, int cy0, int cw, int ch, int cB, int cG, int cR, int version,
                                     out int outW, out int outH)
        {
            // ── 降采样后再模糊 ──
            // 模糊是整次重算里最贵的一步（实测占九成）。半径 >= 6 时先把裁切区 2×2 盒式降采样，
            // 抽头数同步减半（R/2），等效模糊半径保持不变 —— 结果几乎一致，工作量却是 1/8。
            // ⚠️ 阈值从 4 提到 6：整张玻璃（包括中部没折射的部分）采的都是这张模糊图，
            //    默认档 4px 也降采样等于把"清晰的玻璃"再糊掉一半分辨率。
            int bs = _radius >= 6 ? 2 : 1;
            _blurScale = bs;

            int bw = (cw + bs - 1) / bs;
            int bh = (ch + bs - 1) / bs;
            outW = bw;
            outH = bh;

            // 权重表跟着 _radius 走（SetRadius 重建）。取局部引用再进 fixed：
            // 半径刚变大、权重还没重建的那一帧直接用旧图，别拿短表当长表索引。
            int R = _radius / bs;
            if (R < 1) R = 1;
            float[] weights = _weights;
            bool skipKernel = _radius <= 0 || cw * ch > MaxBlurPixels;
            if (!skipKernel && weights.Length < R + 1) return;

            int need = Math.Max(cw * ch * 4, bw * bh * 4);
            if (_blurA.Length < need) _blurA = new byte[need];
            if (_blurB.Length < need) _blurB = new byte[need];
            else if (_blurA.Length > need * 4 && _blurA.Length > (1 << 20))
            {
                // 岛体从大展开态收回折叠态后，裁切区会小一个数量级 —— 但数组只涨不缩，
                // 那几 MB 会一直挂在进程里。超过实际用量 4 倍且大于 1MB 就按当前尺寸重开。
                _blurA = new byte[need];
                _blurB = new byte[need];
            }

            // 缩放比与半径都要进缓存键：否则换模糊档位 / 换抓屏倍率而裁切区恰巧没变时，
            // 会拿旧尺度的数据配上新尺度的输出宽高，采样整体错位。
            if (_blurValid && _blurVersion == version && _blurX == cx0 && _blurY == cy0
                && _blurW == cw && _blurH == ch && _blurScaleKey == bs && _blurRadiusKey == _radius)
                return;

            fixed (byte* pa = _blurA)
            fixed (byte* pb = _blurB)
            fixed (float* wp = weights)
            {
                if (pa == null || pb == null || wp == null) return;

                if (skipKernel)
                {
                    // 模糊档位设成 0（最清晰），或超大岛体跳过核：直接搬像素，不做降采样。
                    // 后者靠抓屏那一步的盒式降采样 / 放大镜 1:1，已经够用。
                    _blurScale = 1;
                    outW = cw;
                    outH = ch;
                    for (int j = 0; j < ch; j++)
                    {
                        int sy = cy0 + j;
                        if (sy < 0) sy = 0; else if (sy > ih - 1) sy = ih - 1;
                        Buffer.MemoryCopy(src + sy * srcStride + cx0 * 4, pb + j * cw * 4, cw * 4, cw * 4);
                    }
                }
                else
                {
                    // 等效半径：降采样 bs 倍后抽头数同比减少，模糊观感不变（R 已在函数开头算好）
                    int taps = R * 2 + 1;

                    // 把边缘夹取从内层循环里挪出去：原来每个抽头都要 clamp 一次
                    // （分支 + 地址不可预测的 gather），现在内层是一条直线扫描。
                    int padW = bw + taps;
                    if (_pad.Length < padW * 4) _pad = new byte[padW * 4];
                    if (_rowMap.Length < bh + taps) _rowMap = new int[bh + taps];

                    // ① 2×2 盒式降采样（bs=1 时就是原样拷贝）
                    int downNeed = bw * bh * 4;
                    if (_down.Length < downNeed) _down = new byte[downNeed];
                    fixed (byte* pdn = _down)
                    {
                        if (pdn == null) return;
                        if (bs == 1)
                        {
                            for (int j = 0; j < bh; j++)
                            {
                                int sy = cy0 + j;
                                if (sy < 0) sy = 0; else if (sy > ih - 1) sy = ih - 1;
                                byte* q = src + sy * srcStride + cx0 * 4;
                                byte* d = pdn + j * bw * 4;
                                for (int i = 0; i < bw; i++)
                                {
                                    d[i * 4] = q[i * 4 + cB];
                                    d[i * 4 + 1] = q[i * 4 + cG];
                                    d[i * 4 + 2] = q[i * 4 + cR];
                                    d[i * 4 + 3] = 255;
                                }
                            }
                        }
                        else
                        {
                            for (int j = 0; j < bh; j++)
                            {
                                byte* d = pdn + j * bw * 4;
                                int y0 = cy0 + j * 2, y1 = y0 + 1;
                                if (y0 < 0) y0 = 0; else if (y0 > ih - 1) y0 = ih - 1;
                                if (y1 < 0) y1 = 0; else if (y1 > ih - 1) y1 = ih - 1;
                                byte* r0 = src + y0 * srcStride;
                                byte* r1 = src + y1 * srcStride;
                                for (int i = 0; i < bw; i++)
                                {
                                    int x0 = cx0 + i * 2, x1 = x0 + 1;
                                    if (x1 > cx0 + cw - 1) x1 = cx0 + cw - 1;
                                    int a0 = x0 * 4, a1 = x1 * 4;
                                    d[i * 4] = (byte)((r0[a0 + cB] + r0[a1 + cB] + r1[a0 + cB] + r1[a1 + cB]) >> 2);
                                    d[i * 4 + 1] = (byte)((r0[a0 + cG] + r0[a1 + cG] + r1[a0 + cG] + r1[a1 + cG]) >> 2);
                                    d[i * 4 + 2] = (byte)((r0[a0 + cR] + r0[a1 + cR] + r1[a0 + cR] + r1[a1 + cR]) >> 2);
                                    d[i * 4 + 3] = 255;
                                }
                            }
                        }
                    }

                    fixed (byte* pdn = _down)
                    fixed (byte* ppad = _pad)
                    fixed (int* pmap = _rowMap)
                    {
                        if (pdn == null || ppad == null || pmap == null) return;

                        for (int k = 0; k < bh + taps; k++)
                        {
                            int y = k - R;
                            _rowMap[k] = (y < 0 ? 0 : (y > bh - 1 ? bh - 1 : y)) * bw * 4;
                        }

                        // ② 水平
                        for (int j = 0; j < bh; j++)
                        {
                            byte* rowSrc = pdn + j * bw * 4;
                            for (int k = 0; k < padW; k++)
                            {
                                int sx = k - R;
                                if (sx < 0) sx = 0; else if (sx > bw - 1) sx = bw - 1;
                                byte* q = rowSrc + sx * 4;
                                int o = k * 4;
                                ppad[o] = q[0];
                                ppad[o + 1] = q[1];
                                ppad[o + 2] = q[2];
                            }

                            byte* rowDst = pa + j * bw * 4;
                            for (int i = 0; i < bw; i++)
                            {
                                byte* w0 = ppad + i * 4;        // 连续窗口起点
                                float accB = 0f, accG = 0f, accR = 0f;
                                for (int t = 0; t < taps; t++)
                                {
                                    int dt = t - R;
                                    float wt = wp[dt < 0 ? -dt : dt];
                                    int o = t * 4;
                                    accB += w0[o] * wt;
                                    accG += w0[o + 1] * wt;
                                    accR += w0[o + 2] * wt;
                                }
                                int o2 = i * 4;
                                rowDst[o2] = ClampByte(accB);
                                rowDst[o2 + 1] = ClampByte(accG);
                                rowDst[o2 + 2] = ClampByte(accR);
                                rowDst[o2 + 3] = 255;
                            }
                        }

                        // ③ 垂直
                        for (int j = 0; j < bh; j++)
                        {
                            byte* rowDst = pb + j * bw * 4;
                            for (int i = 0; i < bw; i++)
                            {
                                float accB = 0f, accG = 0f, accR = 0f;
                                for (int t = 0; t < taps; t++)
                                {
                                    byte* q = pa + pmap[j + t] + i * 4;
                                    int dt = t - R;
                                    float wt = wp[dt < 0 ? -dt : dt];
                                    accB += q[0] * wt;
                                    accG += q[1] * wt;
                                    accR += q[2] * wt;
                                }
                                int o = i * 4;
                                rowDst[o] = ClampByte(accB);
                                rowDst[o + 1] = ClampByte(accG);
                                rowDst[o + 2] = ClampByte(accR);
                                rowDst[o + 3] = 255;
                            }
                        }
                    }
                }
            }

            _blurValid = true;
            _blurScaleKey = bs;
            _blurRadiusKey = _radius;
            _blurVersion = version;
            _blurX = cx0;
            _blurY = cy0;
            _blurW = cw;
            _blurH = ch;
        }

        // ── 8bit 输出的色带（banding）打散 ──
        // 大范围高斯模糊出来是一整片平滑渐变，256 级输出必然分层：那圈一圈圈的"条纹"就是它。
        // ±0.5LSB 的 4×4 有序抖动把色阶换成看不见的噪点，亮度均值不变、不改变任何几何。
        private static readonly float[] Bayer4 =
        {
             0f,  8f,  2f, 10f,
            12f,  4f, 14f,  6f,
             3f, 11f,  1f,  9f,
            15f,  7f, 13f,  5f
        };

        private static float DitherAt(int x, int y) => (Bayer4[((y & 3) << 2) | (x & 3)] * (1f / 16f)) - 0.5f;

        // 玻璃着色末段（慢路径与中部快路径共用）
        // lum 是采样到的原始亮度：拿它做**暗场散射补偿** —— 真实玻璃不会因为背后是黑墙就变成
        // 一块死黑，它会把散射回来的光补上一点。背板越暗掺入的玻璃本色越多（最多 +0.10），
        // 暗色壁纸下玻璃仍是一层雾面；亮环境补偿为 0，观感不变。
        private static void FinishColor(ref float ar, ref float ag, ref float ab, float rim)
        {
            float lum = ar * 0.213f + ag * 0.715f + ab * 0.072f;
            ar = lum + (ar - lum) * Saturation;
            ag = lum + (ag - lum) * Saturation;
            ab = lum + (ab - lum) * Saturation;
            ar *= Darkening;
            ag *= Darkening;
            ab *= Darkening;

            float tint = TintAlpha;
            if (lum < 96f) tint += (96f - lum) * (0.10f / 96f);
            ar += (255f - ar) * tint;
            ag += (255f - ag) * tint;
            ab += (255f - ab) * tint;

            if (rim > 0f)
            {
                ar += (255f - ar) * rim;
                ag += (255f - ag) * rim;
                ab += (255f - ab) * rim;
            }
        }

        // edge = 边缘羽化后的 alpha；抖动按 alpha 缩放，边缘不会出现噪点圈
        private static void WritePixel(byte* row, int p, float ar, float ag, float ab, float edge, float dith)
        {
            float a = 255f * edge;
            float k = a / 255f;
            float ds = dith * k;
            row[p] = ClampByte(ab * k + ds);        // B
            row[p + 1] = ClampByte(ag * k + ds);    // G
            row[p + 2] = ClampByte(ar * k + ds);    // R
            row[p + 3] = (byte)a;
        }

        private static byte ClampByte(float v) => v < 0f ? (byte)0 : (v > 255f ? (byte)255 : (byte)v);

        // 工作区统一按 B,G,R,A 排布，采样端不用再管源图的通道顺序
        private static void AccumSample(byte* buf, int w, int h, int stride, float fx, float fy,
                                        float wr, float wg, float wb, ref float ar, ref float ag, ref float ab)
        {
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 < 0) x1 = 0;
            if (y1 < 0) y1 = 0;
            if (x0 > w - 1) x0 = w - 1;
            if (x1 > w - 1) x1 = w - 1;
            if (y0 > h - 1) y0 = h - 1;
            if (y1 > h - 1) y1 = h - 1;

            byte* p00 = buf + y0 * stride + x0 * 4;
            byte* p10 = buf + y0 * stride + x1 * 4;
            byte* p01 = buf + y1 * stride + x0 * 4;
            byte* p11 = buf + y1 * stride + x1 * 4;

            float itx = 1f - tx, ity = 1f - ty;
            float b = (p00[0] * itx + p10[0] * tx) * ity + (p01[0] * itx + p11[0] * tx) * ty;
            float g = (p00[1] * itx + p10[1] * tx) * ity + (p01[1] * itx + p11[1] * tx) * ty;
            float r = (p00[2] * itx + p10[2] * tx) * ity + (p01[2] * itx + p11[2] * tx) * ty;

            ar += r * wr;
            ag += g * wg;
            ab += b * wb;
        }

        // ---- SDF（HLSL SDF_COMMON 直译）----

        // radii: 左上 / 右上 / 右下 / 左下
        private static float RadiusAt(float cx, float cy, float tl, float tr, float br, float bl)
        {
            if (cx >= 0f) return cy <= 0f ? tr : br;
            return cy <= 0f ? tl : bl;
        }

        private static float SdRoundedRect(float cx, float cy, float hsx, float hsy, float r)
        {
            float qx = MathF.Abs(cx) - (hsx - r);
            float qy = MathF.Abs(cy) - (hsy - r);
            float mx = qx > 0f ? qx : 0f;
            float my = qy > 0f ? qy : 0f;
            float outside = MathF.Sqrt(mx * mx + my * my) - r;
            float inside = MathF.Min(MathF.Max(qx, qy), 0f);
            return outside + inside;
        }

        private static void GradSdRoundedRect(float cx, float cy, float hsx, float hsy, float r, out float gx, out float gy)
        {
            float qx = MathF.Abs(cx) - (hsx - r);
            float qy = MathF.Abs(cy) - (hsy - r);
            float sx = cx >= 0f ? 1f : -1f;
            float sy = cy >= 0f ? 1f : -1f;

            if (qx > 0f || qy > 0f)
            {
                float mx = qx > 0f ? qx : 0f;
                float my = qy > 0f ? qy : 0f;
                float len = MathF.Sqrt(mx * mx + my * my);
                if (len > 0f)
                {
                    gx = sx * mx / len;
                    gy = sy * my / len;
                    return;
                }
            }

            float step = qy <= qx ? 1f : 0f;
            gx = sx * step;
            gy = sy * (1f - step);
        }
    }
}
