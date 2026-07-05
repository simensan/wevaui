using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Cascade L6 §7.3 — @scope `to` clause depth and interaction edges.
    //
    // The `to` clause defines a "lower boundary" for the scope: any element
    // that matches the end selector (or a descendant of it) is OUTSIDE the
    // scope. The start root itself is always in scope. Elements between the
    // start root and the first matching end descendant are in scope. Elements
    // below (inside) an end-matching descendant are out.
    //
    // These tests focus on cases not covered by ScopeRuleTests:
    //   - Multiple end selectors (comma-separated list).
    //   - End at different depths on different branches.
    //   - @scope interacting with @layer.
    //   - @scope `to` with !important declarations.
    //   - Verifying the start root's direct children are in scope even when
    //     the end selector matches at a deeper level.
    public class ScopeToDepthTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ---- comma-separated end selectors ----

        [Test]
        public void Scope_to_with_two_end_selectors_excludes_both() {
            // @scope (.card) to (.header, .footer) — excludes elements inside
            // .header OR .footer. The middle element .body is in scope.
            var doc = Html(
                "<div class='card' id='card'>" +
                "<div class='header'><p id='in-header'>a</p></div>" +
                "<div class='body'><p id='in-body'>b</p></div>" +
                "<div class='footer'><p id='in-footer'>c</p></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.header, .footer) { p { color: red; } }")
            });
            // in-header and in-footer are inside the end elements → out of scope.
            Assert.That(engine.Compute(doc.GetElementById("in-header")).Get("color"), Is.Not.EqualTo("red"),
                "p inside .header (end) must be out of scope");
            Assert.That(engine.Compute(doc.GetElementById("in-footer")).Get("color"), Is.Not.EqualTo("red"),
                "p inside .footer (end) must be out of scope");
            // in-body is between header and footer, not inside any end element → in scope.
            Assert.That(engine.Compute(doc.GetElementById("in-body")).Get("color"), Is.EqualTo("red"),
                "p inside .body must be in scope");
        }

        // ---- end at different depths in different branches ----

        [Test]
        public void Scope_to_on_one_branch_does_not_close_other_branch() {
            // .card has two children: .left (no .stop) and .right (.stop inside it).
            // @scope (.card) to (.stop) must close only the right branch.
            var doc = Html(
                "<div class='card'>" +
                "<div class='left'><p id='left-p'>a</p></div>" +
                "<div class='right'><div class='stop'><p id='stopped'>b</p></div></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.stop) { p { color: green; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("left-p")).Get("color"), Is.EqualTo("green"),
                "left branch has no .stop so p is in scope");
            Assert.That(engine.Compute(doc.GetElementById("stopped")).Get("color"), Is.Not.EqualTo("green"),
                "p inside .stop is excluded");
        }

        // ---- the end element itself is excluded, but elements BEFORE are in scope ----

        [Test]
        public void Scope_includes_element_directly_before_end_element() {
            // Verifies that a sibling immediately before .footer stays in scope.
            var doc = Html(
                "<div class='card'>" +
                "<p id='before-footer'>x</p>" +
                "<div class='footer'></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.footer) { p { color: red; } }")
            });
            Assert.That(engine.Compute(doc.GetElementById("before-footer")).Get("color"), Is.EqualTo("red"),
                "sibling p before .footer is still in scope");
        }

        [Test]
        public void Scope_end_element_itself_is_out_of_scope() {
            // The element matching the `to` selector is itself outside the scope.
            // CSS Cascade L6 §7.3: "the lower boundary is excluded".
            // Use `font-style` (inherited) with a selector limited to `.footer` so
            // only a DIRECT match (not inheritance) would set the value.
            var doc = Html(
                "<div class='card'>" +
                "<div class='footer' id='footer-el'>y</div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                // The `.footer` selector is inside scope, so if the footer element is
                // in scope it would get font-style: italic. If it's out of scope, it won't.
                Author("@scope (.card) to (.footer) { .footer { font-style: italic; } }")
            });
            // .footer itself matches the end selector → out of scope → rule must not apply.
            Assert.That(engine.Compute(doc.GetElementById("footer-el")).Get("font-style"), Is.Not.EqualTo("italic"),
                "The .footer element matching the end selector is itself out of scope");
        }

        // ---- @scope to + @layer interaction ----

        [Test]
        public void Scope_to_inside_layer_respects_layer_ordering() {
            // Two layered @scope rules for the same element with different `to` cuts.
            // Layer `overrides` comes after `base` so its scoped rule wins for elements
            // that are in-scope for BOTH layers' scopes.
            var doc = Html(
                "<div class='card'>" +
                "<p id='target'>x</p>" +
                "<div class='stop'><p id='stopped'>y</p></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @layer base, overrides;
                    @layer base {
                        @scope (.card) to (.stop) { p { color: red; } }
                    }
                    @layer overrides {
                        @scope (.card) to (.stop) { p { color: blue; } }
                    }
                ")
            });
            // target is in scope for both; overrides comes later → blue.
            Assert.That(engine.Compute(doc.GetElementById("target")).Get("color"), Is.EqualTo("blue"),
                "layer ordering must apply inside @scope rules");
            // stopped is excluded from both scopes.
            Assert.That(engine.Compute(doc.GetElementById("stopped")).Get("color"), Is.Not.EqualTo("blue"),
                "element inside .stop must remain out of scope across both layers");
        }

        // ---- @scope to with !important ----

        [Test]
        public void Scope_to_important_beats_unlayered_normal_inside_scope() {
            var doc = Html(
                "<div class='card'><p id='target'>x</p></div>");
            var engine = new CascadeEngine(new[] {
                Author(
                    "@scope (.card) { p { color: red !important; } }" +
                    "p { color: blue; }")
            });
            // !important from inside scope beats normal author rule regardless.
            Assert.That(engine.Compute(doc.GetElementById("target")).Get("color"), Is.EqualTo("red"));
        }

        // ---- nested scopes with to clause ----

        [Test]
        public void Nested_scope_inner_to_clause_works_independently_of_outer() {
            // Outer: @scope (.outer) — no end.
            // Inner: @scope (.inner) to (.stop).
            // p#in-inner is inside .inner but before .stop → matches both scopes.
            // p#in-stop is inside .stop → excluded from inner scope.
            var doc = Html(
                "<div class='outer'>" +
                "<div class='inner'>" +
                "<p id='in-inner'>a</p>" +
                "<div class='stop'><p id='in-stop'>b</p></div>" +
                "</div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @scope (.outer) {
                        @scope (.inner) to (.stop) {
                            p { color: green; }
                        }
                    }
                ")
            });
            Assert.That(engine.Compute(doc.GetElementById("in-inner")).Get("color"), Is.EqualTo("green"),
                "p before .stop in inner scope must match");
            Assert.That(engine.Compute(doc.GetElementById("in-stop")).Get("color"), Is.Not.EqualTo("green"),
                "p inside .stop must be excluded by inner scope's to clause");
        }

        // ---- @scope where start and end share the same parent ----

        [Test]
        public void Scope_to_at_sibling_depth_applies_to_items_between() {
            // .list is the scope start; .divider is the end; li#after-divider is
            // a sibling OF .divider (not a child of it) and sits after it — it is
            // NOT inside the end element, but the spec says the SUBTREE of the end
            // is excluded, not elements after it. Siblings of .divider remain in scope.
            var doc = Html(
                "<ul class='list'>" +
                "<li id='before'>a</li>" +
                "<li class='divider' id='divider-el'>---</li>" +
                "<li id='after'>b</li>" +
                "</ul>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.list) to (.divider) { li { color: red; } }")
            });
            // li#before is in scope (above the end element's subtree boundary).
            Assert.That(engine.Compute(doc.GetElementById("before")).Get("color"), Is.EqualTo("red"),
                "li before .divider must be in scope");
            // li.divider itself is the end match → out of scope.
            Assert.That(engine.Compute(doc.GetElementById("divider-el")).Get("color"), Is.Not.EqualTo("red"),
                "the .divider end element is itself out of scope");
            // li#after is a sibling of .divider, NOT inside its subtree → in scope.
            // (The `to` clause closes the SUBTREE of the end, not everything after it.)
            Assert.That(engine.Compute(doc.GetElementById("after")).Get("color"), Is.EqualTo("red"),
                "li after .divider (not inside it) must remain in scope");
        }

        // ---- @scope with no content in start still applies to start element ----

        [Test]
        public void Scope_to_start_element_itself_is_always_in_scope() {
            // Even when there's a `to` clause, the start root itself is in scope.
            var doc = Html(
                "<div class='card' id='card-root'>" +
                "<div class='footer'><span id='inside-footer'>x</span></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) to (.footer) { div { color: red; } }")
            });
            // The .card root itself must be in scope.
            Assert.That(engine.Compute(doc.GetElementById("card-root")).Get("color"), Is.EqualTo("red"),
                "scope start root must always be in scope");
        }
    }
}
