using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace NotchPeninsula
{
    /// <summary>
    /// 歌词 / 歌名 / 歌手等"任意语言文本"的缺字兜底层（仅服务于默认系统字体）。
    ///
    /// 背景：默认字体 Microsoft YaHei UI 只含中文 + 拉丁 + 西里尔，韩文（谚文）几乎全缺，
    /// 日文假名虽勉强收录却用的是中文字形。缺的字过去会落到 Segoe UI Emoji —— 那里同样没有，
    /// 最终画出方块。
    ///
    /// 本类把"缺字 → 该用哪套字体"的判定交给系统字体服务（<see cref="SKFontManager.MatchCharacter"/>），
    /// 由 Windows 自己回答"这个码点该用哪套已安装字体"。这是唯一能正确覆盖
    /// 韩 / 日 / 俄 / 泰 / 阿拉伯 / 希伯来 / 天城文…… 的做法：硬编码字体族名既覆盖不全，
    /// 又会在用户机器上没装那套字体时退化——更糟的是 <c>FromFamilyName</c> 找不到族时会
    /// 静默返回默认字体，继续画方块。
    ///
    /// 性能与内存纪律（常驻渲染路径的硬约束）：
    ///   • <b>决定只在码点首次出现时解析一次</b>，之后走 <see cref="_perCp"/> 字典命中，全是引用比较；
    ///     歌词每个码点正常只出现一次，因此稳态 60FPS 下本类几乎不被触碰。
    ///   • <b>不做任何后台预热、不建常驻表</b>：字典条目随真实歌词增长，一首多语言歌最多几十条。
    ///   • <b>解析失败时为负缓存</b>（记 <c>null</c>），保证同一个码点绝不会被反复询问系统字体服务。
    ///   • <b>只持有系统已安装字体的引用</b>，不加载字体文件、不读取字体数据，
    ///     因此没有可泄漏的非托管内存，也不需要 Dispose。
    ///
    /// 与自定义字体的关系（优先级铁律）：
    ///   用户选了自定义字体 ⇒ 用户已经明确表达了自己要的那套字面，
    ///   此时本类<b>完全不参与</b>，由 Renderer 沿用原有的「基础字体 → 系统字体 → Emoji」链路。
    /// </summary>
    internal static class LyricsFont
    {
        /// <summary>码点 → 该用哪套字体面（null 表示"系统也给不出"，负缓存）。</summary>
        private static readonly Dictionary<int, SKTypeface?> _perCp = new(96);

        /// <summary>上次解析时使用的基础字体（引用比较即可识别换字体），换字体后所有决定一律重算。</summary>
        private static SKTypeface? _baseFace;

        /// <summary>整串文本 → 排版宽度缓存（同一句歌词每帧都会被测量，这里保证只算一次）。</summary>
        private static readonly Dictionary<string, float> _widths = new(24);
        private static readonly Queue<string> _widthOrder = new(24);
        private const int WidthCacheCap = 64;

        /// <summary>
        /// 该文本是否需要走本兜底层。判定极廉价：只要有一个码点在基础字体里没有字形，
        /// 就说明这段文本超出了默认字体的覆盖范围（韩文歌、夹杂谚文的日文歌、泰文歌……）。
        /// 纯中文 / 纯英文歌曲在第一次扫描后即被判定为 false，渲染路径零额外成本。
        /// </summary>
        internal static bool NeedsFallback(string text, SKTypeface baseTypeface)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                // 控制字符与不可见修饰符（变体选择符 / 零宽连字）交给原有 Emoji 链路，不算缺字
                if (cp < 0x20 || cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3) continue;
                if (baseTypeface.GetGlyph(cp) == 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 解析一个在基础字体里缺字形的码点该交给哪套字体。
        /// 返回 null 表示系统也给不出能画这个字的字体（此时调用方沿用原有的 Emoji 兜底，行为与改动前一致）。
        /// </summary>
        internal static SKTypeface? Resolve(int cp, SKTypeface baseTypeface)
        {
            // 基础字体变了（用户换字体 / 恢复系统字体）⇒ 全部决定作废重算。
            // 绝大多数调用里 ReferenceEquals 直接成立，连 FamilyName 拼接都不做。
            if (!ReferenceEquals(baseTypeface, _baseFace))
            {
                _baseFace = baseTypeface;
                _perCp.Clear();
                _widths.Clear();
                _widthOrder.Clear();
            }

            if (_perCp.TryGetValue(cp, out var cached)) return cached;

            var face = Lookup(cp, baseTypeface);
            _perCp[cp] = face;   // face 为 null 即负缓存：同一码点绝不重复询问系统
            return face;
        }

        /// <summary>
        /// 向系统字体服务询问「这个码点该用哪套字体」，并对结果做字形校验。
        ///
        /// 校验不可省略：<c>MatchCharacter</c> 在少数情况下会返回一个<b>并不含该字形</b>的面
        /// （任务栏 / 浏览器过去正是这样拿到错误结果而画出方块）。拿到结果后自己再确认一次
        /// <see cref="SKTypeface.GetGlyph"/> 有值，有值才采纳。
        /// </summary>
        private static SKTypeface? Lookup(int cp, SKTypeface baseTypeface)
        {
            // ① 首选：带上当前字体族作提示，让系统在"与基础字体的风格关系"上做最优选择
            SKTypeface? hit = Match(cp, baseTypeface.FamilyName);
            if (hit != null) return hit;

            // ② 次选：不带族名提示，让系统在全字体集里挑。中文机器上这条路能拿到韩文 / 泰文等，
            //    而带族名提示时系统偶尔会失败。
            hit = Match(cp, null);
            if (hit != null) return hit;

            // ③ 末选：枚举已安装字体族逐个问。这一步必然命中（用户机器上总有能画韩文的字体），
            //    只有当整个系统确实没有任何字体覆盖该码点时才会走空 —— 那是真无解，交给 Emoji 兜底。
            //    每个字体族只在"基础字体给不出答案的新码点"上被遍历一次，随后进负缓存，不会反复执行。
            try
            {
                foreach (var family in SKFontManager.Default.GetFontFamilies())
                {
                    if (string.IsNullOrEmpty(family)) continue;
                    using var candidate = SKTypeface.FromFamilyName(family);
                    if (candidate == null || candidate.GetGlyph(cp) == 0) continue;

                    // 用族名再向系统取一份"可长期持有"的面（上面的 candidate 会被 using 释放）。
                    // 顺手要求一个更贴近正文的常规字重，避免风格跳脱。
                    var kept = SKTypeface.FromFamilyName(family);
                    if (kept != null && kept.GetGlyph(cp) != 0) return kept;
                    kept?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[LyricsFont] 枚举系统字体失败：{ex.Message}");
            }
            return null;
        }

        /// <summary>向系统询问一次并做字形校验；familyName 为 null 表示不给提示。</summary>
        private static SKTypeface? Match(int cp, string? familyName)
        {
            try
            {
                var face = familyName == null
                    ? SKFontManager.Default.MatchCharacter(cp)
                    : SKFontManager.Default.MatchCharacter(familyName, cp);
                if (face != null && face.GetGlyph(cp) != 0) return face;
            }
            catch { /* 系统字体服务异常：静默降级到下一策略 */ }
            return null;
        }

        /// <summary>
        /// 整串文本的排版宽度（带缓存）。
        /// 单码点逐次 <c>MeasureText</c> 求和会丢失字距调整，行宽会失真；
        /// 而同一句歌词每个渲染帧都要测量，所以必须缓存 —— 稳定期 60FPS 零重算、零分配。
        /// </summary>
        internal static float MeasureText(string text, SKPaint paint)
        {
            if (_widths.TryGetValue(text, out float w)) return w;

            float width = paint.MeasureText(text);
            if (_widthOrder.Count >= WidthCacheCap)
            {
                var oldest = _widthOrder.Dequeue();
                _widths.Remove(oldest);
            }
            _widths[text] = width;
            _widthOrder.Enqueue(text);
            return width;
        }
    }
}
