using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;
using Weva.ViewTransitions;

namespace Weva.Tests.Css.Cascade {
    // CSS View Transitions L1 §4 — view-transition-name cascade.
    //
    // `view-transition-name` is a NON-INHERITED property with initial value `none`.
    // Authors assign a <custom-ident> (or the keyword `none`) to flag an element
    // for participation in a view transition. The engine's `SnapshotCapture`
    // then walks the box tree and collects boxes whose `view-transition-name`
    // differs from `none`.
    //
    // The property is registered lazily via `ViewTransitionProperties.EnsureRegistered()`
    // to avoid forcing the ViewTransitions module into apps that don't use it.
    //
    // This file tests ONLY the cascade / property-registration surface — it does
    // NOT exercise the snapshot / animation pipeline. That pipeline is tested in
    // `Tests/Runtime/ViewTransitions/ViewTransitionTests.cs`.
    //
    // Spec references:
    //   CSS View Transitions L1 §4 (property definition)
    //   CSS Cascade L5 §4.1 (non-inherited initial-value semantics)
    public class ViewTransitionNameCascadeTests {
        [SetUp]
        public void SetUp() {
            ViewTransitionProperties.EnsureRegistered();
        }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(Document doc, string id, params OriginatedStylesheet[] sheets) {
            var engine = new CascadeEngine(sheets);
            return engine.Compute(doc.GetElementById(id));
        }

        // ── Registration / initial value ─────────────────────────────────────

        [Test]
        public void View_transition_name_is_registered() {
            Assert.That(CssProperties.TryGet("view-transition-name", out _), Is.True,
                "view-transition-name must be registered after EnsureRegistered()");
        }

        [Test]
        public void View_transition_name_initial_is_none() {
            // CSS VT1 §4: initial value is `none` (element does not participate).
            var doc = Html("<div id=\"x\"></div>");
            var cs = Compute(doc, "x", Author(""));
            Assert.That(cs.Get("view-transition-name"), Is.EqualTo("none"),
                "initial value of view-transition-name must be 'none'");
        }

        [Test]
        public void View_transition_name_is_not_inherited() {
            // CSS VT1 §4: the property is non-inherited. A parent declaration
            // must NOT propagate to a child without its own declaration.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { view-transition-name: hero; }")
            });
            var parent = engine.Compute(doc.GetElementById("p"));
            var child  = engine.Compute(doc.GetElementById("c"));
            Assert.That(parent.Get("view-transition-name"), Is.EqualTo("hero"),
                "parent element must see the declared name");
            Assert.That(child.Get("view-transition-name"), Is.EqualTo("none"),
                "child without own declaration must see initial 'none', not inherit from parent");
        }

        // ── Custom-ident round-trip ──────────────────────────────────────────

        [Test]
        public void View_transition_name_ident_round_trips() {
            var doc = Html("<div id=\"x\"></div>");
            var cs = Compute(doc, "x", Author("#x { view-transition-name: hero; }"));
            Assert.That(cs.Get("view-transition-name"), Is.EqualTo("hero"));
        }

        [Test]
        public void View_transition_name_kebab_ident_round_trips() {
            var doc = Html("<div id=\"x\"></div>");
            var cs = Compute(doc, "x", Author("#x { view-transition-name: main-header; }"));
            Assert.That(cs.Get("view-transition-name"), Is.EqualTo("main-header"),
                "hyphenated ident must survive parse → cascade → Get");
        }

        [Test]
        public void View_transition_name_none_explicit_round_trips() {
            // Explicit `none` must not be treated as an error — it's the opt-out value.
            var doc = Html("<div id=\"x\"></div>");
            var cs = Compute(doc, "x", Author("#x { view-transition-name: none; }"));
            Assert.That(cs.Get("view-transition-name"), Is.EqualTo("none"));
        }

        // ── Multiple elements with the same name ─────────────────────────────

        [Test]
        public void Multiple_elements_can_carry_same_name_at_cascade_level() {
            // The spec says duplicate view-transition-names cause undefined behaviour
            // at transition time, but the CSS cascade has no restriction on it — two
            // elements may legally receive the same cascade value. The snapshot layer
            // (SnapshotCapture) is responsible for handling duplicates at capture time.
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#a, #b { view-transition-name: card; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("view-transition-name"), Is.EqualTo("card"));
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("view-transition-name"), Is.EqualTo("card"),
                "cascade layer does not prevent two elements from sharing the same view-transition-name");
        }

        // ── Cascade specificity / order ──────────────────────────────────────

        [Test]
        public void Higher_specificity_rule_wins_for_view_transition_name() {
            // #id (1,0,0) beats .cls (0,1,0).
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { view-transition-name: from-class; } #x { view-transition-name: from-id; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("view-transition-name"), Is.EqualTo("from-id"),
                "ID selector must win specificity over class selector");
        }

        [Test]
        public void Later_source_order_wins_at_equal_specificity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { view-transition-name: first; } div { view-transition-name: second; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("view-transition-name"), Is.EqualTo("second"),
                "later source-order rule must win at equal specificity");
        }

        [Test]
        public void Important_view_transition_name_beats_non_important() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { view-transition-name: winner !important; } #x { view-transition-name: loser; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("view-transition-name"), Is.EqualTo("winner"));
        }

        // ── CSS-wide keywords ────────────────────────────────────────────────

        [Test]
        public void View_transition_name_inherit_resolves_to_parent_value() {
            // Even though the property is non-inherited, an explicit `inherit`
            // keyword must force inheritance for that element.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { view-transition-name: hero; } #c { view-transition-name: inherit; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("c")).Get("view-transition-name"), Is.EqualTo("hero"),
                "explicit 'inherit' on non-inherited property must pull parent's value");
        }

        [Test]
        public void View_transition_name_initial_keyword_resets_to_none() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { view-transition-name: hero; } #x { view-transition-name: initial; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("view-transition-name"), Is.EqualTo("none"),
                "'initial' must reset view-transition-name to its initial value 'none'");
        }
    }
}
