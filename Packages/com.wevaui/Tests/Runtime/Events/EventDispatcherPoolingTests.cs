using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    // P1 / P2 / P21 regression suite. Pins:
    //   - BuildPath / UpdateHover / DiffApplyFlagChain allocate zero
    //     List<Element> per steady-state pointer move.
    //   - PointerEvent objects come from a per-dispatcher pool and the
    //     constructor count stays flat after warm-up.
    //   - try/finally returns survive a handler that throws.
    //   - Hover-enter / hover-leave sequencing is unchanged by the pooling
    //     refactor (regression pin against the in-place DiffApplyFlagChain).
    public class EventDispatcherPoolingTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        EventDispatcher Build(Document doc, FakeHitTester ht) =>
            new EventDispatcher(doc, ht, new FakeUIClock());

        // GC.GetAllocatedBytesForCurrentThread is in netstandard 2.1 / .NET 5+
        // but the Unity 6 player profile exposes it as a public static method.
        // Bind once via reflection into a typed Func so the hot-path call is a
        // simple delegate invocation — reflection Invoke() itself allocates
        // (boxes the long return + allocates the args object[]), which
        // poisoned the per-iteration alloc count in earlier iterations of
        // this test.
        static readonly Func<long> AllocatedBytesFn = BindAllocatedBytes();
        static Func<long> BindAllocatedBytes() {
            var m = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",
                BindingFlags.Public | BindingFlags.Static);
            if (m == null) return null;
            try { return (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), m); }
            catch { return null; }
        }
        static long? AllocatedBytes() => AllocatedBytesFn?.Invoke();

        static int ConstructedCount(EventDispatcher d) {
            var p = typeof(EventDispatcher).GetProperty(
                "PointerEventsConstructedForTests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)p.GetValue(d);
        }

        [Test]
        public void Steady_state_pointer_moves_allocate_zero_bytes() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            // Add a handler so the dispatch path runs end-to-end including
            // the per-listener snapshot copy. Use an empty closure-less
            // handler — closures themselves allocate at compile-time, not
            // per-dispatch.
            d.AddEventListener(leaf, EventKind.PointerMove, NoopHandler);

            // Warm-up: prime the path pool, the listener scratch, the
            // pointer-event pool, the FakeHitTester foreach enumerator, etc.
            for (int i = 0; i < 10; i++) d.DispatchPointerMove(50, 50, KeyModifiers.None);

            var before = AllocatedBytes();
            if (before == null) Assert.Ignore("GC.GetAllocatedBytesForCurrentThread unavailable on this runtime.");

            const int iters = 10;
            for (int i = 0; i < iters; i++) {
                // Same coords -> same hit -> hover-unchanged path. This is
                // the most-common steady-state branch and the one that used
                // to allocate one List<Element> per move via the
                // `if (newHit == hovered)` BuildPath call.
                d.DispatchPointerMove(50, 50, KeyModifiers.None);
            }
            var after = AllocatedBytes();
            long delta = after.Value - before.Value;

            // Tight bound — pool hits should yield literally zero, but allow
            // a tiny slack for measurement noise from the GC accounting
            // itself (the netstandard impl is precise but the wrapper isn't
            // contractually zero-overhead).
            Assert.That(delta, Is.LessThanOrEqualTo(64),
                $"Pointer-move steady-state allocated {delta} bytes over {iters} dispatches — pooling regressed.");
        }

        [Test]
        public void Pointer_event_pool_constructs_zero_after_warmup() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);
            d.AddEventListener(leaf, EventKind.PointerMove, NoopHandler);

            // Warm-up: drain at least one allocation into the pool. The
            // hover-unchanged branch needs only one PointerEvent (the
            // PointerMove dispatch), so one rent + return cycles the pool.
            for (int i = 0; i < 5; i++) d.DispatchPointerMove(50, 50, KeyModifiers.None);

            int before = ConstructedCount(d);
            for (int i = 0; i < 10; i++) d.DispatchPointerMove(50, 50, KeyModifiers.None);
            int after = ConstructedCount(d);

            Assert.That(after - before, Is.EqualTo(0),
                "PointerEvent pool constructed new instances during steady-state moves — pooling regressed.");
        }

        [Test]
        public void Path_and_pointer_event_pools_return_when_handler_throws() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            // Listener exceptions are caught and warn-logged by the dispatcher
            // (see InvokeListeners). The handler does throw, but the throw
            // is caught BEFORE the try/finally in Dispatch / DispatchPointerMove
            // unwinds — so we need to verify pool return via behavior, not by
            // observing an exception escape. The proxy: if try/finally didn't
            // fire, the pool would drain and the next 10 moves would allocate
            // 10 new PointerEvent instances; with proper return, they don't.
            d.AddEventListener(leaf, EventKind.PointerMove, _ => throw new InvalidOperationException("handler boom"));

            // Warm-up — first move primes both pools.
            d.DispatchPointerMove(50, 50, KeyModifiers.None);

            int before = ConstructedCount(d);
            for (int i = 0; i < 10; i++) d.DispatchPointerMove(50, 50, KeyModifiers.None);
            int after = ConstructedCount(d);

            Assert.That(after - before, Is.EqualTo(0),
                "PointerEvent pool drained when a handler threw — try/finally return contract broken.");
        }

        [Test]
        public void Hover_enter_leave_sequence_is_unchanged_after_pooling_refactor() {
            // Two siblings; pointer moves from a -> b should fire leave(a)
            // then enter(b). Each element also has a parent <section> in
            // the chain, but the section is in the common prefix on neither
            // move (siblings have separate paths), so for sibling moves all
            // ancestors above the common root fire enter/leave. With a
            // single <section> wrapping both, the section IS shared and
            // does NOT fire enter/leave when the pointer hops siblings —
            // pin that semantic.
            var doc = Html("<section id=\"root\"><div id=\"a\"></div><div id=\"b\"></div></section>");
            var root = doc.GetElementById("root");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(root, EventKind.PointerEnter, _ => seen.Add("enter:root"));
            d.AddEventListener(root, EventKind.PointerLeave, _ => seen.Add("leave:root"));
            d.AddEventListener(a, EventKind.PointerEnter, _ => seen.Add("enter:a"));
            d.AddEventListener(a, EventKind.PointerLeave, _ => seen.Add("leave:a"));
            d.AddEventListener(b, EventKind.PointerEnter, _ => seen.Add("enter:b"));
            d.AddEventListener(b, EventKind.PointerLeave, _ => seen.Add("leave:b"));

            // Initial entry into `a` — fires enter:root then enter:a (root-to-target order).
            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            // Hop to sibling `b` — root is in the common prefix and is
            // NOT re-fired; only a leaves and b enters.
            d.DispatchPointerMove(150, 50, KeyModifiers.None);

            Assert.That(seen, Is.EqualTo(new List<string> {
                "enter:root", "enter:a",
                "leave:a", "enter:b",
            }));

            // Hover state should reflect the final chain: root + b carry :hover, a does not.
            Assert.That((d.StateProvider.GetState(root) & ElementState.Hover) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Hover) != 0, Is.False);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Hover) != 0, Is.True);
        }

        [Test]
        public void Pointer_event_pool_is_reused_across_dispatches() {
            // Direct verification that the SAME PointerEvent instance is
            // recycled — a stronger assertion than just "construction count
            // stays flat". Captures the event reference (with the documented
            // contract violation knowingly) and confirms a later dispatch
            // receives the SAME object back, and that ResetForReuse clears
            // sticky stop-propagation state from the prior dispatch.
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seenRefs = new List<PointerEvent>();
            var seenStopBits = new List<bool>();
            d.AddEventListener(a, EventKind.PointerMove, e => {
                var pe = (PointerEvent)e;
                seenRefs.Add(pe);
                seenStopBits.Add(pe.PropagationStopped);
                // Set a sticky bit; if pooling reuses this object without
                // ResetForReuse, the NEXT dispatch's handler would observe
                // PropagationStopped == true.
                pe.StopPropagation();
            });

            d.DispatchPointerMove(10, 10, KeyModifiers.None);
            d.DispatchPointerMove(20, 20, KeyModifiers.None);
            d.DispatchPointerMove(30, 30, KeyModifiers.None);

            Assert.That(seenRefs.Count, Is.EqualTo(3));
            Assert.That(ReferenceEquals(seenRefs[0], seenRefs[1]), Is.True,
                "Expected PointerEvent to be pooled (same reference on second dispatch).");
            Assert.That(ReferenceEquals(seenRefs[1], seenRefs[2]), Is.True,
                "Expected PointerEvent to be pooled (same reference on third dispatch).");
            // Every dispatch must enter the handler with a freshly-reset
            // sticky bit, even though the previous handler set it.
            Assert.That(seenStopBits, Is.EqualTo(new List<bool> { false, false, false }),
                "ResetForReuse failed to clear PropagationStopped on rent — handlers leaked stop-propagation across dispatches.");
        }

        static void NoopHandler(UIEvent _) { }
    }
}
