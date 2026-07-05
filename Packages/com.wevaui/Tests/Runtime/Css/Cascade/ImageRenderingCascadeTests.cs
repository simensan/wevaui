using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Images L3 §5.3 — `image-rendering` cascade.
    //
    // Controls the algorithm used to scale a raster image to its target
    // box. Inherited per spec (CSS Images L3 §5.3) so a top-level
    // `body { image-rendering: pixelated }` propagates to every <img>
    // descendant without per-image rule duplication.
    //
    // Registration (CssProperties.BuildRegistry):
    //   image-rendering    inherited=true  initial="auto"
    //
    // Keyword set (CSS Images L3 §5.3):
    //   auto                 — UA chooses (typically bilinear smoothing)
    //   crisp-edges          — preserve hard edges; nearest or specialised filter
    //   pixelated            — nearest-neighbour (pixel-art use case)
    //   smooth               — bilinear/bicubic smoothing (older)
    //   high-quality         — best available filter (CSS Images L4)
    //
    // The visual resolver `ImageRenderingResolver` is covered by
    // `ImageRenderingResolverTests`; this file pins the parse → cascade →
    // Get round-trip + inheritance + cascade-keyword semantics.
    public class ImageRenderingCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ─── Initial value ──────────────────────────────────────────────

        [Test]
        public void ImageRendering_initial_is_auto() {
            // §5.3: initial = `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("auto"));
        }

        // ─── Keyword round-trips ───────────────────────────────────────

        [Test]
        public void ImageRendering_auto_round_trips() {
            var cs = Compute("#x { image-rendering: auto; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("auto"));
        }

        [Test]
        public void ImageRendering_pixelated_round_trips() {
            // §5.3: `pixelated` requests nearest-neighbour scaling —
            // the canonical pixel-art game UI value.
            var cs = Compute("#x { image-rendering: pixelated; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("pixelated"));
        }

        [Test]
        public void ImageRendering_crisp_edges_round_trips() {
            var cs = Compute("#x { image-rendering: crisp-edges; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("crisp-edges"));
        }

        [Test]
        public void ImageRendering_smooth_round_trips() {
            // §5.3 — `smooth` is an older keyword that resolves to a
            // smoothing algorithm. The cascade carries the keyword as-is;
            // the visual resolver maps it to `auto` (see
            // ImageRenderingResolverTests).
            var cs = Compute("#x { image-rendering: smooth; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("smooth"));
        }

        [Test]
        public void ImageRendering_high_quality_round_trips() {
            // CSS Images L4 keyword — best available filter.
            var cs = Compute("#x { image-rendering: high-quality; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("high-quality"));
        }

        // ─── Inheritance (yes per spec) ────────────────────────────────

        [Test]
        public void ImageRendering_inherits_from_parent() {
            // §5.3: inherited = yes — a top-level `body { image-rendering:
            // pixelated }` propagates to every descendant.
            var child = ComputeChild("#p { image-rendering: pixelated; }");
            Assert.That(child.Get("image-rendering"), Is.EqualTo("pixelated"));
        }

        [Test]
        public void ImageRendering_child_overrides_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { image-rendering: pixelated; } #c { image-rendering: auto; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("image-rendering"), Is.EqualTo("auto"));
        }

        // ─── Cascade keyword semantics ─────────────────────────────────

        [Test]
        public void ImageRendering_important_wins_cascade() {
            var cs = Compute("#x { image-rendering: pixelated !important; image-rendering: auto; }");
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("pixelated"));
        }

        [Test]
        public void ImageRendering_initial_keyword_resets_to_auto() {
            // CSS Cascade L5 §7.1 — `initial` resolves to the property's
            // spec initial regardless of parent.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { image-rendering: pixelated; } #c { image-rendering: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("image-rendering"), Is.EqualTo("auto"),
                "`initial` must resolve to spec initial `auto`, not inherit parent's `pixelated`");
        }

        [Test]
        public void ImageRendering_inherit_keyword_pulls_parent() {
            // CSS Cascade L5 §7.2 — explicit `inherit` always pulls parent.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { image-rendering: pixelated; } #c { image-rendering: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("image-rendering"), Is.EqualTo("pixelated"));
        }

        [Test]
        public void ImageRendering_unset_on_inherited_acts_as_inherit() {
            // §5.3 inherited → `unset` falls through to inherit.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { image-rendering: crisp-edges; } #c { image-rendering: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("image-rendering"), Is.EqualTo("crisp-edges"));
        }

        // ─── Specificity / origin ──────────────────────────────────────

        [Test]
        public void ImageRendering_id_beats_element_selector() {
            var doc = Html("<img id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author("img { image-rendering: smooth; } #x { image-rendering: pixelated; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("pixelated"));
        }

        [Test]
        public void ImageRendering_late_class_loses_to_earlier_id() {
            // Specificity: ID (1,0,0) > class (0,1,0); later rule with
            // lower specificity still loses.
            var doc = Html("<div id=\"x\" class=\"sprite\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { image-rendering: pixelated; } .sprite { image-rendering: smooth; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("image-rendering"), Is.EqualTo("pixelated"));
        }
    }
}
