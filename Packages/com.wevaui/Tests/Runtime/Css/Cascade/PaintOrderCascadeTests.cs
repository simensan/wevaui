using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Paint Order §6 (SVG Paint Server L1, inherited by CSS Fill/Stroke)
    //
    // `paint-order` controls the sequence in which fill, stroke, and markers
    // are painted for an element.  Inherited per spec; initial = `normal`
    // (equivalent to fill, stroke, markers in that order).
    //
    // Weva registers `paint-order` as a string-passthrough inherited property.
    // The tests pin the parse → cascade → Get round-trip; the renderer always
    // paints fill before stroke in v1 and ignores the value at paint time.
    public class PaintOrderCascadeTests {
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

        // ── Initial value ─────────────────────────────────────────────────

        [Test]
        public void Paint_order_initial_value_is_normal() {
            // SVG Paint Order §6: initial = normal.
            var cs = Compute("");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("normal"));
        }

        // ── Single-keyword forms ──────────────────────────────────────────

        [Test]
        public void Paint_order_fill_round_trips() {
            var cs = Compute("#x { paint-order: fill; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("fill"));
        }

        [Test]
        public void Paint_order_stroke_round_trips() {
            var cs = Compute("#x { paint-order: stroke; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke"));
        }

        [Test]
        public void Paint_order_markers_round_trips() {
            var cs = Compute("#x { paint-order: markers; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("markers"));
        }

        // ── Multi-keyword combos ──────────────────────────────────────────

        [Test]
        public void Paint_order_stroke_fill_round_trips() {
            // Stroke first, then fill — common SVG technique for legible text.
            var cs = Compute("#x { paint-order: stroke fill; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke fill"));
        }

        [Test]
        public void Paint_order_stroke_fill_markers_round_trips() {
            var cs = Compute("#x { paint-order: stroke fill markers; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke fill markers"));
        }

        [Test]
        public void Paint_order_markers_stroke_fill_round_trips() {
            var cs = Compute("#x { paint-order: markers stroke fill; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("markers stroke fill"));
        }

        [Test]
        public void Paint_order_fill_markers_round_trips() {
            var cs = Compute("#x { paint-order: fill markers; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("fill markers"));
        }

        // ── Inheritance ───────────────────────────────────────────────────

        [Test]
        public void Paint_order_is_inherited() {
            // SVG Paint Order §6: paint-order is inherited.
            var cs = ComputeChild("#p { paint-order: stroke fill; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke fill"));
        }

        [Test]
        public void Paint_order_child_can_override_inherited_value() {
            var cs = ComputeChild(
                "#p { paint-order: stroke fill; } " +
                "#c { paint-order: markers; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("markers"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Paint_order_initial_keyword_restores_normal() {
            var cs = Compute("#x { paint-order: stroke fill; paint-order: initial; }");
            Assert.That(cs.Get("paint-order"), Is.EqualTo("normal"));
        }

        [Test]
        public void Paint_order_inherit_keyword_propagates_parent_value() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { paint-order: stroke fill; } #c { paint-order: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke fill"));
        }

        [Test]
        public void Paint_order_unset_on_inherited_acts_as_inherit() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { paint-order: stroke fill; } #c { paint-order: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("paint-order"), Is.EqualTo("stroke fill"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Paint_order_id_beats_element_selector() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { paint-order: stroke; } #x { paint-order: fill markers; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("paint-order"), Is.EqualTo("fill markers"));
        }
    }
}
