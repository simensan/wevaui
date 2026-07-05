using NUnit.Framework;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;
using Weva.Diagnostics;

namespace Weva.Tests.Css.Animation {
    // EC2 — ValueInterpolator.TryParseLength catch was bare `catch { }`; the
    // narrowing tightens it to `catch (CssValueParseException)` so programmer
    // errors (NullReferenceException etc) propagate. By-design fallback
    // behavior — interpolating an unparseable length yields the discrete-step
    // result (t<0.5 ? from : to) — is preserved.
    //
    // The narrowed catch is a private static; we exercise it through the
    // public Interpolate entry on PropertyKind.Transform — that path calls
    // TryParseLength while parsing translate(...) args. PropertyKind.Length
    // routes through CssValue.TryParse (which has its own log path and
    // catches CssValueParseException itself), so the Transform path is the
    // cleanest way to land in the EC2 catch.
    public class ValueInterpolatorEC2NarrowedCatchTests {
        static LengthContext Ctx() => LengthContext.Default;

        [SetUp]
        public void Reset() {
            // Several upstream interpolation paths share UICssDiagnostics for
            // unrelated warnings; reset so this test starts clean.
            UICssDiagnostics.ResetForTests();
        }

        [Test]
        public void Transform_translate_with_unknown_unit_arg_discrete_steps() {
            // `10badunit` triggers CssValueParseException ("Unknown length
            // unit") inside CssValueParser. TryParseLength catches it
            // (narrowed to CssValueParseException) and returns null. The
            // transform arg-parser then treats the arg as "not a length",
            // falls through to TryParseNumber (which also fails), and the
            // function signature mismatch causes a discrete-step fallback.
            var v = ValueInterpolator.Interpolate(
                "translate(10badunit)",
                "translate(20px)",
                0.7, PropertyKind.Transform, Ctx());
            // Discrete step at t=0.7 returns the `to` endpoint.
            Assert.That(v, Is.EqualTo("translate(20px)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Transform_translate_with_unknown_unit_to_arg_steps_to_from() {
            var v = ValueInterpolator.Interpolate(
                "translate(10px)",
                "translate(20wibbleunit)",
                0.2, PropertyKind.Transform, Ctx());
            Assert.That(v, Is.EqualTo("translate(10px)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Transform_translate_with_both_args_unknown_unit_discrete_steps() {
            // Both endpoints throw CssValueParseException; the narrowed catch
            // handles both. By-design fallback is discrete-step using t<0.5.
            var v = ValueInterpolator.Interpolate(
                "translate(5wibbleunit)",
                "translate(10bogusunit)",
                0.3, PropertyKind.Transform, Ctx());
            Assert.That(v, Is.EqualTo("translate(5wibbleunit)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_transform_translate_still_interpolates() {
            // Sanity: the narrowing did not affect the happy path.
            var v = ValueInterpolator.Interpolate(
                "translate(10px)",
                "translate(20px)",
                0.5, PropertyKind.Transform, Ctx());
            Assert.That(v, Is.EqualTo("translate(15px)"));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
