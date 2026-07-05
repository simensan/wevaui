using System.Collections.Generic;

namespace Weva.Paint {
    // Pool of reusable PaintList instances. The converter rents one at the start
    // of Convert(), writes commands into it, and the caller (WevaDocument /
    // EmitPaint) returns it after the backend has consumed the commands.
    //
    // Lifetime contract:
    //   - Rent() returns a PaintList with Count == 0 but with its backing array
    //     intact from the previous rental. The capacity is "warm" — repeated
    //     Convert/Submit cycles do NOT re-allocate.
    //   - Return(list) clears the list and parks it for re-use. Calling Return
    //     on an already-returned list is undefined; callers must not retain the
    //     reference past Return.
    //   - The pool is NOT thread-safe. Weva is single-threaded by design;
    //     mirroring CascadePools / BoxPool which also avoid lock overhead.
    //
    // Ownership in the pipeline:
    //   - BoxToPaintConverter.Convert(root) without an explicit output param
    //     rents internally and returns the list to the caller; the caller owns
    //     return-on-done.
    //   - When called with an explicit output PaintList, the converter writes
    //     into it and the caller is the sole owner (the converter never returns
    //     it to the pool — because the caller might still hold pre-existing
    //     state in it).
    //
    // Capacity cap exists so a single oversized scene doesn't permanently bloat
    // every subsequent rental's idle memory.
    public sealed class PaintListPool {
        public const int DefaultMaxCapacity = 8;

        readonly Stack<PaintList> stack;
        readonly int maxCapacity;

        public PaintListPool() : this(DefaultMaxCapacity) { }

        public PaintListPool(int maxCapacity) {
            this.maxCapacity = maxCapacity > 0 ? maxCapacity : DefaultMaxCapacity;
            stack = new Stack<PaintList>(this.maxCapacity);
        }

        public int CurrentSize => stack.Count;
        public int MaxCapacity => maxCapacity;

        public PaintList Rent() {
            if (stack.Count > 0) {
                var l = stack.Pop();
                l.Reset();
                return l;
            }
            return new PaintList();
        }

        public PaintList Rent(int capacityHint) {
            if (stack.Count > 0) {
                var l = stack.Pop();
                l.Reset();
                return l;
            }
            return new PaintList(capacityHint);
        }

        public void Return(PaintList list) {
            if (list == null) return;
            list.Reset();
            if (stack.Count >= maxCapacity) return;
            stack.Push(list);
        }

        // Static shared instance for callers that don't need a dedicated pool.
        // Each frame in the lifecycle uses Shared.Rent() / Shared.Return() so
        // the steady-state has zero PaintList allocs across the whole document.
        public static readonly PaintListPool Shared = new PaintListPool();
    }
}
