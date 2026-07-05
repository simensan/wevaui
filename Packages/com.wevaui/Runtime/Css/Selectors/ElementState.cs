using System;

namespace Weva.Css.Selectors {
    [Flags]
    public enum ElementState {
        None = 0,
        Hover = 1 << 0,
        Focus = 1 << 1,
        FocusVisible = 1 << 2,
        FocusWithin = 1 << 3,
        Active = 1 << 4,
        Disabled = 1 << 5,
        Checked = 1 << 6,
        PlaceholderShown = 1 << 7,
        Root = 1 << 8,
        UserInteracted = 1 << 9,
        Target = 1 << 10,
        // CSS Selectors L4 §11.4 — set when the UA pre-fills a form control's
        // value (browser password manager, address autofill, etc.). Hosts must
        // wire their IElementStateProvider impl to set this bit; the headless
        // NullStateProvider never sets it (no autofill source in CI).
        Autofill = 1 << 11
    }
}
