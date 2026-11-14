using SkiaSharp;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 插件文字的「逐元素背景明暗」适配。
///
/// <para><b>为什么需要它</b>：同一块面板上完全可能左半边压着深色壁纸、右半边压着白色窗口。
/// 整块面板只认一个 <see cref="RenderTheme.TextColor"/> 时，总有一半文字看不清。宿主内置文字早就按
/// 「每段文字自己下方那块底色」判黑白了；本类把这套能力开放给插件。</para>
///
/// <para><b>三种材质都支持</b>：实体按主题纯色亮度、亚克力按「模糊背板 + 涂层」、液态玻璃按
/// 「背板 + 玻璃明度/雾度」各自建亮度网格。所以插件不需要知道自己跑在哪种材质上。</para>
///
/// <para><b>怎么用</b>：把原来直接调 <c>canvas.DrawText(...)</c> 的地方换成
/// <see cref="DrawText"/>；图标 / 圆点 / 进度条这类非文字元素用
/// <see cref="ColorFor"/> 按自己的矩形取色。不需要传矩形 —— 本类会用画笔量出文字包围盒，
/// 再用当前画布矩阵映射回宿主的亮度网格（插件自己 Translate/Scale 过也能对）。</para>
///
/// <para><b>退化行为</b>：玻璃 / 亚克力背板还没抓到（启动首帧、抓屏没出图）或元素跑到网格外时，
/// 本类原样保留调用方已经设好的画笔颜色；实体材质始终有网格，不存在退化。</para>
/// </summary>
public static class PluginTone
{
    /// <summary>本帧是否有逐元素亮度数据（玻璃 / 亚克力 + 背板已就绪）。</summary>
    public static bool IsAvailable => Renderer.PluginToneReady;

    /// <summary>
    /// 取矩形覆盖区域的平均背景亮度（0..1）。没有亮度数据时返回 -1。
    /// <paramref name="area"/> 用调用方当前画布坐标即可。
    /// </summary>
    public static float LuminanceAt(SKCanvas canvas, SKRect area)
        => Renderer.PluginToneAt(canvas, area);

    /// <summary>该矩形下方是不是「浅底」（亮过阈值就该压黑字）。</summary>
    public static bool IsLightAt(SKCanvas canvas, SKRect area)
    {
        float t = Renderer.PluginToneAt(canvas, area);
        return t >= 0f && t > Renderer.PluginToneLightAt;
    }

    /// <summary>
    /// 给非文字元素（图标、圆点、进度条、描边…）按自己下方的亮度取色：
    /// 亮底返回深色、暗底返回浅色；没有亮度数据时原样返回 <paramref name="preferred"/>。
    /// </summary>
    public static SKColor ColorFor(SKCanvas canvas, SKRect area, SKColor preferred, bool sub = false)
    {
        if (!Renderer.PluginToneReady) return preferred;
        float t = Renderer.PluginToneAt(canvas, area);
        if (t < 0f) return preferred;

        byte a = preferred.Alpha;
        if (t > Renderer.PluginToneLightAt)
            return (sub ? new SKColor(55, 55, 55) : SKColors.Black).WithAlpha(a);
        return (sub ? new SKColor(180, 180, 180) : SKColors.White).WithAlpha(a);
    }

    /// <summary>
    /// 语义色（成功绿 / 错误红 / 品牌粉…）的自适应版本：只在对比度不够时修正，
    /// 够清楚时保留原本的色相，别把「状态色」全洗成黑白。
    /// </summary>
    public static SKColor AccentColor(SKCanvas canvas, SKRect area, SKColor preferred)
    {
        if (!Renderer.PluginToneReady) return preferred;
        float t = Renderer.PluginToneAt(canvas, area);
        if (t < 0f) return preferred;

        float pl = (0.2126f * preferred.Red + 0.7152f * preferred.Green + 0.0722f * preferred.Blue) / 255f;
        byte a = preferred.Alpha;

        // 浅底 + 浅色字：压暗到原来的 55%，色相还在；暗底 + 深色字：提亮 55%。
        if (t > Renderer.PluginToneLightAt && pl > 0.55f)
            return new SKColor((byte)(preferred.Red * 0.45f), (byte)(preferred.Green * 0.45f),
                (byte)(preferred.Blue * 0.45f), a);
        if (t <= Renderer.PluginToneLightAt && pl < 0.35f)
            return new SKColor(Blend(preferred.Red), Blend(preferred.Green), Blend(preferred.Blue), a);

        return preferred;

        static byte Blend(byte c) => (byte)(c + (255 - c) * 0.55f);
    }

