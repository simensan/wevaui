using System.Collections.Generic;
using NUnit.Framework;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // CSS 2.1 Appendix E (painting order) step 3 sub-pass coverage for
    // BoxToPaintConverter. The converter used to walk box.Children in
    // raw document order for the in-flow paint pass, which placed floats
    // *before* their block-level siblings in the command stream even
    // though the spec puts floats in step 3b — after 3a (block-level)
    // and before 3c (inline-level). PaintOrderTraversal already split
    // EnumerateContext into 3a/3b/3c; B4b rewires the converter so the
    // step-3 split actually drives output. These tests pin the spec
    // ordering at the direct-child level (the only level the converter
    // sees per recursive frame) and verify hit-testing is unaffected.
    public class InflowPaintOrderTests {
        // Locates the first solid-color FillRect whose dominant channel
        // matches `channel` ('R', 'G', 'B'). Mirrors the helper used by
        // StackingOrderPaintTests so the two suites assert against the
        // same paint surface without dragging in a shared helper.
        static int FindFillIndex(List<PaintCommand> cmds, char channel) {
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor) {
                    var c = fr.Brush.Color;
                    switch (channel) {
                        case 'R': if (c.R > 0.5f && c.G < 0.3f && c.B < 0.3f) return i; break;
                        case 'G': if (c.G > 0.5f && c.R < 0.3f && c.B < 0.3f) return i; break;
                        case 'B': if (c.B > 0.5f && c.R < 0.3f && c.G < 0.3f) return i; break;
                    }
                }
            }
            return -1;
        }

        // Index of the first DrawText command whose text contains `needle`.
        static int FindTextIndex(List<PaintCommand> cmds, string needle) {
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is DrawTextCommand dt && dt.Text != null && dt.Text.Contains(needle)) {
                    return i;
                }
            }
            return -1;
        }

        [Test]
        public void Inline_element_background_paints_behind_its_own_text() {
            // An inline element (<code>, <mark>, highlighted <span>) carries
            // a background/border on an InlineBox decoration shell. Inline
            // layout flattens the element's text out into sibling TextRuns
            // under the LineBox and appends the shell as a TRAILING sibling.
            // Walked in raw document order the shell paints LAST — its solid
            // fill covers the glyphs, blanking the text (the 9slice-demo
            // "Sprite Mode = Single" amber-on-pill regression). CSS 2.1
            // Appendix E puts inline-box backgrounds before the inline
            // content, so the fill must precede the DrawText.
            const string html =
                "<p>before <span class=\"hl\">HILITE</span> after</p>";
            const string css = @"
                .hl { background-color: blue; color: rgb(255,255,0); }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int fillIdx = FindFillIndex(cmds, 'B');
            int textIdx = FindTextIndex(cmds, "HILITE");
            Assert.That(fillIdx, Is.GreaterThanOrEqualTo(0), "Expected blue inline background fill");
            Assert.That(textIdx, Is.GreaterThanOrEqualTo(0), "Expected DrawText for the highlighted run");
            Assert.That(fillIdx, Is.LessThan(textIdx),
                "Inline-box background must paint BEFORE its text so the glyphs sit on top of the pill");
        }

        [Test]
        public void Float_first_then_block_sibling_paints_block_before_float() {
            // Document order [.f, .b] where .f is float:left and .b is a
            // plain in-flow block. Per CSS 2.1 §E step 3a → 3b, the
            // block-level sibling (.b, red) must paint BEFORE the float
            // (.f, blue) so the float's decoration sits atop the block
            // background. Pre-B4b this came out in document order (.f
            // then .b) because the converter walked box.Children raw.
            const string html =
                "<div class=\"f\"></div>" +
                "<div class=\"b\"></div>";
            const string css = @"
                .f { float: left; width: 50px; height: 50px; background-color: blue; }
                .b { width: 200px; height: 100px; background-color: red; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int floatIdx = FindFillIndex(cmds, 'B');
            int blockIdx = FindFillIndex(cmds, 'R');
            Assert.That(floatIdx, Is.GreaterThanOrEqualTo(0), "Expected blue fill from float .f");
            Assert.That(blockIdx, Is.GreaterThanOrEqualTo(0), "Expected red fill from block .b");
            // Block-level paints before float — 3a then 3b.
            Assert.That(blockIdx, Is.LessThan(floatIdx),
                ".b (block-level, step 3a) must paint before .f (float, step 3b)");
        }

        [Test]
        public void Mixed_floats_and_blocks_paint_in_spec_3a_then_3b_order() {
            // Three siblings: red block, lime float, blue block. Spec
            // order at the direct-child level: red, blue (both 3a in
            // doc order), then lime (3b). Doc order would have
            // produced [red, lime, blue]; the spec walk produces
            // [red, blue, lime]. Asserting `lime is LAST among the
            // three` pins the float-after-block rule without depending
            // on the relative order of the two block-level siblings
            // (which IS doc-order within 3a — but we'd rather not
            // double-pin that here).
            const string html =
                "<div class=\"a\"></div>" +
                "<div class=\"f\"></div>" +
                "<div class=\"c\"></div>";
            const string css = @"
                .a { width: 100px; height: 50px; background-color: red; }
                .f { float: left; width: 40px; height: 40px; background-color: lime; }
                .c { width: 100px; height: 50px; background-color: blue; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int aIdx = FindFillIndex(cmds, 'R');
            int fIdx = FindFillIndex(cmds, 'G');
            int cIdx = FindFillIndex(cmds, 'B');
            Assert.That(aIdx, Is.GreaterThanOrEqualTo(0), "Expected red fill from .a");
            Assert.That(fIdx, Is.GreaterThanOrEqualTo(0), "Expected lime fill from float .f");
            Assert.That(cIdx, Is.GreaterThanOrEqualTo(0), "Expected blue fill from .c");

            // Float (3b) paints AFTER every block-level sibling (3a).
            Assert.That(aIdx, Is.LessThan(fIdx),
                ".a (block-level 3a) must paint before .f (float 3b)");
            Assert.That(cIdx, Is.LessThan(fIdx),
                ".c (block-level 3a) must paint before .f (float 3b)");
        }

        [Test]
        public void Hit_test_still_picks_topmost_element_after_spec_paint_rewire() {
            // Regression pin for the subtlety called out in B4b: paint
            // order changed (block-level before float), but hit-testing
            // walks box.Children in REVERSE document order independently
            // of paint order, so the front-to-back topmost-pick must be
            // unaffected. Scene: a 100x100 .b block at (0,0), a 50x50
            // .top absolutely-positioned overlay anchored at (0,0).
            // Pointer at (10,10) is inside both. The overlay comes
            // LATER in the document, so the reverse-doc-order hit
            // walker still finds it as the topmost target. Without
            // this pin a future rewire that accidentally swaps
            // hit-testing to consume PaintOrderTraversal would silently
            // change topmost selection.
            //
            // We use position:absolute (not float) for the overlay so
            // the visual stacking is unambiguous and doesn't depend on
            // BlockLayout's float-placement intricacies; the property
            // under test is "paint-walker change didn't leak into
            // hit-test", which holds for any visually-topmost element.
            const string html =
                "<div class=\"b\"></div>" +
                "<div class=\"top\"></div>";
            const string css = @"
                .b { position: relative; width: 100px; height: 100px; background-color: red; }
                .top { position: absolute; left: 0; top: 0; width: 50px; height: 50px; background-color: blue; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var hit = new BoxTreeHitTester(root).HitTest(10, 10);
            Assert.That(hit, Is.Not.Null, "expected the topmost element under (10,10)");
            // .top is the visually-topmost element at (10,10) and its
            // DOM node is the last sibling — reverse-doc-order hit
            // walking still picks it.
            Assert.That(hit.GetAttribute("class"), Is.EqualTo("top"),
                "hit-test must pick .top (later in document, visually atop) even after the paint walker switched to spec order");
        }

        [Test]
        public void Document_order_walker_would_paint_float_first_spec_walker_paints_float_last() {
            // Detection pin: the previous (doc-order) walker placed the
            // float BEFORE the block sibling. The spec walker places
            // the float AFTER. Today we expect the latter; if the
            // converter ever silently reverts to doc-order for inflow
            // children, this assertion fails loudly. This is the
            // explicit "baseline-was-wrong, now-it's-right" pin called
            // out in the B4b task.
            const string html =
                "<div class=\"f\"></div>" +
                "<div class=\"b\"></div>";
            const string css = @"
                .f { float: left; width: 30px; height: 30px; background-color: lime; }
                .b { width: 200px; height: 80px; background-color: red; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            // Sanity-check the layout tree: the float and the block
            // ARE direct siblings (i.e. the float wasn't hoisted to a
            // different parent). Without this confidence the index
            // comparison below could pass for unrelated reasons.
            BlockBox floatBox = null;
            BlockBox blockBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    string cls = bb.Element.GetAttribute("class");
                    if (cls == "f") floatBox = bb;
                    else if (cls == "b") blockBox = bb;
                }
            }
            Assert.That(floatBox, Is.Not.Null, "test setup: expected the float box in the layout tree");
            Assert.That(blockBox, Is.Not.Null, "test setup: expected the block box in the layout tree");
            Assert.That(floatBox.IsFloat, Is.True, "test setup: .f must be classified as a float");
            Assert.That(blockBox.IsFloat, Is.False, "test setup: .b must NOT be a float");
            Assert.That(ReferenceEquals(floatBox.Parent, blockBox.Parent), Is.True,
                "test setup: float and block must be direct siblings for the bucket walk to govern their order");

            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int floatIdx = FindFillIndex(cmds, 'G');
            int blockIdx = FindFillIndex(cmds, 'R');
            Assert.That(floatIdx, Is.GreaterThanOrEqualTo(0), "Expected lime fill from float .f");
            Assert.That(blockIdx, Is.GreaterThanOrEqualTo(0), "Expected red fill from block .b");

            // The whole point: spec-correct order is block-then-float.
            // Doc-order would have given block-fill INDEX > float-fill
            // INDEX (because float appears first). If we ever see that
            // again, B4b regressed.
            Assert.That(blockIdx, Is.LessThan(floatIdx),
                "Spec order: 3a (block) must precede 3b (float). " +
                "If this fails the converter is back to walking box.Children in document order.");
        }
    }
}
