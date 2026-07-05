using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Reactive;
using static Weva.Tests.Layout.Incremental.IncrementalLayoutTestHelpers;

namespace Weva.Tests.Layout.Incremental {
    public class LayoutSubtreeIncrementalTests {
        [Test]
        public void Hover_changes_color_only_skips_layout_entirely() {
            // Paint-only hover rule: IncrementalLayoutGate skips layout, so
            // SubtreeSkipHits should NOT increment (we never even tried the
            // subtree path; the gate skipped at the top).
            var h = Build(
                "<div id=\"r\"><span id=\"a\">x</span></div>",
                "span:hover { color: red; }");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SkipCount, Is.EqualTo(1));
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
        }

        [Test]
        public void Layout_dirty_on_block_descendant_takes_subtree_path() {
            // A leaf block element with explicit Layout dirty AND a stable
            // parent (block-flow, not flex/grid) should trigger the subtree
            // relayout path instead of a full Layout pass.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div><div id=\"b\"></div></div>",
                "div { padding: 4px; }");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
            Assert.That(h.Engine.SkipCount, Is.EqualTo(0));
        }

        [Test]
        public void Sibling_unchanged_after_subtree_skip_keeps_position() {
            // After subtree-only relayout of element a, sibling b's box
            // instance must be the same and its X/Y unchanged.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div><div id=\"b\"></div></div>");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));
            double bx1 = b1.X, by1 = b1.Y;

            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b2 = FindBoxFor(r2, h.Doc.GetElementById("b"));
            Assert.That(b2, Is.SameAs(b1));
            Assert.That(b2.X, Is.EqualTo(bx1));
            Assert.That(b2.Y, Is.EqualTo(by1));
        }

        [Test]
        public void Text_change_inside_inline_normalizes_to_containing_block() {
            var h = Build(
                "<div id=\"r\"><p id=\"p\"><span id=\"s\">1</span></p><div id=\"b\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();

            var tracker = new InvalidationTracker();
            tracker.Attach(h.Doc);
            tracker.Clear();
            var text = (TextNode)h.Doc.GetElementById("s").Children[0];
            text.Data = "2";

            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
            Assert.That(TextRunContent(FindBoxFor(r2, h.Doc.GetElementById("p"))), Is.EqualTo("2"));
        }

        [Test]
        public void Text_change_that_changes_outer_height_falls_back_to_full_layout() {
            var h = Build(
                "<div id=\"r\"><p id=\"p\">short</p><div id=\"b\" style=\"height: 10px\"></div></div>",
                "#p { width: 40px; margin: 0; }",
                viewportWidth: 120);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));
            double beforeY = b1.Y;
            h.Engine.ResetCacheStats();

            var tracker = new InvalidationTracker();
            tracker.Attach(h.Doc);
            tracker.Clear();
            var text = (TextNode)h.Doc.GetElementById("p").Children[0];
            text.Data = "one two three four five six";

            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b2 = FindBoxFor(r2, h.Doc.GetElementById("b"));
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
            Assert.That(b2.Y, Is.GreaterThan(beforeY));
        }

        [Test]
        public void Subtree_skip_returns_same_root_instance() {
            // Subtree relayout splices new boxes into lastRoot but returns
            // the SAME root reference — same instance the caller saw last
            // frame. PaintCache keyed off Box references must still be valid.
            var h = Build("<div id=\"r\"><div id=\"a\"></div></div>");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(r2, Is.SameAs(r1));
        }

        [Test]
        public void Subtree_skip_clears_cached_from_last_frame_flag() {
            // The subtree path re-runs layout on the dirty subtree (and the
            // root-level positioning pass), so the returned root is NOT a
            // pure cache return. The flag must read false, matching the
            // existing v0.7 contract that fresh-layout passes clear it.
            var h = Build("<div id=\"r\"><div id=\"a\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(r2.IsCachedFromLastFrame, Is.False);
        }

        [Test]
        public void Structure_dirty_falls_through_to_full_layout() {
            // Structure invalidation means the tree topology may have
            // changed; the subtree-skip predicate cannot safely splice. Must
            // fall through to full Layout.
            var h = Build("<div id=\"r\"><div id=\"a\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout | InvalidationKind.Structure);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
        }

        [Test]
        public void Text_change_inside_stable_flex_item_takes_subtree_path() {
            // Repeated quest perf readout updates should keep using the local
            // subtree path and must not corrupt parent links between frames.
            var h = Build(
                "<div id=\"r\" style=\"display:flex\"><div id=\"chip\" style=\"display:flex;min-width:72px;height:24px\"><span id=\"value\">11</span><span>ms</span></div><div id=\"b\" style=\"width:10px;height:10px\"></div></div>");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));
            double bx1 = b1.X, by1 = b1.Y;
            h.Engine.ResetCacheStats();

            var text = (TextNode)h.Doc.GetElementById("value").Children[0];
            var tracker = new InvalidationTracker();
            tracker.Attach(h.Doc);
            tracker.Clear();

            for (int i = 0; i < 5; i++) {
                string next = (12 + i).ToString();
                text.Data = next;
                var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
                var b2 = FindBoxFor(r2, h.Doc.GetElementById("b"));
                Assert.That(b2, Is.SameAs(b1));
                Assert.That(b2.X, Is.EqualTo(bx1));
                Assert.That(b2.Y, Is.EqualTo(by1));
                Assert.That(TextRunContent(FindBoxFor(r2, h.Doc.GetElementById("chip"))), Is.EqualTo(next + "ms"));
                tracker.Clear();
            }
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(5));
        }

        [Test]
        public void Flex_parent_size_change_reflows_parent_subtree() {
            var h = Build(
                "<div id=\"r\" style=\"display:flex\"><div id=\"a\"></div><div id=\"b\"></div></div>",
                "#a { width: 20px; height: 10px; } #a.wide { width: 80px; } #b { width: 10px; height: 10px; }");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));
            double beforeX = b1.X;
            h.Engine.ResetCacheStats();
            h.Doc.GetElementById("a").SetAttribute("class", "wide");
            h.Recompute();

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout | InvalidationKind.Style);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b2 = FindBoxFor(r2, h.Doc.GetElementById("b"));

            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
            Assert.That(b2.X, Is.GreaterThan(beforeX));
        }

        [Test]
        public void Grid_parent_size_change_reflows_parent_subtree() {
            // Grid item size changes need the parent track-sizing pass, but
            // that pass can be scoped to the grid parent when the grid's own
            // external allocation stays stable.
            var h = Build(
                "<div id=\"r\" style=\"display:grid\"><div id=\"a\"></div><div id=\"b\"></div></div>",
                "#a { width: 20px; height: 10px; } #a.wide { width: 80px; } #b { width: 10px; height: 10px; }");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            h.Doc.GetElementById("a").SetAttribute("class", "wide");
            h.Recompute();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout | InvalidationKind.Style);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
        }

        [Test]
        public void Layout_change_inside_grid_stretched_scroll_region_promotes_to_stable_ancestor() {
            var h = Build(
                "<div id=\"r\"><section id=\"scroller\"><div id=\"a\" class=\"item\"></div><div id=\"b\" class=\"item\"></div></section></div>",
                "#r { display: grid; width: 400px; height: 300px; grid-template-rows: 1fr; } " +
                "#scroller { display: flex; flex-direction: column; overflow-y: auto; min-height: 0; } " +
                ".item { height: 20px; } #a.big { padding-top: 8px; padding-bottom: 8px; }");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var scroller1 = (BlockBox)FindBoxFor(r1, h.Doc.GetElementById("scroller"));
            var a1 = (BlockBox)FindBoxFor(r1, h.Doc.GetElementById("a"));
            var b1 = (BlockBox)FindBoxFor(r1, h.Doc.GetElementById("b"));
            double stableHeight = scroller1.Height;
            double aHeightBefore = a1.Height;
            double bYBefore = b1.Y;
            h.Engine.ResetCacheStats();

            var a = h.Doc.GetElementById("a");
            a.SetAttribute("class", "item big");
            h.Recompute();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(a, InvalidationKind.Layout | InvalidationKind.Style);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var scroller2 = (BlockBox)FindBoxFor(r2, h.Doc.GetElementById("scroller"));
            var a2 = (BlockBox)FindBoxFor(r2, h.Doc.GetElementById("a"));
            var b2 = (BlockBox)FindBoxFor(r2, h.Doc.GetElementById("b"));

            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
            Assert.That(r2, Is.SameAs(r1));
            Assert.That(scroller2.Height, Is.EqualTo(stableHeight).Within(0.5));
            Assert.That(a2.Height, Is.GreaterThan(aHeightBefore));
            Assert.That(b2.Y, Is.GreaterThan(bYBefore));
        }

        [Test]
        public void Two_dirty_subtrees_each_relaid() {
            // Two non-overlapping dirty elements both processed on one
            // subtree-skip frame. The hit counter increments by exactly 1 —
            // the call took the subtree path overall, even though it
            // visited two subtrees.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            tracker.MarkDirty(h.Doc.GetElementById("c"), InvalidationKind.Layout);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(1));
        }

        [Test]
        public void Cold_layout_does_not_take_subtree_path() {
            // Subtree skip requires lastRoot from a prior pass. Cold start
            // (no lastRoot) must always run a full Layout.
            var h = Build("<div id=\"a\"></div>");
            h.Engine.ResetCacheStats();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
        }

        [Test]
        public void Subtree_relayout_picks_up_new_padding() {
            // Mutate the element's computed style padding and verify the
            // subtree relayout produces a box with the new padding. This
            // guarantees the path runs the layout algorithm — it doesn't
            // just reuse the stale geometry.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                ".big { padding: 20px; }");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a1 = FindBoxFor(r1, h.Doc.GetElementById("a"));
            double padBefore = a1.PaddingTop;

            h.Doc.GetElementById("a").SetAttribute("class", "big");
            h.Recompute();
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var a2 = FindBoxFor(r2, h.Doc.GetElementById("a"));
            Assert.That(a2.PaddingTop, Is.GreaterThan(padBefore));
            Assert.That(a2.PaddingTop, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Subtree_relayout_does_not_affect_unrelated_subtree() {
            // Two sibling subtrees; only one is dirty. The other's box is
            // verbatim retained — no version bump, same instance.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div><div id=\"b\"><span id=\"bs\">child</span></div></div>");
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));
            long bv1 = b1.Version;

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b2 = FindBoxFor(r2, h.Doc.GetElementById("b"));
            Assert.That(b2, Is.SameAs(b1));
            Assert.That(b2.Version, Is.EqualTo(bv1));
        }

        [Test]
        public void Null_tracker_falls_through_to_full_layout() {
            // No tracker = null — the gate doesn't engage and neither does
            // the subtree path. A full layout runs.
            var h = Build("<div id=\"a\"></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, null);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
            Assert.That(h.Engine.SkipCount, Is.EqualTo(0));
        }

        [Test]
        public void Subtree_skip_hits_increment_across_calls() {
            // SubtreeSkipHits is a monotonic counter. Three subtree-only
            // toggles → three increments.
            var h = Build("<div id=\"r\"><div id=\"a\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();
            for (int i = 0; i < 3; i++) {
                var tracker = new InvalidationTracker();
                tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
                h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            }
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(3));
        }

        [Test]
        public void Reset_subtree_skip_stats_zeroes_counter() {
            var h = Build("<div id=\"r\"><div id=\"a\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.GreaterThan(0));
            h.Engine.ResetCacheStats();
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
        }

        static string TextRunContent(Box box) {
            if (box is TextRun tr) return tr.Text;
            string text = "";
            for (int i = 0; i < box.Children.Count; i++) {
                text += TextRunContent(box.Children[i]);
            }
            return text;
        }
    }
}
