namespace Weva.Binding {
    internal interface IBindingScope {
        bool TryResolveLocal(string segment, out object value);
    }

    // Mutable so RepeatBinding can keep one scope per instance and re-point it
    // each frame via Reset instead of allocating a fresh scope per Update —
    // the per-frame binding poll must stay alloc-free at idle.
    internal sealed class BindingScope : IBindingScope {
        object parent;
        readonly string alias;
        object item;
        int index;
        // Cached box for $index so idle polls don't box an int per resolution.
        object boxedIndex;

        public BindingScope(object parent, string alias, object item, int index) {
            this.parent = parent;
            this.alias = alias;
            this.item = item;
            this.index = index;
        }

        // Re-point this scope at a new (parent, item, index) triple.
        internal void Reset(object parent, object item, int index) {
            this.parent = parent;
            this.item = item;
            if (this.index != index) {
                this.index = index;
                boxedIndex = null;
            }
        }

        public bool TryResolveLocal(string segment, out object value) {
            if (segment == alias) {
                value = item;
                return true;
            }
            if (segment == "$index") {
                value = boxedIndex ?? (boxedIndex = index);
                return true;
            }
            // Single-segment resolve against the parent context. Segments
            // arrive from already-parsed BindingPaths, so re-parsing here
            // (the old BindingPath.Parse call) only cost allocations — one
            // Trim + Split + array per bound segment per frame.
            if (parent != null && BindingResolver.TryResolveSegment(parent, segment, out value)) {
                return true;
            }
            value = null;
            return false;
        }
    }
}
