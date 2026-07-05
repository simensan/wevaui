using Weva.Css.Cascade;

namespace Weva.ViewTransitions {
    public static class ViewTransitionProperties {
        static bool registered;

        public static void EnsureRegistered() {
            if (registered) return;
            registered = true;
            CssProperties.Register("view-transition-name", false, "none");
        }
    }
}
