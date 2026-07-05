using System;

namespace Weva.Events {
    [Flags]
    public enum KeyModifiers {
        None = 0,
        Shift = 1 << 0,
        Ctrl = 1 << 1,
        Alt = 1 << 2,
        Meta = 1 << 3
    }
}
