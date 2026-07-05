// Stub for Weva.Text.TextCore.FontResolver. The Text directory is excluded from
// this test project (see csproj), so we provide a minimal no-op shim here so
// the project compiles. CssParser calls RegisterFontFace (weight/style-aware
// path) when a @font-face rule is parsed; the stub discards the registration
// because headless tests exercise the CSS parsing only, not font loading.
namespace Weva.Text.TextCore {
    internal static class FontResolver {
        public static void RegisterFont(string family, string src) { }
        public static void RegisterFontFace(
            string family, string path,
            float weightMin, float weightMax, bool isItalic) { }
        public static void UnregisterFont(string family) { }
    }
}
