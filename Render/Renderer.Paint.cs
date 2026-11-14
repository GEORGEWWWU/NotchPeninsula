using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        private static readonly SKPaint _layerPaint = new SKPaint(); // 零GC硬件级透明图层

        private static bool _acrylicLogged;

        private static readonly SKPaint _wakePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true }; // 折角箭头（线条）

        private static readonly SKPaint _wakeChipPaint = new SKPaint { Color = SKColors.Black.WithAlpha(WakeChipAlpha), Style = SKPaintStyle.Fill, IsAntialias = true }; // 芯片底色

        private static readonly SKPaint _wakeHitPaint = new SKPaint { Style = SKPaintStyle.Fill }; // 隐形物理热区底板

        private static readonly SKPath _wakePath = CreateWakePath();

        private static readonly SKPath _wakeChipPath = CreateWakeChipPath();

        private const byte WakeChipAlpha = 158;

        // 穿透唤醒按钮的边长（逻辑坐标）。

        public const float WAKE_BTN_SIZE = 36f;

        public static float WakeButtonX => (WINDOW_WIDTH - WAKE_BTN_SIZE) / 2f;

        private static SKPath CreateWakeChipPath()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(5f, 5f, 31f, 31f), 8f, 8f);
            return path;
        }

        // 也避免两段斜线连成一根粗斜杠（那样就认不出是箭头了）。

        private static SKPath CreateWakePath()
        {
            var path = new SKPath();

            path.MoveTo(24.50f, 18.00f);  // 竖直臂末端（落在水平中线）
            path.LineTo(24.50f, 11.50f);  // 尖角
            path.LineTo(18.00f, 11.50f);  // 水平臂末端（落在垂直中线）

            // 左下折角：上面的点对称
            path.MoveTo(11.50f, 18.00f);
            path.LineTo(11.50f, 24.50f);
            path.LineTo(18.00f, 24.50f);

            return path;
        }

        private static readonly SKPaint _hoverCirclePaint = new() { IsAntialias = true }; // 零 GC 纯色画笔

        private static SKColor _currentTextColor = SKColors.White;

        private static SKColor _currentSubTextColor = new SKColor(200, 200, 200);

        // 液态玻璃下的文字黑白状态：
        //   _glassTextIsLight   —— 迟滞判定的目标值（Renderer.Draw 每帧按玻璃亮度推进）
        //   _glassTextKnown     —— 是否已经拿到过有效亮度；没有就沿用主题色，别拿"还没测"当"纯黑"
        //   _paletteIsLight     —— 画笔上**实际**生效的明暗，唯一写入点是 ApplyThemeColors。
        //                          任何来源写画笔（UI 线程改主题、渲染线程判亮度）都会同步它，
        //                          这样"画笔状态"和"目标状态"不会各记一套而对不上。
        private static volatile bool _glassTextIsLight;
        private static volatile bool _glassTextKnown;
        private static volatile bool _paletteIsLight;

        // 亮度平滑：0..1，只在渲染线程读写
        private static float _glassLumSmooth = 0.5f;

        // 明暗翻转阈值（归一化亮度，gamma 空间 Rec.709）。
        // ⚠️ 分界整体偏上：中亮彩色底（亮蓝 / 彩色壁纸，采样值约 0.55~0.70）配**白字** ——
        // 旧分界 0.44/0.58 时，从亮处切到这类底，黑字态的平滑值收敛到带内就永远翻不回白
        // （0.65 够不到 0.44 的翻白线），观感就是「浅色背景却一直黑字」。
        // 现在只有非常亮的底（白 / 近白壁纸，>0.78）才用黑字，其余一律白字；
        // 0.70~0.78 保留一段滞回挡抖动。
        private const float GlassLightAt = 0.78f;

        private const float GlassDarkAt = 0.70f;

        // 每帧朝目标收敛的比例：换壁纸 / 展开成卡片时约 10 帧（≈160ms）走完，不做硬切。
        private const float GlassToneLerp = 0.22f;

        /// <summary>玻璃下文字当前是否按「浅底」处理（= 黑字）。诊断面板用。</summary>
        public static bool GlassTextIsLight => _glassTextIsLight;

        /// <summary>平滑后的玻璃亮度（0..1）。诊断面板用。</summary>
        public static float GlassToneLevel => _glassLumSmooth;

        /// <summary>
        /// 按玻璃成品的平均亮度重定岛上文字黑白。必须每帧调（哪怕是缓存命中的帧）。
        /// </summary>
        /// <param name="lum">玻璃成品中心平均亮度 0..255。</param>
        /// <param name="valid">这个亮度是否来自一张真正合成出来的玻璃图。</param>
        internal static void UpdateGlassTextTone(int lum, bool valid)
        {
            if (valid)
            {
                float t = lum <= 0 ? 0f : (lum >= 255 ? 1f : lum / 255f);
                _glassLumSmooth += (t - _glassLumSmooth) * GlassToneLerp;
                if (!_glassTextIsLight && _glassLumSmooth > GlassLightAt) _glassTextIsLight = true;
                else if (_glassTextIsLight && _glassLumSmooth < GlassDarkAt) _glassTextIsLight = false;
                _glassTextKnown = true;
            }

            // 不能只在"判定值翻过阈值"那一帧重绑画笔 —— 那要求 _paletteIsLight 始终等于判定值，
            // 而它会被 UI 线程的主题切换、材质切换改成主题色。一旦两者悄悄错开
            // （浅色主题 + 暗玻璃、实体切回玻璃…），文字就会一直用错颜色，直到亮度跨过一次阈值。
            // 改成每帧比对"画笔实际明暗"和"目标明暗"，任何来源的偏差下一帧就自愈。
            if (_glassTextKnown && _glassTextIsLight != _paletteIsLight)
                ApplyThemeColors();
        }

        // ── 元素级文字明暗 ──
        //
        // 以前整块玻璃只有一个明暗状态：要么全黑字、要么全白字。壁纸横向从深到浅时
        // 总有一半文字压在错的那半边底色上（左边白字糊在黑底、右边黑字糊在白底）。
        //
        // 现在每个元素按自己下方那块玻璃的亮度决定黑白：
        //   每帧把背板（隐身抓到的真实桌面）降采样成一张 24×6 的亮度网格，
        //   查询时按元素矩形取覆盖格的平均 —— 144 个采样点，比逐像素便宜得多，
        //   而且天然平滑，不会出现「同一个字一半黑一半白」。
        // 网格与上一帧插值一次：背板 250ms 才换一张，硬切会看到文字颜色跳一下。
        private const int ToneGridX = 24;

        private const int ToneGridY = 6;

        private static readonly float[] _toneGrid = new float[ToneGridX * ToneGridY];

        private static readonly float[] _toneGridPrev = new float[ToneGridX * ToneGridY];

        private static bool _toneReady;

        private static bool _tonePrimed;

        /// <summary>本帧岛体亮度网格的平均值（0..1）。亚克力/实体用它决定整块调色，玻璃仍用中心亮度。</summary>
        private static float _toneGridAverage;

        private static float _toneDstL, _toneDstT, _toneDstW, _toneDstH;

        private static float _toneSrcL, _toneSrcT, _toneSrcW, _toneSrcH;

        // 建立网格那一刻的画布矩阵。插件可能自己 Translate/Scale 后再排版，查询时用
        // 「当前矩阵 → 基准矩阵的逆」把文字矩形换回网格坐标系（见 PluginTone）。
        private static SKMatrix _toneBase = SKMatrix.Identity;

        // 实况窗详情页面板是**岛外**的另一块玻璃（背的是另一块桌面），不能借岛体那张网格判黑白。
        // 面板绘制期间把上下文切到这套字段，文字自动变色的逻辑完全复用同一份采样 / 判定代码。
        private static readonly float[] _panelToneGrid = new float[ToneGridX * ToneGridY];

        private static readonly float[] _panelToneGridPrev = new float[ToneGridX * ToneGridY];

        private static bool _panelToneReady;

        private static bool _panelTonePrimed;

        private static bool _toneUsePanel;

        private static float _panelToneDstL, _panelToneDstT, _panelToneDstW, _panelToneDstH;

        private static SKMatrix _panelToneBase = SKMatrix.Identity;

        // 面板绘制期间临时切到「面板整体明暗」的全局调色，画完原样还回去。
        private static bool _panelPaletteOverridden;

        private static SKColor _panelSavedText, _panelSavedSub, _panelSavedIcon, _panelSavedHover;

        // 元素级判定阈值：亮过它就用黑字。
        // 全局那套仍是 0.78/0.70 的滞回；元素级单值就够 —— 网格本身已经平滑过。
        // 0.55 太低：深色面板（压在深色标题栏上的玻璃）亮度常在 0.55~0.60，会被误判成浅底、
        // 黑字压深底；0.62 又会让浅灰面板（玻璃补偿后 0.60~0.62）继续白字。0.60 是两者的分界。
        private const float ToneLightAt = 0.60f;

        // ---- 公开给插件的逐元素明暗查询（PluginTone 用） ----

        /// <summary>当前帧是否有可用的亮度网格（三种材质都有：实体填纯色、亚克力/玻璃采样背板）。</summary>
        internal static bool PluginToneReady => _toneUsePanel ? _panelToneReady : _toneReady;

        /// <summary>本帧岛体网格平均亮度（0..1）；没有网格时 0。</summary>
        internal static float ToneGridAverage => _toneGridAverage;

        /// <summary>当前是否在实况窗面板绘制期（PluginTone 诊断用）。</summary>
        internal static bool PluginTonePanel => _toneUsePanel;

        /// <summary>面板网格是否可用（PluginTone 诊断用）。</summary>
        internal static bool PluginTonePanelReady => _panelToneReady;

        /// <summary>元素级判亮阈值：玻璃亮度高于它按浅底（黑字）处理。</summary>
        internal const float PluginToneLightAt = ToneLightAt;

        /// <summary>
        /// 插件传入的矩形是它自己画布坐标系里的（可能被 Translate/Scale 过）。
        /// 先用当前画布矩阵映射到宿主坐标，再用建网格时的基准矩阵逆映射回网格坐标。
        /// 没有网格时返回 -1，调用方回落到主题色。
        /// </summary>
        internal static float PluginToneAt(SKCanvas canvas, SKRect area)
        {
            if (!PluginToneReady) return -1f;

            // 实况窗详情页面板的网格本来就是「面板局部坐标」：页面在那里排版用的也是局部坐标，
            // 直接查即可。再经过画布矩阵映射反而会把局部坐标变成屏幕绝对坐标，落到网格外。
            if (_toneUsePanel) return ToneAt(area);

            SKRect mapped = canvas.TotalMatrix.MapRect(area);
            SKMatrix b = _toneBase;
            if (b.TryInvert(out SKMatrix inv)) mapped = inv.MapRect(mapped);
            return ToneAt(mapped);
        }

        /// <summary>
        /// 本帧重新采样「文字底色亮度网格」。三种材质都建：
        ///   · 实体 —— 主题纯色，整张网格填同一个亮度；
        ///   · 亚克力 —— 背板 + 主岛涂层（按涂层 alpha 混合）；
        ///   · 玻璃 —— 背板 + 玻璃明度 / 雾度着色。
        /// 背板为空（玻璃 / 亚克力）时置 _toneReady=false，所有元素回落到全局文字色。
        /// </summary>
        private static void BeginToneGrid(SKCanvas canvas, SKImage? backdrop, SKRect dst, SKRect src)
        {
            _toneBase = canvas.TotalMatrix;
            _toneGridAverage = 0f;
            _toneReady = SampleToneGrid(backdrop, dst, src, _toneGrid, _toneGridPrev, ref _tonePrimed,
                IslandMaterialIndex);
            if (!_toneReady) return;
            _toneDstL = dst.Left; _toneDstT = dst.Top; _toneDstW = dst.Width; _toneDstH = dst.Height;
            _toneSrcL = src.Left; _toneSrcT = src.Top; _toneSrcW = src.Width; _toneSrcH = src.Height;
            _toneGridAverage = Average(_toneGrid);
        }

        /// <summary>
        /// 实况窗详情页：面板在岛外、背的是另一块桌面，用自己的亮度建一张同规格的网格。
        /// 采样 / 平滑 / 着色补偿全部复用 <see cref="SampleToneGrid"/>，判定仍走 <see cref="ToneText"/>。
        /// </summary>
        internal static void BeginPanelToneGrid(SKCanvas canvas, SKImage? backdrop, SKRect dst, SKRect src)
        {
            _panelToneBase = canvas.TotalMatrix;
            _toneUsePanel = false;
            _panelToneReady = SampleToneGrid(backdrop, dst, src, _panelToneGrid, _panelToneGridPrev,
                ref _panelTonePrimed, IslandMaterialIndex);
            if (!_panelToneReady)
            {
                // 面板网格建不起来（背板映射失败 / 没抓到像素）时也必须把「面板作用域」打开：
                // 否则 PluginTone 会退到主岛网格，把面板局部坐标当岛体坐标查，颜色完全随机。
                bool hasGlassLum = IslandAnyGlass
                    && (IslandLiquidGlass2 ? LiquidGlass2.HasLuminance : LiquidGlass.HasLuminance);
                float lum = hasGlassLum
                    ? (IslandLiquidGlass2 ? LiquidGlass2.GlassLuminance : LiquidGlass.GlassLuminance) / 255f
                    : Luminance(_bgPaint.Color);
                lum = Math.Clamp(lum, 0f, 1f);
                for (int i = 0; i < _panelToneGrid.Length; i++) _panelToneGrid[i] = lum;
                Array.Copy(_panelToneGrid, _panelToneGridPrev, _panelToneGrid.Length);
                _panelTonePrimed = true;
                _panelToneReady = true;
            }

            _panelToneDstL = dst.Left; _panelToneDstT = dst.Top; _panelToneDstW = dst.Width; _panelToneDstH = dst.Height;
            _toneUsePanel = true;

            ApplyPanelPaletteAverage(Average(_panelToneGrid));
        }

        /// <summary>
        /// 实况窗面板专用：直接采样**合成后的玻璃位图**，用于内置媒体面板。
        /// 背板映射只反映桌面原样，玻璃合成（尤其上半压在深色标题栏上）会把实际明暗改掉；
        /// 位图就是用户看到的那张图，逐格采样最准。
        /// </summary>
        internal static void BeginPanelToneGridBitmap(SKCanvas canvas, SKBitmap glass, SKRect dst)
        {
            _panelToneBase = canvas.TotalMatrix;
            _toneUsePanel = false;
            _panelToneReady = false;

            if (dst.Width < 4f || dst.Height < 4f) return;

            using var pm = glass.PeekPixels();
            if (pm == null) return;
            IntPtr basePtr = pm.GetPixels();
            if (basePtr == IntPtr.Zero) return;

            SKColorType ct = pm.ColorType;
            if (ct != SKColorType.Bgra8888 && ct != SKColorType.Rgba8888) return;
            int bOff = ct == SKColorType.Bgra8888 ? 0 : 2;
            int rOff = ct == SKColorType.Bgra8888 ? 2 : 0;
            int stride = pm.RowBytes, pw = pm.Width, ph = pm.Height;
            if (pw < 1 || ph < 1) return;

            bool hadPrev = _panelTonePrimed;
            for (int gy = 0; gy < ToneGridY; gy++)
            {
                int py = Math.Clamp((int)((gy + 0.5f) / ToneGridY * ph), 0, ph - 1);
                IntPtr row = basePtr + py * stride;
                for (int gx = 0; gx < ToneGridX; gx++)
                {
                    int px = Math.Clamp((int)((gx + 0.5f) / ToneGridX * pw), 0, pw - 1);
                    int i = px * 4;
                    float b = Marshal.ReadByte(row, i + bOff);
                    float g = Marshal.ReadByte(row, i + 1);
                    float r = Marshal.ReadByte(row, i + rOff);
                    float lum = (0.213f * r + 0.715f * g + 0.072f * b) * (1f / 255f);

                    int idx = gy * ToneGridX + gx;
                    _panelToneGrid[idx] = hadPrev
                        ? _panelToneGridPrev[idx] + (lum - _panelToneGridPrev[idx]) * 0.35f
                        : lum;
                }
            }

            Array.Copy(_panelToneGrid, _panelToneGridPrev, _panelToneGrid.Length);
            _panelTonePrimed = true;
            _panelToneReady = true;
            _panelToneDstL = dst.Left; _panelToneDstT = dst.Top; _panelToneDstW = dst.Width; _panelToneDstH = dst.Height;
            _toneUsePanel = true;

            ApplyPanelPalette(Average(_panelToneGrid));
        }

        /// <summary>面板绘制期间，插件文字统一取面板整体明暗（含滞回）。</summary>
        internal static SKColor PanelToneText(byte alpha, bool sub)
            => (sub ? _currentSubTextColor : _currentTextColor).WithAlpha(alpha);

        /// <summary>面板调色滞回：只有明确跨过 0.65 / 0.55 才翻转，挡住平均亮度抖动。</summary>
        private static bool _panelPaletteLight;
        private static bool _panelPaletteKnown;

        private static void ApplyPanelPaletteAverage(float average)
        {
            bool light;
            if (!_panelPaletteKnown)
            {
                light = average > ToneLightAt;
                _panelPaletteKnown = true;
            }
            else if (_panelPaletteLight && average < ToneLightAt - 0.05f) light = false;
            else if (!_panelPaletteLight && average > ToneLightAt + 0.05f) light = true;
            else light = _panelPaletteLight;

            _panelPaletteLight = light;
            _panelSavedText = _currentTextColor;
            _panelSavedSub = _currentSubTextColor;
            _panelSavedIcon = _mediaIconPaint.Color;
            _panelSavedHover = _hoverCirclePaint.Color;
            _currentTextColor = light ? SKColors.Black : SKColors.White;
            _currentSubTextColor = light ? new SKColor(55, 55, 55) : new SKColor(180, 180, 180);
            _mediaIconPaint.Color = _currentTextColor;
            _hoverCirclePaint.Color = _currentTextColor.WithAlpha(25);
            _panelPaletteOverridden = true;
        }

        /// <summary>面板调色（ApplyPanelPalette 的旧入口名，保留给位图采样路径）。</summary>
        private static void ApplyPanelPalette(float average) => ApplyPanelPaletteAverage(average);

        internal static void EndPanelToneGrid()
        {
            _toneUsePanel = false;
            _panelPaletteKnown = false;
            if (!_panelPaletteOverridden) return;
            _panelPaletteOverridden = false;
            _currentTextColor = _panelSavedText;
            _currentSubTextColor = _panelSavedSub;
            _mediaIconPaint.Color = _panelSavedIcon;
            _hoverCirclePaint.Color = _panelSavedHover;
        }

        /// <summary>
        /// 把一块底色降采样成 24×6 亮度网格（岛体 / 实况窗面板共用同一份采样与平滑）。
        /// 三种材质各有各的「成品亮度」算法：
        ///   · 实体（material = 0）：主题纯色，整张网格填同一个亮度；
        ///   · 亚克力（1）：背板像素按涂层 alpha 与主题底色混合；
        ///   · 玻璃（2 / 3）：背板像素乘明度补偿、再掺玻璃雾度（v1 / v2 各用自己的参数）。
        /// 返回 false = 玻璃 / 亚克力拿不到背板（或像素格式不支持），调用方回落到全局文字色。
        /// </summary>
        private static bool SampleToneGrid(SKImage? backdrop, SKRect dst, SKRect src,
            float[] grid, float[] prev, ref bool primed, int material)
        {
            // primed 表示「上一帧已经有网格」；先取出来再清，否则下面的插值永远走不到。
            bool hadPrev = primed;
            primed = false;
            if (dst.Width < 4f || dst.Height < 4f) return false;

            // 实体：底就是主题纯色，不需要背板。整张网格同亮度，逐元素查询仍然可用。
            if (material == 0)
            {
                float lum = Luminance(_bgPaint.Color);
                for (int i = 0; i < grid.Length; i++)
                    grid[i] = hadPrev ? prev[i] + (lum - prev[i]) * 0.35f : lum;
                Array.Copy(grid, prev, grid.Length);
                primed = true;
                return true;
            }

            if (backdrop == null || src.Width < 4f || src.Height < 4f) return false;

            using var pm = backdrop.PeekPixels();
            if (pm == null) return false;
            IntPtr basePtr = pm.GetPixels();
            if (basePtr == IntPtr.Zero) return false;

            SKColorType ct = pm.ColorType;
            if (ct != SKColorType.Bgra8888 && ct != SKColorType.Rgba8888) return false;
            int bOff = ct == SKColorType.Bgra8888 ? 0 : 2;
            int rOff = ct == SKColorType.Bgra8888 ? 2 : 0;
            int stride = pm.RowBytes;
            int pw = pm.Width, ph = pm.Height;
            if (pw < 1 || ph < 1) return false;

            bool acrylic = material == 1;

            // 亚克力：背板 + 主岛涂层（_bgPaint 的 RGB 与 alpha）。
            // 玻璃：背板 + 明度补偿 / 雾度掺白。当前只有一档液态玻璃（2.0 已下线，分支保留但不会命中）。
            float coatAlpha = 0f, coatLum = 0f;
            float dark = 1f, tint = 0f;
            if (acrylic)
            {
                SKColor coat = _bgPaint.Color;
                coatAlpha = coat.Alpha * (1f / 255f);
                coatLum = Luminance(coat);
            }
            else
            {
                dark = IslandLiquidGlass2 ? LiquidGlass2.Darkening : LiquidGlass.Darkening;
                tint = IslandLiquidGlass2 ? LiquidGlass2.TintAlpha : LiquidGlass.TintAlpha;
            }

            for (int gy = 0; gy < ToneGridY; gy++)
            {
                int py = (int)(src.Top + src.Height * (gy + 0.5f) / ToneGridY);
                if (py < 0) py = 0; else if (py >= ph) py = ph - 1;
                IntPtr row = basePtr + py * stride;
                for (int gx = 0; gx < ToneGridX; gx++)
                {
                    int px = (int)(src.Left + src.Width * (gx + 0.5f) / ToneGridX);
                    if (px < 0) px = 0; else if (px >= pw) px = pw - 1;
                    int i = px * 4;
                    float b = Marshal.ReadByte(row, i + bOff);
                    float g = Marshal.ReadByte(row, i + 1);
                    float r = Marshal.ReadByte(row, i + rOff);
                    float lum = (0.213f * r + 0.715f * g + 0.072f * b) * (1f / 255f);

                    if (acrylic)
                    {
                        lum = lum * (1f - coatAlpha) + coatLum * coatAlpha;
                    }
                    else
                    {
                        lum *= dark;
                        lum += (1f - lum) * tint;
                    }
                    if (lum < 0f) lum = 0f; else if (lum > 1f) lum = 1f;

                    int idx = gy * ToneGridX + gx;
                    grid[idx] = hadPrev
                        ? prev[idx] + (lum - prev[idx]) * 0.35f
                        : lum;
                }
            }

            Array.Copy(grid, prev, grid.Length);
            primed = true;
            return true;
        }

        /// <summary>BT.601 亮度，0..1。</summary>
        private static float Luminance(SKColor c)
            => (0.213f * c.Red + 0.715f * c.Green + 0.072f * c.Blue) * (1f / 255f);

        private static float Average(float[] values)
        {
            if (values.Length == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < values.Length; i++) sum += values[i];
            return sum / values.Length;
        }

        /// <summary>取某个矩形覆盖的网格平均亮度；没有网格时返回 -1（调用方回落到全局判定）。</summary>
        internal static float ToneAt(SKRect area)
        {
            bool panel = _toneUsePanel;
            bool ready = panel ? _panelToneReady : _toneReady;
            float dstL = panel ? _panelToneDstL : _toneDstL;
            float dstT = panel ? _panelToneDstT : _toneDstT;
            float dstW = panel ? _panelToneDstW : _toneDstW;
            float dstH = panel ? _panelToneDstH : _toneDstH;
            float[] grid = panel ? _panelToneGrid : _toneGrid;
            if (!ready || dstW < 1f || dstH < 1f) return -1f;

            float fx0 = (area.Left - dstL) / dstW * ToneGridX;
            float fx1 = (area.Right - dstL) / dstW * ToneGridX;
            float fy0 = (area.Top - dstT) / dstH * ToneGridY;
            float fy1 = (area.Bottom - dstT) / dstH * ToneGridY;

            int ix0 = (int)MathF.Floor(fx0), ix1 = (int)MathF.Ceiling(fx1);
            int iy0 = (int)MathF.Floor(fy0), iy1 = (int)MathF.Ceiling(fy1);
            if (ix0 < 0) ix0 = 0; if (ix1 >= ToneGridX) ix1 = ToneGridX - 1;
            if (iy0 < 0) iy0 = 0; if (iy1 >= ToneGridY) iy1 = ToneGridY - 1;
            if (ix1 < ix0) ix1 = ix0;
            if (iy1 < iy0) iy1 = iy0;
            // 元素跑到岛外（滑入/滑出的中间态）时别拿边缘格凑答案
            if (fx1 < 0f || fx0 > ToneGridX || fy1 < 0f || fy0 > ToneGridY) return -1f;

            float sum = 0f;
            int n = 0;
            for (int y = iy0; y <= iy1; y++)
            {
                int rowBase = y * ToneGridX;
                for (int x = ix0; x <= ix1; x++) { sum += grid[rowBase + x]; n++; }
            }
            return n > 0 ? sum / n : -1f;
        }

        /// <summary>
        /// 非文字元素（图标 / 圆点 / 进度条 / 胶囊底）的逐元素明暗：
        /// 亮底给深色、暗底给浅色；没有网格时回落到当前的全局主 / 副文字色。
        /// </summary>
        private static SKColor ToneColor(SKRect area, byte alpha, bool sub = false)
        {
            SKColor fallback = sub ? _currentSubTextColor : _currentTextColor;
            bool ready = _toneUsePanel ? _panelToneReady : _toneReady;
            if (!ready || area.Width <= 0f || area.Height <= 0f) return fallback.WithAlpha(alpha);

            float t = ToneAt(area);
            if (t < 0f) return fallback.WithAlpha(alpha);

            return t > ToneLightAt
                ? (sub ? new SKColor(55, 55, 55) : SKColors.Black).WithAlpha(alpha)
                : (sub ? new SKColor(180, 180, 180) : SKColors.White).WithAlpha(alpha);
        }

        /// <summary>
        /// 某个文字元素该用什么颜色：亮底黑字、暗底白字，逐个元素独立判定。
        /// 拿不到网格（实体/亚克力、背板为空）时回落到全局主题色。
        /// </summary>
        private static SKColor ToneText(string text, float x, float baseline, SKPaint paint, byte alpha, bool sub)
        {
            // 次级文字的画笔（副标题/时间轴/日期/紧凑模式应用名）自动走灰色档，调用方不用逐个标注
            if (!sub && IsSecondaryPaint(paint)) sub = true;
            SKColor fallback = sub ? _currentSubTextColor : _currentTextColor;

            // 实况窗详情面板：面板上下可能一半深一半浅，仍按每段文字自己的矩形判；
            // 整块面板的平均调色只用于图标 / 进度条这些「不逐字判」的元素。
            bool ready = _toneUsePanel ? _panelToneReady : _toneReady;
            if (!ready || string.IsNullOrEmpty(text)) return fallback.WithAlpha(alpha);

            float w = paint.MeasureText(text);
            if (w <= 0f) return fallback.WithAlpha(alpha);
            SKFontMetrics fm = paint.FontMetrics;

            // 只取字形实际高度的窄带（基线 -0.80×字号 ~ 基线 +0.20×字号），不要用完整
            // ascent/descent：11.5px 的「下载 / 上传」紧贴深色标题栏下沿，完整字体框会把
            // 上方那格深色一起平均进来，于是被误判成深底、这几个字一直不变色。
            float size = paint.TextSize;
            float top = baseline + Math.Max(fm.Ascent, -size * 0.80f);
            float bottom = baseline + Math.Min(fm.Descent, size * 0.20f);
            var area = new SKRect(x, top, x + w, bottom);

            float t = ToneAt(area);
            if (t < 0f) return fallback.WithAlpha(alpha);

            if (t > ToneLightAt)
                return sub ? new SKColor(55, 55, 55).WithAlpha(alpha) : SKColors.Black.WithAlpha(alpha);
            return sub ? new SKColor(180, 180, 180).WithAlpha(alpha) : SKColors.White.WithAlpha(alpha);
        }

        /// <summary>根据元素自己的底色选黑白再画 —— 内置文字全部走这里，别直接 canvas.DrawText。</summary>
        private static void DrawToneText(SKCanvas canvas, string text, float x, float baseline,
            SKPaint paint, byte alpha, bool sub = false)
        {
            paint.Color = ToneText(text, x, baseline, paint, alpha, sub);
            canvas.DrawText(text, x, baseline, paint);
        }

        /// <summary>这个画笔画的是不是「次级文字」（灰字那几档）。</summary>
        private static bool IsSecondaryPaint(SKPaint paint)
            => ReferenceEquals(paint, _bodyPaint) || ReferenceEquals(paint, _tlTextPaint)
            || ReferenceEquals(paint, _datePaint) || ReferenceEquals(paint, _compactAppPaint);

        public static void ApplyThemeColors() // 刷新颜色的方法
        {            bool isLight = ThemeMode == 1;
            if (ThemeMode == 2) // 跟随系统
            {
                isLight = SystemIsLightTheme;
            }
            // 液态玻璃 / 亚克力：明暗不再由 ThemeMode 决定 —— 玻璃的底是桌面，亚克力的底是
            // 模糊背板 + 涂层，文字黑白跟着实际底色走（Renderer.Draw 里逐帧判，翻转时回调本方法）。
            // 判定还没出来（首帧抓屏之前）时沿用 ThemeMode 兜底，避免启动头一瞬文字色跳变。
            // ⚠️ 这里不能再看 AcrylicBackdrop 是否为 null：背板在隐藏 / 停顿抓屏时也会是 null，
            //    那样文字会在玻璃文字色与主题色之间来回闪。
            if ((IslandAnyGlass || IslandAcrylic) && _glassTextKnown)
                isLight = _glassTextIsLight;

            // 预计算颜色，避免在渲染树中生成新对象
            // 亚克力 = 模糊背板 + 上层的浅色/深色涂层（真实亚克力的"奶感"就来自这层）。
            // 上限 140/255 ≈ 55%，与设置窗口那套亚克力色调（0x8C）同口径；档位滑到最左 = 纯模糊。
            // 液态玻璃的涂层固定为 0：玻璃的着色（明度/雾度/饱和）全在 LiquidGlass 合成里算，
            // 再叠一层黑只会让背板变暗（暗壁纸下直接糊成一片死黑，就是"玻璃永远黑"的一半原因）。
            // ⚠️ 唯一例外：背板还没产出时（启动头一瞬）必须垫一层 45 的深色底 ——
            //    否则岛体整帧全透明，看起来就是"启动没渲染、岛体闪没"。
            // ⚠️ 这里算出来的只是初值：玻璃模式下 Renderer.Draw 每帧按"背板在不在"重定 ——
            //    本方法在启动时跑在首帧抓屏之前，那时背板必然为 null，一把梭定成 45 就再也下不来了。
            bool backdropReady = AcrylicBackdrop != null;
            byte bgAlpha = IslandAnyGlass
                ? (backdropReady ? (byte)0 : (byte)45)
                : IslandAcrylic
                    ? (byte)Math.Clamp(BgOpacityLevel * 140 / 4, 0, 255)
                    : (byte)Math.Clamp(BgOpacityLevel * 255 / 4, 0, 255);

            // 一次性诊断：材质开关一变就记一行 —— 排查「亚克力/玻璃看不见」时第一时间看它
            bool wantBackdrop = IslandAcrylic || IslandAnyGlass;
            if (wantBackdrop != _acrylicLogged)
            {
                _acrylicLogged = wantBackdrop;
                Logger.Info($"[背景材质] 材质={(IslandLiquidGlass2 ? "液态玻璃2.0" : IslandLiquidGlass ? "液态玻璃" : IslandAcrylic ? "亚克力" : "实体")}"
                    + $"  涂层alpha={(IslandAnyGlass ? "按背板每帧重定" : bgAlpha.ToString())}"
                    + $"  档位={BgOpacityLevel}  主题={(isLight ? "浅" : "深")}  底色={(isLight ? "白" : "黑")}");
            }
            var baseBg = isLight ? SKColors.White : SKColors.Black;
            var bg = baseBg.WithAlpha(bgAlpha); // 只改变背景色的透明度，不影响内部元素
            _currentTextColor = isLight ? SKColors.Black : SKColors.White;
            _currentSubTextColor = isLight ? new SKColor(80, 80, 80) : new SKColor(200, 200, 200);

            // 直接复写已存在的静态画笔属性 (极致内存复用)
            _bgPaint.Color = bg;
            _titlePaint.Color = _currentTextColor;
            _bodyPaint.Color = _currentSubTextColor;
            _textPaint.Color = _currentTextColor;
            _timePaint.Color = _currentTextColor;
            _datePaint.Color = _currentSubTextColor;
            _compactTimePaint.Color = _currentTextColor;   // “现在”纯黑纯白
            _compactAppPaint.Color = _currentSubTextColor; // 应用名灰色
            _mediaIconPaint.Color = _currentTextColor;
            _barPaint.Color = _currentTextColor;
            _tlTextPaint.Color = _currentSubTextColor; // 时间轴时间文本
            _shadowPaint.Color = _currentTextColor.WithAlpha(50);
            _hoverCirclePaint.Color = _currentTextColor.WithAlpha(25);

            _tagTextPaint.Color = _currentTextColor;
            _tagBgPaint.Color = _currentTextColor.WithAlpha(25);  // 浅色半透明背景标签
            _barBgPaint.Color = _currentTextColor.WithAlpha(30);   // 未填充进度条的半透明纯色底槽

            // 画笔此刻真正是哪一档明暗 —— 渲染线程靠它判断要不要重绑（见 UpdateGlassTextTone）
            _paletteIsLight = isLight;
        }

        public static Plugins.RenderTheme GetCurrentTheme()
            => new Plugins.RenderTheme(_currentTextColor, _currentSubTextColor, _bgPaint.Color, GLOBAL_DPI, NOTCH_BOTTOM_RADIUS);

        private static readonly object _renderLock = new();

        // 全局复用池 (彻底实现 60FPS 零 GC 分配)

        private static readonly SKPaint _bgPaint = new() { Color = SKColors.Black, IsAntialias = true };

        // 液态玻璃的涂层：每帧按"背板在不在"重定（见 Renderer.Draw）。
        // ⚠️ 故意与 _bgPaint 分开：_bgPaint 还要经 GetCurrentTheme 交给插件当主题底色，
        //    在玻璃模式下每帧把它改成透明会让插件自己画的底色一起消失。
        private static readonly SKPaint _coatPaint = new() { Color = SKColors.Black, IsAntialias = true };

        // 亚克力背板画笔。
        // 这里**故意不带 ImageFilter** —— 每帧带高斯画一次会让 Skia 每帧开临时缓冲，帧率和内存都会崩。
        // 模糊已经在抓到背板时一次性做完了（缩 1/12 + 降半升回的盒式模糊），这里只需平滑放大。
        // ⚠️ 必须用 Low（纯双线性）：Medium 会走 mipmap，Skia 按图像 uniqueID 缓存 mip 层级，
        // 而背板每 250ms 就是一张新图 → 同样只增不减。
        private static readonly SKPaint _acrylicPaint = new()
        { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

        private static readonly SKPaint _fallbackIconPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };

        private static SKTypeface _boldTypeface = FontConfig.Bold;

        private static SKTypeface _normalTypeface = FontConfig.Normal;

        private static SKTypeface _semiBoldTypeface = FontConfig.SemiBold;

        static Renderer()
        {
            FontConfig.Changed += ApplyFont;
            ApplyFont();
        }

        public static void ApplyFont()
        {
            _boldTypeface = FontConfig.Bold;
            _normalTypeface = FontConfig.Normal;
            _semiBoldTypeface = FontConfig.SemiBold;

            _titlePaint.Typeface = _boldTypeface;
            _bodyPaint.Typeface = _normalTypeface;
            _textPaint.Typeface = _semiBoldTypeface;
            _timePaint.Typeface = _boldTypeface;
            _datePaint.Typeface = _normalTypeface;
            _compactTimePaint.Typeface = _semiBoldTypeface;
            _compactAppPaint.Typeface = _normalTypeface;
            _tagTextPaint.Typeface = _boldTypeface;
            _tlTextPaint.Typeface = _semiBoldTypeface; // 时间轴时间文本

            _lastMinute = -1;                 // 时间/日期文本与宽度缓存
            _lastToastId = uint.MaxValue;     // Toast 分段缓存（下一帧强制重建）
            _lastMediaTitle = "";             // 媒体文本度量缓存
            _lastMediaArtist = "";            // 不动 _lastLyric，避免误触发歌词叠化动画

            _cachedToastSenderRuns.Clear();
            _cachedToastBodyRuns.Clear();
            _cachedToastAppNameRuns.Clear();
            _karaokeRuns.Clear();
            _karaokeRuns1.Clear();
            _krKey0 = default;
            _krKey1 = default;
            _fallbackWidths.Clear();
        }

        private static readonly SKPaint _titlePaint = new() { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _bodyPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 11.5f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _textPaint = new() { Color = SKColors.White, TextSize = 12.5f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _shadowPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 1.5f) };

        private static readonly SKPaint _mediaIconPaint = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPaint _barPaint = new() { Color = SKColors.White, IsAntialias = true };

        private static readonly SKPaint _tlTextPaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPath _bgPath = new();

        private static readonly SKPath _clipPath = new();

        private static readonly SKPath _playPath = CreatePlayPath();

        private static readonly SKPath _pausePath = CreatePausePath();

        private static readonly SKPath _prevPath = CreatePrevPath();

        private static readonly SKPath _nextPath = CreateNextPath();

        // PNG 图标缓存替换 SVG

        private static SKBitmap? _defaultAppIcon;

        private static SKBitmap? _qqIcon;

        private static SKBitmap? _defaultToastIcon;

        private static bool _iconsLoaded = false;

        private static readonly SKPaint _highQualitySampling = new() { FilterQuality = SKFilterQuality.High };

        private static SKBitmap? GetDefaultAppIcon()
        {
            if (_defaultAppIcon == null)
            {
                // 优先走 Shell 取大图：Icon.ExtractAssociatedIcon 只会给 32×32，
                // 而岛体 28 逻辑 px 在 150%/200% 下要画 42/56 物理像素 —— 用 32 的源就是"低清"。
                try
                {
                    if (Environment.ProcessPath is { } exe)
                        _defaultAppIcon = ShellIcon.Load(exe, 96);
                }
                catch { }

                if (_defaultAppIcon == null)
                {
                    try
                    {
                        // 每次调用都会重进本分支，所以更必须 Dispose。
                        using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                        if (icon != null)
                        {
                            using var bmp = icon.ToBitmap();
                            using var ms = new MemoryStream();
                            bmp.Save(ms, ImageFormat.Png);
                            ms.Position = 0;
                            _defaultAppIcon = SKBitmap.Decode(ms);
                        }
                    }
                    catch { }
                }
            }
            return _defaultAppIcon;
        }

        private static void EnsureIconsLoaded()
        {
            if (_iconsLoaded) return;
            try
            {
                // 直接极速解码为位图
                using (var stream = DataResources.OpenRead("data/image/qq-icon.png"))
                    if (stream != null) _qqIcon = SKBitmap.Decode(stream);

                using (var stream = DataResources.OpenRead("data/image/wintoast-icon.png"))
                    if (stream != null) _defaultToastIcon = SKBitmap.Decode(stream);
            }
            catch (Exception ex)
            {
                Logger.Error("加载 PNG 图标失败", ex);
            }
            finally { _iconsLoaded = true; }
        }

        // 待机时间显示专用画笔
        private static readonly SKPaint _timePaint = new() { Color = SKColors.White, TextSize = 14.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _datePaint = new() { Color = new SKColor(200, 200, 200), TextSize = 14.5f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _compactTimePaint = new() { Color = SKColors.White, TextSize = 10f, IsAntialias = true, Typeface = _semiBoldTypeface };

        private static readonly SKPaint _compactAppPaint = new() { Color = new SKColor(200, 200, 200), TextSize = 10f, IsAntialias = true, Typeface = _normalTypeface };

        private static readonly SKPaint _tagTextPaint = new() { Color = SKColors.White, TextSize = 10.5f, IsAntialias = true, Typeface = _boldTypeface };

        private static readonly SKPaint _tagBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKPaint _barBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

        private static readonly SKTypeface _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji");

        private static void DrawSvgPath(SKCanvas canvas, SKPaint paint, float x, float y, SKPath path, float scale = 1f)
        {
            canvas.Save();
            canvas.Translate(x, y);
            if (scale != 1f) canvas.Scale(scale);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        private static SKPath CreatePlayPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(10, 6); path.LineTo(0, 12); path.Close(); return path; }

        private static SKPath CreatePausePath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 3, 12)); path.AddRect(new SKRect(6, 0, 9, 12)); return path; }

        private static SKPath CreatePrevPath() { var path = new SKPath(); path.AddRect(new SKRect(0, 0, 2, 10)); path.MoveTo(8, 0); path.LineTo(2, 5); path.LineTo(8, 10); path.Close(); return path; }

        private static SKPath CreateNextPath() { var path = new SKPath(); path.MoveTo(0, 0); path.LineTo(6, 5); path.LineTo(0, 10); path.Close(); path.AddRect(new SKRect(6, 0, 8, 10)); return path; }

        private static void BuildTextRuns(string text, SKPaint paint, SKTypeface baseTypeface, List<(string Text, SKTypeface Type, float X)> runs, out float totalWidth)
        {
            runs.Clear();
            totalWidth = 0;
            if (string.IsNullOrEmpty(text)) return;

            int start = 0;
            SKTypeface? currentType = null;

            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                int charLen = 1;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                {
                    cp = char.ConvertToUtf32(text, i);
                    charLen = 2;
                }

                bool missingInBase = baseTypeface.GetGlyph(cp) == 0;
                bool forcedEmoji = false;

                if (!missingInBase && i + charLen < text.Length)
                {
                    char nextChar = text[i + charLen];
                    if (nextChar == '\uFE0F' || nextChar == '\u200D' || nextChar == '\u20E3')
                    {
                        forcedEmoji = true;
                    }
                }

                if (cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3)
                {
                    forcedEmoji = true;
                }

                if (cp >= 0x1F3FB && cp <= 0x1F3FF)
                {
                    forcedEmoji = true;
                }

                SKTypeface type = ResolveTypeface(cp, baseTypeface, missingInBase, forcedEmoji);
                currentType ??= type; // 初始化第一个状态

                // 只有当字体类型发生真正的改变时，才进行安全切割
                if (!ReferenceEquals(type, currentType))
                {
                    string sub = text.Substring(start, i - start);
                    runs.Add((sub, currentType, totalWidth));
                    paint.Typeface = currentType;
                    totalWidth += paint.MeasureText(sub);

                    currentType = type;
                    start = i;
                }

                if (charLen == 2) i++; // 跳过代理对的后半段
            }

            // 处理收尾文本
            if (start < text.Length && currentType != null)
            {
                string sub = text.Substring(start);
                runs.Add((sub, currentType, totalWidth));
                paint.Typeface = currentType;
                totalWidth += paint.MeasureText(sub);
            }

            paint.Typeface = baseTypeface; // 重置画笔
        }

        private static SKTypeface ResolveTypeface(int cp, SKTypeface baseTypeface, bool missingInBase, bool forcedEmoji)
        {
            if (!missingInBase && !forcedEmoji) return baseTypeface;
            if (forcedEmoji) return _emojiTypeface;
            if (IsEmojiCodePoint(cp) && _emojiTypeface.GetGlyph(cp) != 0) return _emojiTypeface;

            if (!FontConfig.HasCustomFont && missingInBase)
            {
                SKTypeface? multi = LyricsFont.Resolve(cp, baseTypeface);
                if (multi != null) return multi;
            }

            if (FontConfig.Fallback.GetGlyph(cp) != 0) return FontConfig.Fallback;
            return _emojiTypeface; // 兜底字体也没有：维持改动前「交给 Emoji 字体」的旧行为
        }

        private static bool IsEmojiCodePoint(int cp)
            => (cp >= 0x1F000 && cp <= 0x1FAFF)   // Emoji 主体区（表情、交通、补充符号等）
            || (cp >= 0x2600 && cp <= 0x27BF)     // 杂项符号与装饰符号
            || (cp >= 0x2B00 && cp <= 0x2BFF)     // 杂项符号与箭头
            || (cp >= 0xFE00 && cp <= 0xFE0F)     // 变体选择符
            || cp == 0x200D || cp == 0x20E3;

        private static readonly List<(string Text, SKTypeface Type, float X)> _measureRuns = new(8);
        private static readonly Dictionary<string, float> _fallbackWidths = new(64);

        public static float MeasureTextWithFallback(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            if (_fallbackWidths.TryGetValue(text, out float cached)) return cached;

            BuildTextRuns(text, _textPaint, _textPaint.Typeface, _measureRuns, out float width);
            if (_fallbackWidths.Count >= 128) _fallbackWidths.Clear();
            _fallbackWidths[text] = width;
            return width;
        }

        private static bool ReuseCachedRuns(string text, SKTypeface baseTypeface, out float totalWidth)
        {
            if (_krKey0.Text == text && ReferenceEquals(_krKey0.Type, baseTypeface)) { totalWidth = _krKey0.Width; return true; }
            if (_krKey1.Text == text && ReferenceEquals(_krKey1.Type, baseTypeface)) { totalWidth = _krKey1.Width; return true; }
            totalWidth = 0f;
            return false;
        }

    }
}
