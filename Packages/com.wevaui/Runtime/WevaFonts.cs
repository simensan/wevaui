using Weva.Text.TextCore;

namespace Weva {
    /// <summary>
    /// Public entry point for using your own fonts with Weva.
    /// </summary>
    /// <remarks>
    /// You usually don't need this — fonts resolve from standard locations and
    /// plain CSS already:
    /// <list type="bullet">
    /// <item>A custom family by name: drop <c>Assets/Resources/Fonts/MyFont.ttf</c>
    /// (and -Bold / -Italic variants), then use it in CSS:
    /// <c>body { font-family: "MyFont", sans-serif; }</c></item>
    /// <item>@font-face in your stylesheet:
    /// <c>@font-face { font-family: "MyFont"; src: url("Assets/UI/Fonts/MyFont.ttf"); } h1 { font-family: "MyFont"; }</c></item>
    /// <item>An OS-installed font by name (e.g. "Segoe UI" on Windows) resolves to
    /// the user's own installed copy — no bundling required.</item>
    /// </list>
    /// This class is the programmatic equivalent, for when registering at runtime
    /// (from a controller, a settings screen, a mod loader) is more convenient
    /// than authoring CSS. All methods are idempotent and safe to call before or
    /// after the document is built.
    /// </remarks>
    public static class WevaFonts {
        /// <summary>
        /// Register a custom family from a .ttf/.otf path (project-relative like
        /// "Assets/UI/Fonts/MyFont.ttf", or absolute). Use the family name in
        /// CSS: <c>font-family: "MyFont"</c>. Covers the full weight range as a single
        /// face; call the overload for separate weight/italic faces.
        /// </summary>
        public static void Register(string family, string ttfPath) {
            FontResolver.RegisterFont(family, ttfPath);
        }

        /// <summary>
        /// Register one weight/style face of a family. Call once per face (e.g.
        /// Regular 100–600, Bold 700–1000, Italic) to get proper weight matching.
        /// </summary>
        public static void Register(
            string family, string ttfPath,
            int weightMin, int weightMax, bool italic) {
            FontResolver.RegisterFontFace(family, ttfPath, weightMin, weightMax, italic);
        }

        /// <summary>
        /// Replace the bundled default (Inter) for unstyled text and the generic
        /// families (sans-serif / serif / monospace / system-ui). After this,
        /// any element without an explicit, resolvable font-family renders with
        /// your font. boldPath / italicPath are optional; the regular face is
        /// used for them when omitted.
        /// </summary>
        public static void SetDefault(string ttfPath, string boldPath = null, string italicPath = null) {
            if (string.IsNullOrEmpty(ttfPath)) return;
            string bold = string.IsNullOrEmpty(boldPath) ? ttfPath : boldPath;
            string italic = string.IsNullOrEmpty(italicPath) ? ttfPath : italicPath;
            string[] families = { "sans-serif", "serif", "monospace", "system-ui" };
            foreach (var family in families) {
                // Drop the bundled default's faces for this generic family so the
                // custom faces win resolution, then register the custom faces
                // (same shape as SdfBootstrap.EnsurePackageDefaultRegistered).
                FontResolver.UnregisterFont(family);
                FontResolver.RegisterFontFace(family, ttfPath, 100f, 600f, false);
                FontResolver.RegisterFontFace(family, bold, 700f, 1000f, false);
                FontResolver.RegisterFontFace(family, italic, 100f, 1000f, true);
            }
        }
    }
}
