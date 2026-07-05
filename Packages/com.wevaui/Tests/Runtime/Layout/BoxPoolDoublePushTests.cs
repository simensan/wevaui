using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;

namespace Weva.Tests.Layout {
    // Regression for the BoxPool double-push (audit C6): a box that was
    // Recycle()d mid-pass is still in the pool's allocated[] live set, so
    // EndPass pushed the SAME instance onto its free list a second time —
    // two subsequent Allocate* calls then handed out one Box for two tree
    // positions, silently entangling their state. Trigger sites:
    // LineClampHelper's mid-pass recycles and the scroll-boundary graft
    // (LayoutEngine.ScrollReuse), which is on by default.
    public class BoxPoolDoublePushTests {
        [Test]
        public void Mid_pass_recycle_plus_endpass_does_not_hand_one_box_to_two_callers() {
            var pool = new BoxPool();
            pool.BeginPass();

            var victim = pool.AllocateBlockBox();
            // Mid-pass discard (the LineClampHelper / scroll-graft pattern).
            pool.Recycle(victim);
            // End of pass: victim is in allocated[] and not a survivor, so the
            // pre-fix EndPass pushed it onto the free list AGAIN.
            pool.EndPass(survivedRoot: null);

            pool.BeginPass();
            var first = pool.AllocateBlockBox();
            var second = pool.AllocateBlockBox();
            Assert.That(ReferenceEquals(first, second), Is.False,
                "the same Box instance was handed out for two tree positions — " +
                "double-pushed onto the free list by mid-pass Recycle + EndPass");
            pool.EndPass(survivedRoot: null);
        }

        [Test]
        public void Mid_pass_recycle_plus_endpass_by_allocated_parent_does_not_double_push() {
            var pool = new BoxPool();
            pool.BeginPass();

            var victim = pool.AllocateTextRun();
            pool.Recycle(victim);
            // The subtree-splice variant: victim has Parent == null after
            // ResetForPool, so the pre-fix path also re-pushed it here.
            pool.EndPassByAllocatedParent();

            pool.BeginPass();
            var first = pool.AllocateTextRun();
            var second = pool.AllocateTextRun();
            Assert.That(ReferenceEquals(first, second), Is.False,
                "the same TextRun instance was handed out twice after a " +
                "mid-pass Recycle + EndPassByAllocatedParent double-push");
            pool.EndPassByAllocatedParent();
        }

        [Test]
        public void Recycled_box_is_still_reusable_once() {
            // The guard must not leak boxes: one Recycle = one slot on the
            // free list = exactly one reuse.
            var pool = new BoxPool();
            pool.BeginPass();
            var victim = pool.AllocateBlockBox();
            pool.Recycle(victim);
            pool.EndPass(survivedRoot: null);

            pool.BeginPass();
            var reused = pool.AllocateBlockBox();
            Assert.That(ReferenceEquals(reused, victim), Is.True,
                "the recycled box should be reused (pooling still works)");
            pool.EndPass(survivedRoot: null);
        }

        [Test]
        public void Survivor_then_recycle_next_pass_still_pools_correctly() {
            // A box that survives pass 1 (InFreeList=false), then is recycled
            // in pass 2, must land on the free list exactly once.
            var pool = new BoxPool();
            pool.BeginPass();
            var root = pool.AllocateBlockBox();
            var child = pool.AllocateBlockBox();
            root.ChildList.Add(child);
            child.Parent = root;
            pool.EndPass(survivedRoot: root);

            pool.BeginPass();
            // Pass 2 discards the child mid-pass and ends with only root.
            root.ChildList.Clear();
            pool.Recycle(child);
            pool.EndPass(survivedRoot: root);

            pool.BeginPass();
            var first = pool.AllocateBlockBox();
            var second = pool.AllocateBlockBox();
            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(ReferenceEquals(first, child) || ReferenceEquals(second, child), Is.True,
                "the recycled child should be available for reuse exactly once");
            pool.EndPass(survivedRoot: null);
        }
    }
}
