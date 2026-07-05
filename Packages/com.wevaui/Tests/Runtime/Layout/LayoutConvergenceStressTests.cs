using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Layout {
    // A5: Multi-pass convergence stress suite.
    //
    // Two core invariants tested:
    //
    //   1. IDEMPOTENCE — laying out the SAME document twice with fresh engines
    //      produces byte-identical (within 0.001px) box geometry at every node.
    //      Divergence exposes first-pass state leaking into the tree.
    //
    //   2. FIXED-POINT — re-running Layout on an ALREADY-laid-out engine (warm
    //      cache, same styles, no DOM/style changes) does NOT move any box.
    //      Divergence exposes the convergence loop not reaching a true fixed point.
    //
    // A pseudo-random document generator (fixed seeds per test, never time-based)
    // composes depth-bounded HTML/CSS trees from a vocabulary of risky features:
    //   - flex row / column with grow / shrink / baseline
    //   - grid with spanning items and minmax / fr tracks
    //   - percent widths/heights under auto parents
    //   - min/max-width/height clamps
    //   - absolute / fixed children with various inset combinations
    //   - overflow:hidden scroll containers
    //   - aspect-ratio
    //   - min-height:100vh chains (historical balloon bug family)
    //   - nested flex-in-grid-in-flex topologies
    //   - contain:size / inline-size containment
    //
    // Each test case uses a FIXED seed. Failure output includes the seed plus
    // the generated HTML/CSS so the case is fully reproducible.
    //
    // Suite design:
    //   - ~60 seeded cases across three groups (idempotency, fixed-point, curated).
    //   - Bounded depth (<=5) and element count (<=40) for speed.
    //   - Target runtime: <10s total on a developer machine.
    //
    // NUnit pitfalls observed in this repo:
    //   - NEVER chain .Within() off Is.GreaterThan/LessThan — use Is.EqualTo(x).Within(eps).
    //   - Does.Not.Contain is substring-only on strings; use Has.None.EqualTo for collections.

    public class LayoutConvergenceStressTests {
        // -------------------------------------------------------------------------
        // Tolerance: geometry is compared within 0.001 px.
        // This is intentionally tight — convergence must be exact, not "close".
        // If float jitter forces a wider tolerance, the root cause must be explained
        // in a comment and the tolerance documented.
        // -------------------------------------------------------------------------
        const double Tol = 0.001;

        // -------------------------------------------------------------------------
        // Minimal UA stylesheet — mirrors LayoutTestHelpers.BuiltinUserAgent.
        // -------------------------------------------------------------------------
        const string UA = @"
            html, body, div, section, header, footer, nav, main, article, aside,
            p, h1, h2, h3, h4, h5, h6, ul, ol, hr { display: block; }
            li { display: list-item; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
            template, link, meta, head, title, script, style { display: none; }
            [hidden] { display: none; }
        ";

        // -------------------------------------------------------------------------
        // Deterministic document generator
        // -------------------------------------------------------------------------

        // Feature vocabulary — each entry is a CSS snippet + its HTML contribution.
        // The generator picks from this list seeded from the test case's RNG.

        enum Feature {
            FlexRow, FlexColumn, FlexRowGrow, FlexColumnGrow, FlexShrink,
            Grid2Col, Grid3ColFr, GridSpanning, GridMinmax,
            PercentWidth, PercentHeight, MinWidth, MaxWidth, MinHeight, MaxHeight,
            AbsChild, FixedChild, OverflowHidden, AspectRatio,
            MinHeight100Vh, ContainSize, ContainInlineSize,
            NestedFlexInGrid, GridInFlex, InlineBlock,
        }

        static readonly Feature[] AllFeatures = (Feature[])Enum.GetValues(typeof(Feature));

        // A node in the generated document tree.
        sealed class GenNode {
            public string Tag = "div";
            public string Id;
            public string Classes = "";
            public string InlineStyle = "";
            public string TextContent = "";
            public List<GenNode> Children = new List<GenNode>();
        }

        sealed class GeneratedDoc {
            public string Html;
            public string Css;
            public int Seed;
            public int NodeCount;
        }

        // Generate a bounded random document for the given seed.
        static GeneratedDoc Generate(int seed) {
            var rng = new Random(seed);
            var css = new StringBuilder();
            var nodeCounter = 0;

            // Recursive generator: returns a GenNode of bounded depth.
            GenNode MakeNode(int depth) {
                var node = new GenNode {
                    Id = $"n{nodeCounter++}",
                    Tag = "div",
                };

                // Pick a display/layout feature for this node.
                var feat = AllFeatures[rng.Next(AllFeatures.Length)];
                ApplyFeature(feat, node, css, rng, depth);

                // Recurse into children if depth allows and element budget remains.
                if (depth < 5 && nodeCounter < 38) {
                    int childCount = rng.Next(1, 4); // 1-3 children
                    for (int i = 0; i < childCount && nodeCounter < 38; i++) {
                        if (depth >= 4) {
                            // Leaf: just text content, no recursion.
                            var leaf = new GenNode {
                                Id = $"n{nodeCounter++}",
                                Tag = "div",
                                InlineStyle = $"width:{rng.Next(20, 120)}px; height:{rng.Next(10, 60)}px;",
                                TextContent = depth % 2 == 0 ? "X" : "",
                            };
                            node.Children.Add(leaf);
                        } else {
                            node.Children.Add(MakeNode(depth + 1));
                        }
                    }
                }

                return node;
            }

            // Apply the chosen feature to this node's CSS class + inline style.
            void ApplyFeature(Feature feat, GenNode node, StringBuilder cssBuf, Random r, int depth) {
                string cls = $"f{(int)feat}_{node.Id}";
                node.Classes = cls;
                int w = r.Next(100, 400);
                int h = r.Next(50, 300);
                int gap = r.Next(4, 20);
                int pad = r.Next(2, 16);
                int minW = r.Next(30, w);
                int maxW = r.Next(w, w + 200);
                int minH = r.Next(20, h);

                switch (feat) {
                    case Feature.FlexRow:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:row; gap:{gap}px; width:{w}px; height:{h}px; align-items:stretch; }}");
                        break;
                    case Feature.FlexColumn:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:column; gap:{gap}px; width:{w}px; height:{h}px; }}");
                        break;
                    case Feature.FlexRowGrow:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:row; width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ flex:1; }}");
                        break;
                    case Feature.FlexColumnGrow:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:column; width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ flex:1; }}");
                        break;
                    case Feature.FlexShrink:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:row; width:{w}px; height:{h}px; flex-wrap:nowrap; }}");
                        cssBuf.AppendLine($".{cls} > * {{ flex:0 1 auto; min-width:0; }}");
                        break;
                    case Feature.Grid2Col:
                        cssBuf.AppendLine($".{cls} {{ display:grid; grid-template-columns:1fr 1fr; gap:{gap}px; width:{w}px; }}");
                        break;
                    case Feature.Grid3ColFr:
                        cssBuf.AppendLine($".{cls} {{ display:grid; grid-template-columns:1fr 2fr 1fr; gap:{gap}px; width:{w}px; height:{h}px; }}");
                        break;
                    case Feature.GridSpanning:
                        cssBuf.AppendLine($".{cls} {{ display:grid; grid-template-columns:repeat(3,1fr); gap:{gap}px; width:{w}px; }}");
                        cssBuf.AppendLine($".{cls} > *:first-child {{ grid-column:1/3; }}");
                        break;
                    case Feature.GridMinmax:
                        cssBuf.AppendLine($".{cls} {{ display:grid; grid-template-columns:minmax({minW}px,1fr) minmax({minW}px,1fr); gap:{gap}px; width:{w}px; }}");
                        break;
                    case Feature.PercentWidth:
                        // Parent provides a definite width; child uses 50%.
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ width:50%; }}");
                        break;
                    case Feature.PercentHeight:
                        // Parent has explicit height; child uses 80%.
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ height:80%; }}");
                        break;
                    case Feature.MinWidth:
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; min-width:{minW}px; }}");
                        break;
                    case Feature.MaxWidth:
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; max-width:{maxW}px; }}");
                        break;
                    case Feature.MinHeight:
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; height:auto; min-height:{minH}px; }}");
                        break;
                    case Feature.MaxHeight:
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; height:{h}px; max-height:{h + r.Next(0, 50)}px; }}");
                        break;
                    case Feature.AbsChild:
                        // Container is relative; one child is absolute.
                        cssBuf.AppendLine($".{cls} {{ position:relative; width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > *:last-child {{ position:absolute; top:{r.Next(0, 20)}px; right:{r.Next(0, 20)}px; width:30px; height:20px; }}");
                        break;
                    case Feature.FixedChild:
                        // NOTE: fixed positioning is relative to viewport, not parent.
                        // Use a single fixed child at known position to test positioning pass.
                        cssBuf.AppendLine($".{cls} {{ position:relative; width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > *:first-child {{ position:fixed; top:0; left:0; width:40px; height:30px; }}");
                        break;
                    case Feature.OverflowHidden:
                        cssBuf.AppendLine($".{cls} {{ width:{w}px; height:{h}px; overflow:hidden; }}");
                        break;
                    case Feature.AspectRatio:
                        int aw = r.Next(80, 200);
                        cssBuf.AppendLine($".{cls} {{ width:{aw}px; aspect-ratio:16/9; }}");
                        break;
                    case Feature.MinHeight100Vh:
                        // Historical balloon bug family: min-height:100vh on a flex container.
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:column; min-height:100vh; width:{w}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ flex:1; }}");
                        break;
                    case Feature.ContainSize:
                        cssBuf.AppendLine($".{cls} {{ contain:size; width:{w}px; height:{h}px; }}");
                        break;
                    case Feature.ContainInlineSize:
                        cssBuf.AppendLine($".{cls} {{ contain:inline-size; width:{w}px; }}");
                        break;
                    case Feature.NestedFlexInGrid:
                        cssBuf.AppendLine($".{cls} {{ display:grid; grid-template-columns:1fr 1fr; gap:{gap}px; width:{w}px; height:{h}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ display:flex; flex-direction:column; gap:{gap / 2}px; }}");
                        break;
                    case Feature.GridInFlex:
                        cssBuf.AppendLine($".{cls} {{ display:flex; flex-direction:row; width:{w}px; height:{h}px; gap:{gap}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ display:grid; grid-template-columns:1fr 1fr; flex:1; }}");
                        break;
                    case Feature.InlineBlock:
                        cssBuf.AppendLine($".{cls} {{ display:block; width:{w}px; }}");
                        cssBuf.AppendLine($".{cls} > * {{ display:inline-block; padding:{pad}px; }}");
                        break;
                }
            }

            // Build the root node with a wrapping body.
            var root = MakeNode(0);

            // Serialize to HTML.
            var html = new StringBuilder();
            html.AppendLine("<body>");
            SerializeNode(root, html);
            html.AppendLine("</body>");

            return new GeneratedDoc {
                Html = html.ToString(),
                Css = css.ToString(),
                Seed = seed,
                NodeCount = nodeCounter,
            };
        }

        static void SerializeNode(GenNode node, StringBuilder sb) {
            sb.Append($"<{node.Tag}");
            if (!string.IsNullOrEmpty(node.Id)) sb.Append($" id=\"{node.Id}\"");
            if (!string.IsNullOrEmpty(node.Classes)) sb.Append($" class=\"{node.Classes}\"");
            if (!string.IsNullOrEmpty(node.InlineStyle)) sb.Append($" style=\"{node.InlineStyle}\"");
            sb.Append(">");
            if (!string.IsNullOrEmpty(node.TextContent)) sb.Append(node.TextContent);
            foreach (var c in node.Children) SerializeNode(c, sb);
            sb.AppendLine($"</{node.Tag}>");
        }

        // -------------------------------------------------------------------------
        // Layout helpers
        // -------------------------------------------------------------------------

        // Build a full layout from scratch using a fresh engine. Returns the box root.
        static Box BuildFresh(GeneratedDoc doc, double vpW = 800, double vpH = 600) {
            var d = HtmlParser.Parse(doc.Html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA))
            };
            if (!string.IsNullOrEmpty(doc.Css)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(doc.Css)));
            }
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(d)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = vpW,
                ViewportHeightPx = vpH,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            return le.Layout(d, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
        }

        // Build and return both the first-pass root and a warm second-pass root
        // from the SAME engine (fixed-point test).
        static (Box first, Box second) BuildWarm(GeneratedDoc doc, double vpW = 800, double vpH = 600) {
            var d = HtmlParser.Parse(doc.Html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA))
            };
            if (!string.IsNullOrEmpty(doc.Css)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(doc.Css)));
            }
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(d)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = vpW,
                ViewportHeightPx = vpH,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles,
            };
            Func<Element, ComputedStyle> styleOf = e => styles.TryGetValue(e, out var cs) ? cs : null;
            var le = new LayoutEngine(new MonoFontMetrics());
            var first = le.Layout(d, styleOf, ctx);
            // Second pass: same engine, same doc, same styles — this exercises the
            // warm-cache / incremental path. No tracker → full pass (no skip gate).
            var second = le.Layout(d, styleOf, ctx);
            return (first, second);
        }

        // -------------------------------------------------------------------------
        // Tree comparison
        // -------------------------------------------------------------------------

        // Walk two box trees in depth-first order and compare X,Y,Width,Height.
        // Returns a non-empty string on first mismatch (with path), or null if equal.
        static string CompareBoxTrees(Box a, Box b, string path = "root") {
            double ax = a.X, ay = a.Y, aw = a.Width, ah = a.Height;
            double bx = b.X, by = b.Y, bw = b.Width, bh = b.Height;
            if (Math.Abs(ax - bx) > Tol) return $"X at {path}: first={ax:F4} second={bx:F4}";
            if (Math.Abs(ay - by) > Tol) return $"Y at {path}: first={ay:F4} second={by:F4}";
            if (Math.Abs(aw - bw) > Tol) return $"Width at {path}: first={aw:F4} second={bw:F4}";
            if (Math.Abs(ah - bh) > Tol) return $"Height at {path}: first={ah:F4} second={bh:F4}";

            if (a.Children.Count != b.Children.Count)
                return $"child-count at {path}: first={a.Children.Count} second={b.Children.Count}";

            for (int i = 0; i < a.Children.Count; i++) {
                string childPath = $"{path}[{i}:{BoxLabel(a.Children[i])}]";
                var msg = CompareBoxTrees(a.Children[i], b.Children[i], childPath);
                if (msg != null) return msg;
            }
            return null;
        }

        static string BoxLabel(Box b) {
            if (b is TextRun tr) {
                var t = tr.Text ?? "";
                return $"TextRun({(t.Length > 6 ? t.Substring(0, 6) : t)})";
            }
            if (b is LineBox) return "LineBox";
            if (b is BlockBox bb) return $"Block({bb.Element?.TagName ?? "anon"})";
            return b.GetType().Name;
        }

        static string FailMessage(GeneratedDoc doc, string invariant, string mismatch) {
            return $"[A5 {invariant}] seed={doc.Seed} nodes={doc.NodeCount}: {mismatch}\n" +
                   $"--- HTML ---\n{doc.Html}\n--- CSS ---\n{doc.Css}";
        }

        // -------------------------------------------------------------------------
        // INVARIANT 1: IDEMPOTENCE
        //
        // Two independent Build calls (different engine instances, fresh cascade)
        // must produce identical box geometry. Seeds 1000-1059.
        // -------------------------------------------------------------------------

        [Test]
        public void Idempotency_seed_1000() => AssertIdempotent(1000);
        [Test]
        public void Idempotency_seed_1001() => AssertIdempotent(1001);
        [Test]
        public void Idempotency_seed_1002() => AssertIdempotent(1002);
        [Test]
        public void Idempotency_seed_1003() => AssertIdempotent(1003);
        [Test]
        public void Idempotency_seed_1004() => AssertIdempotent(1004);
        [Test]
        public void Idempotency_seed_1005() => AssertIdempotent(1005);
        [Test]
        public void Idempotency_seed_1006() => AssertIdempotent(1006);
        [Test]
        public void Idempotency_seed_1007() => AssertIdempotent(1007);
        [Test]
        public void Idempotency_seed_1008() => AssertIdempotent(1008);
        [Test]
        public void Idempotency_seed_1009() => AssertIdempotent(1009);
        [Test]
        public void Idempotency_seed_1010() => AssertIdempotent(1010);
        [Test]
        public void Idempotency_seed_1011() => AssertIdempotent(1011);
        [Test]
        public void Idempotency_seed_1012() => AssertIdempotent(1012);
        [Test]
        public void Idempotency_seed_1013() => AssertIdempotent(1013);
        [Test]
        public void Idempotency_seed_1014() => AssertIdempotent(1014);
        [Test]
        public void Idempotency_seed_1015() => AssertIdempotent(1015);
        [Test]
        public void Idempotency_seed_1016() => AssertIdempotent(1016);
        [Test]
        public void Idempotency_seed_1017() => AssertIdempotent(1017);
        [Test]
        public void Idempotency_seed_1018() => AssertIdempotent(1018);
        [Test]
        public void Idempotency_seed_1019() => AssertIdempotent(1019);
        [Test]
        public void Idempotency_seed_1020() => AssertIdempotent(1020);
        [Test]
        public void Idempotency_seed_1021() => AssertIdempotent(1021);
        [Test]
        public void Idempotency_seed_1022() => AssertIdempotent(1022);
        [Test]
        public void Idempotency_seed_1023() => AssertIdempotent(1023);
        [Test]
        public void Idempotency_seed_1024() => AssertIdempotent(1024);
        [Test]
        public void Idempotency_seed_1025() => AssertIdempotent(1025);
        [Test]
        public void Idempotency_seed_1026() => AssertIdempotent(1026);
        [Test]
        public void Idempotency_seed_1027() => AssertIdempotent(1027);
        [Test]
        public void Idempotency_seed_1028() => AssertIdempotent(1028);
        [Test]
        public void Idempotency_seed_1029() => AssertIdempotent(1029);
        [Test]
        public void Idempotency_seed_1030() => AssertIdempotent(1030);
        [Test]
        public void Idempotency_seed_1031() => AssertIdempotent(1031);
        [Test]
        public void Idempotency_seed_1032() => AssertIdempotent(1032);
        [Test]
        public void Idempotency_seed_1033() => AssertIdempotent(1033);
        [Test]
        public void Idempotency_seed_1034() => AssertIdempotent(1034);
        [Test]
        public void Idempotency_seed_1035() => AssertIdempotent(1035);
        [Test]
        public void Idempotency_seed_1036() => AssertIdempotent(1036);
        [Test]
        public void Idempotency_seed_1037() => AssertIdempotent(1037);
        [Test]
        public void Idempotency_seed_1038() => AssertIdempotent(1038);
        [Test]
        public void Idempotency_seed_1039() => AssertIdempotent(1039);
        [Test]
        public void Idempotency_seed_1040() => AssertIdempotent(1040);
        [Test]
        public void Idempotency_seed_1041() => AssertIdempotent(1041);
        [Test]
        public void Idempotency_seed_1042() => AssertIdempotent(1042);
        [Test]
        public void Idempotency_seed_1043() => AssertIdempotent(1043);
        [Test]
        public void Idempotency_seed_1044() => AssertIdempotent(1044);
        [Test]
        public void Idempotency_seed_1045() => AssertIdempotent(1045);
        [Test]
        public void Idempotency_seed_1046() => AssertIdempotent(1046);
        [Test]
        public void Idempotency_seed_1047() => AssertIdempotent(1047);
        [Test]
        public void Idempotency_seed_1048() => AssertIdempotent(1048);
        [Test]
        public void Idempotency_seed_1049() => AssertIdempotent(1049);

        static void AssertIdempotent(int seed) {
            var doc = Generate(seed);
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE", mismatch));
        }

        // -------------------------------------------------------------------------
        // INVARIANT 2: FIXED-POINT
        //
        // A second Layout call on a warm engine (with a built cache) must not move
        // any box. Seeds 2000-2049.
        // -------------------------------------------------------------------------

        [Test]
        public void FixedPoint_seed_2000() => AssertFixedPoint(2000);
        [Test]
        public void FixedPoint_seed_2001() => AssertFixedPoint(2001);
        [Test]
        public void FixedPoint_seed_2002() => AssertFixedPoint(2002);
        [Test]
        public void FixedPoint_seed_2003() => AssertFixedPoint(2003);
        [Test]
        public void FixedPoint_seed_2004() => AssertFixedPoint(2004);
        [Test]
        public void FixedPoint_seed_2005() => AssertFixedPoint(2005);
        [Test]
        public void FixedPoint_seed_2006() => AssertFixedPoint(2006);
        [Test]
        public void FixedPoint_seed_2007() => AssertFixedPoint(2007);
        [Test]
        public void FixedPoint_seed_2008() => AssertFixedPoint(2008);
        [Test]
        public void FixedPoint_seed_2009() => AssertFixedPoint(2009);
        [Test]
        public void FixedPoint_seed_2010() => AssertFixedPoint(2010);
        [Test]
        public void FixedPoint_seed_2011() => AssertFixedPoint(2011);
        [Test]
        public void FixedPoint_seed_2012() => AssertFixedPoint(2012);
        [Test]
        public void FixedPoint_seed_2013() => AssertFixedPoint(2013);
        [Test]
        public void FixedPoint_seed_2014() => AssertFixedPoint(2014);
        [Test]
        public void FixedPoint_seed_2015() => AssertFixedPoint(2015);
        [Test]
        public void FixedPoint_seed_2016() => AssertFixedPoint(2016);
        [Test]
        public void FixedPoint_seed_2017() => AssertFixedPoint(2017);
        [Test]
        public void FixedPoint_seed_2018() => AssertFixedPoint(2018);
        [Test]
        public void FixedPoint_seed_2019() => AssertFixedPoint(2019);
        [Test]
        public void FixedPoint_seed_2020() => AssertFixedPoint(2020);
        [Test]
        public void FixedPoint_seed_2021() => AssertFixedPoint(2021);
        [Test]
        public void FixedPoint_seed_2022() => AssertFixedPoint(2022);
        [Test]
        public void FixedPoint_seed_2023() => AssertFixedPoint(2023);
        [Test]
        public void FixedPoint_seed_2024() => AssertFixedPoint(2024);
        [Test]
        public void FixedPoint_seed_2025() => AssertFixedPoint(2025);
        [Test]
        public void FixedPoint_seed_2026() => AssertFixedPoint(2026);
        [Test]
        public void FixedPoint_seed_2027() => AssertFixedPoint(2027);
        [Test]
        public void FixedPoint_seed_2028() => AssertFixedPoint(2028);
        [Test]
        public void FixedPoint_seed_2029() => AssertFixedPoint(2029);
        [Test]
        public void FixedPoint_seed_2030() => AssertFixedPoint(2030);
        [Test]
        public void FixedPoint_seed_2031() => AssertFixedPoint(2031);
        [Test]
        public void FixedPoint_seed_2032() => AssertFixedPoint(2032);
        [Test]
        public void FixedPoint_seed_2033() => AssertFixedPoint(2033);
        [Test]
        public void FixedPoint_seed_2034() => AssertFixedPoint(2034);
        [Test]
        public void FixedPoint_seed_2035() => AssertFixedPoint(2035);
        [Test]
        public void FixedPoint_seed_2036() => AssertFixedPoint(2036);
        [Test]
        public void FixedPoint_seed_2037() => AssertFixedPoint(2037);
        [Test]
        public void FixedPoint_seed_2038() => AssertFixedPoint(2038);
        [Test]
        public void FixedPoint_seed_2039() => AssertFixedPoint(2039);
        [Test]
        public void FixedPoint_seed_2040() => AssertFixedPoint(2040);
        [Test]
        public void FixedPoint_seed_2041() => AssertFixedPoint(2041);
        [Test]
        public void FixedPoint_seed_2042() => AssertFixedPoint(2042);
        [Test]
        public void FixedPoint_seed_2043() => AssertFixedPoint(2043);
        [Test]
        public void FixedPoint_seed_2044() => AssertFixedPoint(2044);
        [Test]
        public void FixedPoint_seed_2045() => AssertFixedPoint(2045);
        [Test]
        public void FixedPoint_seed_2046() => AssertFixedPoint(2046);
        [Test]
        public void FixedPoint_seed_2047() => AssertFixedPoint(2047);
        [Test]
        public void FixedPoint_seed_2048() => AssertFixedPoint(2048);
        [Test]
        public void FixedPoint_seed_2049() => AssertFixedPoint(2049);

        static void AssertFixedPoint(int seed) {
            var doc = Generate(seed);
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT", mismatch));
        }

        // -------------------------------------------------------------------------
        // CURATED CASES: known risky topologies from the A5 documented suspects.
        // These exercise documented past bug families directly.
        // -------------------------------------------------------------------------

        // Topology A: min-height:100vh flex column with flex:1 grow children.
        // Historical FLEXINTRINSIC-STALE-PREFLEX / FLEX-MINHEIGHT-FILL family.
        [Test]
        public void Curated_min_height_100vh_flex_column_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='app'>
  <header class='topbar'>Nav</header>
  <main class='content'>
    <div class='sidebar'>Side</div>
    <section class='main'>Main content</section>
  </main>
