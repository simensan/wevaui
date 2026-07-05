using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for ListStyleShorthandExpander.
    // CSS Lists 3 §3.4: `list-style: <type> || <position> || <image>`. The
    // shorthand always resets all three longhands, with `none` applying to
    // both type and image when no image URL is given.
    public class ListStyleShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new ListStyleShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_type_keyword_resets_position_and_image_to_initial() {
            var d = Expand("square");
            Assert.That(d["list-style-type"], Is.EqualTo("square"));
            Assert.That(d["list-style-position"], Is.EqualTo("outside"));
            Assert.That(d["list-style-image"], Is.EqualTo("none"));
        }

        [Test]
        public void Inside_keyword_sets_position_only() {
            // Type token absent — implementation emits initial `disc`.
            var d = Expand("inside");
            Assert.That(d["list-style-position"], Is.EqualTo("inside"));
            Assert.That(d["list-style-type"], Is.EqualTo("disc"));
            Assert.That(d["list-style-image"], Is.EqualTo("none"));
        }

        [Test]
        public void Url_image_is_classified_as_image_longhand() {
            var d = Expand("url(bullet.png)");
            Assert.That(d["list-style-image"], Is.EqualTo("url(bullet.png)"));
            Assert.That(d["list-style-type"], Is.EqualTo("disc"));
            Assert.That(d["list-style-position"], Is.EqualTo("outside"));
        }

        [Test]
        public void Three_value_form_routes_each_slot() {
            var d = Expand("square inside url(bullet.png)");
            Assert.That(d["list-style-type"], Is.EqualTo("square"));
            Assert.That(d["list-style-position"], Is.EqualTo("inside"));
            Assert.That(d["list-style-image"], Is.EqualTo("url(bullet.png)"));
        }

        [Test]
        public void None_alone_clears_both_type_and_image() {
            // CSS Lists 3 §3.4: a bare `none` suppresses the marker glyph.
            var d = Expand("none");
            Assert.That(d["list-style-type"], Is.EqualTo("none"));
            Assert.That(d["list-style-image"], Is.EqualTo("none"));
            Assert.That(d["list-style-position"], Is.EqualTo("outside"));
        }

        [Test]
        public void Explicit_image_overrides_bare_none_for_image_slot() {
            // Author wrote a URL — `none` only zeros the type, image keeps URL.
            var d = Expand("none url(b.png)");
            Assert.That(d["list-style-type"], Is.EqualTo("none"));
            Assert.That(d["list-style-image"], Is.EqualTo("url(b.png)"));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_list_style_to_expander() {
            Assert.That(ShorthandRegistry.IsShorthand("list-style"), Is.True);
            Assert.That(ShorthandRegistry.TryGet("list-style", out var ex), Is.True);
            Assert.That(ex.ShorthandName, Is.EqualTo("list-style"));
        }
    }
}
