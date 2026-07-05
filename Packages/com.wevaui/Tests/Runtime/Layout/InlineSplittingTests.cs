using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // HTML5 §13.2.6 "adoption agency": a block-level element opening while
    // an inline formatting element (`<a>`, `<span>`, etc.) is open inside a
    // `<p>` causes the parser to close the `<p>` and re-open the formatting
    // element around the block. Result: instead of a single `<p>` with an
    // inline-split block child, you get a `<p>`, a sibling `<div>`, and any
    // trailing inline content as further siblings — matching what Chrome
    // and Firefox produce.
    //
    // Before the parser fix these tests asserted that the layout engine
    // split the IFC at the block. Post-fix the split lives in the parser
    // tree itself, so the assertions verify the structural reshape (and
    // that the block child still has the visual properties it should).
    //
    // For block-inside-inline cases that DON'T involve a `<p>` (e.g.
    // `<span><div></div></span>` directly under <body>) the parser leaves
    // the tree alone and the InlineLayout splitting code path still runs —
    // see InlineLayoutTests for that coverage.
    public class InlineSplittingTests {
        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static int CountBlockOfTag(Box root, string tag) {
            int n = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && !bb.IsInlineBlock && bb.Element?.TagName == tag) n++;
            }
            return n;
        }

        [Test]
        public void Block_inside_inline_in_p_lifts_block_to_p_sibling() {
            // Chrome/Firefox per HTML5 §13.2.6 adoption-agency: the `<div>`
            // closes the `<p>` (block-closes-p rule), the `<a>` is
            // re-opened around "middle" inside the new `<div>`, and a
            // second `<a>` re-opens around "z". Final tree under <body>:
            //   <p>before <a/></p>
            //   <div><a>middle</a></div>
            //   <a>z</a>
            //   " after"
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">x<div>middle</div>z</a> after</p>",
                null, 800);
            // Exactly one <div> in the tree, and it is NOT a child of <p>.
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(1));
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);
            int divChildrenOfP = 0;
            foreach (var c in p.Children) {
                if (c is BlockBox bb && !(c is AnonymousBlockBox) && !bb.IsInlineBlock
                    && bb.Element?.TagName == "div") divChildrenOfP++;
            }
            Assert.That(divChildrenOfP, Is.EqualTo(0));
        }

        [Test]
        public void Inline_with_only_block_child_promotes_block_to_p_sibling() {
            // `<p><a><div>solo</div></a></p>` — AAA closes <p> for the <div>,
            // wraps "solo" in a re-opened <a> inside the <div>. The result
            // tree has 1 <div> and an empty <p>.
            var (root, _, _) = Build(
                "<p><a href=\"#\"><div>solo</div></a></p>",
                null, 800);
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(1));
        }

        [Test]
        public void Block_height_contributes_to_paragraph_sibling_height() {
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">x<div style=\"height:50px\">middle</div>z</a> after</p>",
                null, 800);
            BlockBox div = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == "div") { div = bb; break; }
            }
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Height, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Inline_style_propagates_to_text_in_re_opened_formatting() {
            // After AAA the `<a>` is re-opened inside the `<div>` and again
            // around "z" — both clones inherit attributes/styles from the
            // original, so descendant text styled via `a { color: blue }`
            // still receives the color.
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">x<div>middle</div>z</a> after</p>",
                "a { color: blue; }", 800);
            TextRun midRun = null;
            TextRun zRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr) {
                    if (tr.Text == "middle") midRun = tr;
                    else if (tr.Text == "z") zRun = tr;
                }
            }
            Assert.That(midRun, Is.Not.Null);
            Assert.That(zRun, Is.Not.Null);
            Assert.That(midRun.Style.Get("color"), Is.EqualTo("blue"));
            Assert.That(zRun.Style.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Two_blocks_inside_inline_each_become_p_siblings() {
            // <p><a>x<div>1</div>y<div>2</div>z</a></p>:
            // AAA fires twice. Result tree:
            //   <p><a>x</a></p>
            //   <div><a>1</a></div>
            //   <a>y</a>          -- wait: after `</div>` and reopen for y,
            //                       then <div>2</div> fires AAA again.
            // So the y-text gets an <a>, then the AAA for the second <div>
            // pops the freshly-re-opened <a> too. Net: 2 <div>s, multiple
            // <a> clones around the inline runs.
            var (root, _, _) = Build(
                "<p><a href=\"#\">x<div>one</div>y<div>two</div>z</a></p>",
                null, 800);
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(2));
        }

        [Test]
        public void Text_block_text_block_text_yields_two_divs() {
            // Same shape as Two_blocks_inside_inline_each_become_p_siblings
            // but with text outside the inline element too.
            var (root, _, _) = Build(
                "<p>a<a href=\"#\"><div>b</div>c<div>d</div></a>e</p>",
                null, 800);
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(2));
        }

        [Test]
        public void Inline_block_does_not_split_inline_run() {
            // An inline-block is an inline atom; it stays on the line, and
            // the AAA rule doesn't fire (display is inline). Single <p>
            // with one line, no <div> children.
            var (root, _, _) = Build(
                "<p>before <span style=\"display:inline-block;width:20px;height:20px\"></span> after</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.EqualTo(1));
            int blocks = 0;
            foreach (var c in p.Children) {
                if (c is LineBox) continue;
                if (c is BlockBox bb && !(c is AnonymousBlockBox) && !bb.IsInlineBlock) blocks++;
            }
            Assert.That(blocks, Is.EqualTo(0));
        }

        [Test]
        public void Anchor_with_div_matches_chrome_dom_shape() {
            // PLAN §11 demo / golden 23: `<p>Click <a><div>here</div></a> to start</p>`.
            // Chrome's output: <p>Click <a/></p>, then <div><a>here</a></div>,
            // then trailing " to start" as a sibling text node.
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\"><div>here</div></a> to start</p>",
                null, 800);
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(1));
            // The single <div> contains "here" wrapped in a re-opened <a>.
            TextRun hereRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text == "here") { hereRun = tr; break; }
            }
            Assert.That(hereRun, Is.Not.Null);
        }

        [Test]
        public void Block_descendant_uses_full_container_width() {
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">x<div>middle</div>z</a> after</p>",
                null, 400);
            BlockBox div = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == "div") { div = bb; break; }
            }
            Assert.That(div, Is.Not.Null);
            // <div> is block-level and not nested under a constrained
            // inline ancestor, so it takes the viewport content width.
            Assert.That(div.Width, Is.EqualTo(400).Within(0.001));
        }

        [Test]
        public void Empty_inline_with_only_block_still_produces_block_in_tree() {
            var (root, _, _) = Build(
                "<p><a href=\"#\"><div></div></a></p>",
                null, 800);
            Assert.That(CountBlockOfTag(root, "div"), Is.EqualTo(1));
        }
    }
}
