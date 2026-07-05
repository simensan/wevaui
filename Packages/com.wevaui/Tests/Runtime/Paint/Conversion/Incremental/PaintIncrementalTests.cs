using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Reactive;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion.Incremental {
    public class PaintIncrementalTests {
        static BlockBox SimpleRedBox() {
            var s = Style();
            s.Set("background-color", "red");
            return Block(0, 0, 50, 50, s);
        }

        static BlockBox MakeFlatTree(int childCount, out List<Element> elements) {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            elements = new List<Element> { rootStyle.Element };
            for (int i = 0; i < childCount; i++) {
                var cs = Style();
                cs.Set("background-color", i % 2 == 0 ? "red" : "blue");
                var c = Block(0, i * 10, 100, 10, cs);
                root.AddChild(c);
                elements.Add(cs.Element);
            }
            return root;
        }

        [Test]
        public void Cold_convert_misses_for_every_cacheable_box() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(1));
            Assert.That(c.CacheHits, Is.EqualTo(0));
            Assert.That(c.CacheSize, Is.EqualTo(1));
        }

        [Test]
        public void Re_convert_with_no_changes_hits_for_every_box() {
            var s = Style();
            s.Set("background-color", "red");
            var root = Block(0, 0, 100, 100, s);
            var child = Block(0, 0, 50, 50, Style());
            child.Style.Set("background-color", "blue");
            root.AddChild(child);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            c.ResetCacheStats();

            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(0));
            Assert.That(c.CacheHits, Is.GreaterThanOrEqualTo(1),
                "Top-level box should hit; descendants are inlined into the parent's slice.");
        }

        [Test]
        public void Hit_count_rises_with_repeated_Convert() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            c.Convert(root);
            long h0 = c.CacheHits;
            c.Convert(root);
            long h1 = c.CacheHits;
            c.Convert(root);
            long h2 = c.CacheHits;
            Assert.That(h1, Is.GreaterThan(h0));
            Assert.That(h2, Is.GreaterThan(h1));
        }

        [Test]
        public void Box_with_style_change_regenerates_siblings_reuse() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 100, 50, aStyle);
            var b = Block(0, 50, 100, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Change `a`'s style by giving it a fresh ComputedStyle with a new Version.
            var aStyle2 = CloneWithNewVersion(aStyle, ("background-color", "green"));
            a.Style = aStyle2;
            BumpBoxVersion(a);
            BumpBoxVersion(root); // root's slice contains a's slice, so root must re-emit too.

            c.ResetCacheStats();
            c.Convert(root);
            // Root miss (its slice changed because child a changed) + a miss. b should hit.
            Assert.That(c.CacheMisses, Is.GreaterThanOrEqualTo(2));
            Assert.That(c.CacheHits, Is.GreaterThanOrEqualTo(1), "Sibling b should still be cached.");
        }

        [Test]
        public void Sibling_change_does_not_invalidate_unchanged_box() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 100, 50, aStyle);
            var b = Block(0, 50, 100, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);

            long aVersionBefore = a.Version;
            long aStyleVersionBefore = a.Style.Version;

            // Mutate b's style.
            b.Style = CloneWithNewVersion(bStyle, ("background-color", "yellow"));
            BumpBoxVersion(b);

            // a's box version and style version are unchanged.
            Assert.That(a.Version, Is.EqualTo(aVersionBefore));
            Assert.That(a.Style.Version, Is.EqualTo(aStyleVersionBefore));

            // a's cache entry is still present and would still hit if asked directly,
            // even though we don't currently route through it (root re-emits).
            // Verify by invalidating root + b only, then converting just `a`.
            c.Invalidate(root);
            c.Invalidate(b);
            c.ResetCacheStats();
            c.Convert(a);
            Assert.That(c.CacheHits, Is.EqualTo(1), "a's cache entry must survive sibling b's change.");
        }

        [Test]
        public void Invalidate_box_drops_only_that_entry() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var a = Block(0, 0, 100, 50, aStyle);
            root.AddChild(a);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            c.Invalidate(a);
            Assert.That(c.CacheSize, Is.EqualTo(sizeBefore - 1));
        }

        [Test]
        public void InvalidateSubtree_drops_root_and_descendants() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 100, 50, aStyle);
            var b = Block(0, 0, 100, 50, bStyle);
            a.AddChild(b);
            root.AddChild(a);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            c.InvalidateSubtree(a);
            // Only root remains.
            Assert.That(c.CacheSize, Is.EqualTo(1));
        }

        [Test]
        public void InvalidateAll_empties_the_cache() {
            var root = MakeFlatTree(5, out _);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            Assert.That(c.CacheSize, Is.GreaterThan(0));
            c.InvalidateAll();
            Assert.That(c.CacheSize, Is.EqualTo(0));
        }

        [Test]
        public void Apply_document_level_dirty_invalidates_all_entries() {
            var root = MakeFlatTree(5, out _);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            Assert.That(c.CacheSize, Is.GreaterThan(0));
            long contextBefore = c.ContextVersion;

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Document(), InvalidationKind.Layout | InvalidationKind.Paint);
            c.Apply(tracker, e => FindBox(root, e));

            Assert.That(c.CacheSize, Is.EqualTo(0));
            Assert.That(c.ContextVersion, Is.GreaterThan(contextBefore));
        }

        [Test]
        public void Scroll_container_ancestor_snapshot_does_not_replay_over_fresh_scroll_transform() {
            var root = Block(0, 0, 200, 200, Style());
            var scrollerStyle = Style();
            scrollerStyle.Set("overflow-y", "auto");
            var scroller = Block(0, 0, 100, 80, scrollerStyle);
            var childStyle = Style();
            childStyle.Set("background-color", "red");
            var child = Block(0, 0, 100, 200, childStyle);
            scroller.AddChild(child);
            root.AddChild(scroller);

            var scroll = new ScrollContainer();
            var state = scroll.GetOrCreate(scroller);
            state.OverflowY = ScrollOverflow.Auto;
            state.ViewportHeight = 80;
            state.ScrollHeight = 200;
            state.ScrollY = 40;

            var converter = new BoxToPaintConverter();
            converter.RegisterSubtreeSnapshot(root, new FakeSnapshot());
            var list = converter.Convert(root, null, null, scroll, null);

            Assert.That(ContainsType<ReplaySubtreeSnapshotCommand>(list.Commands), Is.False,
                "An ancestor snapshot that contains a scroll container would replay stale baked child positions.");
            Assert.That(ContainsType<PushTransformCommand>(list.Commands), Is.True,
                "The scroll wrapper must be emitted fresh so the current ScrollY reaches rendering.");
        }

        [Test]
        public void Scrolled_content_subtree_snapshot_does_not_replay_under_scroll_transform() {
            var root = Block(0, 0, 200, 200, Style());
            var scrollerStyle = Style();
            scrollerStyle.Set("overflow-y", "auto");
            var scroller = Block(0, 0, 100, 80, scrollerStyle);
            var childStyle = Style();
            childStyle.Set("background-color", "red");
            var child = Block(0, 0, 100, 20, childStyle);
            scroller.AddChild(child);
            root.AddChild(scroller);

            var scroll = new ScrollContainer();
            var state = scroll.GetOrCreate(scroller);
            state.OverflowY = ScrollOverflow.Auto;
            state.ViewportHeight = 80;
            state.ScrollHeight = 200;
            state.ScrollY = 40;

            var converter = new BoxToPaintConverter();
            converter.RegisterSubtreeSnapshot(child, new FakeSnapshot());
            var list = converter.Convert(root, null, null, scroll, null);

            Assert.That(ContainsType<ReplaySubtreeSnapshotCommand>(list.Commands), Is.False,
                "Retained scrolled-content snapshots can carry stale screen-space clip rects.");
            Assert.That(ContainsType<BeginSubtreeCaptureCommand>(list.Commands), Is.False,
                "Do not capture descendants while a scroll transform is active.");
        }

        [Test]
        public void Apply_drops_entries_for_elements_marked_Paint() {
            var root = MakeFlatTree(3, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[1], InvalidationKind.Paint);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.LessThan(sizeBefore));
        }

        [Test]
        public void Apply_keeps_decoration_cache_for_Composite_only_invalidation() {
            // Composite-only marks come from wrapper-only animation ticks
            // (transform / opacity) and the tracker's ancestor propagation.
            // Neither changes the element's DECORATION output — wrappers are
            // re-resolved fresh on every VisitBox — so the PaintBoxCache must
            // SURVIVE. (The old behavior dropped it, which forced a full
            // EmitDecorations rebuild per animated element per frame:
            // particles.html paid 420 radial-gradient rebuilds every frame.)
            var root = MakeFlatTree(3, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[2], InvalidationKind.Composite);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.EqualTo(sizeBefore),
                "Composite-only invalidation must not drop cached decoration commands");
        }

        [Test]
        public void Apply_drops_cache_when_Composite_combines_with_Paint() {
            // Composite + Paint together (e.g. a hover that also bubbles a
            // composite mark) must still stale the cache via the Paint bit.
            var root = MakeFlatTree(3, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[2], InvalidationKind.Composite | InvalidationKind.Paint);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.LessThan(sizeBefore));
        }

        [Test]
        public void Apply_drops_entries_for_Layout_invalidation() {
            var root = MakeFlatTree(3, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[3], InvalidationKind.Layout);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.LessThan(sizeBefore));
        }

        [Test]
        public void Apply_drops_entries_for_Style_invalidation() {
            var root = MakeFlatTree(2, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[1], InvalidationKind.Style);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.LessThan(sizeBefore));
        }

        [Test]
        public void Apply_drops_entries_for_Structure_invalidation() {
            var root = MakeFlatTree(2, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[1], InvalidationKind.Structure);
            c.Apply(tracker, e => FindBox(root, e));
            Assert.That(c.CacheSize, Is.LessThan(sizeBefore));
        }

        [Test]
        public void Apply_with_null_tracker_or_lookup_is_no_op() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int before = c.CacheSize;
            c.Apply(null, _ => null);
            c.Apply(new InvalidationTracker(), null);
            Assert.That(c.CacheSize, Is.EqualTo(before));
        }

        [Test]
        public void Cache_hits_emit_identical_command_sequences_to_from_scratch() {
            var s = Style();
            s.Set("background-color", "red");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "2px");
            s.Set("border-top-color", "black");
            s.Set("opacity", "0.7");
            var root = Block(0, 0, 100, 50, s);

            var oracle = new BoxToPaintConverter();
            var oracleCmds = oracle.Convert(root).Commands;

            var c = new BoxToPaintConverter();
            c.Convert(root);
            var hit = c.Convert(root).Commands;

            Assert.That(hit.Count, Is.EqualTo(oracleCmds.Count));
            for (int i = 0; i < hit.Count; i++) {
                Assert.That(hit[i].GetType(), Is.EqualTo(oracleCmds[i].GetType()),
                    "Command type mismatch at index " + i);
            }
        }

        [Test]
        public void Hit_rate_above_95_percent_for_100_box_tree_with_one_change() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var children = new List<Box>();
            for (int i = 0; i < 100; i++) {
                var s = Style();
                s.Set("background-color", "red");
                var b = Block(i % 100, (i / 100) * 10, 10, 10, s);
                root.AddChild(b);
                children.Add(b);
            }

            var c = new BoxToPaintConverter();
            c.Convert(root);
            // Mutate one child.
            var target = children[42];
            target.Style = CloneWithNewVersion(target.Style, ("background-color", "blue"));
            BumpBoxVersion(target);

            c.ResetCacheStats();
            // Re-converting from `root` always misses root (root.Version unchanged but
            // the stored slice contains the mutated child's old commands; we'd need
            // root.Version bumped for correctness — the layout engine does that in
            // production. Here we explicitly bump root to mirror that behavior).
            BumpBoxVersion(root);
            // Convert each child individually so we measure cache hits at that level
            // (root's slice would otherwise inline everyone and only count one miss).
            foreach (var ch in children) {
                c.Convert(ch);
            }

            long total = c.CacheHits + c.CacheMisses;
            Assert.That(total, Is.GreaterThanOrEqualTo(100));
            double rate = (double)c.CacheHits / total;
            Assert.That(rate, Is.GreaterThanOrEqualTo(0.95),
                "Expected >=95% hit rate after a single-child change, got " + rate);
        }

        [Test]
        public void Cached_slice_includes_push_pop_for_opacity_transform_clip() {
            var s = Style();
            s.Set("transform", "translate(5px,5px)");
            s.Set("opacity", "0.5");
            s.Set("overflow", "hidden");
            s.Set("background-color", "red");
            var root = Block(0, 0, 100, 100, s);

            var c = new BoxToPaintConverter();
            var first = c.Convert(root).Commands;
            var second = c.Convert(root).Commands;

            Assert.That(first.Count, Is.EqualTo(second.Count));
            // Verify both PushXxx and PopXxx are present in both runs.
            Assert.That(ContainsType<PushTransformCommand>(first), Is.True);
            Assert.That(ContainsType<PushOpacityCommand>(first), Is.True);
            Assert.That(ContainsType<PushClipCommand>(first), Is.True);
            Assert.That(ContainsType<PopTransformCommand>(first), Is.True);
            Assert.That(ContainsType<PopOpacityCommand>(first), Is.True);
            Assert.That(ContainsType<PopClipCommand>(first), Is.True);
            // Same for the cached run.
            Assert.That(ContainsType<PushTransformCommand>(second), Is.True);
            Assert.That(ContainsType<PopTransformCommand>(second), Is.True);
            Assert.That(ContainsType<PushOpacityCommand>(second), Is.True);
            Assert.That(ContainsType<PopOpacityCommand>(second), Is.True);
            Assert.That(ContainsType<PushClipCommand>(second), Is.True);
            Assert.That(ContainsType<PopClipCommand>(second), Is.True);
        }

        [Test]
        public void Empty_visible_commands_box_still_gets_cached() {
            var s = Style(); // no decorations at all
            var root = Block(0, 0, 100, 100, s);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            Assert.That(c.CacheSize, Is.EqualTo(1));
            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheHits, Is.EqualTo(1));
            Assert.That(c.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Re_convert_PaintList_equals_from_scratch_oracle() {
            var rootStyle = Style();
            var root = Block(0, 0, 200, 200, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var a = Block(0, 0, 100, 50, aStyle);
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var b = Block(0, 50, 100, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var oracle = new BoxToPaintConverter();
            var oracleCmds = oracle.Convert(root).Commands;

            var c = new BoxToPaintConverter();
            c.Convert(root);
            var cached = c.Convert(root).Commands;

            Assert.That(cached.Count, Is.EqualTo(oracleCmds.Count));
            for (int i = 0; i < cached.Count; i++) {
                Assert.That(cached[i].GetType(), Is.EqualTo(oracleCmds[i].GetType()));
            }
        }

        [Test]
        public void Invalidate_on_never_cached_box_is_no_op() {
            var c = new BoxToPaintConverter();
            var s = Style();
            var orphan = Block(0, 0, 10, 10, s);
            // Should not throw, should not change size from 0.
            c.Invalidate(orphan);
            c.InvalidateSubtree(orphan);
            Assert.That(c.CacheSize, Is.EqualTo(0));
        }

        [Test]
        public void Invalidate_null_box_is_no_op() {
            var c = new BoxToPaintConverter();
            c.Invalidate(null);
            c.InvalidateSubtree(null);
            Assert.That(c.CacheSize, Is.EqualTo(0));
        }

        [Test]
        public void Convert_with_tracker_applies_invalidation_before_walking() {
            var root = MakeFlatTree(3, out var elements);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            int sizeBefore = c.CacheSize;

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(elements[2], InvalidationKind.Paint);

            c.ResetCacheStats();
            c.Convert(root, tracker, e => FindBox(root, e), null, null);

            // Box-local-coords cache contract: only the element marked Paint loses
            // its cache; ancestors and siblings are unaffected because each box's
            // own decoration commands are independent of where it ends up
            // positioned absolutely on screen. So we expect exactly one miss
            // (the dirtied child) and the remaining boxes hit.
            Assert.That(c.CacheMisses, Is.EqualTo(1));
            Assert.That(c.CacheHits, Is.GreaterThanOrEqualTo(1),
                "Root + unaffected siblings should still hit on the new Convert.");
        }

        [Test]
        public void Dirty_subtree_evicts_only_intersecting_batch_snapshots() {
            var rootStyle = Style();
            var root = Block(0, 0, 200, 100, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 50, 50, aStyle);
            var b = Block(0, 50, 50, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);

            var snapA = new FakeSnapshot();
            var snapB = new FakeSnapshot();
            c.RegisterSubtreeSnapshot(a, snapA);
            c.RegisterSubtreeSnapshot(b, snapB);

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(a.Element, InvalidationKind.Paint);
            var list = c.Convert(root, tracker, e => FindBox(root, e), null, null);

            Assert.That(snapA.Recycled, Is.True, "Dirty branch snapshot must be dropped.");
            Assert.That(snapB.Recycled, Is.False, "Unrelated branch snapshot should stay retained.");
            Assert.That(ContainsType<ReplaySubtreeSnapshotCommand>(list.Commands), Is.True,
                "Unrelated branch should replay from its retained snapshot.");
        }

        [Test]
        public void Layout_dirty_evicts_all_batch_snapshots() {
            var rootStyle = Style();
            var root = Block(0, 0, 200, 100, rootStyle);
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 50, 50, aStyle);
            var b = Block(0, 50, 50, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);

            var snapA = new FakeSnapshot();
            var snapB = new FakeSnapshot();
            c.RegisterSubtreeSnapshot(a, snapA);
            c.RegisterSubtreeSnapshot(b, snapB);

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(a.Element, InvalidationKind.Layout);
            var list = c.Convert(root, tracker, e => FindBox(root, e), null, null);

            Assert.That(snapA.Recycled, Is.True);
            Assert.That(snapB.Recycled, Is.True,
                "Layout changes can move siblings, so retained batch chunks need a fresh capture boundary.");
            Assert.That(ContainsType<ReplaySubtreeSnapshotCommand>(list.Commands), Is.False);
        }

        [Test]
        public void Convert_called_multiple_times_cache_survives_across_calls() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            for (int i = 0; i < 5; i++) c.Convert(root);
            Assert.That(c.CacheSize, Is.EqualTo(1));
            Assert.That(c.CacheHits, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void Display_none_box_does_not_populate_cache() {
            var s = Style();
            s.Set("display", "none");
            s.Set("background-color", "red");
            var root = Block(0, 0, 50, 50, s);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            Assert.That(c.CacheSize, Is.EqualTo(0));
        }

        [Test]
        public void TextRun_consumes_its_own_cache_slot() {
            var rootStyle = Style();
            var root = Block(0, 0, 200, 100, rootStyle);
            var trStyle = Style();
            trStyle.Set("color", "red");
            var tr = new TextRun("hi", trStyle, trStyle.Element, null);
            tr.X = 0; tr.Y = 0; tr.Width = 20; tr.Height = 16;
            root.AddChild(tr);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            // Root caches its own decoration slice; the TextRun caches its
            // own DrawTextCommands at box-local origin so a transform-only
            // tick on the parent can skip the per-frame font / decoration /
            // shadow resolution that EmitTextRunLocal performs on miss.
            Assert.That(c.CacheSize, Is.EqualTo(2));
        }

        [Test]
        public void Text_subtree_never_replays_retained_batch_snapshot() {
            var rootStyle = Style();
            var root = Block(0, 0, 200, 100, rootStyle);
            var trStyle = Style();
            trStyle.Set("color", "red");
            var tr = new TextRun("hover text", trStyle, trStyle.Element, null);
            tr.X = 0; tr.Y = 0; tr.Width = 80; tr.Height = 16;
            root.AddChild(tr);

            var c = new BoxToPaintConverter();
            c.RegisterSubtreeSnapshot(root, new FakeSnapshot());
            var list = c.Convert(root);

            Assert.That(ContainsType<ReplaySubtreeSnapshotCommand>(list.Commands), Is.False,
                "Text atlas UVs can change independently of layout/style; text-bearing subtrees must submit fresh.");
            Assert.That(ContainsType<BeginSubtreeCaptureCommand>(list.Commands), Is.False,
                "Do not capture text-bearing subtrees for later retained replay.");
        }

        [Test]
        public void ResetCacheStats_zeroes_counters_but_preserves_entries() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            c.Convert(root);
            c.Convert(root);
            Assert.That(c.CacheHits, Is.GreaterThan(0));
            int sz = c.CacheSize;
            c.ResetCacheStats();
            Assert.That(c.CacheHits, Is.EqualTo(0));
            Assert.That(c.CacheMisses, Is.EqualTo(0));
            Assert.That(c.CacheSize, Is.EqualTo(sz));
        }

        [Test]
        public void InvalidateAll_bumps_context_version_so_old_keys_miss() {
            var root = SimpleRedBox();
            var c = new BoxToPaintConverter();
            c.Convert(root);
            c.InvalidateAll();
            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(1),
                "After InvalidateAll, the next Convert must miss because the entry is gone.");
        }

        // Helper: linear scan of the box tree for an Element-keyed lookup. Cheap
        // enough for tests with <100 elements.
        static Box FindBox(Box root, Element e) {
            if (root == null || e == null) return null;
            if (root.Element == e) return root;
            foreach (var c in root.Children) {
                var f = FindBox(c, e);
                if (f != null) return f;
            }
            return null;
        }

        static bool ContainsType<T>(IList<PaintCommand> cmds) where T : PaintCommand {
            foreach (var c in cmds) if (c is T) return true;
            return false;
        }

        sealed class FakeSnapshot : IBoxBatchSnapshot {
            public bool ContainsFilterScopes => false;
            public bool IsValid => true;
            public double AnchorX => 0;
            public double AnchorY => 0;
            public bool Recycled { get; private set; }
            public void Recycle() {
                Recycled = true;
            }
        }

        // ===== Box-local-coords cache scenario tests (≥8 specific scenarios) =====

        [Test]
        public void Two_consecutive_Convert_calls_second_path_is_pure_hit() {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            for (int i = 0; i < 10; i++) {
                var s = Style();
                s.Set("background-color", i % 2 == 0 ? "red" : "blue");
                root.AddChild(Block(0, i * 10, 100, 10, s));
            }
            var c = new BoxToPaintConverter();
            c.Convert(root);
            c.ResetCacheStats();
            c.Convert(root);
            // 11 boxes (root + 10 children), all valid → 11 hits, 0 misses.
            Assert.That(c.CacheMisses, Is.EqualTo(0));
            Assert.That(c.CacheHits, Is.EqualTo(11));
        }

        [Test]
        public void Single_box_style_change_only_that_box_misses() {
            var root = Block(0, 0, 1000, 1000, Style());
            var children = new List<Box>();
            for (int i = 0; i < 5; i++) {
                var s = Style(); s.Set("background-color", "red");
                var b = Block(0, i * 10, 100, 10, s);
                root.AddChild(b);
                children.Add(b);
            }
            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Mutate exactly one child. Don't bump root or other siblings — under
            // the box-local cache they must still hit.
            var target = (BlockBox)children[2];
            target.Style = CloneWithNewVersion(target.Style, ("background-color", "yellow"));

            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(1),
                "Only the dirtied box should miss; root and other siblings stay valid.");
            Assert.That(c.CacheHits, Is.EqualTo(5),
                "Root + 4 unaffected siblings = 5 hits.");
        }

        [Test]
        public void Parent_color_change_only_parent_re_emits_children_translate() {
            // Single parent + child. Change parent's color → parent misses; child
            // hits and just retranslates (its own style/version unchanged).
            var parentStyle = Style();
            parentStyle.Set("background-color", "white");
            var parent = Block(0, 0, 200, 100, parentStyle);
            var childStyle = Style();
            childStyle.Set("background-color", "blue");
            var child = Block(10, 10, 50, 50, childStyle);
            parent.AddChild(child);

            var c = new BoxToPaintConverter();
            c.Convert(parent);

            // Change parent color. Bump parent box-version (in production layout
            // wouldn't bump because color is paint-only, but we model the dirty
            // signal via style version too).
            parent.Style = CloneWithNewVersion(parentStyle, ("background-color", "yellow"));

            c.ResetCacheStats();
            c.Convert(parent);
            Assert.That(c.CacheMisses, Is.EqualTo(1), "Only parent re-emits.");
            Assert.That(c.CacheHits, Is.EqualTo(1), "Child still hits — its bounds just retranslate.");
        }

        [Test]
        public void Parent_layout_resize_invalidates_parent_only_children_translate() {
            // Resize the parent (width change). Parent's own decoration bounds
            // depend on its size, so it misses. Children's CACHED commands are
            // box-local and unaffected by parent resize — they still hit.
            var parentStyle = Style();
            parentStyle.Set("background-color", "white");
            var parent = Block(0, 0, 200, 100, parentStyle);
            var childStyle = Style();
            childStyle.Set("background-color", "blue");
            var child = Block(10, 10, 50, 50, childStyle);
            parent.AddChild(child);

            var c = new BoxToPaintConverter();
            c.Convert(parent);

            // Simulate layout resizing parent and bumping its version.
            parent.Width = 400;
            BumpBoxVersion(parent);

            c.ResetCacheStats();
            c.Convert(parent);
            Assert.That(c.CacheMisses, Is.EqualTo(1), "Parent re-emits after resize.");
            Assert.That(c.CacheHits, Is.EqualTo(1), "Child's box-local cache survives parent resize.");
        }

        [Test]
        public void Hover_toggle_only_hovered_box_misses() {
            var root = Block(0, 0, 1000, 1000, Style());
            var children = new List<Box>();
            for (int i = 0; i < 100; i++) {
                var s = Style();
                s.Set("background-color", "white");
                var b = Block(0, i * 10, 100, 10, s);
                root.AddChild(b);
                children.Add(b);
            }
            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Simulate hover state change: the hovered box gets a fresh ComputedStyle.
            var hovered = (BlockBox)children[42];
            hovered.Style = CloneWithNewVersion(hovered.Style, ("background-color", "cyan"));

            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(1));
            Assert.That(c.CacheHits, Is.EqualTo(100), "Root + 99 unaffected children all hit.");
        }

        [Test]
        public void Translation_by_parent_offset_produces_correct_absolute_bounds() {
            // Verify the bounds-translation math end-to-end. Place a child at a
            // non-trivial local offset, then re-position its parent and confirm
            // the cached child's commands re-emit at the correctly summed origin.
            var root = Block(0, 0, 1000, 1000, Style());
            var s = Style(); s.Set("background-color", "red");
            var child = Block(15, 25, 30, 40, s);
            root.AddChild(child);

            var c = new BoxToPaintConverter();
            var first = c.Convert(root).Commands;
            FillRectCommand fr1 = null;
            foreach (var cmd in first) if (cmd is FillRectCommand fr) fr1 = fr;
            Assert.That(fr1.Bounds.X, Is.EqualTo(15).Within(1e-6));
            Assert.That(fr1.Bounds.Y, Is.EqualTo(25).Within(1e-6));

            // Move root. Bump root.Version so root re-emits; child cache stays valid.
            root.X = 200; root.Y = 300;
            BumpBoxVersion(root);

            var second = c.Convert(root).Commands;
            FillRectCommand fr2 = null;
            foreach (var cmd in second) if (cmd is FillRectCommand fr) fr2 = fr;
            Assert.That(fr2.Bounds.X, Is.EqualTo(215).Within(1e-6), "200 + 15");
            Assert.That(fr2.Bounds.Y, Is.EqualTo(325).Within(1e-6), "300 + 25");
            Assert.That(fr2.Bounds.Width, Is.EqualTo(30).Within(1e-6));
            Assert.That(fr2.Bounds.Height, Is.EqualTo(40).Within(1e-6));
        }

        [Test]
        public void Sibling_is_not_invalidated_by_neighbor_change() {
            var root = Block(0, 0, 1000, 1000, Style());
            var aStyle = Style(); aStyle.Set("background-color", "red");
            var bStyle = Style(); bStyle.Set("background-color", "blue");
            var a = Block(0, 0, 100, 50, aStyle);
            var b = Block(0, 50, 100, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Mutate b. a's PaintCache must remain valid.
            b.Style = CloneWithNewVersion(b.Style, ("background-color", "green"));

            var beforeCache = a.PaintCache;
            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(a.PaintCache, Is.SameAs(beforeCache),
                "a's cache instance is preserved across a sibling-only mutation.");
            Assert.That(c.CacheHits, Is.GreaterThanOrEqualTo(2),
                "Root + a hit; b misses.");
        }

        [Test]
        public void Deeply_nested_subtree_only_dirty_box_misses() {
            // 5-level deep chain; mutate the leaf only. Every ancestor stays valid
            // because each cache holds box-local commands that don't depend on the
            // descendant.
            var levels = new List<Box>();
            Box parent = null;
            Box leaf = null;
            for (int i = 0; i < 5; i++) {
                var s = Style();
                s.Set("background-color", i % 2 == 0 ? "red" : "blue");
                var b = Block(i * 5, i * 5, 100 - i * 5, 100 - i * 5, s);
                if (parent != null) parent.AddChild(b);
                levels.Add(b);
                parent = b;
                if (i == 4) leaf = b;
            }
            var root = levels[0];

            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Mutate ONLY the leaf.
            leaf.Style = CloneWithNewVersion(leaf.Style, ("background-color", "magenta"));

            c.ResetCacheStats();
            c.Convert(root);
            Assert.That(c.CacheMisses, Is.EqualTo(1), "Leaf is the only miss.");
            Assert.That(c.CacheHits, Is.EqualTo(4), "All 4 ancestors hit.");
        }

        [Test]
        public void Cache_replay_byte_identical_to_oracle_on_simple_tree() {
            // Two converters: one is the oracle (cold every time we ask); one is
            // hit on the second call. Verify the bounds, brush kind, and widths
            // match exactly so the cache-translation path is provably correct.
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            var s = Style(); s.Set("background-color", "red");
            var child = Block(7, 11, 50, 25, s);
            root.AddChild(child);

            var oracle = new BoxToPaintConverter();
            var oracleCmds = oracle.Convert(root).Commands;

            var c = new BoxToPaintConverter();
            c.Convert(root);
            var hit = c.Convert(root).Commands;

            Assert.That(hit.Count, Is.EqualTo(oracleCmds.Count));
            for (int i = 0; i < hit.Count; i++) {
                Assert.That(hit[i].GetType(), Is.EqualTo(oracleCmds[i].GetType()));
                if (hit[i] is FillRectCommand a && oracleCmds[i] is FillRectCommand o) {
                    Assert.That(a.Bounds, Is.EqualTo(o.Bounds));
                    Assert.That(a.Brush.Kind, Is.EqualTo(o.Brush.Kind));
                    Assert.That(a.Brush.Color, Is.EqualTo(o.Brush.Color));
                    Assert.That(a.Radii.TopLeft.XRadius, Is.EqualTo(o.Radii.TopLeft.XRadius));
                }
            }
        }
    }
}
