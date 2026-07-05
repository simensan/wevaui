using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssCalcTests {
        static CssCalc ParseCalc(string s) => (CssCalc)CssValueParser.Parse(s);

        static LengthContext Ctx(double basis = 200) {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            c.ViewportWidthPx = 1000;
            c.ViewportHeightPx = 800;
            c.DpiPixelsPerInch = 96;
            c.BasisPixels = basis;
            return c;
        }

        [Test]
        public void Simple_addition() {
            var c = ParseCalc("calc(10px + 5px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(15).Within(1e-9));
        }

        [Test]
        public void Simple_subtraction() {
            var c = ParseCalc("calc(20px - 5px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(15).Within(1e-9));
        }

        [Test]
        public void Multiplication() {
            var c = ParseCalc("calc(10px * 3)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(30).Within(1e-9));
        }

        [Test]
        public void Division() {
            var c = ParseCalc("calc(30px / 3)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void Mixed_units_with_basis() {
            var c = ParseCalc("calc(50% - 10px)");
            Assert.That(c.Evaluate(Ctx(200)), Is.EqualTo(90).Within(1e-9));
        }

        [Test]
        public void Nested_parens() {
            var c = ParseCalc("calc((10px + 5px) * 2)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(30).Within(1e-9));
        }

        [Test]
        public void Operator_precedence() {
            var c = ParseCalc("calc(10px + 2px * 3)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(16).Within(1e-9));
        }

        [Test]
        public void Whitespace_required_around_plus_throws() {
            Assert.Throws<CssValueParseException>(() => ParseCalc("calc(5px+3px)"));
        }

        [Test]
        public void Whitespace_required_around_minus_throws() {
            Assert.Throws<CssValueParseException>(() => ParseCalc("calc(5px-3px)"));
        }

        [Test]
        public void ToText_round_trips_simple() {
            var c = ParseCalc("calc(100% - 20px)");
            Assert.That(c.ToText(), Is.EqualTo("calc(100% - 20px)"));
        }

        [Test]
        public void ToText_handles_multiplication() {
            var c = ParseCalc("calc(10px * 2)");
            Assert.That(c.ToText(), Is.EqualTo("calc(10px * 2)"));
        }

        [Test]
        public void Em_in_calc() {
            var c = ParseCalc("calc(2em + 4px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(36).Within(1e-9));
        }

        [Test]
        public void Var_in_calc_throws_on_evaluate() {
            var c = ParseCalc("calc(var(--x) + 10px)");
            Assert.Throws<System.InvalidOperationException>(() => c.Evaluate(Ctx()));
        }

        // Nested calc(calc(...)) — the parser unwraps the inner calc at the
        // factor position. Verifies the flatten path stays correct as the
        // tokenizer evolves; without it, mixed-unit nested expressions would
        // either throw or double-wrap.
        [Test]
        public void Nested_calc_flattens() {
            var c = ParseCalc("calc(calc(50% - 10px) + 5px)");
            // basis 200 -> 50% = 100 -> 100 - 10 + 5 = 95
            Assert.That(c.Evaluate(Ctx(200)), Is.EqualTo(95).Within(1e-9));
        }

        // calc(2 * (10px + 5px)) — multiplication distributing across a parenthesised
        // sum. The existing Nested_parens test covers (sum) * num; this covers num * (sum).
        [Test]
        public void Multiplication_outside_parens() {
            var c = ParseCalc("calc(2 * (10px + 5px))");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(30).Within(1e-9));
        }

        // calc(50vw + 1em) — exercises viewport+font mixed units in a single sum.
        // ViewportWidth 1000 -> 50vw = 500; BaseFontSize 16 -> 1em = 16; total 516.
        [Test]
        public void Vw_plus_em_mixed() {
            var c = ParseCalc("calc(50vw + 1em)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(516).Within(1e-9));
        }

        // ch and ex are registered units (CssLength.TryParseUnit) and currently
        // approximate to 0.5 * BaseFontSizePx. Lock the behaviour so a future
        // metric-aware implementation has a regression boundary to update.
        // CSS Values 3 §10 calc() type rules: '*' requires at least one <number>
        // operand; '/' requires the denominator to be a <number>. Regression
        // guards keep number-on-either-side and number-denominator working.
        [Test]
        public void Calc_mul_div_type_checking() {
            Assert.That(ParseCalc("calc(10px * 2)").Evaluate(Ctx()), Is.EqualTo(20).Within(1e-9));
            Assert.That(ParseCalc("calc(2 * 10px)").Evaluate(Ctx()), Is.EqualTo(20).Within(1e-9));
            Assert.That(ParseCalc("calc(10px / 2)").Evaluate(Ctx()), Is.EqualTo(5).Within(1e-9));

            var mulLenLen = ParseCalc("calc(10px * 5px)");
            Assert.Throws<System.InvalidOperationException>(() => mulLenLen.Evaluate(Ctx()));

            var divLenLen = ParseCalc("calc(10px / 5px)");
            Assert.Throws<System.InvalidOperationException>(() => divLenLen.Evaluate(Ctx()));

            var divByZero = ParseCalc("calc(50 / 0)");
            Assert.Throws<System.InvalidOperationException>(() => divByZero.Evaluate(Ctx()));
        }

        // CSS Values 3 §10 type rules for '+'/'-' and arg-list math functions:
        // operands and args must share a type; length and percentage are
        // compatible via <length-percentage>.
        [Test]
        public void Calc_add_sub_and_arglist_type_checking() {
            Assert.That(ParseCalc("calc(10px + 5px)").Evaluate(Ctx()), Is.EqualTo(15).Within(1e-9));

            var addLenNum = ParseCalc("calc(10px + 5)");
            Assert.Throws<System.InvalidOperationException>(() => addLenNum.Evaluate(Ctx()));

            var subLenNum = ParseCalc("calc(10px - 5)");
            Assert.Throws<System.InvalidOperationException>(() => subLenNum.Evaluate(Ctx()));

            Assert.That(ParseCalc("min(10px, 5px)").Evaluate(Ctx()), Is.EqualTo(5).Within(1e-9));

            var minLenNum = ParseCalc("min(10px, 5)");
            Assert.Throws<System.InvalidOperationException>(() => minLenNum.Evaluate(Ctx()));

            var maxLenNum = ParseCalc("max(10px, 5)");
            Assert.Throws<System.InvalidOperationException>(() => maxLenNum.Evaluate(Ctx()));

            var clampMixed = ParseCalc("clamp(1px, 5, 10px)");
            Assert.Throws<System.InvalidOperationException>(() => clampMixed.Evaluate(Ctx()));

            // length + percentage compatible (resolves at use-site via basis).
            Assert.That(ParseCalc("calc(10px + 5%)").Evaluate(Ctx(200)), Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void Ch_and_ex_units_resolve() {
            var ch = ParseCalc("calc(30ch)");
            var ex = ParseCalc("calc(1.4ex)");
            // BaseFontSize 16 -> ch/ex = 8px each.
            Assert.That(ch.Evaluate(Ctx()), Is.EqualTo(30 * 8).Within(1e-9));
            Assert.That(ex.Evaluate(Ctx()), Is.EqualTo(1.4 * 8).Within(1e-9));
        }
    }
}
