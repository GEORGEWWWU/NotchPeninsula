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

        // 抓屏刷新间隔（ms）：越短越跟手，代价是更频繁地读屏幕
        private static readonly int[] CaptureMsTable = { 1000, 500, 250, 160, 60 };

        // 岛体渲染帧率（fps）→ 帧间隔 ms
        private static readonly int[] FrameFpsTable = { 30, 60, 90, 120, 144 };

        /// <summary>后台抓屏线程的刷新节拍（ms）。</summary>
        public static int RefreshMs => CaptureMsTable[_levels[P_CaptureHz]];

        /// <summary>渲染线程的帧间隔（ms）。渲染循环每帧读一次，所以改完立刻生效。</summary>
        public static int FrameIntervalMs => 1000 / FrameFpsTable[_levels[P_FrameRate]];

        /// <summary>某根滑轨 5 个档位各自的显示文本（数值型参数显示具体值，其余显示强弱）。</summary>
        public static string[] ValueTexts(int index)
        {
            switch (index)
            {
                case P_Blur: return ["不模糊", "2", "4", "6", "10"];
                case P_CaptureHz: return ["1s", "0.5s", "250", "160", "60"];
                case P_FrameRate: return ["30", "60", "90", "120", "144"];
                case P_Zoom: return ["1.0x", "1.08x", "1.18x", "1.32x", "1.5x"];
                default: return ["最低", "偏低", "标准", "偏高", "最高"];
            }
        }

        // 所有滑块统一 5 档（0..4），与背景透明度那根滑轨同口径
        public const int MaxLevel = 4;

        // 各档默认值（下标 = 上面的 P_*）：刷新默认 250ms、帧率默认 60fps，都是表中下标 2
        private static readonly int[] _levels = { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };

        public static int GetLevel(int index) => index >= 0 && index < ParamCount ? _levels[index] : 0;

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

        // ---- 高斯模糊（抓屏图像素口径；demo 里是 15-tap 可分离高斯）----
        private static int _radius = 6;

        private static float _sigma = 2.2f;

        public static int BlurRadius => _radius;

        private static void ApplyBlur()
        {
            _radius = BlurRadiusTable[_levels[P_Blur]];
            _sigma = _radius <= 0 ? 0f : _radius / 2.7f;
            _weights = new float[Math.Max(1, _radius + 1)];
            if (_radius > 0) ComputeGaussianWeights(_sigma, _radius, _weights);
        }

        // ---- 质量档：抓屏倍率 / 色散采样数 / 玻璃层上限 / 刷新间隔 ----

        private static int _quality = 1;      // 0=流畅 1=均衡 2=精细

        public static int Quality
        {
            get => _quality;
            set
            {
                int q = value < 0 ? 0 : (value > 2 ? 2 : value);
                if (q == _quality) return;
                _quality = q;
                ApplyQuality();
            }
        }

        public static string QualityName => _quality switch { 0 => "流畅", 1 => "均衡", _ => "精细" };

        // GDI 抓屏降采样（放大镜抓屏固定 1:1，不受它影响）：
        // 折射位移要看得出来，不能像亚克力那样糊到 1/12
        private static float _downscale = 2f;

        public static float CaptureDownscale => _downscale;

        // 玻璃图层分辨率上限：超出后按面积等比降采样（内容本来就模糊，看不出来）
        private static int _maxGlassPixels = 90000;

        // 色散采样数：7 次（R/G/B 各一整套）或 3 次（每通道只采一次）
        private static int _samples = 7;

        // 质量档只管「抓屏倍率 / 玻璃层上限 / 色散采样数」——
        // 刷新间隔交给 P_CaptureHz 单独控制，免得两个控件抢同一个数。
        private static void ApplyQuality()
        {
            switch (_quality)
            {
                case 0:                       // 流畅：抓屏更糊、色散只采 3 次
                    _downscale = 3f;
                    _maxGlassPixels = 40000;
                    _samples = 3;
                    break;
                case 2:                       // 精细：1:1 抓屏 + 全分辨率玻璃层
                    _downscale = 1f;
                    _maxGlassPixels = 260000;
                    _samples = 7;
                    break;
                default:                      // 均衡
                    _downscale = 2f;
                    _maxGlassPixels = 90000;
                    _samples = 7;
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

        // 命中统计：诊断里能直接看到「缓存命中 / 内容未变跳过 / 真重算」的比例
        private static int _statCache, _statHash, _statBuild;

        public static string Stats => $"重算 {_statBuild} / 内容未变跳过 {_statHash} / 缓存命中 {_statCache}";

        public static void ResetStats() { _statCache = _statHash = _statBuild = 0; }

        private static float[] _weights = new float[7];

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
        /// <param name="logicalW">胶囊逻辑宽度</param>
        /// <param name="logicalH">胶囊逻辑高度</param>
        /// <param name="rTop">顶部圆角（逻辑像素）</param>
        /// <param name="rBottom">底部圆角（逻辑像素）</param>
        /// <returns>玻璃图层；null 表示这台机器读不到像素，调用方应退回普通模糊背板</returns>
        public static SKBitmap? Compose(SKImage backdrop, int version, SKRect src,
                                       float logicalW, float logicalH, float rTop, float rBottom)
        {
            if (logicalW < 8f || logicalH < 4f) return null;
            if (src.Width < 4f || src.Height < 2f) return null;

            int gw = (int)MathF.Round(src.Width);
            int gh = (int)MathF.Round(src.Height);
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
                    float minHs = MathF.Min(hsx, hsy);

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

                                float r = RadiusAt(ccx, ccy, rTop, rTop, rBottom, rBottom);
                                float sd = SdRoundedRect(ccx, ccy, hsx, hsy, r);

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
                                float centerFade = 1f - SmoothStep(rhEff * 0.3f, rhEff * 1.0f, depth);
                                d *= centerFade;

                                float disp = (ccx * ccy) / (hsx * hsy);

                                GradSdRoundedRect(ccx, ccy, hsx, hsy, MathF.Min(r * 1.5f, minHs), out float ggx, out float ggy);
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

                                // 饱和度提升 → 压暗 → 掺玻璃本色
                                float lum = ar * 0.213f + ag * 0.715f + ab * 0.072f;
                                ar = lum + (ar - lum) * Saturation;
                                ag = lum + (ag - lum) * Saturation;
                                ab = lum + (ab - lum) * Saturation;
                                ar *= Darkening;
                                ag *= Darkening;
                                ab *= Darkening;
                                ar += (255f - ar) * TintAlpha;
                                ag += (255f - ag) * TintAlpha;
                                ab += (255f - ab) * TintAlpha;

                                // 边缘反光：上边缘最亮（光从上方来），下边缘几乎不亮
                                float rim = RimStrength * MathF.Exp(sdi / RimWidth) * MathF.Max(0f, 0.55f - 0.75f * ggy);
                                if (rim > 0f)
                                {
                                    ar += (255f - ar) * rim;
                                    ag += (255f - ag) * rim;
                                    ab += (255f - ab) * rim;
                                }

                                if (ar < 0f) ar = 0f; else if (ar > 255f) ar = 255f;
                                if (ag < 0f) ag = 0f; else if (ag > 255f) ag = 255f;
                                if (ab < 0f) ab = 0f; else if (ab > 255f) ab = 255f;

                                float a = 255f * edge;
                                float k = a / 255f;
                                row[p] = (byte)(ab * k);          // B
                                row[p + 1] = (byte)(ag * k);      // G
                                row[p + 2] = (byte)(ar * k);      // R
                                row[p + 3] = (byte)a;
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
            // 模糊是整次重算里最贵的一步（实测占九成）。半径 >= 4 时先把裁切区 2×2 盒式降采样，
            // 抽头数同步减半（R/2），等效模糊半径保持不变 —— 结果几乎一致，工作量却是 1/8：
            // 像素少 4 倍 × 抽头少 2 倍。半径 <= 2（用户要清晰）时保持 1:1，一点细节都不丢。
            int bs = _radius >= 4 ? 2 : 1;
            _blurScale = bs;

            int bw = (cw + bs - 1) / bs;
            int bh = (ch + bs - 1) / bs;
            outW = bw;
            outH = bh;

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

            // 缩放比也要进缓存键：否则换模糊档位（bs 1↔2）而裁切区恰巧没变时，
            // 会拿旧尺度的数据配上新尺度的输出宽高，采样整体错位一倍。
            if (_blurValid && _blurVersion == version && _blurX == cx0 && _blurY == cy0
                && _blurW == cw && _blurH == ch && _blurScaleKey == bs)
                return;

            fixed (byte* pa = _blurA)
            fixed (byte* pb = _blurB)
            fixed (float* wp = _weights)
            {
                if (pa == null || pb == null || wp == null) return;

                if (_radius <= 0 || cw * ch > MaxBlurPixels)
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
                    // 等效半径：降采样 bs 倍后抽头数同比减少，模糊观感不变
                    int R = _radius / bs;
                    if (R < 1) R = 1;
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
            _blurVersion = version;
            _blurX = cx0;
            _blurY = cy0;
            _blurW = cw;
            _blurH = ch;
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
