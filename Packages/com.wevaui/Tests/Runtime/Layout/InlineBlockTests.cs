using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class InlineBlockTests {
        static BlockBox FirstInlineBlock(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.IsInlineBlock) return bb;
            }
            return null;
        }

        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        [Test]
        public void Inline_block_sits_on_a_line_with_text() {
            var (root, _, _) = Build(
                "<p>before <span style=\"display:inline-block;width:30px;height:20px\"></span> after</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            int lineCount = 0;
            LineBox firstLine = null;
            foreach (var c in p.Children) {
                if (c is LineBox lb) {
                    lineCount++;
                    if (firstLine == null) firstLine = lb;
                }
            }
            Assert.That(lineCount, Is.EqualTo(1));
            // The line must contain the inline-block as one of its children.
            bool hasInlineBlock = false;
            foreach (var c in firstLine.Children) {
                if (c is BlockBox bb && bb.IsInlineBlock) hasInlineBlock = true;
            }
            Assert.That(hasInlineBlock, Is.True);
        }

        [Test]
        public void Inline_block_size_matches_intrinsic_content_width() {
            // Without an explicit width, inline-block shrinks to fit its content.
            var (root, _, _) = Build(
                "<p><span style=\"display:inline-block\">hi</span></p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom, Is.Not.Null);
            // "hi" at 16px mono = 2 chars * 8 = 16. Atom shrink-to-fit = 16.
            Assert.That(atom.Width, Is.EqualTo(16).Within(0.001));
        }

        [Test]
        public void Multiple_inline_blocks_line_up_horizontally() {
            var (root, _, _) = Build(
                "<p>" +
                "<span style=\"display:inline-block;width:30px;height:20px\"></span>" +
                "<span style=\"display:inline-block;width:40px;height:20px\"></span>" +
                "</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            // Two inline-block atoms on the same line.
            int atomCount = 0;
            foreach (var c in line.Children) {
                if (c is BlockBox bb && bb.IsInlineBlock) atomCount++;
            }
            Assert.That(atomCount, Is.EqualTo(2));
        }

        [Test]
        public void Inline_block_wraps_to_next_line_when_overflow() {
            var (root, _, _) = Build(
                "<p>" +
                "<span style=\"display:inline-block;width:60px;height:20px\"></span>" +
                "<span style=\"display:inline-block;width:60px;height:20px\"></span>" +
                "</p>",
                null, viewportWidth: 100);
            var p = FirstByTag(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.EqualTo(2));
        }

        [Test]
        public void Inline_block_with_explicit_width_uses_that_width() {
            var (root, _, _) = Build(
                "<p><span style=\"display:inline-block;width:120px;height:30px\">x</span></p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom.Width, Is.EqualTo(120).Within(0.001));
            Assert.That(atom.Height, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Inline_block_baseline_aligns_with_text_baseline() {
            // The inline-block's baseline (bottom of its content) should sit at
            // the line's baseline. With default font 16px mono (ascent 0.8 = 12.8,
            // descent 0.4 = 6.4, line-height 1.2 = 19.2), an inline-block of
            // height 20 should sit so its bottom is at baseline 12.8 within line —
            // the line will grow to accommodate.
            var (root, _, _) = Build(
                "<p>x<span style=\"display:inline-block;width:20px;height:20px\"></span></p>",
                null, 800);
            var p = FirstByTag(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            var atom = FirstInlineBlock(root);
            Assert.That(line, Is.Not.Null);
            Assert.That(atom, Is.Not.Null);
            // atom baseline = bottom of atom (height 20). Line baseline must equal
            // max(atom.Height, text.Ascent). Atom bottom Y = atom.Y + atom.Height
            // should equal line.Baseline (within line-local coords).
            Assert.That(atom.Y + atom.Height, Is.EqualTo(line.Baseline).Within(0.001));
        }

        [Test]
        public void Inline_block_content_children_lay_out_as_block() {
            var (root, _, _) = Build(
                "<p><span style=\"display:inline-block;width:200px\"><div style=\"height:40px\"></div></span></p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom, Is.Not.Null);
            // The inner <div> is a block-level child of the inline-block; its
            // height contributes to the atom's height.
            Assert.That(atom.Height, Is.GreaterThanOrEqualTo(40 - 0.001));
        }

        [Test]
        public void Inline_block_shrink_to_fit_preserves_text_runs() {
            // Regression: shrink-to-fit lays out the atom twice (once at the
            // unconstrained width to measure max-content, once at the fitted
            // width). The first pass turns atom.Children into LineBox-es; the
            // second pass must still see real TextRun-bearing children, not the
            // LineBox-es from pass 1, otherwise the atom ends up with an empty
            // line and the badge text vanishes (background still paints because
            // padding gives the box a non-zero size).
            var (root, _, _) = Build(
                "<p><span style=\"display:inline-block;padding:2px 10px\">ready</span></p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom, Is.Not.Null);
            LineBox innerLine = null;
            foreach (var c in atom.Children) if (c is LineBox lb) { innerLine = lb; break; }
            Assert.That(innerLine, Is.Not.Null, "inline-block atom must contain a LineBox");
            bool hasText = false;
            foreach (var c in innerLine.Children) {
                if (c is TextRun tr && !string.IsNullOrEmpty(tr.Text)) { hasText = true; break; }
            }
            Assert.That(hasText, Is.True, "inline-block atom's line must contain a non-empty TextRun");
            // 5 chars * 8px (mono 16px) = 40 + padding 20 = 60.
            Assert.That(atom.Width, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Button_default_inline_block_sizes_to_text_content() {
            // <button> defaults to inline-block per UA stylesheet. Our UA-lite
            // (LayoutTestHelpers.BuiltinUserAgent) doesn't set that, so we set
            // it explicitly via class style here.
            var (root, _, _) = Build(
                "<p><button style=\"display:inline-block\">Start</button></p>",
                null, 800);
            BlockBox btn = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "button") btn = bb;
            }
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.IsInlineBlock, Is.True);
            // "Start" = 5 chars * 8 = 40 at 16px mono.
            Assert.That(btn.Width, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Inline_block_with_non_visible_overflow_uses_bottom_margin_edge_as_baseline() {
            // CSS 2.1 §10.8.1: when `overflow` is anything other than `visible`,
            // the inline-block's baseline is the bottom margin edge, not the
            // last in-flow line box. With no margins, the bottom border-edge
            // sits on the surrounding text's baseline.
            var (root, _, _) = Build(
                "<p>x<span style=\"display:inline-block;overflow:hidden;width:40px;height:80px\">hi</span></p>",
                null, 800);
            var p = FirstByTag(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            var atom = FirstInlineBlock(root);
            Assert.That(line, Is.Not.Null);
            Assert.That(atom, Is.Not.Null);
            // Bottom-margin edge (= atom.Y + atom.Height with zero margin)
            // must coincide with the line's baseline.
            Assert.That(atom.Y + atom.Height, Is.EqualTo(line.Baseline).Within(0.001));
        }

        [Test]
        public void Inline_block_with_visible_overflow_uses_last_line_box_baseline() {
            // Regression guard: with the default `overflow: visible`, the
            // inline-block's baseline must still derive from the last in-flow
            // line box (CSS 2.1 §10.8.1 default rule). Distinguishable from
            // the bottom-margin-edge case because the explicit 80px height
            // places the inner line near the top of the box.
            var (root, _, _) = Build(
                "<p>x<span style=\"display:inline-block;width:40px;height:80px\">hi</span></p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom, Is.Not.Null);
            LineBox innerLine = null;
            foreach (var c in atom.ChildList) if (c is LineBox lb) { innerLine = lb; break; }
            Assert.That(innerLine, Is.Not.Null);
            var p = FirstByTag(root, "p");
            LineBox outerLine = null;
            foreach (var c in p.Children) if (c is LineBox lb) { outerLine = lb; break; }
            Assert.That(outerLine, Is.Not.Null);
            // The atom is positioned so that (innerLine.Y + innerLine.Baseline),
            // measured from atom's top, lands on the outer line's baseline.
            double expectedAtomTop = outerLine.Baseline - (innerLine.Y + innerLine.Baseline);
            Assert.That(atom.Y, Is.EqualTo(expectedAtomTop).Within(0.001));
            // And the atom's bottom edge sits BELOW the outer baseline, since
            // the inner content baseline is far above the box bottom (80px).
            Assert.That(atom.Y + atom.Height, Is.GreaterThan(outerLine.Baseline + 1));
        }
    }
}
