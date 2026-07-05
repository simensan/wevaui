using System;

namespace Weva.Reactive {
    [Flags]
    public enum InvalidationKind {
        None      = 0,
        Structure = 1,
        Style     = 2,
        Layout    = 4,
        Paint     = 8,
        Composite = 16,
        // Pseudo-class state flip on an element (Hover/Focus/Active/Checked/
        // PlaceholderShown/Disabled/etc.). Implies Style for downstream
        // consumers — a state change can only affect computed values via
        // selector matching — but is distinguishable so the cascade can
        // route it through a per-element-state-digest fast path that does
        // NOT discard cache entries for unaffected elements. PaintConverter
        // and LayoutEngine treat PseudoClassState identically to Style.
        PseudoClassState = 32,
        All       = Structure | Style | Layout | Paint | Composite | PseudoClassState,
    }
}
