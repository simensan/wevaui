using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class BorderRadiusShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new BorderRadiusShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void One_value_applies_to_all_four_corners() {
            var d = Expand("12px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("12px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("12px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("12px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("12px"));
        }

        [Test]
        public void Two_values_set_diagonal_pairs_TL_BR_and_TR_BL() {
            var d = Expand("4px 8px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("4px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("4px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("8px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("8px"));
        }

        [Test]
        public void Three_values_set_TL_then_TR_BL_then_BR() {
            var d = Expand("1px 2px 3px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("1px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("2px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("2px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("3px"));
        }

        [Test]
        public void Four_values_set_each_corner_clockwise_from_TL() {
            var d = Expand("1px 2px 3px 4px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("1px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("2px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("3px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("4px"));
        }

        [Test]
        public void Percentage_is_accepted() {
            var d = Expand("50%");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("50%"));
        }

        [Test]
        public void Zero_is_accepted_as_a_unitless_length() {
            var d = Expand("0");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("0"));
        }

        [Test]
        public void Five_values_yield_nothing() {
            var d = Expand("1px 2px 3px 4px 5px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Garbage_value_yields_nothing() {
            var d = Expand("nope");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_border_radius() {
            Assert.That(ShorthandRegistry.IsShorthand("border-radius"), Is.True);
        }

        // ── Elliptical "/" form (CSS Backgrounds & Borders L3 §5) ───────────────
        // Tokens before "/" are horizontal radii, after are vertical; each corner
        // longhand becomes "<rx> <ry>" (or a single token when the axes match).

        [Test]
        public void Slash_single_each_side_makes_uniform_ellipse() {
            var d = Expand("20px / 10px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("20px 10px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("20px 10px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("20px 10px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("20px 10px"));
        }

        [Test]
        public void Slash_four_and_four_maps_each_corner_independently() {
            var d = Expand("70px 60px 64px 72px / 48px 46px 50px 46px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("70px 48px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("60px 46px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("64px 50px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("72px 46px"));
        }

        [Test]
        public void Slash_horizontal_shorthand_fills_before_pairing_vertical() {
            // h = 1 value → all four corners 10px; v = 2 values → TL/BR 4px, TR/BL 8px.
            var d = Expand("10px / 4px 8px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("10px 4px"));
            Assert.That(d["border-top-right-radius"], Is.EqualTo("10px 8px"));
            Assert.That(d["border-bottom-right-radius"], Is.EqualTo("10px 4px"));
            Assert.That(d["border-bottom-left-radius"], Is.EqualTo("10px 8px"));
        }

        [Test]
        public void Slash_with_matching_axes_collapses_to_single_token() {
            var d = Expand("12px / 12px");
            Assert.That(d["border-top-left-radius"], Is.EqualTo("12px"));
        }

        [Test]
        public void Slash_with_empty_vertical_group_yields_nothing() {
            var d = Expand("10px /");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Slash_with_too_many_vertical_values_yields_nothing() {
            var d = Expand("10px / 1px 2px 3px 4px 5px");
            Assert.That(d.Count, Is.EqualTo(0));
        }
    }
}
