using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout {
    // TEXTALIGN-ABS: text-align:center/right/justify must apply to text even when
    // the block also contains an absolutely- or fixed-positioned child.
    //
    // Root cause (pre-fix): BoxFinalize.IsBlockLevel treated out-of-flow boxes as
    // block-level, causing a block with inline text + abs child to be classified as
    // "mixed block+inline". The inline text was swept into an AnonymousBlockBox.
    // InlineLayout.TryLayoutSingleRunFast read container.Style directly (null for the
    // anon box) and resolved text-align as "left". CSS 2.1 §9.2.1.1 requires out-of-flow
    // boxes to NOT trigger anonymous block generation.
    //
    // Fix: BoxFinalize.IsBlockLevel now returns false for position:absolute|fixed.
    // InlineLayout.TryLayoutSingleRunFast uses container.Style ?? container.Parent?.Style
    // (same fallback as ApplyTextAlign). InlineLayout.CollectInlineInner skips OOF boxes
    // and re-attaches them after the line rebuild so PositioningPass can place them.
    // BlockLayout's post-LayoutInline cursor walk skips OOF children so they do not
    // inflate the container height.
    //
    // MonoFontMetrics defaults: charWidthEm = 0.5 → 8 px/char @ 16 px font-size.
    // "HELLO" = 5 chars × 8 px = 40 px.
    public class TextAlignWithOutOfFlowTests {

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static TextRun FindFirstTextRun(Box root) {
            foreach (var b in AllBoxes(root))
                if (b is TextRun tr) return tr;
            return null;
        }

        static TextRun FindTextRunWithText(Box root, string text) {
            foreach (var b in AllBoxes(root))
                if (b is TextRun tr && tr.Text == text) return tr;
            return null;
        }

        static List<TextRun> AllTextRuns(Box root) {
            var list = new List<TextRun>();
            foreach (var b in AllBoxes(root))
                if (b is TextRun tr) list.Add(tr);
            return list;
        }

        // Accumulate X from the box up to the root to get an absolute X coordinate.
        static double AbsoluteX(Box box) {
            double x = 0;
            for (var b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        // -----------------------------------------------------------------------
        // Test 1 — center-align: text is centered even with an abs child present
        //
        // Container: 400 px wide, text-align:center.
        // "HELLO" = 5 × 8 = 40 px. Center offset = (400 - 40)/2 = 180.
        // The abs child (no dims) must not prevent the centering.
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_applies_when_block_has_abs_child() {
            // white-space:nowrap forces the single-line slow path so we don't hit
            // the fast path with a different container.Style read.
            const string html = "<div id='outer'><span id='txt'>HELLO</span><i id='abs'></i></div>";
            const string css = @"
                #outer { width: 400px; font-size: 16px; text-align: center; white-space: nowrap; }
                #abs   { position: absolute; top: 0; left: 0; width: 10px; height: 10px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            // "HELLO" = 5 chars × 8 px = 40 px; center in 400 = offset 180.
            const double glyphWidth = 40;
            const double containerWidth = 400;
            const double expectedRunX = (containerWidth - glyphWidth) * 0.5; // 180

            var run = FindTextRunWithText(root, "HELLO");
            Assert.That(run, Is.Not.Null, "TextRun 'HELLO' must exist");
            // The run's X is in LineBox-local coords; LineBox.X is set by BlockLayout.
            // For a single line, LineBox.X = container padding-left (0 here).
            // run.X is the centering shift.
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                $"text-align:center with abs child: run.X should be {expectedRunX}, got {run.X}");
            Assert.That(run.X, Is.GreaterThan(1.0),
                "text-align:center: run must be shifted right, not pinned at left");
        }

        // -----------------------------------------------------------------------
        // Test 2 — right-align: text is right-aligned even with an abs child
        //
        // Container: 400 px, text-align:right.
        // "HELLO" = 40 px. Right offset = 400 - 40 = 360.
        // -----------------------------------------------------------------------
        [Test]
        public void Right_align_applies_when_block_has_abs_child() {
            const string html = "<div id='outer'><span id='txt'>HELLO</span><i id='abs'></i></div>";
            const string css = @"
                #outer { width: 400px; font-size: 16px; text-align: right; white-space: nowrap; }
                #abs   { position: absolute; top: 0; left: 0; width: 10px; height: 10px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            const double glyphWidth = 40;
            const double containerWidth = 400;
            const double expectedRunX = containerWidth - glyphWidth; // 360

            var run = FindTextRunWithText(root, "HELLO");
            Assert.That(run, Is.Not.Null, "TextRun 'HELLO' must exist");
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                $"text-align:right with abs child: run.X should be {expectedRunX}, got {run.X}");
            Assert.That(run.X, Is.GreaterThan(1.0),
                "text-align:right: run must be shifted right, not pinned at left");
        }

        // -----------------------------------------------------------------------
        // Test 3 — abs child is STILL placed correctly (its resolved position)
        //
        // The abs child has top:10px left:20px width:50px height:30px.
        // Container is position:relative. After fix, the abs box must still be
        // positioned by PositioningPass at (20, 10) relative to the container.
        // -----------------------------------------------------------------------
        [Test]
        public void Abs_child_is_still_placed_correctly_after_textalign_fix() {
            const string html = "<div id='outer'><span>HELLO</span><i id='abs'></i></div>";
            const string css = @"
                #outer { position: relative; width: 400px; font-size: 16px; text-align: center; }
                #abs   { position: absolute; top: 10px; left: 20px; width: 50px; height: 30px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var abs = FirstById(root, "abs");
            Assert.That(abs, Is.Not.Null, "abs box must exist in the tree");
            Assert.That(abs.Width, Is.EqualTo(50).Within(0.5),
                "abs child width must be 50px as specified");
            Assert.That(abs.Height, Is.EqualTo(30).Within(0.5),
                "abs child height must be 30px as specified");

            var outer = FirstById(root, "outer");
            var (outerAbsX, outerAbsY) = AbsoluteOriginOf(outer);
            var (absX, absY) = AbsoluteOriginOf(abs);
            // left:20px, top:10px relative to the positioned container.
            Assert.That(absX - outerAbsX, Is.EqualTo(20).Within(0.5),
                "abs child must be at left:20px from its containing block");
            Assert.That(absY - outerAbsY, Is.EqualTo(10).Within(0.5),
                "abs child must be at top:10px from its containing block");
        }

        // -----------------------------------------------------------------------
        // Test 4 — fixed-position variant: same centering effect
        //
        // A fixed-position child must also not disrupt text-align.
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_applies_when_block_has_fixed_child() {
            const string html = "<div id='outer'><span>HI</span><i id='fxd'></i></div>";
            const string css = @"
                #outer { width: 400px; font-size: 16px; text-align: center; white-space: nowrap; }
                #fxd   { position: fixed; top: 0; left: 0; width: 10px; height: 10px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            // "HI" = 2 × 8 = 16 px; center in 400 = offset 192.
            const double glyphWidth = 16;
            const double containerWidth = 400;
            const double expectedRunX = (containerWidth - glyphWidth) * 0.5; // 192

            var run = FindTextRunWithText(root, "HI");
            Assert.That(run, Is.Not.Null, "TextRun 'HI' must exist");
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                $"text-align:center with fixed child: run.X should be {expectedRunX}, got {run.X}");
            Assert.That(run.X, Is.GreaterThan(1.0),
                "text-align:center with fixed child: run must be shifted right");
        }

        // -----------------------------------------------------------------------
        // Test 5 — multiple abs children: text still centered, all abs children
        //          remain in the tree so PositioningPass can place them
        //
        // Container: 400 px, text-align:center.
        // "AB" = 2 × 8 = 16 px. Center offset = (400 - 16)/2 = 192.
        // Two abs children with distinct insets must both be placed correctly.
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_applies_with_multiple_abs_children_all_placed() {
            const string html = "<div id='outer'>AB<i id='a1'></i><i id='a2'></i></div>";
            const string css = @"
                #outer { position: relative; width: 400px; font-size: 16px; text-align: center; white-space: nowrap; }
                #a1    { position: absolute; top:  5px; left: 10px; width: 20px; height: 20px; }
                #a2    { position: absolute; top: 15px; left: 50px; width: 30px; height: 10px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            // Text centering.
            const double glyphWidth = 16; // "AB" = 2 chars × 8 px
            const double containerWidth = 400;
            const double expectedRunX = (containerWidth - glyphWidth) * 0.5; // 192
            var run = FindTextRunWithText(root, "AB");
            Assert.That(run, Is.Not.Null, "TextRun 'AB' must exist");
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                $"text must be centered at {expectedRunX}; got {run.X}");

            // Both abs children must be in the tree and correctly placed.
            var outer = FirstById(root, "outer");
            var a1 = FirstById(root, "a1");
            var a2 = FirstById(root, "a2");
            Assert.That(a1, Is.Not.Null, "#a1 must exist in tree");
            Assert.That(a2, Is.Not.Null, "#a2 must exist in tree");

            var (outerAbsX, outerAbsY) = AbsoluteOriginOf(outer);
            var (a1X, a1Y) = AbsoluteOriginOf(a1);
            var (a2X, a2Y) = AbsoluteOriginOf(a2);
            Assert.That(a1X - outerAbsX, Is.EqualTo(10).Within(0.5), "#a1 left:10px");
            Assert.That(a1Y - outerAbsY, Is.EqualTo(5).Within(0.5),  "#a1 top:5px");
            Assert.That(a2X - outerAbsX, Is.EqualTo(50).Within(0.5), "#a2 left:50px");
            Assert.That(a2Y - outerAbsY, Is.EqualTo(15).Within(0.5), "#a2 top:15px");
        }

        // -----------------------------------------------------------------------
        // Test 6 — abs child between two text runs: each run is centered
        //
        // "HI" + abs + "BYE" in a 400px center-aligned block.
        // Both text runs should be offset to center in their respective lines.
        // "HI"  = 2 × 8 = 16 px → center offset 192.
        // "BYE" = 3 × 8 = 24 px → center offset 188.
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_applies_to_text_before_and_after_abs_child() {
            const string html = "<div id='outer'><span>HI</span><i id='abs'></i><span>BYE</span></div>";
            const string css = @"
                #outer { position: relative; width: 400px; font-size: 16px; text-align: center; white-space: nowrap; }
                #abs   { position: absolute; top: 0; left: 0; width: 5px; height: 5px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            const double containerWidth = 400;
            var hiRun  = FindTextRunWithText(root, "HI");
            var byeRun = FindTextRunWithText(root, "BYE");
            Assert.That(hiRun,  Is.Not.Null, "TextRun 'HI' must exist");
            Assert.That(byeRun, Is.Not.Null, "TextRun 'BYE' must exist");

            double expectedHiX  = (containerWidth - 16) * 0.5; // 192
            double expectedByeX = (containerWidth - 24) * 0.5; // 188

            Assert.That(hiRun.X, Is.EqualTo(expectedHiX).Within(0.5),
                $"'HI' run must be centered; expected {expectedHiX}, got {hiRun.X}");
            Assert.That(byeRun.X, Is.EqualTo(expectedByeX).Within(0.5),
                $"'BYE' run must be centered; expected {expectedByeX}, got {byeRun.X}");

            // Both must be > 0 (not left-pinned).
            Assert.That(hiRun.X,  Is.GreaterThan(1.0), "'HI' must not be pinned left");
            Assert.That(byeRun.X, Is.GreaterThan(1.0), "'BYE' must not be pinned left");
        }

        // -----------------------------------------------------------------------
        // Test 7 — canonical real-world case: centered h1 + abs decorative underline
        //
        // `<h1 style="text-align:center">LOAD GAME<i style="position:absolute"></i></h1>`
        // The heading text must be centered in its block.
        // -----------------------------------------------------------------------
        [Test]
        public void Canonical_centered_heading_with_abs_underline() {
            const string html = "<div id='wrap'><h1 id='h'>LOAD GAME<i id='deco'></i></h1></div>";
            const string css = @"
                #wrap { width: 400px; font-size: 16px; }
                #h    { text-align: center; white-space: nowrap; margin: 0; padding: 0; }
                #deco { position: absolute; bottom: 0; left: 10%; right: 10%; height: 2px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            // "LOAD GAME" = 9 chars × 8 px = 72 px (UA h1 bumps font-size to 2em=32px,
            // chars become 16px → 9×16=144). At 32px: 9 chars × 16 px = 144 px.
            // container width = 400. center offset = (400-144)/2 = 128.
            // We only check the run is meaningfully shifted (> 50 px) not pinned left.
            var run = FindTextRunWithText(root, "LOAD GAME");
            Assert.That(run, Is.Not.Null, "TextRun 'LOAD GAME' must exist");
            Assert.That(run.X, Is.GreaterThan(50.0),
                $"heading text must be visibly centered (run.X > 50); got {run.X}");

            // Decorative abs child must be in the tree.
            var deco = FirstById(root, "deco");
            Assert.That(deco, Is.Not.Null, "abs decorative underline must be in tree");
        }

        // -----------------------------------------------------------------------
        // Test 8 — regression guard: pure-inline block (no abs child) still works
        //
        // A block with ONLY inline text must still center correctly (no regression
        // on the non-mixed case that was already working).
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_still_works_for_pure_inline_block_no_abs_child() {
            const string html = "<div id='outer'>HELLO</div>";
            const string css  = @"#outer { width: 400px; font-size: 16px; text-align: center; white-space: nowrap; }";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            const double glyphWidth = 40; // "HELLO" 5 × 8
            const double containerWidth = 400;
            const double expectedRunX = (containerWidth - glyphWidth) * 0.5; // 180

            var run = FindFirstTextRun(root);
            Assert.That(run, Is.Not.Null);
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                "pure-inline block text-align:center must still work (regression guard)");
        }

        // -----------------------------------------------------------------------
        // Test 9 — regression guard: pure-block container (only block children, no
        //          abs mixing) still works — ContainsInlines stays false
        //
        // A block containing ONLY a normal in-flow block child must not be
        // misclassified as inline after the fix.
        // -----------------------------------------------------------------------
        [Test]
        public void Pure_block_container_is_not_affected_by_oof_fix() {
            const string html = "<div id='outer'><div id='inner'>HI</div></div>";
            const string css  = @"
                #outer { width: 400px; }
                #inner { width: 200px; height: 40px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var outer = FirstById(root, "outer");
            var inner = FirstById(root, "inner");
            Assert.That(inner, Is.Not.Null, "inner block must exist");
            Assert.That(inner.Width, Is.EqualTo(200).Within(0.5), "inner must keep its width");
            Assert.That(inner.Height, Is.EqualTo(40).Within(0.5), "inner must keep its height");
            // outer must have ContainsInlines = false (block-flow context)
            Assert.That(outer.ContainsInlines, Is.False,
                "a block with only block children must remain ContainsInlines=false");
        }

        // -----------------------------------------------------------------------
        // Test 10 — abs-only child (no text siblings): container height is NOT
        //           inflated by the abs child's height (OOF must not contribute
        //           to in-flow height per CSS 2.1 §9.4.2)
        //
        // Container: 200 px wide, no padding/border, abs child 80px tall.
        // The container's height must be just one line-box height (font-based),
        // not 80+ px (which would indicate the abs height was counted in-flow).
        // -----------------------------------------------------------------------
        [Test]
        public void Abs_only_child_does_not_inflate_container_height() {
            const string html = "<div id='outer'><i id='abs'></i></div>";
            const string css  = @"
                #outer { width: 200px; font-size: 16px; }
                #abs   { position: absolute; top: 0; left: 0; width: 50px; height: 80px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            var outer = FirstById(root, "outer");
            // With no text content, the container should have height ≈ one empty line box
            // (≈ 16px × 1.2 = 19.2 from MonoFontMetrics default line-height).
            // It must NOT be 80+ px (which would mean the abs child inflated it).
            Assert.That(outer.Height, Is.LessThanOrEqualTo(30),
                $"container with only abs child must not be inflated by abs height; got {outer.Height}");
        }

        // -----------------------------------------------------------------------
        // Test 11 — abs child with inset:0 inside a positioned container
        //           (regression guard for the most common abs-fill pattern)
        //
        // `.container { position: relative; width: 300px; height: 200px; text-align: center; }
        //  .overlay    { position: absolute; inset: 0; }`
        // Overlay fills the CB; centered text must work.
        // -----------------------------------------------------------------------
        [Test]
        public void Abs_fill_inset_zero_plus_center_text() {
            const string html = "<div id='cb'><span>GO</span><div id='overlay'></div></div>";
            const string css  = @"
                #cb      { position: relative; width: 300px; height: 200px; font-size: 16px; text-align: center; white-space: nowrap; }
                #overlay { position: absolute; top: 0; right: 0; bottom: 0; left: 0; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            // "GO" = 2 × 8 = 16 px; center in 300 → offset 142.
            var run = FindTextRunWithText(root, "GO");
            Assert.That(run, Is.Not.Null, "TextRun 'GO' must exist");
            const double expectedRunX = (300 - 16) * 0.5; // 142
            Assert.That(run.X, Is.EqualTo(expectedRunX).Within(0.5),
                $"text-align:center with abs inset:0 overlay: run.X should be {expectedRunX}");

            // The overlay must be placed at the containing block (fill).
            var overlay = FirstById(root, "overlay");
            Assert.That(overlay, Is.Not.Null, "overlay must be in the tree");
            Assert.That(overlay.Width, Is.EqualTo(300).Within(0.5), "overlay must fill width");
            Assert.That(overlay.Height, Is.EqualTo(200).Within(0.5), "overlay must fill height");
        }
    }
}
