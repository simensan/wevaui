using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Regression coverage for MS4: BoxToPaintConverter.subtreeSnapshots used
    // to retain references to Boxes forever — when LayoutPools recycled the
    // Box, the dictionary entry was never pruned, leaking the snapshot's
    // pooled UIQuadInstance[] AND risking a stale-snapshot replay on a
    // recycled Box that came back representing a different element.
    //
    // The fix subscribes the converter to the process-global Box.Recycled
    // event (fired from Box.ResetForPool which the pool's Recycle path
    // invokes for every retired box). These tests pin both the eviction-
    // on-recycle behavior and the regression-pin that in-use boxes keep
    // their snapshots.
    public class BoxSnapshotRecycleEvictionTests {
        sealed class FakeSnapshot : IBoxBatchSnapshot {
            public bool ContainsFilterScopes => false;
            public double AnchorX => 0;
            public double AnchorY => 0;
            public bool Recycled { get; private set; }
            public void Recycle() {
                Recycled = true;
            }
        }

        static BlockBox MakeRedBox() {
            var s = Style();
            s.Set("background-color", "red");
            return Block(0, 0, 50, 50, s);
        }

        [Test]
        public void Removed_subtree_box_evicts_its_snapshot_when_recycled() {
            // Parent owns a child Box whose subtree was previously captured.
            // The DOM subtree gets removed and the layout pool recycles the
            // child box — the converter must drop the snapshot entry and
            // recycle the snapshot's backing buffers.
            var parent = MakeRedBox();
            var child = MakeRedBox();
            parent.AddChild(child);

            var converter = new BoxToPaintConverter();
            var snap = new FakeSnapshot();
            converter.RegisterSubtreeSnapshot(child, snap);
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(1));
            Assert.That(converter.HasSubtreeSnapshot(child), Is.True);

            // Simulate "subtree removed from the DOM, layout pool returns
            // the orphaned box to the free list". LayoutPools.Recycle(box)
            // calls box.ResetForPool() which fires Box.Recycled. The
            // converter's handler must drop the entry AND recycle the
            // snapshot so its rented buffers go back to their pool.
            child.ResetForPool();

            Assert.That(converter.HasSubtreeSnapshot(child), Is.False,
                "Recycled-box snapshot must be evicted from subtreeSnapshots.");
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(0));
            Assert.That(snap.Recycled, Is.True,
                "Snapshot's pooled buffers must be returned on recycle, not GC'd.");
        }

        [Test]
        public void Reacquired_recycled_box_starts_with_no_stale_snapshot() {
            // Pool recycle semantics: the SAME Box instance gets handed back
            // out for a different subtree on a future layout pass. Without
            // recycle-eviction, that re-allocated Box would inherit the
            // previous tenant's snapshot and replay stale geometry. After
            // ResetForPool fires Box.Recycled, the dictionary entry must be
            // gone so the re-used instance is treated as a fresh capture
            // candidate by the next Convert.
            var box = MakeRedBox();

            var converter = new BoxToPaintConverter();
            var oldSnap = new FakeSnapshot();
            converter.RegisterSubtreeSnapshot(box, oldSnap);
            Assert.That(converter.HasSubtreeSnapshot(box), Is.True);

            // Recycle the box back to the pool — simulates LayoutPools.Recycle.
            box.ResetForPool();
            Assert.That(oldSnap.Recycled, Is.True);
            Assert.That(converter.HasSubtreeSnapshot(box), Is.False);

            // The Box instance is now re-acquired by a future layout for a
            // different element. The converter must NOT see a stale entry
            // when it asks "do I have a snapshot for this Box?". Capability
            // check: the re-used box is a fresh slate.
            box.Element = new Weva.Dom.Element("div");
            box.Style = Style(box.Element);
            box.X = 200; box.Y = 200; box.Width = 80; box.Height = 80;
            Assert.That(converter.HasSubtreeSnapshot(box), Is.False,
                "Re-used Box instance must not inherit the prior tenant's snapshot.");

            // And a fresh RegisterSubtreeSnapshot on the re-used box installs
            // the new snapshot cleanly without colliding with the dead entry.
            var newSnap = new FakeSnapshot();
            converter.RegisterSubtreeSnapshot(box, newSnap);
            Assert.That(converter.HasSubtreeSnapshot(box), Is.True);
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(1));
            Assert.That(newSnap.Recycled, Is.False,
                "The new snapshot must not be recycled — only the stale prior entry was.");
        }

        [Test]
        public void In_use_box_snapshot_is_retained_until_recycle() {
            // Regression pin: the recycle-eviction hook must NOT be so
            // aggressive that snapshots for in-use Boxes vanish. Without
            // this pin, an over-eager "drop everything that looks unused"
            // sweep on Convert would defeat the whole replay fast path.
            var boxA = MakeRedBox();
            var boxB = MakeRedBox();

            var converter = new BoxToPaintConverter();
            var snapA = new FakeSnapshot();
            var snapB = new FakeSnapshot();
            converter.RegisterSubtreeSnapshot(boxA, snapA);
            converter.RegisterSubtreeSnapshot(boxB, snapB);
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(2));

            // Recycle ONLY boxA. boxB's snapshot must be untouched — boxB
            // is still a live, addressable Box in the layout tree.
            boxA.ResetForPool();

            Assert.That(snapA.Recycled, Is.True);
            Assert.That(converter.HasSubtreeSnapshot(boxA), Is.False);

            Assert.That(snapB.Recycled, Is.False,
                "Recycling boxA must not recycle unrelated snapshots.");
            Assert.That(converter.HasSubtreeSnapshot(boxB), Is.True,
                "boxB is still in use; its snapshot must stay in the dictionary.");
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(1));
        }

        [Test]
        public void Recycle_with_no_registered_snapshot_is_a_no_op() {
            // Defensive: a Box that never had a snapshot deposited should
            // not throw / mis-count when it goes through the recycle path.
            // (Most boxes never reach the capture window — TextRuns, scroll
            // containers, anonymous wrappers etc are all suppressed.)
            var box = MakeRedBox();
            var converter = new BoxToPaintConverter();
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(0));

            Assert.DoesNotThrow(() => box.ResetForPool());
            Assert.That(converter.SubtreeSnapshotCount, Is.EqualTo(0));
        }
    }
}
