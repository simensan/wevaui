using System;
using System.Collections.Generic;

namespace Weva.Paint {
    public sealed class PaintList {
        readonly List<PaintCommand> commands;

        public PaintList() {
            commands = new List<PaintCommand>();
        }

        // capacityHint pre-sizes the backing array so the converter doesn't pay
        // List<> growth-rehashes while emitting commands. Negative or zero falls
        // back to the default capacity.
        public PaintList(int capacityHint) {
            commands = capacityHint > 0
                ? new List<PaintCommand>(capacityHint)
                : new List<PaintCommand>();
        }

        public List<PaintCommand> Commands => commands;

        public int Count => commands.Count;

        public void Add(PaintCommand command) {
            if (command == null) throw new ArgumentNullException(nameof(command));
            commands.Add(command);
        }

        public void AddRange(IEnumerable<PaintCommand> items) {
            if (items == null) throw new ArgumentNullException(nameof(items));
            foreach (var c in items) {
                Add(c);
            }
        }

        public void Clear() {
            commands.Clear();
        }

        // Removes the entry at `index` and shifts subsequent entries down by
        // one. Used by BoxToPaintConverter to elide empty-subtree capture
        // markers — when a Begin marker was inserted but the End wasn't yet,
        // dropping the Begin in place avoids leaving a no-op pair in the
        // emitted command stream. Forwards to the backing List<T>.
        public void RemoveAt(int index) {
            commands.RemoveAt(index);
        }

        // Pool contract: Reset() is what PaintListPool calls when an instance is
        // returned. It MUST NOT free the backing array — the whole point of pooling
        // is to keep capacity warm so the next Convert pass writes into it without
        // re-allocating. List<T>.Clear preserves Capacity (zeroes only the slots
        // up to Count), so this is the cheapest valid reset.
        //
        // Behaviorally identical to Clear(); kept as a separate method so the pool
        // contract is explicit at call sites.
        public void Reset() {
            commands.Clear();
        }
    }
}
