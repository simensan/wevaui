using Weva.Css.Cascade;

namespace Weva.Layout.Scrolling.Smooth {
    public static class SmoothScrollProperties {
        static bool registered;

        public static void EnsureRegistered() {
            if (registered) return;
            registered = true;
            CssProperties.Register("scroll-behavior", false, "auto");
        }
    }
}
