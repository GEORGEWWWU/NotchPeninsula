using System;
using SkiaSharp;

namespace NotchPeninsula
{
    // 液态玻璃背景（Liquid Glass）—— C++ / HLSL 版 Liquid Glass Demo 的纯 C# + SkiaSharp 移植。
    //
    // 管线与 demo 一一对应：
    //   1. 抓屏      ：IslandBackdrop（玻璃模式降采样 1/2，亚克力 1/12）
    //   2. 可分离高斯 ：ComputeGaussianWeights + 水平/垂直两趟（对应 BlurH_PS / BlurV_PS）
    //   3. 玻璃本体   ：SDF 圆角矩形 → 边缘折射位移 + RGB 色散 + 饱和度提升 + 玻璃本色
    //                  （对应 GlassDispersionPS 的 sdRoundedRect / gradSdRoundedRect / circleMap）
    //
    // 为什么不用 SKRuntimeEffect（SKSL）：
    //   岛体画布是 CPU 光栅面（SKSurface.Create 直写位图），没有 GPU 上下文，SKSL 也是走
    //   SkRasterPipeline 逐像素算；而 demo 的 7 次采样在胶囊这么小的一块上（典型 300x30）
    //   纯 C# 也就 1ms 量级，还省掉一层运行时编译与后端差异。代价是要自己做缓存 —— 见下面
    //   「玻璃图层缓存」：静态时零成本，只有几何/抓屏变了才重算。
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

        public const int ParamCount = 9;

        public static readonly string[] ParamNames =
        {
            "背景模糊", "折射强度", "边缘色散", "边缘高光", "玻璃厚度", "透镜感", "饱和度", "玻璃雾度", "明度"
        };

        public static readonly string[] ParamKeys =
        {
            "LiquidGlassBlur", "LiquidGlassRefraction", "LiquidGlassDispersion", "LiquidGlassRim",
            "LiquidGlassThickness", "LiquidGlassDepth", "LiquidGlassSaturation", "LiquidGlassFrost",
            "LiquidGlassBrightness"
        };

        // 所有滑块统一 5 档（0..4），与背景透明度那根滑轨同口径
        public const int MaxLevel = 4;

        // 各档默认值（下标 = 上面的 P_*）
        private static readonly int[] _levels = { 2, 2, 2, 2, 2, 2, 2, 2, 2 };

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

        // 抓屏刷新间隔：质量档越高跟手越好，代价是屏幕读取更频繁
        private static int _refreshMs = 250;

        public static int RefreshMs => _refreshMs;

