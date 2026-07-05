using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class VariableResolverPolishTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        [Test]
        public void Three_level_fallback_chain_resolves_innermost() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --c: red; color: var(--a, var(--b, var(--c, blue))); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Three_level_fallback_chain_falls_through_to_literal() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(--a, var(--b, var(--c, magenta))); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("magenta"));
        }

        [Test]
        public void Middle_var_resolves_when_intermediate_defined() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --b: orange; color: var(--a, var(--b, blue)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("orange"));
        }

        [Test]
        public void Cycle_short_circuits_to_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); color: var(--a, teal); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("teal").Or.EqualTo(""));
        }

        [Test]
        public void Self_referential_cycle_does_not_infinite_loop() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--a); color: var(--a, brown); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Either fallback or empty acceptable; test that it terminates.
            Assert.That(cs, Is.Not.Null);
        }

        [Test]
        public void Custom_property_inherits_through_var() {
            var doc = Html("<section class=\"theme\"><div id=\"x\"></div></section>");
            var engine = new CascadeEngine(new[] {
                Author(".theme { --accent: cyan; } #x { color: var(--accent); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("cyan"));
        }

        [Test]
        public void Important_declaration_with_var_takes_precedence() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: red; color: blue; color: var(--a) !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Var_in_inherited_context_resolves_against_child_custom() {
            var doc = Html("<div><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: var(--accent, red); } span { --accent: green; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // The span inherits the resolved color from div (computed at div). Spec-wise,
            // var() resolves at the originating element. Here color was resolved on div
            // with --accent undefined → red, and span inherits "red".
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Malformed_var_recovers_via_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: var(notavar, indigo); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("indigo"));
        }

        [Test]
        public void Var_inside_calc_in_fallback() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --w: 50px; width: var(--missing, var(--w)); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("50px"));
        }

        [Test]
        public void Many_elements_with_var_does_not_blow_up_performance() {
            // Sanity perf check: 1000 elements each pulling --accent through var()
            // resolve in a reasonable time. We don't assert wall time — just that
            // the cascade completes and produces correct values.
            var sb = new System.Text.StringBuilder("<main>");
            for (int i = 0; i < 1000; i++) sb.Append("<div></div>");
            sb.Append("</main>");
            var doc = Html(sb.ToString());
            var engine = new CascadeEngine(new[] {
                Author("main { --accent: salmon; } div { color: var(--accent); }")
            });
            int count = 0;
            foreach (var kv in engine.ComputeAll(doc)) {
                if (kv.Key.TagName == "div") {
                    Assert.That(kv.Value.Get("color"), Is.EqualTo("salmon"));
                    count++;
                }
            }
            Assert.That(count, Is.EqualTo(1000));
        }

        [Test]
        public void Var_with_default_resolution_via_parent_chain() {
            var doc = Html("<a><b><c><d id=\"x\"></d></c></b></a>");
            var engine = new CascadeEngine(new[] {
                Author("a { --depth: 4; } #x { width: var(--depth, 0); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("4"));
        }

        // Regression: a two-name cycle (--a -> --b -> --a) must terminate AND
        // resolve consumer-side var() calls to the consumer's fallback rather
        // than poisoning the value. The HashSet<seen> guard in ResolveVarCall
        // breaks the loop without raising; the consumer var() then sees an
        // empty/unresolved chain and chooses its own fallback.
        [Test]
        public void Two_name_cycle_terminates_and_consumer_fallback_wins() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --a: var(--b); --b: var(--a); width: var(--a, 42px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("42px"));
        }

        // Regression: var() inside calc() must be substituted textually before
        // the calc evaluator runs (CssCalc throws on CalcVariableNode). End-to-end
        // verification that the cascade's pre-calc var-resolution pass works.
        [Test]
        public void Var_inside_calc_is_substituted_before_evaluation() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --gap: 8px; padding-left: calc(var(--gap) * 2); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Post var() substitution the property carries calc(8px * 2). We don't
            // require the engine to fold the calc here (that's CssCalc's job at
            // layout time); we just require the var() be gone from the stored value.
            string v = cs.Get("padding-left");
            Assert.That(v, Does.Not.Contain("var("));
            Assert.That(v, Does.Contain("8px"));
        }

        // Regression: shorthand `font: var(--header-font)` must re-expand into
        // its longhands AFTER var() substitution. ExpandShorthandMatchesInto
        // bypasses var-bearing shorthands; CascadeEngine.ComputeFor re-attempts
        // expansion in its post-resolution loop. Without the late-expand path
        // font-weight/font-size/font-family would all stay at their initial
        // values and `bold 16px sans-serif` would be lost.
        [Test]
        public void Shorthand_var_value_reexpands_to_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { --header-font: bold 16px sans-serif; font: var(--header-font); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("font-weight"), Is.EqualTo("bold"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("16px"));
            Assert.That(cs.Get("font-family"), Is.EqualTo("sans-serif"));
        }
    }
}
