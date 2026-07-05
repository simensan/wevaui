using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Pointer Events / Basic User Interface 4 §13 + SVG 2 §17.
    //
    // `pointer-events` is NOT inherited for HTML elements (CSS UI4 §13 /
    // SVG has it inherited, but HTML usage is non-inherited). Initial = `auto`.
    //
    // For HTML content the only values with defined semantics are `auto` and
    // `none`. The SVG-extended keywords (`all`, `visible`, `painted`, etc.)
    // are accepted by the parser and round-trip through the cascade as raw
    // strings; the engine hit-tester only consumes `auto`/`none`.
    public class PointerEventsCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── Initial value ─────────────────────────────────────────────────

        [Test]
        public void Pointer_events_initial_value_is_auto() {
            // CSS Pointer Events §13 — initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("auto"));
        }

        // ── HTML-relevant keywords ────────────────────────────────────────

        [Test]
        public void Pointer_events_none_round_trips() {
            // CSS UI4 §13 — `none`: element is not the target of pointer events.
            var cs = Compute("#x { pointer-events: none; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("none"));
        }

        [Test]
        public void Pointer_events_auto_explicit_round_trips() {
            // Explicit `auto` must survive the cascade identical to the initial.
            var cs = Compute("#x { pointer-events: auto; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("auto"));
        }

        // ── SVG-extended keywords (cascade round-trip only) ───────────────

        [Test]
        public void Pointer_events_all_round_trips() {
            // SVG 2 §17 — `all`: element receives pointer events regardless
            // of fill/stroke/visibility. Round-trips through cascade; hit-tester
            // is HTML-only so runtime effect is same as `auto`.
            var cs = Compute("#x { pointer-events: all; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("all"));
        }

        [Test]
        public void Pointer_events_visible_round_trips() {
            var cs = Compute("#x { pointer-events: visible; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("visible"));
        }

        [Test]
        public void Pointer_events_painted_round_trips() {
            var cs = Compute("#x { pointer-events: painted; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("painted"));
        }

        [Test]
        public void Pointer_events_visiblePainted_round_trips() {
            var cs = Compute("#x { pointer-events: visiblePainted; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("visiblePainted"));
        }

        [Test]
        public void Pointer_events_fill_round_trips() {
            var cs = Compute("#x { pointer-events: fill; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("fill"));
        }

        [Test]
        public void Pointer_events_stroke_round_trips() {
            var cs = Compute("#x { pointer-events: stroke; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("stroke"));
        }

        // ── Non-inheritance (HTML) ────────────────────────────────────────

        [Test]
        public void Pointer_events_does_not_inherit_from_parent() {
            // CSS UI4 §13: NOT inherited for HTML — child keeps initial `auto`
            // when only the parent is set to `none`.
            var cs = ComputeChild("#parent { pointer-events: none; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("auto"),
                "pointer-events must NOT inherit to HTML children");
        }

        [Test]
        public void Pointer_events_inherit_keyword_overrides_non_inheritance() {
            // Explicit `inherit` keyword must still propagate the parent value.
            var cs = ComputeChild(
                "#parent { pointer-events: none; } " +
                "#child  { pointer-events: inherit; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("none"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Pointer_events_unset_resolves_to_initial_for_non_inherited() {
            // `unset` on a non-inherited property acts as `initial` (auto).
            var cs = Compute("#x { pointer-events: none; }");
            // Override with unset via a second rule at lower specificity —
            // use the element directly so unset wins:
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { pointer-events: none; } #x { pointer-events: unset; }")
            });
            var cs2 = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs2.Get("pointer-events"), Is.EqualTo("auto"));
        }

        [Test]
        public void Pointer_events_specificity_higher_id_wins() {
            // Higher-specificity rule (#x) beats class rule (.x).
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { pointer-events: all; } #x { pointer-events: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("none"));
        }
    }
}
