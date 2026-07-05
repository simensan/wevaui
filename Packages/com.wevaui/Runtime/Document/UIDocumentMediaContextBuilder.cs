using Weva.Css.Media;

namespace Weva.Documents {
    // Builds a MediaContext for the cascade, given a viewport size + color
    // scheme hint. WevaDocument resolves the viewport from one of three sources
    // (in priority order):
    //   1. an explicit override (Vector2 != (0, 0))
    //   2. the camera's pixel size (Unity-side, plumbed in by WevaDocument)
    //   3. UIDocumentDefaults.DefaultViewport(Width|Height)Px
    //
    // The builder itself is pure data; WevaDocument computes the viewport floats
    // and hands them in. Keeping this separate makes it trivial to test the
    // override semantics headlessly.
    public static class UIDocumentMediaContextBuilder {
        public static MediaContext Build(double viewportWidthPx, double viewportHeightPx, double dpiPixelsPerInch, bool prefersDark) {
            if (viewportWidthPx <= 0) viewportWidthPx = UIDocumentDefaults.DefaultViewportWidthPx;
            if (viewportHeightPx <= 0) viewportHeightPx = UIDocumentDefaults.DefaultViewportHeightPx;
            if (dpiPixelsPerInch <= 0) dpiPixelsPerInch = UIDocumentDefaults.DefaultDpi;
            var scheme = prefersDark ? ColorScheme.Dark : ColorScheme.Light;
            return new MediaContext(
                viewportWidthPx,
                viewportHeightPx,
                dpiPixelsPerInch,
                scheme,
                HoverCapability.Hover,
                PointerCapability.Fine,
                false,
                MediaType.Screen);
        }

        public static MediaContext Resolve(
            double overrideWidthPx,
            double overrideHeightPx,
            double cameraWidthPx,
            double cameraHeightPx,
            double dpiPixelsPerInch,
            bool prefersDark) {
            double w, h;
            if (overrideWidthPx > 0 && overrideHeightPx > 0) {
                w = overrideWidthPx;
                h = overrideHeightPx;
            } else if (cameraWidthPx > 0 && cameraHeightPx > 0) {
                w = cameraWidthPx;
                h = cameraHeightPx;
            } else {
                w = UIDocumentDefaults.DefaultViewportWidthPx;
                h = UIDocumentDefaults.DefaultViewportHeightPx;
            }
            return Build(w, h, dpiPixelsPerInch, prefersDark);
        }
    }
}
