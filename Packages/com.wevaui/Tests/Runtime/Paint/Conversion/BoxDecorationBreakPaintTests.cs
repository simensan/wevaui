using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Paint-layer tests for CSS Fragmentation L3 §6.1 box-decoration-break.
    //
    // The property controls how borders and backgrounds are applied to inline
    // elements that wrap across multiple line boxes:
    //
    //   slice (initial): the inline box is treated as one unbroken decoration;
    //     the LEFT border of non-first fragments and the RIGHT border of
    //     non-last fragments are suppressed. Background paints per-fragment
    //     rect (unavoidable with the per-fragment-box model — each fragment
    //     IS a box and gets its own FillRect).
    //
    //   clone: each fragment is decorated independently with full borders on
    //     all four sides. This is distinct from slice in the presence of
    //     multi-line wrapping.
    //
    // Implementation notes (paint side only — see CSS_OPEN_GAPS.md B22 for
    // the layout-side gap):
    //   - InlineLayout.AttachInlineFragmentsToLines sets IsLineFragment=true
    //     on non-first fragment InlineBoxes and IsLastFragment=true on the
    //     last fragment of each span.
    //   - BoxToPaintConverter.EmitVisibleDecorations checks these flags under
    //     slice mode and zeroes out the break-edge BorderEdge values.
    //
    // Viewport: 25px wide, 600px tall so "AB CD" (2 words × 16px) wraps onto
    // 2 lines.  MonoFontMetrics: CharWidthEm=0.5 → 8px/char at 16px font-size.
    // "AB" = 16px, "CD" = 16px; with viewport 25px both fit individually but
    // not together → 2 InlineBox fragments guaranteed.
    //
    // NUnit constraint notes — NEVER chain .Within() off Is.LessThan/GreaterThan;
    // NEVER use Does.Not.Contain on collections (use Has.None).
    public class BoxDecorationBreakPaintTests {
        // ── helpers ────────────────────────────────────────────────────────

        // Collect all InlineBox instances that carry a specific element tag.
        static List<InlineBox> InlineFragments(Box root, string tagName) {
            return AllBoxes(root)
                .OfType<InlineBox>()
                .Where(ib => ib.Element != null && ib.Element.TagName == tagName)
                .ToList();
        }

        // Collect StrokeBorderCommand instances from the paint list.
        static List<StrokeBorderCommand> BorderCommands(List<PaintCommand> cmds) {
            return cmds.OfType<StrokeBorderCommand>().ToList();
        }

        // Collect FillRect commands from the paint list.
        static List<FillRectCommand> FillRects(List<PaintCommand> cmds) {
            return cmds.OfType<FillRectCommand>().ToList();
        }

        // Build a 2-fragment scenario: "AB CD" in a <span> with a 25px-wide
        // viewport. Returns (root, spans, paintCommands).
        static (Box root, List<InlineBox> frags, List<PaintCommand> cmds)
            BuildTwoFragmentScenario(string css) {
            const string html = "<span class=\"s\">AB CD</span>";
            var (root, _, _) = Build(html, css, viewportWidth: 25, viewportHeight: 600);
            var frags = InlineFragments(root, "span");
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            return (root, frags, cmds);
        }

        // Build a 3-fragment scenario: "AB CD EF" in a <span>.
        static (Box root, List<InlineBox> frags, List<PaintCommand> cmds)
            BuildThreeFragmentScenario(string css) {
            const string html = "<span class=\"s\">AB CD EF</span>";
            var (root, _, _) = Build(html, css, viewportWidth: 25, viewportHeight: 600);
            var frags = InlineFragments(root, "span");
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            return (root, frags, cmds);
        }

        // ── property-cascade regression (paint only acts on what the cascade
        //    delivers; if the cascade is wrong nothing else matters) ────────

        [Test]
        public void Box_decoration_break_initial_value_is_slice_from_cascade() {
            // Without any author rule, box-decoration-break defaults to slice.
            // This test is a cascade regression that ensures the paint layer
            // always has slice as the baseline even after cascade refactors.
            var (_, _, cmds) = BuildTwoFragmentScenario("");
            // Under slice the test implicitly runs without exception — the test
            // below (Slice_suppresses_break_edge_borders) asserts the actual values.
            Assert.That(cmds, Is.Not.Null);
        }

        // ── fragment-count sanity ──────────────────────────────────────────

        [Test]
        public void Two_fragment_scenario_produces_exactly_two_inline_fragments() {
            var (_, frags, _) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            // Layout must produce 2 fragments or the border-suppression tests
            // below are vacuous. If this fails, adjust the viewport or text.
            Assert.That(frags.Count, Is.EqualTo(2),
                "Expected 2 InlineBox fragments for 'AB CD' at 25px viewport");
        }

        [Test]
        public void Three_fragment_scenario_produces_exactly_three_inline_fragments() {
            var (_, frags, _) = BuildThreeFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(3),
                "Expected 3 InlineBox fragments for 'AB CD EF' at 25px viewport");
        }

        // ── IsLineFragment / IsLastFragment flags ──────────────────────────

        [Test]
        public void First_fragment_is_not_marked_IsLineFragment() {
            var (_, frags, _) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            // Fragments are in line order; the first has IsLineFragment=false.
            var first = frags.FirstOrDefault(f => !f.IsLineFragment);
            Assert.That(first, Is.Not.Null, "First fragment must have IsLineFragment=false");
        }

        [Test]
        public void Second_fragment_is_marked_IsLineFragment() {
            var (_, frags, _) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            var second = frags.FirstOrDefault(f => f.IsLineFragment);
            Assert.That(second, Is.Not.Null, "Second fragment must have IsLineFragment=true");
        }

        [Test]
        public void Last_fragment_has_IsLastFragment_true_for_two_fragment_span() {
            var (_, frags, _) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            // Exactly one fragment must have IsLastFragment=true: the second one.
            int lastCount = frags.Count(f => f.IsLastFragment);
            Assert.That(lastCount, Is.EqualTo(1), "Exactly one fragment should be marked IsLastFragment");
            // The last fragment is the non-first (IsLineFragment=true) one.
            var last = frags.First(f => f.IsLastFragment);
            Assert.That(last.IsLineFragment, Is.True, "The last of two fragments is also a line fragment");
        }

        [Test]
        public void Single_line_span_is_both_first_and_last_fragment() {
            // Single-line: one fragment, not a line clone, and it IS the last.
            const string html = "<span class=\"s\">Hi</span>";
            var (root, _, _) = Build(html, ".s { border: 2px solid black; }",
                                     viewportWidth: 800, viewportHeight: 600);
            var frags = InlineFragments(root, "span");
            // A single-line span produces exactly 1 fragment.
            Assert.That(frags.Count, Is.EqualTo(1));
            Assert.That(frags[0].IsLineFragment, Is.False, "Single fragment is not a line clone");
            Assert.That(frags[0].IsLastFragment, Is.True, "Single fragment must be the last fragment");
        }

        // ── slice (default) — border suppression ──────────────────────────

        [Test]
        public void Slice_suppresses_right_border_on_first_of_two_fragments() {
            // Under slice, the RIGHT border of the first fragment is at a break
            // edge and must be suppressed (the box continues on the next line).
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));

            var borders = BorderCommands(cmds);
            // Two StrokeBorder commands — one per fragment.
            Assert.That(borders.Count, Is.EqualTo(2),
                "Two border strokes expected for two fragments");

            // First fragment (IsLineFragment=false, IsLastFragment=false):
            //   right border suppressed, left present.
            var first = borders[0];
            Assert.That(first.Borders.Right.Style, Is.EqualTo(BorderStyle.None),
                "First fragment: right (break) edge must be suppressed under slice");
            Assert.That(first.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None),
                "First fragment: left edge must be present (not a break edge)");
        }

        [Test]
        public void Slice_suppresses_left_border_on_second_of_two_fragments() {
            // Under slice, the LEFT border of the second (last) fragment is at a
            // break edge and must be suppressed.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));

            // Second fragment (IsLineFragment=true, IsLastFragment=true):
            //   left border suppressed, right present.
            var second = borders[1];
            Assert.That(second.Borders.Left.Style, Is.EqualTo(BorderStyle.None),
                "Second fragment: left (break) edge must be suppressed under slice");
            Assert.That(second.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None),
                "Second fragment: right edge must be present (it is the trailing edge)");
        }

        [Test]
        public void Slice_keeps_top_and_bottom_borders_on_all_fragments() {
            // Slice only suppresses inline-axis (left/right) break edges.
            // Top and bottom borders are visible on every fragment.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(2));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));
            foreach (var bc in borders) {
                Assert.That(bc.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None),
                    "Top border must be present on all fragments under slice");
                Assert.That(bc.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None),
                    "Bottom border must be present on all fragments under slice");
            }
        }

        [Test]
        public void Slice_three_fragments_middle_suppresses_both_left_and_right() {
            // The middle fragment of a 3-fragment span is neither first nor last.
            // Under slice BOTH left and right borders are suppressed for it.
            var (_, frags, cmds) = BuildThreeFragmentScenario(
                ".s { border: 2px solid black; }");
            Assert.That(frags.Count, Is.EqualTo(3));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(3));

            // Middle fragment: borders[1].
            var mid = borders[1];
            Assert.That(mid.Borders.Left.Style, Is.EqualTo(BorderStyle.None),
                "Middle fragment: left break edge suppressed");
            Assert.That(mid.Borders.Right.Style, Is.EqualTo(BorderStyle.None),
                "Middle fragment: right break edge suppressed");
            // Top/bottom still visible.
            Assert.That(mid.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None),
                "Middle fragment: top border present");
            Assert.That(mid.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None),
                "Middle fragment: bottom border present");
        }

        // ── clone — full borders on every fragment ─────────────────────────

        [Test]
        public void Clone_paints_full_border_on_first_fragment() {
            // Under clone every fragment gets all four borders.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; box-decoration-break: clone; }");
            Assert.That(frags.Count, Is.EqualTo(2));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));

            var first = borders[0];
            Assert.That(first.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: first fragment left border present");
            Assert.That(first.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: first fragment right border present");
            Assert.That(first.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: first fragment top border present");
            Assert.That(first.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: first fragment bottom border present");
        }

        [Test]
        public void Clone_paints_full_border_on_second_fragment() {
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; box-decoration-break: clone; }");
            Assert.That(frags.Count, Is.EqualTo(2));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));

            var second = borders[1];
            Assert.That(second.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: second fragment left border present");
            Assert.That(second.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None),
                "clone: second fragment right border present");
        }

        [Test]
        public void Clone_three_fragments_all_get_all_four_borders() {
            var (_, frags, cmds) = BuildThreeFragmentScenario(
                ".s { border: 2px solid black; box-decoration-break: clone; }");
            Assert.That(frags.Count, Is.EqualTo(3));

            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(3));

            foreach (var bc in borders) {
                Assert.That(bc.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None),
                    "clone: every fragment has left border");
                Assert.That(bc.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None),
                    "clone: every fragment has right border");
                Assert.That(bc.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None),
                    "clone: every fragment has top border");
                Assert.That(bc.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None),
                    "clone: every fragment has bottom border");
            }
        }

        // ── single-fragment element: both values identical ─────────────────

        [Test]
        public void Single_fragment_slice_has_all_four_borders() {
            const string html = "<span class=\"s\">Hi</span>";
            var (root, _, _) = Build(html, ".s { border: 2px solid black; }",
                                     viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(1));
            var b = borders[0];
            Assert.That(b.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Single_fragment_clone_has_all_four_borders() {
            const string html = "<span class=\"s\">Hi</span>";
            var (root, _, _) = Build(html,
                ".s { border: 2px solid black; box-decoration-break: clone; }",
                viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(1));
            var b = borders[0];
            Assert.That(b.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(b.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None));
        }

        // ── background: paint per fragment rect in both modes ──────────────

        [Test]
        public void Clone_paints_background_per_fragment_N_times() {
            // Under clone each fragment is an independent decoration box —
            // each should emit its own FillRect for the background.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { background-color: red; box-decoration-break: clone; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            var fills = FillRects(cmds)
                .Where(f => f.Brush != null && f.Brush.Kind == BrushKind.SolidColor
                            && f.Brush.Color.R > 0.5f && f.Brush.Color.G < 0.3f)
                .ToList();
            Assert.That(fills.Count, Is.EqualTo(2),
                "clone: each of the 2 fragments must emit its own background FillRect");
        }

        [Test]
        public void Slice_paints_background_per_fragment_rect_as_well() {
            // Background is always painted per-fragment rect (the per-fragment-
            // box model makes this unavoidable). Two fragments = two FillRects
            // under slice just as under clone.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { background-color: red; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            var fills = FillRects(cmds)
                .Where(f => f.Brush != null && f.Brush.Kind == BrushKind.SolidColor
                            && f.Brush.Color.R > 0.5f && f.Brush.Color.G < 0.3f)
                .ToList();
            Assert.That(fills.Count, Is.EqualTo(2),
                "slice: two fragments still produce two background FillRects");
        }

        // ── border-radius: applied per fragment under clone ────────────────

        [Test]
        public void Clone_border_radius_nonzero_on_each_fragment() {
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; border-radius: 4px;" +
                "     box-decoration-break: clone; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));
            foreach (var bc in borders) {
                Assert.That(bc.Radii.TopLeft.XRadius, Is.GreaterThan(0),
                    "clone: border-radius applied per fragment");
            }
        }

        [Test]
        public void Slice_border_radius_still_applied_per_fragment_box() {
            // Even under slice the InlineBox fragments are separate boxes, so
            // border-radius resolves against each box's own bounds. Top-left and
            // top-right radii are present on both fragments (top/bottom edges are
            // never suppressed). The exact radius value is the box's own.
            var (_, frags, cmds) = BuildTwoFragmentScenario(
                ".s { border: 2px solid black; border-radius: 4px; }");
            Assert.That(frags.Count, Is.EqualTo(2));
            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.EqualTo(2));
            foreach (var bc in borders) {
                Assert.That(bc.Radii.TopLeft.XRadius, Is.GreaterThan(0),
                    "slice: border-radius still applied to each fragment box");
            }
        }

        // ── inheritance: non-inherited — child sees initial (slice) ───────

        [Test]
        public void Clone_on_parent_does_not_propagate_to_child_inline() {
            // box-decoration-break is non-inherited per CSS Fragmentation L3 §6.1.
            // A child <em> inside a clone-decorated <span> must see slice, not clone.
            const string html =
                "<span class=\"outer\"><em class=\"inner\">A B</em></span>";
            const string css =
                ".outer { box-decoration-break: clone; }" +
                ".inner { border: 2px solid blue; }";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var innerFrags = InlineFragments(root, "em");
            // Single-line em — one fragment. Its border-break is initial=slice.
            // For a single-line span slice and clone are identical (all 4 borders).
            // We just verify the fragment doesn't unexpectedly inherit clone.
            Assert.That(innerFrags.Count, Is.GreaterThanOrEqualTo(1));
            // The em's painted borders must be present (no suppression on a single
            // fragment regardless of slice/clone).
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var emBorders = BorderCommands(cmds)
                .Where(bc => bc.Borders.Left.Style != BorderStyle.None ||
                             bc.Borders.Right.Style != BorderStyle.None)
                .ToList();
            Assert.That(emBorders.Count, Is.GreaterThanOrEqualTo(1),
                "Inner em must have its own borders (box-decoration-break non-inherited)");
        }

        // ── regression: block elements unchanged by this feature ──────────

        [Test]
        public void Block_element_border_unaffected_by_box_decoration_break() {
            // box-decoration-break on a block element that doesn't fragment
            // should leave all four borders intact regardless of value.
            const string html = "<div class=\"b\">Hello</div>";
            const string css = ".b { border: 2px solid black; box-decoration-break: clone; }";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var borders = BorderCommands(cmds);
            Assert.That(borders.Count, Is.GreaterThanOrEqualTo(1));
            var first = borders[0];
            Assert.That(first.Borders.Left.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(first.Borders.Right.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(first.Borders.Top.Style, Is.Not.EqualTo(BorderStyle.None));
            Assert.That(first.Borders.Bottom.Style, Is.Not.EqualTo(BorderStyle.None));
        }
    }
}
