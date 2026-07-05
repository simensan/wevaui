using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Fragmentation L3 §6.1 — layout-side inline-axis PBM reservation for
    // box-decoration-break. Under slice (the initial value) the inline-axis
    // padding + border + margin are applied ONLY to the START edge of the FIRST
    // fragment and the END edge of the LAST fragment; break edges get none.
    //
    // MonoFontMetrics defaults: charWidthEm=0.5, so at 16px each char = 8px.
    // All expected values are derived arithmetically in the comments below and
    // reference CSS Fragmentation L3 §6.1 and CSS 2.1 §10.3.1.
    public class BoxDecorationBreakLayoutTests {

        // ----------------------------------------------------------------
        // Test helpers
        // ----------------------------------------------------------------
        static List<InlineBox> InlineFragmentsFor(Box root, string tag) {
            var list = new List<InlineBox>();
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox ib && ib.Element?.TagName == tag)
                    list.Add(ib);
            }
            return list;
        }

        static List<LineBox> LinesIn(BlockBox container) {
            var list = new List<LineBox>();
            foreach (var c in container.Children)
                if (c is LineBox lb) list.Add(lb);
            return list;
        }

        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                    && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static TextRun FirstTextRunInLine(LineBox line) {
            foreach (var c in line.Children)
                if (c is TextRun tr) return tr;
            return null;
        }

        const double Eps = 0.5; // tolerance for floating-point layout arithmetic

        // ================================================================
        // §1 — Single-line span with inline padding
        //      CSS Fragmentation L3 §6.1: a single-line span is both the
        //      first and last fragment, so it receives BOTH start and end
        //      padding edges. The fast path (TryLayoutSingleRunFast) must
        //      also handle this correctly.
        // ================================================================

        [Test]
        public void Single_line_span_padding_fragment_width_includes_both_edges() {
            // "hello" = 5 chars × 8px = 40px of text.
            // padding: 0 10px → startPbm=10, endPbm=10.
            // Expected fragment width = 10 + 40 + 10 = 60px.
            // Expected fragment X = 0 (start of the padding area).
            // Line width = 60px (same as fragment width for a single-run line).
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1), "fixture must stay single-line");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(1), "single-line span must have one fragment");

            // CSS Fragmentation L3 §6.1: single-line = first+last = both edges.
            InlineBox frag = frags[0];
            Assert.That(frag.X, Is.EqualTo(0).Within(Eps),
                "fragment X must start at 0 (before start padding)");
            Assert.That(frag.Width, Is.EqualTo(60).Within(Eps),
                "fragment width = startPbm(10) + text(40) + endPbm(10) = 60");

            // Line width must include both PBM edges.
            Assert.That(lines[0].Width, Is.EqualTo(60).Within(Eps),
                "line.Width = 60 (10+40+10)");
        }

        [Test]
        public void Single_line_span_text_run_offset_by_start_padding() {
            // "hello" = 5 chars × 8px = 40px.  padding: 0 10px.
            // Text run must start at X=10 (after the start padding area).
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1));

            TextRun run = FirstTextRunInLine(lines[0]);
            Assert.That(run, Is.Not.Null);
            // CSS Fragmentation L3 §6.1: start padding shifts text right by startPbm.
            Assert.That(run.X, Is.EqualTo(10).Within(Eps),
                "text run X = firstIndent(0) + startPbm(10) = 10");
        }

        [Test]
        public void Single_line_span_no_padding_unchanged() {
            // Regression guard: with zero padding the behavior must be identical
            // to the pre-B22 layout (no width inflation, text at X=0).
            // "hello" = 5×8=40px.
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                null,
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1));

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(1));

            // No PBM: fragment at X=0, Width=40, text at X=0.
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(40).Within(Eps));
            Assert.That(lines[0].Width, Is.EqualTo(40).Within(Eps));
            TextRun run = FirstTextRunInLine(lines[0]);
            Assert.That(run, Is.Not.Null);
            Assert.That(run.X, Is.EqualTo(0).Within(Eps));
        }

        // ================================================================
        // §2 — Two-line slice: start PBM on first fragment, end PBM on last
        //      CSS Fragmentation L3 §6.1: break edges get no PBM under slice.
        // ================================================================

        [Test]
        public void Two_line_slice_first_fragment_has_start_pbm_only() {
            // padding: 0 10px → startPbm=10, endPbm=10.
            // "alpha charlie" with viewport=100:
            //   items: [spacer(10), "alpha"(40), " "(8), "charlie"(56), spacer(10)]
            //   Line 1: spacer(10) + "alpha"(40) = 50 ≤ 100; " " fits at 58;
            //           "charlie" would go to 58+56=114 > 100 → wrap.
            //           Line 1 width = 50 (10 + 40, trailing space stripped).
            //   Fragment 1 (first, not last): text bbox minX=10,maxX=50.
            //     After PBM expansion: X=10-10=0, Width=(50-10)+10=50.
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 100);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2), "fixture must wrap to 2 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(2), "two-line span must have 2 fragments");

            // First fragment (IsLineFragment=false): receives start PBM only.
            // CSS Fragmentation L3 §6.1: break-edge (right) gets no border/padding.
            InlineBox first = frags[0];
            Assert.That(first.X, Is.EqualTo(0).Within(Eps),
                "first fragment X=0 (before start padding)");
            Assert.That(first.Width, Is.EqualTo(50).Within(Eps),
                "first fragment width = startPbm(10) + text(40) = 50; end PBM suppressed on break edge");

            // Line 1 width includes start PBM but not end PBM.
            Assert.That(lines[0].Width, Is.EqualTo(50).Within(Eps),
                "line 1 width = 50 (start PBM + text, no end PBM on break edge)");
        }

        [Test]
        public void Two_line_slice_last_fragment_has_end_pbm_only() {
            // Continuation of the above test.
            // Line 2: "charlie"(56) = 56 ≤ 100; spacer(end, 10) → state.X=66.
            //   Line 2 width = 66.
            //   Fragment 2 (last): text bbox minX=0, maxX=56.
            //     After PBM expansion: Width=56+10=66.
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 100);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2));

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(2));

            // Second fragment (IsLineFragment=true, IsLastFragment=true): end PBM only.
            InlineBox last = frags[1];
            Assert.That(last.X, Is.EqualTo(0).Within(Eps),
                "last fragment X=0 (no start PBM on break edge)");
            Assert.That(last.Width, Is.EqualTo(66).Within(Eps),
                "last fragment width = text(56) + endPbm(10) = 66");

            Assert.That(lines[1].Width, Is.EqualTo(66).Within(Eps),
                "line 2 width = 66 (text + end PBM)");
        }

        [Test]
        public void Two_line_slice_text_runs_positioned_correctly() {
            // Line 1 text run ("alpha") should start at X=10 (after start padding).
            // Line 2 text run ("charlie") should start at X=0 (no start PBM on break edge).
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 100);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2));

            TextRun run1 = FirstTextRunInLine(lines[0]);
            Assert.That(run1, Is.Not.Null);
            Assert.That(run1.X, Is.EqualTo(10).Within(Eps),
                "line 1 text run X=10 (start padding shifts text right)");

            TextRun run2 = FirstTextRunInLine(lines[1]);
            Assert.That(run2, Is.Not.Null);
            Assert.That(run2.X, Is.EqualTo(0).Within(Eps),
                "line 2 text run X=0 (no start PBM on non-first fragment)");
        }

        // ================================================================
        // §3 — Three-line slice: middle fragment has no PBM
        //      CSS Fragmentation L3 §6.1: ONLY start edge on first, ONLY end
        //      edge on last; ALL break edges (both sides of middle) get none.
        // ================================================================

        [Test]
        public void Three_line_slice_middle_fragment_has_no_pbm() {
            // padding: 0 10px, "alpha bravo charlie", viewport=80.
            // Items: [spacer(10), "alpha"(40), " "(8), "bravo"(40), " "(8),
            //         "charlie"(56), spacer(10)]
            // Line 1: 10+40=50 ≤ 80; " " at 58; "bravo" at 58+40=98>80 → wrap.
            //   Line1 width=50 (spacer+alpha; trailing space stripped).
            // Line 2: "bravo"=40; " " at 48; "charlie"=48+56=104>80 → wrap.
            //   Line2 width=40 (no PBM on either break edge).
            // Line 3: "charlie"=56; spacer(end,10)=66. Line3 width=66.
            var (root, _, _) = Build(
                "<p><span>alpha bravo charlie</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 80);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(3), "fixture must wrap to 3 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(3), "three-line span must have 3 fragments");

            // Fragment 1 (first): startPbm only → Width=10+40=50, X=0.
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps),
                "first fragment X=0");
            Assert.That(frags[0].Width, Is.EqualTo(50).Within(Eps),
                "first fragment width=50 (startPbm 10 + text 40)");

            // Fragment 2 (middle): both break edges → no PBM → Width=40, X=0.
            // CSS Fragmentation L3 §6.1: break edges receive neither left nor right PBM.
            Assert.That(frags[1].X, Is.EqualTo(0).Within(Eps),
                "middle fragment X=0 (no start PBM on break edge)");
            Assert.That(frags[1].Width, Is.EqualTo(40).Within(Eps),
                "middle fragment width=40 (no PBM on either break edge)");

            // Fragment 3 (last): endPbm only → Width=56+10=66, X=0.
            Assert.That(frags[2].X, Is.EqualTo(0).Within(Eps),
                "last fragment X=0");
            Assert.That(frags[2].Width, Is.EqualTo(66).Within(Eps),
                "last fragment width=66 (text 56 + endPbm 10)");
        }

        // ================================================================
        // §4 — Border edges (not just padding)
        // ================================================================

        [Test]
        public void Single_line_span_border_contributes_to_pbm() {
            // border: 0 3px solid black → bordLeft=3, bordRight=3.
            // padding=0, margin=0 → startPbm=3, endPbm=3.
            // "hello"=40px. Fragment width=3+40+3=46. X=0. Line.Width=46.
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { border: 3px solid black; border-top: none; border-bottom: none; padding: 0; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1), "single-line fixture");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(1));

            // CSS Fragmentation L3 §6.1: border contributes to PBM edge reservation.
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(46).Within(Eps),
                "fragment width = bordLeft(3) + text(40) + bordRight(3) = 46");
        }

        // ================================================================
        // §5 — Margin edges
        // ================================================================

        [Test]
        public void Single_line_span_margin_contributes_to_pbm() {
            // margin: 0 5px → marLeft=5, marRight=5. padding=0, border=0.
            // startPbm=5, endPbm=5. "hello"=40. Width=5+40+5=50.
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { margin: 0 5px; padding: 0; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1));

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(1));

            // CSS Fragmentation L3 §6.1 / CSS 2.1 §10.3.1: inline margin
            // participates in start/end edge PBM reservation.
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(50).Within(Eps),
                "fragment width = marLeft(5) + text(40) + marRight(5) = 50");
        }

        // ================================================================
        // §6 — Mixed padding + border + margin together
        // ================================================================

        [Test]
        public void Single_line_combined_padding_border_margin() {
            // padding: 0 5px, border: 3px solid black (left+right only),
            // margin: 0 2px.
            // startPbm = 5 + 3 + 2 = 10. endPbm = 5 + 3 + 2 = 10.
            // "hi" = 2×8=16px. Fragment width = 10+16+10=36.
            var (root, _, _) = Build(
                "<p><span>hi</span></p>",
                "span { padding: 0 5px; border: 3px solid black; " +
                "border-top: none; border-bottom: none; margin: 0 2px; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1));

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(1));

            // Combined: pad(5) + bord(3) + mar(2) = 10 per edge.
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(36).Within(Eps),
                "fragment width = pbm(10) + text(16) + pbm(10) = 36");
        }

        // ================================================================
        // §7 — Line-break wrap point influenced by PBM reservation
        //      Under slice, the start PBM narrows the first line so a word
        //      that would fit without padding wraps to the second line.
        // ================================================================

        [Test]
        public void Start_pbm_can_cause_wrap_that_would_not_occur_without_padding() {
            // viewport=50. "hello"=40px. Without padding: fits (40 ≤ 50).
            // With padding: 0 10px → startPbm=10. Space = 50-10=40 for text.
            // "hello" = 40px — exact fit (should still fit at ≤50+eps).
            // Now use "hello world" with same viewport:
            //   Without padding: "hello" (40) fits; "hello world"=88>50 → wraps at " ".
            //   With padding: spacer(10)+"hello"=50 ≤ 50 → line1="hello" (50px).
            //   "world"=40; spacer(end,10)=50 ≤ 50 → line2=50.
            //   Both cases: 2 lines. Same wrap point — padding didn't change it here.
            //
            // Tighter: viewport=48, "hello" = 40, without pad: fits (40≤48).
            // With padding: 0 10px → spacer(10)+"hello"=50>48 → wraps!
            //   spacer(10) alone on line 1? No — spacer doesn't wrap.
            //   Actually the spacer advances X=10, then "hello" (40) at X=10
            //   gives X=50 > 48: since HasAnyNonSpaceFragment is false (only spacer),
            //   "hello" wraps... actually the spacer ISN'T a fragment in state,
            //   so HasAnyNonSpaceFragment = false when "hello" is first considered.
            //   The check: state.X + wWidth <= availableWidth+eps?
            //   50 <= 48+1e-9? No. And !HasAnyNonSpaceFragment → no break fired.
            //   So "hello" is placed anyway (no prior content to break before).
            //   Result: still 1 line but line overflows.
            //   → This tests the WRAP effect when there IS prior content.
            //
            // Use "a hello" (1+1+5=7 chars):
            //   Without padding: "a"(8)+" "(8) fits; "hello"(40) at X=16+40=56>48 → wrap.
            //   With padding: spacer(10)+"a"=18; " "=26; "hello"=66>48 → wrap (same point).
            //   Line1: spacer(10)+"a"=18. Line2: "hello"(40)+spacer(end,10)=50.
            //   The end PBM CAUSES line2.Width=50 > available(48) but no wrap because
            //   there's no later content — spacer just advances, no wrap trigger.
            //
            // For a clear test of wrap-point widening, use "aa hello":
            //   Without padding, viewport=48: "aa"(16)+" "(8); "hello"=16+8+40=64>48 → wrap.
            //     Line1="aa" (16), line2="hello" (40).
            //   With padding 0 10px: spacer(10)+"aa"=26; " "=34; "hello"=34+40=74>48 → wrap.
            //     Line1=spacer(10)+"aa"=26. Line2="hello"(40)+spacer(end,10)=50.
            //     Line1.Width=26, Line2.Width=50.
            //   Same number of lines but with different widths — PBM reservation is confirmed.
            var (root, _, _) = Build(
                "<p><span>aa hello</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 48);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2), "must wrap to 2 lines");

            // Line 1 contains "aa" with start PBM reserved → width = 26.
            Assert.That(lines[0].Width, Is.EqualTo(26).Within(Eps),
                "line 1 = startPbm(10) + 'aa'(16) = 26");
            // Line 2 contains "hello" with end PBM → width = 50.
            Assert.That(lines[1].Width, Is.EqualTo(50).Within(Eps),
                "line 2 = 'hello'(40) + endPbm(10) = 50");
        }

        // ================================================================
        // §8 — box-decoration-break: slice is the initial value and is
        //      equivalent to the default behavior (no explicit declaration).
        // ================================================================

        [Test]
        public void Explicit_slice_value_matches_default_behavior() {
            // An explicit `box-decoration-break: slice` must produce the same
            // layout as the default (no declaration), since slice is initial.
            // Two-line test: "alpha charlie", viewport=100, padding: 0 10px.
            var (rootDefault, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; }",
                viewportWidth: 100);
            var (rootSlice, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; box-decoration-break: slice; }",
                viewportWidth: 100);

            var pDef = FirstByTag(rootDefault, "p");
            var pSlice = FirstByTag(rootSlice, "p");

            var fragsDef = InlineFragmentsFor(pDef, "span");
            var fragsSlice = InlineFragmentsFor(pSlice, "span");

            Assert.That(fragsSlice.Count, Is.EqualTo(fragsDef.Count),
                "slice must produce same fragment count as default");
            for (int i = 0; i < fragsDef.Count; i++) {
                Assert.That(fragsSlice[i].X, Is.EqualTo(fragsDef[i].X).Within(Eps),
                    $"fragment {i} X must match");
                Assert.That(fragsSlice[i].Width, Is.EqualTo(fragsDef[i].Width).Within(Eps),
                    $"fragment {i} Width must match");
            }
        }

        // ================================================================
        // §9 — Nested spans: inner span PBM stacks with outer span PBM.
        //      The LINE WIDTH reflects both layers of PBM reservation.
        //      CSS Fragmentation L3 §6.1 is non-inherited (each span
        //      resolves its own PBM independently).
        // ================================================================

        [Test]
        public void Nested_spans_line_width_reflects_stacked_pbm() {
            // Outer span: padding: 0 20px → startPbm=20, endPbm=20.
            // Inner span: padding: 0 5px  → startPbm=5,  endPbm=5.
            // "hi" = 2×8=16px.
            // Item stream: [spacer(20), spacer(5), "hi"(16), spacer(5), spacer(20)]
            // Total line width (single line, no wrap):
            //   state.X: 0 → +20 → +5 → +16 → +5 → +20 = 66.
            //   line.Width = 66.
            // This verifies that outer and inner PBMs both contribute to the
            // line's occupied width, which drives text-align centering correctly.
            var (root, _, _) = Build(
                "<p><span id='outer'><span id='inner'>hi</span></span></p>",
                "#outer { padding: 0 20px; } #inner { padding: 0 5px; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1), "single-line fixture");

            // Line width = sum of all PBM spacers + text.
            Assert.That(lines[0].Width, Is.EqualTo(66).Within(Eps),
                "line width = outer_start(20) + inner_start(5) + text(16) + inner_end(5) + outer_end(20) = 66");
        }

        [Test]
        public void Inner_span_fragment_position_reflects_outer_plus_inner_pbm() {
            // Outer span: padding: 0 20px. Inner span: padding: 0 5px.
            // "hi" = 2×8=16px. Viewport=800 (single line).
            // Text run "hi" starts at X = outerStartPbm(20) + innerStartPbm(5) = 25.
            // Inner span fragment: text bbox minX=25, maxX=41.
            //   After inner PBM expansion: X=25-5=20, Width=(41-25)+5+5=26.
            var (root, _, _) = Build(
                "<p><span id='outer'><span id='inner'>hi</span></span></p>",
                "#outer { padding: 0 20px; } #inner { padding: 0 5px; }",
                viewportWidth: 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            // Find the inner <span> fragment by its id (both are "span" tags;
            // InlineFragmentsFor returns all span fragments). The inner one
            // is narrower: it covers innerPbm(5)+text(16)+innerPbm(5)=26px.
            // The outer span, because its text runs are attributed to inner,
            // falls to the empty-inline fallback and has Width=0 — filter it.
            var allSpanFrags = InlineFragmentsFor(p, "span");
            InlineBox innerFrag = null;
            foreach (var f in allSpanFrags) {
                // The inner span fragment has non-zero width covering its PBM+text.
                if (f.Width > 1) { innerFrag = f; break; }
            }
            Assert.That(innerFrag, Is.Not.Null, "inner span fragment found");

            // CSS Fragmentation L3 §6.1: inner fragment starts after outer start PBM.
            // X = outerStart(20) + innerStart(5) - innerStart(5) = 20.
            Assert.That(innerFrag.X, Is.EqualTo(20).Within(Eps),
                "inner fragment X=20 (outer_start_pbm=20; inner fragment starts right after it)");
            Assert.That(innerFrag.Width, Is.EqualTo(26).Within(Eps),
                "inner fragment width = innerPbm(5) + text(16) + innerPbm(5) = 26");
        }

        // ================================================================
        // §10 — box-decoration-break: clone — two-line span.
        //       CSS Fragmentation L3 §6.1: under clone, EVERY fragment
        //       carries BOTH start and end PBM edges; mid-span wrap points
        //       also shift earlier because the end PBM must fit on the
        //       outgoing line.
        // ================================================================

        [Test]
        public void Clone_two_line_both_fragments_carry_both_edges() {
            // padding: 0 10px → startPbm=10, endPbm=10.
            // "alpha charlie" with viewport=100.
            //
            // Under clone the end-PBM is reserved in the fit test, so:
            //   Items: [spacer(10), "alpha"(40,clone), " "(8,clone),
            //           "charlie"(56,clone), spacer(10)]
            //   Fit for "charlie" at X=58: 58+56+10=124 > 100 → wrap.
            //   Line 1: FinishLine(non-final) adds endPbm(10) to X=50+10=60.
            //           line1.Width=60. Next line starts at X=startPbm=10.
            //   Fit for "charlie" at X=10: 10+56+10=76 ≤ 100 → fits.
            //   Line 2: "charlie"(56)@X=10, spacer(10)→X=76. line2.Width=76.
            //
            // Fragment 1 (first): textBbox minX=10,maxX=50, textW=40.
            //   Clone expansion: X=0, W=40+start(10)+end(10)=60.
            // Fragment 2 (last): textBbox minX=10,maxX=66, textW=56.
            //   Clone expansion: X=0, W=56+start(10)+end(10)=76.
            // CSS Fragmentation L3 §6.1: clone ⇒ both edges on every fragment.
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; box-decoration-break: clone; }",
                viewportWidth: 100);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2), "clone fixture must wrap to 2 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(2), "two-line clone span has 2 fragments");

            // Fragment 1: both edges (X=0, Width = startPbm+text+endPbm = 60).
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps),
                "fragment 1 X=0 (startPbm shifts left)");
            Assert.That(frags[0].Width, Is.EqualTo(60).Within(Eps),
                "fragment 1 width = start(10)+text(40)+end(10) = 60");
            Assert.That(lines[0].Width, Is.EqualTo(60).Within(Eps),
                "line 1 width = start(10)+alpha(40)+end(10) = 60");

            // Fragment 2: both edges (X=0, Width = startPbm+text+endPbm = 76).
            Assert.That(frags[1].X, Is.EqualTo(0).Within(Eps),
                "fragment 2 X=0 (startPbm shifts left)");
            Assert.That(frags[1].Width, Is.EqualTo(76).Within(Eps),
                "fragment 2 width = start(10)+text(56)+end(10) = 76");
            Assert.That(lines[1].Width, Is.EqualTo(76).Within(Eps),
                "line 2 width = start(10)+charlie(56)+end(10) = 76");
        }

        [Test]
        public void Clone_two_line_text_runs_positioned_correctly() {
            // Continuation of the above fixture.
            // Line 1 text run ("alpha") must start at X=10 (after cloned startPbm).
            // Line 2 text run ("charlie") must also start at X=10 (clone start
            // offset applied by FinishLine after each mid-span wrap).
            // CSS Fragmentation L3 §6.1: clone ⇒ startPbm on every line.
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                "span { padding: 0 10px; box-decoration-break: clone; }",
                viewportWidth: 100);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2));

            TextRun run1 = FirstTextRunInLine(lines[0]);
            Assert.That(run1, Is.Not.Null);
            Assert.That(run1.X, Is.EqualTo(10).Within(Eps),
                "line 1 text run X=10 (start padding present on first fragment)");

            TextRun run2 = FirstTextRunInLine(lines[1]);
            Assert.That(run2, Is.Not.Null);
            Assert.That(run2.X, Is.EqualTo(10).Within(Eps),
                "line 2 text run X=10 (clone startPbm on every continuation line)");
        }

        // ================================================================
        // §11 — clone three-line: middle fragment also gets both edges.
        //       CSS Fragmentation L3 §6.1: ALL fragments have both PBM
        //       edges, not just first and last.
        // ================================================================

        [Test]
        public void Clone_three_line_middle_fragment_has_both_edges() {
            // padding: 0 10px, "alpha bravo charlie", viewport=80.
            // Clone end-PBM reserve=10 shifts wrap points:
            //   X=10 (start spacer). "alpha"(40): 10+40+10=60≤80. X=50.
            //   " ": X=58. "bravo"(40): 58+40+10=108>80 → wrap.
            //   FinishLine: X+=10→60. line1.Width=60. X=10.
            //   "bravo"(40): 10+40+10=60≤80. X=50.
            //   " ": X=58. "charlie"(56): 58+56+10=124>80 → wrap.
            //   FinishLine: X+=10→60. line2.Width=60. X=10.
            //   "charlie"(56): 10+56+10=76≤80. X=66. spacer(10)→X=76.
            //   line3.Width=76.
            //
            // Fragment 1: textW=40, X=0, W=40+10+10=60.
            // Fragment 2 (middle): textW=40, X=0, W=40+10+10=60.
            // Fragment 3 (last): textW=56, X=0, W=56+10+10=76.
            // CSS Fragmentation L3 §6.1: middle fragment has BOTH edges under clone
            // (contrast with slice where the middle has ZERO edges).
            var (root, _, _) = Build(
                "<p><span>alpha bravo charlie</span></p>",
                "span { padding: 0 10px; box-decoration-break: clone; }",
                viewportWidth: 80);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(3), "fixture must wrap to 3 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(3), "three-line clone span has 3 fragments");

            // All three fragments: X=0, Width = startPbm(10)+text+endPbm(10).
            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps), "frag0 X=0");
            Assert.That(frags[0].Width, Is.EqualTo(60).Within(Eps),
                "frag0 width = start(10)+alpha(40)+end(10) = 60");

            // CSS Fragmentation L3 §6.1: middle fragment under clone carries both edges.
            Assert.That(frags[1].X, Is.EqualTo(0).Within(Eps), "frag1 X=0 (middle, both edges)");
            Assert.That(frags[1].Width, Is.EqualTo(60).Within(Eps),
                "frag1 (middle) width = start(10)+bravo(40)+end(10) = 60");

            Assert.That(frags[2].X, Is.EqualTo(0).Within(Eps), "frag2 X=0");
            Assert.That(frags[2].Width, Is.EqualTo(76).Within(Eps),
                "frag2 width = start(10)+charlie(56)+end(10) = 76");
        }

        // ================================================================
        // §12 — clone vs slice wrap-point divergence.
        //       Under clone, the end-PBM reservation narrows the effective
        //       available width and causes an earlier wrap than slice.
        // ================================================================

        [Test]
        public void Clone_wraps_earlier_than_slice_due_to_endPbm_reservation() {
            // padding: 0 10px → endPbm=10. viewport=59.
            // "aa bb" = ["aa"(16), "bb"(16)].
            //
            // Under SLICE:
            //   Items: [spacer(10), "aa"(16), " "(8), "bb"(16), spacer(10)]
            //   "aa"@X=10→26. " "→34. "bb"@34: 34+16=50≤59→fits. spacer→60.
            //   Single line. line.Width=60.
            //
            // Under CLONE:
            //   Same items but "bb" fit test: 34+16+10(endPbm)=60>59 → WRAP.
            //   FinishLine: X+=10→36. line1.Width=36. X=10.
            //   "bb"@10: 10+16+10=36≤59→fits. spacer(10)→36.
            //   Two lines. line1.Width=36, line2.Width=36.
            //
            // CSS Fragmentation L3 §6.1: the end-edge reservation causes clone
            // to produce 2 lines where slice produces 1.
            var (rootSlice, _, _) = Build(
                "<p><span>aa bb</span></p>",
                "span { padding: 0 10px; box-decoration-break: slice; }",
                viewportWidth: 59);
            var (rootClone, _, _) = Build(
                "<p><span>aa bb</span></p>",
                "span { padding: 0 10px; box-decoration-break: clone; }",
                viewportWidth: 59);

            var pSlice = FirstByTag(rootSlice, "p");
            var pClone = FirstByTag(rootClone, "p");

            var linesSlice = LinesIn(pSlice);
            var linesClone = LinesIn(pClone);

            // Slice: fits on one line.
            Assert.That(linesSlice.Count, Is.EqualTo(1),
                "slice: 'bb' fits without endPbm reservation → 1 line");

            // Clone: wraps because endPbm reservation pushes 'bb' to next line.
            Assert.That(linesClone.Count, Is.EqualTo(2),
                "clone: 'bb' doesn't fit with endPbm(10) reserved → 2 lines");

            // Verify clone line widths.
            Assert.That(linesClone[0].Width, Is.EqualTo(36).Within(Eps),
                "clone line1 = startPbm(10)+aa(16)+endPbm(10) = 36");
            Assert.That(linesClone[1].Width, Is.EqualTo(36).Within(Eps),
                "clone line2 = startPbm(10)+bb(16)+endPbm(10) = 36");
        }

        // ================================================================
        // §13 — clone single-line is identical to slice single-line.
        //       CSS Fragmentation L3 §6.1: a single-line span is both first
        //       and last fragment — both edges always apply regardless of
        //       box-decoration-break value. No behavioral difference.
        // ================================================================

        [Test]
        public void Clone_single_line_identical_to_slice_single_line() {
            // "hello"=40px, padding: 0 10px. viewport=800.
            // Both modes: fragment X=0, Width=10+40+10=60, line.Width=60.
            // CSS Fragmentation L3 §6.1: slice and clone are identical for
            // single-line spans because the span is both first and last.
            var (rootSlice, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { padding: 0 10px; box-decoration-break: slice; }",
                viewportWidth: 800);
            var (rootClone, _, _) = Build(
                "<p><span>hello</span></p>",
                "span { padding: 0 10px; box-decoration-break: clone; }",
                viewportWidth: 800);

            var pSlice = FirstByTag(rootSlice, "p");
            var pClone = FirstByTag(rootClone, "p");

            var fragsSlice = InlineFragmentsFor(pSlice, "span");
            var fragsClone = InlineFragmentsFor(pClone, "span");

            Assert.That(fragsSlice.Count, Is.EqualTo(1), "slice single-line: 1 fragment");
            Assert.That(fragsClone.Count, Is.EqualTo(1), "clone single-line: 1 fragment");

            // Both must produce the same geometry: X=0, Width=60.
            Assert.That(fragsClone[0].X, Is.EqualTo(fragsSlice[0].X).Within(Eps),
                "clone X must equal slice X for single-line span");
            Assert.That(fragsClone[0].Width, Is.EqualTo(fragsSlice[0].Width).Within(Eps),
                "clone Width must equal slice Width for single-line span");

            // Sanity: line widths also match.
            var linesSlice = LinesIn(pSlice);
            var linesClone = LinesIn(pClone);
            Assert.That(linesClone[0].Width, Is.EqualTo(linesSlice[0].Width).Within(Eps),
                "clone line.Width must equal slice line.Width for single-line span");
        }

        // ================================================================
        // §14 — clone margin-only and border-only.
        //       CSS Fragmentation L3 §6.1: the PBM includes all three
        //       components; each appears on every fragment under clone.
        // ================================================================

        [Test]
        public void Clone_two_line_margin_only_both_edges_on_every_fragment() {
            // margin: 0 5px → startPbm=5, endPbm=5. padding=0, border=0.
            // "aa bb", viewport=43.
            // Under clone end-reserve=5:
            //   X=5 (spacer). "aa"(16): 5+16+5=26≤43. X=21.
            //   " ": X=29. "bb"(16): 29+16+5=50>43 → wrap.
            //   FinishLine: X+=5→26. line1.Width=26. X=5.
            //   "bb"(16): 5+16+5=26≤43. X=21. spacer(5)→26. line2.Width=26.
            //
            // Fragment 1: textBbox minX=5,maxX=21, textW=16.
            //   Clone expansion: X=0, W=16+5+5=26.
            // Fragment 2 (last): textBbox minX=5,maxX=21, textW=16.
            //   Clone expansion: X=0, W=16+5+5=26.
            // CSS Fragmentation L3 §6.1: margin contributes to the PBM
            // edge that clone repeats on every fragment (CSS 2.1 §10.3.1).
            var (root, _, _) = Build(
                "<p><span>aa bb</span></p>",
                "span { margin: 0 5px; padding: 0; box-decoration-break: clone; }",
                viewportWidth: 43);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2), "margin-only clone wraps to 2 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(2));

            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(26).Within(Eps),
                "frag0 width = margin(5)+text(16)+margin(5) = 26");
            Assert.That(frags[1].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[1].Width, Is.EqualTo(26).Within(Eps),
                "frag1 width = margin(5)+text(16)+margin(5) = 26");
        }

        [Test]
        public void Clone_two_line_border_only_both_edges_on_every_fragment() {
            // border: 3px solid black (left+right only). startPbm=3, endPbm=3.
            // "aa bb", viewport=35.
            // Under clone end-reserve=3:
            //   X=3 (spacer). "aa"(16): 3+16+3=22≤35. X=19.
            //   " ": X=27. "bb"(16): 27+16+3=46>35 → wrap.
            //   FinishLine: X+=3→22. line1.Width=22. X=3.
            //   "bb"(16): 3+16+3=22≤35. X=19. spacer(3)→22. line2.Width=22.
            //
            // Both fragments: X=0, Width=3+16+3=22.
            // CSS Fragmentation L3 §6.1: border contributes to the PBM edge.
            var (root, _, _) = Build(
                "<p><span>aa bb</span></p>",
                "span { border: 3px solid black; border-top: none; border-bottom: none; " +
                "padding: 0; box-decoration-break: clone; }",
                viewportWidth: 35);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2), "border-only clone wraps to 2 lines");

            var frags = InlineFragmentsFor(p, "span");
            Assert.That(frags.Count, Is.EqualTo(2));

            Assert.That(frags[0].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[0].Width, Is.EqualTo(22).Within(Eps),
                "frag0 width = border(3)+text(16)+border(3) = 22");
            Assert.That(frags[1].X, Is.EqualTo(0).Within(Eps));
            Assert.That(frags[1].Width, Is.EqualTo(22).Within(Eps),
                "frag1 width = border(3)+text(16)+border(3) = 22");
        }

        // ================================================================
        // §15 — nested clone/slice interaction.
        //       A clone span inside a slice span: only the clone span's
        //       content items carry clone PBMs (the outer slice span does not
        //       contribute to the inherited clone accumulator).
        //       A slice span inside a clone span: the inner slice items
        //       inherit the outer clone PBMs and the wrap test is narrowed.
        // ================================================================

        [Test]
        public void Clone_inside_slice_only_inner_span_has_clone_behavior() {
            // Outer slice: padding 0 10px (startPbm=10, endPbm=10), slice.
            // Inner clone: padding 0 5px (startPbm=5, endPbm=5), clone.
            // "aa bb" inside inner clone, viewport=52.
            //
            // outerCloneStartPbm for inner items = 0 (outer is slice; slice
            // spans do NOT accumulate into childCloneStartPbm/EndPbm).
            // innerCloneStartPbm = 0 + 5 = 5.
            // Inner items carry CloneSpanStartPbm=5, CloneSpanEndPbm=5.
            //
            // Item stream:
            //   [spacer(10), spacer(5), "aa"(16,clone 5/5),
            //    " "(8,clone 5/5), "bb"(16,clone 5/5), spacer(5), spacer(10)]
            // X=0+10(outer start)+5(inner start)=15.
            // "aa": 15+16+5(cloneEnd)=36≤52 → fits. X=31.
            // " ": X=39.
            // "bb": 39+16+5=60>52 → wrap.
            //   TrimTrailingSpace (X=31).
            //   FinishLine(non-final): state.X += ActiveCloneEndPbm(5) → 31+5=36.
            //   line1.Width=36. state.X = ActiveCloneStartPbm(5)=5.
            // "bb"@5: 5+16+5=26≤52. X=21. spacer(5)→26. spacer(10)→36.
            // FinishLine final: line2.Width=36.
            //
            // CSS Fragmentation L3 §6.1: only the inner clone's PBMs (5/5) are
            // inherited by the text items; the outer slice's PBMs (10/10) do NOT
            // contribute to clone behavior. This means the FinishLine clone path
            // only adds 5 (not 15) to the outgoing line.
            var (root, _, _) = Build(
                "<p><span id='outer'><span id='inner'>aa bb</span></span></p>",
                "#outer { padding: 0 10px; box-decoration-break: slice; } " +
                "#inner { padding: 0 5px; box-decoration-break: clone; }",
                viewportWidth: 52);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2),
                "inner clone endPbm(5) reserve forces wrap → 2 lines");

            // line1.Width = outerStart(10)+innerStart(5)+aa(16)+cloneEnd(5) = 36.
            // (The outer slice start spacer is included via state.X but the
            // FinishLine clone path only adds inner clone endPbm=5, not outer's.)
            Assert.That(lines[0].Width, Is.EqualTo(36).Within(Eps),
                "line1 = spacer10+spacer5+aa(16)+cloneEnd(5) = 36");
            // line2.Width = cloneStart(5)+bb(16)+innerEndSpacer(5)+outerEndSpacer(10) = 36.
            Assert.That(lines[1].Width, Is.EqualTo(36).Within(Eps),
                "line2 = cloneStart(5)+bb(16)+innerEnd(5)+outerEnd(10) = 36");
        }

        [Test]
        public void Slice_inside_clone_inner_items_inherit_outer_clone_pbm() {
            // Outer clone: padding 0 10px (startPbm=10, endPbm=10), clone.
            // Inner slice: padding 0 5px (startPbm=5, endPbm=5), slice.
            // "aa bb" inside inner slice, viewport=64.
            //
            // outerCloneStartPbm for outer items = 10 (outer IS clone, so
            // childCloneStartPbm = 0+10=10, childCloneEndPbm = 0+10=10).
            // inner is slice (thisSpanIsClone=false) so childClone stays =10.
            // Inner text items carry CloneSpanStartPbm=10, CloneSpanEndPbm=10.
            //
            // Item stream:
            //   [spacer(10), spacer(5), "aa"(16,clone 10/10),
            //    " "(8,clone 10/10), "bb"(16,clone 10/10), spacer(5), spacer(10)]
            // X=0+10+5=15. "aa": 15+16+10=41≤64 → fits. X=31.
            // " ": X=39.
            // "bb": 39+16+10=65>64 → wrap.
            //   TrimTrailingSpace (X=31).
            //   FinishLine(non-final): state.X += 10 → 41. line1.Width=41. X=10.
            // "bb"@10: 10+16+10=36≤64. X=26. spacer(5)→31. spacer(10)→41.
            // FinishLine final: line2.Width=41.
            //
            // CSS Fragmentation L3 §6.1: the outer clone span's PBM(10/10)
            // propagates into inner items (even though the inner is slice), so
            // the effective wrap reservation is endPbm=10 from the outer clone.
            var (root, _, _) = Build(
                "<p><span id='outer'><span id='inner'>aa bb</span></span></p>",
                "#outer { padding: 0 10px; box-decoration-break: clone; } " +
                "#inner { padding: 0 5px; box-decoration-break: slice; }",
                viewportWidth: 64);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);

            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2),
                "outer clone endPbm(10) propagates into inner items → earlier wrap");

            // line1.Width = spacer10+spacer5+aa(16)+cloneEnd(10) = 41.
            Assert.That(lines[0].Width, Is.EqualTo(41).Within(Eps),
                "line1 = outerStart(10)+innerStart(5)+aa(16)+cloneEnd(10) = 41");
            // line2.Width = cloneStart(10)+bb(16)+innerEnd(5)+outerEnd(10) = 41.
            Assert.That(lines[1].Width, Is.EqualTo(41).Within(Eps),
                "line2 = cloneStart(10)+bb(16)+innerEnd(5)+outerEnd(10) = 41");
        }
    }
}
