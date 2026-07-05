using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Custom Properties L1 §3 — "Invalid Variables".
    //
    // "Using a variable in a property value is similar to using `attr()` or
    //  `inherit` — if the variable references end up being invalid, the
    //  property containing them is invalid at computed-value time."
    //
    // Invalid-at-computed-value-time means the property reverts to its
    // INHERITED value (for inherited properties) or its INITIAL value (for
    // non-inherited properties). It does NOT silently pass through as an
    // empty string — that was the A10 bug.
    //
    // Per-task acceptance: ≥4 tests covering the matrix
    //   (inherited / non-inherited) × (no fallback / with fallback)
    // are required, plus the parent-inheritance case for inherited props.
    public class VariableResolverInvalidAtComputedValueTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // (1) Inherited property, var() with no usable fallback → property
        //     reverts to its INITIAL value when the element has no parent
        //     style to inherit from (i.e. the root). `color`'s initial value
        //     is "black" per CssProperties registration. Pre-A10 this
        //     resolved to "" (empty string).
        [Test]
        public void Color_with_undefined_var_no_fallback_resolves_to_initial_at_root() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--undef); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // `color` is inherited; at the root the parent is null so
            // FillInherited falls back to the property's initial value.
            // Must NOT be empty — that was the silent-substitution bug.
            Assert.That(cs.Get("color"), Is.Not.Empty);
            Assert.That(cs.Get("color"), Is.EqualTo(CssProperties.InitialValueOf("color")));
        }

        // (2) `var(--undef, red)` must STILL honour the fallback — the
        //     declaration is only invalid-at-computed-value-time when BOTH
        //     the var is unresolved AND no fallback exists (or the fallback
        //     itself fails). Per CSS Custom Properties L1 §3.
        [Test]
        public void Color_with_undefined_var_and_fallback_uses_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--undef, red); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        // (3) `color` is inherited. When the child's `color: var(--undef)`
        //     becomes invalid-at-computed-value-time, the child must INHERIT
        //     the parent's color (per the spec: invalid → inherit for
        //     inherited properties, not the property's initial value).
        [Test]
        public void Color_with_undefined_var_on_child_inherits_parent_color() {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { color: blue; } #x { color: var(--undef); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        // (4) `background-color` is non-inherited; its initial value is
        //     `transparent`. An invalid var() on it must produce the initial
        //     value (`transparent`) rather than the empty string. (For
        //     `background-color` specifically the visible paint result is
        //     the same as the pre-fix behaviour, but for other non-inherited
        //     properties the difference matters — see test (5) below.)
        [Test]
        public void BackgroundColor_with_undefined_var_no_fallback_resolves_to_transparent() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { background-color: var(--undef); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("transparent"));
            Assert.That(cs.Get("background-color"), Is.EqualTo(CssProperties.InitialValueOf("background-color")));
        }

        // (5) Non-inherited property whose initial value is NOT the empty
        //     string and NOT visually equivalent to "". `display`'s initial
        //     is "inline"; an invalid var() must reset to "inline", not to
        //     "" — that was load-bearing for the regression. The child has
        //     no `display: block` ancestor influence (display isn't
        //     inherited).
        [Test]
        public void Display_with_undefined_var_no_fallback_resolves_to_initial_inline() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { display: var(--undef); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("inline"));
            Assert.That(cs.Get("display"), Is.EqualTo(CssProperties.InitialValueOf("display")));
        }

        // (6) Spec note in §3: a var() whose fallback ALSO contains an
        //     unresolvable var() makes the whole declaration invalid. The
        //     fallback chain doesn't paper over the failure.
        [Test]
        public void Color_with_chained_undefined_vars_in_fallback_becomes_invalid() {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { color: green; } #x { color: var(--a, var(--b)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Both --a and --b are undefined and --b has no fallback, so the
            // entire declaration is invalid-at-computed-value-time. `color`
            // is inherited; child must take the parent's green.
            Assert.That(cs.Get("color"), Is.EqualTo("green"));
        }
    }
}