</div>
</body>",
                Css = @"
.app { display:flex; flex-direction:column; min-height:100vh; width:800px; }
.topbar { height:60px; }
.content { display:flex; flex:1; }
.sidebar { width:200px; }
.main { flex:1; }
",
                Seed = -1, NodeCount = 5,
            };
            var first = BuildFresh(doc, 800, 600);
            var second = BuildFresh(doc, 800, 600);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (min-height:100vh)", mismatch));
        }

        [Test]
        public void Curated_min_height_100vh_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='app'>
  <header class='topbar'>Nav</header>
  <main class='content'>
    <div class='sidebar'>Side</div>
    <section class='main'>Main content</section>
  </main>
</div>
</body>",
                Css = @"
.app { display:flex; flex-direction:column; min-height:100vh; width:800px; }
.topbar { height:60px; }
.content { display:flex; flex:1; }
.sidebar { width:200px; }
.main { flex:1; }
",
                Seed = -2, NodeCount = 5,
            };
            var (first, second) = BuildWarm(doc, 800, 600);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (min-height:100vh)", mismatch));
        }

        // Topology B: percent-height under fixed-positioned ancestor.
        // Exercises RepairPercentHeightsUnderOutOfFlow.
        [Test]
        public void Curated_percent_height_under_fixed_ancestor_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='overlay'>
  <div class='panel'>
    <div class='content' style='height:100%;'>Content</div>
  </div>
