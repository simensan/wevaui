using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class BackgroundShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new BackgroundShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Simple_color_sets_color_and_resets_other_longhands_to_initial() {
            var d = Expand("red");
            Assert.That(d["background-color"], Is.EqualTo("red"));
            Assert.That(d["background-image"], Is.EqualTo("none"));
            Assert.That(d["background-repeat"], Is.EqualTo("repeat"));
            Assert.That(d["background-attachment"], Is.EqualTo("scroll"));
            Assert.That(d["background-position"], Is.EqualTo("0% 0%"));
            Assert.That(d["background-size"], Is.EqualTo("auto"));
            Assert.That(d["background-origin"], Is.EqualTo("padding-box"));
            Assert.That(d["background-clip"], Is.EqualTo("border-box"));
        }

        [Test]
        public void Hex_color_works() {
            var d = Expand("#abc");
            Assert.That(d["background-color"], Is.EqualTo("#abc"));
        }

        [Test]
        public void Url_image_sets_image_longhand() {
            var d = Expand("url(\"img.png\")");
            Assert.That(d["background-image"], Is.EqualTo("url(\"img.png\")"));
        }

        [Test]
        public void Linear_gradient_image_works() {
            var d = Expand("linear-gradient(red, blue)");
            Assert.That(d["background-image"], Is.EqualTo("linear-gradient(red, blue)"));
            Assert.That(d["background-color"], Is.EqualTo("transparent"));
        }

        [Test]
        public void No_repeat_sets_repeat_longhand() {
            var d = Expand("url(x.png) no-repeat");
            Assert.That(d["background-image"], Is.EqualTo("url(x.png)"));
            Assert.That(d["background-repeat"], Is.EqualTo("no-repeat"));
        }

        [Test]
        public void Position_token_sets_position() {
            var d = Expand("center");
            Assert.That(d["background-position"], Is.EqualTo("center"));
        }

        [Test]
        public void Position_slash_size_separates_size() {
            var d = Expand("center/cover");
            Assert.That(d["background-position"], Is.EqualTo("center"));
            Assert.That(d["background-size"], Is.EqualTo("cover"));
        }

        [Test]
        public void Url_with_position_size_and_repeat() {
            var d = Expand("url(bg.png) center/cover no-repeat");
            Assert.That(d["background-image"], Is.EqualTo("url(bg.png)"));
            Assert.That(d["background-position"], Is.EqualTo("center"));
            Assert.That(d["background-size"], Is.EqualTo("cover"));
            Assert.That(d["background-repeat"], Is.EqualTo("no-repeat"));
        }

        [Test]
        public void Two_position_tokens_join_with_space() {
            var d = Expand("left top");
            Assert.That(d["background-position"], Is.EqualTo("left top"));
        }

        [Test]
        public void Box_keyword_once_sets_origin_and_clip() {
            var d = Expand("padding-box");
            Assert.That(d["background-origin"], Is.EqualTo("padding-box"));
            Assert.That(d["background-clip"], Is.EqualTo("padding-box"));
        }

        [Test]
        public void Two_box_keywords_set_origin_then_clip() {
            var d = Expand("padding-box content-box");
            Assert.That(d["background-origin"], Is.EqualTo("padding-box"));
            Assert.That(d["background-clip"], Is.EqualTo("content-box"));
        }

        [Test]
        public void Attachment_keyword_works() {
            var d = Expand("fixed");
            Assert.That(d["background-attachment"], Is.EqualTo("fixed"));
        }

        [Test]
        public void Comma_separated_layers_emit_per_layer_lists() {
            // First layer = "red" but per spec only the FINAL layer carries color, so
            // a non-final color invalidates the shorthand.
            var d = Expand("red, blue");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Two_layer_image_then_color_emits_color_for_final_layer() {
            var d = Expand("url(a.png), red");
            Assert.That(d["background-color"], Is.EqualTo("red"));
            Assert.That(d["background-image"], Is.EqualTo("url(a.png), none"));
            Assert.That(d["background-repeat"], Is.EqualTo("repeat, repeat"));
        }

        [Test]
        public void Two_image_layers_keep_color_transparent() {
            var d = Expand("url(a.png), linear-gradient(red, blue)");
            Assert.That(d["background-color"], Is.EqualTo("transparent"));
            Assert.That(d["background-image"], Is.EqualTo("url(a.png), linear-gradient(red, blue)"));
        }

        [Test]
        public void Comma_inside_gradient_does_not_count_as_layer_separator() {
            var d = Expand("linear-gradient(red, green, blue)");
            Assert.That(d["background-image"], Is.EqualTo("linear-gradient(red, green, blue)"));
        }

        [Test]
        public void Empty_value_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_background() {
            Assert.That(ShorthandRegistry.IsShorthand("background"), Is.True);
        }

        [Test]
        public void Color_mix_function_is_recognized_as_color() {
            // Regression: modern color functions (oklab/oklch/hwb/color-mix) used
            // to be silently rejected by the shorthand expander, dropping the entire
            // declaration and leaving the element with no background.
            var d = Expand("color-mix(in oklch, #4f46e5 50%, #ec4899)");
            Assert.That(d["background-color"], Is.EqualTo("color-mix(in oklch, #4f46e5 50%, #ec4899)"));
            Assert.That(d["background-image"], Is.EqualTo("none"));
        }

        [Test]
        public void Oklch_function_is_recognized_as_color() {
            var d = Expand("oklch(70% 0.15 270)");
            Assert.That(d["background-color"], Is.EqualTo("oklch(70% 0.15 270)"));
        }
    }
}
