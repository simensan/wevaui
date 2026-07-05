using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Cascade L5 §7 — the five CSS-wide keywords (`initial`, `inherit`,
    // `unset`, `revert`, `revert-layer`). Existing CascadeEngineTests cover
    // the spec-correct cases for `initial` / `inherit` / `unset` only
    // implicitly via other rules. This file pins each keyword's resolution
    // explicitly and documents the current `revert` / `revert-layer`
    // partial behaviour so a future spec-correct implementation has a clear
    // regression suite.
    //
    // Per CSS_FEATURE_AUDIT.md §"Core Cascade":
    //   `revert` and `revert-layer` both fall back to `initial` (the spec
    //   rolls back to UA/user origin or lower layer respectively).
    //
    // Tests marked CURRENT-BEHAVIOUR pin v1's `revert == initial` semantics.
    // Tests marked SPEC pin behaviour Chrome / Firefox actually implement.
    // The latter currently align with v1 because revert→initial is the same
    // result when the only origin in play is author; they'll start diverging
    // when this engine grows UA / user-origin support, at which point the
    // SPEC tests will need updating to assert the rolled-back UA value
    // rather than initial.
    public class CssWideKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(Css(s));
        static OriginatedStylesheet User(string s) => OriginatedStylesheet.User(Css(s));

        // ── initial ────────────────────────────────────────────────────────

        [Test]
        public void Initial_resolves_to_propertys_initial_value() {
            // CSS Cascade L5 §7.1: `initial` rolls the property back to its
            // spec-defined initial value, regardless of any other rules.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; color: initial; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "color's initial value per CSS Color L3 §3.2 is black");
        }

        [Test]
        public void Initial_on_inherited_property_does_not_inherit() {
            // CSS Cascade L5 §7.1: `initial` always uses the property's
            // initial, even on inherited properties. The parent's `color: red`
            // must NOT leak through.
            var doc = Html("<div><p id=\"x\"></p></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: initial; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "`color: initial` overrides parent inheritance with the property's initial value");
        }

        // ── inherit ────────────────────────────────────────────────────────

        [Test]
        public void Inherit_pulls_parent_value_for_inherited_property() {
            // CSS Cascade L5 §7.2: `inherit` always uses the parent's
            // computed value, even on inherited properties (a no-op for the
            // common case but explicit syntax for code review).
            var doc = Html("<div><p id=\"x\"></p></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inherit_pulls_parent_value_for_non_inherited_property() {
            // `inherit` forces inheritance even on non-inherited properties
            // (`border-top-width` is not normally inherited). Using the
            // longhand to avoid shorthand-expansion ambiguity in `inherit`
            // round-trip through a shorthand storage slot.
            var doc = Html("<div><p id=\"x\"></p></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { border-top-width: 5px; } #x { border-top-width: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("border-top-width"), Is.EqualTo("5px"),
                "`inherit` forces parent value on a non-inherited property");
        }

        [Test]
        public void Inherit_at_root_falls_back_to_initial() {
            // CSS Cascade L5 §7.2: the root element has no parent, so
            // `inherit` falls back to the property's initial value.
            var doc = Html("<html id=\"x\"></html>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "`color: inherit` at root resolves to initial (black)");
        }

        // ── unset ──────────────────────────────────────────────────────────

        [Test]
        public void Unset_behaves_as_inherit_on_inherited_property() {
            // CSS Cascade L5 §7.3: `unset` acts as `inherit` if the property
            // is inherited (color is), else as `initial`.
            var doc = Html("<div><p id=\"x\"></p></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: red; } #x { color: green; color: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"),
                "`color: unset` on an inherited property inherits the parent value");
        }

        [Test]
        public void Unset_behaves_as_initial_on_non_inherited_property() {
            // CSS Cascade L5 §7.3: `border-width` is not inherited, so
            // `unset` resolves to its initial value (medium / 3px).
            var doc = Html("<div><p id=\"x\"></p></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { border-width: 5px; } #x { border-width: 9px; border-width: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            string bw = cs.Get("border-width");
            // CSS Backgrounds & Borders L3 §4.3 — initial value is `medium`,
            // which resolves to 3px in this engine's defaults.
            Assert.That(bw, Is.EqualTo("medium").Or.EqualTo("3px"),
                $"`border-width: unset` on non-inherited property must yield the initial value (medium / 3px), got '{bw}'");
        }

        // ── revert (v1 partial — currently == initial per audit) ────────────

        [Test]
        public void Revert_currently_treated_as_initial_when_no_origin_below_author() {
            // CURRENT-BEHAVIOUR + SPEC (they coincide here): with no UA or
            // user-origin declaration for `color`, both spec'd `revert` AND
            // the v1 `revert==initial` shortcut land on the same value —
            // the property's initial value (`black`). This test pins the
            // observed result and will stay green when origin-tracked
            // revert eventually lands.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; color: revert; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "with no UA/user-origin rule in play, revert and initial both resolve to black");
        }

        [Test]
        public void Revert_rolls_back_to_UA_origin_value() {
            // CSS Cascade L5 §7.4 — `revert` discards the current origin's
            // contribution and re-resolves from origins of lower precedence.
            // Here the Author rule sets color:red then color:revert; revert
            // drops both Author declarations for color and the cascade re-
            // resolves from the UA-origin rule → green.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: green; }"),
                Author("#x { color: red; color: revert; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("green"),
                "revert must roll back to the UA-origin value (green), not collapse to initial (black)");
        }

        [Test]
        public void Revert_with_no_user_or_UA_origin_collapses_to_initial() {
            // CSS Cascade L5 §7.4 — when no lower-precedence origin rule
            // applies, `revert` resolves to the initial value.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; color: revert; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "no UA/user origin → revert collapses to initial (black)");
        }

        [Test]
        public void Revert_rolls_back_through_User_origin_when_present() {
            // CSS Cascade L5 §7.4 — Author > User > UA. Author revert lands
            // on User (not UA) when User has a matching rule.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: green; }"),
                User("#x { color: blue; }"),
                Author("#x { color: red; color: revert; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"),
                "Author `revert` drops the Author origin; User-origin blue wins over UA-origin green");
        }

        // ── revert-layer (CSS Cascade L5 §7.5) ──────────────────────────────

        [Test]
        public void Revert_layer_rolls_back_to_lower_layer_value() {
            // CSS Cascade L5 §7.5 — revert-layer rolls back to the value
            // contributed by the next-lower layer at the same origin. Here
            // base.color=green and top.color=red; revert-layer in `top`
            // resolves to base's `green`.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@layer base { #x { color: green; } } " +
                       "@layer top { #x { color: red; color: revert-layer; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("green"),
                "revert-layer must roll back to the next-lower layer's value (green)");
        }

        [Test]
        public void Revert_layer_with_no_lower_layer_falls_through_to_revert() {
            // §7.5 — when no lower layer at the same origin matches,
            // revert-layer falls through to `revert` semantics (drop the
            // origin entirely). No UA/user rule → initial.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; color: revert-layer; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "revert-layer with no lower layer falls through to revert → initial (black)");
        }

        [Test]
        public void Revert_layer_with_no_lower_layer_but_UA_rule_falls_through_to_UA() {
            // §7.5 → §7.4 chain: revert-layer with no lower-layer match
            // falls through to revert, which rolls back to UA.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: green; }"),
                Author("#x { color: red; color: revert-layer; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("green"),
                "revert-layer falls through to revert; revert lands on UA (green)");
        }
    }
}
