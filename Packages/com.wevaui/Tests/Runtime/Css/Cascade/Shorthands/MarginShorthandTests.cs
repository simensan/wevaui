using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class MarginShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            var ex = MarginShorthandExpander.Margin();
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void One_value_applies_to_all_four_edges() {
            var d = Expand("8px");
            Assert.That(d["margin-top"], Is.EqualTo("8px"));
            Assert.That(d["margin-right"], Is.EqualTo("8px"));
            Assert.That(d["margin-bottom"], Is.EqualTo("8px"));
            Assert.That(d["margin-left"], Is.EqualTo("8px"));
        }

        [Test]
        public void Two_values_apply_top_bottom_and_left_right() {
            var d = Expand("8px 16px");
            Assert.That(d["margin-top"], Is.EqualTo("8px"));
            Assert.That(d["margin-bottom"], Is.EqualTo("8px"));
            Assert.That(d["margin-right"], Is.EqualTo("16px"));
            Assert.That(d["margin-left"], Is.EqualTo("16px"));
        }

        [Test]
        public void Three_values_apply_top_horizontal_bottom() {
            var d = Expand("1px 2px 3px");
            Assert.That(d["margin-top"], Is.EqualTo("1px"));
            Assert.That(d["margin-right"], Is.EqualTo("2px"));
            Assert.That(d["margin-left"], Is.EqualTo("2px"));
            Assert.That(d["margin-bottom"], Is.EqualTo("3px"));
        }

        [Test]
        public void Four_values_apply_top_right_bottom_left() {
            var d = Expand("1px 2px 3px 4px");
            Assert.That(d["margin-top"], Is.EqualTo("1px"));
            Assert.That(d["margin-right"], Is.EqualTo("2px"));
            Assert.That(d["margin-bottom"], Is.EqualTo("3px"));
            Assert.That(d["margin-left"], Is.EqualTo("4px"));
        }

        [Test]
        public void Auto_keyword_is_allowed_per_side() {
            var d = Expand("64px auto");
            Assert.That(d["margin-top"], Is.EqualTo("64px"));
            Assert.That(d["margin-bottom"], Is.EqualTo("64px"));
            Assert.That(d["margin-left"], Is.EqualTo("auto"));
            Assert.That(d["margin-right"], Is.EqualTo("auto"));
        }

        [Test]
        public void All_auto_centers_on_both_axes() {
            var d = Expand("auto");
            Assert.That(d["margin-top"], Is.EqualTo("auto"));
            Assert.That(d["margin-right"], Is.EqualTo("auto"));
            Assert.That(d["margin-bottom"], Is.EqualTo("auto"));
            Assert.That(d["margin-left"], Is.EqualTo("auto"));
        }

        [Test]
        public void Zero_is_accepted_as_a_unitless_length() {
            var d = Expand("0");
            Assert.That(d["margin-top"], Is.EqualTo("0"));
        }

        [Test]
        public void Mixed_units_are_preserved() {
            var d = Expand("10px 1em 20% 0.5rem");
            Assert.That(d["margin-top"], Is.EqualTo("10px"));
            Assert.That(d["margin-right"], Is.EqualTo("1em"));
            Assert.That(d["margin-bottom"], Is.EqualTo("20%"));
            Assert.That(d["margin-left"], Is.EqualTo("0.5rem"));
        }

        [Test]
        public void Negative_lengths_are_preserved() {
            var d = Expand("-4px");
            Assert.That(d["margin-top"], Is.EqualTo("-4px"));
        }

        [Test]
        public void Calc_expression_is_a_valid_edge_value() {
            var d = Expand("calc(10px + 1em)");
            Assert.That(d["margin-top"], Is.EqualTo("calc(10px + 1em)"));
            Assert.That(d["margin-right"], Is.EqualTo("calc(10px + 1em)"));
        }

        [Test]
        public void Five_values_are_rejected() {
            var d = Expand("1px 2px 3px 4px 5px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_yields_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Garbage_token_yields_no_longhands() {
            var d = Expand("not-a-length");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Whitespace_runs_are_collapsed() {
            var d = Expand("   8px    16px   ");
            Assert.That(d["margin-top"], Is.EqualTo("8px"));
            Assert.That(d["margin-right"], Is.EqualTo("16px"));
        }

        [Test]
        public void Registry_resolves_margin_to_expander() {
            Assert.That(ShorthandRegistry.IsShorthand("margin"), Is.True);
            Assert.That(ShorthandRegistry.TryGet("margin", out var ex), Is.True);
            Assert.That(ex.ShorthandName, Is.EqualTo("margin"));
        }
    }
}
