using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;
using Weva.Tests.Events;

namespace Weva.Tests.Reactive {
    public class PseudoClassPropagationTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static EventDispatcher Dispatcher(Document doc, FakeHitTester ht) =>
            new EventDispatcher(doc, ht, new FakeUIClock());

        [Test]
        public void Hover_toggle_marks_element_with_pseudo_class_state() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(tracker.IsDirty(a, InvalidationKind.PseudoClassState), Is.True);
        }

        [Test]
        public void Hover_toggle_does_not_dirty_unrelated_siblings() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(tracker.IsDirty(a, InvalidationKind.PseudoClassState), Is.True);
            Assert.That(tracker.IsDirty(b, InvalidationKind.PseudoClassState), Is.False);
        }

        [Test]
        public void Pseudo_class_state_implies_style_in_tracker() {
            // Downstream consumers (layout, paint) filter by Style; PseudoClassState
            // must imply Style so a state flip triggers their re-resolution paths
            // even when the cascade short-circuits via the per-element digest.
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.PseudoClassState);
            Assert.That(tracker.IsDirty(e, InvalidationKind.Style), Is.True);
            Assert.That(tracker.IsDirty(e, InvalidationKind.PseudoClassState), Is.True);
        }

        [Test]
        public void Pseudo_class_state_does_not_imply_layout() {
            // Only Style is implied; PseudoClassState alone does not force a layout
            // recompute. A layout-affecting :hover rule (like padding) would also
            // require the cascade to surface a Style change to layout, which it does
            // through the result-dictionary invalidation.
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.PseudoClassState);
            Assert.That(tracker.IsDirty(e, InvalidationKind.Layout), Is.False);
        }

        [Test]
        public void Multiple_state_flips_on_same_element_coalesce() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            // Hover + Active set on `b`; both state flips for the same element must
            // coalesce to a single dirty entry for `b` (no double-mark).
            // HtmlParser now wraps fragments in synthetic <html><body>, so ancestor
            // elements also get Hover/Active state — DirtyCount ≥ 1. We pin the
            // coalescing contract on `b` specifically rather than the global count.
            Assert.That(tracker.IsDirty(b, InvalidationKind.PseudoClassState), Is.True,
                "button must be dirty after Hover+Active");
            Assert.That(tracker.IsDirty(b, InvalidationKind.Style), Is.True,
                "PseudoClassState must imply Style on b");
            // Verify coalescing: b should appear at most once in the dirty set.
            int bCount = 0;
            foreach (var kv in tracker.DirtyEntries) { if (ReferenceEquals(kv.Key, b)) bCount++; }
            Assert.That(bCount, Is.EqualTo(1), "multiple state flips on b must coalesce to one dirty entry");
        }

        [Test]
        public void State_flip_with_no_tracker_attached_is_safe() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            // tracker NOT attached.
            Assert.DoesNotThrow(() => d.DispatchPointerMove(50, 50, KeyModifiers.None));
            Assert.That((d.StateProvider.GetState(b) & ElementState.Hover) != 0, Is.True);
        }

        [Test]
        public void Setting_same_state_flag_is_idempotent_and_does_not_redirty() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            tracker.Clear();
            // Same hit point — state unchanged.
            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(tracker.DirtyCount, Is.EqualTo(0),
                "no state flip should leave tracker empty");
        }

        [Test]
        public void Hover_chain_change_dirties_old_chain_and_new_chain_only() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerMove(50, 50, KeyModifiers.None); // hover a
            tracker.Clear();
            d.DispatchPointerMove(150, 50, KeyModifiers.None); // hover b, unhover a
            Assert.That(tracker.IsDirty(a, InvalidationKind.PseudoClassState), Is.True);
            Assert.That(tracker.IsDirty(b, InvalidationKind.PseudoClassState), Is.True);
        }

        [Test]
        public void Pseudo_class_state_flag_value_is_distinct_from_other_kinds() {
            Assert.That((int)InvalidationKind.PseudoClassState, Is.EqualTo(32));
            Assert.That(InvalidationKind.PseudoClassState & InvalidationKind.Style,
                Is.EqualTo(InvalidationKind.None));
            Assert.That(InvalidationKind.PseudoClassState & InvalidationKind.Layout,
                Is.EqualTo(InvalidationKind.None));
        }

        [Test]
        public void All_kinds_includes_pseudo_class_state() {
            Assert.That((InvalidationKind.All & InvalidationKind.PseudoClassState) != 0);
        }

        [Test]
        public void Tracker_clear_pseudo_class_state_only_preserves_other_kinds() {
            var tracker = new InvalidationTracker();
            var e = new Element("div");
            tracker.MarkDirty(e, InvalidationKind.PseudoClassState | InvalidationKind.Layout);
            // Style was implicitly added; clear PseudoClassState | Style and Layout remains.
            tracker.Clear(InvalidationKind.PseudoClassState | InvalidationKind.Style);
            Assert.That(tracker.IsDirty(e, InvalidationKind.Layout), Is.True);
            Assert.That(tracker.IsDirty(e, InvalidationKind.PseudoClassState), Is.False);
        }

        [Test]
        public void Active_state_marks_pseudo_class_state_on_pointer_down() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Dispatcher(doc, ht);
            var tracker = new InvalidationTracker();
            d.StateProvider.Tracker = tracker;

            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That(tracker.IsDirty(b, InvalidationKind.PseudoClassState), Is.True);
        }

        [Test]
        public void State_provider_version_increments_on_each_state_change() {
            var sp = new InteractionStateProvider();
            long v0 = sp.Version;
            var e = new Element("div");
            // Use the public hover-chain setter to flip Hover. SetFlag is internal,
            // but SetHoverChain exposes the same write path.
            sp.GetType().GetMethod("SetFlag",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(sp, new object[] { e, ElementState.Hover, true });
            Assert.That(sp.Version, Is.GreaterThan(v0));
            long v1 = sp.Version;
            // Setting same flag again is a no-op for version.
            sp.GetType().GetMethod("SetFlag",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(sp, new object[] { e, ElementState.Hover, true });
            Assert.That(sp.Version, Is.EqualTo(v1));
        }
    }
}
