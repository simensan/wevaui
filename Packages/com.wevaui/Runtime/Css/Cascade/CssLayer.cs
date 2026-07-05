using System;

namespace Weva.Css.Cascade {
    // CssLayer — represents one entry in the cascade-layer registry.
    //
    // CSS Cascade and Inheritance Module Level 5 §6.4.1: the layer-ordering
    // axis is appended between origin and specificity in the cascade sort.
    // Earlier-declared layers lose to later-declared layers regardless of
    // selector specificity. Unlayered rules count as the IMPLICIT FINAL
    // (highest-priority) layer per the spec.
    //
    // For !important declarations, the layer order is REVERSED per spec §6.4.1
    // step 5: the earliest layer wins. This mirrors how !important inverts the
    // origin order (UA < User < Author for normal; Author < User < UA for
    // important).
    //
    // v1 simplifications:
    //   - Sub-layers (`@layer base.utilities { ... }`) are flattened to a
    //     single layer named `base.utilities`. Spec semantics for nested
    //     sub-layer ordering are not implemented (they form a tree; we treat
    //     it as a flat list keyed on the dotted path).
    //   - Anonymous layers (`@layer { ... }`) are assigned the next unused
    //     ordinal and never reachable by name; the spec gives them the same
    //     treatment as named layers without the name lookup.
    public readonly struct CssLayer : IEquatable<CssLayer> {
        // Sentinel ordinal for unlayered rules. Spec: unlayered rules
        // outrank every named-or-anonymous layer. We store this as int.MaxValue
        // so the comparator is a plain ascending integer compare for normal
        // declarations; the !important inversion is handled in the comparator.
        public const int UnlayeredOrdinal = int.MaxValue;

        public string Name { get; }
        public int Ordinal { get; }

        public CssLayer(string name, int ordinal) {
            Name = name;
            Ordinal = ordinal;
        }

        public static CssLayer Unlayered => new CssLayer(null, UnlayeredOrdinal);

        public bool IsUnlayered => Ordinal == UnlayeredOrdinal;
        public bool IsAnonymous => Name == null && !IsUnlayered;

        public bool Equals(CssLayer other) => Name == other.Name && Ordinal == other.Ordinal;
        public override bool Equals(object obj) => obj is CssLayer l && Equals(l);
        public override int GetHashCode() => unchecked((Name?.GetHashCode() ?? 0) * 397 ^ Ordinal);
        public override string ToString() => IsUnlayered ? "(unlayered)" : (Name ?? $"<anon#{Ordinal}>");
    }
}