</div>
</body>",
                Css = @"
.overlay { position:fixed; inset:0; display:flex; flex-direction:column; }
.panel { flex:1; overflow:hidden; }
",
                Seed = -3, NodeCount = 4,
            };
            var first = BuildFresh(doc, 800, 600);
            var second = BuildFresh(doc, 800, 600);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (percent-height/fixed)", mismatch));
        }

        [Test]
        public void Curated_percent_height_under_fixed_ancestor_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='overlay'>
  <div class='panel'>
    <div class='content' style='height:100%;'>Content</div>
  </div>
</div>
</body>",
                Css = @"
.overlay { position:fixed; inset:0; display:flex; flex-direction:column; }
.panel { flex:1; overflow:hidden; }
",
                Seed = -4, NodeCount = 4,
            };
            var (first, second) = BuildWarm(doc, 800, 600);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (percent-height/fixed)", mismatch));
        }

        // Topology C: grid with spanning items + nested flex in cell.
        [Test]
        public void Curated_grid_spanning_nested_flex_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='grid'>
  <div class='span2'>
    <div class='flex-row'>
      <div class='item'>A</div>
      <div class='item grow'>B</div>
    </div>
  </div>
  <div class='cell'>C</div>
  <div class='cell'>D</div>
  <div class='cell'>E</div>
