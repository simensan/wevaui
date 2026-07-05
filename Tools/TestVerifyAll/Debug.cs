using System;
using System.Collections.Generic;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Snap;
using Weva.Layout.Positioning;
using Weva.Css.Cascade;
using Weva.Css;
using Weva.Parsing;
using Weva.Dom;
using Weva.Layout.Text;

namespace TestRunner {
    public static class DebugSnap {
        public static void Run() {
            string css = "" +
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y proximity; }" +
                ".item { height: 80px; scroll-snap-align: start; }" +
                ".pad { height: 200px; }";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div>";
            ScrollSnapProperties.EnsureRegistered();
            var (root, _, _) = BuildLayout(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = FindByClass(root, "vp");
            Console.WriteLine($"vp: X={vp.X} Y={vp.Y} W={vp.Width} H={vp.Height} children={vp.Children.Count} pt={vp.PaddingTop} bt={vp.BorderTop}");
            for (int i = 0; i < vp.Children.Count; i++) {
                var c = vp.Children[i];
                string cls = c.Element?.GetAttribute("class") ?? "?";
                Console.WriteLine($"  [{i}] {cls}: X={c.X} Y={c.Y} W={c.Width} H={c.Height}");
            }
            var state = sc.Get(vp);
            Console.WriteLine($"state: ViewportH={state.ViewportHeight} MaxScrollY={state.MaxScrollY} scrollY={state.ScrollY}");

            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(vp);
            Console.WriteLine($"snap points (y): count={pts.Count}");
            for (int i = 0; i < pts.Count; i++) {
                var p = pts[i];
                Console.WriteLine($"  [{i}] pos={p.Position}");
            }

            state.ScrollY = 40;
            var type = SnapResolver.ResolveType(vp);
            Console.WriteLine($"snap type: active={type.IsActive} strict={type.Strictness} axis={type.Axis}");
            bool ok = resolver.TryFindSnapTargetY(vp, state.ScrollY, type, out double snapped);
            Console.WriteLine($"TryFindSnapTargetY @ y=40: ok={ok} snapped={snapped}");

            // Now centre-align
            Console.WriteLine("--- center align ---");
            string css2 = "" +
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }" +
                ".item { height: 40px; scroll-snap-align: center; }" +
                ".pad { height: 200px; }";
            string html2 = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root2, _, _) = BuildLayout(html2, css2);
            var sc2 = new ScrollContainer();
            new ScrollLayout(sc2).Run(root2);
            Box vp2 = FindByClass(root2, "vp");
            for (int i = 0; i < vp2.Children.Count; i++) {
                var c = vp2.Children[i];
                Console.WriteLine($"  [{i}]: Y={c.Y} H={c.Height}");
            }
            var state2 = sc2.Get(vp2);
            Console.WriteLine($"state2: ViewportH={state2.ViewportHeight} MaxScrollY={state2.MaxScrollY}");
            var resolver2 = new SnapResolver(sc2);
            var pts2 = resolver2.CollectSnapPointsY(vp2);
            for (int i = 0; i < pts2.Count; i++) {
                Console.WriteLine($"  pt[{i}] pos={pts2[i].Position}");
            }
            state2.ScrollY = 18;
            var type2 = SnapResolver.ResolveType(vp2);
            bool ok2 = resolver2.TryFindSnapTargetY(vp2, 18, type2, out double snapped2);
            Console.WriteLine($"center: ok={ok2} snapped={snapped2}");

            // Now end-align
            Console.WriteLine("--- end align ---");
            string css3 = "" +
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }" +
                ".item { height: 40px; scroll-snap-align: end; }" +
                ".pad { height: 400px; }";
            string html3 = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root3, _, _) = BuildLayout(html3, css3);
            var sc3 = new ScrollContainer();
            new ScrollLayout(sc3).Run(root3);
            Box vp3 = FindByClass(root3, "vp");
            for (int i = 0; i < vp3.Children.Count; i++) {
                var c = vp3.Children[i];
                Console.WriteLine($"  [{i}]: Y={c.Y} H={c.Height}");
            }
            var state3 = sc3.Get(vp3);
            Console.WriteLine($"state3: ViewportH={state3.ViewportHeight} MaxScrollY={state3.MaxScrollY}");
            var resolver3 = new SnapResolver(sc3);
            var pts3 = resolver3.CollectSnapPointsY(vp3);
            for (int i = 0; i < pts3.Count; i++) {
                Console.WriteLine($"  pt[{i}] pos={pts3[i].Position}");
            }
        }

        static Box FindByClass(Box root, string cls) {
            foreach (var b in All(root)) {
                if (b.Element?.GetAttribute("class") == cls) return b;
            }
            return null;
        }

        public static IEnumerable<Box> All(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in All(c)) yield return d;
            }
        }

        public static (Box, Dictionary<Element, ComputedStyle>, LayoutContext) BuildLayout(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(
                "html, body, div, section, header, footer, nav, main, article, aside, p { display: block; } " +
                "body { margin: 0; padding: 0; }")));
            sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 600, RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles, ctx);
        }
    }

    public static class DebugSticky {
        public static void Run() {
            const string css = ".sticky { position: sticky; top: 25px; height: 30px; width: 100px; }";
            var (root, _, _) = DebugSnap.BuildLayout("<div class=\"sticky\"></div>", css);
            Box sticky = null;
            foreach (var b in DebugSnap.All(root)) {
                if (b.Element?.GetAttribute("class") == "sticky") { sticky = b; break; }
            }
            Console.WriteLine($"sticky: X={sticky.X} Y={sticky.Y} W={sticky.Width} H={sticky.Height}");
            Console.WriteLine($"  position={sticky.Position} StickyOffsetY={sticky.StickyOffsetY}");
            Console.WriteLine($"  OffsetTop={sticky.OffsetTop}");
            Console.WriteLine($"  parent type={sticky.Parent?.GetType().Name} parent.Y={sticky.Parent?.Y}");
        }
    }

    public static class DebugAnchor {
        public static void Run() {
            // Reproduce TryFallback_flip_inline_fires_when_right_overflows
            const string css = @"
                .target {
                    anchor-name: --t;
                    position: relative;
                    width: 50px; height: 30px;
                    left: 480px; top: 50px;
                }
                .pop {
                    position: absolute;
                    position-anchor: --t;
                    width: 100px; height: 50px;
                    left: anchor(right);
                    top: anchor(bottom);
                    position-try-fallbacks: flip-block, flip-inline;
                }
            ";
            string html = "<div class=\"target\" id=\"t\"></div><div class=\"pop\" id=\"p\"></div>";
            var (root, _, _) = DebugSnap.BuildLayout(html, css);
            Box pop = null;
            foreach (var b in DebugSnap.All(root)) {
                if (b.Element?.GetAttribute("id") == "p") { pop = b; break; }
            }
            Console.WriteLine($"pop: X={pop.X} Y={pop.Y} W={pop.Width} H={pop.Height}");
            // Right edge
            Console.WriteLine($"right edge = {pop.X + pop.Width}");
        }
    }

}
