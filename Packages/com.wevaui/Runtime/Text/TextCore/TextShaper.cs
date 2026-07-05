using System.Collections.Generic;
using Weva.Layout.Text;

namespace Weva.Text.TextCore {
    // TextShaper turns a string into a sequence of (codepoint, advance) pairs.
    //
    // v1 scope (per PLAN §1 non-goals):
    //   - Latin / Latin-1 / basic punctuation only.
    //   - No bidi reordering.
    //   - No complex shaping (ligatures, marks, contextual forms).
    //   - No kerning beyond the per-glyph advance reported by IFontMetrics.
    //
    // Surrogate pairs are decoded into a single 32-bit codepoint so a single
    // emoji counts as one shaped glyph (even though we may not have a font
    // for it — the caller falls back to .notdef in that case).
    //
    // Tab and newline are NOT emitted as shaped glyphs. The caller (LineBreaker)
    // is responsible for handling tab stops and forced line breaks.
    public static class TextShaper {
        public readonly struct ShapedGlyph {
            public readonly uint Codepoint;
            public readonly double AdvancePx;
            public readonly int SourceCharIndex;
            public readonly int SourceCharLength;

            public ShapedGlyph(uint codepoint, double advancePx, int sourceCharIndex, int sourceCharLength) {
                Codepoint = codepoint;
                AdvancePx = advancePx;
                SourceCharIndex = sourceCharIndex;
                SourceCharLength = sourceCharLength;
            }
        }

        public static List<ShapedGlyph> Shape(string text, IFontMetrics metrics, double fontSize) {
            var result = new List<ShapedGlyph>();
            if (string.IsNullOrEmpty(text)) return result;
            ShapeInto(text, metrics, fontSize, result);
            return result;
        }

        public static int ShapeInto(string text, IFontMetrics metrics, double fontSize, List<ShapedGlyph> output) {
            if (output == null) return 0;
            if (string.IsNullOrEmpty(text)) return 0;
            int produced = 0;
            int i = 0;
            int n = text.Length;
            while (i < n) {
                char c = text[i];
                if (c == '\t' || c == '\n' || c == '\r') {
                    i++;
                    continue;
                }
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    len = 2;
                }
                double advance = MeasureGlyph(metrics, text, i, len, fontSize);
                output.Add(new ShapedGlyph(cp, advance, i, len));
                produced++;
                i += len;
            }
            return produced;
        }

        public static double MeasureShaped(List<ShapedGlyph> glyphs) {
            if (glyphs == null) return 0;
            double total = 0;
            for (int i = 0; i < glyphs.Count; i++) total += glyphs[i].AdvancePx;
            return total;
        }

        static double MeasureGlyph(IFontMetrics metrics, string source, int index, int length, double fontSize) {
            if (metrics == null) return 0;
            // Fast path: use TryGetAdvance when the metrics support it (avoids
            // allocating a 1-char substring per shaped glyph).
            if (metrics is IGlyphMetrics gm) {
                uint cp = length == 1
                    ? source[index]
                    : (uint)char.ConvertToUtf32(source[index], source[index + 1]);
                if (gm.TryGetAdvance(cp, fontSize, out double adv)) return adv;
            }
            string sub = length == 1 ? source[index].ToString() : source.Substring(index, length);
            return metrics.Measure(sub, fontSize);
        }
    }
}
