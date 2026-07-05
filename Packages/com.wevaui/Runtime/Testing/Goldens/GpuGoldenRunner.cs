#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;
using Weva.Rendering.URP;

namespace Weva.Testing.Goldens {
    // GPU-side golden runner: parses HTML+CSS, runs the full CPU pipeline (cascade,
    // layout, paint conversion), then submits the produced UIBatcher batches to the GPU
    // through the REAL BatchedURPRenderBackend + Hidden/Weva/Quad shader path.
    //
    // This differs from GoldenRunner (SoftwareRasterizer) in that shaders, UIBatcher,
    // and the StructuredBuffer/DrawMesh path all participate. Shader and URP batching
    // regressions are only catchable through this GPU path.
    //
    // Constraints:
    //   - Requires Unity Play-mode (RenderTexture, ReadPixels, Graphics.ExecuteCommandBuffer).
    //   - The Hidden/Weva/Quad shader must be reachable (listed in Always Included Shaders
    //     or in a URP renderer asset that imports the package's shader includes).
    //   - Text glyphs render transparent (no TextCore glyph atlas in headless tests).
    //     The goldens capture geometry, color, gradient, and border fidelity.
    //   - Does NOT touch UIPaintSourceRegistry — it drives EmitPaint directly.
    public static class GpuGoldenRunner {
        // Renders html+css through the GPU path and returns the pixel data as a Texture2D.
        // The caller is responsible for calling UnityEngine.Object.Destroy on the returned texture.
        public static Texture2D RenderToTexture(string html, string css, int width, int height) {
            if (width  <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            // ── 1. CPU pipeline: parse → cascade → layout → paint → batcher ──────────
            var doc = HtmlParser.Parse(html ?? string.Empty, new ParseOptions { ThrowOnError = false });

            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UserAgentStylesheet.Parse());
            if (!string.IsNullOrEmpty(css)) {
                sheets.Add(OriginatedStylesheet.Author(
                    CssParser.Parse(css, new ParseOptions { ThrowOnError = false })));
            }

            var cascade = new CascadeEngine(sheets);
            var styles  = cascade.ComputeAll(doc);

            // Use the Chrome-calibrated font shim (same as GoldenRunner) so layout
            // geometry matches the software goldens.
            var fontMetrics = MonoFontMetrics.ChromeSansSerif();
            var ctx = new LayoutContext(fontMetrics) {
                ViewportWidthPx  = width,
                ViewportHeightPx = height,
                Snapshot         = cascade.LastSnapshot,
            };
            ctx.RegisterFont("monospace", MonoFontMetrics.ChromeMonospace());

            var layout = new LayoutEngine(fontMetrics);
            layout.BackdropStyleOf = e => cascade.ComputeBackdrop(e);
            layout.BeforeStyleOf   = e => cascade.ComputeBefore(e);
            layout.AfterStyleOf    = e => cascade.ComputeAfter(e);
            layout.MarkerStyleOf   = e => cascade.ComputeMarker(e);
            var rootBox = layout.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            var converter = new BoxToPaintConverter();
            PaintList paintList = converter.Convert(rootBox);

            // Record into the batched backend (no URP render feature required).
            var backend = new BatchedURPRenderBackend();
            backend.BeginFrame();
            foreach (var paintCmd in paintList.Commands) paintCmd.Submit(backend);
            backend.EndFrame();

            // ── 2. GPU surface render (shared with the editor panel host) ─────────────
            // BatchedSurfaceRenderer owns the shader/material setup, chunk-mesh cache, and the
            // CommandBuffer/StructuredBuffer DrawMesh path — the same code the live editor panel
            // uses, so a shader/batching regression shows up identically in both.
            var rtDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) {
                sRGB             = false,
                useMipMap        = false,
                autoGenerateMips = false,
            };
            var rt = RenderTexture.GetTemporary(rtDesc);
            rt.filterMode = FilterMode.Point;

            var surface = new BatchedSurfaceRenderer();
            try {
                if (!surface.IsReady) {
                    // Shader not found — degrade to white so first-run auto-seeds a white
                    // baseline rather than crashing the runner.
                    RenderTexture.ReleaseTemporary(rt);
                    return MakeWhiteTexture(width, height);
                }

                // Clear to solid white (matching GoldenRunner's SoftwareRasterizer background).
                surface.Render(backend, rt, width, height, Color.white);
                // ReadPixels below syncs the GPU, so the instance buffers can be freed now.
                surface.FlushBuffers();

                // ── 3. Read pixels back ───────────────────────────────────────────────
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, false);
                RenderTexture.active = prevActive;
                return tex;
            } finally {
                surface.Dispose();
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Renders and encodes the result as PNG bytes. The bytes are compatible with
        // GoldenRunner.Compare for pixel-diff math.
        public static byte[] RenderToPng(string html, string css, int width, int height) {
            var tex = RenderToTexture(html, css, width, height);
            try {
                return tex.EncodeToPNG();
            } finally {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                else UnityEngine.Object.DestroyImmediate(tex);
#else
                UnityEngine.Object.Destroy(tex);
#endif
            }
        }

        static Texture2D MakeWhiteTexture(int width, int height) {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var px  = new Color32[width * height];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
#endif
