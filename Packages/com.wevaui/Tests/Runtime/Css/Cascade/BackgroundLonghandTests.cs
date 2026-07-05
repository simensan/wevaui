using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Backgrounds and Borders Level 3 — background-* longhand cascade tests.
    //
    // CssProperties registers all nine background longhands (background-color,
    // background-image, background-position, background-size, background-repeat,
    // background-attachment, background-origin, background-clip, and via
    // BackgroundShorthandTests coverage). These tests pin:
    //
    //   - spec initial value when no rule applies
    //   - common keyword round-trips (parse → cascade → Get)
    //   - non-inheritance (all background-* are non-inherited per CSS Backgrounds 3)
    //   - background-repeat two-value axis-explicit syntax (repeat-x / repeat-y
    //     canonical forms vs explicit `repeat no-repeat`)
    //   - background-attachment scroll/fixed/local
    //
    // Paint-side resolution (BackgroundResolver, multi-layer compositing) lives in
    // Tests/Runtime/Paint/Conversion/BackgroundResolverTests.cs and is not
    // duplicated here.
    public class BackgroundLonghandTests {
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

        // ── background-color §3.1 ────────────────────────────────────────

        [Test]
        public void Background_color_initial_is_transparent() {
            // CSS Backgrounds 3 §3.1: initial value `transparent`.
            var cs = Compute("");
            Assert.That(cs.Get("background-color"), Is.EqualTo("transparent"));
        }

        [Test]
        public void Background_color_named_color_round_trips() {
            var cs = Compute("#x { background-color: rebeccapurple; }");
            Assert.That(cs.Get("background-color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Background_color_hex_round_trips() {
            var cs = Compute("#x { background-color: #ff0000; }");
            Assert.That(cs.Get("background-color"), Is.EqualTo("#ff0000"));
        }

        [Test]
        public void Background_color_does_not_inherit() {
            // CSS Backgrounds 3 §3.1: background-color is not inherited.
            var cs = ComputeChild("div { background-color: red; }");
            Assert.That(cs.Get("background-color"), Is.EqualTo("transparent"),
                "background-color is non-inherited; child sees initial transparent");
        }

        // ── background-image §3.2 ────────────────────────────────────────

        [Test]
        public void Background_image_initial_is_none() {
            // CSS Backgrounds 3 §3.2: initial value `none`.
            var cs = Compute("");
            Assert.That(cs.Get("background-image"), Is.EqualTo("none"));
        }

        [Test]
        public void Background_image_url_round_trips() {
            var cs = Compute("#x { background-image: url(\"bg.png\"); }");
            Assert.That(cs.Get("background-image"), Is.EqualTo("url(\"bg.png\")"));
        }

        [Test]
        public void Background_image_linear_gradient_round_trips() {
            var cs = Compute("#x { background-image: linear-gradient(red, blue); }");
            Assert.That(cs.Get("background-image"), Is.EqualTo("linear-gradient(red, blue)"));
        }

        [Test]
        public void Background_image_does_not_inherit() {
            var cs = ComputeChild("div { background-image: url(img.png); }");
            Assert.That(cs.Get("background-image"), Is.EqualTo("none"),
                "background-image is non-inherited; child sees initial none");
        }

        // ── background-position §3.5 ──────────────────────────────────────

        [Test]
        public void Background_position_initial_is_0_0() {
            // CSS Backgrounds 3 §3.5: initial value `0% 0%`.
            var cs = Compute("");
            Assert.That(cs.Get("background-position"), Is.EqualTo("0% 0%"));
        }

        [Test]
        public void Background_position_keyword_pair_round_trips() {
            var cs = Compute("#x { background-position: center bottom; }");
            Assert.That(cs.Get("background-position"), Is.EqualTo("center bottom"));
        }

        [Test]
        public void Background_position_percentage_pair_round_trips() {
            var cs = Compute("#x { background-position: 75% 25%; }");
            Assert.That(cs.Get("background-position"), Is.EqualTo("75% 25%"));
        }

        [Test]
        public void Background_position_does_not_inherit() {
            var cs = ComputeChild("div { background-position: center; }");
            Assert.That(cs.Get("background-position"), Is.EqualTo("0% 0%"),
                "background-position is non-inherited; child sees initial 0% 0%");
        }

        // ── background-size §3.9 ──────────────────────────────────────────

        [Test]
        public void Background_size_initial_is_auto() {
            // CSS Backgrounds 3 §3.9: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("background-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void Background_size_cover_round_trips() {
            var cs = Compute("#x { background-size: cover; }");
            Assert.That(cs.Get("background-size"), Is.EqualTo("cover"));
        }

        [Test]
        public void Background_size_contain_round_trips() {
            var cs = Compute("#x { background-size: contain; }");
            Assert.That(cs.Get("background-size"), Is.EqualTo("contain"));
        }

        [Test]
        public void Background_size_explicit_dimensions_round_trip() {
            var cs = Compute("#x { background-size: 200px 100px; }");
            Assert.That(cs.Get("background-size"), Is.EqualTo("200px 100px"));
        }

        [Test]
        public void Background_size_does_not_inherit() {
            var cs = ComputeChild("div { background-size: cover; }");
            Assert.That(cs.Get("background-size"), Is.EqualTo("auto"),
                "background-size is non-inherited; child sees initial auto");
        }

        // ── background-repeat §3.4 ────────────────────────────────────────

        [Test]
        public void Background_repeat_initial_is_repeat() {
            // CSS Backgrounds 3 §3.4: initial value `repeat`.
            var cs = Compute("");
            Assert.That(cs.Get("background-repeat"), Is.EqualTo("repeat"));
        }

        [Test]
        public void Background_repeat_no_repeat_round_trips() {
            var cs = Compute("#x { background-repeat: no-repeat; }");
            Assert.That(cs.Get("background-repeat"), Is.EqualTo("no-repeat"));
        }

        [Test]
        public void Background_repeat_x_shorthand_round_trips() {
            // `repeat-x` is a shorthand for `repeat no-repeat` (horizontal only).
            // The cascade must carry whichever form the parser produces.
            var cs = Compute("#x { background-repeat: repeat-x; }");
            // Accept both canonical representations: `repeat-x` or `repeat no-repeat`.
            var got = cs.Get("background-repeat");
            Assert.That(got == "repeat-x" || got == "repeat no-repeat",
                "background-repeat: repeat-x must survive the cascade as repeat-x or repeat no-repeat, got: " + got);
        }

        [Test]
        public void Background_repeat_y_shorthand_round_trips() {
            // `repeat-y` is a shorthand for `no-repeat repeat` (vertical only).
            var cs = Compute("#x { background-repeat: repeat-y; }");
            var got = cs.Get("background-repeat");
            Assert.That(got == "repeat-y" || got == "no-repeat repeat",
                "background-repeat: repeat-y must survive the cascade as repeat-y or no-repeat repeat, got: " + got);
        }

        [Test]
        public void Background_repeat_two_value_axis_explicit_round_trips() {
            // Two-value form (`repeat no-repeat`) explicitly sets horizontal then
            // vertical axes — spec §3.4 axis-explicit syntax.
            var cs = Compute("#x { background-repeat: repeat no-repeat; }");
            var got = cs.Get("background-repeat");
            Assert.That(got == "repeat no-repeat" || got == "repeat-x",
                "Two-value axis-explicit form must survive cascade, got: " + got);
        }

        [Test]
        public void Background_repeat_round_keyword_round_trips() {
            var cs = Compute("#x { background-repeat: round; }");
            Assert.That(cs.Get("background-repeat"), Is.EqualTo("round"));
        }

        [Test]
        public void Background_repeat_space_keyword_round_trips() {
            var cs = Compute("#x { background-repeat: space; }");
            Assert.That(cs.Get("background-repeat"), Is.EqualTo("space"));
        }

        [Test]
        public void Background_repeat_does_not_inherit() {
            var cs = ComputeChild("div { background-repeat: no-repeat; }");
            Assert.That(cs.Get("background-repeat"), Is.EqualTo("repeat"),
                "background-repeat is non-inherited; child sees initial repeat");
        }

        // ── background-attachment §3.6 ────────────────────────────────────

        [Test]
        public void Background_attachment_initial_is_scroll() {
            // CSS Backgrounds 3 §3.6: initial value `scroll`.
            var cs = Compute("");
            Assert.That(cs.Get("background-attachment"), Is.EqualTo("scroll"));
        }

        [Test]
        public void Background_attachment_scroll_round_trips() {
            var cs = Compute("#x { background-attachment: scroll; }");
            Assert.That(cs.Get("background-attachment"), Is.EqualTo("scroll"));
        }

        [Test]
        public void Background_attachment_fixed_round_trips() {
            // `fixed` = viewport-relative positioning. Parse-level must accept and
            // carry this keyword; full viewport-fixed rendering is a separate concern.
            var cs = Compute("#x { background-attachment: fixed; }");
            Assert.That(cs.Get("background-attachment"), Is.EqualTo("fixed"));
        }

        [Test]
        public void Background_attachment_local_round_trips() {
            // `local` = scroll with the element's own scroll offset (CSS Backgrounds 3 §3.6).
            var cs = Compute("#x { background-attachment: local; }");
            Assert.That(cs.Get("background-attachment"), Is.EqualTo("local"));
        }

        [Test]
        public void Background_attachment_does_not_inherit() {
            var cs = ComputeChild("div { background-attachment: fixed; }");
            Assert.That(cs.Get("background-attachment"), Is.EqualTo("scroll"),
                "background-attachment is non-inherited; child sees initial scroll");
        }

        // ── background-origin §3.7 ────────────────────────────────────────

        [Test]
        public void Background_origin_initial_is_padding_box() {
            // CSS Backgrounds 3 §3.7: initial value `padding-box`.
            var cs = Compute("");
            Assert.That(cs.Get("background-origin"), Is.EqualTo("padding-box"));
        }

        [Test]
        public void Background_origin_border_box_round_trips() {
            var cs = Compute("#x { background-origin: border-box; }");
            Assert.That(cs.Get("background-origin"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Background_origin_content_box_round_trips() {
            var cs = Compute("#x { background-origin: content-box; }");
            Assert.That(cs.Get("background-origin"), Is.EqualTo("content-box"));
        }

        [Test]
        public void Background_origin_does_not_inherit() {
            var cs = ComputeChild("div { background-origin: content-box; }");
            Assert.That(cs.Get("background-origin"), Is.EqualTo("padding-box"),
                "background-origin is non-inherited; child sees initial padding-box");
        }

        // ── background-clip §3.8 ──────────────────────────────────────────

        [Test]
        public void Background_clip_initial_is_border_box() {
            // CSS Backgrounds 3 §3.8: initial value `border-box`.
            var cs = Compute("");
            Assert.That(cs.Get("background-clip"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Background_clip_padding_box_round_trips() {
            var cs = Compute("#x { background-clip: padding-box; }");
            Assert.That(cs.Get("background-clip"), Is.EqualTo("padding-box"));
        }

        [Test]
        public void Background_clip_content_box_round_trips() {
            var cs = Compute("#x { background-clip: content-box; }");
            Assert.That(cs.Get("background-clip"), Is.EqualTo("content-box"));
        }

        [Test]
        public void Background_clip_text_round_trips() {
            // `text` clips the background to the foreground text glyph outlines.
            var cs = Compute("#x { background-clip: text; }");
            Assert.That(cs.Get("background-clip"), Is.EqualTo("text"));
        }

        [Test]
        public void Background_clip_does_not_inherit() {
            var cs = ComputeChild("div { background-clip: content-box; }");
            Assert.That(cs.Get("background-clip"), Is.EqualTo("border-box"),
                "background-clip is non-inherited; child sees initial border-box");
        }
    }
}
