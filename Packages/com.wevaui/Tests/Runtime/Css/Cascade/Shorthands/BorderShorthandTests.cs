using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class BorderShorthandTests {
        static Dictionary<string, string> Expand(IShorthandExpander ex, string value) {
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Border_full_triplet_sets_all_twelve_longhands() {
            var d = Expand(BorderShorthandExpander.Border(), "1px solid red");
            Assert.That(d["border-top-width"], Is.EqualTo("1px"));
            Assert.That(d["border-right-width"], Is.EqualTo("1px"));
            Assert.That(d["border-bottom-width"], Is.EqualTo("1px"));
            Assert.That(d["border-left-width"], Is.EqualTo("1px"));
            Assert.That(d["border-top-style"], Is.EqualTo("solid"));
            Assert.That(d["border-right-style"], Is.EqualTo("solid"));
            Assert.That(d["border-bottom-style"], Is.EqualTo("solid"));
            Assert.That(d["border-left-style"], Is.EqualTo("solid"));
            Assert.That(d["border-top-color"], Is.EqualTo("red"));
            Assert.That(d["border-right-color"], Is.EqualTo("red"));
            Assert.That(d["border-bottom-color"], Is.EqualTo("red"));
            Assert.That(d["border-left-color"], Is.EqualTo("red"));
            Assert.That(d.Count, Is.EqualTo(12));
        }

        [Test]
        public void Border_only_width_resets_style_to_none_and_color_to_currentcolor() {
            var d = Expand(BorderShorthandExpander.Border(), "1px");
            Assert.That(d["border-top-width"], Is.EqualTo("1px"));
            Assert.That(d["border-top-style"], Is.EqualTo("none"));
            Assert.That(d["border-top-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Border_only_style_resets_width_to_medium_and_color_to_currentcolor() {
            var d = Expand(BorderShorthandExpander.Border(), "solid");
            Assert.That(d["border-top-width"], Is.EqualTo("medium"));
            Assert.That(d["border-top-style"], Is.EqualTo("solid"));
            Assert.That(d["border-top-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Border_tokens_can_appear_in_any_order() {
            var d = Expand(BorderShorthandExpander.Border(), "red solid 2px");
            Assert.That(d["border-top-width"], Is.EqualTo("2px"));
            Assert.That(d["border-top-style"], Is.EqualTo("solid"));
            Assert.That(d["border-top-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Border_with_hex_color_works() {
            var d = Expand(BorderShorthandExpander.Border(), "1px solid #336699");
            Assert.That(d["border-top-color"], Is.EqualTo("#336699"));
        }

        [Test]
        public void Border_with_rgb_color_works() {
            var d = Expand(BorderShorthandExpander.Border(), "1px solid rgb(0, 0, 0)");
            Assert.That(d["border-top-color"], Is.EqualTo("rgb(0, 0, 0)"));
        }

        [Test]
        public void Border_with_thin_keyword_is_a_width() {
            var d = Expand(BorderShorthandExpander.Border(), "thin solid black");
            Assert.That(d["border-top-width"], Is.EqualTo("thin"));
        }

        [Test]
        public void Border_top_only_sets_three_longhands() {
            var d = Expand(BorderShorthandExpander.BorderTop(), "2px dashed blue");
            Assert.That(d["border-top-width"], Is.EqualTo("2px"));
            Assert.That(d["border-top-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-top-color"], Is.EqualTo("blue"));
            Assert.That(d.ContainsKey("border-bottom-width"), Is.False);
        }

        [Test]
        public void Border_left_only_sets_three_longhands() {
            var d = Expand(BorderShorthandExpander.BorderLeft(), "1px solid green");
            Assert.That(d["border-left-width"], Is.EqualTo("1px"));
            Assert.That(d["border-left-style"], Is.EqualTo("solid"));
            Assert.That(d["border-left-color"], Is.EqualTo("green"));
        }

        [Test]
        public void Border_width_one_value_applies_to_all_four_sides() {
            var d = Expand(BorderShorthandExpander.BorderWidth(), "3px");
            Assert.That(d["border-top-width"], Is.EqualTo("3px"));
            Assert.That(d["border-right-width"], Is.EqualTo("3px"));
            Assert.That(d["border-bottom-width"], Is.EqualTo("3px"));
            Assert.That(d["border-left-width"], Is.EqualTo("3px"));
        }

        [Test]
        public void Border_width_four_values_apply_top_right_bottom_left() {
            var d = Expand(BorderShorthandExpander.BorderWidth(), "1px 2px 3px 4px");
            Assert.That(d["border-top-width"], Is.EqualTo("1px"));
            Assert.That(d["border-right-width"], Is.EqualTo("2px"));
            Assert.That(d["border-bottom-width"], Is.EqualTo("3px"));
            Assert.That(d["border-left-width"], Is.EqualTo("4px"));
        }

        [Test]
        public void Border_style_alone_dashed_sets_all_four_styles() {
            var d = Expand(BorderShorthandExpander.BorderStyle(), "dashed");
            Assert.That(d["border-top-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-right-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-bottom-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-left-style"], Is.EqualTo("dashed"));
        }

        [Test]
        public void Border_style_two_values_set_vertical_and_horizontal() {
            var d = Expand(BorderShorthandExpander.BorderStyle(), "solid dashed");
            Assert.That(d["border-top-style"], Is.EqualTo("solid"));
            Assert.That(d["border-bottom-style"], Is.EqualTo("solid"));
            Assert.That(d["border-right-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-left-style"], Is.EqualTo("dashed"));
        }

        [Test]
        public void Border_color_one_value_applies_to_all_four_sides() {
            var d = Expand(BorderShorthandExpander.BorderColor(), "red");
            Assert.That(d["border-top-color"], Is.EqualTo("red"));
            Assert.That(d["border-right-color"], Is.EqualTo("red"));
            Assert.That(d["border-bottom-color"], Is.EqualTo("red"));
            Assert.That(d["border-left-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Border_color_four_values_set_each_side() {
            var d = Expand(BorderShorthandExpander.BorderColor(), "red green blue yellow");
            Assert.That(d["border-top-color"], Is.EqualTo("red"));
            Assert.That(d["border-right-color"], Is.EqualTo("green"));
            Assert.That(d["border-bottom-color"], Is.EqualTo("blue"));
            Assert.That(d["border-left-color"], Is.EqualTo("yellow"));
        }

        [Test]
        public void Border_with_too_many_tokens_yields_nothing() {
            var d = Expand(BorderShorthandExpander.Border(), "1px solid red extra");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Border_with_two_widths_yields_nothing() {
            var d = Expand(BorderShorthandExpander.Border(), "1px 2px solid");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Border_with_currentcolor_keyword_works() {
            var d = Expand(BorderShorthandExpander.Border(), "1px solid currentcolor");
            Assert.That(d["border-top-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Registry_resolves_all_border_shorthand_names() {
            Assert.That(ShorthandRegistry.IsShorthand("border"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-top"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-right"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-bottom"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-left"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-width"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-style"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-color"), Is.True);
        }
    }
}
