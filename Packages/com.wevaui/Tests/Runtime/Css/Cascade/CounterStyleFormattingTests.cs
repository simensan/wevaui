using System.Reflection;
using NUnit.Framework;
using Weva.Css.Cascade;

namespace Weva.Tests.Css.Cascade {
    // CSS Counter Styles L3 §6 — predefined counter-style formatting.
    // `CascadeEngine.FormatCounterValue(int, string)` is the engine's
    // entry point for resolving counter() / counters() generated content
    // to display strings. Each predefined style in §6 must format the
    // integer value per its rule.
    //
    // Background: the advanced-dashboard audit (2026-06-01) surfaced
    // that `counter(name, decimal-leading-zero)` was falling through to
    // the `default` decimal branch, returning "1" instead of "01" — a
    // visible regression for any author using paginated content.
    // This file pins the §6 styles that the engine now handles.
    public class CounterStyleFormattingTests {
        static MethodInfo s_format;

        static string Format(int v, string style) {
            if (s_format == null) {
                s_format = typeof(CascadeEngine).GetMethod(
                    "FormatCounterValue",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
            return (string)s_format.Invoke(null, new object[] { v, style });
        }

        // ─── decimal (default) ────────────────────────────────────────────

        [Test]
        public void Decimal_default_for_unknown_style() {
            // Unrecognised style names fall through to decimal.
            Assert.That(Format(7, "garbage"), Is.EqualTo("7"));
        }

        [Test]
        public void Decimal_negative_values_pass_through() {
            Assert.That(Format(-3, "decimal"), Is.EqualTo("-3"));
        }

        // ─── decimal-leading-zero (§6.1) ─────────────────────────────────

        [Test]
        public void DecimalLeadingZero_pads_single_digit() {
            // §6.1: values 0-9 are padded to 2 digits with a leading zero.
            Assert.That(Format(1, "decimal-leading-zero"), Is.EqualTo("01"));
            Assert.That(Format(7, "decimal-leading-zero"), Is.EqualTo("07"));
        }

        [Test]
        public void DecimalLeadingZero_passes_through_two_digit() {
            // §6.1: values 10-99 are unchanged from decimal.
            Assert.That(Format(10, "decimal-leading-zero"), Is.EqualTo("10"));
            Assert.That(Format(42, "decimal-leading-zero"), Is.EqualTo("42"));
            Assert.That(Format(99, "decimal-leading-zero"), Is.EqualTo("99"));
        }

        [Test]
        public void DecimalLeadingZero_passes_through_three_digit() {
            // §6.1: values beyond 99 are decimal without extra padding.
            Assert.That(Format(100, "decimal-leading-zero"), Is.EqualTo("100"));
            Assert.That(Format(2026, "decimal-leading-zero"), Is.EqualTo("2026"));
        }

        [Test]
        public void DecimalLeadingZero_zero_renders_as_zero_zero() {
            // §6.1: value 0 should also pad to 00.
            Assert.That(Format(0, "decimal-leading-zero"), Is.EqualTo("00"));
        }

        // ─── upper-roman (§6.3) ──────────────────────────────────────────

        [Test]
        public void UpperRoman_canonical_values() {
            Assert.That(Format(1, "upper-roman"), Is.EqualTo("I"));
            Assert.That(Format(4, "upper-roman"), Is.EqualTo("IV"));
            Assert.That(Format(9, "upper-roman"), Is.EqualTo("IX"));
            Assert.That(Format(1994, "upper-roman"), Is.EqualTo("MCMXCIV"));
        }

        [Test]
        public void LowerRoman_canonical_values() {
            Assert.That(Format(2, "lower-roman"), Is.EqualTo("ii"));
            Assert.That(Format(40, "lower-roman"), Is.EqualTo("xl"));
        }

        // ─── upper-alpha / upper-latin / lower-alpha / lower-latin ───────

        [Test]
        public void UpperAlpha_a_to_z() {
            Assert.That(Format(1, "upper-alpha"), Is.EqualTo("A"));
            Assert.That(Format(26, "upper-alpha"), Is.EqualTo("Z"));
            Assert.That(Format(27, "upper-alpha"), Is.EqualTo("AA"));
        }

        [Test]
        public void UpperLatin_is_alias_of_upper_alpha() {
            Assert.That(Format(1, "upper-latin"), Is.EqualTo("A"));
            Assert.That(Format(27, "upper-latin"), Is.EqualTo("AA"));
        }

        [Test]
        public void LowerAlpha_a_to_z() {
            Assert.That(Format(1, "lower-alpha"), Is.EqualTo("a"));
            Assert.That(Format(26, "lower-alpha"), Is.EqualTo("z"));
        }

        [Test]
        public void LowerLatin_is_alias_of_lower_alpha() {
            Assert.That(Format(2, "lower-latin"), Is.EqualTo("b"));
        }

        // ─── Simple bullets (§6.2) ───────────────────────────────────────

        [Test]
        public void Disc_renders_bullet_glyph() {
            // §6.2: disc → •  (U+2022).
            Assert.That(Format(1, "disc"), Is.EqualTo("•"));
            Assert.That(Format(99, "disc"), Is.EqualTo("•"));
        }

        [Test]
        public void Circle_renders_white_bullet() {
            // §6.2: circle → ◦  (U+25E6).
            Assert.That(Format(1, "circle"), Is.EqualTo("◦"));
        }

        [Test]
        public void Square_renders_filled_square() {
            // §6.2: square → ▪  (U+25AA, black small square).
            Assert.That(Format(1, "square"), Is.EqualTo("▪"));
        }

        [Test]
        public void None_renders_empty_string() {
            // §6.2: none — no marker.
            Assert.That(Format(1, "none"), Is.EqualTo(""));
            Assert.That(Format(42, "none"), Is.EqualTo(""));
        }

        // ─── Style is independent of value sign (regression: negative
        //     value at non-decimal style should not crash) ────────────────

        [Test]
        public void Negative_value_with_roman_style_returns_decimal_fallback() {
            // Roman numerals aren't defined for non-positive values in
            // §6.3 — engine's ToRoman path returns the value as decimal.
            Assert.That(Format(-1, "upper-roman"), Is.EqualTo("-1"));
            Assert.That(Format(0, "upper-roman"), Is.EqualTo("0"));
        }
    }
}
