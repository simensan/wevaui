using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for LogicalBoxShorthandExpander.
    // Covers margin-inline / margin-block, padding-inline / padding-block,
    // inset-inline / inset-block. One value applies to both -start and -end;
    // two values map start then end. `auto` is allowed for margin/inset only.
    public class LogicalBoxShorthandTests {
        static Dictionary<string, string> Expand(IShorthandExpander ex, string value) {
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Margin_inline_one_value_applies_to_both_sides() {
            var ex = new LogicalBoxShorthandExpander("margin-inline", "margin", "inline", true);
            var d = Expand(ex, "10px");
            Assert.That(d["margin-inline-start"], Is.EqualTo("10px"));
            Assert.That(d["margin-inline-end"], Is.EqualTo("10px"));
        }

        [Test]
        public void Margin_block_two_values_map_start_then_end() {
            var ex = new LogicalBoxShorthandExpander("margin-block", "margin", "block", true);
            var d = Expand(ex, "1px 2px");
            Assert.That(d["margin-block-start"], Is.EqualTo("1px"));
            Assert.That(d["margin-block-end"], Is.EqualTo("2px"));
        }

        [Test]
        public void Padding_inline_disallows_auto_keyword() {
            // CSS Box 3 §5: paddings never accept `auto`.
            var ex = new LogicalBoxShorthandExpander("padding-inline", "padding", "inline", false);
            var d = Expand(ex, "auto");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Margin_inline_accepts_auto_per_side() {
            var ex = new LogicalBoxShorthandExpander("margin-inline", "margin", "inline", true);
            var d = Expand(ex, "auto 10px");
            Assert.That(d["margin-inline-start"], Is.EqualTo("auto"));
            Assert.That(d["margin-inline-end"], Is.EqualTo("10px"));
        }

        [Test]
        public void Inset_block_accepts_auto_and_percentages() {
            var ex = new LogicalBoxShorthandExpander("inset-block", "inset", "block", true);
            var d = Expand(ex, "auto 50%");
            Assert.That(d["inset-block-start"], Is.EqualTo("auto"));
            Assert.That(d["inset-block-end"], Is.EqualTo("50%"));
        }

        [Test]
        public void Padding_block_accepts_calc_expression() {
            var ex = new LogicalBoxShorthandExpander("padding-block", "padding", "block", false);
            var d = Expand(ex, "calc(10px + 1em)");
            Assert.That(d["padding-block-start"], Is.EqualTo("calc(10px + 1em)"));
            Assert.That(d["padding-block-end"], Is.EqualTo("calc(10px + 1em)"));
        }

        [Test]
        public void Three_values_drop_the_declaration() {
            var ex = new LogicalBoxShorthandExpander("margin-inline", "margin", "inline", true);
            var d = Expand(ex, "1px 2px 3px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Garbage_token_drops_the_declaration() {
            var ex = new LogicalBoxShorthandExpander("margin-block", "margin", "block", true);
            var d = Expand(ex, "not-a-length");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var ex = new LogicalBoxShorthandExpander("padding-inline", "padding", "inline", false);
            var d = Expand(ex, "");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_registers_all_six_logical_box_shorthands() {
            Assert.That(ShorthandRegistry.IsShorthand("margin-inline"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("margin-block"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("padding-inline"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("padding-block"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("inset-inline"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("inset-block"), Is.True);
        }
    }
}
