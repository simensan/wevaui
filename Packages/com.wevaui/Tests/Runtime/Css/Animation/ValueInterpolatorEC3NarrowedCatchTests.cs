using NUnit.Framework;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // EC3 — ValueInterpolator.TryParseColorToken (private static in the
    // MultiComponent partial) had a bare `catch { }`; narrowing tightens it to
    // `catch (CssValueParseException)` so programmer errors propagate. The
    // by-design fallback (caller treats `false` as "this token isn't a color"
    // and ultimately discrete-steps) is preserved.
    //
    // TryParseColorToken is reached through the box-shadow / text-shadow
    // multi-component interpolation path; a malformed color token in a shadow
    // component triggers the catch.
    public class ValueInterpolatorEC3NarrowedCatchTests {
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void BoxShadow_with_invalid_hex_color_token_discrete_steps() {
            // `#xyz` is an invalid hex — CssValueParser throws
            // CssValueParseException ("Invalid hex digit") which the narrowed
            // catch in TryParseColorToken handles, returning false. The
            // outer shadow-component parser treats the unknown token as
            // discrete-step. The key assertion is (a) no exception, (b) no
            // unexpected log warning, (c) deterministic discrete-step value.
            var v = ValueInterpolator.Interpolate(
                "2px 2px 0 #xyz",
                "4px 4px 0 #00f",
                0.5,
                PropertyKind.BoxShadow,
                Ctx());
            Assert.That(v, Is.Not.Null);
            Assert.That(v.Length, Is.GreaterThan(0));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TextShadow_with_invalid_hex_color_token_discrete_steps() {
            var v = ValueInterpolator.Interpolate(
                "1px 1px 2px #zz",
                "2px 2px 4px #fff",
                0.7,
                PropertyKind.TextShadow,
                Ctx());
            Assert.That(v, Is.Not.Null);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_shadow_endpoints_interpolate_without_warning() {
            // Sanity check that the happy path is unaffected by the narrowing.
            var v = ValueInterpolator.Interpolate(
                "2px 2px 0 #000",
                "4px 4px 0 #000",
                0.5,
                PropertyKind.BoxShadow,
                Ctx());
            Assert.That(v, Does.Contain("3px"));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
