using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ---- 「刘海样式」选项卡里的迷你岛体预览 ----
        //
        // 这张卡是**形态示意**：两个选项各画一个缩小版的岛（经典刘海 / 灵动岛）。
        // 背景材质选到「液态玻璃」时，岛体在桌面上是一块玻璃，预览要是还画成一块纯黑剪影，
        // 就会让人以为「液态玻璃下岛是黑的」。所以这里按材质给预览换画法 —— 只有玻璃预览，
        // 外面那个选项卡框子不动（它表达的是"选哪个形态"，不是材质）。
        //
        // 为什么是手画的假玻璃、而不是搬主岛那套 LiquidGlass：
        //   这里是 20px 高的示意图形，没有可折射的真实桌面，抓屏 + SDF 位移搬过来也没东西可折射；
        //   而且设置窗是逐像素 alpha 的分层窗，半透明渐变本来就会把背后的亚克力透上来，
        //   足够"像玻璃"。成本只有几个渐变对象，设置窗按需重绘，谈不上性能。
        //
        // 层次（与主岛/宣传图的玻璃同序）：投影 → 上亮下透的玻璃体 → 底缘冷色 → 顶缘高光线 → 1px 亮边。

        private static readonly SKMaskFilter _glassBlurShadow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
        private static readonly SKMaskFilter _glassBlurEdge = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1f);

        private static readonly SKPaint _glassBody = new() { IsAntialias = true };
        private static readonly SKPaint _glassStroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
        private static readonly SKPaint _glassShadow = new() { IsAntialias = true };

        private static SKColor GlassWhite(byte a) => new(255, 255, 255, a);

        /// <summary>把预览里的岛体（刘海 / 胶囊）画成一块液态玻璃。形状由调用方按形态构造。</summary>
        private static void DrawLiquidGlassPreview(SKCanvas canvas, SKPath shape)
        {
            bool light = _isLightAppearance;
            var bounds = shape.Bounds;
            float top = bounds.Top;
            float bottom = bounds.Bottom;

            // ① 投影：玻璃是「浮」在选项卡上的。纯色剪影不需要，玻璃没有它就贴成了平面。
            _glassShadow.Color = new SKColor(0, 0, 0, light ? (byte)50 : (byte)82);
            _glassShadow.MaskFilter = _glassBlurShadow;
            canvas.Save();
            canvas.Translate(0, 2f);
            canvas.DrawPath(shape, _glassShadow);
            canvas.Restore();

            // ② 玻璃体：20px 高的形状塞不下太多层次，只保留「上亮下透」这一段主渐变。
            //    深色下是淡白玻璃（让亚克力透上来），浅色下是乳白玻璃（纯白会糊成一块白板）。
            using (var sh = SKShader.CreateLinearGradient(
                new SKPoint(0, top), new SKPoint(0, bottom),
                light
                    ? new[] { GlassWhite(216), GlassWhite(178), GlassWhite(148) }
                    : new[] { GlassWhite(78), GlassWhite(44), GlassWhite(26) },
                new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp))
            {
                _glassBody.Shader = sh;
                _glassBody.MaskFilter = null;
                canvas.DrawPath(shape, _glassBody);
                _glassBody.Shader = null;
            }

            canvas.Save();
            canvas.ClipPath(shape, SKClipOperation.Intersect, true);

            // ③ 底部冷色透光：玻璃会把背后的冷色压到底缘，没有它下半截像死黑
            using (var sh = SKShader.CreateLinearGradient(
                new SKPoint(0, bottom), new SKPoint(0, bottom - bounds.Height * 0.5f),
                new[] { new SKColor(140, 185, 255, light ? (byte)70 : (byte)58), new SKColor(140, 185, 255, 0) },
                null, SKShaderTileMode.Clamp))
            {
                _glassBody.Shader = sh;
                _glassBody.MaskFilter = null;
                canvas.DrawRect(bounds, _glassBody);
                _glassBody.Shader = null;
            }

            // ④ 顶缘高光线：缩到 20px 之后，这条亮线是「有顶面」的唯一线索
            using (var sh = SKShader.CreateLinearGradient(
                new SKPoint(0, top - 0.5f), new SKPoint(0, top + 3.2f),
                new[] { GlassWhite(light ? (byte)240 : (byte)190), GlassWhite(0) },
                null, SKShaderTileMode.Clamp))
            {
                _glassStroke.Shader = sh;
                _glassStroke.MaskFilter = _glassBlurEdge;
                _glassStroke.StrokeWidth = 2.4f;
                canvas.DrawPath(shape, _glassStroke);
                _glassStroke.Shader = null;
            }
            canvas.Restore();

            // ⑤ 全周 1px 亮边：上亮下暗。玻璃的边一定要比内部亮，否则轮廓在深色选项卡里会化掉。
            using (var sh = SKShader.CreateLinearGradient(
                new SKPoint(0, top), new SKPoint(0, bottom),
                light
                    ? new[] { new SKColor(0, 0, 0, 52), new SKColor(0, 0, 0, 34), new SKColor(0, 0, 0, 26) }
                    : new[] { GlassWhite(150), GlassWhite(62), GlassWhite(38) },
                new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp))
            {
                _glassStroke.Shader = sh;
                _glassStroke.MaskFilter = null;
                _glassStroke.StrokeWidth = 1f;
                canvas.DrawPath(shape, _glassStroke);
                _glassStroke.Shader = null;
            }

            // 共用画笔复位：这几个是 static 的，留着 shader / mask 会污染下一帧
            _glassShadow.MaskFilter = null;
            _glassStroke.MaskFilter = null;
        }
    }
}
