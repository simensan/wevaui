using System.IO;
using UnityEngine;
using Weva.Figma.Fidelity;
using Weva.Testing.Goldens;

namespace Weva.Figma.EditorTools
{
    /// <summary>
    /// Closes the fidelity loop: renders an exported HTML+CSS document through the
    /// engine's CPU rasterizer (<see cref="GoldenRunner.Render"/>) and pixel-diffs
    /// it against the frame's Figma-rendered reference PNG using the package's own
    /// <see cref="ImageDiff"/>. The reference PNG should be rendered at scale 1 so
    /// its pixels line up with CSS px; the Weva side is rendered at the PNG's
    /// dimensions so the buffers always match.
    ///
    /// NEEDS UNITY VALIDATION: written without a Unity compile/run available —
    /// in particular the Texture2D row-flip (Unity is bottom-up; GoldenRunner is
    /// top-down) should be confirmed against a known reference.
    /// </summary>
    public static class FigmaFidelityChecker
    {
        public static FidelityReport Check(string html, string css, byte[] figmaReferencePng,
            FidelityThresholds thresholds = null, string heatmapOutputPath = null)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (figmaReferencePng == null || !tex.LoadImage(figmaReferencePng))
            {
                Object.DestroyImmediate(tex);
                return new FidelityReport { SizeMismatch = true };
            }

            int w = tex.width, h = tex.height;
            byte[] figmaRgba = TopDownRgba(tex);
            Object.DestroyImmediate(tex);

            byte[] rendered = GoldenRunner.Render(html, css, w, h); // top-down RGBA, CSS-space
            FidelityReport report = ImageDiff.Compare(rendered, w, h, figmaRgba, w, h,
                thresholds, buildHeatmap: heatmapOutputPath != null);

            if (heatmapOutputPath != null && report.Heatmap != null)
                File.WriteAllBytes(heatmapOutputPath, PngWriter.Encode(report.Heatmap, w, h));

            return report;
        }

        // Unity textures are stored bottom-up; flip to the top-down order GoldenRunner uses.
        static byte[] TopDownRgba(Texture2D tex)
        {
            Color32[] px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w;
                int dstRow = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[srcRow + x];
                    int di = dstRow + x * 4;
                    rgba[di] = c.r; rgba[di + 1] = c.g; rgba[di + 2] = c.b; rgba[di + 3] = c.a;
                }
            }
            return rgba;
        }
    }
}
