using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // CSS Values & Units L4 §10: stepped-value, exponential, sign-related,
    // and trigonometric math functions. The min/max/clamp surface is
    // exercised in CssMathFunctionTests; this file pins down the L4 batch.
    public class CssMathFunctionsL4Tests {
        const double Eps = 1e-9;
        const double TrigEps = 1e-7;

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

        // ---- round() ----

        [Test]
        public void Round_nearest_default_strategy() {
            Assert.That(ParseMath("round(7.5, 5)").Evaluate(Ctx()), Is.EqualTo(10).Within(Eps));
        }

        [Test]
        public void Round_explicit_nearest() {
            Assert.That(ParseMath("round(nearest, 7.5, 5)").Evaluate(Ctx()), Is.EqualTo(10).Within(Eps));
        }

        [Test]
        public void Round_down_floors_to_multiple() {
            Assert.That(ParseMath("round(down, 7.5, 5)").Evaluate(Ctx()), Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Round_up_ceilings_to_multiple() {
            Assert.That(ParseMath("round(up, 7.5, 5)").Evaluate(Ctx()), Is.EqualTo(10).Within(Eps));
        }

        [Test]
        public void Round_to_zero_truncates_negative() {
            // -7.5 / 5 = -1.5; truncate toward 0 -> -1; * 5 = -5.
            Assert.That(ParseMath("round(to-zero, -7.5, 5)").Evaluate(Ctx()), Is.EqualTo(-5).Within(Eps));
        }

        [Test]
        public void Round_with_length_args() {
            Assert.That(ParseMath("round(7.5px, 5px)").Evaluate(Ctx()), Is.EqualTo(10).Within(Eps));
        }

        // ---- mod() vs rem() sign rules ----
        // mod() takes the sign of B (Euclidean-style: floored division).
        // rem() takes the sign of A (truncated division — matches JS %).

        [Test]
        public void Mod_positive_positive() {
            Assert.That(ParseMath("mod(10, 3)").Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Mod_negative_a_positive_b_takes_sign_of_b() {
            Assert.That(ParseMath("mod(-10, 3)").Evaluate(Ctx()), Is.EqualTo(2).Within(Eps));
        }

        [Test]
        public void Mod_positive_a_negative_b_takes_sign_of_b() {
            Assert.That(ParseMath("mod(10, -3)").Evaluate(Ctx()), Is.EqualTo(-2).Within(Eps));
        }

        [Test]
        public void Rem_positive_positive() {
            Assert.That(ParseMath("rem(10, 3)").Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rem_negative_a_takes_sign_of_a() {
            Assert.That(ParseMath("rem(-10, 3)").Evaluate(Ctx()), Is.EqualTo(-1).Within(Eps));
        }

        [Test]
        public void Rem_positive_a_negative_b_takes_sign_of_a() {
            Assert.That(ParseMath("rem(10, -3)").Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        // ---- pow / sqrt / hypot ----

        [Test]
        public void Pow_integer_exponent() {
            Assert.That(ParseMath("pow(2, 10)").Evaluate(Ctx()), Is.EqualTo(1024).Within(Eps));
        }

        [Test]
        public void Pow_fractional_exponent() {
            Assert.That(ParseMath("pow(9, 0.5)").Evaluate(Ctx()), Is.EqualTo(3).Within(Eps));
        }

        [Test]
        public void Sqrt_perfect_square() {
            Assert.That(ParseMath("sqrt(16)").Evaluate(Ctx()), Is.EqualTo(4).Within(Eps));
        }

        [Test]
        public void Hypot_two_args_3_4_5() {
            Assert.That(ParseMath("hypot(3, 4)").Evaluate(Ctx()), Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Hypot_three_args_3_4_12_13() {
            Assert.That(ParseMath("hypot(3, 4, 12)").Evaluate(Ctx()), Is.EqualTo(13).Within(Eps));
        }

        // ---- log / exp ----

        [Test]
        public void Log_base_10_of_100_is_2() {
            Assert.That(ParseMath("log(100, 10)").Evaluate(Ctx()), Is.EqualTo(2).Within(Eps));
        }

        [Test]
        public void Log_base_2_of_8_is_3() {
            Assert.That(ParseMath("log(8, 2)").Evaluate(Ctx()), Is.EqualTo(3).Within(Eps));
        }

        [Test]
        public void Log_natural_default_base() {
            // log(A) with no base = ln(A). ln(e) = 1.
            double e = System.Math.E;
            var c = ParseMath("log(" + e.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Exp_zero_is_one() {
            Assert.That(ParseMath("exp(0)").Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Exp_one_is_e() {
            Assert.That(ParseMath("exp(1)").Evaluate(Ctx()), Is.EqualTo(System.Math.E).Within(Eps));
        }

        // ---- abs / sign ----

        [Test]
        public void Abs_negative_number() {
            Assert.That(ParseMath("abs(-5)").Evaluate(Ctx()), Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Abs_negative_length_preserves_pixel_magnitude() {
            // abs(<length>) preserves the length type; here we evaluate the
            // px magnitude.
            Assert.That(ParseMath("abs(-5px)").Evaluate(Ctx()), Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Sign_negative_returns_minus_one() {
            Assert.That(ParseMath("sign(-3)").Evaluate(Ctx()), Is.EqualTo(-1).Within(Eps));
        }

        [Test]
        public void Sign_positive_returns_one() {
            Assert.That(ParseMath("sign(7)").Evaluate(Ctx()), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Sign_zero_returns_zero() {
            Assert.That(ParseMath("sign(0)").Evaluate(Ctx()), Is.EqualTo(0).Within(Eps));
        }

        // ---- Trig: sin / cos / tan (accept <angle> or <number> radians) ----

        [Test]
        public void Sin_radians_zero() {
            Assert.That(ParseMath("sin(0)").Evaluate(Ctx()), Is.EqualTo(0).Within(TrigEps));
        }

        [Test]
        public void Cos_radians_zero_is_one() {
            Assert.That(ParseMath("cos(0)").Evaluate(Ctx()), Is.EqualTo(1).Within(TrigEps));
        }

        [Test]
        public void Sin_ninety_deg_is_one() {
            Assert.That(ParseMath("sin(90deg)").Evaluate(Ctx()), Is.EqualTo(1).Within(TrigEps));
        }

        [Test]
        public void Cos_one_eighty_deg_is_minus_one() {
            Assert.That(ParseMath("cos(180deg)").Evaluate(Ctx()), Is.EqualTo(-1).Within(TrigEps));
        }

        [Test]
        public void Tan_forty_five_deg_is_one() {
            Assert.That(ParseMath("tan(45deg)").Evaluate(Ctx()), Is.EqualTo(1).Within(TrigEps));
        }

        [Test]
        public void Sin_accepts_turn_unit() {
            // 0.25 turn = 90deg -> sin = 1.
            Assert.That(ParseMath("sin(0.25turn)").Evaluate(Ctx()), Is.EqualTo(1).Within(TrigEps));
        }

        // ---- Inverse trig: asin / acos / atan / atan2 return <angle> (degrees) ----

        [Test]
        public void Asin_one_is_ninety_deg() {
            Assert.That(ParseMath("asin(1)").Evaluate(Ctx()), Is.EqualTo(90).Within(TrigEps));
        }

        [Test]
        public void Acos_zero_is_ninety_deg() {
            Assert.That(ParseMath("acos(0)").Evaluate(Ctx()), Is.EqualTo(90).Within(TrigEps));
        }

        [Test]
        public void Atan_one_is_forty_five_deg() {
            Assert.That(ParseMath("atan(1)").Evaluate(Ctx()), Is.EqualTo(45).Within(TrigEps));
        }

        [Test]
        public void Atan2_one_one_is_forty_five_deg() {
            Assert.That(ParseMath("atan2(1, 1)").Evaluate(Ctx()), Is.EqualTo(45).Within(TrigEps));
        }

        [Test]
        public void Atan2_one_zero_is_ninety_deg() {
            Assert.That(ParseMath("atan2(1, 0)").Evaluate(Ctx()), Is.EqualTo(90).Within(TrigEps));
        }

        // ---- Nesting / parse-surface sanity ----

        [Test]
        public void L4_math_kind_is_calc() {
            // All math fns wrap into CssCalc so downstream resolvers see
            // a uniform value-kind, exactly like min/max/clamp.
            Assert.That(CssValueParser.Parse("sqrt(16)").Kind, Is.EqualTo(CssValueKind.Calc));
            Assert.That(CssValueParser.Parse("abs(-5)").Kind, Is.EqualTo(CssValueKind.Calc));
            Assert.That(CssValueParser.Parse("sin(0)").Kind, Is.EqualTo(CssValueKind.Calc));
        }

        [Test]
        public void Pow_inside_calc_expression() {
            // calc(10 + pow(2, 3)) = 18.
            var c = ParseMath("calc(10 + pow(2, 3))");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(18).Within(Eps));
        }

        [Test]
        public void Hypot_inside_calc_with_lengths() {
            // hypot(3px, 4px) -> 5; +1px -> 6.
            var c = ParseMath("calc(hypot(3px, 4px) + 1px)");
            Assert.That(c.Evaluate(Ctx()), Is.EqualTo(6).Within(Eps));
        }

        [Test]
        public void Sqrt_arg_count_mismatch_throws() {
            Assert.Throws<CssValueParseException>(() => ParseMath("sqrt(1, 2)"));
        }

        [Test]
        public void Round_arg_count_mismatch_throws() {
            Assert.Throws<CssValueParseException>(() => ParseMath("round(7)"));
        }

        [Test]
        public void Round_strategy_without_comma_throws() {
            Assert.Throws<CssValueParseException>(() => ParseMath("round(up 7.5, 5)"));
        }
    }
}
