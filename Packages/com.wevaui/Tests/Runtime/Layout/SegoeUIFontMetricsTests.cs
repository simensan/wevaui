using NUnit.Framework;
using Weva.Layout.Text;
using FontStyle = Weva.Paint.FontStyle;

namespace Weva.Tests.Layout {
    // SegoeUIFontMetrics carries the EXTRACTED table values of the six real
    // Windows Segoe UI faces (segoeuil / segoeuisl / segoeui / seguisb /
    // segoeuib / seguibl.ttf): upem 2048, ascent 2210, descent 514, lineGap
    // 0, per-glyph U+0020..U+00FF advances from cmap+hmtx, plus the Segoe UI
    // Symbol geometric glyphs Chrome resolves through the system fallback.
    // These tests pin the ground truth so a table regeneration or constant
    // drift is caught — same contract InterFontMetricsTests pins for Inter.
    public class SegoeUIFontMetricsTests {
        const double Eps = 1e-9;
        const double Upem = 2048.0;
        static readonly SegoeUIFontMetrics M = SegoeUIFontMetrics.Instance;

        [Test]
        public void Line_metrics_match_the_extracted_tables() {
            // (2210 + 514 + 0) / 2048 = 2724/2048 = 1.330078125 exactly —
            // the well-known Chrome-on-Windows Segoe UI ~1.33 line height
            // (USE_TYPO_METRICS is unset, so win/hhea metrics apply).
            Assert.That(SegoeUIFontMetrics.LineHeightEm, Is.EqualTo(2724.0 / Upem).Within(Eps));
            Assert.That(M.LineHeight(16), Is.EqualTo(16 * 2724.0 / Upem).Within(Eps));
            Assert.That(M.Ascent(16), Is.EqualTo(16 * 2210.0 / Upem).Within(Eps));
            Assert.That(M.Descent(16), Is.EqualTo(16 * 514.0 / Upem).Within(Eps));
            // Ascent + descent + gap == line-height (no hidden leading).
            Assert.That(M.Ascent(16) + M.Descent(16), Is.EqualTo(M.LineHeight(16)).Within(Eps));
        }

        [Test]
        public void Styled_line_metrics_are_face_invariant() {
            // Verified from the Windows TTFs: all six faces share identical
            // vertical metrics — the styled overloads must agree at every
            // rung of the weight ladder.
            foreach (int w in new[] { 100, 300, 350, 400, 600, 700, 800, 900 }) {
                Assert.That(M.LineHeight(16, "Segoe UI", FontStyle.Normal, w),
                    Is.EqualTo(M.LineHeight(16)).Within(Eps), "weight " + w);
                Assert.That(M.Ascent(16, "Segoe UI", FontStyle.Normal, w),
                    Is.EqualTo(M.Ascent(16)).Within(Eps), "weight " + w);
            }
        }

        [Test]
        public void Ascii_advances_are_per_glyph_exact() {
            // hmtx ground truth (Regular): 'M' = 1839, 'i' = 496, space = 561.
            Assert.That(M.Measure("M", 16), Is.EqualTo(16 * 1839.0 / Upem).Within(Eps));
            Assert.That(M.Measure("i", 16), Is.EqualTo(16 * 496.0 / Upem).Within(Eps));
            Assert.That(M.Measure(" ", 16), Is.EqualTo(16 * 561.0 / Upem).Within(Eps));
            // Sum rule: a string measures as the sum of its glyph advances.
            double m = M.Measure("M", 16) + M.Measure("i", 16);
            Assert.That(M.Measure("Mi", 16), Is.EqualTo(m).Within(Eps));
        }

        [Test]
        public void Weight_ladder_snaps_to_chromes_face_choice() {
            // CSS Fonts §5.2 against Segoe UI's discrete set {300, 350, 400,
            // 600, 700, 900}. hmtx ground truth for 'M' per face:
            //   Light 1706, Semilight 1772, Regular 1839, Semibold 1893,
            //   Bold 1960, Black 2021.
            double MAdv(int weight) => M.Measure("M", 16, "Segoe UI", FontStyle.Normal, weight) * Upem / 16;
            Assert.That(MAdv(100), Is.EqualTo(1706).Within(1e-6)); // <350 → Light
            Assert.That(MAdv(300), Is.EqualTo(1706).Within(1e-6));
            Assert.That(MAdv(350), Is.EqualTo(1772).Within(1e-6)); // 350-399 → Semilight
            Assert.That(MAdv(399), Is.EqualTo(1772).Within(1e-6));
            Assert.That(MAdv(400), Is.EqualTo(1839).Within(1e-6)); // 400-500 → Regular
            Assert.That(MAdv(500), Is.EqualTo(1839).Within(1e-6));
            Assert.That(MAdv(501), Is.EqualTo(1893).Within(1e-6)); // 501-600 → Semibold
            Assert.That(MAdv(600), Is.EqualTo(1893).Within(1e-6));
            Assert.That(MAdv(601), Is.EqualTo(1960).Within(1e-6)); // 601-700 → Bold
            Assert.That(MAdv(700), Is.EqualTo(1960).Within(1e-6));
            Assert.That(MAdv(701), Is.EqualTo(2021).Within(1e-6)); // >700 → Black
            Assert.That(MAdv(800), Is.EqualTo(2021).Within(1e-6)); // match3's combo banner
            Assert.That(MAdv(900), Is.EqualTo(2021).Within(1e-6));
        }

