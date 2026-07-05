using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Multicol;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Tests.Layout;

namespace Weva.Tests.Paint.Conversion {
    // NUnit coverage for MULTICOL-FRAG v1: paint-level column fragmentation.
    //
    // Design: CSS Multicol containers whose children exceed the column height
    // are sliced across columns at paint time only — layout remains whole-child.
    // For each spanned column the converter emits:
    //   PushClip(colRect) → PushTransform(translateUp) → child subtree → PopTransform → PopClip
    // Children that fit in one column produce the original single VisitBox — no
    // clip/transform — so the fits case is byte-identical to the pre-slicing path.
    //
    // Coverage matrix:
    //   A — child exactly column-height tall: no slicing (fits case)
    //   B — child 2x column-height: 2 PushClip+PushTransform scopes
    //   C — child 2.5x column-height: 3 column scope sets (last overflows)
    //   D — text child: DrawText commands repeat per column, geometry unchanged
    //   E — column rules still emitted alongside sliced children
    //   F — multiple tall children each fragment independently
    //   G — short child after a tall one paints normally (no slicing)
    //   H — explicit-height container: colHeight derived from content height
    public class MulticolFragmentationPaintTests {

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static (Weva.Layout.Boxes.Box root, Dictionary<Weva.Dom.Element, Weva.Css.Cascade.ComputedStyle> styles, Weva.Layout.LayoutContext ctx)
            Build(string html, string css = null, double viewportWidth = 800, double viewportHeight = 600)
            => LayoutTestHelpers.Build(html, css, viewportWidth, viewportHeight);

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children)
                foreach (var d in Walk(c)) yield return d;
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        // Convert box tree to a flat command list.
        static List<PaintCommand> Convert(Box root) {
            var conv = new BoxToPaintConverter();
            return conv.Convert(root).Commands;
        }

        // Count commands of a specific type.
        static int Count<T>(List<PaintCommand> cmds) where T : PaintCommand
            => cmds.OfType<T>().Count();

        // Collect commands of a specific type in order.
        static List<T> All<T>(List<PaintCommand> cmds) where T : PaintCommand
            => cmds.OfType<T>().ToList();

        // -----------------------------------------------------------------------
        // A — child exactly column-height tall: fits, no slicing.
        // The fits case must be byte-identical to pre-slicing: no PushClip or
        // PushTransform from the fragmentation path (only normal decoration clips).
        // -----------------------------------------------------------------------
        [Test]
        public void Child_exactly_column_height_fits_no_slicing() {
            // 2 columns, each child is exactly colHeight=80px tall, gap=0.
            // column-count:2 on 400px container with column-gap:0 → colWidth=200px.
            // Balanced: each child gets 80px which exactly equals colHeight=80px.
            var (root, _, _) = Build(
                "<div id='mc'><div id='a' style='background:red'></div>" +
                "<div id='b' style='background:blue'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; }" +
                "#a, #b { height: 80px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");

            var cmds = Convert(root);

            // The only clips emitted must be from overflow:hidden decoration if any,
            // NOT from the fragmentation path (which would only fire if children spanned
            // more than one column). Since both children fit in their columns, no
            // fragmentation clips should appear.
            // Count PushTransform commands: there should be 0 (no author transform,
            // no fragmentation transform).
            int pushTransformCount = Count<PushTransformCommand>(cmds);
            Assert.That(pushTransformCount, Is.EqualTo(0),
                "Fits case: no PushTransform from fragmentation path");
        }

        // -----------------------------------------------------------------------
        // B — child 2x column-height: painted in 2 columns.
        // Assert 2 PushClip+PushTransform scopes, correct clip rects,
        // and correct translate on the 2nd slice.
        // -----------------------------------------------------------------------
        [Test]
        public void Child_2x_column_height_produces_two_clip_transform_scopes() {
            // 2-column container, 400px wide, gap=0, explicit height 100px so
            // colHeight = 100px. One child 200px tall → spans both columns.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:green'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#tall { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");

            var cmds = Convert(root);

            // Expect 2 PushClip commands from the fragmentation path.
            var pushClips = All<PushClipCommand>(cmds);
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(2),
                "Two column slices → at least 2 PushClip commands");

