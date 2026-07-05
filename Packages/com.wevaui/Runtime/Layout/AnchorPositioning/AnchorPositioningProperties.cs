using Weva.Css.Cascade;

namespace Weva.Layout.AnchorPositioning {
    // Registers `anchor-name` and `position-anchor` CSS properties on first
    // touch. Loaded eagerly via [RuntimeInitializeOnLoadMethod] in the Unity
    // build, and on first AnchorRegistry construction in headless tests.
    public static class AnchorPositioningProperties {
        static bool registered;

        public static void EnsureRegistered() {
            if (registered) return;
            registered = true;
            // All are non-inherited per the CSS Anchor Positioning spec.
            CssProperties.Register("anchor-name", false, "none");
            CssProperties.Register("position-anchor", false, "auto");
            // v2 — comma-separated list of `flip-block` / `flip-inline` /
            // `flip-block flip-inline` strategies attempted on viewport overflow.
            CssProperties.Register("position-try-fallbacks", false, "none");
        }
    }
}
