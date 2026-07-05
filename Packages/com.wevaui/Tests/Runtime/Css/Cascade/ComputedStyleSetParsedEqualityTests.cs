using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Tests.Css.Cascade {
    // PI2 (CODE_AUDIT_FINDINGS.md): SetParsed must short-circuit when the
    // typed value being written is reference-equal to the slot's current
    // typed value, mirroring the string setter's `string.Equals` early-out.
    // Without this, animation engines that re-emit the same end-keyframe
    // CssValue every Tick (e.g. a paused transition) churn ComputedStyle
    // Version -> downstream paint cache invalidations.
    public class ComputedStyleSetParsedEqualityTests {
        [Test]
        public void SetParsed_with_same_reference_does_not_bump_Version_PI2() {
            var style = new ComputedStyle(new Element("div"));
            var value = new CssLength(16, CssLengthUnit.Px);

            // First write: occupies the slot and bumps Version. This is the
            // legitimate change we expect to flow through.
            style.SetParsed(CssProperties.FontSizeId, value);
            long versionAfterFirstSet = style.Version;

            // Re-emit the SAME reference (simulates an animator sampling
            // the same end-keyframe each Tick). Version must NOT bump.
            style.SetParsed(CssProperties.FontSizeId, value);
            Assert.That(style.Version, Is.EqualTo(versionAfterFirstSet),
                "Re-setting same CssValue reference must not bump Version (PI2).");

            // And again, to pin the steady-state behaviour over multiple ticks.
            style.SetParsed(CssProperties.FontSizeId, value);
            style.SetParsed(CssProperties.FontSizeId, value);
            Assert.That(style.Version, Is.EqualTo(versionAfterFirstSet),
                "Repeated same-reference SetParsed calls must remain version-stable.");
        }

        [Test]
        public void SetParsed_with_different_reference_bumps_Version_PI2_regression_pin() {
            // Regression pin: the equality short-circuit must NOT swallow
            // real value changes. A different CssValue instance (even one
            // whose Raw text happens to match) must still bump Version so
            // downstream caches re-resolve. Reference-only equality is the
            // chosen strategy - structurally equivalent but distinct
            // instances are treated as a fresh write.
            var style = new ComputedStyle(new Element("div"));
            var firstValue = new CssLength(16, CssLengthUnit.Px);
            style.SetParsed(CssProperties.FontSizeId, firstValue);
            long versionAfterFirst = style.Version;

            // Distinct instance with a different numeric value.
            var secondValue = new CssLength(20, CssLengthUnit.Px);
            style.SetParsed(CssProperties.FontSizeId, secondValue);
            Assert.That(style.Version, Is.GreaterThan(versionAfterFirst),
                "Setting a different CssValue reference must bump Version.");

            // Another distinct instance - even with the same logical value
            // as firstValue, reference equality differs so we bump again.
            // This documents the chosen strategy (ref-only) for future
            // readers who might expect structural equality.
            long versionAfterSecond = style.Version;
            var thirdValue = new CssLength(16, CssLengthUnit.Px);
            style.SetParsed(CssProperties.FontSizeId, thirdValue);
            Assert.That(style.Version, Is.GreaterThan(versionAfterSecond),
                "Distinct CssValue instance bumps Version even at structural parity (ref-only equality strategy).");
        }

        [Test]
        public void SetParsed_with_same_reference_does_not_bump_DecorationVersion_PI2() {
            // DecorationVersion has the same churn-on-resample concern as
            // Version - PaintBoxCache.IsValid gates on DecorationVersion
            // for the decoration-emit fast path. Use a decoration property
            // (background-color) so the bumpVersion branch routes through
            // the DecorationVersion update (not the wrapper branch).
            var style = new ComputedStyle(new Element("div"));
            int bgColorId = CssProperties.GetId("background-color");
            Assume.That(bgColorId, Is.GreaterThanOrEqualTo(0),
                "background-color must be a registered property for this test.");

            var color = new CssString("red", "red");
            style.SetParsed(bgColorId, color);
            long decorationVersionAfterFirst = style.DecorationVersion;
            long versionAfterFirst = style.Version;
            Assert.That(decorationVersionAfterFirst, Is.EqualTo(versionAfterFirst),
                "First SetParsed on a decoration property must stamp DecorationVersion alongside Version.");

            // Same reference: neither version may move.
            style.SetParsed(bgColorId, color);
            Assert.That(style.DecorationVersion, Is.EqualTo(decorationVersionAfterFirst),
                "Re-setting same CssValue reference must not bump DecorationVersion (PI2).");
            Assert.That(style.Version, Is.EqualTo(versionAfterFirst),
                "Re-setting same CssValue reference must not bump Version either (PI2).");
        }

        [Test]
        public void SetParsed_with_null_after_real_value_still_bumps_Version() {
            // Edge: the equality short-circuit only fires when the cached
            // parsedState is not ParsedNotYet AND the new reference equals
            // the cached one. Going from a real value to null is a real
            // change; it must bump.
            var style = new ComputedStyle(new Element("div"));
            var value = new CssLength(16, CssLengthUnit.Px);
            style.SetParsed(CssProperties.FontSizeId, value);
            long versionAfterFirst = style.Version;

            style.SetParsed(CssProperties.FontSizeId, null);
            Assert.That(style.Version, Is.GreaterThan(versionAfterFirst),
                "Clearing a typed slot with null must bump Version.");
        }
    }
}
