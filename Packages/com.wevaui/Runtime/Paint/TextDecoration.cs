using System;

namespace Weva.Paint {
    [Flags]
    public enum TextDecoration {
        None = 0,
        Underline = 1 << 0,
        Overline = 1 << 1,
        LineThrough = 1 << 2
    }

    // CSS Text Decoration 4 §3.2: line style applied uniformly to every
    // active decoration line. v1 supports the spec keywords; `Wavy` is
    // approximated as a dashed pattern at the baker level.
    public enum DecorationStyle {
        Solid = 0,
        Double = 1,
        Dotted = 2,
        Dashed = 3,
        Wavy = 4
    }
}
