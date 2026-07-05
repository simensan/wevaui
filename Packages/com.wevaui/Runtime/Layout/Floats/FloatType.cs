namespace Weva.Layout.Floats {
    // CSS 2.1 §9.5.1 `float` values. `inline-start` / `inline-end` aliases
    // to Left / Right since we don't support RTL writing modes (CSS Logical
    // Properties Level 1 §4.1).
    public enum FloatType {
        None,
        Left,
        Right
    }

    // CSS 2.1 §9.5.2 `clear` values. `inline-start` / `inline-end` again
    // alias to Left / Right.
    public enum ClearType {
        None,
        Left,
        Right,
        Both
    }
}
