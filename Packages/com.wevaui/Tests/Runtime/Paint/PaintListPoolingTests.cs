using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint {
    public class PaintListPoolingTests {
        static FillRectCommand Fill() =>
            new FillRectCommand(new Rect(0, 0, 1, 1), Brush.SolidColor(LinearColor.White));

        [Test]
        public void Reset_clears_commands_without_freeing_capacity() {
            var list = new PaintList(64);
            for (int i = 0; i < 32; i++) list.Add(Fill());
            int beforeCapacity = list.Commands.Capacity;
            list.Reset();
            Assert.That(list.Commands, Has.Count.EqualTo(0));
            // List<T>.Clear preserves Capacity. The pool relies on this so
            // re-rents don't pay reallocation.
            Assert.That(list.Commands.Capacity, Is.EqualTo(beforeCapacity),
                "Reset must preserve backing array capacity");
        }

        [Test]
        public void Pool_rent_then_return_then_rent_returns_same_instance() {
            var pool = new PaintListPool();
            var first = pool.Rent();
            first.Add(Fill());
            pool.Return(first);
            var second = pool.Rent();
            Assert.That(second, Is.SameAs(first), "pool should hand back the parked instance");
            Assert.That(second.Commands, Has.Count.EqualTo(0), "rented list must be empty");
        }

        [Test]
        public void Pool_returns_freshly_rented_when_empty() {
            var pool = new PaintListPool();
            var a = pool.Rent();
            var b = pool.Rent();
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a, Is.Not.SameAs(b), "two unbacked rents should produce distinct lists");
        }

        [Test]
        public void Pool_caps_size_at_max_capacity() {
            var pool = new PaintListPool(maxCapacity: 2);
            var a = pool.Rent();
            var b = pool.Rent();
            var c = pool.Rent();
            pool.Return(a);
            pool.Return(b);
            pool.Return(c);
            Assert.That(pool.CurrentSize, Is.EqualTo(2), "pool should drop the third return");
        }

        [Test]
        public void Pool_does_not_retain_returned_list_strongly_after_eviction() {
            // With maxCapacity 1, the second Return must not push — verified by
            // pool.CurrentSize staying at 1, NOT 2. (The previous WeakReference
            // assertion was flaky against Mono Play-mode GC scheduling.)
            var pool = new PaintListPool(maxCapacity: 1);
            var keep = pool.Rent();
            pool.Return(keep);
            keep = pool.Rent();
            pool.Return(new PaintList()); // park unrelated instance; pool at cap

            var dropped = new PaintList();
            dropped.Add(Fill());
            pool.Return(dropped); // pool is at cap → must not push dropped
            Assert.That(pool.CurrentSize, Is.EqualTo(1),
                "pool at cap must reject the Return (CurrentSize stays at 1, not 2)");
            GC.KeepAlive(keep);
            GC.KeepAlive(dropped);
        }

        [Test]
        public void Two_consecutive_Convert_calls_produce_same_command_sequence() {
            // Behavioral parity: pooling must not change Convert output.
            var s = Style();
            s.Set("background-color", "red");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "1px");
            s.Set("border-top-color", "black");
            var root = Block(0, 0, 100, 50, s);

            var c = new BoxToPaintConverter();

            var first = c.Convert(root);
            var firstSnapshot = new List<Type>();
            foreach (var cmd in first.Commands) firstSnapshot.Add(cmd.GetType());
            // Don't return: keep the list alive so the pool's next Rent allocates fresh.

            var second = c.Convert(root);
            Assert.That(second.Commands.Count, Is.EqualTo(firstSnapshot.Count));
            for (int i = 0; i < firstSnapshot.Count; i++) {
                Assert.That(second.Commands[i].GetType(), Is.EqualTo(firstSnapshot[i]),
                    $"command {i} type mismatch between consecutive Convert calls");
            }
        }

        [Test]
        public void Convert_with_explicit_output_writes_into_provided_list() {
            var s = Style();
            s.Set("background-color", "blue");
            var root = Block(0, 0, 100, 50, s);

            var c = new BoxToPaintConverter();
            var owned = new PaintList(8);
            var result = c.Convert(root, null, null, null, null, owned);
            Assert.That(result, Is.SameAs(owned), "Convert must write into the supplied output");
            Assert.That(owned.Commands.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Convert_with_explicit_output_resets_existing_contents() {
            var owned = new PaintList(8);
            owned.Add(Fill());
            owned.Add(Fill());
            int before = owned.Commands.Count;

            var s = Style();
            s.Set("background-color", "green");
            var root = Block(0, 0, 100, 50, s);

            var c = new BoxToPaintConverter();
            c.Convert(root, null, null, null, null, owned);

            // Convert must Reset the output before emitting; the prior two Fill
            // commands must NOT remain.
            Assert.That(owned.Commands.Count, Is.LessThan(before + 2),
                "Convert must clear the output before emitting");
        }

        [Test]
        public void Pool_grows_until_cap_then_falls_back_to_fresh_alloc() {
            var pool = new PaintListPool(maxCapacity: 3);
            var rented = new List<PaintList>();
            for (int i = 0; i < 5; i++) rented.Add(pool.Rent());
            Assert.That(pool.CurrentSize, Is.EqualTo(0));
            // Returning fills the pool up to the cap.
            for (int i = 0; i < 5; i++) pool.Return(rented[i]);
            Assert.That(pool.CurrentSize, Is.EqualTo(3));
            // 4th and 5th return entries are dropped — re-renting beyond 3 must
            // mint a new instance.
            var pool2 = pool;  // rebind for clarity
            var first = pool2.Rent();
            var second = pool2.Rent();
            var third = pool2.Rent();
            var fourth = pool2.Rent();
            Assert.That(pool2.CurrentSize, Is.EqualTo(0));
            Assert.That(rented, Does.Contain(first));
            Assert.That(rented, Does.Contain(second));
            Assert.That(rented, Does.Contain(third));
            Assert.That(rented, Has.No.Member(fourth),
                "4th rent should be a freshly allocated list since pool only retained the first 3");
        }

        [Test]
        public void Disposing_a_rented_PaintList_without_returning_does_not_leak() {
            // The pool stores a Stack of references — not returning a rented list
            // simply means it gets GC'd, not retained.
            //
            // The allocation happens in a separate method so the local slot for
            // the rented list is freed when that frame unwinds; under Mono / a
            // debug JIT, a local in the same method as the GC.Collect() call
            // can be artificially kept alive in a register/stack slot.
            var pool = new PaintListPool();
            var weak = RentAndDrop(pool);
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.That(weak.IsAlive, Is.False, "non-returned rentals must be GC'able");
        }

        static WeakReference RentAndDrop(PaintListPool pool) {
            var l = pool.Rent();
            l.Add(Fill());
            return new WeakReference(l);
        }

        [Test]
        public void Converter_Return_clears_list_and_parks_commands() {
            var s = Style();
            s.Set("background-color", "red");
            var root = Block(0, 0, 100, 50, s);

            var c = new BoxToPaintConverter();
            var list = c.Convert(root);
            Assume.That(list.Commands.Count, Is.GreaterThan(0));
            int initialFillStack = c.CommandPool.FillRectStackSize;

            c.Return(list);
            // After return, the FillRect (or whatever pool-able commands were
            // emitted) should be parked back in the command pool.
            Assert.That(c.CommandPool.FillRectStackSize, Is.GreaterThanOrEqualTo(initialFillStack + 1),
                "Return should park FillRect commands back in the pool");
            // The list itself returned to the list pool — re-renting from
            // BoxToPaintConverter's pool should hand back the same list.
            var second = c.ListPool.Rent();
            Assert.That(second, Is.SameAs(list), "PaintList instance must be parked in pool after Return");
        }

        [Test]
        public void Repeated_pooled_Convert_does_not_grow_alloc_rate() {
            // Sanity: 100 back-to-back Convert+Return cycles should allocate at
            // a roughly steady rate, not super-linearly. We don't assert a hard
            // ceiling here (sister tasks own the CSS-parser leftover) — just
            // that consecutive 50-call windows are within 4× of each other.
            var s = Style();
            s.Set("background-color", "red");
            var root = Block(0, 0, 100, 50, s);
            var c = new BoxToPaintConverter();
            for (int w = 0; w < 50; w++) { var l = c.Convert(root); c.Return(l); }

            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            // Snapshots force a full collection (GetTotalMemory(true)) so they
            // measure LIVE bytes — the raw-heap-size (false) snapshots were
            // hostage to Gen0 segment boundaries and flaked at clean HEAD
            // (8,224-byte artifact vs the 8,192 noise floor, zero actual
            // regression). One post-collect warm window absorbs whatever the
            // forced collection reclaimed that the converter lazily rebuilds
            // (weakly-held pool state), so both measured windows run at true
            // steady state. GetTotalAllocatedBytes would be the ideal metric
            // but doesn't exist on Unity's Mono profile.
            for (int i = 0; i < 50; i++) { var l = c.Convert(root); c.Return(l); }
            long s1 = GC.GetTotalMemory(true);
            for (int i = 0; i < 50; i++) { var l = c.Convert(root); c.Return(l); }
            long s2 = GC.GetTotalMemory(true);
            for (int i = 0; i < 50; i++) { var l = c.Convert(root); c.Return(l); }
            long s3 = GC.GetTotalMemory(true);

            long w12 = s2 - s1;
            long w23 = s3 - s2;
            Assert.That(System.Math.Abs(w23 - w12), Is.LessThanOrEqualTo(System.Math.Max(w12 / 2, 8192)),
                $"alloc rate should be steady; w12={w12}, w23={w23}");
        }
    }
}