</div>
</body>",
                Css = @"
.grid { display:grid; grid-template-columns:1fr 1fr 1fr; gap:8px; width:600px; }
.span2 { grid-column:1/3; }
.flex-row { display:flex; gap:8px; }
.item { min-width:60px; height:40px; }
.grow { flex:1; }
.cell { height:40px; }
",
                Seed = -5, NodeCount = 8,
            };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (grid+flex)", mismatch));
        }

        [Test]
        public void Curated_grid_spanning_nested_flex_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='grid'>
  <div class='span2'>
    <div class='flex-row'>
      <div class='item'>A</div>
      <div class='item grow'>B</div>
    </div>
  </div>
  <div class='cell'>C</div>
  <div class='cell'>D</div>
  <div class='cell'>E</div>
</div>
</body>",
                Css = @"
.grid { display:grid; grid-template-columns:1fr 1fr 1fr; gap:8px; width:600px; }
.span2 { grid-column:1/3; }
.flex-row { display:flex; gap:8px; }
.item { min-width:60px; height:40px; }
.grow { flex:1; }
.cell { height:40px; }
",
                Seed = -6, NodeCount = 8,
            };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (grid+flex)", mismatch));
        }

        // Topology D: deep alternating column/row flex chain
        // (this exact topology was the FLEX-DEEP-CROSS-STRETCH-INCREMENTAL bug).
        [Test]
        public void Curated_deep_alternating_flex_chain_idempotent() {
            const string html = @"<body>
<div class='l0'><div class='top0'></div>
<div class='l1'><div class='l2'><div class='top2'></div>
<div class='l3'><div class='leaf'></div></div></div></div></div>
</body>";
            const string css = @"
.l0   { display:flex; flex-direction:column; height:400px; width:400px; }
.top0 { height:20px; }
.l1   { display:flex; flex:1 1 auto; align-items:stretch; }
.l2   { display:flex; flex-direction:column; flex:1 1 auto; }
.top2 { height:20px; }
.l3   { display:flex; flex:1 1 auto; align-items:stretch; }
.leaf { flex:0 0 40px; }";
            var doc = new GeneratedDoc { Html = html, Css = css, Seed = -7, NodeCount = 7 };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (deep-flex-chain)", mismatch));
        }

        [Test]
        public void Curated_deep_alternating_flex_chain_fixed_point() {
            const string html = @"<body>
<div class='l0'><div class='top0'></div>
<div class='l1'><div class='l2'><div class='top2'></div>
<div class='l3'><div class='leaf'></div></div></div></div></div>
</body>";
            const string css = @"
.l0   { display:flex; flex-direction:column; height:400px; width:400px; }
.top0 { height:20px; }
.l1   { display:flex; flex:1 1 auto; align-items:stretch; }
.l2   { display:flex; flex-direction:column; flex:1 1 auto; }
.top2 { height:20px; }
.l3   { display:flex; flex:1 1 auto; align-items:stretch; }
.leaf { flex:0 0 40px; }";
            var doc = new GeneratedDoc { Html = html, Css = css, Seed = -8, NodeCount = 7 };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (deep-flex-chain)", mismatch));
        }

        // Topology E: abs-pos child in flex container (TEXTALIGN-ABS family).
        [Test]
        public void Curated_abs_child_in_flex_container_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='row'>
  <div class='grow'>Content</div>
  <div class='abs'>Abs</div>
  <div class='fixed-size'>Right</div>
