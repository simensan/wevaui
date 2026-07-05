using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Fragmentation L3 §3 — break-before / break-after / break-inside
    //
    // These properties declare forced and discretionary fragmentation breaks
    // around and within boxes.  They are NOT inherited per spec.  Initial
    // value = `auto` for all three.
    //
    // Weva registers them as string-passthrough non-inherited properties.
    // Game UI is non-paginated so these never trigger a real fragment break;
    // the cascade round-trip is tested here so computed-style consumers (e.g.
    // @page tooling) can read the correct value.
    public class BreakControlCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><div id=\"c\"></div></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ── Initial values ────────────────────────────────────────────────

        [Test]
        public void Break_before_initial_value_is_auto() {
            // Fragmentation L3 §3.1: initial = auto.
            var cs = Compute("");
            Assert.That(cs.Get("break-before"), Is.EqualTo("auto"));
        }

        [Test]
        public void Break_after_initial_value_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("break-after"), Is.EqualTo("auto"));
        }

        [Test]
        public void Break_inside_initial_value_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("auto"));
        }

        // ── break-before keyword round-trips ──────────────────────────────

        [Test]
        public void Break_before_always_round_trips() {
            // CSS Fragmentation L3 §3.1: `always` forces a break before.
            var cs = Compute("#x { break-before: always; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("always"));
        }

        [Test]
        public void Break_before_avoid_round_trips() {
            var cs = Compute("#x { break-before: avoid; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("avoid"));
        }

        [Test]
        public void Break_before_page_round_trips() {
            var cs = Compute("#x { break-before: page; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("page"));
        }

        [Test]
        public void Break_before_avoid_page_round_trips() {
            var cs = Compute("#x { break-before: avoid-page; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("avoid-page"));
        }

        [Test]
        public void Break_before_column_round_trips() {
            var cs = Compute("#x { break-before: column; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("column"));
        }

        // ── break-after keyword round-trips ───────────────────────────────

        [Test]
        public void Break_after_always_round_trips() {
            var cs = Compute("#x { break-after: always; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("always"));
        }

        [Test]
        public void Break_after_avoid_round_trips() {
            var cs = Compute("#x { break-after: avoid; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("avoid"));
        }

        [Test]
        public void Break_after_left_right_round_trips() {
            // Fragmentation L3 §3.1: left/right force breaks to an odd/even page.
            var cs = Compute("#x { break-after: left; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("left"));

            cs = Compute("#x { break-after: right; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("right"));
        }

        // ── break-inside keyword round-trips ──────────────────────────────

        [Test]
        public void Break_inside_avoid_round_trips() {
            // Fragmentation L3 §3.2: `avoid` prevents breaks inside the box.
            var cs = Compute("#x { break-inside: avoid; }");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("avoid"));
        }

        [Test]
        public void Break_inside_avoid_page_round_trips() {
            var cs = Compute("#x { break-inside: avoid-page; }");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("avoid-page"));
        }

        [Test]
        public void Break_inside_avoid_column_round_trips() {
            var cs = Compute("#x { break-inside: avoid-column; }");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("avoid-column"));
        }

        // ── Non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Break_before_does_not_inherit() {
            // Fragmentation L3 §3: break-before is NOT inherited.
            var cs = ComputeChild("#p { break-before: always; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("auto"),
                "break-before must not propagate to children");
        }

        [Test]
        public void Break_after_does_not_inherit() {
            var cs = ComputeChild("#p { break-after: always; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("auto"),
                "break-after must not propagate to children");
        }

        [Test]
        public void Break_inside_does_not_inherit() {
            var cs = ComputeChild("#p { break-inside: avoid; }");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("auto"),
                "break-inside must not propagate to children");
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Break_before_initial_keyword_restores_auto() {
            var cs = Compute("#x { break-before: always; break-before: initial; }");
            Assert.That(cs.Get("break-before"), Is.EqualTo("auto"));
        }

        [Test]
        public void Break_after_initial_keyword_restores_auto() {
            var cs = Compute("#x { break-after: page; break-after: initial; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("auto"));
        }

        [Test]
        public void Break_inside_initial_keyword_restores_auto() {
            var cs = Compute("#x { break-inside: avoid; break-inside: initial; }");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("auto"));
        }

        // ── Three properties are independent ─────────────────────────────

        [Test]
        public void Break_properties_do_not_bleed_into_each_other() {
            var cs = Compute("#x { break-before: always; }");
            Assert.That(cs.Get("break-after"), Is.EqualTo("auto"),
                "break-after must remain auto when only break-before is set");
            Assert.That(cs.Get("break-inside"), Is.EqualTo("auto"),
                "break-inside must remain auto when only break-before is set");
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Break_before_id_beats_element_selector() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { break-before: always; } #x { break-before: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("break-before"), Is.EqualTo("auto"));
        }
    }
}
