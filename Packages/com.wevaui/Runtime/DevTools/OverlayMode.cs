using System;

namespace Weva.DevTools {
    // Composable. Match the names listed in the spec (Off / Outlines /
    // DirtyTracking / Performance / All), keeping flags semantics so authors
    // can compose subsets — e.g. "DirtyTracking | Performance" for a perf-
    // tuning session without box outlines cluttering the screen. Hover
    // inspection is implicit whenever the overlay is enabled at all.
    [Flags]
    public enum OverlayMode {
        Off = 0,
        Outlines = 1,
        DirtyTracking = 2,
        Performance = 4,
        All = Outlines | DirtyTracking | Performance,
    }
}
