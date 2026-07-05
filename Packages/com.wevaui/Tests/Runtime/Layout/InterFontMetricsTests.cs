using NUnit.Framework;
using Weva.Layout.Text;
using FontStyle = Weva.Paint.FontStyle;

namespace Weva.Tests.Layout {
    // W1 increment 2 — InterFontMetrics carries the EXTRACTED table values of
    // the bundled Inter faces (Weva-Default*.ttf): upem 2048, ascent 1984,
    // descent 494, lineGap 0, per-glyph ASCII advances from cmap+hmtx. These
    // tests pin the ground truth so a font swap or constant drift is caught.
    public class InterFontMetricsTests {
        const double Eps = 1e-9;
        static readonly InterFontMetrics M = InterFontMetrics.Instance;

        [Test]
        public void Line_metrics_match_the_extracted_tables() {
            // (1984 + 494 + 0) / 2048 = 2478/2048 = 1.2099609375 exactly
            // (≈1.21 — the table value, not the rounded marketing number).
            Assert.That(InterFontMetrics.LineHeightEm, Is.EqualTo(2478.0 / 2048.0).Within(Eps));
            Assert.That(M.LineHeight(16), Is.EqualTo(19.359375).Within(Eps));
            Assert.That(M.Ascent(16), Is.EqualTo(16 * 1984.0 / 2048.0).Within(Eps));
            Assert.That(M.Descent(16), Is.EqualTo(16 * 494.0 / 2048.0).Within(Eps));
            // Ascent + descent + gap == line-height (no hidden leading).
            Assert.That(M.Ascent(16) + M.Descent(16), Is.EqualTo(M.LineHeight(16)).Within(Eps));
        }

        [Test]
        public void Styled_line_metrics_are_face_invariant() {
            // Verified from the shipped TTFs: Regular/Bold/Italic share
            // identical vertical metrics — the styled overloads must agree.
            Assert.That(M.LineHeight(16, "Inter", FontStyle.Normal, 700),
                Is.EqualTo(M.LineHeight(16)).Within(Eps));
            Assert.That(M.Ascent(20, "Inter", FontStyle.Italic, 400),
                Is.EqualTo(M.Ascent(20)).Within(Eps));
        }

        [Test]
        public void Ascii_advances_are_per_glyph_exact() {
            // hmtx ground truth: 'M' = 1850, 'i' = 496, space = 576 units.
            Assert.That(M.Measure("M", 16), Is.EqualTo(16 * 1850.0 / 2048.0).Within(Eps));
            Assert.That(M.Measure("i", 16), Is.EqualTo(16 * 496.0 / 2048.0).Within(Eps));
            Assert.That(M.Measure(" ", 16), Is.EqualTo(16 * 576.0 / 2048.0).Within(Eps));
            // Sum rule: a string measures as the sum of its glyph advances.
            double m = M.Measure("M", 16) + M.Measure("i", 16);
            Assert.That(M.Measure("Mi", 16), Is.EqualTo(m).Within(Eps));
        }

        [Test]
        public void Bold_face_uses_its_own_advance_table() {
            // Bold 'M' = 1908 vs Regular 1850 — the styled overload must
            // route by weight (>= 600 → Bold).
            double reg = M.Measure("M", 16, "Inter", FontStyle.Normal, 400);
            double bold = M.Measure("M", 16, "Inter", FontStyle.Normal, 700);
            Assert.That(reg, Is.EqualTo(16 * 1850.0 / 2048.0).Within(Eps));
            Assert.That(bold, Is.EqualTo(16 * 1908.0 / 2048.0).Within(Eps));
            Assert.That(bold, Is.GreaterThan(reg));
            // Bold space is NARROWER in Inter (485 vs 576) — a real-table
            // quirk a calibrated approximation would never produce; pins that
            // we really read hmtx.
            Assert.That(M.Measure(" ", 16, "Inter", FontStyle.Normal, 700),
                Is.EqualTo(16 * 485.0 / 2048.0).Within(Eps));
        }

        [Test]
        public void Substring_window_matches_full_measure_contract() {
            // IFontMetrics contract: Measure(text, 0, len, fs) == Measure(text, fs).
            const string s = "Hello, Weva!";
            Assert.That(M.Measure(s, 0, s.Length, 16), Is.EqualTo(M.Measure(s, 16)).Within(Eps));
            // Window sums: [0..4) + [4..len) == whole.
            double parts = M.Measure(s, 0, 4, 16) + M.Measure(s, 4, s.Length - 4, 16);
            Assert.That(parts, Is.EqualTo(M.Measure(s, 16)).Within(Eps));
            // Out-of-range clamps, empty returns 0.
            Assert.That(M.Measure(s, s.Length, 5, 16), Is.EqualTo(0));
            Assert.That(M.Measure("", 16), Is.EqualTo(0));
            Assert.That(M.Measure(null, 16), Is.EqualTo(0));
        }

        [Test]
        public void Cjk_flow_chars_measure_fullwidth() {
            // Inter has no CJK coverage; the deterministic fallback is 1 em
            // (what Chrome's CJK fallback faces produce for ideographs).
            Assert.That(M.Measure("中", 16), Is.EqualTo(16.0).Within(Eps));
            Assert.That(M.Measure("中文", 20), Is.EqualTo(40.0).Within(Eps));
        }

        [Test]
        public void Scales_linearly_with_font_size() {
            Assert.That(M.Measure("Hello", 32), Is.EqualTo(2 * M.Measure("Hello", 16)).Within(1e-9));
            Assert.That(M.LineHeight(32), Is.EqualTo(2 * M.LineHeight(16)).Within(1e-9));
        }
    }
}