        private static void ApplyQuality()
        {
            switch (_quality)
            {
                case 0:                       // 流畅：抓屏更糊、色散只采 3 次、刷新最慢
                    _downscale = 3f;
                    _maxGlassPixels = 40000;
                    _samples = 3;
                    _refreshMs = 400;
                    break;
                case 2:                       // 精细：1:1 抓屏 + 全分辨率玻璃层 + 刷新最快
                    _downscale = 1f;
                    _maxGlassPixels = 260000;
                    _samples = 7;
                    _refreshMs = 160;
                    break;
                default:                      // 均衡
                    _downscale = 2f;
                    _maxGlassPixels = 90000;
                    _samples = 7;
                    _refreshMs = 250;
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

            // 命中缓存：抓屏没换、几何没动 → 直接复用上一帧的玻璃
            if (_glass != null && _keyVersion == version && _keyW == gw && _keyH == gh
                && MathF.Abs(_keySx - src.Left) < 0.5f && MathF.Abs(_keySy - src.Top) < 0.5f
                && MathF.Abs(_keyLw - logicalW) < 0.5f && MathF.Abs(_keyLh - logicalH) < 0.5f
                && MathF.Abs(_keyRt - rTop) < 0.5f && MathF.Abs(_keyRb - rBottom) < 0.5f)
                return _glass;

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

                BuildBlur((byte*)pxs, istride, iw, ih, cx0, cy0, cw, ch, cB, cG, cR, version);

                var bmp = new SKBitmap(new SKImageInfo(gw, gh, SKColorType.Bgra8888, SKAlphaType.Premul));
                byte* dstBase = (byte*)bmp.GetPixels();
                int dstStride = bmp.RowBytes;
                int blurStride = cw * 4;

                fixed (byte* blur = _blurB)
                {
                    if (blur == null) { bmp.Dispose(); return null; }

                    float hsx = logicalW * 0.5f, hsy = logicalH * 0.5f;
                    float pxX = gw / logicalW, pxY = gh / logicalH;   // 玻璃图层 px / 逻辑 px
                    float minHs = MathF.Min(hsx, hsy);

                    for (int gy = 0; gy < gh; gy++)
                    {
                        float ly = (gy + 0.5f) / pxY;
                        float ccy = ly - hsy;
                        byte* row = dstBase + gy * dstStride;

                        for (int gx = 0; gx < gw; gx++)
                        {
                            int p = gx * 4;
                            float lx = (gx + 0.5f) / pxX;
                            float ccx = lx - hsx;

                            float r = RadiusAt(ccx, ccy, rTop, rTop, rBottom, rBottom);
                            float sd = SdRoundedRect(ccx, ccy, hsx, hsy, r);

                            // 1 玻璃像素宽的边缘羽化（外层还有路径裁切的抗锯齿）
                            float edge = 0.5f - sd * pxY;
                            if (edge <= 0f)
                            {
                                row[p] = row[p + 1] = row[p + 2] = row[p + 3] = 0;
                                continue;
                            }
                            if (edge > 1f) edge = 1f;

                            // 深度：边缘 0，往里越深越负
                            float sdi = sd < 0f ? sd : 0f;
                            float t = 1f + sdi / RefractionHeight;
                            if (t < 0f) t = 0f;
                            // circleMap：边缘位移最大，往里平滑收敛到 0
                            float d = (1f - MathF.Sqrt(1f - t * t)) * RefractionAmount;

                            float disp = (ccx * ccy) / (hsx * hsy);

                            GradSdRoundedRect(ccx, ccy, hsx, hsy, MathF.Min(r * 1.5f, minHs), out float ggx, out float ggy);
                            float ln = MathF.Sqrt(ccx * ccx + ccy * ccy) + 0.000001f;
                            ggx += DepthEffect * (ccx / ln);
                            ggy += DepthEffect * (ccy / ln);
                            float gl = MathF.Sqrt(ggx * ggx + ggy * ggy) + 0.000001f;
                            ggx /= gl;
                            ggy /= gl;

                            float baseX = src.Left + (lx + d * ggx) * imgPerX - cx0;
                            float baseY = src.Top + (ly + d * ggy) * imgPerY - cy0;
                            float dxi = d * ggx * disp * Dispersion * imgPerX;
                            float dyi = d * ggy * disp * Dispersion * imgPerY;

                            float ar = 0f, ag = 0f, ab = 0f;

                            // 内部（位移≈0）时多次采样完全重合 → 只采一次，省下其余双线性
                            if (dxi > -0.25f && dxi < 0.25f && dyi > -0.25f && dyi < 0.25f)
                            {
                                AccumSample(blur, cw, ch, blurStride, baseX, baseY, 1f, 1f, 1f, ref ar, ref ag, ref ab);
                            }
                            else if (_samples < 7)
                            {
                                // 流畅档：每通道只采一次（R 前 / G 中 / B 后），色散更糙但省 4 次采样
                                AccumSample(blur, cw, ch, blurStride, baseX + dxi, baseY + dyi,
                                    1f, 0f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX, baseY,
                                    0f, 1f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX - dxi, baseY - dyi,
                                    0f, 0f, 1f, ref ar, ref ag, ref ab);
                            }
                            else
                            {
                                // 与 GlassDispersionPS 完全同权：R 1.0 / G 1.0 / B 1.0
                                AccumSample(blur, cw, ch, blurStride, baseX + dxi, baseY + dyi,
                                    1f / 3.5f, 0f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX + dxi * (2f / 3f), baseY + dyi * (2f / 3f),
                                    1f / 3.5f, 1f / 7f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX + dxi / 3f, baseY + dyi / 3f,
                                    1f / 3.5f, 1f / 3.5f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX, baseY,
                                    0f, 1f / 3.5f, 0f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX - dxi / 3f, baseY - dyi / 3f,
                                    0f, 1f / 3.5f, 1f / 3f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX - dxi * (2f / 3f), baseY - dyi * (2f / 3f),
                                    0f, 0f, 1f / 3f, ref ar, ref ag, ref ab);
                                AccumSample(blur, cw, ch, blurStride, baseX - dxi, baseY - dyi,
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
                return bmp;
            }
        }

        private static int AlignDown(int v, int grid) => (int)MathF.Floor(v / (float)grid) * grid;

        private static int AlignUp(int v, int grid) => (int)MathF.Ceiling(v / (float)grid) * grid;

        // ---- 可分离高斯：只糊裁切区，边缘夹取（对应 demo 的水平/垂直两趟）----
        private static void BuildBlur(byte* src, int srcStride, int iw, int ih,
                                     int cx0, int cy0, int cw, int ch, int cB, int cG, int cR, int version)
        {
            int need = cw * ch * 4;
            if (_blurA.Length < need) _blurA = new byte[need];
            if (_blurB.Length < need) _blurB = new byte[need];

            if (_blurValid && _blurVersion == version && _blurX == cx0 && _blurY == cy0 && _blurW == cw && _blurH == ch)
                return;

            fixed (byte* pa = _blurA)
            fixed (byte* pb = _blurB)
            fixed (float* wp = _weights)
            {
                if (pa == null || pb == null || wp == null) return;

                if (_radius <= 0 || cw * ch > MaxBlurPixels)
                {
                    // 模糊档位设成 0（最清晰），或超大岛体跳过核：直接搬像素。
                    // 后者靠抓屏那一步的盒式降采样 / 放大镜 1:1，已经够用。
                    for (int j = 0; j < ch; j++)
                    {
                        int sy = cy0 + j;
                        if (sy < 0) sy = 0; else if (sy > ih - 1) sy = ih - 1;
                        Buffer.MemoryCopy(src + sy * srcStride + cx0 * 4, pb + j * cw * 4, cw * 4, cw * 4);
                    }
                }
                else
                {
                    int R = _radius;

                    // 水平
                    for (int j = 0; j < ch; j++)
                    {
                        int sy = cy0 + j;
                        if (sy < 0) sy = 0; else if (sy > ih - 1) sy = ih - 1;
                        byte* rowSrc = src + sy * srcStride;
                        byte* rowDst = pa + j * cw * 4;
                        for (int i = 0; i < cw; i++)
                        {
                            float accB = 0f, accG = 0f, accR = 0f;
                            for (int t = -R; t <= R; t++)
                            {
                                int sx = cx0 + i + t;
                                if (sx < cx0) sx = cx0; else if (sx > cx0 + cw - 1) sx = cx0 + cw - 1;
                                byte* q = rowSrc + sx * 4;
                                float wt = wp[t < 0 ? -t : t];
                                accB += q[cB] * wt;
                                accG += q[cG] * wt;
                                accR += q[cR] * wt;
                            }
                            int o = i * 4;
                            rowDst[o] = ClampByte(accB);
                            rowDst[o + 1] = ClampByte(accG);
                            rowDst[o + 2] = ClampByte(accR);
                            rowDst[o + 3] = 255;
                        }
                    }

                    // 垂直
                    for (int j = 0; j < ch; j++)
                    {
                        byte* rowDst = pb + j * cw * 4;
                        for (int i = 0; i < cw; i++)
                        {
                            float accB = 0f, accG = 0f, accR = 0f;
                            for (int t = -R; t <= R; t++)
                            {
                                int sy = j + t;
                                if (sy < 0) sy = 0; else if (sy > ch - 1) sy = ch - 1;
                                byte* q = pa + sy * cw * 4 + i * 4;
                                float wt = wp[t < 0 ? -t : t];
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

            _blurValid = true;
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
