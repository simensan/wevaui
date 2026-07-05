using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.PerfBench {
    // Standalone copy of the bench fixture builders for the dotnet runner. Mirrors
    // Tests/Runtime/Bench/BenchScenes.cs but without NUnit dependencies. Kept in
    // sync manually — both sides build the same DOM shapes from the same author
    // stylesheet so timings are directly comparable.
    static class BenchScenes {
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
            public string Name;
            public Document Document;
            public List<OriginatedStylesheet> Sheets;
            public CascadeEngine Cascade;
            public Dictionary<Element, ComputedStyle> Styles;
            public LayoutContext Context;
            public LayoutEngine Layout;
            public int ElementCount;
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
                    for (int c = 0; c < 8; c++) sb.Append("<div class=\"card\"><span class=\"title\">F").Append(idx++).Append("</span></div>");
                    sb.Append("</div>");
                } else if (p % 3 == 1) {
                    sb.Append("<div class=\"grid\" style=\"display: grid\">");
                    for (int c = 0; c < 12; c++) sb.Append("<div class=\"card\"><span class=\"title\">G").Append(idx++).Append("</span></div>");
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
                    if (r % 4 == 0) sb.Append("<input type=\"text\" name=\"t").Append(f).Append("_").Append(r).Append("\" />");
                    else if (r % 4 == 1) sb.Append("<input type=\"checkbox\" name=\"c").Append(f).Append("_").Append(r).Append("\" />");
                    else if (r % 4 == 2) sb.Append("<select><option>A</option></select>");
                    else sb.Append("<button>OK</button>");
                    sb.Append("</div>");
                }
                sb.Append("</form>");
            }
            sb.Append("</section>");
            return BuildScene("1000Forms", sb.ToString());
        }

        public static Scene Build1000Deep() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int d = 0; d < 50; d++) {
                sb.Append("<div class=\"panel zone-").Append(d % 4).Append("\">");
                for (int l = 0; l < 19; l++) sb.Append("<span class=\"item\">L").Append(d).Append("_").Append(l).Append("</span>");
            }
            for (int d = 0; d < 50; d++) sb.Append("</div>");
            sb.Append("</section>");
            return BuildScene("1000Deep", sb.ToString());
        }

        // Mimics a production game-HUD CSS: dozens of state-pseudo selectors
        // (`:hover`, `:focus`, `:active`, `:disabled`) layered over a flat
        // tree of buttons / panels. Exercises the SnapshotMatcher's
        // pseudo-class fast-path — every selector with `:hover` etc. would
        // otherwise fall back to the managed matcher.
        public const string HudCss =
            ".slot { padding: 6px; border: 1px solid #444; }" +
            ".slot:hover { border-color: white; }" +
            ".slot:focus { border-color: cyan; }" +
            ".slot:active { background: red; }" +
            ".slot:disabled { opacity: 0.5; }" +
            ".btn { padding: 4px 8px; }" +
            ".btn:hover { background: #555; }" +
            ".btn:focus { outline: 2px solid yellow; }" +
            ".btn:active { transform: translateY(1px); }" +
            ".btn:disabled { color: gray; }" +
            ".icon { width: 32px; height: 32px; }" +
            ".icon:hover { transform: scale(1.1); }" +
            ".panel { background: #222; }" +
            ".panel:hover .icon { transform: scale(1.05); }" +
            ".tab { padding: 8px; }" +
            ".tab:hover { background: #333; }" +
            ".tab:focus { background: #444; }" +
            ".tab.active { background: #555; }" +
            ".tab.active:hover { background: #666; }" +
            ".item { padding: 2px; }" +
            ".item:hover { color: yellow; }" +
            ".item:focus { color: orange; }" +
            ".row:hover { background: rgba(255,255,255,0.05); }" +
            ".col:hover .item { font-weight: bold; }" +
            ".chip { padding: 2px 4px; }" +
            ".chip:hover { transform: translateY(-1px); }" +
            ".badge:hover { background: yellow; }" +
            ".badge:active { background: red; }" +
            ".link:hover { text-decoration: underline; }" +
            ".link:focus { outline: 1px solid white; }" +
            ".link:active { color: red; }" +
            ".card:hover { border-color: cyan; }";

        public static Scene BuildHudHeavyPseudos() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            // ~200 elements with a mix of classes that match the pseudo
            // selectors in HudCss. Each selector in the index touches many
            // candidate elements.
            for (int g = 0; g < 10; g++) {
                sb.Append("<div class=\"panel zone-").Append(g % 4).Append("\">");
                for (int r = 0; r < 4; r++) {
                    sb.Append("<div class=\"row\">");
                    for (int i = 0; i < 5; i++) {
                        sb.Append("<button class=\"slot btn\"><span class=\"icon\"></span><span class=\"item\">").Append(g * 20 + r * 5 + i).Append("</span></button>");
                    }
                    sb.Append("</div>");
                }
                sb.Append("<div class=\"col\">");
                for (int t = 0; t < 3; t++) sb.Append("<div class=\"tab\">T").Append(t).Append("</div>");
                sb.Append("</div>");
                sb.Append("<div class=\"row\">");
                for (int c = 0; c < 4; c++) sb.Append("<a class=\"link chip\">").Append(c).Append("</a>");
                sb.Append("</div>");
                sb.Append("</div>"); // close panel
            }
            sb.Append("</section>");
            return BuildScene("HudHeavyPseudos", sb.ToString(), useHudCss: true);
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

        public static BlockBox BuildPaintFlatTree(int count) {
            var rootElem = new Element("div");
            var rootStyle = new ComputedStyle(rootElem);
            var root = new BlockBox { Style = rootStyle, Element = rootElem, Width = 1000, Height = count * 10 };
            string[] colors = { "red", "blue", "green", "yellow", "orange", "purple", "cyan" };
            for (int i = 0; i < count; i++) {
                var elem = new Element("div");
                var s = new ComputedStyle(elem);
                s.Set("background-color", colors[i % colors.Length]);
                s.Set("border-top-style", "solid");
                s.Set("border-top-width", "1px");
                s.Set("border-top-color", "black");
                root.AddChild(new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 });
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
                root.AddChild(new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 });
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
                root.AddChild(new BlockBox { Style = s, Element = elem, Width = 200, Height = 10, Y = i * 10 });
            }
            return root;
        }

        public static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        static Scene BuildScene(string name, string html, bool useHudCss = false) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA)),
                OriginatedStylesheet.Author(CssParser.Parse(useHudCss ? HudCss : AuthorCss))
            };
            var cascade = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768, RootFontSizePx = 16, DpiPixelsPerInch = 96,
                // Wire cascade snapshot so LayoutEngine reuses it across passes.
                Snapshot = cascade.LastSnapshot
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);

            int count = 0;
            foreach (var _ in AllElements(doc)) count++;

            return new Scene {
                Name = name, Document = doc, Sheets = sheets, Cascade = cascade,
                Styles = styles, Context = ctx, Layout = layout, ElementCount = count
            };
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

        public static long AllocatedBytes() => GC.GetTotalAllocatedBytes(precise: false);

        public static double Median(double[] vals) => Percentile(vals, 0.5);

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

        public static (double median, double p95, double p99) Time(int iterations, int warmup, Action body) {
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
            return (Median(times), Percentile(times, 0.95), Percentile(times, 0.99));
        }

        public static long MeasureAllocs(int iterations, int warmup, Action body) {
            for (int w = 0; w < warmup; w++) body();
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < iterations; i++) body();
            long after = AllocatedBytes();
            return (after - before) / iterations;
        }
    }
}