        [Test]
        public void Latin1_advances_are_per_glyph_exact() {
            // U+00D7 (×) — the glyph that made match3's combo banner narrow
            // when it fell to the generic average. Regular 1401, Black 1464.
            Assert.That(M.Measure("×", 16), Is.EqualTo(16 * 1401.0 / Upem).Within(Eps));
            Assert.That(M.Measure("×", 16, "Segoe UI", FontStyle.Normal, 800),
                Is.EqualTo(16 * 1464.0 / Upem).Within(Eps));
            // NBSP measures exactly like a space (same hmtx advance).
            Assert.That(M.Measure("\u00A0", 16), Is.EqualTo(M.Measure(" ", 16)).Within(Eps));
        }

        [Test]
        public void Combo_banner_text_matches_chrome_to_the_hundredth() {
            // The end-to-end pin behind the match3 fix: "SWEET! ×3" at 23px
            // weight 800 must measure with Segoe UI BLACK's advances
            // (10901 units); measured with Bold (10447) the banner is 5 px
            // narrow and the golden drifts.
            double w = M.Measure("SWEET! ×3", 23, "Segoe UI", FontStyle.Normal, 800);
            Assert.That(w, Is.EqualTo(23 * 10901.0 / Upem).Within(Eps));
        }

        [Test]
        public void Segoe_ui_symbol_glyphs_use_the_fallback_face_advances() {
            // Chrome resolves these through Segoe UI Symbol (seguisym.ttf,
            // upem 2048): ★/☆ 1706, ●/○ 1764, card suits 1402.
            Assert.That(M.Measure("★", 27), Is.EqualTo(27 * 1706.0 / Upem).Within(Eps));
            Assert.That(M.Measure("☆", 27), Is.EqualTo(27 * 1706.0 / Upem).Within(Eps));
            Assert.That(M.Measure("●", 16), Is.EqualTo(16 * 1764.0 / Upem).Within(Eps));
            Assert.That(M.Measure("♥", 16), Is.EqualTo(16 * 1402.0 / Upem).Within(Eps));
            // The symbol advance is face-independent (one fallback font),
            // so weight must not change it.
            Assert.That(M.Measure("★", 27, "Segoe UI", FontStyle.Normal, 800),
                Is.EqualTo(M.Measure("★", 27)).Within(Eps));
        }

        [Test]
        public void Substring_window_matches_full_measure_contract() {
            // IFontMetrics contract: Measure(text, 0, len, fs) == Measure(text, fs).
            const string s = "Score × 3 ★";
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
            // Same deterministic fallback model as InterFontMetrics: CJK
            // ideographs measure 1 em.
            Assert.That(M.Measure("中", 16), Is.EqualTo(16.0).Within(Eps));
            Assert.That(M.Measure("中文", 20), Is.EqualTo(40.0).Within(Eps));
        }

        [Test]
        public void Unmapped_c1_controls_fall_through_to_the_generic_fallback() {
            // The C1 block (U+0080..U+009F) has no cmap entries — table rows
            // are 0 and the measure falls through to FallbackAdvanceEm
            // rather than reporting zero width.
            Assert.That(M.Measure("\u0085", 16),
                Is.EqualTo(16 * SegoeUIFontMetrics.FallbackAdvanceEm).Within(Eps));
        }

        [Test]
        public void Scales_linearly_with_font_size() {
            Assert.That(M.Measure("Hello ×★", 32), Is.EqualTo(2 * M.Measure("Hello ×★", 16)).Within(1e-9));
            Assert.That(M.LineHeight(32), Is.EqualTo(2 * M.LineHeight(16)).Within(1e-9));
        }
    }
}
