using SkiaSharp;

namespace NotchPeninsula
{
    internal static class LyricsFont
    {
        private static readonly Dictionary<int, WeakReference<SKTypeface>?> _perCp = new(96);

        private static readonly Queue<int> _cpOrder = new(96);

        private const int PerCpCap = 512;

        private static SKTypeface? _baseFace;

        private static readonly Dictionary<string, float> _widths = new(24);
        private static readonly Queue<string> _widthOrder = new(24);
        private const int WidthCacheCap = 64;

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
                if (cp < 0x20 || cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3) continue;
                if (baseTypeface.GetGlyph(cp) == 0) return true;
            }
            return false;
        }

        internal static SKTypeface? Resolve(int cp, SKTypeface baseTypeface)
        {
            if (!ReferenceEquals(baseTypeface, _baseFace))
            {
                _baseFace = baseTypeface;
                _perCp.Clear();
                _cpOrder.Clear();
                _widths.Clear();
                _widthOrder.Clear();
            }

            if (_perCp.TryGetValue(cp, out var cached))
            {
                if (cached == null) return null;                      // 负缓存命中
                if (cached.TryGetTarget(out var alive)) return alive; // 正缓存命中
            }

            var face = Lookup(cp, baseTypeface);
            if (face == null)
            {
                Store(cp, null); // 负缓存：同一码点绝不重复询问系统
                return null;
            }

            Store(cp, face);
            return face;
        }

        private static void Store(int cp, SKTypeface? face)
        {
            if (!_perCp.ContainsKey(cp))
            {
                if (_perCp.Count >= PerCpCap && _cpOrder.Count > 0)
                    _perCp.Remove(_cpOrder.Dequeue());
                _cpOrder.Enqueue(cp);
            }

            if (face == null)
                _perCp[cp] = null; // 负缓存
            else
                _perCp[cp] = new WeakReference<SKTypeface>(face);
        }

        private static SKTypeface? Lookup(int cp, SKTypeface baseTypeface)
        {
            SKTypeface? hit = Match(cp, baseTypeface.FamilyName);
            if (hit != null) return hit;

            //    而带族名提示时系统偶尔会失败。
            hit = Match(cp, null);
            if (hit != null) return hit;

            try
            {
                foreach (var family in SKFontManager.Default.GetFontFamilies())
                {
                    if (string.IsNullOrEmpty(family)) continue;
                    using var candidate = SKTypeface.FromFamilyName(family);
                    if (candidate == null || candidate.GetGlyph(cp) == 0) continue;

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
