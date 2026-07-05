using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // CSS Values & Units L4 §10: math functions min(), max(), clamp().
    //   min(a, b, ...)         -> smallest argument
    //   max(a, b, ...)         -> largest  argument
    //   clamp(MIN, VAL, MAX)   -> max(MIN, min(VAL, MAX))
    // Each argument is a <calc-sum>: a length, percentage, number, or nested
    // math/calc expression. Results resolve in the same length context as
    // their arguments (so `vmin` etc. inherit viewport dimensions).
    public class CssMathFunctionTests {
        static CssCalc ParseMath(string s) => (CssCalc)CssValueParser.Parse(s);

        static LengthContext Ctx() {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            c.ViewportWidthPx = 1000;
            c.ViewportHeightPx = 800;
            c.DpiPixelsPerInch = 96;
            return c;
        }

        // ---- clamp() ----
        // The three canonical scenarios from the task spec:

        [Test]
        public void Clamp_below_min_returns_min() {
            // VAL (5px) < MIN (10px): result clamps up to 10px.
            var c = ParseMath("clamp(10px, 5px, 20px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void Clamp_above_max_returns_max() {
            // VAL (50px) > MAX (20px): result clamps down to 20px.
            var c = ParseMath("clamp(10px, 50px, 20px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void Clamp_in_range_returns_val() {
            // MIN <= VAL <= MAX: pass-through.
            var c = ParseMath("clamp(10px, 15px, 20px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(15).Within(1e-9));
        }

        // ---- min() / max() (mirror path used by clamp) ----

        [Test]
        public void Min_returns_smallest() {
            var c = ParseMath("min(5px, 12px, 8px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(5).Within(1e-9));
        }

        [Test]
        public void Max_returns_largest() {
            var c = ParseMath("max(5px, 12px, 8px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(12).Within(1e-9));
        }

        [Test]
        public void Min_single_argument() {
            var c = ParseMath("min(7px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(7).Within(1e-9));
        }

        // ---- Mixed units / context-dependent resolution ----
        // clamp() must evaluate its args in the SAME length context as a
        // bare length. The randhtml demo relies on this for `vmin` clamping.

        [Test]
        public void Clamp_with_vmin_resolves_in_viewport_context() {
            // 1.2vmin in an 800-min viewport = 1.2% of 800 = 9.6px.
            // clamp(8px, 9.6px, 16px) = 9.6px (in range).
            var c = ParseMath("clamp(8px, 1.2vmin, 16px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(9.6).Within(1e-9));
        }

        [Test]
        public void Clamp_with_vmin_above_max_clamps_down() {
            // 5vmin = 40px in an 800-min viewport, clamped to 16px max.
            var c = ParseMath("clamp(8px, 5vmin, 16px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(16).Within(1e-9));
        }

        // ---- Nesting / inside calc() ----

        [Test]
        public void Clamp_inside_calc_expression() {
            // calc(10px + clamp(5px, 8px, 12px)) = 10 + 8 = 18.
            var c = ParseMath("calc(10px + clamp(5px, 8px, 12px))");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(18).Within(1e-9));
        }

        [Test]
        public void Min_with_calc_argument() {
            // min(20px, calc(5px * 2)) = min(20, 10) = 10.
            var c = ParseMath("min(20px, calc(5px * 2))");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(10).Within(1e-9));
        }

        // ---- Round-trip + parse error surface ----

        [Test]
        public void Clamp_kind_is_calc() {
            // Math functions wrap into a CssCalc so the existing length
            // resolver path picks them up for free.
            var v = CssValueParser.Parse("clamp(1px, 2px, 3px)");
            Assert.That(v.Kind, Is.EqualTo(CssValueKind.Calc));
        }

        [Test]
        public void Clamp_with_wrong_arg_count_throws() {
            Assert.Throws<CssValueParseException>(() => ParseMath("clamp(1px, 2px)"));
        }
    }
}
