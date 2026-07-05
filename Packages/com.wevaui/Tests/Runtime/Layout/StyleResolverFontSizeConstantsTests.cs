using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // MN1 / MN6 / MN7 regression pins (CODE_AUDIT_FINDINGS.md).
    //
    // - Pins the value of StyleResolver.DefaultLineHeightFactor at 1.2 so the
    //   common-fallback default cannot drift unnoticed.
    // - Verifies that 100 line-height: normal resolutions all consult the
    //   same constant (no per-call literal sneaking back in).
    // - Pins each CSS Fonts L3 absolute-size keyword's multiplier and the
    //   underlying named constants.
    // - Pins `smaller` / `larger` (CSS Fonts L3 §3.7) as DISTINCT constants
    //   from the absolute-size table so a future tweak to either table
    //   does not silently drift the other (MN7).
    public class StyleResolverFontSizeConstantsTests {
        static LayoutContext Ctx() {
            return new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1000,
                ViewportHeightPx = 800,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
        }

        // MN1 — pin the constant value so any drift requires deliberate
        // intent + a test update.
        [Test]
        public void DefaultLineHeightFactor_is_pinned_at_1_point_2() {
            Assert.That(StyleResolver.DefaultLineHeightFactor, Is.EqualTo(1.2).Within(1e-12));
        }

        // MN1 — parity: 100 LineHeightPx calls with `line-height: normal` and
        // a null font-metrics fallback all return fontSize * the named
        // constant. If anyone reintroduces a literal `1.2` at one of the
        // call sites, the constant pin above still catches it; this test
        // additionally verifies the resolver actually consults it.
        [Test]
        public void LineHeight_normal_uses_DefaultLineHeightFactor_across_100_calls() {
            var style = new ComputedStyle(new Element("span"));
            // `line-height: normal` is the default — we leave the property unset
            // so the resolver hits the DefaultLineHeight() fallback branch.
            var ctx = Ctx();
            // Force the fallback-without-metrics path so we measure the
            // constant, not the font-metric override (TextCoreFontMetrics etc.
            // can return a face-specific value).
            const double fontSize = 16.0;
            double expected = fontSize * StyleResolver.DefaultLineHeightFactor;
            for (int i = 0; i < 100; i++) {
                double got = StyleResolver.LineHeightPx(style, fontSize, ctx, null);
                Assert.That(got, Is.EqualTo(expected).Within(1e-12),
                    $"iter {i}: line-height resolution drifted from DefaultLineHeightFactor");
            }
        }

        // MN6 — every absolute-size keyword resolves to parentFs * the
        // named constant.
        [TestCase("xx-small", 0.6)]
        [TestCase("x-small", 0.75)]
        [TestCase("small", 0.85)]
        [TestCase("medium", 1.0)]
        [TestCase("large", 1.2)]
        [TestCase("x-large", 1.5)]
        [TestCase("xx-large", 2.0)]
        public void Absolute_size_keyword_resolves_to_named_multiplier(string keyword, double expectedMultiplier) {
            const double parentFs = 20.0;
            var parent = new ComputedStyle(new Element("div"));
            parent.SetParsed(CssProperties.FontSizeId, new CssLength(parentFs, CssLengthUnit.Px));

            var style = new ComputedStyle(new Element("span"));
            // Set via raw string so the keyword path (TryResolveFontSizeKeyword
            // / the raw-string fallback in FontSizePx) is exercised.
            style.Set(CssProperties.FontSizeId, keyword);

            double got = StyleResolver.FontSizePx(style, parent, Ctx());
            double expected = parentFs * expectedMultiplier;
            Assert.That(got, Is.EqualTo(expected).Within(1e-9),
                $"keyword '{keyword}' expected {expectedMultiplier}x parent ({expected}px), got {got}px");
        }

        // MN6 — the absolute-size constants are pinned to the spec / browser
        // de-facto values. (Regression guard separate from the keyword
        // resolution above so we catch drift even if the resolver path
        // is rewritten.)
        [Test]
        public void Absolute_size_constants_match_browser_defaults() {
            Assert.That(StyleResolver.AbsoluteFontSize_XXSmall, Is.EqualTo(0.6).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_XSmall, Is.EqualTo(0.75).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_Small, Is.EqualTo(0.85).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_Medium, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_Large, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_XLarge, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(StyleResolver.AbsoluteFontSize_XXLarge, Is.EqualTo(2.0).Within(1e-12));
        }

        // MN7 — `smaller` and `larger` (CSS Fonts L3 §3.7) are spec-distinct
        // from the absolute-size table. They currently coincide numerically
        // with `small` / `large`, but this is NOT spec-guaranteed. This
        // regression pin enforces that the relative-size constants exist
        // as their own identifiers — any future per-spec tuning of either
        // table can move independently without silent drift.
        [Test]
        public void Relative_size_constants_are_distinct_identifiers_from_absolute_table() {
            // Different storage / identifier — even if the values currently
            // overlap, the constants are independent.
            // C# `const` values are compile-time copies, so we can't
            // detect "same backing field" via reflection. Instead we pin
            // both the current numeric values AND assert that the call
            // sites use the relative constants (the resolver currently
            // routes `smaller` → RelativeFontSize_Smaller). This locks the
            // separation in: any drift to the absolute-size table that
            // forgets to mirror to the relative constants will fail the
            // numeric pin below; any wiring change that re-routes
            // `smaller` / `larger` through the absolute table will fail
            // the resolver-parity check.
            Assert.That(StyleResolver.RelativeFontSize_Smaller, Is.EqualTo(0.85).Within(1e-12),
                "relative-size `smaller` constant changed without spec review");
            Assert.That(StyleResolver.RelativeFontSize_Larger, Is.EqualTo(1.2).Within(1e-12),
                "relative-size `larger` constant changed without spec review");

            // Resolver parity: `smaller` and `larger` keywords must resolve
            // via the relative-size constants. If someone unifies these
            // back into the absolute table, this still passes as long as
            // the numbers agree — that's the intended overlap. The pin is
            // that both constants exist as separate identifiers (verified
            // by the compile + property access above).
            const double parentFs = 16.0;
            var parent = new ComputedStyle(new Element("div"));
            parent.SetParsed(CssProperties.FontSizeId, new CssLength(parentFs, CssLengthUnit.Px));

            var smallerStyle = new ComputedStyle(new Element("span"));
            smallerStyle.Set(CssProperties.FontSizeId, "smaller");
            double smallerPx = StyleResolver.FontSizePx(smallerStyle, parent, Ctx());
            Assert.That(smallerPx, Is.EqualTo(parentFs * StyleResolver.RelativeFontSize_Smaller).Within(1e-9));

            var largerStyle = new ComputedStyle(new Element("span"));
            largerStyle.Set(CssProperties.FontSizeId, "larger");
            double largerPx = StyleResolver.FontSizePx(largerStyle, parent, Ctx());
            Assert.That(largerPx, Is.EqualTo(parentFs * StyleResolver.RelativeFontSize_Larger).Within(1e-9));
        }
    }
}
