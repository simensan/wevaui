using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Backgrounds and Borders Level 3 §6 — border-image-* longhand cascade tests.
    //
    // CssProperties registers all five border-image longhands:
    //   border-image-source  initial: none
    //   border-image-slice   initial: 100%
    //   border-image-width   initial: 1
    //   border-image-outset  initial: 0
    //   border-image-repeat  initial: stretch
    //
    // BorderImageShorthandTests covers the shorthand expander; this file covers the
    // longhand cascade contract: initial values, keyword round-trips, and
    // non-inheritance (all five are non-inherited per spec).
    public class BorderImageLonghandTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── border-image-source §6.1 ──────────────────────────────────────

        [Test]
        public void Border_image_source_initial_is_none() {
            // CSS Backgrounds 3 §6.1: initial value `none` (no border image).
            var cs = Compute("");
            Assert.That(cs.Get("border-image-source"), Is.EqualTo("none"));
        }

        [Test]
        public void Border_image_source_url_round_trips() {
            var cs = Compute("#x { border-image-source: url(\"frame.png\"); }");
            Assert.That(cs.Get("border-image-source"), Is.EqualTo("url(\"frame.png\")"));
        }

        [Test]
        public void Border_image_source_gradient_round_trips() {
            var cs = Compute("#x { border-image-source: linear-gradient(red, blue); }");
            Assert.That(cs.Get("border-image-source"), Is.EqualTo("linear-gradient(red, blue)"));
        }

        [Test]
        public void Border_image_source_does_not_inherit() {
            // CSS Backgrounds 3 §6.1: border-image-source is not inherited.
            var cs = ComputeChild("div { border-image-source: url(frame.png); }");
            Assert.That(cs.Get("border-image-source"), Is.EqualTo("none"),
                "border-image-source is non-inherited; child sees initial none");
        }

        // ── border-image-slice §6.2 ───────────────────────────────────────

        [Test]
        public void Border_image_slice_initial_is_100_percent() {
            // CSS Backgrounds 3 §6.2: initial value `100%`.
            var cs = Compute("");
            Assert.That(cs.Get("border-image-slice"), Is.EqualTo("100%"));
        }

        [Test]
        public void Border_image_slice_number_round_trips() {
            // Unitless numbers are absolute pixel insets in the source image.
            var cs = Compute("#x { border-image-slice: 27; }");
            Assert.That(cs.Get("border-image-slice"), Is.EqualTo("27"));
        }

        [Test]
        public void Border_image_slice_four_values_round_trips() {
            // Four values set top/right/bottom/left insets respectively.
            var cs = Compute("#x { border-image-slice: 10 20 30 40; }");
            Assert.That(cs.Get("border-image-slice"), Is.EqualTo("10 20 30 40"));
        }

        [Test]
        public void Border_image_slice_fill_keyword_survives_parse() {
            // CSS Backgrounds 3 §6.2: the `fill` keyword fills the center tile.
            // It may appear before or after the numbers and must not be dropped.
            var cs = Compute("#x { border-image-slice: 25% fill; }");
            Assert.That(cs.Get("border-image-slice"), Is.EqualTo("25% fill"));
        }

        [Test]
        public void Border_image_slice_does_not_inherit() {
            var cs = ComputeChild("div { border-image-slice: 10; }");
            Assert.That(cs.Get("border-image-slice"), Is.EqualTo("100%"),
                "border-image-slice is non-inherited; child sees initial 100%");
        }

        // ── border-image-width §6.3 ───────────────────────────────────────

        [Test]
        public void Border_image_width_initial_is_one() {
            // CSS Backgrounds 3 §6.3: initial value `1` (a multiplier of
            // the corresponding border-width; `1` = same as border-width).
            var cs = Compute("");
            Assert.That(cs.Get("border-image-width"), Is.EqualTo("1"));
        }

        [Test]
        public void Border_image_width_pixel_value_round_trips() {
            var cs = Compute("#x { border-image-width: 4px; }");
            Assert.That(cs.Get("border-image-width"), Is.EqualTo("4px"));
        }

        [Test]
        public void Border_image_width_four_values_round_trips() {
            var cs = Compute("#x { border-image-width: 1px 2px 3px 4px; }");
            Assert.That(cs.Get("border-image-width"), Is.EqualTo("1px 2px 3px 4px"));
        }

        [Test]
        public void Border_image_width_auto_round_trips() {
            // `auto` = intrinsic width of the border-image slice (§6.3).
            var cs = Compute("#x { border-image-width: auto; }");
            Assert.That(cs.Get("border-image-width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Border_image_width_does_not_inherit() {
            var cs = ComputeChild("div { border-image-width: 8px; }");
            Assert.That(cs.Get("border-image-width"), Is.EqualTo("1"),
                "border-image-width is non-inherited; child sees initial 1");
        }

        // ── border-image-outset §6.4 ──────────────────────────────────────

        [Test]
        public void Border_image_outset_initial_is_zero() {
            // CSS Backgrounds 3 §6.4: initial value `0`.
            var cs = Compute("");
            Assert.That(cs.Get("border-image-outset"), Is.EqualTo("0"));
        }

        [Test]
        public void Border_image_outset_pixel_value_round_trips() {
            var cs = Compute("#x { border-image-outset: 5px; }");
            Assert.That(cs.Get("border-image-outset"), Is.EqualTo("5px"));
        }

        [Test]
        public void Border_image_outset_four_values_round_trips() {
            var cs = Compute("#x { border-image-outset: 1px 2px 3px 4px; }");
            Assert.That(cs.Get("border-image-outset"), Is.EqualTo("1px 2px 3px 4px"));
        }

        [Test]
        public void Border_image_outset_does_not_inherit() {
            var cs = ComputeChild("div { border-image-outset: 10px; }");
            Assert.That(cs.Get("border-image-outset"), Is.EqualTo("0"),
                "border-image-outset is non-inherited; child sees initial 0");
        }

        // ── border-image-repeat §6.5 ──────────────────────────────────────

        [Test]
        public void Border_image_repeat_initial_is_stretch() {
            // CSS Backgrounds 3 §6.5: initial value `stretch`.
            var cs = Compute("");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Border_image_repeat_round_keyword_round_trips() {
            // `round` = scale the tile so it fits an integer number of times.
            var cs = Compute("#x { border-image-repeat: round; }");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("round"));
        }

        [Test]
        public void Border_image_repeat_space_keyword_round_trips() {
            // `space` = distribute tiles with equal spacing.
            var cs = Compute("#x { border-image-repeat: space; }");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("space"));
        }

        [Test]
        public void Border_image_repeat_repeat_keyword_round_trips() {
            var cs = Compute("#x { border-image-repeat: repeat; }");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("repeat"));
        }

        [Test]
        public void Border_image_repeat_two_value_form_round_trips() {
            // Two values set horizontal then vertical axes independently (§6.5).
            var cs = Compute("#x { border-image-repeat: round space; }");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("round space"));
        }

        [Test]
        public void Border_image_repeat_does_not_inherit() {
            var cs = ComputeChild("div { border-image-repeat: round; }");
            Assert.That(cs.Get("border-image-repeat"), Is.EqualTo("stretch"),
                "border-image-repeat is non-inherited; child sees initial stretch");
        }
    }
}
