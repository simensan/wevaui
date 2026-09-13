using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // Deterministic per-character width variation to expose bugs that rely on
    // all glyphs being the same width (MonoFontMetrics).  Width per character
    // varies in [0.45em .. 0.65em] using a golden-ratio hash keyed on codepoint.
    // Height/ascent/descent mirror MonoFontMetrics defaults so existing line-height
    // expectations remain valid.
    internal sealed class JitteredFontMetrics : IFontMetrics {
        // Same height metrics as default MonoFontMetrics.
        const double LineHeightEm = 1.2;
        const double AscentEm     = 0.8;
        const double DescentEm    = 0.4;

        public double LineHeight(double fontSize) => fontSize * LineHeightEm;
        public double Ascent(double fontSize)     => fontSize * AscentEm;
        public double Descent(double fontSize)    => fontSize * DescentEm;

        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            return Measure(text, 0, text.Length, fontSize);
        }

        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int end = start + length;
            if (end > text.Length) end = text.Length;
            double total = 0;
            for (int i = start; i < end; i++) {
                int cp = text[i];
                // Golden-ratio Fibonacci hash: spreads codepoints uniformly in [0,15].
                uint bucket = (uint)(cp * 2654435769u) >> 28; // 28 = 32-4, 16 buckets
                // Map bucket to widthEm in [0.45, 0.65].
                double widthEm = 0.55 + 0.10 * (bucket / 16.0 - 0.5);
                total += widthEm * fontSize;
            }
            return total;
        }
    }
}