</div>
</body>",
                Css = @"
.row { display:flex; align-items:center; position:relative; width:800px; height:60px; }
.grow { flex:1; }
.abs { position:absolute; top:0; right:0; width:100px; height:100%; }
.fixed-size { width:80px; }
",
                Seed = -9, NodeCount = 4,
            };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (abs-in-flex)", mismatch));
        }

        [Test]
        public void Curated_abs_child_in_flex_container_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='row'>
  <div class='grow'>Content</div>
  <div class='abs'>Abs</div>
  <div class='fixed-size'>Right</div>
</div>
</body>",
                Css = @"
.row { display:flex; align-items:center; position:relative; width:800px; height:60px; }
.grow { flex:1; }
.abs { position:absolute; top:0; right:0; width:100px; height:100%; }
.fixed-size { width:80px; }
",
                Seed = -10, NodeCount = 4,
            };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (abs-in-flex)", mismatch));
        }

        // Topology F: contain:size + nested flex — verify containment boundary is
        // stable across re-layout.
        [Test]
        public void Curated_contain_size_nested_flex_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='outer'>
  <div class='contained'>
    <div class='inner-flex'>
      <div class='child'>A</div>
      <div class='child grow'>B</div>
    </div>
  </div>
  <div class='sibling'>Sibling</div>
