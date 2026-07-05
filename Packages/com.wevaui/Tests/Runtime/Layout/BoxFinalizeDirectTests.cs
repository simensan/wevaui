using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout {
    // Direct unit coverage for BoxFinalize.FinalizeBlockChildren — the post-build
    // pass that enforces the CSS 2.1 §9.2.1.1 anonymous-block-wrapping rule.
    //
    // CODE_AUDIT_FINDINGS.md TG11 flagged this pass as "0 hits — only mentioned
    // in comments inside table tests; no direct exercise of the wrap-runs logic."
    // The behaviour is exercised through BoxBuilder integration today (see
    // BoxBuilderTests.Mixed_block_and_inline_children_wrap_inlines_in_anonymous_block)
    // but the pure logic of FinalizeBlockChildren is never invoked in isolation.
    //
    // These tests hand-construct minimal BlockBox trees, allocate inline/block
    // children directly out of a BoxPool, and call
    //   BoxFinalize.FinalizeBlockChildren(parent, pool, scratch)
    // then verify the parent's Children list against the spec rule:
    //
    //   - all-block children                        -> no wrapping, ContainsInlines = false
    //   - all-inline children                       -> no wrapping, ContainsInlines = true (IFC parent)
    //   - mixed inline-then-block                   -> inline run wrapped
    //   - mixed block-then-inline                   -> inline run wrapped
    //   - inline-block-inline pattern               -> TWO separate anonymous wrappers
    //   - whitespace-only TextRun between blocks    -> NOT wrapped (collapsed)
    //   - whitespace-only TextRun between non-blocks-> NOT wrapped (no IFC promotion)
    //   - inline-block mixed with blocks            -> wrapped (CSS 2.1 §9.2.1.1 — see test note)
    public class BoxFinalizeDirectTests {
        // Each test owns its own pool + scratch so state is isolated.
        static BoxPool NewPool() => new BoxPool();
        static LayoutScratch NewScratch() => new LayoutScratch();

        // ----- helpers --------------------------------------------------------

        static BlockBox NewBlockChild(BoxPool pool, string tag = "div") {
            var b = pool.AllocateBlockBox();
            b.Element = new Element(tag);
            return b;
        }

        static InlineBox NewInlineChild(BoxPool pool, string tag = "span") {
            var ib = pool.AllocateInlineBox();
            ib.Element = new Element(tag);
            return ib;
        }

        static TextRun NewTextRun(BoxPool pool, string text) {
            var tr = pool.AllocateTextRun();
            tr.Text = text;
            return tr;
        }

        static BlockBox NewInlineBlockChild(BoxPool pool, string tag = "div") {
            var b = pool.AllocateBlockBox();
            b.Element = new Element(tag);
            b.IsInlineBlock = true;
            return b;
        }

        static int CountChildrenOfType<T>(BlockBox parent) where T : Box {
            int n = 0;
            for (int i = 0; i < parent.Children.Count; i++) {
                if (parent.Children[i] is T) n++;
            }
            return n;
        }

        // ----- 1. all-inline: no wrapping, IFC promotion -----------------------

        [Test]
        public void Block_parent_with_only_inline_children_does_not_wrap() {
            // CSS 2.1 §9.2.1.1: when a block container box has only inline-level
            // children it establishes an inline formatting context — no
            // anonymous-block wrappers are inserted; ContainsInlines flips true.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "p");
            parent.AddChild(NewInlineChild(pool, "span"));
            parent.AddChild(NewTextRun(pool, "hello"));
            parent.AddChild(NewInlineChild(pool, "em"));

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(3),
                "Pure inline children must not be regrouped into anonymous blocks.");
            Assert.That(CountChildrenOfType<AnonymousBlockBox>(parent), Is.EqualTo(0));
            Assert.That(parent.ContainsInlines, Is.True,
                "All-inline parent establishes an inline formatting context.");
        }

        // ----- 2. all-block: no wrapping ---------------------------------------

        [Test]
        public void Block_parent_with_only_block_children_does_not_wrap() {
            // CSS 2.1 §9.2.1.1: block container with only block-level children
            // is already cleanly block-formatted — no anonymous wrappers, and
            // ContainsInlines stays false.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            parent.AddChild(NewBlockChild(pool, "div"));
            parent.AddChild(NewBlockChild(pool, "p"));
            parent.AddChild(NewBlockChild(pool, "section"));

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(3));
            Assert.That(CountChildrenOfType<AnonymousBlockBox>(parent), Is.EqualTo(0));
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 3. inline-then-block --------------------------------------------

        [Test]
        public void Mixed_inline_then_block_wraps_leading_inline_run() {
            // CSS 2.1 §9.2.1.1: a run of inline-level children adjacent to a
            // block sibling must be wrapped in an anonymous block box so the
            // parent's children alternate at block level.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            parent.AddChild(NewInlineChild(pool, "span"));
            parent.AddChild(NewTextRun(pool, "leading inline"));
            var blockChild = NewBlockChild(pool, "div");
            parent.AddChild(blockChild);

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(2),
                "Leading inline run + 1 block -> 1 anonymous wrapper + 1 block.");
            Assert.That(parent.Children[0], Is.InstanceOf<AnonymousBlockBox>(),
                "First child should be the synthetic anon wrapper around the inline run.");
            Assert.That(parent.Children[1], Is.SameAs(blockChild),
                "The original block child must follow the anon wrapper unchanged.");
            var anon = (AnonymousBlockBox)parent.Children[0];
            Assert.That(anon.Children.Count, Is.EqualTo(2),
                "Anon wrapper must contain BOTH inline children from the run.");
            Assert.That(anon.Element, Is.Null,
                "Synthetic anonymous block must have null Element.");
            Assert.That(anon.Style, Is.Null,
                "Synthetic anonymous block must have null Style.");
            Assert.That(parent.ContainsInlines, Is.False,
                "After wrapping, parent's direct children are all block-level.");
        }

        // ----- 4. block-then-inline --------------------------------------------

        [Test]
        public void Mixed_block_then_inline_wraps_trailing_inline_run() {
            // Symmetric to inline-then-block: trailing inline siblings after
            // a block must also be wrapped in their own anonymous block.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            var blockChild = NewBlockChild(pool, "div");
            parent.AddChild(blockChild);
            parent.AddChild(NewTextRun(pool, "trailing"));
            parent.AddChild(NewInlineChild(pool, "span"));

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(2));
            Assert.That(parent.Children[0], Is.SameAs(blockChild));
            Assert.That(parent.Children[1], Is.InstanceOf<AnonymousBlockBox>());
            var anon = (AnonymousBlockBox)parent.Children[1];
            Assert.That(anon.Children.Count, Is.EqualTo(2),
                "Trailing TextRun + InlineBox must both land in the wrapper.");
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 5. inline-block-inline: two separate wrappers -------------------

        [Test]
        public void Mixed_inline_block_inline_produces_two_separate_anonymous_wrappers() {
            // CSS 2.1 §9.2.1.1: each contiguous run of inline-level siblings
            // gets ITS OWN anonymous wrapper; the intervening block is left
            // in place. Matches the BoxBuilder integration test
            // `Mixed_block_and_inline_children_wrap_inlines_in_anonymous_block`
            // which exercises this through the full HTML pipeline.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            parent.AddChild(NewTextRun(pool, "before"));
            var middle = NewBlockChild(pool, "div");
            parent.AddChild(middle);
            parent.AddChild(NewTextRun(pool, "after"));

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(3),
                "Expected anon + middle-block + anon (two separate wrappers).");
            Assert.That(parent.Children[0], Is.InstanceOf<AnonymousBlockBox>(),
                "Leading inline run must get its own wrapper.");
            Assert.That(parent.Children[1], Is.SameAs(middle),
                "Block sibling must remain at its original position.");
            Assert.That(parent.Children[2], Is.InstanceOf<AnonymousBlockBox>(),
                "Trailing inline run must get its own (distinct) wrapper.");
            Assert.That(parent.Children[0], Is.Not.SameAs(parent.Children[2]),
                "The two wrappers must be distinct AnonymousBlockBox instances.");
            Assert.That(CountChildrenOfType<AnonymousBlockBox>(parent), Is.EqualTo(2));
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 6. whitespace-only TextRun between blocks: dropped --------------

        [Test]
        public void Whitespace_only_textrun_between_blocks_is_not_wrapped() {
            // CSS 2.1 §9.2.1.1 last bullet: "If an anonymous block box would
            // be empty (apart from white-space) it would have been collapsed
            // as if it had display: none". BoxFinalize implements this via
            // AreAllWhitespaceTextRuns in FlushAnonymous — the wrapper is
            // simply never emitted.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            var a = NewBlockChild(pool, "div");
            var b = NewBlockChild(pool, "div");
            parent.AddChild(a);
            parent.AddChild(NewTextRun(pool, "   \n\t "));
            parent.AddChild(b);

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(2),
                "Whitespace-only TextRun between blocks must be dropped, not wrapped.");
            Assert.That(parent.Children[0], Is.SameAs(a));
            Assert.That(parent.Children[1], Is.SameAs(b));
            Assert.That(CountChildrenOfType<AnonymousBlockBox>(parent), Is.EqualTo(0),
                "No anonymous wrapper should be synthesised for a whitespace-only run.");
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 7. whitespace-only TextRun between non-blocks: no IFC promotion -

        [Test]
        public void Whitespace_only_textrun_between_inline_children_does_not_force_wrapping() {
            // When no block-level child exists the parent stays as an IFC
            // (the !anyBlock branch). Whitespace-only TextRuns are perfectly
            // legitimate IFC participants — no wrapping is needed here either,
            // since the wrap path is only entered when both block AND inline
            // children co-exist.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "p");
            parent.AddChild(NewInlineChild(pool, "span"));
            parent.AddChild(NewTextRun(pool, "   "));
            parent.AddChild(NewInlineChild(pool, "em"));

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(3),
                "Pure inline + whitespace stays an IFC — no wrappers added.");
            Assert.That(CountChildrenOfType<AnonymousBlockBox>(parent), Is.EqualTo(0));
            Assert.That(parent.ContainsInlines, Is.True,
                "Inline-only parent (whitespace included) establishes an IFC.");
        }

        // ----- 8. inline-block mixed with blocks: wraps per spec ---------------

        [Test]
        public void Inline_block_child_between_blocks_is_wrapped_like_other_inline_level_boxes() {
            // Per BoxFinalize.IsBlockLevel (lines 89-94): an inline-block
            // (BlockBox with IsInlineBlock=true) is NOT block-level, so it
            // joins the "inline run" group and gets pulled into an anonymous
            // wrapper alongside any sibling inlines/TextRuns.
            //
            // This matches CSS 2.1 §9.2.1.1: inline-block is an inline-level
            // box, so it participates in the IFC and triggers anonymous-block
            // wrapping when mixed with block-level siblings — same treatment
            // as InlineBox and TextRun. (The audit-task brief speculated the
            // opposite; the implementation and the spec agree on wrapping.)
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            var firstBlock = NewBlockChild(pool, "div");
            var inlineBlock = NewInlineBlockChild(pool, "div");
            var lastBlock = NewBlockChild(pool, "div");
            parent.AddChild(firstBlock);
            parent.AddChild(inlineBlock);
            parent.AddChild(lastBlock);

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            // Expected: [block, anon([inlineBlock]), block]
            Assert.That(parent.Children.Count, Is.EqualTo(3),
                "inline-block between blocks: leading + anon(inline-block) + trailing.");
            Assert.That(parent.Children[0], Is.SameAs(firstBlock));
            Assert.That(parent.Children[1], Is.InstanceOf<AnonymousBlockBox>(),
                "Per CSS 2.1 §9.2.1.1, inline-block is inline-level and must be wrapped.");
            Assert.That(parent.Children[2], Is.SameAs(lastBlock));
            var anon = (AnonymousBlockBox)parent.Children[1];
            Assert.That(anon.Children.Count, Is.EqualTo(1));
            Assert.That(anon.Children[0], Is.SameAs(inlineBlock));
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 9. empty parent: ContainsInlines = false ------------------------

        [Test]
        public void Empty_parent_clears_contains_inlines_flag() {
            // Defensive contract: an empty Children list short-circuits to
            // ContainsInlines=false at the top of FinalizeBlockChildren. Pin
            // it so a refactor that drops this fast-path can't silently leak
            // a stale `true` from a recycled BlockBox.
            var pool = NewPool();
            var scratch = NewScratch();
            var parent = NewBlockChild(pool, "div");
            parent.ContainsInlines = true; // simulate stale flag from prior use

            BoxFinalize.FinalizeBlockChildren(parent, pool, scratch);

            Assert.That(parent.Children.Count, Is.EqualTo(0));
            Assert.That(parent.ContainsInlines, Is.False);
        }

        // ----- 10. Flex container with raw text: anonymous flex item wrap ------

        [Test]
        public void Flex_parent_with_only_text_children_wraps_in_anonymous_flex_item() {
            // CSS Flexbox §4 (also documented in BoxFinalize:34-44): text
            // directly contained in a flex container is wrapped in an
            // anonymous flex item so FlexLayout (which only iterates
            // BlockBox children) sees a sized item rather than collapsing
            // the cross axis to the padding frame.
            var pool = NewPool();
            var scratch = NewScratch();
            var flex = pool.AllocateFlexBox();
            flex.Element = new Element("div");
            flex.AddChild(NewTextRun(pool, "Recover the Sun-Sigil"));

            BoxFinalize.FinalizeBlockChildren(flex, pool, scratch);

            Assert.That(flex.Children.Count, Is.EqualTo(1),
                "Raw text in a flex container must be wrapped in one anon flex item.");
            Assert.That(flex.Children[0], Is.InstanceOf<AnonymousBlockBox>(),
                "The synthesised flex item is an AnonymousBlockBox.");
            Assert.That(flex.ContainsInlines, Is.False,
                "After wrap, the flex container's direct children are block-level.");
        }
    }
}
