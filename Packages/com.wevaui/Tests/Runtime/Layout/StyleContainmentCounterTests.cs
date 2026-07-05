using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Containment L2 §3.3 — style containment counter-scoping tests.
    //
    // Style containment (`contain: style`, `contain: strict`, `contain: content`)
    // makes an element a counter-isolation boundary: descendant counter-increment /
    // counter-set mutations MUST NOT leak to elements outside the boundary, while
    // counter() reads inside the boundary MAY still see outer counter values.
    //
    // All tests exercise the CounterContext.BuildFor path via BoxBuilder pseudo-
    // element resolution — the same hot path used in production.
    //
    // References:
    //   CSS Containment L2 §2.3  — shorthand token expansion (strict/content)
    //   CSS Containment L2 §3.3  — style containment semantics
    //   CSS Lists L3 §5           — counter scope model
    public class StyleContainmentCounterTests {
        // ── Test helpers ─────────────────────────────────────────────────────────

        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithPseudos(
            string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));

            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);
            return (bb.BuildDocument(doc), styles);
        }

        // Find the first TextRun with the given text value anywhere in the tree.
        static TextRun FindTextRun(Box root, string expected) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text == expected) return tr;
            }
            return null;
        }

        // Collect all pseudo-element TextRun texts (element == null, non-whitespace).
        static List<string> CollectPseudoTexts(Box root) {
            var result = new List<string>();
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Element == null && tr.Text != null
                    && tr.Text.Trim().Length > 0) {
                    result.Add(tr.Text);
                }
            }
            return result;
        }

        // ── ContainmentResolver.HasStyle unit tests ───────────────────────────

        // 1. `contain: style` activates style containment.
        [Test]
        public void HasStyle_style_token_returns_true() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:style\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.True,
                "contain:style must activate HasStyle");
        }

        // 2. `contain: strict` (= layout+paint+size+style) activates style containment.
        [Test]
        public void HasStyle_strict_token_returns_true() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:strict\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.True,
                "contain:strict must activate HasStyle (strict = layout+paint+size+style, css-contain-2 §2.3)");
        }

        // 3. `contain: content` (= layout+paint+style) activates style containment.
        [Test]
        public void HasStyle_content_token_returns_true() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:content\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.True,
                "contain:content must activate HasStyle (content = layout+paint+style, css-contain-2 §2.3)");
        }

        // 4. `contain: layout` alone does NOT activate style containment.
        [Test]
        public void HasStyle_layout_only_returns_false() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:layout\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.False,
                "contain:layout alone must NOT activate style containment");
        }

        // 5. `contain: paint` alone does NOT activate style containment.
        [Test]
        public void HasStyle_paint_only_returns_false() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:paint\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.False,
                "contain:paint alone must NOT activate style containment");
        }

        // 6. `contain: size` alone does NOT activate style containment.
        [Test]
        public void HasStyle_size_only_returns_false() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:size\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.False,
                "contain:size alone must NOT activate style containment");
        }

        // 7. `contain: none` (initial) does NOT activate style containment.
        [Test]
        public void HasStyle_none_returns_false() {
            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasStyle(cs), Is.False,
                "initial contain:none must not activate style containment");
        }

        // 8. strict does NOT change HasLayout / HasPaint / HasSize behaviours.
        [Test]
        public void Strict_implies_HasLayout_HasPaint_HasSize_unchanged() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:strict\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            // All four bits must be set for strict.
            Assert.That(ContainmentResolver.HasLayout(cs), Is.True,  "strict implies layout");
            Assert.That(ContainmentResolver.HasPaint(cs),  Is.True,  "strict implies paint");
            Assert.That(ContainmentResolver.HasSize(cs),   Is.True,  "strict implies size");
            Assert.That(ContainmentResolver.HasStyle(cs),  Is.True,  "strict implies style");
        }

        // 9. content does NOT change HasLayout / HasPaint / HasSize behaviours.
        [Test]
        public void Content_implies_HasLayout_HasPaint_but_not_HasSize() {
            var doc = HtmlParser.Parse("<div id=\"x\" style=\"contain:content\"></div>");
            var engine = new CascadeEngine(new OriginatedStylesheet[0]);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(ContainmentResolver.HasLayout(cs), Is.True,  "content implies layout");
            Assert.That(ContainmentResolver.HasPaint(cs),  Is.True,  "content implies paint");
            Assert.That(ContainmentResolver.HasStyle(cs),  Is.True,  "content implies style");
            Assert.That(ContainmentResolver.HasSize(cs),   Is.False, "content must NOT imply size");
        }

        // ── Counter isolation: descendant increments do not leak out ──────────

        // 10. Core isolation: descendant increment inside style boundary doesn't
        //     affect a sibling counter(c) outside the boundary.
        [Test]
        public void Style_boundary_prevents_descendant_increment_from_leaking() {
            // Layout:
            //   .outer (counter-reset: c)
            //     .boundary (contain: style)
            //       .inner (counter-increment: c) ← inside boundary
            //     .sibling::before { content: counter(c) } ← outside boundary
            //
            // Expected: .inner's increment is scoped inside .boundary, so
            // .sibling sees counter(c) = 0 (the reset value), not 1.
            const string css = @"
                .outer    { counter-reset: c; }
                .boundary { contain: style; }
                .inner    { counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            // .sibling::before must see counter(c) = 0, not 1.
            var run0 = FindTextRun(root, "0");
            Assert.That(run0, Is.Not.Null,
                "sibling outside style boundary must see counter(c)=0; descendant increment must not leak");
            // "1" must not appear in pseudo texts.
            var pseudoTexts = CollectPseudoTexts(root);
            Assert.That(pseudoTexts, Has.None.EqualTo("1"),
                "leaked value '1' must not appear outside the style containment boundary");
        }

        // 11. Without style containment, increment DOES leak (regression pin to
        //     ensure we haven't broken the uncontained case).
        [Test]
        public void Without_style_boundary_increment_does_leak_to_sibling() {
            // Same structure but NO contain:style. .inner increments, .sibling
            // must see counter(c) = 1.
            const string css = @"
                .outer    { counter-reset: c; }
                .inner    { counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run1 = FindTextRun(root, "1");
            Assert.That(run1, Is.Not.Null,
                "without style containment, counter-increment inside subtree MUST leak to sibling");
        }

        // 12. counter() inside a style boundary CAN still read an outer counter.
        [Test]
        public void Counter_inside_style_boundary_reads_outer_counter_value() {
            // .outer sets counter c = 5.
            // .boundary (contain: style) has a child .inner::before { counter(c) }.
            // The inner element should see c=5 from the outer scope.
            const string css = @"
                .outer    { counter-reset: c 5; }
                .boundary { contain: style; }
                .inner::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner"">x</div>
                    </div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run5 = FindTextRun(root, "5");
            Assert.That(run5, Is.Not.Null,
                "counter() inside style boundary must still read outer counter value (reading is unrestricted)");
        }

        // 13. counter-reset inside a boundary is scoped locally; the outer
        //     counter (same name) is restored after the boundary's subtree.
        [Test]
        public void Counter_reset_inside_boundary_does_not_affect_outer_scope() {
            // .outer resets c=10. .boundary (contain:style) child resets c=0.
            // .sibling::before reads counter(c) — must see 10 not 0.
            const string css = @"
                .outer    { counter-reset: c 10; }
                .boundary { contain: style; }
                .boundary-child { counter-reset: c 0; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""boundary-child""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run10 = FindTextRun(root, "10");
            Assert.That(run10, Is.Not.Null,
                "outer counter(c)=10 must survive after style-contained child reset c=0 internally");
        }

        // 14. counter-set inside a boundary doesn't overwrite the outer value.
        [Test]
        public void Counter_set_inside_boundary_does_not_overwrite_outer_value() {
            const string css = @"
                .outer    { counter-reset: c 7; }
                .boundary { contain: style; }
                .inner    { counter-set: c 99; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run7 = FindTextRun(root, "7");
            Assert.That(run7, Is.Not.Null,
                "counter-set:99 inside style boundary must not overwrite outer counter(c)=7");
        }

        // 15. `contain: strict` also acts as a style containment boundary.
        [Test]
        public void Strict_boundary_also_scopes_counter_increments() {
            const string css = @"
                .outer    { counter-reset: c; }
                .boundary { contain: strict; width: 100px; height: 100px; }
                .inner    { counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run0 = FindTextRun(root, "0");
            Assert.That(run0, Is.Not.Null,
                "contain:strict must scope counter-increment; sibling must see c=0");
        }

        // 16. `contain: content` also acts as a style containment boundary.
        [Test]
        public void Content_boundary_also_scopes_counter_increments() {
            const string css = @"
                .outer    { counter-reset: c; }
                .boundary { contain: content; }
                .inner    { counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run0 = FindTextRun(root, "0");
            Assert.That(run0, Is.Not.Null,
                "contain:content must scope counter-increment; sibling must see c=0");
        }

        // 17. `contain: layout` and `contain: paint` do NOT scope counters.
        [Test]
        public void Layout_paint_boundary_does_not_scope_counters() {
            const string css = @"
                .outer    { counter-reset: c; }
                .boundary { contain: layout paint; }
                .inner    { counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            // layout+paint boundary must NOT contain counters.
            var run1 = FindTextRun(root, "1");
            Assert.That(run1, Is.Not.Null,
                "contain:layout paint must NOT scope counters; sibling must see c=1");
        }

        // 18. Nested style boundaries: only the innermost is the active scope.
        [Test]
        public void Nested_style_boundaries_each_independently_scope_increments() {
            // .outer resets c=0. .b1 (contain:style) wraps .b2 (contain:style) which
            // wraps .inner (counter-increment: c).
            // .after-b1::before (sibling of b1) reads counter(c) — expects 0.
            const string css = @"
                .outer { counter-reset: c; }
                .b1    { contain: style; }
                .b2    { contain: style; }
                .inner { counter-increment: c; }
                .after-b1::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""b1"">
                        <div class=""b2"">
                            <div class=""inner""></div>
                        </div>
                    </div>
                    <div class=""after-b1"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run0 = FindTextRun(root, "0");
            Assert.That(run0, Is.Not.Null,
                "nested style boundaries must prevent counter leakage; after-b1 must see c=0");
        }

        // 19. Multiple siblings: only the contained subtree is isolated.
        //     Sibling increments OUTSIDE the boundary still accumulate normally.
        [Test]
        public void Increments_outside_boundary_accumulate_normally() {
            // .outer resets c=0.
            // .before-boundary (counter-increment: c) → c becomes 1
            // .boundary (contain: style, inner increments c inside — isolated)
            // .after-boundary (counter-increment: c) → c becomes 2 (not 3)
            // .last::before reads counter(c) — expects 2.
            const string css = @"
                .outer           { counter-reset: c; }
                .before-boundary { counter-increment: c; }
                .boundary        { contain: style; }
                .inner           { counter-increment: c; }
                .after-boundary  { counter-increment: c; }
                .last::before    { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""before-boundary""></div>
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""after-boundary""></div>
                    <div class=""last"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run2 = FindTextRun(root, "2");
            Assert.That(run2, Is.Not.Null,
                "increments outside style boundary must accumulate; c=1 (before) + 1 (after) = 2");
        }

        // 20. List-item ordinals across a style boundary: ordinals inside a
        //     style-contained element do not disturb the list-item counter outside.
        [Test]
        public void List_item_ordinals_outside_boundary_unaffected_by_items_inside() {
            // .list resets "item". .li1 increments. .boundary (contain:style)
            // contains extra .sub-item elements that would increment "item" inside
            // (but are scoped). .li2 then increments outside.
            // .li1::before = "1", .li2::before = "2" — not "3" or "4".
            const string css = @"
                .list     { counter-reset: item; }
                .li1      { counter-increment: item; }
                .li2      { counter-increment: item; }
                .li1::before { content: counter(item); }
                .li2::before { content: counter(item); }
                .boundary { contain: style; }
                .sub-item { counter-increment: item; }
            ";
            const string html = @"
                <div class=""list"">
                    <div class=""li1"">a</div>
                    <div class=""boundary"">
                        <div class=""sub-item""></div>
                        <div class=""sub-item""></div>
                    </div>
                    <div class=""li2"">b</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run1 = FindTextRun(root, "1");
            var run2 = FindTextRun(root, "2");
            Assert.That(run1, Is.Not.Null, ".li1::before must show counter(item)=1");
            Assert.That(run2, Is.Not.Null, ".li2::before must show counter(item)=2 (sub-items inside boundary don't count)");
        }

        // 21. The boundary element's own counter-increment is NOT suppressed
        //     (only DESCENDANT operations are scoped).
        [Test]
        public void Boundary_element_own_increment_is_not_suppressed() {
            // .outer resets c=0. .boundary itself has counter-increment:c AND contain:style.
            // .sibling::before must see counter(c)=1 (boundary's own increment is visible outside).
            const string css = @"
                .outer    { counter-reset: c; }
                .boundary { contain: style; counter-increment: c; }
                .sibling::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sibling"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var run1 = FindTextRun(root, "1");
            Assert.That(run1, Is.Not.Null,
                "the boundary element's own counter-increment must not be suppressed; sibling sees c=1");
        }

        // 22. Multiple counters: style containment is per-counter; an outer
        //     counter B that is not touched inside the boundary is unaffected.
        [Test]
        public void Style_boundary_does_not_interfere_with_untouched_counters() {
            // .outer resets both a and b. Inside boundary, only a is incremented.
            // Sibling reads both: a=0 (isolated), b=5 (unmodified).
            const string css = @"
                .outer    { counter-reset: a b 5; }
                .boundary { contain: style; }
                .inner    { counter-increment: a; }
                .sib-a::before { content: counter(a); }
                .sib-b::before { content: counter(b); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""boundary"">
                        <div class=""inner""></div>
                    </div>
                    <div class=""sib-a"">x</div>
                    <div class=""sib-b"">x</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            // a must be 0 (increment scoped away), b must be 5 (untouched).
            var runA = FindTextRun(root, "0");
            var runB = FindTextRun(root, "5");
            Assert.That(runA, Is.Not.Null, "counter a must stay 0 outside the style boundary");
            Assert.That(runB, Is.Not.Null, "counter b must still be 5 (untouched by the boundary)");
        }
    }
}
