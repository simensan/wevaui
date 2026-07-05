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
using Weva.Parsing;

namespace Weva.BaselineGen {
    static class LayoutBuildBench {
        const string UA =
            "html, body, div, section, ul, li, header, footer, nav, p { display: block; } " +
            "a, span, strong, em, b, i, u, code, small { display: inline; } " +
            "body { margin: 0; padding: 0; }";

        public static int Run() {
            string html = BuildLargeHtml(500);
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA)),
                OriginatedStylesheet.Author(CssParser.Parse(".item { font-size: 14px; padding: 2px; margin: 1px; } .panel { padding: 4px; }"))
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var snap = cascade.LastSnapshot;

            int elementCount = 0;
            foreach (var _ in AllElements(doc)) elementCount++;
            Console.WriteLine($"DOM: {elementCount} elements; snap NodeCount={snap.NodeCount}");

            ComputedStyle StyleOfEl(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            var styleArr = SnapshotStyleArray.Build(snap, StyleOfEl);

            // Warm
            for (int w = 0; w < 100; w++) {
                var pool = new BoxPool(); pool.BeginPass();
                new BoxBuilder(StyleOfEl, pool, new LayoutScratch()).BuildDocument(doc);
                pool.EndPass(null);
            }
            for (int w = 0; w < 100; w++) {
                var pool = new BoxPool(); pool.BeginPass();
                new SnapshotBoxBuilder(styleArr.At, pool, new LayoutScratch()).BuildFromSnapshot(snap);
                pool.EndPass(null);
            }

            int n = 1000;
            var sw = new Stopwatch();
            var managedPool = new BoxPool();
            var managedScratch = new LayoutScratch();
            var managedTimes = new double[n];
            for (int i = 0; i < n; i++) {
                managedPool.BeginPass();
                var b = new BoxBuilder(StyleOfEl, managedPool, managedScratch);
                sw.Restart();
                b.BuildDocument(doc);
                sw.Stop();
                managedTimes[i] = sw.Elapsed.TotalMilliseconds;
                managedPool.EndPass(null);
            }

            var snapPool = new BoxPool();
            var snapScratch = new LayoutScratch();
            var snapTimes = new double[n];
            for (int i = 0; i < n; i++) {
                snapPool.BeginPass();
                var b = new SnapshotBoxBuilder(styleArr.At, snapPool, snapScratch);
                sw.Restart();
                b.BuildFromSnapshot(snap);
                sw.Stop();
                snapTimes[i] = sw.Elapsed.TotalMilliseconds;
                snapPool.EndPass(null);
            }

            double mManaged = Median(managedTimes);
            double mSnap = Median(snapTimes);
            Console.WriteLine($"managed BoxBuilder median: {mManaged:F4}ms");
            Console.WriteLine($"snapshot BoxBuilder median: {mSnap:F4}ms");
            Console.WriteLine($"speedup: {mManaged / mSnap:F2}x");
            return 0;
        }

        static double Median(double[] vals) {
            var copy = (double[])vals.Clone();
            Array.Sort(copy);
            int n = copy.Length;
            return n % 2 == 1 ? copy[n / 2] : 0.5 * (copy[n / 2 - 1] + copy[n / 2]);
        }

        static string BuildLargeHtml(int elementCount) {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            int nPanels = elementCount / 100;
            if (nPanels < 1) nPanels = 1;
            int idCounter = 0;
            for (int p = 0; p < nPanels; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 4).Append("\">");
                for (int r = 0; r < 10; r++) {
                    sb.Append("<ul class=\"list\">");
                    for (int i = 0; i < 10; i++) {
                        bool selected = (i + r + p) % 7 == 0;
                        string cls = selected ? "item selected" : "item";
                        sb.Append("<li class=\"").Append(cls).Append("\"");
                        if ((idCounter % 13) == 0) sb.Append(" id=\"e").Append(idCounter).Append('"');
                        sb.Append("><a href=\"#\">link</a></li>");
                        idCounter++;
                    }
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return sb.ToString();
        }

        static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }
    }
}
