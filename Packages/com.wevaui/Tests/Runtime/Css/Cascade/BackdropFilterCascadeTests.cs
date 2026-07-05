using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Filter Effects Module Level 1 — cascade-side coverage for
    // `backdrop-filter:`.
    //
    // `backdrop-filter` shares the same value syntax as `filter` but applies
    // to the area behind the element (used for frosted-glass effects).
    // CssProperties.cs registers it at line ~886 with initial value `none`
    // and non-inherited. These tests pin:
    //   - initial value (`none`) when no rule applies
    //   - parse → cascade → Get round-trip for the canonical filter functions
    //   - blur + brightness chain (the most common backdrop pattern)
    //   - `backdrop-filter: none` explicit keyword
    //   - non-inheritance per spec (it is non-inherited like `filter`)
    //   - independence from the `filter` property (writing one must not affect
    //     the other)
    //
    // Rendering-side tests (frosted-glass compositing, offscreen RT) live in
    // Tests/Runtime/Rendering/URP/BackdropFilterClipTests.cs which is excluded
    // from the headless TestVerifyAll harness. This file is headless-safe.
    public class BackdropFilterCascadeTests {
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

        // ══════════════════════════════════════════════════════════════════
        // Initial value and `none` keyword
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_initial_value_is_none() {
            // CSS Filter Effects 1 §2: `backdrop-filter` initial value is `none`.
            var cs = Compute("");
            Assert.That(cs.Get("backdrop-filter"), Is.EqualTo("none"));
        }

        [Test]
        public void Backdrop_filter_none_explicit_round_trips() {
            // Explicitly setting `none` should store the keyword unchanged.
            var cs = Compute("#x { backdrop-filter: none; }");
            Assert.That(cs.Get("backdrop-filter"), Is.EqualTo("none"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Per-function cascade round-trips
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_blur_round_trips() {
            // blur() on backdrop-filter — the frosted-glass workhorse.
            var cs = Compute("#x { backdrop-filter: blur(10px); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("10px"));
        }

        [Test]
        public void Backdrop_filter_brightness_number_round_trips() {
            var cs = Compute("#x { backdrop-filter: brightness(0.8); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("brightness"));
            Assert.That(v, Does.Contain("0.8"));
        }

        [Test]
        public void Backdrop_filter_contrast_round_trips() {
            var cs = Compute("#x { backdrop-filter: contrast(1.1); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("contrast"));
            Assert.That(v, Does.Contain("1.1"));
        }

        [Test]
        public void Backdrop_filter_grayscale_percentage_round_trips() {
            var cs = Compute("#x { backdrop-filter: grayscale(30%); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("grayscale"));
            Assert.That(v, Does.Contain("30%"));
        }

        [Test]
        public void Backdrop_filter_opacity_round_trips() {
            var cs = Compute("#x { backdrop-filter: opacity(0.7); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("opacity"));
            Assert.That(v, Does.Contain("0.7"));
        }

        [Test]
        public void Backdrop_filter_saturate_round_trips() {
            var cs = Compute("#x { backdrop-filter: saturate(1.8); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("saturate"));
            Assert.That(v, Does.Contain("1.8"));
        }

        [Test]
        public void Backdrop_filter_hue_rotate_round_trips() {
            var cs = Compute("#x { backdrop-filter: hue-rotate(180deg); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("hue-rotate"));
            Assert.That(v, Does.Contain("180deg"));
        }

        [Test]
        public void Backdrop_filter_invert_round_trips() {
            var cs = Compute("#x { backdrop-filter: invert(0.5); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("invert"));
            Assert.That(v, Does.Contain("0.5"));
        }

        [Test]
        public void Backdrop_filter_sepia_round_trips() {
            var cs = Compute("#x { backdrop-filter: sepia(0.6); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("sepia"));
            Assert.That(v, Does.Contain("0.6"));
        }

        [Test]
        public void Backdrop_filter_drop_shadow_round_trips() {
            var cs = Compute("#x { backdrop-filter: drop-shadow(0 2px 4px black); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("drop-shadow"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Chain composition
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_blur_brightness_chain_round_trips() {
            // The most common frosted-glass pattern: blur + darken/lighten.
            var cs = Compute("#x { backdrop-filter: blur(8px) brightness(0.9); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("brightness"));
            // blur must appear before brightness in the stored text.
            Assert.That(v.IndexOf("blur", System.StringComparison.Ordinal),
                Is.LessThan(v.IndexOf("brightness", System.StringComparison.Ordinal)));
        }

        [Test]
        public void Backdrop_filter_three_function_chain_preserves_order() {
            // Three-function chain; all three must appear and in authored order.
            var cs = Compute("#x { backdrop-filter: blur(12px) brightness(0.85) saturate(1.2); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("brightness"));
            Assert.That(v, Does.Contain("saturate"));
            int blurIdx = v.IndexOf("blur", System.StringComparison.Ordinal);
            int brightIdx = v.IndexOf("brightness", System.StringComparison.Ordinal);
            int satIdx = v.IndexOf("saturate", System.StringComparison.Ordinal);
            Assert.That(blurIdx, Is.LessThan(brightIdx));
            Assert.That(brightIdx, Is.LessThan(satIdx));
        }

        // ══════════════════════════════════════════════════════════════════
        // Non-inheritance
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_does_not_inherit() {
            // CSS Filter Effects 1 §2: `backdrop-filter` is non-inherited.
            var cs = ComputeChild("div { backdrop-filter: blur(8px) brightness(0.9); }");
            Assert.That(cs.Get("backdrop-filter"), Is.EqualTo("none"),
                "backdrop-filter is non-inherited; child must see initial `none`");
        }

        [Test]
        public void Backdrop_filter_does_not_inherit_single_function() {
            var cs = ComputeChild("div { backdrop-filter: sepia(0.5); }");
            Assert.That(cs.Get("backdrop-filter"), Is.EqualTo("none"),
                "backdrop-filter is non-inherited; child must not see parent sepia filter");
        }

        // ══════════════════════════════════════════════════════════════════
        // Independence from `filter`
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_and_filter_are_independent() {
            // Setting backdrop-filter must not affect the `filter` property
            // and vice versa — they are registered as separate properties.
            var cs = Compute("#x { filter: blur(4px); backdrop-filter: brightness(0.8); }");
            var filter = cs.Get("filter");
            var bdFilter = cs.Get("backdrop-filter");
            Assert.That(filter, Does.Contain("blur"),
                "`filter` must contain its authored blur");
            Assert.That(bdFilter, Does.Contain("brightness"),
                "`backdrop-filter` must contain its authored brightness");
            // Cross-contamination check.
            Assert.That(filter, Does.Not.Contain("brightness"),
                "`filter` must not contain backdrop-filter's brightness");
            Assert.That(bdFilter, Does.Not.Contain("blur"),
                "`backdrop-filter` must not contain filter's blur");
        }

        [Test]
        public void Filter_none_does_not_clear_backdrop_filter() {
            // Resetting `filter` to none must leave `backdrop-filter` untouched.
            var cs = Compute("#x { filter: none; backdrop-filter: blur(6px); }");
            Assert.That(cs.Get("filter"), Is.EqualTo("none"));
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("blur"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Cascade mechanics
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Backdrop_filter_higher_specificity_wins() {
            // #x (0,1,0) beats div (0,0,1): the ID rule must win.
            var cs = Compute("div { backdrop-filter: grayscale(1); } #x { backdrop-filter: blur(10px); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("blur"),
                "Higher-specificity #x rule must win");
        }

        [Test]
        public void Backdrop_filter_source_order_tiebreak() {
            // Same specificity: later source wins.
            var cs = Compute("#x { backdrop-filter: sepia(1); } #x { backdrop-filter: invert(0.5); }");
            var v = cs.Get("backdrop-filter");
            Assert.That(v, Does.Contain("invert"),
                "Later same-specificity declaration must win (source-order tiebreak)");
        }
    }
}
