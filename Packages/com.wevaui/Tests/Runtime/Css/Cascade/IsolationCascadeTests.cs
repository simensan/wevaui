using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Compositing and Blending L1 §2.2 — `isolation`.
    //
    // `isolation` controls whether an element forms a new stacking context
    // for the purpose of mix-blend-mode compositing.
    //
    // Values: auto | isolate
    // Inherited: no
    // Initial: auto
    //
    // Weva registers `isolation` as a cascade-pass-through. The compositing
    // layer reads the cascaded string; actual blending isolation is render-path
    // work tracked separately.
    public class IsolationCascadeTests {
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
        public void Isolation_initial_value_is_auto() {
            // CSS Compositing L1 §2.2: initial = `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"));
        }

        // ── Keyword values ────────────────────────────────────────────────

        [Test]
        public void Isolation_auto_explicit_round_trips() {
            var cs = Compute("#x { isolation: auto; }");
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"));
        }

        [Test]
        public void Isolation_isolate_round_trips() {
            // CSS Compositing L1 §2.2: `isolate` creates a new isolation group.
            var cs = Compute("#x { isolation: isolate; }");
            Assert.That(cs.Get("isolation"), Is.EqualTo("isolate"));
        }

        // ── Non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Isolation_does_not_inherit_to_child() {
            // CSS Compositing L1 §2.2: Inherited: no.
            // A child should see the initial value `auto` even if parent is `isolate`.
            var cs = ComputeChild("#parent { isolation: isolate; }");
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"),
                "isolation is non-inherited; child must not pick up parent value");
        }

        [Test]
        public void Isolation_child_rule_sets_independently() {
            // Non-inherited: child explicitly overrides.
            var cs = ComputeChild(
                "#parent { isolation: isolate; } " +
                "#child  { isolation: isolate; }");
            Assert.That(cs.Get("isolation"), Is.EqualTo("isolate"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Isolation_initial_keyword_restores_auto() {
            var cs = Compute("#x { isolation: initial; }");
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"));
        }

        [Test]
        public void Isolation_inherit_keyword_propagates_parent_value() {
            // Explicit `inherit` forces propagation even for non-inherited props.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { isolation: isolate; } " +
                       "#child  { isolation: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("isolation"), Is.EqualTo("isolate"));
        }

        [Test]
        public void Isolation_unset_on_non_inherited_property_resolves_as_initial() {
            // `unset` on a non-inherited property acts like `initial`.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { isolation: isolate; } " +
                       "#child  { isolation: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Isolation_id_selector_beats_class_selector() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { isolation: isolate; } #x { isolation: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("isolation"), Is.EqualTo("auto"));
        }

        [Test]
        public void Isolation_later_rule_beats_earlier_at_equal_specificity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { isolation: auto; } #x { isolation: isolate; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("isolation"), Is.EqualTo("isolate"));
        }
    }
}
