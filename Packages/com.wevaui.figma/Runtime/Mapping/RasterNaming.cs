using Weva.Figma.Model;

namespace Weva.Figma.Mapping
{
    /// <summary>
    /// Deterministic output file names for assets that can't be expressed in the
    /// Weva subset and must be rasterized (image fills, vector shapes). Shared
    /// by the style mapper (which references the file) and the exporter (which
    /// records the request), so the URL and the request never drift.
    /// </summary>
    public static class RasterNaming
    {
        public static string ImageFile(string imageRef)
            => "images/" + Safe(imageRef, "image") + ".png";

        public static string VectorFile(FigmaNode node)
            => "images/" + Safe(node?.Name, "vector") + "-" + Safe(node?.Id, "0") + ".png";

        static string Safe(string s, string fallback)
        {
            string id = CssText.SanitizeIdent(s);
            return id.Length > 0 ? id : fallback;
        }
    }
}
