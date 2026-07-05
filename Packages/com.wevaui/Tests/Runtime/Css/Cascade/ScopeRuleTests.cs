using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class ScopeRuleTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [Test]
        public void Scope_start_only_applies_inside() {
            var doc = Html("<main><div class='card'><p id='in'>x</p></div><p id='out'>y</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { p { color: red; } }")
            });
            var inside = doc.GetElementById("in");
            var outside = doc.GetElementById("out");
            Assert.That(engine.Compute(inside).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(outside).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_does_not_match_outside() {
            var doc = Html("<main><p id='out'>y</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { p { color: red; } }")
            });
            var outside = doc.GetElementById("out");
            Assert.That(engine.Compute(outside).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_start_root_element_is_in_scope() {
            // The scope-root element itself is considered in-scope, so a rule
            // whose selector matches the root will apply.
            var doc = Html("<main><div id='root' class='card'>x</div></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { div { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("root")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Scope_end_excludes_at_and_below_the_end_match() {
            var doc = Html(
              "<main><div class='card'>" +
              "<p id='a'>a</p>" +
              "<div class='footer'><p id='b'>b</p></div>" +
              "</div></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.footer) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("color"), Is.EqualTo("red"));
            // <p id='b'> sits inside .footer (the end), so it's outside scope.
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Implicit_scope_with_no_selector_applies_to_everything() {
            var doc = Html("<main><p id='x'>x</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Nested_scopes_inner_wins_via_cascade_order() {
            var doc = Html("<main><div class='outer'><div class='inner'><p id='p'>x</p></div></div></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.outer) { p { color: red; } } @scope (.inner) { p { color: blue; } }")
            });
            // Both scopes contain the <p>; later author rule wins by source order.
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Sibling_outside_scope_unaffected() {
            var doc = Html("<main><div class='card'><p id='in'>x</p></div><p id='out'>y</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { p { color: red; font-weight: bold; } }")
            });
            var outside = doc.GetElementById("out");
            Assert.That(engine.Compute(outside).Get("color"), Is.Not.EqualTo("red"));
            Assert.That(engine.Compute(outside).Get("font-weight"), Is.Not.EqualTo("bold"));
        }

        [Test]
        public void Scope_with_no_matching_start_does_not_apply_anywhere() {
            var doc = Html("<main><p id='x'>x</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.no-such-class) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Multiple_scope_starts_either_can_anchor() {
            var doc = Html(
              "<main><div class='a'><p id='ai'>x</p></div>" +
              "<div class='b'><p id='bi'>y</p></div>" +
              "<p id='out'>z</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.a, .b) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("ai")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("bi")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("out")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_pseudo_matches_scope_root_in_scoped_rule() {
            var doc = Html("<main><section class='card' id='card'><p id='child'>x</p></section><section id='other'></section></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { :scope { color: red; } :scope > p { font-weight: bold; } }")
            });

            Assert.That(engine.Compute(doc.GetElementById("card")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("child")).Get("font-weight"), Is.EqualTo("bold"));
            Assert.That(engine.Compute(doc.GetElementById("other")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_pseudo_works_with_compute_all_managed_fallback() {
            var doc = Html("<main><section class='card' id='card'><p id='child'>x</p></section></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { :scope > p { color: red; } }")
            });

            var all = engine.ComputeAll(doc);

            Assert.That(all[doc.GetElementById("child")].Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Scope_end_selector_is_scoped_to_start_root() {
            var doc = Html(
                "<main>" +
                "<div class='card' id='card1'>" +
                "<div class='item' id='in'>x</div>" +
                "</div>" +
                "<div class='card-footer' id='outside'>y</div>" +
                "</main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (:scope > .card-footer) { .item { color: red; } }")
            });
            // The outside .card-footer is not a descendant of card1 — it must
            // not terminate card1's scope, so .item inside card1 still gets red.
            Assert.That(engine.Compute(doc.GetElementById("in")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Scope_end_with_scope_pseudo_only_terminates_within_start() {
            var doc = Html(
                "<main>" +
                "<div class='card' id='card1'>" +
                "<div class='card-footer'><p id='deep'>deep</p></div>" +
                "<p id='above-footer'>x</p>" +
                "</div>" +
                "</main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (:scope > .card-footer) { p { color: red; } }")
            });
            // p#above-footer sits before reaching the .card-footer end -> in scope.
            Assert.That(engine.Compute(doc.GetElementById("above-footer")).Get("color"), Is.EqualTo("red"));
            // p#deep sits inside .card-footer (the end) -> out of scope.
            Assert.That(engine.Compute(doc.GetElementById("deep")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_end_without_start_pseudo_still_works_regression() {
            // Regression guard: the prior (non-:scope) end selector form must
            // continue to behave correctly after the scopeRoot threading fix.
            var doc = Html(
                "<main><div class='card'>" +
                "<p id='a'>a</p>" +
                "<div class='footer'><p id='b'>b</p></div>" +
                "</div></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.footer) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("a")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("b")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_start_only_still_works_after_end_fix_regression() {
            // Regression guard: @scope with no end-selector must remain unaffected.
            var doc = Html("<main><div class='card'><p id='in'>x</p></div><p id='out'>y</p></main>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("in")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("out")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Scope_rule_parses_into_ScopeRule_in_stylesheet() {
            var sheet = Css("@scope (.card) { p { color: red; } }");
            Assert.That(sheet.Rules.Count, Is.EqualTo(1));
            Assert.That(sheet.Rules[0], Is.TypeOf<ScopeRule>());
            var sc = (ScopeRule)sheet.Rules[0];
            Assert.That(sc.ScopeStartSelectors, Has.Count.EqualTo(1));
            Assert.That(sc.ScopeStartSelectors[0], Is.EqualTo(".card"));
        }
    }
}
