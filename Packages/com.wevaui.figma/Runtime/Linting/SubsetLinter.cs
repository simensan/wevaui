using System.Collections.Generic;
using Weva.Figma.Model;

namespace Weva.Figma.Linting
{
    public sealed class LintOptions
    {
        /// <summary>Report rasterizable nodes (vectors, image fills) as Info (handled) rather than Warning.</summary>
        public bool RasterAsInfo = true;
    }

    /// <summary>
    /// Validates a Figma node tree against the Weva conformance subset and
    /// reports what won't survive export faithfully — so the gaps surface in the
    /// design tool / importer instead of as silent visual drift at runtime.
    ///
    /// Walks visible nodes in document order; output is deterministic.
    /// </summary>
    public static class SubsetLinter
    {
        // Node types the exporter knows how to handle (as containers, boxes, or rasterized shapes).
        static readonly HashSet<string> SupportedTypes = new HashSet<string>
        {
            "DOCUMENT", "CANVAS", "SECTION", "FRAME", "GROUP",
            "COMPONENT", "COMPONENT_SET", "INSTANCE",
            "TEXT", "RECTANGLE", "ELLIPSE",
            "VECTOR", "LINE", "STAR", "REGULAR_POLYGON", "BOOLEAN_OPERATION",
        };

        public static LintReport Lint(FigmaNode root, LintOptions options = null)
        {
            options = options ?? new LintOptions();
            var report = new LintReport();
            Walk(root, report, options);
            return report;
        }

        static void Walk(FigmaNode n, LintReport r, LintOptions o)
        {
            if (n == null || !n.Visible) return;
            Check(n, r, o);
            if (n.IsVector) return; // a rasterized shape absorbs its descendants
            if (n.HasChildren)
                foreach (FigmaNode c in n.Children)
                    Walk(c, r, o);
        }

        static void Check(FigmaNode n, LintReport r, LintOptions o)
        {
            LintSeverity raster = o.RasterAsInfo ? LintSeverity.Info : LintSeverity.Warning;

            if (n.Type != null && !SupportedTypes.Contains(n.Type))
                r.Add(LintSeverity.Warning, "node-type-unsupported", n,
                    $"Node type {n.Type} is outside the supported subset and may not export.");

            if (n.IsVector)
                r.Add(raster, "vector-rasterized", n,
                    $"{n.Type} has no CSS-subset equivalent; it will be rasterized to PNG.",
                    "Keep as an exported image, or rebuild simple shapes with frames + border-radius.");

            if (n.BlendMode != null && n.BlendMode != "NORMAL" && n.BlendMode != "PASS_THROUGH")
                r.Add(LintSeverity.Warning, "blend-mode-unsupported", n,
                    $"blendMode {n.BlendMode} is not in the subset; it will render as normal.");

            if (System.Math.Abs(n.Raw["rotation"].AsDouble()) > 1e-4)
                r.Add(LintSeverity.Warning, "rotation-not-exported", n,
                    "Node rotation is not exported; the element will be axis-aligned.",
                    "Bake the rotation into an image, or add transform: rotate() by hand.");

            if (n.Raw["isMask"].AsBool())
                r.Add(LintSeverity.Warning, "mask-unsupported", n,
                    "Mask layers have no subset equivalent (clip-path is out of scope); content won't be clipped.");

            CheckFills(n, r, raster);
            CheckStrokes(n, r);
            CheckText(n, r);
        }

        static void CheckFills(FigmaNode n, LintReport r, LintSeverity raster)
        {
            if (n.Fills == null) return;

            int visible = 0;
            foreach (FigmaPaint f in n.Fills)
                if (f.Visible && f.Opacity > 0) visible++;
            if (visible > 1 && !n.IsText)
                r.Add(LintSeverity.Warning, "multi-fill-flattened", n,
                    $"{visible} visible fills; only the topmost is exported as the background.");

            FigmaPaint top = FigmaNode.FirstVisible(n.Fills);
            if (top == null) return;
            if (top.IsImage)
                r.Add(raster, "image-fill-fetch", n,
                    "Image fill; the bitmap must be fetched and bundled with the export.");
            if (top.Type == "GRADIENT_ANGULAR" || top.Type == "GRADIENT_DIAMOND")
                r.Add(LintSeverity.Warning, "gradient-type-approximated", n,
                    $"{top.Type} has no CSS-subset equivalent and will not be exported.",
                    "Bake to an image, or switch to a linear/radial gradient.");
        }

        static void CheckStrokes(FigmaNode n, LintReport r)
        {
            if (n.StrokeWeight <= 0 || n.Strokes == null) return;
            FigmaPaint s = FigmaNode.FirstVisible(n.Strokes);
            if (s != null && !s.IsSolid)
                r.Add(LintSeverity.Warning, "stroke-not-solid", n,
                    "Gradient/image strokes aren't exported; the border will be omitted.");
            if (n.StrokeAlign == "OUTSIDE" || n.StrokeAlign == "CENTER")
                r.Add(LintSeverity.Info, "stroke-align-approximated", n,
                    $"strokeAlign {n.StrokeAlign} is approximated as INSIDE (CSS border-box).");
        }

        static void CheckText(FigmaNode n, LintReport r)
        {
            if (!n.IsText) return;
            Json.JsonValue overrides = n.Raw["characterStyleOverrides"];
            Json.JsonValue table = n.Raw["styleOverrideTable"];
            bool mixed = (overrides.IsArray && overrides.Count > 0) || (table.IsObject && table.Count > 0);
            if (mixed)
                r.Add(LintSeverity.Warning, "text-mixed-styles-flattened", n,
                    "Text has per-character style overrides; only the base style is exported.",
                    "Split into separate text nodes, or accept the flattened style.");
        }
    }
}