</div>
</body>",
                Css = @"
.outer { display:flex; flex-direction:column; width:600px; }
.contained { contain:size; width:400px; height:200px; }
.inner-flex { display:flex; gap:8px; }
.child { width:80px; height:40px; }
.grow { flex:1; }
.sibling { height:100px; }
",
                Seed = -11, NodeCount = 6,
            };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (contain:size)", mismatch));
        }

        [Test]
        public void Curated_contain_size_nested_flex_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='outer'>
  <div class='contained'>
    <div class='inner-flex'>
      <div class='child'>A</div>
      <div class='child grow'>B</div>
    </div>
  </div>
  <div class='sibling'>Sibling</div>
</div>
</body>",
                Css = @"
.outer { display:flex; flex-direction:column; width:600px; }
.contained { contain:size; width:400px; height:200px; }
.inner-flex { display:flex; gap:8px; }
.child { width:80px; height:40px; }
.grow { flex:1; }
.sibling { height:100px; }
",
                Seed = -12, NodeCount = 6,
            };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (contain:size)", mismatch));
        }

        // Topology G: grid with minmax and auto-rows.
        [Test]
        public void Curated_grid_minmax_auto_rows_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='grid'>
  <div class='item'>A</div>
  <div class='item tall'>B</div>
  <div class='item'>C</div>
  <div class='item'>D</div>
  <div class='item'>E</div>
  <div class='item'>F</div>
