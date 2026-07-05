using System;
using System.IO;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Testing.Goldens;

namespace Weva.BaselineGen {
    // Standalone smoke test of the rasterizer primitives + golden round-trip.
    // Mirrors the NUnit RasterizerPrimitiveTests in spirit so we can exercise
    // the headless path without Unity Test Runner.
    static class TestSelfCheck {
        public static int Run(string root) {
            int failures = 0;

            failures += Check("FillRect emits solid color at expected pixels", () => {
                var r = new SoftwareRasterizer(16, 16);
                r.Clear(0, 0, 0, 0);
                var bounds = new Rect(4, 4, 8, 8);
                var brush = Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f));
                r.Submit(new FillRectCommand(bounds, brush));
                int Pixel(int x, int y) => (y * 16 + x) * 4;
                Expect("outside transparent", r.Pixels[Pixel(2, 2) + 3] == 0);
                Expect("inside red R", r.Pixels[Pixel(8, 8) + 0] == 255);
                Expect("inside red G", r.Pixels[Pixel(8, 8) + 1] == 0);
                Expect("inside red B", r.Pixels[Pixel(8, 8) + 2] == 0);
                Expect("inside alpha", r.Pixels[Pixel(8, 8) + 3] == 255);
            });

            failures += Check("Clip stack constrains subsequent draws", () => {
                var r = new SoftwareRasterizer(16, 16);
                r.Clear(0, 0, 0, 0);
                r.Submit(new PushClipCommand(new Rect(0, 0, 8, 8)));
                r.Submit(new FillRectCommand(new Rect(0, 0, 16, 16),
                    Brush.SolidColor(new LinearColor(0f, 1f, 0f, 1f))));
                r.Submit(new PopClipCommand());
                int Pixel(int x, int y) => (y * 16 + x) * 4;
                Expect("inside clip drawn", r.Pixels[Pixel(4, 4) + 1] == 255);
                Expect("outside clip untouched", r.Pixels[Pixel(12, 12) + 3] == 0);
            });

            failures += Check("Opacity stack composes multiplicatively", () => {
                var r = new SoftwareRasterizer(8, 8);
                r.Clear(0, 0, 0, 255);
                r.Submit(new PushOpacityCommand(0.5));
                r.Submit(new PushOpacityCommand(0.5));
                r.Submit(new FillRectCommand(new Rect(0, 0, 8, 8),
                    Brush.SolidColor(new LinearColor(1f, 1f, 1f, 1f))));
                r.Submit(new PopOpacityCommand());
                r.Submit(new PopOpacityCommand());
                int o = (4 * 8 + 4) * 4;
                byte red = r.Pixels[o + 0];
                Expect($"stacked opacity ~64 (got {red})", red >= 55 && red <= 75);
            });

