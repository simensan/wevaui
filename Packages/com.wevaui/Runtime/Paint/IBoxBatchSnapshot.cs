namespace Weva.Paint {
    // Painter-side view of a per-subtree batch snapshot. The concrete type
    // (Weva.Rendering.URP.BoxBatchSnapshot) lives in the URP backend and
    // carries UnityEngine.Vector4 fields for the captured parent context.
    // BoxToPaintConverter only needs to read three primitives off it — keeping
    // the interface here lets non-URP test harnesses compile without the URP
    // assembly + UnityEngine present.
    public interface IBoxBatchSnapshot {
        bool ContainsFilterScopes { get; }
        double AnchorX { get; }
        double AnchorY { get; }
        // Returns the snapshot to its pool (if any). Callers should invoke
        // this whenever the snapshot is being replaced or discarded from a
        // long-lived cache (e.g. the painter's per-box snapshot dictionary)
        // so backing buffers can be reclaimed instead of GC'd. No-op for
        // non-pooled implementations.
        void Recycle();
    }

    // Optional extension for backend snapshots whose resources can be
    // invalidated independently of paint/layout state, for example cached
    // glyph atlas UVs after a text atlas grow/repack.
    public interface IValidatedBoxBatchSnapshot : IBoxBatchSnapshot {
        bool IsValid { get; }
    }
}