</div>
</body>",
                Css = @"
.grid { display:grid; grid-template-columns:minmax(80px,1fr) minmax(80px,2fr) minmax(80px,1fr);
        grid-auto-rows:auto; gap:8px; width:600px; }
.item { padding:12px; }
.tall { height:100px; }
",
                Seed = -13, NodeCount = 7,
            };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (grid minmax)", mismatch));
        }

        [Test]
        public void Curated_grid_minmax_auto_rows_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='grid'>
  <div class='item'>A</div>
  <div class='item tall'>B</div>
  <div class='item'>C</div>
  <div class='item'>D</div>
  <div class='item'>E</div>
  <div class='item'>F</div>
</div>
</body>",
                Css = @"
.grid { display:grid; grid-template-columns:minmax(80px,1fr) minmax(80px,2fr) minmax(80px,1fr);
        grid-auto-rows:auto; gap:8px; width:600px; }
.item { padding:12px; }
.tall { height:100px; }
",
                Seed = -14, NodeCount = 7,
            };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (grid minmax)", mismatch));
        }

        // Topology H: aspect-ratio + flex grow. Aspect-ratio fixup pass runs after flex.
        [Test]
        public void Curated_aspect_ratio_in_flex_idempotent() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='row'>
  <div class='square'>Square</div>
  <div class='wide'>Wide</div>
  <div class='auto-ar'></div>
