using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for LogicalBorderShorthandExpander.
    // Covers the four expander modes:
    //   - SideBorder  (border-inline-start, border-block-end, ...)
    //   - AxisBorder  (border-inline, border-block) — expands both sides
    //   - AxisWidth   (border-inline-width, border-block-width)
    //   - AxisStyle   (border-inline-style, border-block-style)
    //   - AxisColor   (border-inline-color, border-block-color)
    public class LogicalBorderShorthandTests {
        static Dictionary<string, string> Expand(IShorthandExpander ex, string value) {
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Side_form_expands_to_width_style_color_triplet() {
            var ex = new LogicalBorderShorthandExpander("border-inline-start", "inline", "start");
            var d = Expand(ex, "2px solid red");
            Assert.That(d["border-inline-start-width"], Is.EqualTo("2px"));
            Assert.That(d["border-inline-start-style"], Is.EqualTo("solid"));
            Assert.That(d["border-inline-start-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Side_form_fills_missing_components_with_spec_initials() {
            // Per CSS Backgrounds 3: width=medium, style=none, color=currentcolor.
            var ex = new LogicalBorderShorthandExpander("border-block-end", "block", "end");
            var d = Expand(ex, "dashed");
            Assert.That(d["border-block-end-style"], Is.EqualTo("dashed"));
            Assert.That(d["border-block-end-width"], Is.EqualTo("medium"));
            Assert.That(d["border-block-end-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Axis_border_form_expands_to_both_start_and_end_sides() {
            // border-inline: <triplet>  =>  start + end longhands (6 emits).
            var ex = new LogicalBorderShorthandExpander("border-inline", "inline", null);
            var d = Expand(ex, "1px solid blue");
            Assert.That(d["border-inline-start-width"], Is.EqualTo("1px"));
            Assert.That(d["border-inline-start-style"], Is.EqualTo("solid"));
            Assert.That(d["border-inline-start-color"], Is.EqualTo("blue"));
            Assert.That(d["border-inline-end-width"], Is.EqualTo("1px"));
            Assert.That(d["border-inline-end-style"], Is.EqualTo("solid"));
            Assert.That(d["border-inline-end-color"], Is.EqualTo("blue"));
        }

        [Test]
        public void Axis_width_two_values_map_start_then_end() {
            var ex = LogicalBorderShorthandExpander.AxisWidth("border-inline-width", "inline");
            var d = Expand(ex, "1px 3px");
            Assert.That(d["border-inline-start-width"], Is.EqualTo("1px"));
            Assert.That(d["border-inline-end-width"], Is.EqualTo("3px"));
        }

        [Test]
        public void Axis_width_one_value_applies_to_both_sides() {
            var ex = LogicalBorderShorthandExpander.AxisWidth("border-block-width", "block");
            var d = Expand(ex, "thin");
            Assert.That(d["border-block-start-width"], Is.EqualTo("thin"));
            Assert.That(d["border-block-end-width"], Is.EqualTo("thin"));
        }

        [Test]
        public void Axis_style_rejects_non_border_style_keywords() {
            var ex = LogicalBorderShorthandExpander.AxisStyle("border-inline-style", "inline");
            var d = Expand(ex, "blue");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Axis_color_accepts_hex_and_named_color_pair() {
            var ex = LogicalBorderShorthandExpander.AxisColor("border-block-color", "block");
            var d = Expand(ex, "#abc red");
            Assert.That(d["border-block-start-color"], Is.EqualTo("#abc"));
            Assert.That(d["border-block-end-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Side_form_drops_declaration_on_garbage_token() {
            var ex = new LogicalBorderShorthandExpander("border-inline-start", "inline", "start");
            var d = Expand(ex, "2px foo red");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Axis_width_three_values_drop_the_declaration() {
            var ex = LogicalBorderShorthandExpander.AxisWidth("border-inline-width", "inline");
            var d = Expand(ex, "1px 2px 3px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_registers_the_logical_border_shorthand_family() {
            Assert.That(ShorthandRegistry.IsShorthand("border-inline"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-inline-start"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-inline-end"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-inline-width"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-inline-style"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-inline-color"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-block"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-block-start"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("border-block-end"), Is.True);
        }
    }
}