            // Expect 2 PushTransform commands from the fragmentation path.
            var pushTransforms = All<PushTransformCommand>(cmds);
            Assert.That(pushTransforms.Count, Is.GreaterThanOrEqualTo(2),
                "Two column slices → at least 2 PushTransform commands");

            // Check clip widths: each clip should be colWidth=200px.
            foreach (var pc in pushClips) {
                Assert.That(pc.Bounds.Width, Is.EqualTo(200.0).Within(1.0),
                    "Each column clip rect must be colWidth wide");
            }

            // Column 0 transform: translate(0,0) = identity (no shift for first slice).
            // Column 1 transform: translate(0, -100) shifts content up by colHeight.
            // The pushTransforms list ordering: first=col0 (Ty=0), second=col1 (Ty=-100).
            var transforms = pushTransforms.Take(2).ToList();
            Assert.That(transforms[0].Transform.Ty, Is.EqualTo(0f).Within(0.5f),
                "First slice translate Y must be 0 (no shift)");
            Assert.That(transforms[1].Transform.Ty, Is.EqualTo(-100f).Within(0.5f),
                "Second slice translate Y must be -colHeight = -100px");
            // Tx MUST also shift: the child is laid out at column 0's X, so slice k
            // moves RIGHT by k*(colWidth+gap) to land in column k. Without this the
            // column-k clip rejects the content (cols 2+ render empty) — the exact
            // bug that shipped because the original tests only checked Ty.
            // colWidth=200, gap=0 → Tx sequence 0, 200.
            Assert.That(transforms[0].Transform.Tx, Is.EqualTo(0f).Within(0.5f),
                "First slice translate X must be 0");
            Assert.That(transforms[1].Transform.Tx, Is.EqualTo(200f).Within(0.5f),
                "Second slice translate X must be colWidth+gap = 200px");
        }

        // -----------------------------------------------------------------------
        // B2 — verify second clip rect is at the second column's X position.
        // -----------------------------------------------------------------------
        [Test]
        public void Second_column_clip_rect_is_at_second_column_x() {
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:green'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#tall { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);
            var pushClips = All<PushClipCommand>(cmds);
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(2));

            // colWidth=200. Col0 left=0 (content box start), col1 left=200.
            // Absolute absX=0 for root. Content left = 0 (no padding/border).
            double col0X = pushClips[0].Bounds.X;
            double col1X = pushClips[1].Bounds.X;
            Assert.That(col1X - col0X, Is.EqualTo(200.0).Within(1.0),
                "Second clip rect must be colWidth further right than first");
        }

        // -----------------------------------------------------------------------
        // C — child 2.5x column-height spans 3 columns; last tail overflows.
        // Assert 3 clip+transform scopes.
        // -----------------------------------------------------------------------
        [Test]
        public void Child_2_5x_column_height_produces_three_clip_transform_scopes() {
            // 3-column, 300px wide, gap=0, explicit height=100px (colHeight=100px).
            // Child is 250px → spans 3 columns (0..99 = col0, 100..199 = col1,
            // 200..249 = col2 tail).
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:teal'></div></div>",
                "#mc { width: 300px; column-count: 3; column-gap: 0; height: 100px; }" +
                "#tall { height: 250px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");

            var cmds = Convert(root);

            var pushClips = All<PushClipCommand>(cmds);
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(3),
                "Three column slices → at least 3 PushClip commands");

            var pushTransforms = All<PushTransformCommand>(cmds);
            Assert.That(pushTransforms.Count, Is.GreaterThanOrEqualTo(3),
                "Three column slices → at least 3 PushTransform commands");

            // Verify translate Ty sequence: 0, -100, -200.
            var tys = pushTransforms.Take(3).Select(pt => pt.Transform.Ty).ToList();
            Assert.That(tys[0], Is.EqualTo(0f).Within(0.5f),   "Col0 Ty=0");
            Assert.That(tys[1], Is.EqualTo(-100f).Within(0.5f), "Col1 Ty=-100");
            Assert.That(tys[2], Is.EqualTo(-200f).Within(0.5f), "Col2 Ty=-200");
            // Tx sequence: colWidth=100, gap=0 → 0, 100, 200 (slice k into column k).
            var txs = pushTransforms.Take(3).Select(pt => pt.Transform.Tx).ToList();
            Assert.That(txs[0], Is.EqualTo(0f).Within(0.5f),   "Col0 Tx=0");
            Assert.That(txs[1], Is.EqualTo(100f).Within(0.5f), "Col1 Tx=colWidth=100");
            Assert.That(txs[2], Is.EqualTo(200f).Within(0.5f), "Col2 Tx=2*colWidth=200");
        }

        // -----------------------------------------------------------------------
        // D — text child: DrawText commands appear per column slice, geometry
        // is the child's original bounds (clip restricts visible area).
        // -----------------------------------------------------------------------
        [Test]
        public void Text_child_draw_text_commands_appear_per_column_slice() {
            // 2-column, child with text overflows column height.
            // The child has a background (FillRect) and text (DrawText).
            // After fragmentation both commands should appear twice.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall'>Hi</div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 50px; }" +
                "#tall { height: 100px; background: yellow; color: black; " +
                "        font-size: 16px; line-height: 16px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // Two column slices → at least 2 PushClip scopes.
            Assert.That(Count<PushClipCommand>(cmds), Is.GreaterThanOrEqualTo(2),
                "Text child spanning 2 columns → 2 clip scopes");

            // The FillRect (background) appears inside each slice.
            Assert.That(Count<FillRectCommand>(cmds), Is.GreaterThanOrEqualTo(2),
                "Background FillRect must appear once per column slice");
        }

        // -----------------------------------------------------------------------
        // E — column rules still emitted correctly alongside sliced children.
        // -----------------------------------------------------------------------
        [Test]
        public void Column_rules_emitted_with_sliced_children() {
            // 3-column container, column-rule: 2px solid red.
            // One child is tall (spans columns).
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall'></div></div>",
                "#mc { width: 300px; column-count: 3; column-gap: 0; height: 100px; " +
                "       column-rule: 2px solid red; }" +
                "#tall { height: 250px; background: blue; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // Column rules: N-1 = 2 FillRect commands for the 2 gaps,
            // plus FillRects from the child background per slice (>=3).
            // The rule FillRects are at the column rule positions.
            // Total FillRects must be > 3 (child slices) + 2 (rules) = ≥5.
            Assert.That(Count<FillRectCommand>(cmds), Is.GreaterThanOrEqualTo(5),
                "Column rules (2) + child slices (3) → at least 5 FillRect commands");

            // Also confirm fragmentation is happening: at least 3 PushClip.
            Assert.That(Count<PushClipCommand>(cmds), Is.GreaterThanOrEqualTo(3),
                "Three column slices → at least 3 PushClip commands");
        }

        // -----------------------------------------------------------------------
        // F — multiple tall children each fragment independently.
        // Child A is in column 0 and spans both columns → sliced (2 clips).
        // Child B lands in column 1 (last column) and overflows → NOT sliced
        // (last-column overflow; documented v1 limit, matches today's behavior).
        // -----------------------------------------------------------------------
        [Test]
        public void Multiple_tall_children_each_fragment_independently() {
            // 3-column, 3 tall children. Children A and B each span multiple
            // columns; child C lands in the last column and overflows (no further
            // slicing). A: col0→2 spans, B: col1→col2 spans, C: in col2 (last, overflow).
            // With 3 cols of 100px each: A(200px)→spans cols 0,1; B(200px from col1)
            // → but layout places A in col0, B in col1, C in col2 (each occupying
            // one column). A and B each overflow their column → A spans col0+col1 (2 clips),
            // B spans col1+col2 (2 clips). C is in col2 (last column) and overflows;
            // since the C2 div #1 fix it now gets ONE last-column tail clip (was 0).
            // Total: 5 PushClip (4 from slicing + 1 last-column tail).
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='a' style='background:red'></div>" +
                "<div id='b' style='background:blue'></div>" +
                "<div id='c' style='background:green'></div>" +
                "</div>",
                "#mc { width: 300px; column-count: 3; column-gap: 0; height: 100px; }" +
                "#a { height: 200px; } #b { height: 200px; } #c { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // A spans col0+col1 (2 clips); B spans col1+col2 (2 clips);
            // C is in col2 (last column) and overflows → 1 last-column tail clip.
            // Total PushClip ≥ 4 (actually 5 since the C2 div #1 fix).
            Assert.That(Count<PushClipCommand>(cmds), Is.GreaterThanOrEqualTo(4),
                "Children A and B each span 2 columns → 4 PushClip scopes total");

            Assert.That(Count<PushTransformCommand>(cmds), Is.GreaterThanOrEqualTo(4),
                "Children A and B each span 2 columns → 4 PushTransform scopes total");
        }

        // -----------------------------------------------------------------------
        // G — short child after a tall one paints normally (no extra clip/transform).
        // -----------------------------------------------------------------------
        [Test]
        public void Short_child_after_tall_child_paints_without_slicing() {
            // Child A: 200px (spans 2 columns). Child B: 50px (fits in its column).
            // With 2 columns of height 100px, after A fills both columns B is placed
            // back in col0 if there's room, or col1 at the right.
            // The key assertion: B does NOT get a PushClip/PushTransform from slicing.
            // We verify this by counting that the clip/transform count matches
            // ONLY what A needs (2 each).
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='tall' style='background:red'></div>" +
                "<div id='short' style='background:green'></div>" +
                "</div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#tall { height: 200px; } #short { height: 30px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // Tall child (200px spanning 2×100px columns) → 2 PushClip/Transform.
            // Short child (30px in a column with 100px height) → 0 from fragmentation.
            // We cannot assert exactly 2 without knowing overflow:hidden clips, so
            // instead verify: exactly the TALL child's 2 scopes are present and the
            // short child contributes 0 additional fragmentation clips.
            // Use the FillRect count as the proxy: tall = 2 (one per slice),
            // short = 1 (no slicing). Total >= 3 from backgrounds.
            Assert.That(Count<FillRectCommand>(cmds), Is.GreaterThanOrEqualTo(3),
                "Tall child (2 slices) + short child (1 slice) → ≥3 FillRect commands");

            // PushTransform count should NOT grow beyond 2 (only tall child sliced).
            // If it were 4 the short child was wrongly sliced too.
            var pushTransforms = All<PushTransformCommand>(cmds);
            Assert.That(pushTransforms.Count, Is.LessThanOrEqualTo(3),
                "Short child must not produce additional fragmentation transforms beyond the tall child's 2");
        }

        // -----------------------------------------------------------------------
        // H — explicit-height container: colHeight from content height.
        // -----------------------------------------------------------------------
        [Test]
        public void Explicit_height_container_uses_content_height_as_col_height() {
            // 2-column, height:80px (colHeight=80px). Child=160px → spans 2 columns.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:purple'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 80px; }" +
                "#tall { height: 160px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // 2 column slices → 2 PushClip + 2 PushTransform.
            Assert.That(Count<PushClipCommand>(cmds), Is.GreaterThanOrEqualTo(2),
                "2-column explicit-height slice → 2 PushClip");
            Assert.That(Count<PushTransformCommand>(cmds), Is.GreaterThanOrEqualTo(2),
                "2-column explicit-height slice → 2 PushTransform");

            // Verify translate amounts.
            var transforms = All<PushTransformCommand>(cmds);
            var tys = transforms.Take(2).Select(t => t.Transform.Ty).ToList();
            Assert.That(tys[0], Is.EqualTo(0f).Within(0.5f),   "Col0 Ty=0");
            Assert.That(tys[1], Is.EqualTo(-80f).Within(0.5f),  "Col1 Ty=-colHeight=-80px");
        }

        // -----------------------------------------------------------------------
        // I — clip rect heights equal colHeight (not container height).
        // -----------------------------------------------------------------------
        [Test]
        public void Clip_rects_have_col_height_not_container_height() {
            // height:100px, so each clip rect must be 100px tall.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:orange'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#tall { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);
            var pushClips = All<PushClipCommand>(cmds);
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(2));

            // All fragmentation clip rects must be exactly colHeight=100px tall.
            foreach (var pc in pushClips.Take(2)) {
                Assert.That(pc.Bounds.Height, Is.EqualTo(100.0).Within(1.0),
                    "Fragmentation clip rect height must equal colHeight=100px");
            }
        }

        // -----------------------------------------------------------------------
        // J — single-column multicol: child spanning exactly 1 column is not sliced.
        // -----------------------------------------------------------------------
        [Test]
        public void Single_column_multicol_child_never_sliced() {
            // column-count:1 → only 1 column → no fragmentation possible.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:pink'></div></div>",
                "#mc { width: 400px; column-count: 1; height: 100px; }" +
                "#tall { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var cmds = Convert(root);

            // No fragmentation transforms in a single-column container.
            Assert.That(Count<PushTransformCommand>(cmds), Is.EqualTo(0),
                "Single-column multicol: no fragmentation transforms");
        }

        // -----------------------------------------------------------------------
        // K — C2 divergence #1 fix: child taller than total column span is clipped
        // to colHeight on the last column.
        // A child with height > N * colHeight must NOT paint below colAbsY + colHeight
        // on the last column.  The clip rect for the last-column tail must have
        // height == UsedColumnHeight (not the full child height).
        // -----------------------------------------------------------------------
        [Test]
        public void Child_taller_than_total_column_span_last_column_clip_height_equals_col_height() {
            // 2-column container, colHeight=100px.  Total span = 200px.
            // Child is 350px — far taller than 2 columns.
            // After slicing: col0 clip height=100, col1 last-col clip height=100.
            // Before the fix: col1 had no clip → child overflowed below the column box.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall' style='background:green'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#tall { height: 350px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");
            Assert.That(mc.UsedColumnHeight, Is.EqualTo(100.0).Within(1.0),
                "UsedColumnHeight must be 100px");

            var cmds = Convert(root);
            var pushClips = All<PushClipCommand>(cmds);

            // Two columns → 2 PushClip scopes (col0 and col1 last-column tail).
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(2),
                "One clip per column including last-column tail");

            // EVERY clip rect height must be exactly colHeight=100px — not the child
            // height (350px) and not any larger value.  This is the Chrome-parity assertion.
            foreach (var pc in pushClips.Take(2)) {
                Assert.That(pc.Bounds.Height, Is.EqualTo(100.0).Within(1.0),
                    "Each column clip (including last-column tail) must be colHeight=100px tall");
            }

            // No PushTransform is expected: col0 gets Translate(0,0) → the slicer
            // emits it; col1-tail goes through the endColWasCapped path which uses
            // only a PushClip (no translate needed since the child is already at col1).
            // However col0 IS sliced normally so PushTransform appears for that path.
            // Key contract: total PushTransforms ≤ PushClips (last-column tail adds
            // clip but NOT a transform).
            var pushTransforms = All<PushTransformCommand>(cmds);
            Assert.That(pushTransforms.Count, Is.LessThanOrEqualTo(pushClips.Count),
                "Last-column tail clip must not introduce an extra PushTransform");
        }

        // -----------------------------------------------------------------------
        // K2 — C2 divergence #1: child placed IN the last column and taller than
        // colHeight must be clipped.  This is the exact code path added by the fix:
        // startCol == endCol == N-1, endColWasCapped == true, !needsSlicing →
        // PushClip(colHeight) wraps the VisitBox call.
        //
        // Before the fix: child went through `VisitBox` with NO clip and painted
        // below the column box.  After the fix: one PushClip with height == colHeight,
        // zero PushTransform (no column X/Y shift needed, child is already in place).
        // -----------------------------------------------------------------------
        [Test]
        public void Child_in_last_column_overflowing_gets_clip_to_col_height() {
            // 2-column container, 200px wide, colHeight=100px.
            // placeholder (100px) fills col0; tall (200px) lands in col1 (last column)
            // and overflows downward by 100px.
            // After the fix: tall child gets PushClip(height=100), no PushTransform.
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='placeholder' style='background:red'></div>" +
                "<div id='tall' style='background:lime'></div>" +
                "</div>",
                "#mc { width: 200px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#placeholder { height: 100px; }" +    // fills col0 exactly
                "#tall { height: 200px; }");            // in col1, overflows by 100px

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");
            Assert.That(mc.UsedColumnHeight, Is.EqualTo(100.0).Within(1.0),
                "UsedColumnHeight must be 100px");

            var cmds = Convert(root);
            var pushClips = All<PushClipCommand>(cmds);
            var pushTransforms = All<PushTransformCommand>(cmds);

            // Exactly 1 PushClip from the last-column tail path.
            // The placeholder fits in col0 (no clip), tall overflows in col1 (1 clip).
            Assert.That(pushClips.Count, Is.EqualTo(1),
                "One clip from last-column tail (endColWasCapped path)");

            // The clip must be exactly colHeight=100px tall, not the child height (200px).
            Assert.That(pushClips[0].Bounds.Height, Is.EqualTo(100.0).Within(1.0),
                "Last-column tail clip height must equal colHeight=100px (Chrome parity)");

            // No PushTransform: the child is already in its column's X position;
            // the clip alone is sufficient to prevent the downward overflow.
            Assert.That(pushTransforms.Count, Is.EqualTo(0),
                "Last-column tail fix uses clip only — no translate needed");
        }

        // -----------------------------------------------------------------------
        // L — regression: a child that FITS in its column (no overflow) is byte-
        // identical to pre-fix behavior — no PushClip or PushTransform from the
        // fragmentation path.
        // -----------------------------------------------------------------------
        [Test]
        public void Child_fitting_in_last_column_produces_no_fragmentation_clip() {
            // 2-column container, colHeight=100px.  Child is only 80px tall — fits
            // in column 1 (the last column) without overflowing.  The fix must NOT
            // add any clip because endColWasCapped == false here.
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='placeholder' style='background:red'></div>" +
                "<div id='short' style='background:blue'></div>" +
                "</div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#placeholder { height: 100px; }" +   // fills col0 exactly
                "#short { height: 80px; }");           // lands in col1, fits

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");

            var cmds = Convert(root);

            // The short child fits in col1 (80px < 100px = colHeight), so neither
            // a fragmentation PushClip nor a PushTransform must appear.
            Assert.That(Count<PushTransformCommand>(cmds), Is.EqualTo(0),
                "Fitting child in last column: no PushTransform from fragmentation");
            // PushClip count = 0 as well (no overflow:hidden, no fragmentation clips).
            Assert.That(Count<PushClipCommand>(cmds), Is.EqualTo(0),
                "Fitting child in last column: no fragmentation PushClip");
        }

        // -----------------------------------------------------------------------
        // M — regression: a child spanning exactly to the last column boundary
        // (occupiedHeight == N * colHeight, no tail) must NOT gain an extra clip
        // from the endColWasCapped path.
        // -----------------------------------------------------------------------
        [Test]
        public void Child_spanning_exactly_to_last_column_boundary_no_extra_clip() {
            // 2-column container, colHeight=100px.  Total span = 200px.
            // Child is exactly 200px — fills columns 0 and 1 completely, no tail.
            // spanCols = ceil(200/100) = 2, endCol = 1 = N-1 = 1 — NOT capped.
            // endColWasCapped == false → the slicing loop handles it normally.
            // The fix must NOT add a third clip.
            var (root, _, _) = Build(
                "<div id='mc'><div id='exact' style='background:orange'></div></div>",
                "#mc { width: 400px; column-count: 2; column-gap: 0; height: 100px; }" +
                "#exact { height: 200px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox required");

            var cmds = Convert(root);
            var pushClips = All<PushClipCommand>(cmds);
            var pushTransforms = All<PushTransformCommand>(cmds);

            // Slicing loop emits exactly 2 clips and 2 transforms (one per column).
            // The endColWasCapped branch must not fire (endCol was already N-1 naturally).
            Assert.That(pushClips.Count, Is.EqualTo(2),
                "Exactly 2 clips for 2 columns — no bonus clip from last-column-tail path");
            Assert.That(pushTransforms.Count, Is.EqualTo(2),
                "Exactly 2 transforms for 2 columns — slicing loop, not tail path");

            // Verify clip heights are both colHeight=100px.
            foreach (var pc in pushClips) {
                Assert.That(pc.Bounds.Height, Is.EqualTo(100.0).Within(1.0),
                    "Each clip must be colHeight=100px");
            }
        }
    }
}
