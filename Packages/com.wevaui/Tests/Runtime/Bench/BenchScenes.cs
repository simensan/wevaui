using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Bench {
    // Fixture builders + shared scaffolding for the Bench/* suite. Each Build*
    // helper returns a fully-warmed Scene: parsed Document, originated stylesheets,
    // a CascadeEngine that has run ComputeAll once (so its caches/snapshot are
    // primed), a LayoutContext, and the resolved style dictionary. Tests pull
    // whichever pieces they need from the Scene rather than re-running the prefix.
    //
    // Style philosophy: the scenes encode realistic shapes (cards / forms / deep
    // trees) that exercise different cost centres, but they all share the same
    // 50-rule author stylesheet so cascade timings are comparable across scales.
    internal static class BenchScenes {
        public const string UA =
            "html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr, form, fieldset, label { display: block; } " +
            "a, span, strong, em, b, i, u, code, small, button, input, select, textarea { display: inline; } " +
            "body { margin: 0; padding: 0; }";

        public const string AuthorCss =
            ".container { padding: 8px; }" +
            ".panel { padding: 4px; margin: 2px; }" +
            ".panel.zone-0 { background-color: red; }" +
            ".panel.zone-1 { background-color: blue; }" +
            ".panel.zone-2 { background-color: green; }" +
            ".panel.zone-3 { background-color: yellow; }" +
            ".list { padding-left: 16px; }" +
            ".item { font-size: 14px; padding: 2px; margin: 1px; color: black; }" +
            ".item.selected { color: blue; font-weight: bold; }" +
            "li a { color: purple; text-decoration: underline; }" +
            "li:hover { background-color: cyan; }" +
            "section .panel { border-top-width: 1px; border-top-style: solid; border-top-color: black; }" +
            ".card { padding: 8px; margin: 4px; background-color: white; border-top-width: 1px; border-top-style: solid; border-top-color: gray; }" +
            ".card .title { font-size: 16px; font-weight: bold; }" +
            ".card .body { font-size: 12px; color: gray; }" +
            ".form-row { padding: 4px; margin-bottom: 4px; }" +
            ".form-row label { font-weight: bold; padding-right: 4px; }" +
            "input[type=text] { padding: 2px; }" +
            "input[type=checkbox] { margin-right: 4px; }" +
            "button { padding: 4px 8px; }";

        public sealed class Scene {
            public Document Document;
            public List<OriginatedStylesheet> Sheets;
            public CascadeEngine Cascade;
            public Dictionary<Element, ComputedStyle> Styles;
            public LayoutContext Context;
            public LayoutEngine Layout;
            public int ElementCount;
            public string Name;
            public string Html;
        }

        public static Scene Build100Cards() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int p = 0; p < 5; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 4).Append("\">");
                for (int c = 0; c < 4; c++) {
                    sb.Append("<div class=\"card\"><div class=\"title\">Card ").Append(p * 4 + c)
                      .Append("</div><div class=\"body\">Body text for card ").Append(p * 4 + c)
                      .Append("</div></div>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return BuildScene("100Cards", sb.ToString());
        }

        public static Scene Build500Mixed() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            int idx = 0;
            for (int p = 0; p < 10; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 4).Append("\">");
                if (p % 3 == 0) {
                    sb.Append("<div class=\"flex-row\" style=\"display: flex\">");
                    for (int c = 0; c < 8; c++) {
                        sb.Append("<div class=\"card\"><span class=\"title\">F").Append(idx++).Append("</span></div>");
                    }
                    sb.Append("</div>");
                } else if (p % 3 == 1) {
                    sb.Append("<div class=\"grid\" style=\"display: grid\">");
                    for (int c = 0; c < 12; c++) {
                        sb.Append("<div class=\"card\"><span class=\"title\">G").Append(idx++).Append("</span></div>");
                    }
                    sb.Append("</div>");
                } else {
                    sb.Append("<ul class=\"list\">");
                    for (int c = 0; c < 12; c++) {
                        bool selected = (c + p) % 5 == 0;
                        sb.Append("<li class=\"").Append(selected ? "item selected" : "item")
                          .Append("\"><a href=\"#\">L").Append(idx++).Append("</a></li>");
                    }
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return BuildScene("500Mixed", sb.ToString());
        }

        public static Scene Build1000Forms() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int f = 0; f < 50; f++) {
                sb.Append("<form class=\"panel zone-").Append(f % 4).Append("\">");
                for (int r = 0; r < 6; r++) {
                    sb.Append("<div class=\"form-row\">");
                    sb.Append("<label>F").Append(f).Append("R").Append(r).Append("</label>");
                    if (r % 4 == 0) {
                        sb.Append("<input type=\"text\" name=\"t").Append(f).Append("_").Append(r).Append("\" />");
                    } else if (r % 4 == 1) {
                        sb.Append("<input type=\"checkbox\" name=\"c").Append(f).Append("_").Append(r).Append("\" />");
                    } else if (r % 4 == 2) {
                        sb.Append("<select><option>A</option></select>");
                    } else {
                        sb.Append("<button>OK</button>");
                    }
                    sb.Append("</div>");
                }
                sb.Append("</form>");
            }
            sb.Append("</section>");
            return BuildScene("1000Forms", sb.ToString());
        }

        public static Scene Build1000Deep() {
            // 50 deep, ~20 wide per leaf level. Open then close 50 nesting <div>s, with
            // each shallowness contributing ~20 leaves. Total elements = 50 wrappers +
            // 50 * ~19 leaves ≈ 1000.
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int d = 0; d < 50; d++) {
                sb.Append("<div class=\"panel zone-").Append(d % 4).Append("\">");
                for (int l = 0; l < 19; l++) {
                    sb.Append("<span class=\"item\">L").Append(d).Append("_").Append(l).Append("</span>");
                }
            }
            for (int d = 0; d < 50; d++) sb.Append("</div>");
            sb.Append("</section>");
            return BuildScene("1000Deep", sb.ToString());
        }

        public static Scene Build5000Massive() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            int idCounter = 0;
            for (int p = 0; p < 50; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 4).Append("\">");
                for (int r = 0; r < 10; r++) {
                    sb.Append("<ul class=\"list\">");
                    for (int i = 0; i < 9; i++) {
                        bool selected = (i + r + p) % 7 == 0;
                        sb.Append("<li class=\"").Append(selected ? "item selected" : "item").Append("\"");
                        if ((idCounter % 13) == 0) sb.Append(" id=\"e").Append(idCounter).Append('"');
                        sb.Append("><a href=\"#\">L").Append(idCounter).Append("</a></li>");
                        idCounter++;
                    }
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return BuildScene("5000Massive", sb.ToString());
        }

        // Prebuilt 500-mixed paint tree used by the paint-only benches: a flat list
        // of 500 styled BlockBoxes. Skips the parser/cascade so paint timings reflect
        // pure converter throughput.
        public static BlockBox BuildPaintFlatTree(int count, string tag = "div") {
            var rootElem = new Element("div");
            var rootStyle = new ComputedStyle(rootElem);
            var root = new BlockBox { Style = rootStyle, Element = rootElem, Width = 1000, Height = count * 10 };
            string[] colors = { "red", "blue", "green", "yellow", "orange", "purple", "cyan" };
            for (int i = 0; i < count; i++) {
                var elem = new Element(tag);
                var s = new ComputedStyle(elem);
                s.Set("background-color", colors[i % colors.Length]);
                s.Set("border-top-style", "solid");
                s.Set("border-top-width", "1px");
                s.Set("border-top-color", "black");
                var child = new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 };
                root.AddChild(child);
            }
            return root;
        }

        public static BlockBox BuildPaintGradientTree(int count) {
            var rootElem = new Element("div");
            var rootStyle = new ComputedStyle(rootElem);
            var root = new BlockBox { Style = rootStyle, Element = rootElem, Width = 1000, Height = count * 10 };
            for (int i = 0; i < count; i++) {
                var elem = new Element("div");
                var s = new ComputedStyle(elem);
                s.Set("background-image", "linear-gradient(45deg, red, blue)");
                var child = new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 };
                root.AddChild(child);
            }
            return root;
        }

        public static BlockBox BuildPaintShadowTree(int count) {
            var rootElem = new Element("div");
            var rootStyle = new ComputedStyle(rootElem);
            var root = new BlockBox { Style = rootStyle, Element = rootElem, Width = 1000, Height = count * 10 };
            for (int i = 0; i < count; i++) {
                var elem = new Element("div");
                var s = new ComputedStyle(elem);
                s.Set("box-shadow", "0 2px 4px rgba(0,0,0,0.2), 0 1px 2px rgba(0,0,0,0.1)");
                s.Set("background-color", "white");
                var child = new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 };
                root.AddChild(child);
            }
            return root;
        }

        static Scene BuildScene(string name, string html) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA)),
                OriginatedStylesheet.Author(CssParser.Parse(AuthorCss))
            };
            var cascade = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024,
                ViewportHeightPx = 768,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                // Wire the cascade-built snapshot so LayoutEngine reuses it
                // instead of rebuilding 11 typed arrays per Layout call.
                Snapshot = cascade.LastSnapshot
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);

            int count = 0;
            foreach (var _ in AllElements(doc)) count++;

            return new Scene {
                Name = name,
                Html = html,
                Document = doc,
                Sheets = sheets,
                Cascade = cascade,
                Styles = styles,
                Context = ctx,
                Layout = layout,
                ElementCount = count
            };
        }

        public static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        public static Func<Element, ComputedStyle> StyleResolver(Scene s) {
            var styles = s.Styles;
            return e => styles.TryGetValue(e, out var cs) ? cs : null;
        }

        // ===== Measurement helpers =====

        public static void StabilizeGC() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        public static long AllocatedBytes() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetTotalAllocatedBytes(precise: false);
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        public static double Mean(double[] vals) {
            double s = 0;
            for (int i = 0; i < vals.Length; i++) s += vals[i];
            return s / vals.Length;
        }

        public static double StdDev(double[] vals) {
            double m = Mean(vals);
            double sq = 0;
            for (int i = 0; i < vals.Length; i++) { double d = vals[i] - m; sq += d * d; }
            return Math.Sqrt(sq / vals.Length);
        }

        public static double Percentile(double[] vals, double p) {
            var copy = (double[])vals.Clone();
            Array.Sort(copy);
            if (copy.Length == 0) return 0;
            if (copy.Length == 1) return copy[0];
            double rank = p * (copy.Length - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            double frac = rank - lo;
            return copy[lo] * (1 - frac) + copy[hi] * frac;
        }

        public static double Median(double[] vals) => Percentile(vals, 0.5);

        public sealed class TimeStats {
            public double MeanMs;
            public double MedianMs;
            public double P95Ms;
            public double P99Ms;
            public double StdDevMs;
            public double PerElementUs;
            public int N;
        }

        public static TimeStats Time(int iterations, int warmup, Action body, int elementCount = 0) {
            for (int w = 0; w < warmup; w++) body();
            StabilizeGC();
            var times = new double[iterations];
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++) {
                sw.Restart();
                body();
                sw.Stop();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            return new TimeStats {
                MeanMs = Mean(times),
                MedianMs = Median(times),
                P95Ms = Percentile(times, 0.95),
                P99Ms = Percentile(times, 0.99),
                StdDevMs = StdDev(times),
                PerElementUs = elementCount > 0 ? Median(times) * 1000.0 / elementCount : 0,
                N = iterations
            };
        }

        public static (TimeStats stats, long bytesPerCall) TimeWithAllocs(int iterations, int warmup, Action body, int elementCount = 0) {
            for (int w = 0; w < warmup; w++) body();
            StabilizeGC();

            long beforeAlloc = AllocatedBytes();
            var times = new double[iterations];
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++) {
                sw.Restart();
                body();
                sw.Stop();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            long afterAlloc = AllocatedBytes();
            long perCall = (afterAlloc - beforeAlloc) / iterations;
            var stats = new TimeStats {
                MeanMs = Mean(times),
                MedianMs = Median(times),
                P95Ms = Percentile(times, 0.95),
                P99Ms = Percentile(times, 0.99),
                StdDevMs = StdDev(times),
                PerElementUs = elementCount > 0 ? Median(times) * 1000.0 / elementCount : 0,
                N = iterations
            };
            return (stats, perCall);
        }

        public static long MeasureAllocs(int iterations, int warmup, Action body) {
            for (int w = 0; w < warmup; w++) body();
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < iterations; i++) body();
            long after = AllocatedBytes();
            return (after - before) / iterations;
        }

        public static void Report(string label, TimeStats s, long bytesPerCall = -1, int elementCount = 0) {
            string allocPart = bytesPerCall < 0 ? "" : $", allocs={FormatBytes(bytesPerCall)}/call";
            string elemPart = elementCount > 0 ? $", {s.PerElementUs:F2}μs/elem" : "";
            TestContext.Progress.WriteLine(
                $"[BENCH] {label}: median={s.MedianMs:F3}ms p95={s.P95Ms:F3}ms p99={s.P99Ms:F3}ms (mean={s.MeanMs:F3}±{s.StdDevMs:F3}, n={s.N}){elemPart}{allocPart}");
        }

        public static string FormatBytes(long bytes) {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + "KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F2") + "MB";
        }
    }
}
