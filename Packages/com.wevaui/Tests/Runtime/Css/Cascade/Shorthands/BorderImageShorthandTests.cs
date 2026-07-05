using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for BorderImageShorthandExpander.
    // CSS Backgrounds 3 §6.6 — five longhands (source/slice/width/outset/
    // repeat) with slice/width/outset separated by `/`. Missing slots reset
    // to spec initials: none / 100% / 1 / 0 / stretch.
    public class BorderImageShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new BorderImageShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_url_source_resets_other_slots_to_initials() {
            var d = Expand("url(b.png)");
            Assert.That(d["border-image-source"], Is.EqualTo("url(b.png)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("100%"));
            Assert.That(d["border-image-width"], Is.EqualTo("1"));
            Assert.That(d["border-image-outset"], Is.EqualTo("0"));
            Assert.That(d["border-image-repeat"], Is.EqualTo("stretch"));
        }

        [Test]
        public void Source_and_slice_with_slash_separated_width() {
            var d = Expand("url(b.png) 25% / 2px");
            Assert.That(d["border-image-source"], Is.EqualTo("url(b.png)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("25%"));
            Assert.That(d["border-image-width"], Is.EqualTo("2px"));
            Assert.That(d["border-image-outset"], Is.EqualTo("0"));
        }

        [Test]
        public void Two_slash_form_routes_slice_width_outset() {
            var d = Expand("url(b.png) 10 20 30 40 / 1px 2px / 3px");
            Assert.That(d["border-image-slice"], Is.EqualTo("10 20 30 40"));
            Assert.That(d["border-image-width"], Is.EqualTo("1px 2px"));
            Assert.That(d["border-image-outset"], Is.EqualTo("3px"));
        }

        [Test]
        public void Single_repeat_keyword_applies_to_repeat_slot() {
            var d = Expand("round");
            Assert.That(d["border-image-repeat"], Is.EqualTo("round"));
            Assert.That(d["border-image-source"], Is.EqualTo("none"));
        }

        [Test]
        public void Two_repeat_keywords_form_horizontal_and_vertical_pair() {
            // CSS Backgrounds 3 §6.4: when two keywords are given they apply
            // to the horizontal and vertical axes respectively.
            var d = Expand("repeat space");
            Assert.That(d["border-image-repeat"], Is.EqualTo("repeat space"));
        }

        [Test]
        public void Full_five_slot_form_with_repeat_at_end() {
            var d = Expand("url(b.png) 25% / 1px / 2px round");
            Assert.That(d["border-image-source"], Is.EqualTo("url(b.png)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("25%"));
            Assert.That(d["border-image-width"], Is.EqualTo("1px"));
            Assert.That(d["border-image-outset"], Is.EqualTo("2px"));
            Assert.That(d["border-image-repeat"], Is.EqualTo("round"));
        }

        [Test]
        public void Fill_keyword_is_preserved_in_slice_slot() {
            // `fill` is a slice-only keyword; the expander forwards it verbatim
            // to the longhand parser.
            var d = Expand("url(b.png) 10 fill");
            Assert.That(d["border-image-slice"], Is.EqualTo("10 fill"));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_border_image_to_expander() {
            Assert.That(ShorthandRegistry.IsShorthand("border-image"), Is.True);
            Assert.That(ShorthandRegistry.TryGet("border-image", out var ex), Is.True);
            Assert.That(ex.ShorthandName, Is.EqualTo("border-image"));
        }
    }
}
