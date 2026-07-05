using NUnit.Framework;
using Weva.Dom;
using Weva.Layout;
using Weva.Reactive;

namespace Weva.Tests.Layout {
    public class IncrementalLayoutGateTests {
        [Test]
        public void Null_tracker_does_not_skip() {
            // A null tracker means the caller didn't opt into incremental skip;
            // the gate must not return true so the caller falls through to a
            // full layout (this is how the legacy LayoutEngine.Layout(...)
            // overload preserves its behavior).
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(null), Is.False);
        }

        [Test]
        public void Empty_tracker_skips() {
            var tracker = new InvalidationTracker();
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Style_only_dirty_skips() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"), InvalidationKind.Style);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void PseudoClassState_only_dirty_skips() {
            // PseudoClassState implies Style in the tracker but never Layout,
            // so a hover/focus flip with only paint-only rules attached should
            // skip layout entirely.
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("button"), InvalidationKind.PseudoClassState);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Paint_only_dirty_skips() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"), InvalidationKind.Paint);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Composite_only_dirty_skips() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"), InvalidationKind.Composite);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Layout_dirty_does_not_skip() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"), InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
        }

        [Test]
        public void Structure_dirty_does_not_skip() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"), InvalidationKind.Structure);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
        }

        [Test]
        public void Mixed_style_and_layout_does_not_skip() {
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("div"),
                InvalidationKind.Style | InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
        }

        [Test]
        public void Mixed_pseudo_and_layout_does_not_skip() {
            // A :hover rule that flips border-width must also mark Layout dirty
            // (cascade is responsible for surfacing this); the gate must
            // respect that and not skip.
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(new Element("button"),
                InvalidationKind.PseudoClassState | InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
        }

        [Test]
        public void Many_style_only_elements_still_skip() {
            var tracker = new InvalidationTracker();
            for (int i = 0; i < 50; i++) {
                tracker.MarkDirty(new Element("div"), InvalidationKind.Style);
            }
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Single_layout_dirty_among_many_style_elements_does_not_skip() {
            var tracker = new InvalidationTracker();
            for (int i = 0; i < 50; i++) {
                tracker.MarkDirty(new Element("div"), InvalidationKind.Style);
            }
            tracker.MarkDirty(new Element("special"), InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
        }

        [Test]
        public void Style_paint_composite_combinations_all_skip() {
            // Every kind that is NOT Layout|Structure should skip individually
            // and in any combination.
            foreach (var kinds in new[] {
                InvalidationKind.Style,
                InvalidationKind.Paint,
                InvalidationKind.Composite,
                InvalidationKind.PseudoClassState,
                InvalidationKind.Style | InvalidationKind.Paint,
                InvalidationKind.Style | InvalidationKind.Composite,
                InvalidationKind.Paint | InvalidationKind.Composite,
                InvalidationKind.PseudoClassState | InvalidationKind.Paint,
            }) {
                var tracker = new InvalidationTracker();
                tracker.MarkDirty(new Element("div"), kinds);
                Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True,
                    $"kinds={kinds} should skip");
            }
        }

        [Test]
        public void Cleared_tracker_returns_to_skip() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
            tracker.Clear();
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }

        [Test]
        public void Cleared_layout_kind_only_returns_to_skip() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.Style | InvalidationKind.Layout);
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.False);
            tracker.Clear(InvalidationKind.Layout);
            // Style remains, Layout was cleared — gate now skips.
            Assert.That(IncrementalLayoutGate.ShouldSkipLayout(tracker), Is.True);
        }
    }
}
