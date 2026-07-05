using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Fragmentation L3 §6.1 / CSS Box Model L4 §5 — `box-decoration-break`.
    //
    // `box-decoration-break` controls how element decorations (background,
    // borders, border-image, box-shadow, border-radius, etc.) are rendered
    // when the element is fragmented across lines, columns, or pages.
    //
    // Values: slice | clone
    // Inherited: no
    // Initial: slice
    //
    // Weva registers `box-decoration-break` as a non-inherited cascade
    // pass-through. Only `slice` is honoured in v1 rendering; `clone` round-trips
    // through the cascade but is not acted on by the paint layer (tracked as B22).
    public class BoxDecorationBreakCascadeTests {
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
        public void Box_decoration_break_initial_value_is_slice() {
            // CSS Fragmentation L3 §6.1: initial = `slice`.
            var cs = Compute("");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"));
        }

        // ── Keyword values ────────────────────────────────────────────────

        [Test]
        public void Box_decoration_break_slice_explicit_round_trips() {
            var cs = Compute("#x { box-decoration-break: slice; }");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"));
        }

        [Test]
        public void Box_decoration_break_clone_round_trips() {
            // `clone` wraps each fragment in its own decoration box.
            // v1 note: cascade round-trips fine; paint-layer does not yet act on it.
            var cs = Compute("#x { box-decoration-break: clone; }");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("clone"));
        }

        // ── Non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Box_decoration_break_does_not_inherit_to_child() {
            // CSS Fragmentation L3 §6.1: Inherited: no.
            var cs = ComputeChild("#parent { box-decoration-break: clone; }");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"),
                "box-decoration-break is non-inherited; child must see initial `slice`");
        }

        [Test]
        public void Box_decoration_break_child_rule_sets_independently() {
            var cs = ComputeChild(
                "#parent { box-decoration-break: clone; } " +
                "#child  { box-decoration-break: clone; }");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("clone"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Box_decoration_break_initial_keyword_restores_slice() {
            var cs = Compute("#x { box-decoration-break: initial; }");
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"));
        }

        [Test]
        public void Box_decoration_break_inherit_keyword_propagates_parent() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { box-decoration-break: clone; } " +
                       "#child  { box-decoration-break: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("clone"));
        }

        [Test]
        public void Box_decoration_break_unset_on_non_inherited_resolves_as_initial() {
            // `unset` on a non-inherited property acts like `initial`.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { box-decoration-break: clone; } " +
                       "#child  { box-decoration-break: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Box_decoration_break_id_beats_class_selector() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { box-decoration-break: clone; } " +
                       "#x  { box-decoration-break: slice; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("slice"));
        }

        [Test]
        public void Box_decoration_break_later_rule_beats_earlier_equal_specificity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { box-decoration-break: slice; } " +
                       "#x { box-decoration-break: clone; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("box-decoration-break"), Is.EqualTo("clone"));
        }

        // ── Multiple elements independently scoped ────────────────────────

        [Test]
        public void Box_decoration_break_sibling_elements_are_independently_scoped() {
            var doc = Html(
                "<div id=\"a\"></div>" +
                "<div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#a { box-decoration-break: clone; } " +
                       "#b { box-decoration-break: slice; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("box-decoration-break"),
                Is.EqualTo("clone"));
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("box-decoration-break"),
                Is.EqualTo("slice"));
        }
    }
}
