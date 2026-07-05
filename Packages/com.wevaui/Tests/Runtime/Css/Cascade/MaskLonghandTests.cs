using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Masking Module Level 1 §3-§6 — mask longhand cascade behaviour.
    //
    // CssProperties.cs registers all eight mask longhands with their spec
    // initial values (mask-image:none, mask-mode:match-source,
    // mask-repeat:repeat, mask-position:0% 0%, mask-clip:border-box,
    // mask-origin:border-box, mask-size:auto, mask-composite:add — all
    // non-inherited). MaskShorthandTests covers the shorthand expander
    // but the longhands themselves had no direct cascade coverage. These
    // tests pin each longhand's:
    //
    //   - initial value (no rule → spec-mandated default)
    //   - parse → cascade → Get round-trip on the common keyword set
    //   - non-inheritance (parent value does NOT leak to child)
    //
    // The paint side (URP shader compositing, layered mask resolution)
    // lives in BoxToPaintConverter / MaskResolver and is exercised by
    // Tests/Runtime/Paint/Conversion/MaskResolver* tests; this file
    // covers the cascade boundary only.
    public class MaskLonghandTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css, string targetSelector = "#x") {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── Initial values per CSS Masking 1 spec ───────────────────────

        [Test]
        public void Mask_image_initial_is_none() {
            // CSS Masking 1 §3.1: initial value `none`.
            var cs = Compute("");
            Assert.That(cs.Get("mask-image"), Is.EqualTo("none"));
        }

        [Test]
        public void Mask_mode_initial_is_match_source() {
            // CSS Masking 1 §3.2: initial value `match-source`. The
            // image's intrinsic alpha vs luminance use is chosen by
            // source type; SVG mask elements use luminance, every
            // other image source uses alpha.
            var cs = Compute("");
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("match-source"));
        }

        [Test]
        public void Mask_repeat_initial_is_repeat() {
            // CSS Masking 1 §4.1: initial value `repeat`.
            var cs = Compute("");
            Assert.That(cs.Get("mask-repeat"), Is.EqualTo("repeat"));
        }

        [Test]
        public void Mask_position_initial_is_zero_zero() {
            // CSS Masking 1 §4.2: initial value `0% 0%` (= top-left).
            var cs = Compute("");
            Assert.That(cs.Get("mask-position"), Is.EqualTo("0% 0%"));
        }

        [Test]
        public void Mask_size_initial_is_auto() {
            // CSS Masking 1 §4.3: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("mask-size"), Is.EqualTo("auto"));
        }

        [Test]
        public void Mask_origin_initial_is_border_box() {
            // CSS Masking 1 §5.1: initial value `border-box` —
            // explicitly differs from background-origin's `padding-box`
            // default.
            var cs = Compute("");
            Assert.That(cs.Get("mask-origin"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Mask_clip_initial_is_border_box() {
            // CSS Masking 1 §5.2: initial value `border-box`.
            var cs = Compute("");
            Assert.That(cs.Get("mask-clip"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Mask_composite_initial_is_add() {
            // CSS Masking 1 §6.1: initial value `add` (mask layers
            // compose with the standard Porter-Duff source-over).
            var cs = Compute("");
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("add"));
        }

        // ── Common keyword round-trips ──────────────────────────────────

        [Test]
        public void Mask_mode_alpha_round_trips() {
            // §3.2 — `alpha`: forces alpha-channel masking regardless of
            // source type. Useful for non-SVG images intended as masks.
            var cs = Compute("#x { mask-mode: alpha; }");
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("alpha"));
        }

        [Test]
        public void Mask_mode_luminance_round_trips() {
            // §3.2 — `luminance`: forces luminance-channel masking.
            var cs = Compute("#x { mask-mode: luminance; }");
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("luminance"));
        }

        [Test]
        public void Mask_repeat_no_repeat_round_trips() {
            var cs = Compute("#x { mask-repeat: no-repeat; }");
            Assert.That(cs.Get("mask-repeat"), Is.EqualTo("no-repeat"));
        }

        [Test]
        public void Mask_repeat_round_trips() {
            var cs = Compute("#x { mask-repeat: round; }");
            Assert.That(cs.Get("mask-repeat"), Is.EqualTo("round"));
        }

        [Test]
        public void Mask_repeat_space_round_trips() {
            var cs = Compute("#x { mask-repeat: space; }");
            Assert.That(cs.Get("mask-repeat"), Is.EqualTo("space"));
        }

        [Test]
        public void Mask_position_explicit_round_trips() {
            var cs = Compute("#x { mask-position: 50% 100%; }");
            Assert.That(cs.Get("mask-position"), Is.EqualTo("50% 100%"));
        }

        [Test]
        public void Mask_position_keyword_round_trips() {
            var cs = Compute("#x { mask-position: center bottom; }");
            Assert.That(cs.Get("mask-position"), Is.EqualTo("center bottom"));
        }

        [Test]
        public void Mask_size_cover_round_trips() {
            // CSS Masking 1 §4.3 / Backgrounds & Borders 3 §3.9 —
            // mask-size shares its keyword set with background-size.
            var cs = Compute("#x { mask-size: cover; }");
            Assert.That(cs.Get("mask-size"), Is.EqualTo("cover"));
        }

        [Test]
        public void Mask_size_contain_round_trips() {
            var cs = Compute("#x { mask-size: contain; }");
            Assert.That(cs.Get("mask-size"), Is.EqualTo("contain"));
        }

        [Test]
        public void Mask_origin_padding_box_round_trips() {
            var cs = Compute("#x { mask-origin: padding-box; }");
            Assert.That(cs.Get("mask-origin"), Is.EqualTo("padding-box"));
        }

        [Test]
        public void Mask_origin_content_box_round_trips() {
            var cs = Compute("#x { mask-origin: content-box; }");
            Assert.That(cs.Get("mask-origin"), Is.EqualTo("content-box"));
        }

        [Test]
        public void Mask_clip_padding_box_round_trips() {
            var cs = Compute("#x { mask-clip: padding-box; }");
            Assert.That(cs.Get("mask-clip"), Is.EqualTo("padding-box"));
        }

        [Test]
        public void Mask_clip_no_clip_round_trips() {
            // CSS Masking 1 §5.2 — `no-clip` means: don't clip the mask
            // to any geometry, it covers the whole bounding box.
            var cs = Compute("#x { mask-clip: no-clip; }");
            Assert.That(cs.Get("mask-clip"), Is.EqualTo("no-clip"));
        }

        [Test]
        public void Mask_composite_subtract_round_trips() {
            // §6.1 — `subtract`: mask layer composes with Porter-Duff
            // source-out (this layer is subtracted from prior layers).
            var cs = Compute("#x { mask-composite: subtract; }");
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("subtract"));
        }

        [Test]
        public void Mask_composite_intersect_round_trips() {
            var cs = Compute("#x { mask-composite: intersect; }");
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("intersect"));
        }

        [Test]
        public void Mask_composite_exclude_round_trips() {
            var cs = Compute("#x { mask-composite: exclude; }");
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("exclude"));
        }

        // ── Non-inheritance ─────────────────────────────────────────────

        [Test]
        public void Mask_image_does_not_inherit() {
            // CSS Masking 1 §3.1: mask-image is NOT inherited. A parent
            // value must not leak through to the child. The child's
            // computed value stays at the property's initial (`none`).
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mask-image: linear-gradient(black, transparent); }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("mask-image"), Is.EqualTo("none"),
                "mask-image is non-inherited; child must see the initial value, not the parent's gradient");
        }

        [Test]
        public void Mask_mode_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mask-mode: alpha; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("mask-mode"), Is.EqualTo("match-source"),
                "mask-mode is non-inherited; child must see the initial match-source");
        }

        [Test]
        public void Mask_composite_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mask-composite: subtract; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("mask-composite"), Is.EqualTo("add"),
                "mask-composite is non-inherited; child must see the initial add");
        }
    }
}
