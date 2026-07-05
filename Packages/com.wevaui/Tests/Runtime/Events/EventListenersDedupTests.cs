using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Events {
    // TG34 — direct coverage of EventListeners (Runtime/Events/EventListeners.cs).
    //
    // EventDispatcher / EventDispatcherListenerLeakTests already exercise the
    // happy paths transitively (register, dispatch, remove on DOM mutation),
    // but the per-element dedup + capture-vs-bubble distinction contracts
    // weren't pinned. These tests target the EventListeners class directly via
    // InternalsVisibleTo so a regression in the dedup loop or the phase filter
    // surfaces here rather than as a flaky higher-level dispatcher test.
    public class EventListenersDedupTests {
        // EventListeners is internal-sealed and lives in Weva.Events; we
        // access it via the InternalsVisibleTo("Weva.Tests.Runtime") that
        // already exists on the Runtime assembly (see Runtime/Css/Selectors/
        // AssemblyInfo.cs).
        static EventListeners NewMap() => new EventListeners();
        static Element NewElement(string tag = "div") => new Element(tag);

        // Tracks how many times each handler fired and in what order, so the
        // dedup + ordering assertions don't depend on instance equality alone.
        sealed class Counter {
            public int Calls;
            public int Order = -1;
            public EventListener Handler(List<string> log, string name) {
                return _ => { Calls++; log.Add(name); };
            }
        }

        [Test]
        public void Adding_same_kind_handler_capture_twice_is_a_noop() {
            // Contract: AddListener walks the per-element list and bails on
            // (Kind, Handler, UseCapture) equality. The same delegate registered
            // twice must NOT produce two dispatch entries.
            var map = NewMap();
            var el = NewElement();
            EventListener h = _ => { };

            map.AddListener(el, EventKind.Click, h, useCapture: false);
            map.AddListener(el, EventKind.Click, h, useCapture: false);

            var got = map.GetListeners(el, EventKind.Click, EventPhase.Bubble);
            Assert.That(got.Count, Is.EqualTo(1),
                "TG34 dedup: identical (kind, handler, capture) must be added at most once");
        }

        [Test]
        public void Same_handler_different_capture_flag_registers_both_separately() {
            // Contract: UseCapture is part of the dedup key. The DOM L3
            // addEventListener spec treats (handler, capture) as distinct
            // registrations and so must we — otherwise authors who add the
            // same handler in both phases lose the bubble (or capture) copy.
            var map = NewMap();
            var el = NewElement();
            EventListener h = _ => { };

            map.AddListener(el, EventKind.Click, h, useCapture: true);
            map.AddListener(el, EventKind.Click, h, useCapture: false);

            var cap = map.GetListeners(el, EventKind.Click, EventPhase.Capture);
            var bub = map.GetListeners(el, EventKind.Click, EventPhase.Bubble);
            Assert.That(cap.Count, Is.EqualTo(1),
                "capture-phase retrieval must surface the useCapture=true registration");
            Assert.That(bub.Count, Is.EqualTo(1),
                "bubble-phase retrieval must surface the useCapture=false registration");
            Assert.That(cap[0], Is.SameAs(h));
            Assert.That(bub[0], Is.SameAs(h));
        }

        [Test]
        public void Remove_matches_capture_flag_and_only_removes_the_matching_registration() {
            // Contract: RemoveListener's key includes UseCapture. Removing
            // (kind, handler, capture=true) must leave the (kind, handler,
            // capture=false) sibling alone.
            var map = NewMap();
            var el = NewElement();
            EventListener h = _ => { };

            map.AddListener(el, EventKind.Click, h, useCapture: true);
            map.AddListener(el, EventKind.Click, h, useCapture: false);

            bool removed = map.RemoveListener(el, EventKind.Click, h, useCapture: true);
            Assert.That(removed, Is.True, "RemoveListener must report success for an existing registration");

            Assert.That(map.GetListeners(el, EventKind.Click, EventPhase.Capture).Count, Is.EqualTo(0),
                "capture registration must be gone");
            Assert.That(map.GetListeners(el, EventKind.Click, EventPhase.Bubble).Count, Is.EqualTo(1),
                "TG34: the bubble-phase sibling must survive — remove keys include UseCapture");

            // Removing again is a no-op (false return), not a throw.
            Assert.That(map.RemoveListener(el, EventKind.Click, h, useCapture: true), Is.False,
                "second remove of the same key must return false (no-op)");
        }

        [Test]
        public void GetListeners_filters_by_phase_and_preserves_registration_order_within_phase() {
            // Contract: AppendListeners walks the per-element list in
            // registration order and emits only the matching phase. Authors
            // relying on "registered first runs first" within a phase need
            // this ordering pinned.
            //
            // Note on the task wording: a single element can't surface
            // "capture-listeners first in document order" because GetListeners
            // returns one phase at a time. The document-order capture-then-
            // bubble guarantee lives in EventDispatcher's ancestor walk
            // (covered by EventDispatcherTests). What EventListeners owns is
            // the per-element ordering within a phase, which is what this
            // test pins.
            var map = NewMap();
            var el = NewElement();
            var log = new List<string>();
            var a = new Counter();
            var b = new Counter();
            var c = new Counter();
            var d = new Counter();
            var hA = a.Handler(log, "A-bubble");
            var hB = b.Handler(log, "B-capture");
            var hC = c.Handler(log, "C-bubble");
            var hD = d.Handler(log, "D-capture");

            // Interleave capture / bubble registrations.
            map.AddListener(el, EventKind.Click, hA, useCapture: false);
            map.AddListener(el, EventKind.Click, hB, useCapture: true);
            map.AddListener(el, EventKind.Click, hC, useCapture: false);
            map.AddListener(el, EventKind.Click, hD, useCapture: true);

            var capture = map.GetListeners(el, EventKind.Click, EventPhase.Capture);
            var bubble = map.GetListeners(el, EventKind.Click, EventPhase.Bubble);

            Assert.That(capture.Count, Is.EqualTo(2), "two capture-phase registrations");
            Assert.That(bubble.Count, Is.EqualTo(2), "two bubble-phase registrations");

            // Within a phase, registration order is preserved.
            Assert.That(capture[0], Is.SameAs(hB),
                "capture registration order: B was registered before D");
            Assert.That(capture[1], Is.SameAs(hD));
            Assert.That(bubble[0], Is.SameAs(hA),
                "bubble registration order: A was registered before C");
            Assert.That(bubble[1], Is.SameAs(hC));

            // Cross-check via firing — invoking the returned listeners in the
            // documented order must produce capture-then-bubble within-phase
            // ordering, matching what EventDispatcher relies on.
            foreach (var l in capture) l(null);
            foreach (var l in bubble) l(null);
            Assert.That(log, Is.EqualTo(new List<string> { "B-capture", "D-capture", "A-bubble", "C-bubble" }));
        }

        [Test]
        public void Two_distinct_handlers_for_same_element_and_kind_both_fire_in_registration_order() {
            // Contract: dedup keys on delegate equality. Two DIFFERENT
            // EventListener delegates (even with identical logic) are
            // distinct registrations and both must run, in the order they
            // were registered.
            var map = NewMap();
            var el = NewElement();
            var log = new List<string>();

            EventListener h1 = _ => log.Add("first");
            EventListener h2 = _ => log.Add("second");

            map.AddListener(el, EventKind.PointerDown, h1, useCapture: false);
            map.AddListener(el, EventKind.PointerDown, h2, useCapture: false);

            var got = map.GetListeners(el, EventKind.PointerDown, EventPhase.Bubble);
            Assert.That(got.Count, Is.EqualTo(2),
                "two distinct delegates are not deduped");

            foreach (var l in got) l(null);
            Assert.That(log, Is.EqualTo(new List<string> { "first", "second" }),
                "TG34: registration order is preserved across distinct handlers");
        }

        [Test]
        public void AtTarget_phase_returns_listeners_of_both_capture_and_bubble_registrations() {
            // Additional pin: AppendListeners' phase filter has three
            // branches — Capture (caller passed Capture, skip non-capture
            // entries), Bubble (skip capture entries), and "anything else"
            // (no filter, which is what AtTarget produces). Without this
            // test the AtTarget fall-through is silently untested.
            var map = NewMap();
            var el = NewElement();
            EventListener cap = _ => { };
            EventListener bub = _ => { };

            map.AddListener(el, EventKind.Focus, cap, useCapture: true);
            map.AddListener(el, EventKind.Focus, bub, useCapture: false);

            var atTarget = map.GetListeners(el, EventKind.Focus, EventPhase.AtTarget);
            Assert.That(atTarget.Count, Is.EqualTo(2),
                "AtTarget phase must surface both capture and bubble registrations on the same element");
            // Use CollectionAssert.Contains — `Does.Contain` on NUnit's
            // current constraint chain is a substring constraint, not a
            // collection-membership one (per repo notes).
            CollectionAssert.Contains(atTarget, cap);
            CollectionAssert.Contains(atTarget, bub);
        }
    }
}
