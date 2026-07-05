using System;
using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;
using Weva.Parsing;

namespace Weva.Testing.Goldens {
    public static class GoldenRunner {
        // Overload that accepts an optional IImageRegistry so tests can supply
        // in-memory images for background-image: url() rendering in the software
        // rasterizer. Passing null (or using the 4-arg overload) keeps the existing
        // behavior — no image registry is wired and url() backgrounds render as
        // transparent (no-op), so existing golden PNGs are unaffected.
        public static byte[] Render(string html, string css, int width, int height,
                                    IImageRegistry imageRegistry = null) {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            var doc = HtmlParser.Parse(html ?? string.Empty, new ParseOptions { ThrowOnError = false });

            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UserAgentStylesheet.Parse());
            if (!string.IsNullOrEmpty(css)) {
                var authorSheet = CssParser.Parse(css, new ParseOptions { ThrowOnError = false });
                sheets.Add(OriginatedStylesheet.Author(authorSheet));
            }

            var cascade = new CascadeEngine(sheets);
            var styles = cascade.ComputeAll(doc);

            // W1 font-determinism (inc 3+6): the headless golden harness now uses
            // InterFontMetrics as the default so it measures text with the SAME
            // face the Chrome reference was generated with (capture-all-chrome-
            // layouts.mjs injects @font-face for Weva-Default*.ttf + forces
            // body{font-family:'Inter',sans-serif}). The inter instance is
            // registered under every alias a snippet might declare ("Inter",
            // "sans-serif", "serif", "system-ui") so `font-family: Inter`,
            // `font-family: sans-serif`, and `font-family: system-ui` all resolve
            // to the same per-glyph table. "monospace" stays on ChromeMonospace()
            // (Inter is proportional; monospace snippets keep their existing
            // calibration).
            var fontMetrics = InterFontMetrics.Instance;
            var ctx = new LayoutContext(fontMetrics) {
                ViewportWidthPx = width,
                ViewportHeightPx = height,
                Snapshot = cascade.LastSnapshot,
            };
            // Explicit family registrations so the font-family stack walk in
            // LayoutContext.ResolveFamilyUncached hits the right instance
            // regardless of which alias the snippet author uses.
            ctx.RegisterFont("Inter", InterFontMetrics.Instance);
            ctx.RegisterFont("sans-serif", InterFontMetrics.Instance);
            ctx.RegisterFont("serif", InterFontMetrics.Instance);
            ctx.RegisterFont("system-ui", InterFontMetrics.Instance);
            ctx.RegisterFont("monospace", MonoFontMetrics.ChromeMonospace());
            var layout = new LayoutEngine(fontMetrics);
            // Wire `::backdrop` resolution so snippets exercising open modal
            // dialogs / popovers render their UA-default top-layer overlay.
            // Without this hook BoxBuilder skips backdrop synthesis entirely.
            layout.BackdropStyleOf = e => cascade.ComputeBackdrop(e);
            // ::before / ::after pseudo-element styles — without these wired
            // the BoxBuilder skips pseudo generation entirely, so any demo
            // with `content: ""` decorative overlays (e.g. match3's
            // `.tile::before` highlight) renders nothing for the pseudo.
            // The Unity runtime wires the same resolver in UIDocumentBuilder.
            layout.BeforeStyleOf = e => cascade.ComputeBefore(e);
            layout.AfterStyleOf = e => cascade.ComputeAfter(e);
            layout.MarkerStyleOf = e => cascade.ComputeMarker(e);
            var rootBox = layout.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            var converter = new BoxToPaintConverter();
            PaintList list = converter.Convert(rootBox);

            var rasterizer = new SoftwareRasterizer(width, height, fontMetrics, imageRegistry);
            rasterizer.Clear(255, 255, 255, 255);
            // Submit each command directly. The default Submit(PaintList) on IRenderBackend
            // is a default-interface-method that resolves through dynamic dispatch on the
            // command type — calling it concretely on SoftwareRasterizer hits the overload
            // resolver instead, so we drive the loop ourselves.
            foreach (var c in list.Commands) c.Submit(rasterizer);
            return rasterizer.Pixels;
        }

        public static byte[] RenderToPng(string html, string css, int width, int height) {
            byte[] rgba = Render(html, css, width, height);
            return PngWriter.Encode(rgba, width, height);
        }

        public static GoldenComparison Compare(byte[] actualPng, byte[] expectedPng, double tolerance = 0.0) {
            if (actualPng == null) throw new ArgumentNullException(nameof(actualPng));
            if (expectedPng == null) throw new ArgumentNullException(nameof(expectedPng));

            PngImage actual, expected;
            try { actual = PngReader.Decode(actualPng); }
            catch (Exception ex) { return new GoldenComparison(false, 0, 1.0, 1.0, null, 0, 0, "actual PNG decode failed: " + ex.Message); }
            try { expected = PngReader.Decode(expectedPng); }
            catch (Exception ex) { return new GoldenComparison(false, 0, 1.0, 1.0, null, 0, 0, "expected PNG decode failed: " + ex.Message); }

            if (actual.Width != expected.Width || actual.Height != expected.Height) {
                return new GoldenComparison(false, actual.Width * actual.Height, 1.0, 1.0, null,
                    actual.Width, actual.Height,
                    $"size mismatch: actual {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}");
            }

            int w = actual.Width;
            int h = actual.Height;
            byte[] a = actual.Rgba;
            byte[] e = expected.Rgba;
            byte[] diff = new byte[w * h * 4];

            int differing = 0;
            double sumSq = 0.0;
            double maxErr = 0.0;
            for (int i = 0; i < a.Length; i += 4) {
                int dr = a[i + 0] - e[i + 0];
                int dg = a[i + 1] - e[i + 1];
                int db = a[i + 2] - e[i + 2];
                int da = a[i + 3] - e[i + 3];
                // Per-pixel error: root-mean-square across channels, normalized to 0..1.
                double sq = (dr * dr + dg * dg + db * db + da * da) / (4.0 * 255.0 * 255.0);
                double err = Math.Sqrt(sq);
                sumSq += sq;
                if (err > maxErr) maxErr = err;
                if (err > tolerance) {
                    differing++;
                    diff[i + 0] = 255;
                    diff[i + 1] = 0;
                    diff[i + 2] = 0;
                    diff[i + 3] = 255;
                } else {
                    // Faded original for context.
                    diff[i + 0] = (byte)(e[i + 0] / 4);
                    diff[i + 1] = (byte)(e[i + 1] / 4);
                    diff[i + 2] = (byte)(e[i + 2] / 4);
                    diff[i + 3] = 255;
                }
            }

            int totalPixels = w * h;
            double rms = totalPixels > 0 ? Math.Sqrt(sumSq / totalPixels) : 0.0;
            bool passed = maxErr <= tolerance;
            string reason = passed ? null : $"{differing} pixels differ; maxError={maxErr:F4}; rms={rms:F4}; tolerance={tolerance:F4}";
            return new GoldenComparison(passed, differing, maxErr, rms, diff, w, h, reason);
        }
    }
}