            failures += Check("PNG round-trips RGBA buffer", () => {
                int w = 12, h = 7;
                byte[] rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        int i = (y * w + x) * 4;
                        rgba[i + 0] = (byte)(x * 17);
                        rgba[i + 1] = (byte)(y * 31);
                        rgba[i + 2] = (byte)((x + y) * 13);
                        rgba[i + 3] = 255;
                    }
                }
                byte[] png = PngWriter.Encode(rgba, w, h);
                var dec = PngReader.Decode(png);
                Expect("width", dec.Width == w);
                Expect("height", dec.Height == h);
                bool matches = true;
                for (int i = 0; i < rgba.Length; i++) if (dec.Rgba[i] != rgba[i]) { matches = false; break; }
                Expect("rgba round-trip", matches);
            });

            failures += Check("GoldenRunner.Compare detects identical images", () => {
                byte[] png = GoldenRunner.RenderToPng("<div></div>", "", 64, 32);
                var cmp = GoldenRunner.Compare(png, png, 0.0);
                Expect("identical compare passes", cmp.Passed);
                Expect("zero diffs", cmp.DifferingPixels == 0);
            });

            failures += Check("GoldenRunner.Compare detects different images", () => {
                byte[] a = GoldenRunner.RenderToPng("<div></div>", ".x { width: 50px; }", 32, 32);
                byte[] b = GoldenRunner.RenderToPng("<div class=\"x\"></div>", ".x { width: 50px; height: 30px; background-color: red; }", 32, 32);
                var cmp = GoldenRunner.Compare(a, b, 0.0);
                Expect("differing images fail", !cmp.Passed);
            });

            failures += Check("Dashed border emits stripes", () => {
                var r = new SoftwareRasterizer(40, 20);
                r.Clear(255, 255, 255, 255);
                var edge = new BorderEdge(BorderStyle.Dashed, 4, LinearColor.Black);
                var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
                r.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
                Expect("stroke at x=0", r.Pixels[(0 * 40 + 0) * 4 + 0] < 50);
                Expect("gap at x=12", r.Pixels[(0 * 40 + 12) * 4 + 0] > 200);
                Expect("stroke at x=16", r.Pixels[(0 * 40 + 16) * 4 + 0] < 50);
            });

            failures += Check("Dotted border emits dots", () => {
                var r = new SoftwareRasterizer(40, 20);
                r.Clear(255, 255, 255, 255);
                var edge = new BorderEdge(BorderStyle.Dotted, 4, LinearColor.Black);
                var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
                r.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
                Expect("dot center black", r.Pixels[(2 * 40 + 2) * 4 + 0] < 80);
                Expect("between dots white", r.Pixels[(2 * 40 + 6) * 4 + 0] > 200);
            });

            failures += Check("Double border emits two strokes with gap", () => {
                var r = new SoftwareRasterizer(20, 20);
                r.Clear(255, 255, 255, 255);
                var edge = new BorderEdge(BorderStyle.Double, 9, LinearColor.Black);
                var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
                r.Submit(new StrokeBorderCommand(new Rect(0, 0, 20, 20), borders));
                Expect("first stroke black", r.Pixels[(0 * 20 + 5) * 4 + 0] < 50);
                Expect("middle gap white", r.Pixels[(4 * 20 + 5) * 4 + 0] > 200);
                Expect("last stroke black", r.Pixels[(7 * 20 + 5) * 4 + 0] < 50);
            });

            failures += Check("Inset shadow paints inside box", () => {
                var r = new SoftwareRasterizer(80, 60);
                r.Clear(255, 255, 255, 255);
                var bounds = new Rect(10, 10, 60, 40);
                var sh = new BoxShadow(8, 8, 4, 0, LinearColor.Black, inset: true);
                r.Submit(new DrawShadowCommand(bounds, BorderRadii.Zero, sh));
                // With offset (+8, +8) the dark band sits on the top-left edge (the
                // hole shifts down-right). (12, 12) is just inside that band.
                Expect("top-left rim darkened", r.Pixels[((12) * 80 + 12) * 4 + 0] < 255);
                Expect("interior un-shadowed", r.Pixels[((40) * 80 + 40) * 4 + 0] == 255);
                Expect("outside untouched", r.Pixels[((5) * 80 + 5) * 4 + 0] == 255);
            });

            failures += Check("Drop-shadow filter casts blurred halo", () => {
                var r = new SoftwareRasterizer(60, 60);
                r.Clear(255, 255, 255, 255);
                var bounds = new Rect(0, 0, 60, 60);
                var ds = new DropShadowFilter(8, 8, 0, LinearColor.Black);
                var chain = new FilterChain(new FilterFunction[] { ds });
                r.Submit(new PushFilterCommand(bounds, chain));
                // Red rect over white background so the diff registers as painted.
                r.Submit(new FillRectCommand(new Rect(10, 10, 20, 20),
                    Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f)), BorderRadii.Zero));
                r.Submit(new PopFilterCommand());
                // Rect was at (10,10..30,30); with offset (+8,+8) the shadow stamp
                // covers (18,18..38,38). (35, 35) is solidly inside the stamp but
                // outside the foreground (which ends at 30, 30), so it should darken.
                Expect("shadow halo at (35,35)", r.Pixels[(35 * 60 + 35) * 4 + 0] < 200);
                Expect("rect interior red", r.Pixels[(20 * 60 + 20) * 4 + 0] > 200);
                Expect("far-out white", r.Pixels[(5 * 60 + 5) * 4 + 0] == 255);
            });

            return failures;
        }

        static int Check(string label, Action body) {
            try { body(); Console.WriteLine($"PASS  {label}"); return 0; }
            catch (Exception ex) { Console.WriteLine($"FAIL  {label}: {ex.Message}"); return 1; }
        }

        static void Expect(string label, bool condition) {
            if (!condition) throw new Exception(label);
        }
    }
}
