using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Container;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Css.Container {
    // CSS Containment L3 §3 — edge cases for @container query evaluation:
    //
    //   - Range form `(width >= 400px)`: NOT yet supported (parse error silently
    //     dropped per EC11 convention). Documented as CON-1 in CSS_OPEN_GAPS.md.
    //   - `style()` container queries: NOT yet supported (silently dropped).
    //     Documented as CON-2 in CSS_OPEN_GAPS.md.
    //   - `@container` inside `@media`: rules must apply only when both conditions
    //     are satisfied.
    //   - Multiple named `@container` rules on the same element.
    //   - `@container` combined with `@layer`.
    //   - `@container` combined with `@scope`.
    public class ContainerQueryEdgeCaseTests {
        sealed class TestBox : BlockBox { }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        sealed class FakeBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
            public void Add(Element e, Box b) { map[e] = b; }
            public System.Func<Element, Box> AsFunc => Lookup;
        }

        static FakeBoxIndex BuildParentChild(Document doc, string parentId, double pw, string childId) {
            var idx = new FakeBoxIndex();
            var parent = doc.GetElementById(parentId);
            var child = doc.GetElementById(childId);
            var ps = new ComputedStyle(parent);
            ps.Set("container-type", "inline-size");
            var pb = new TestBox { Element = parent, Style = ps, Width = pw, Height = pw * 0.75 };
            var cs2 = new ComputedStyle(child);
            var cb = new TestBox { Element = child, Style = cs2 };
            pb.AddChild(cb);
            idx.Add(parent, pb);
            idx.Add(child, cb);
            return idx;
        }

        // ---- CON-1: range form silently dropped (not a crash) ----

        [Test]
        public void Container_range_form_gte_applies_when_container_meets_width() {
            // CSS Containment L3 §3.3: the range form `(width >= 400px)` is equivalent
            // to `(min-width: 400px)`. The engine does not yet support the range syntax
            // (the parser throws on the `>=` token); this test pins the SPEC-CORRECT behaviour
            // so it turns green automatically when range-form support lands.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author("@container (width >= 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("red"),
                "range form >= should match when container is 800px wide (>= 600px threshold)");
        }

        [Test]
        public void Container_range_form_parse_failure_does_not_throw() {
            // EC11 convention: an unparseable @container rule is silently dropped.
            // The cascade engine must not throw any exception.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            Assert.DoesNotThrow(() => {
                var engine = new CascadeEngine(new[] {
                    Author(
                        "#x { color: green; }" +
                        "@container (width >= 600px) { #x { color: red; } }")
                });
                // Just reading the property must not throw.
                _ = engine.Compute(x).Get("color");
            });
        }

        [Test]
        public void Container_range_form_parse_failure_discards_rule() {
            // Spec-correct: a container rule whose condition fails to parse must be
            // silently DROPPED (never applied). Currently the rule becomes always-matching
            // (see CON-3 tracker entry). This test pins the spec-correct behaviour.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "@container (width >= 600px) { #x { color: red; } }")
            });
            // Without box lookup (no container context), the rule must not apply.
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("green"),
                "failed-parse container rule must be dropped; fallback green must win");
        }

        // ---- CON-2: style() query silently dropped ----

        [Test]
        public void Container_style_query_matches_custom_property_value() {
            // CSS Containment L3 §3.4: `style(--foo: bar)` checks a custom property
            // on the nearest container ancestor. The engine does not yet evaluate style
            // queries; this test pins the spec-correct behaviour for when it lands.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var p = doc.GetElementById("p");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#p { --theme: dark; container-type: style; }" +
                    "@container style(--theme: dark) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"),
                "style() query should match when the ancestor has the expected custom property value");
        }

        [Test]
        public void Container_style_query_parse_failure_does_not_crash_cascade() {
            // style() now parses, but with no box lookup (no container context)
            // and --foo unset the query cannot match, so the fallback still wins.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            Assert.DoesNotThrow(() => {
                var engine = new CascadeEngine(new[] {
                    Author(
                        "#x { color: orange; }" +
                        "@container style(--foo: bar) { #x { color: blue; } }")
                });
                var color = engine.Compute(x).Get("color");
                Assert.That(color, Is.EqualTo("orange"),
                    "fallback rule must still apply when style() container rule does not match");
            });
        }

        [Test]
        public void Container_style_query_does_not_match_when_value_differs() {
            // #p declares --theme: light but the query asks for dark → no match.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "#p { --theme: light; }" +
                    "@container style(--theme: dark) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("green"),
                "style() must not match when the custom property value differs");
        }

        [Test]
        public void Container_style_query_existence_form_matches_when_property_set() {
            // Boolean existence form `style(--theme)` matches when the container
            // has the custom property set to any non-empty value.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "#p { --theme: anything; }" +
                    "@container style(--theme) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"),
                "existence form should match when the custom property is set");
        }

        [Test]
        public void Container_style_query_existence_form_does_not_match_when_unset() {
            // No --theme declared anywhere → existence form does not match.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "@container style(--theme) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("green"),
                "existence form must not match an unset custom property");
        }

        [Test]
        public void Container_style_query_negation_matches_when_value_differs() {
            // `not style(--theme: dark)` matches because --theme is light.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var x = doc.GetElementById("x");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "#p { --theme: light; }" +
                    "@container not style(--theme: dark) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"),
                "not style(--theme: dark) should match when --theme is not dark");
        }

        // ---- @container inside @media ----

        [Test]
        public void Container_inside_matching_media_applies() {
            // @media matches at 800px; @container matches when parent is >= 600px wide.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var media = MediaContext.Default(800, 600);
            var engine = new CascadeEngine(new[] {
                Author("@media (min-width: 600px) { @container (min-width: 600px) { #x { color: red; } } }")
            }, media);
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                "both @media and @container conditions match → rule applies");
        }

        [Test]
        public void Container_inside_non_matching_media_does_not_apply() {
            // @media requires 1200px but viewport is only 800px; container is irrelevant.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var media = MediaContext.Default(800, 600);
            var engine = new CascadeEngine(new[] {
                Author("@media (min-width: 1200px) { @container (min-width: 600px) { #x { color: red; } } }")
            }, media);
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.Not.EqualTo("red"),
                "@media is false so the inner @container rule must not apply");
        }

        [Test]
        public void Container_inside_media_overrides_outer_rule() {
            // The container-inside-media rule has higher source order and matches → wins.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var media = MediaContext.Default(900, 600);
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: blue; }" +
                    "@media (min-width: 600px) { @container (min-width: 600px) { #x { color: red; } } }")
            }, media);
            engine.ElementToBoxLookup = idx.AsFunc;
            // Same specificity; later source order (the container-in-media rule) wins.
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                "later source-order container-in-media rule wins same-specificity race");
        }

        // ---- @container combined with @layer ----

        [Test]
        public void Container_rule_inside_layer_participates_in_layer_ordering() {
            // @layer base contains a @container rule; @layer overrides (later) wins.
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @layer base, overrides;
                    @layer base { @container (min-width: 600px) { #x { color: red; } } }
                    @layer overrides { @container (min-width: 600px) { #x { color: blue; } } }
                ")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"),
                "overrides layer is later → its container rule wins");
        }

        // ---- @container combined with @scope ----

        [Test]
        public void Container_inside_scope_respects_both_conditions() {
            // @scope (.card) scopes the rule; @container further gates on size.
            // When the container is too narrow, the rule does not apply even if in scope.
            var doc = Html("<div class='card'><div id='p'><span id='x'>y</span></div></div>");
            var idx = BuildParentChild(doc, "p", 300, "x");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { @container (min-width: 600px) { #x { color: red; } } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            // Container is 300px (< 600px threshold) → rule must not apply.
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.Not.EqualTo("red"),
                "container too narrow → rule does not apply even though element is in scope");
        }

        [Test]
        public void Container_inside_scope_applies_when_both_conditions_met() {
            var doc = Html("<div class='card'><div id='p'><span id='x'>y</span></div></div>");
            var idx = BuildParentChild(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author("@scope (.card) { @container (min-width: 600px) { #x { color: red; } } }")
            });
            engine.ElementToBoxLookup = idx.AsFunc;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                "in-scope + wide container → rule applies");
        }
    }
}
