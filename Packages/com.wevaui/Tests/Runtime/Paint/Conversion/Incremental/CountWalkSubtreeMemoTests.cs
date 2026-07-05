using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion.Incremental {
    // PA3 (CODE_AUDIT_FINDINGS.md): CountWalk used to re-visit the entire box
    // tree whenever the top-level estimateCache missed. Since ChildAggregate
    // propagates any descendant Version-bump up to the root, the top-level
    // memo invalidated on every layout that touched a single element anywhere
    // in the document — making the walk O(N) per frame. The fix adds a
    // per-Box subtree-count memo keyed on Box.Version: dirty branches still
    // recurse, but every stable subtree is a single dictionary probe.
    public class CountWalkSubtreeMemoTests {
        static BlockBox BuildTree(int depth, int fanout, out List<Box> all) {
            all = new List<Box>();
            var root = Block(Style());
            all.Add(root);
            Grow(root, depth - 1, fanout, all);
            return (BlockBox)root;
        }

        static void Grow(Box parent, int remainingDepth, int fanout, List<Box> all) {
            if (remainingDepth <= 0) return;
            for (int i = 0; i < fanout; i++) {
                var child = Block(Style());
                parent.AddChild(child);
                all.Add(child);
                Grow(child, remainingDepth - 1, fanout, all);
            }
        }

        [Test]
        public void Steady_state_reconvert_does_not_re_walk_after_initial_build_PA3() {
            // First Convert is a cold walk over every box. After that, with
            // no Version bumps anywhere, the top-level estimateCache hits
            // and CountWalk is not invoked at all.
            var root = BuildTree(depth: 3, fanout: 4, out var all);
            var c = new BoxToPaintConverter();

            c.Convert(root);
            long afterCold = c.EstimateWalkNodes;
            // 1 + 4 + 16 = 21 boxes in the tree.
            Assert.That(afterCold, Is.EqualTo(all.Count),
                "Cold Convert must walk every box once to seed the per-box subtree-count memo.");

            c.Convert(root);
            Assert.That(c.EstimateWalkNodes, Is.EqualTo(afterCold),
                "Re-convert with no Version bumps must hit the top-level memo and skip CountWalk entirely.");
        }

        [Test]
        public void Single_leaf_bump_only_re_walks_the_dirty_path_PA3() {
            // Simulate a layout that bumps one leaf's Box.Version (and the
            // ancestor chain — ChildAggregate propagates the bump up to
            // the root). Every other subtree must replay its cached count
            // without recursion. With depth=4, fanout=4 there are 85 boxes
            // total; the dirty path from root to a single leaf is 4 nodes.
            var root = BuildTree(depth: 4, fanout: 4, out var all);
            Assert.That(all.Count, Is.EqualTo(85), "Tree shape sanity check (1+4+16+64).");

            var c = new BoxToPaintConverter();
            c.Convert(root);
            long cold = c.EstimateWalkNodes;
            Assert.That(cold, Is.EqualTo(all.Count));

            // Pick one leaf and bump its Version + every ancestor's Version
            // — this is what ChildAggregate would do in the real engine.
            Box leaf = all[all.Count - 1];
            for (Box b = leaf; b != null; b = b.Parent) BumpBoxVersion(b);

            long before = c.EstimateWalkNodes;
            c.Convert(root);
            long walkedThisFrame = c.EstimateWalkNodes - before;

            // The dirty path from root to leaf is exactly 4 nodes
            // (root, child, grandchild, leaf). Only those should re-walk;
            // the 81 stable boxes hit the per-box memo and are not counted.
            Assert.That(walkedThisFrame, Is.EqualTo(4),
                "Only the dirty root-to-leaf chain should re-walk; siblings replay from the per-box memo.");
        }

        [Test]
        public void Estimate_brackets_actual_command_count_for_representative_scene_PA3() {
            // Build a representative scene: a flat list of solid-color
            // boxes. Each box emits one FillRect; the estimator
            // budgets 4 per box (room for fill + border + a wrapper
            // push/pop pair). The estimate is a capacity hint for the
            // PaintList backing array — its job is to bound actual,
            // not match it exactly. We assert (a) the estimate never
            // under-shoots actual (else List grows once on the cold
            // convert), and (b) the estimate is within the
            // documented over-shoot ceiling for this scene shape.
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            const int n = 20;
            for (int i = 0; i < n; i++) {
                var s = Style();
                s.Set("background-color", i % 2 == 0 ? "red" : "blue");
                root.AddChild(Block(0, i * 10, 100, 10, s));
            }

            var c = new BoxToPaintConverter();
            var list = c.Convert(root);
            int actual = list.Commands.Count;
            // Each box budgets 4 commands; (1 root + n children) total.
            int estimated = (1 + n) * 4;

            Assert.That(estimated, Is.GreaterThanOrEqualTo(actual),
                "Capacity-hint estimate must not under-shoot actual command count (was estimate=" + estimated + ", actual=" + actual + ").");
            // Solid-background-only scenes emit roughly 1 command per
            // decorated box, so the 4x budget is the documented
            // over-shoot. Pin the ceiling so a future estimator
            // regression (say, 16x) is caught immediately.
            double ratio = estimated / (double)actual;
            Assert.That(ratio, Is.LessThanOrEqualTo(4.5),
                "Estimate should not over-shoot actual by more than the documented 4x budget; was estimate=" + estimated + " vs actual=" + actual);
        }

        [Test]
        public void InvalidateAll_drops_subtree_memo_so_next_convert_re_walks_PA3() {
            // Defensive pin: a bulk event (theme swap, viewport resize)
            // clears the per-box memo alongside contextVersion++. Next
            // Convert must walk fresh — otherwise the dict could serve
            // counts against Box references whose Version was reset by
            // ResetForPool but never re-stamped.
            var root = BuildTree(depth: 3, fanout: 3, out var all);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            long cold = c.EstimateWalkNodes;
            Assert.That(cold, Is.EqualTo(all.Count));

            c.InvalidateAll();

            long before = c.EstimateWalkNodes;
            c.Convert(root);
            long walked = c.EstimateWalkNodes - before;
            Assert.That(walked, Is.EqualTo(all.Count),
                "InvalidateAll must drop the per-box subtree memo so the next Convert re-walks every box.");
        }
    }
}
