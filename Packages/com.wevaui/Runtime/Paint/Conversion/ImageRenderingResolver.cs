using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // Resolves the CSS `image-rendering` property into an
    // `ImageRenderingMode` enum value. Allocation-free via the per-style
    // parsed-value cache (ComputedStyle.GetParsed) + typed pattern match
    // on CssKeyword / CssIdentifier.
    internal static class ImageRenderingResolver {
        public static ImageRenderingMode Resolve(ComputedStyle style) {
            if (style == null) return ImageRenderingMode.Auto;
            // Single-keyword grammar; the parser produces CssKeyword for
            // known identifiers and CssIdentifier for unrecognized ones.
            // Both expose the token via Identifier / Name — alloc-free
            // dispatch.
            var parsed = style.GetParsed(CssProperties.ImageRenderingId);
            if (parsed == null) return ImageRenderingMode.Auto;
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) return ImageRenderingMode.Auto;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "pixelated")) return ImageRenderingMode.Pixelated;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "crisp-edges")) return ImageRenderingMode.CrispEdges;
            // `auto`, `smooth`, `high-quality`, and any unknown value all
            // collapse to Auto — backends apply their default sampler.
            return ImageRenderingMode.Auto;
        }
    }
}