    /// <summary>
    /// 画一段自适应黑白文字。颜色按这段文字自己的包围盒取，
    /// 所以同一行里左右两段压在不同底色上也能各自正确。
    /// </summary>
    /// <param name="sub">次级文字：亮底用深灰、暗底用浅灰，和宿主内置的副标题同口径。</param>
    /// <param name="alpha">0 = 沿用 <paramref name="paint"/> 当前的 alpha（推荐）。</param>
    public static void DrawText(SKCanvas canvas, RenderTheme theme, string text, float x, float baseline,
        SKPaint paint, bool sub = false, byte alpha = 255)
    {
        byte a = alpha == 255 ? paint.Color.Alpha : alpha;

        // 实况窗面板：整块面板用同一个明暗（面板平均亮度，已带滞回）。
        // 逐元素在面板上会被封面 / 标题栏边缘带偏，出现「深底上个别字还是黑的」。
        if (Renderer.PluginTonePanel)
        {
            paint.Color = Renderer.PanelToneText(a, sub);
            canvas.DrawText(text, x, baseline, paint);
            return;
        }

        if (!Renderer.PluginToneReady || string.IsNullOrEmpty(text))
        {
            // 没有亮度数据：保持调用方已经设好的颜色（旧行为），只同步一下 alpha
            if (paint.Color.Alpha != a) paint.Color = paint.Color.WithAlpha(a);
            canvas.DrawText(text, x, baseline, paint);
            return;
        }

        float w = paint.MeasureText(text);
        if (w <= 0f)
        {
            canvas.DrawText(text, x, baseline, paint);
            return;
        }

        SKFontMetrics fm = paint.FontMetrics;
        var area = new SKRect(TextLeft(paint, text, x), baseline + fm.Ascent,
            TextLeft(paint, text, x) + w, baseline + fm.Descent);
        float t = Renderer.PluginToneAt(canvas, area);
        if (t < 0f)
        {
            canvas.DrawText(text, x, baseline, paint);
            return;
        }

        paint.Color = t > Renderer.PluginToneLightAt
            ? (sub ? new SKColor(55, 55, 55) : SKColors.Black).WithAlpha(a)
            : (sub ? new SKColor(180, 180, 180) : SKColors.White).WithAlpha(a);
        canvas.DrawText(text, x, baseline, paint);
    }

    /// <summary>按 TextAlign 把锚点 x 换算成文字左边界。</summary>
    private static float TextLeft(SKPaint paint, string text, float x)
    {
        if (paint.TextAlign == SKTextAlign.Center) return x - paint.MeasureText(text) * 0.5f;
        if (paint.TextAlign == SKTextAlign.Right) return x - paint.MeasureText(text);
        return x;
    }

    /// <summary>
    /// 画一段「保留语义色」的自适应文字（成功绿、错误红、品牌色…）：
    /// 底色够亮/够暗时保持原色相，只有对比度不够才自动压暗或提亮。
    /// </summary>
    public static void DrawAccentText(SKCanvas canvas, RenderTheme theme, string text, float x, float baseline,
        SKPaint paint, byte alpha = 255)
    {
        byte a = alpha == 255 ? paint.Color.Alpha : alpha;
        if (Renderer.PluginToneReady && !string.IsNullOrEmpty(text))
        {
            float w = paint.MeasureText(text);
            if (w > 0f)
            {
                SKFontMetrics fm = paint.FontMetrics;
                float left = TextLeft(paint, text, x);
                var area = new SKRect(left, baseline + fm.Ascent, left + w, baseline + fm.Descent);
                paint.Color = AccentColor(canvas, area, paint.Color.WithAlpha(a));
            }
        }
        canvas.DrawText(text, x, baseline, paint);
    }
}
