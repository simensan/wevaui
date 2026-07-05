using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for OutlineShorthandExpander.
    // CSS UI 4 §7.1: `outline` is a triplet shorthand of width || style || color.
    // Missing components reset to spec initials: medium / none / invert.
    public class OutlineShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new OutlineShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_style_keyword_fills_other_longhands_with_initials() {
            var d = Expand("solid");
            Assert.That(d["outline-style"], Is.EqualTo("solid"));
            Assert.That(d["outline-width"], Is.EqualTo("medium"));
            Assert.That(d["outline-color"], Is.EqualTo("invert"));
        }

        [Test]
        public void Single_width_length_is_recognised_as_width() {
            var d = Expand("2px");
            Assert.That(d["outline-width"], Is.EqualTo("2px"));
            Assert.That(d["outline-style"], Is.EqualTo("none"));
            Assert.That(d["outline-color"], Is.EqualTo("invert"));
        }

        [Test]
        public void Single_color_keyword_is_recognised_as_color() {
            var d = Expand("red");
            Assert.That(d["outline-color"], Is.EqualTo("red"));
            Assert.That(d["outline-width"], Is.EqualTo("medium"));
            Assert.That(d["outline-style"], Is.EqualTo("none"));
        }

        [Test]
        public void Two_values_width_and_style_are_routed_correctly() {
            var d = Expand("2px dashed");
            Assert.That(d["outline-width"], Is.EqualTo("2px"));
            Assert.That(d["outline-style"], Is.EqualTo("dashed"));
            Assert.That(d["outline-color"], Is.EqualTo("invert"));
        }

        [Test]
        public void Three_values_in_any_order_are_resolved_per_grammar() {
            // CSS UI 4 §7.1 explicitly permits any ordering.
            var d = Expand("dashed #abc 3px");
            Assert.That(d["outline-style"], Is.EqualTo("dashed"));
            Assert.That(d["outline-color"], Is.EqualTo("#abc"));
            Assert.That(d["outline-width"], Is.EqualTo("3px"));
        }

        [Test]
        public void Invert_keyword_is_accepted_as_color() {
            // Spec initial value: a unique keyword only valid on `outline-color`.
            var d = Expand("invert");
            Assert.That(d["outline-color"], Is.EqualTo("invert"));
        }

        [Test]
        public void Four_or_more_tokens_drop_the_declaration() {
            var d = Expand("2px solid red blue");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Garbage_token_drops_the_declaration() {
            var d = Expand("not-a-style");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_outline_to_expander() {
            Assert.That(ShorthandRegistry.IsShorthand("outline"), Is.True);
            Assert.That(ShorthandRegistry.TryGet("outline", out var ex), Is.True);
            Assert.That(ex.ShorthandName, Is.EqualTo("outline"));
        }
    }
}
