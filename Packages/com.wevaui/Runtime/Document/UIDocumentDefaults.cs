using System;
using Weva.Layout.Text;

namespace Weva.Documents {
    // Centralized constants shared by WevaDocument, UIDocumentBuilder, and the
    // media-context builder. The MonoBehaviour reads these at lifecycle time so
    // unit tests can build the same pipeline headlessly without dragging in
    // UnityEngine.
    //
    // FontMetricsFactory is the seam the Unity-side code overrides at runtime
    // (typically from a class-initializer in a Unity-only assembly) so the
    // builder can produce TextCoreFontMetrics in Play Mode while staying on
    // MonoFontMetrics in headless tests. The builder always calls through this
    // delegate; consumers who need a specific implementation set it themselves
    // before instantiating UIDocumentBuilder.
    public static class UIDocumentDefaults {
        public const double DefaultFontSizePx = 16.0;
        public const double DefaultDpi = 96.0;
        public const double DefaultViewportWidthPx = 1920.0;
        public const double DefaultViewportHeightPx = 1080.0;
        public const string DefaultFontFamily = "sans-serif";

        static Func<IFontMetrics> fontMetricsFactory = () => new MonoFontMetrics();

        public static Func<IFontMetrics> FontMetricsFactory {
            get => fontMetricsFactory;
            set => fontMetricsFactory = value ?? (() => new MonoFontMetrics());
        }

        public static IFontMetrics CreateDefaultFontMetrics() {
            var f = fontMetricsFactory;
            var metrics = f != null ? f() : null;
            return metrics ?? new MonoFontMetrics();
        }

        // Per-family metrics resolver: maps a single (quote-stripped) family name
        // to a font-metrics instance, or null to fall through to the default.
        // This is how per-element `font-family` is honoured at LAYOUT time — the
        // TMP/SDF backend installs a resolver returning a TmpFontMetrics bound to
        // the family's registered face, so a Sniglet run is MEASURED with Sniglet
        // advances (matching what the paint dispatcher renders). Null in headless
        // tests. LayoutContext copies this onto each context at build time.
        public static Func<string, IFontMetrics> FamilyMetricsResolver;

        public static void ResetFontMetricsFactory() {
            fontMetricsFactory = () => new MonoFontMetrics();
        }
    }
}
