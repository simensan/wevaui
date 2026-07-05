using NUnit.Framework;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // These tests PIN the deliberately-divergent MonoFontMetrics line-height
    // formula (fontSize * LineHeightEm) documented in the class header and in
    // CSS_COMPLIANCE_ISSUES.md item K2. MonoFontMetrics is the deterministic
    // headless test stand-in, not the production path — SdfBootstrap overrides
    // UIDocumentDefaults.FontMetricsFactory at SubsystemRegistration so real
    // runs resolve TextCoreFontMetrics/SdfFontMetrics/TmpFontMetrics. The
    // production impl derives line-height from face Ascent+Descent+LineGap;
    // the headless impl uses a flat per-em multiplier so ~3,900 PlayMode tests
    // can assert exact pixel numbers without dragging a Unity FontEngine into
    // the assembly. Changing the parameterless ctor cascades into dozens of
    // unrelated assertion failures elsewhere, so we lock the formula here.
    public class MonoFontMetricsTests {
        [Test]
        public void ParameterlessCtor_LineHeight_is_deliberately_fontSize_times_1_2() {
            var m = new MonoFontMetrics();
            // The "legacy default" line-height factor is 1.2 per the class
            // header (`0.5/1.2/0.8/0.4`). Hard-coded into a wide test surface;
            // do not "fix" to align with TextCoreFontMetrics's face-driven
            // formula — see K2 in CSS_COMPLIANCE_ISSUES.md.
            Assert.That(m.LineHeightEm, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(m.LineHeight(16.0), Is.EqualTo(19.2).Within(1e-9));
            Assert.That(m.LineHeight(32.0), Is.EqualTo(38.4).Within(1e-9));
            Assert.That(m.LineHeight(0.0), Is.EqualTo(0.0).Within(1e-12));
            // Ascent + Descent should also be the documented legacy values
            // (0.8em + 0.4em); together they sum to LineHeightEm so the
            // CSS leading split is symmetric for the test fixture.
            Assert.That(m.Ascent(16.0), Is.EqualTo(12.8).Within(1e-9));
            Assert.That(m.Descent(16.0), Is.EqualTo(6.4).Within(1e-9));
            Assert.That(m.AscentEm + m.DescentEm, Is.EqualTo(m.LineHeightEm).Within(1e-12));
        }

        [Test]
        public void ChromeSansSerif_factory_uses_documented_1_143_line_height() {
            // The Chrome* factories are calibrated to Chrome 124's default
            // body font (line-height `normal` ≈ 1.143em). GoldenRunner and
            // LayoutDiffTests assume these numbers — pin them so a casual
            // refactor doesn't silently re-calibrate.
            var sans = MonoFontMetrics.ChromeSansSerif();
            Assert.That(sans.CharWidthEm, Is.EqualTo(0.45).Within(1e-12));
            Assert.That(sans.LineHeightEm, Is.EqualTo(1.143).Within(1e-12));
            Assert.That(sans.AscentEm, Is.EqualTo(0.85).Within(1e-12));
            Assert.That(sans.DescentEm, Is.EqualTo(0.293).Within(1e-12));
            Assert.That(sans.LineHeight(16.0), Is.EqualTo(16.0 * 1.143).Within(1e-9));

            var mono = MonoFontMetrics.ChromeMonospace();
            // Monospace shares the calibrated Chrome line metrics; only the
            // per-glyph advance changes (0.6em vs 0.45em).
            Assert.That(mono.CharWidthEm, Is.EqualTo(0.6).Within(1e-12));
            Assert.That(mono.LineHeightEm, Is.EqualTo(1.143).Within(1e-12));
            Assert.That(mono.AscentEm, Is.EqualTo(0.85).Within(1e-12));
            Assert.That(mono.DescentEm, Is.EqualTo(0.293).Within(1e-12));
            Assert.That(mono.LineHeight(16.0), Is.EqualTo(sans.LineHeight(16.0)).Within(1e-12));
        }

        [Test]
        public void Custom_lineHeightEm_is_propagated_to_LineHeight_independent_of_ascent_descent() {
            // The 4-arg ctor lets callers pin LineHeight independently of
            // ascent+descent. This is by design: production line-height
            // (TextCoreFontMetrics) is face.Ascent+face.Descent+face.LineGap,
            // but MonoFontMetrics must let tests dial line-height to whatever
            // the assertion expects without recomputing ascent/descent. We
            // pin that independence here so a future "consistency fix"
            // doesn't accidentally couple them.
            var m = new MonoFontMetrics(
                charWidthEm: 0.5,
                lineHeightEm: 1.5,
                ascentEm: 0.8,
                descentEm: 0.4);
            Assert.That(m.LineHeight(16.0), Is.EqualTo(24.0).Within(1e-9));
            Assert.That(m.Ascent(16.0) + m.Descent(16.0),
                Is.EqualTo(19.2).Within(1e-9),
                "Ascent+Descent are independent of LineHeight in MonoFontMetrics by design (K2).");
            Assert.That(m.LineHeight(16.0),
                Is.Not.EqualTo(m.Ascent(16.0) + m.Descent(16.0)).Within(1e-9),
                "LineHeight is deliberately NOT Ascent+Descent for the test fixture.");
        }
    }
}
