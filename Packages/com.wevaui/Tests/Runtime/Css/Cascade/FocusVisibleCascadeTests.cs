using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Selectors L4 §9.4.2 — :focus-visible / :focus cascade interaction.
    //
    // `:focus` matches any focused element (keyboard or pointer).
    // `:focus-visible` matches only when the UA determines the focus indicator
    // should be visible — in this engine, that is when the element received
    // focus via a keyboard event (EventDispatcher sets ElementState.FocusVisible).
    //
    // These tests exercise the CASCADE boundary: that the correct set of rules
    // is applied when only :focus vs. only :focus-visible is matched, and that
    // specificity and source-order interact correctly with both pseudos.
    //
    // Ref: CSS Selectors L4 §9.4.2, Selectors L4 §17 (specificity).
    public class FocusVisibleCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // Minimal state provider that allows setting arbitrary ElementState bits.
        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            public void Set(Element e, ElementState s) { map[e] = s; }
            public ElementState GetState(Element e) =>
                map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        // ── :focus — always applied when element is focused ──────────────────

        [Test]
        public void Focus_pseudo_rule_applies_when_element_has_focus_state() {
            // :focus matches any focused element; the Focus bit must cause the
            // rule to land in the computed style.
            var doc = Html("<button id=\"x\">Click</button>");
            var engine = new CascadeEngine(new[] {
                Author(":focus { outline-color: blue; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.Focus);
            var cs = engine.Compute(el, state);
            Assert.That(cs.Get("outline-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Focus_pseudo_rule_does_not_apply_without_focus_state() {
            // Without the Focus bit the :focus rule must not apply.
            var doc = Html("<button id=\"x\">Click</button>");
            var engine = new CascadeEngine(new[] {
                Author(":focus { outline-color: blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("outline-color"), Is.Not.EqualTo("blue"));
        }

        // ── :focus-visible — only applies for keyboard focus ─────────────────

        [Test]
        public void Focus_visible_rule_applies_when_element_has_focus_visible_state() {
            // ElementState.FocusVisible is set by EventDispatcher when the
            // element received focus via a keyboard event.
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author(":focus-visible { outline-color: red; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.FocusVisible | ElementState.Focus);
            var cs = engine.Compute(el, state);
            Assert.That(cs.Get("outline-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Focus_visible_rule_does_not_apply_with_pointer_focus_only() {
            // With Focus bit but without FocusVisible, :focus-visible must NOT
            // match — pointer-focused elements don't show the keyboard indicator.
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author(":focus-visible { outline-color: red; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.Focus); // no FocusVisible
            var cs = engine.Compute(el, state);
            Assert.That(cs.Get("outline-color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Focus_visible_rule_does_not_apply_without_any_focus() {
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author(":focus-visible { outline-color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("outline-color"), Is.Not.EqualTo("red"));
        }

        // ── Both pseudos in same stylesheet — specificity and source-order ───

        [Test]
        public void Focus_visible_rule_overrides_focus_rule_when_keyboard_focused() {
            // Both :focus and :focus-visible have the same specificity (0,1,0).
            // Source order wins: later rule overrides earlier.
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author(":focus { outline-color: blue; } :focus-visible { outline-color: red; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.FocusVisible | ElementState.Focus);
            var cs = engine.Compute(el, state);
            // Both selectors match; :focus-visible is later so red wins.
            Assert.That(cs.Get("outline-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Only_focus_rule_applies_for_pointer_focus_not_focus_visible() {
            // When pointer-focused: :focus matches, :focus-visible does not.
            var doc = Html("<input id=\"x\">");
            var engine = new CascadeEngine(new[] {
                Author(":focus { outline-color: blue; } :focus-visible { outline-color: red; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.Focus); // no FocusVisible
            var cs = engine.Compute(el, state);
            // Only :focus matched; blue is the result.
            Assert.That(cs.Get("outline-color"), Is.EqualTo("blue"));
        }

        // ── Focus on non-input elements ──────────────────────────────────────

        [Test]
        public void Focus_visible_matches_div_with_tabindex_when_keyboard_focused() {
            // Any focusable element (including divs with tabindex) can carry
            // the FocusVisible bit; the cascade must honor it.
            var doc = Html("<div id=\"x\" tabindex=\"0\">Tab target</div>");
            var engine = new CascadeEngine(new[] {
                Author("#x:focus-visible { background-color: yellow; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.FocusVisible | ElementState.Focus);
            var cs = engine.Compute(el, state);
            Assert.That(cs.Get("background-color"), Is.EqualTo("yellow"));
        }

        // ── Inheritance boundary: :focus-visible does not inherit ─────────────

        [Test]
        public void Focus_visible_rule_does_not_match_child_element_directly() {
            // :focus-visible matches the focused element itself, not its children.
            // A child without the FocusVisible state bit must not pick up the rule
            // through selector matching. Use a non-inherited property to distinguish
            // cascade-application from inheritance.
            var doc = Html("<button id=\"btn\"><span id=\"inner\">text</span></button>");
            var engine = new CascadeEngine(new[] {
                Author(":focus-visible { outline-color: red; }")
            });
            var btn = doc.GetElementById("btn");
            var inner = doc.GetElementById("inner");
            var state = new FakeState();
            state.Set(btn, ElementState.FocusVisible | ElementState.Focus);
            // Inner span does NOT have the state bit; the :focus-visible rule
            // must not match the child. outline-color is non-inherited so if it's
            // red the rule matched the child directly.
            var csInner = engine.Compute(inner, state);
            Assert.That(csInner.Get("outline-color"), Is.Not.EqualTo("red"),
                ":focus-visible must not match a child element that lacks the FocusVisible state bit");
        }

        // ── Specificity of compound :focus-visible selectors ─────────────────

        [Test]
        public void Compound_focus_visible_selector_beats_simple_focus_rule() {
            // button:focus-visible has specificity (0,1,1) vs :focus (0,1,0).
            // The compound selector must win even when declared first.
            var doc = Html("<button id=\"x\">OK</button>");
            var engine = new CascadeEngine(new[] {
                Author("button:focus-visible { outline-color: green; } :focus { outline-color: gray; }")
            });
            var el = doc.GetElementById("x");
            var state = new FakeState();
            state.Set(el, ElementState.FocusVisible | ElementState.Focus);
            var cs = engine.Compute(el, state);
            // button:focus-visible has higher specificity; green wins.
            Assert.That(cs.Get("outline-color"), Is.EqualTo("green"));
        }
    }
}
