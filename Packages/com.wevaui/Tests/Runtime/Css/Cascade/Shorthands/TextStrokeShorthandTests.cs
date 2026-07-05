using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for TextStrokeShorthandExpander.
    // CSS Text Decoration L4 §10 `text-stroke` / `-webkit-text-stroke` —
    // grammar `<line-width> || <color>`; missing slots reset to the longhand
    // initial values (width=0, color=currentcolor).
    public class TextStrokeShorthandTests {
        static Dictionary<string, string> Expand(string value, string name = "-webkit-text-stroke") {
            return new TextStrokeShorthandExpander(name).Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_width_resets_color_to_currentcolor() {
            var d = Expand("1px");
            Assert.That(d["-webkit-text-stroke-width"], Is.EqualTo("1px"));
            Assert.That(d["-webkit-text-stroke-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Single_color_resets_width_to_zero() {
            var d = Expand("red");
            Assert.That(d["-webkit-text-stroke-width"], Is.EqualTo("0"));
            Assert.That(d["-webkit-text-stroke-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Width_then_color_is_accepted() {
            var d = Expand("2px black");
            Assert.That(d["-webkit-text-stroke-width"], Is.EqualTo("2px"));
            Assert.That(d["-webkit-text-stroke-color"], Is.EqualTo("black"));
        }

        [Test]
        public void Color_then_width_is_accepted_per_double_bar_grammar() {
            // `||` permits either order.
            var d = Expand("black 2px");
            Assert.That(d["-webkit-text-stroke-width"], Is.EqualTo("2px"));
            Assert.That(d["-webkit-text-stroke-color"], Is.EqualTo("black"));
        }

        [Test]
        public void Border_width_keyword_is_accepted_as_width() {
            var d = Expand("thick red");
            Assert.That(d["-webkit-text-stroke-width"], Is.EqualTo("thick"));
            Assert.That(d["-webkit-text-stroke-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Garbage_token_drops_the_declaration() {
            var d = Expand("foo");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Prefixed_and_unprefixed_shorthand_names_both_registered() {
            // Per ShorthandRegistry both `-webkit-text-stroke` and `text-stroke`
            // are registered and both expand to the prefixed longhands.
            Assert.That(ShorthandRegistry.IsShorthand("-webkit-text-stroke"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("text-stroke"), Is.True);
        }
    }
}
