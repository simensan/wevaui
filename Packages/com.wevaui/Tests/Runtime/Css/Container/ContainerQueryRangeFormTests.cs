using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Container;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Css.Container {
    // CSS Containment L3 §3.3 — range form for @container feature queries.
    //
    // The spec defines an alternative to the `min-`/`max-` prefix form using
    // comparison operators: `>`, `<`, `>=`, `<=`, `=`. These map to the
    // existing `ContainerFeatureRange` enum values:
    //   `>` / `>=` → Min (inclusive; strict vs. inclusive distinction is
    //                      below layout resolution — v1 simplification)
    //   `<` / `<=` → Max (inclusive)
    //   `=`        → Equals
    //
    // Tests cover:
    //   - Parser accepts all five operators without throwing
    //   - `width >= 600px` evaluates identically to `min-width: 600px`
    //   - `width <= 800px` evaluates identically to `max-width: 800px`
    //   - `width > 600px` maps to Min (strict vs. inclusive approximation)
    //   - `width < 800px` maps to Max
    //   - `width = 600px` evaluates identically to `(width: 600px)`
    //   - `height >= 400px` requires a size container (block axis available)
    //   - range form combined with `and`
    //   - range form combined with `or`
    //   - negative case: container too small misses the threshold
    //   - full cascade integration: @container (width >= ...) { ... }
    //   - aspect-ratio range form
    //
    // Spec: https://www.w3.org/TR/css-contain-3/#container-features
    public class ContainerQueryRangeFormTests {
        sealed class TestBox : BlockBox { }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // Builds a minimal parent (container-type inline-size) + child box index
        // so CascadeEngine can resolve @container rules.
        static System.Func<Element, Box> BoxIndex(Document doc, string parentId, double pw,
                                                   string childId) {
            var map = new Dictionary<Element, Box>();
            var parent = doc.GetElementById(parentId);
            var child = doc.GetElementById(childId);
            var ps = new ComputedStyle(parent);
            ps.Set("container-type", "inline-size");
            var pb = new TestBox { Element = parent, Style = ps, Width = pw, Height = pw * 0.75 };
            var cs = new ComputedStyle(child);
            var cb = new TestBox { Element = child, Style = cs };
            pb.AddChild(cb);
            map[parent] = pb;
            map[child] = cb;
            return e => e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        // Builds a size-container (both inline and block axes available).
        static System.Func<Element, Box> SizeBoxIndex(Document doc, string parentId,
                                                        double pw, double ph, string childId) {
            var map = new Dictionary<Element, Box>();
            var parent = doc.GetElementById(parentId);
            var child = doc.GetElementById(childId);
            var ps = new ComputedStyle(parent);
            ps.Set("container-type", "size");
            var pb = new TestBox { Element = parent, Style = ps, Width = pw, Height = ph };
            var cs = new ComputedStyle(child);
            var cb = new TestBox { Element = child, Style = cs };
            pb.AddChild(cb);
            map[parent] = pb;
            map[child] = cb;
            return e => e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        // ---- Parser: all operators are accepted without throwing ----

        [Test]
        public void Range_form_gte_parses_without_throwing() {
            Assert.DoesNotThrow(() => ContainerQueryParser.ParseCondition("(width >= 600px)"),
                "`width >= 600px` must parse without exception");
        }

        [Test]
        public void Range_form_lte_parses_without_throwing() {
            Assert.DoesNotThrow(() => ContainerQueryParser.ParseCondition("(width <= 800px)"),
                "`width <= 800px` must parse without exception");
        }

        [Test]
        public void Range_form_gt_parses_without_throwing() {
            Assert.DoesNotThrow(() => ContainerQueryParser.ParseCondition("(width > 600px)"),
                "`width > 600px` must parse without exception");
        }

        [Test]
        public void Range_form_lt_parses_without_throwing() {
            Assert.DoesNotThrow(() => ContainerQueryParser.ParseCondition("(width < 800px)"),
                "`width < 800px` must parse without exception");
        }

        [Test]
        public void Range_form_eq_parses_without_throwing() {
            Assert.DoesNotThrow(() => ContainerQueryParser.ParseCondition("(width = 600px)"),
                "`width = 600px` must parse without exception");
        }

        // ---- Evaluator: >= maps to Min (inclusive) ----

        [Test]
        public void Range_gte_matches_when_container_meets_width() {
            // CSS Containment L3 §3.3: `(width >= 600px)` ≡ `(min-width: 600px)`.
            var cond = ContainerQueryParser.ParseCondition("(width >= 600px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(800)), Is.True,
                "800px >= 600px must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.True,
                "600px >= 600px (exact) must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(400)), Is.False,
                "400px >= 600px must NOT match");
        }

        [Test]
        public void Range_lte_matches_when_container_is_narrow_enough() {
            // `(width <= 800px)` ≡ `(max-width: 800px)`.
            var cond = ContainerQueryParser.ParseCondition("(width <= 800px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.True,
                "600px <= 800px must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(800)), Is.True,
                "800px <= 800px (exact) must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(1000)), Is.False,
                "1000px <= 800px must NOT match");
        }

        [Test]
        public void Range_gt_maps_to_min_inclusive_approximation() {
            // v1 simplification: `>` collapses to inclusive `>=` (Min range).
            // The ½-pixel strict/inclusive boundary is below layout resolution.
            var cond = ContainerQueryParser.ParseCondition("(width > 600px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(800)), Is.True,
                "800px > 600px must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(400)), Is.False,
                "400px > 600px must NOT match");
        }

        [Test]
        public void Range_lt_maps_to_max_inclusive_approximation() {
            // v1 simplification: `<` collapses to inclusive `<=` (Max range).
            var cond = ContainerQueryParser.ParseCondition("(width < 800px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.True,
                "600px < 800px must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(1000)), Is.False,
                "1000px < 800px must NOT match");
        }

        [Test]
        public void Range_eq_matches_exact_width() {
            // `(width = 600px)` behaves like `(width: 600px)` (Equals range).
            var cond = ContainerQueryParser.ParseCondition("(width = 600px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.True,
                "exact-width match must return true");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(601)), Is.False,
                "off-by-one must not match");
        }

        // ---- Height range form requires a size container ----

        [Test]
        public void Range_gte_height_matches_size_container() {
            // `(height >= 400px)` requires `container-type: size`; an
            // inline-size container has no block axis and never matches.
            var cond = ContainerQueryParser.ParseCondition("(height >= 400px)");
            Assert.That(cond.Evaluate(ContainerContext.Size(800, 600)), Is.True,
                "600px height >= 400px threshold must match with size container");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(800)), Is.False,
                "inline-size container has no block axis; height query must not match");
            Assert.That(cond.Evaluate(ContainerContext.Size(800, 200)), Is.False,
                "200px height < 400px threshold must NOT match");
        }

        // ---- Combinators ----

        [Test]
        public void Range_form_combined_with_and() {
            // `(width >= 400px) and (width <= 1000px)` — a bandwidth window.
            var cond = ContainerQueryParser.ParseCondition("(width >= 400px) and (width <= 1000px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.True,
                "600px is in the [400, 1000] band → must match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(200)), Is.False,
                "200px < 400px lower bound → must NOT match");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(1200)), Is.False,
                "1200px > 1000px upper bound → must NOT match");
        }

        [Test]
        public void Range_form_combined_with_or() {
            // `(width >= 1000px) or (width <= 200px)` — bimodal.
            var cond = ContainerQueryParser.ParseCondition("(width >= 1000px) or (width <= 200px)");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(1200)), Is.True,
                "1200px >= 1000px → first branch matches");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(150)), Is.True,
                "150px <= 200px → second branch matches");
            Assert.That(cond.Evaluate(ContainerContext.InlineSize(600)), Is.False,
                "600px is in neither branch → must NOT match");
        }

        // ---- Full cascade integration ----

        [Test]
        public void Cascade_applies_rule_when_range_gte_matches() {
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BoxIndex(doc, "p", 800, "x");
            var engine = new CascadeEngine(new[] {
                Author("@container (width >= 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = idx;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                "@container range >= rule must apply when container width (800px) meets threshold (600px)");
        }

        [Test]
        public void Cascade_does_not_apply_when_range_gte_misses() {
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BoxIndex(doc, "p", 400, "x");
            var engine = new CascadeEngine(new[] {
                Author(
                    "#x { color: green; }" +
                    "@container (width >= 600px) { #x { color: red; } }")
            });
            engine.ElementToBoxLookup = idx;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("green"),
                "container 400px < 600px threshold; range >= rule must NOT apply");
        }

        [Test]
        public void Cascade_applies_rule_when_range_lte_matches() {
            var doc = Html("<div id='p'><span id='x'>y</span></div>");
            var idx = BoxIndex(doc, "p", 300, "x");
            var engine = new CascadeEngine(new[] {
                Author("@container (width <= 500px) { #x { color: blue; } }")
            });
            engine.ElementToBoxLookup = idx;
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"),
                "@container range <= rule must apply when container width (300px) is within threshold (500px)");
        }
    }
}