</div>
</body>",
                Css = @"
.row { display:flex; align-items:flex-start; gap:12px; width:600px; }
.square { width:80px; aspect-ratio:1/1; }
.wide { flex:1; height:60px; }
.auto-ar { width:120px; aspect-ratio:16/9; }
",
                Seed = -15, NodeCount = 4,
            };
            var first = BuildFresh(doc);
            var second = BuildFresh(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "IDEMPOTENCE (aspect-ratio+flex)", mismatch));
        }

        [Test]
        public void Curated_aspect_ratio_in_flex_fixed_point() {
            var doc = new GeneratedDoc {
                Html = @"<body>
<div class='row'>
  <div class='square'>Square</div>
  <div class='wide'>Wide</div>
  <div class='auto-ar'></div>
</div>
</body>",
                Css = @"
.row { display:flex; align-items:flex-start; gap:12px; width:600px; }
.square { width:80px; aspect-ratio:1/1; }
.wide { flex:1; height:60px; }
.auto-ar { width:120px; aspect-ratio:16/9; }
",
                Seed = -16, NodeCount = 4,
            };
            var (first, second) = BuildWarm(doc);
            var mismatch = CompareBoxTrees(first, second);
            if (mismatch != null)
                Assert.Fail(FailMessage(doc, "FIXED-POINT (aspect-ratio+flex)", mismatch));
        }
    }
}
