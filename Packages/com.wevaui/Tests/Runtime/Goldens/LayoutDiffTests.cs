// LayoutDiffTests — compare Unity's layout engine output against Chrome's
// getBoundingClientRect ground truth captured via Tools/Layout/extract-chrome-
// layout.mjs. One [Test] per snippet; the comparison walks Unity's Box tree
// in document order, pairs each element-backed box with the same-index entry
// from the Chrome JSON, and asserts that every (x, y, w, h) is within a
// configured tolerance.
//
// The Chrome JSON lives next to each .html as `<name>.html.chrome-layout.json`.
// To regenerate after layout changes that are intentional, re-run:
//   node Tools/Layout/capture-all-chrome-layouts.mjs
//
// Per-snippet tolerance overrides live in `<name>.html.tolerance.json` with
// shape `{"absPx": 4, "relPct": 0.05}` (either key optional). Use sparingly
// — the right default is to push the engine to match Chrome, not to widen
// the tolerance.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Goldens {
    public class LayoutDiffTests {
        // Default tolerances. Snippet authors can override per-snippet with
        // a sidecar .tolerance.json file.
        const double DefaultAbsolutePx = 2.0;
        const double DefaultRelative = 0.05;

        static string GoldensDir([CallerFilePath] string callerPath = null) {
            return Path.GetDirectoryName(callerPath);
        }

        static string SnippetPath(string name) => Path.Combine(GoldensDir(), "Snippets", name);

        // ---- Per-snippet tests ----
        // Default viewport for snippets matches GoldenAssert.Match's default
        // (800x600). match3 uses its native 1280x720.
        [Test] public void LayoutDiff_01_empty()                  => RunSnippet("01-empty.html");
        [Test] public void LayoutDiff_02_single_block()           => RunSnippet("02-single-block.html");
        [Test] public void LayoutDiff_03_block_margin_padding()   => RunSnippet("03-block-margin-padding.html");
        // W1 inc 3+6: LD-1 tests un-ignored — harness now uses InterFontMetrics
        // and Chrome references were regenerated with the same Inter font face, so
        // headless layout == Chrome reference within default 2px/5% tolerance.
        [Test] public void LayoutDiff_04_text_paragraph()         => RunSnippet("04-text-paragraph.html");
        [Test] public void LayoutDiff_05_flex_row()               => RunSnippet("05-flex-row.html");
        [Test] public void LayoutDiff_06_flex_column()            => RunSnippet("06-flex-column.html");
        [Test] public void LayoutDiff_07_grid_3x3()               => RunSnippet("07-grid-3x3.html");
        [Test] public void LayoutDiff_08_positioning_absolute()   => RunSnippet("08-positioning-absolute.html");
        [Test] public void LayoutDiff_09_borders_radii()          => RunSnippet("09-borders-radii.html");
        [Test] public void LayoutDiff_10_gradient_background()    => RunSnippet("10-gradient-background.html");
        [Test] public void LayoutDiff_11_shadow()                 => RunSnippet("11-shadow.html");
        // H1-EM-FONTSIZE fixed in c999a869 (em in the element's own font-size
        // declaration resolves against the PARENT per CSS Fonts L4 §2.1) —
        // ignore lifted in the 2026-06-06 stale-Ignore audit.
        [Test] public void LayoutDiff_12_the_demo()        => RunSnippet("12-the-demo.html");
        [Test] public void LayoutDiff_13_inset_shadow()           => RunSnippet("13-inset-shadow.html");
        [Test] public void LayoutDiff_14_dashed_border()          => RunSnippet("14-dashed-border.html");
        [Test] public void LayoutDiff_15_dotted_border()          => RunSnippet("15-dotted-border.html");
        [Test] public void LayoutDiff_16_drop_shadow_filter()     => RunSnippet("16-drop-shadow-filter.html");
        [Test] public void LayoutDiff_17_multi_layer_background() => RunSnippet("17-multi-layer-background.html");
        [Test] public void LayoutDiff_18_margin_collapse()        => RunSnippet("18-margin-collapse.html");
        [Test] public void LayoutDiff_19_inline_block_row()       => RunSnippet("19-inline-block-row.html");
        [Test] public void LayoutDiff_20_flex_baseline()          => RunSnippet("20-flex-baseline.html");
        [Test] public void LayoutDiff_21_word_break()             => RunSnippet("21-word-break.html");
        [Test] public void LayoutDiff_22_flex_with_absolute()     => RunSnippet("22-flex-with-absolute.html");
        [Test] public void LayoutDiff_23_inline_splitting()       => RunSnippet("23-inline-splitting.html");
        [Test] public void LayoutDiff_24_text_overflow_ellipsis() => RunSnippet("24-text-overflow-ellipsis.html");
        [Test] public void LayoutDiff_25_backdrop_modal_dialog()  => RunSnippet("25-backdrop-modal-dialog.html");
        [Test] public void LayoutDiff_26_text_shadow()            => RunSnippet("26-text-shadow.html");
        [Test] public void LayoutDiff_27_text_shadow_blur()       => RunSnippet("27-text-shadow-blur.html");
        [Test] public void LayoutDiff_28_floats()                 => RunSnippet("28-floats.html");
        [Test] public void LayoutDiff_39_multicol()               => RunSnippet("39-multicol.html");
        [Test] public void LayoutDiff_40_containment_size()       => RunSnippet("40-containment-size.html");
        [Test] public void LayoutDiff_41_quotes()                 => RunSnippet("41-quotes.html");
        [Test] public void LayoutDiff_42_cv_hidden()              => RunSnippet("42-cv-hidden.html");
        [Test] public void LayoutDiff_43_quotes_pseudo()          => RunSnippet("43-quotes-pseudo.html");
        // 44: counter-reset/counter-increment with ::before{content:counter(sec)". "} on
        // headings; plus a counters(ch,".") nested case producing "1.1" and "1.2".
        // Chrome-diff verifies the generated digit strings change inline widths.
        [Test] public void LayoutDiff_44_counters()               => RunSnippet("44-counters.html");
        // 45: <ol>/<ul> with list-style-position:inside (in-flow markers for structural
        // agreement with Chrome's element walk), decimal ordinals, disc bullets, nested list.
        // Outside markers are avoided: Chrome's walk omits ::marker pseudo-elements,
        // so outside-positioned markers create a structural mismatch with the engine.
        [Test] public void LayoutDiff_45_list_markers()           => RunSnippet("45-list-markers.html");
        // 46: content:attr(data-label) resolves the attribute string into generated text;
        // attr() with a fallback exercises the fallback path; width:attr(data-w px) uses
        // CSS Values L5 typed attr() — stable Chrome 133+ ships this enabled (#a4.w=120px).
        [Test] public void LayoutDiff_46_attr_content()           => RunSnippet("46-attr-content.html");
        // 47: <br> forced line breaks — two-line and three-line paragraphs verify
        // that <br> splits inline runs and the paragraph heights match Chrome.
        // <br> elements themselves appear in Chrome's DOM walk (w=0, h=line-height)
        // and are placed by AttachInlineFragmentsToLines at their break insertion point.
        [Test] public void LayoutDiff_47_br_linebreak()            => RunSnippet("47-br-linebreak.html");

        // match3 lives under Assets/UI and uses a 1280x720 viewport. It's the
        // big real-world demo so we exercise it through this test too.
        //
        // Fully green (0/125) as of the Segoe UI calibration: the fixture
        // authors `font-family: "Segoe UI"` so its text measures through
        // SegoeUIFontMetrics' six real Windows faces (incl. the CSS-Fonts
        // §5.2 weight snapping — 800 → Black — and the Segoe UI Symbol ★
        // advances), and the walker maps transformed boxes (`scale(1.08)`
        // selected tile, `translateX(-50%)` combo banner) to their
        // getBoundingClientRect AABBs.
        [Test]
        public void LayoutDiff_match3() {
            // GoldensDir() = Tests/Runtime/Goldens. Five levels up lands at the
            // repo root: Goldens -> Runtime -> Tests -> com.wevaui -> Packages -> repo.
            string repoRoot = Path.GetFullPath(Path.Combine(GoldensDir(), "..", "..", "..", "..", ".."));
            string htmlPath = Path.Combine(repoRoot, "Assets", "UI", "match3.html");
            RunFile(htmlPath, 1280, 720);
        }

        // Green (0/55) once its Chrome JSON was captured at the right
        // viewport: the original reference was a one-off manual extract at
        // an uncontrolled 1434x781 window, so every viewport-anchored
        // element (the full-screen overlay, %-positioned confetti, the
        // centered card stack) drifted. capture-all-chrome-layouts.mjs now
        // owns this fixture at 1729x1080.
        [Test]
        public void LayoutDiff_match3_endgame() {
            string repoRoot = Path.GetFullPath(Path.Combine(GoldensDir(), "..", "..", "..", "..", ".."));
            string htmlPath = Path.Combine(repoRoot, "Assets", "UI", "match3-endgame.html");
            RunFile(htmlPath, 1729, 1080);
        }

        void RunSnippet(string name) {
            RunFile(SnippetPath(name), 800, 600);
        }

        // -------------------------------------------------------------------
        // Core diff runner. Loads html+css, runs the layout engine, walks the
        // Box tree, pairs each Element-backed box with the corresponding
        // Chrome rect by document-order index, and asserts per-axis tolerance.
        void RunFile(string htmlPath, int viewportWidth, int viewportHeight) {
            if (!File.Exists(htmlPath)) {
                Assert.Inconclusive("Missing HTML: " + htmlPath);
                return;
            }
            string chromeJsonPath = htmlPath + ".chrome-layout.json";
            if (!File.Exists(chromeJsonPath)) {
                Assert.Inconclusive("Missing Chrome layout JSON: " + chromeJsonPath +
                    " (run Tools/Layout/capture-all-chrome-layouts.mjs to generate)");
                return;
            }

            // Tolerance: defaults + optional sidecar.
            double absPx = DefaultAbsolutePx;
            double rel = DefaultRelative;
            string tolPath = htmlPath + ".tolerance.json";
            if (File.Exists(tolPath)) {
                var tol = MiniJson.Parse(File.ReadAllText(tolPath));
                if (tol is Dictionary<string, object> tm) {
                    if (tm.TryGetValue("absPx", out var ap) && ap is double apd) absPx = apd;
                    if (tm.TryGetValue("relPct", out var rp) && rp is double rpd) rel = rpd;
                }
            }

            // 1) Load + lay out via the same path GoldenRunner uses.
            string html = File.ReadAllText(htmlPath);
            string cssPath = Path.ChangeExtension(htmlPath, ".css");
            string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

            var unityBoxes = BuildUnityBoxes(html, css, viewportWidth, viewportHeight);

            // 2) Load Chrome reference.
            var chromeDoc = MiniJson.Parse(File.ReadAllText(chromeJsonPath)) as Dictionary<string, object>;
            Assert.That(chromeDoc, Is.Not.Null, "Chrome JSON root must be an object: " + chromeJsonPath);
            var chromeElements = chromeDoc["elements"] as List<object>;
            Assert.That(chromeElements, Is.Not.Null, "Chrome JSON missing 'elements': " + chromeJsonPath);

            // 3) Pair element-backed Unity boxes (in DOM order) with Chrome
            // entries by index. We tolerate count mismatches: if Unity has
            // fewer/more boxes, we still compare the common prefix and
            // surface the missing/extra ones at the end of the report.
            int n = Math.Min(unityBoxes.Count, chromeElements.Count);
            var failures = new List<string>();
            double worstDelta = 0;
            int outOfTol = 0;
            for (int i = 0; i < n; i++) {
                var u = unityBoxes[i];
                var c = (Dictionary<string, object>) chromeElements[i];
                double cx = AsDouble(c, "x");
                double cy = AsDouble(c, "y");
                double cw = AsDouble(c, "w");
                double ch = AsDouble(c, "h");
                string tag = (string) c["tag"];
                string cls = (string) c["cls"];
                string id  = (string) c["id"];
                string sig = $"<{tag}{(string.IsNullOrEmpty(id) ? "" : "#" + id)}{(string.IsNullOrEmpty(cls) ? "" : "." + cls.Replace(' ', '.'))}>";

                double dx = u.X - cx;
                double dy = u.Y - cy;
                double dw = u.Width - cw;
                double dh = u.Height - ch;
                double allowW = Math.Max(absPx, Math.Abs(cw) * rel);
                double allowH = Math.Max(absPx, Math.Abs(ch) * rel);
                bool xOk = Math.Abs(dx) <= absPx;
                bool yOk = Math.Abs(dy) <= absPx;
                bool wOk = Math.Abs(dw) <= allowW;
                bool hOk = Math.Abs(dh) <= allowH;

                double m = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)),
                                    Math.Max(Math.Abs(dw), Math.Abs(dh)));
                if (m > worstDelta) worstDelta = m;
                if (xOk && yOk && wOk && hOk) continue;
                outOfTol++;
                if (failures.Count < 12) {
                    failures.Add($"  [{i,3}] {sig,-40} " +
                        $"chrome=({cx,7:0.##},{cy,7:0.##},{cw,7:0.##}x{ch,7:0.##}) " +
                        $"unity=({u.X,7:0.##},{u.Y,7:0.##},{u.Width,7:0.##}x{u.Height,7:0.##}) " +
                        $"Δ=(x:{dx:+0.##;-0.##;0},y:{dy:+0.##;-0.##;0},w:{dw:+0.##;-0.##;0},h:{dh:+0.##;-0.##;0})");
                }
            }

            int chromeOnly = Math.Max(0, chromeElements.Count - unityBoxes.Count);
            int unityOnly  = Math.Max(0, unityBoxes.Count - chromeElements.Count);

            if (outOfTol > 0 || chromeOnly > 0 || unityOnly > 0) {
                var sb = new StringBuilder();
                sb.AppendLine($"LayoutDiff [{Path.GetFileName(htmlPath)}] @ {viewportWidth}x{viewportHeight}: " +
                              $"{outOfTol}/{n} out of tolerance (absPx={absPx}, rel={rel:P0}), " +
                              $"worstΔ={worstDelta:0.##}px, " +
                              $"counts: unity={unityBoxes.Count}, chrome={chromeElements.Count}");
                foreach (var f in failures) sb.AppendLine(f);
                if (failures.Count < outOfTol) sb.AppendLine($"  ... {outOfTol - failures.Count} more");
                if (chromeOnly > 0) sb.AppendLine($"  Chrome has {chromeOnly} extra trailing elements (missing in Unity).");
                if (unityOnly > 0) sb.AppendLine($"  Unity has {unityOnly} extra trailing elements (anonymous boxes or layout artifacts).");
                if (outOfTol == 0 && chromeOnly == 0 && unityOnly == 0) {
                    // Defensive: nothing to fail.
                    return;
                }
                Assert.Fail(sb.ToString());
            }
        }

        // Snapshot of a Box's geometry expressed in root-relative (absolute)
        // coordinates so it lines up 1:1 with Chrome's getBoundingClientRect.
        // Box.X / Box.Y in the engine are local-to-parent; the walker below
        // accumulates the ancestor chain to produce absolute values.
        readonly struct BoxRect {
            public readonly double X, Y, Width, Height;
            public BoxRect(double x, double y, double w, double h) {
                X = x; Y = y; Width = w; Height = h;
            }
        }

        // -------------------------------------------------------------------
        // Layout-only build: same parse/cascade/layout pipeline as
        // GoldenRunner.Render but stops at the Box tree. Returns the boxes
        // in document order, restricted to those backed by a real Element
        // (anonymous boxes, line boxes, and text runs are skipped so the
        // walk lines up with Chrome's element-only dump).
        //
        // Coordinates are converted to absolute (root-relative) on the way
        // out so they match Chrome's getBoundingClientRect output. Box.X
        // and Box.Y in the layout engine are local-to-parent (the same
        // convention BoxToPaintConverter relies on), so a nested element
        // under a non-zero-positioned ancestor would otherwise report a
        // local offset that has no analogue in the Chrome JSON. Accumulating
        // ancestor X/Y during the walk turns the per-Box snapshot into an
        // absolute one without disturbing the engine's coordinate contract.
        static List<BoxRect> BuildUnityBoxes(string html, string css, int width, int height) {
            var doc = HtmlParser.Parse(html ?? string.Empty, new ParseOptions { ThrowOnError = false });
            var sheets = new List<OriginatedStylesheet> { UserAgentStylesheet.Parse() };
            if (!string.IsNullOrEmpty(css)) {
                var authorSheet = CssParser.Parse(css, new ParseOptions { ThrowOnError = false });
                sheets.Add(OriginatedStylesheet.Author(authorSheet));
            }
            var cascade = new CascadeEngine(sheets);
            var styles = cascade.ComputeAll(doc);
            // W1 font-determinism (inc 3+6): use InterFontMetrics as the default
            // so the headless layout measures text with the same per-glyph advances
            // as the Chrome reference JSONs (generated by capture-all-chrome-
            // layouts.mjs which injects @font-face for Weva-Default*.ttf and
            // body{font-family:'Inter',sans-serif}). All common family aliases
            // resolve to the same Inter table; "monospace" stays on ChromeMonospace()
            // since Inter is proportional and monospace snippets have their own
            // calibration.
            var fontMetrics = InterFontMetrics.Instance;
            var ctx = new LayoutContext(fontMetrics) {
                ViewportWidthPx = width,
                ViewportHeightPx = height,
                Snapshot = cascade.LastSnapshot,
            };
            ctx.RegisterFont("Inter", InterFontMetrics.Instance);
            ctx.RegisterFont("sans-serif", InterFontMetrics.Instance);
            ctx.RegisterFont("serif", InterFontMetrics.Instance);
            ctx.RegisterFont("system-ui", InterFontMetrics.Instance);
            ctx.RegisterFont("monospace", MonoFontMetrics.ChromeMonospace());
            // match3 authors `font-family: "Segoe UI"` explicitly; Chrome on
            // the capture machine resolves the REAL Segoe UI, so the fixture
            // measures with the calibrated Segoe tables (extracted from the
            // Windows TTFs) instead of Inter's — the source of the fixture's
            // long-standing 44/125 text-metric drift.
            ctx.RegisterFont("Segoe UI", SegoeUIFontMetrics.Instance);
            var layout = new LayoutEngine(fontMetrics);
            layout.BackdropStyleOf = e => cascade.ComputeBackdrop(e);
            // Wire ::before / ::after resolvers so pseudo-element content
            // (e.g. UA `q::before { content: open-quote }`) is injected into
            // the box tree and its geometry counts toward the host element's
            // rect — matching Chrome's getBoundingClientRect which includes
            // generated content in the element's bounding box. Without this,
            // a block <q> with a block child produces 2 anonymous blocks
            // instead of 3 (::after run dropped), and an inline <q>'s width
            // omits the bracket characters. CSS 2.1 §12.1.
            layout.BeforeStyleOf = e => cascade.ComputeBefore(e);
            layout.AfterStyleOf  = e => cascade.ComputeAfter(e);
            var root = layout.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            var order = new List<BoxRect>();
            // Document-order walk: keep only the box that BackingFinder considers
            // the "principal" box for an element, so the index lines up 1:1 with
            // Chrome's element walk. Anonymous block/inline wrappers, LineBoxes,
            // and TextRuns are excluded — TextRuns in particular carry the
            // parent element's pointer for paint inheritance, but they don't
            // correspond to a DOM element in Chrome's walk, so including them
            // would multi-count text-containing blocks.
            // Transform accumulation: Chrome's getBoundingClientRect reports
            // the post-transform axis-aligned bounding box, with ancestor
            // transforms applied to descendants. The walk therefore carries a
            // local→viewport Transform2D per box (offset-within-parent, then
            // the box's own CSS transform about its resolved transform-origin,
            // then the parent's accumulated matrix — the same model
            // BoxToPaintConverter paints with) and emits the AABB of the four
            // border-box corners. Untransformed trees collapse to the old
            // parentAbs + b.X arithmetic exactly.
            var seenElements = new HashSet<Element>();
            void Walk(Box b, Transform2D parentXf) {
                if (b == null) return;
                Transform2D m = Transform2D.Translate((float)b.X, (float)b.Y).Multiply(parentXf);
                if (b.Style != null) {
                    var xf = TransformResolver.ResolveTransform(b.Style, b.Width, b.Height);
                    if (xf != Transform2D.Identity) {
                        (double ox, double oy) = BoxToPaintConverter.ResolveTransformOrigin(
                            b.Style, b.Width, b.Height, LengthContext.Default);
                        m = Transform2D.Translate((float)-ox, (float)-oy)
                            .Multiply(xf)
                            .Multiply(Transform2D.Translate((float)ox, (float)oy))
                            .Multiply(m);
                    }
                }
                // Mirror Tools/Layout/extract-chrome-layout.mjs's wrapper filter:
                // Chrome's walk skips `<html>` and `<body>` (they're treated as
                // viewport-bound wrappers, not document elements). Unity's HTML
                // parser does not synthesize a body around fragment input, but
                // snippets like 26-text-shadow.html and 27-text-shadow-blur.html
                // include an explicit `<body>` tag — without this filter we
                // emit the body as an extra leading element and every
                // subsequent index pairs against the wrong Chrome row.
                bool isWrapperTag = b.Element != null
                                    && (b.Element.TagName == "html" || b.Element.TagName == "body");
                // TextRuns with an element distinct from any ancestor are the
                // principal box for inline children that the layout engine
                // didn't promote to an InlineBox — typically a bare
                // `<span>text</span>` inside a block parent. Chrome reports
                // a rect for that span (its glyph extents), so we surface
                // the matching Unity TextRun. TextRuns that share their
                // parent's Element (the common "plain text inside a div"
                // case) get filtered by seenElements since the parent box
                // already claimed the element.
                bool isPrincipal = b.Element != null
                                   && !isWrapperTag
                                   && b is not LineBox
                                   && b is not AnonymousBlockBox
                                   && b is not AnonymousInlineBox
                                   && seenElements.Add(b.Element);
                if (isPrincipal) order.Add(AabbOf(m, b.Width, b.Height));
                foreach (var c in b.Children) Walk(c, m);
            }
            // Root box has Element == null (synthetic document container);
            // its children are the real document roots. Root.X/Y are 0 so
            // the recursion starts at the viewport origin.
            Walk(root, Transform2D.Identity);
            return order;
        }

        // Post-transform axis-aligned bounding box of a w×h border box mapped
        // through its accumulated local→viewport matrix — what Chrome's
        // getBoundingClientRect returns for transformed elements.
        static BoxRect AabbOf(Transform2D m, double w, double h) {
            (double x0, double y0) = m.Apply(0, 0);
            (double x1, double y1) = m.Apply(w, 0);
            (double x2, double y2) = m.Apply(0, h);
            (double x3, double y3) = m.Apply(w, h);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            return new BoxRect(minX, minY, maxX - minX, maxY - minY);
        }

        static double AsDouble(Dictionary<string, object> d, string key) {
            if (!d.TryGetValue(key, out var v)) return 0;
            switch (v) {
                case double dd: return dd;
                case long ll: return ll;
                case int ii: return ii;
                default: return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
        }

        // -------------------------------------------------------------------
        // MiniJson: a minimal JSON parser sufficient for the chrome-layout
        // dumps. Returns:
        //   object  -> Dictionary<string, object>
        //   array   -> List<object>
        //   string  -> string
        //   number  -> double
        //   true/false/null -> bool / null
        // Throws on malformed input. We deliberately avoid System.Text.Json
        // and JsonUtility to keep the test free of platform-specific
        // dependencies and quirks (JsonUtility can't deserialize lists of
        // objects without a wrapper type per shape).
        static class MiniJson {
            public static object Parse(string s) {
                int i = 0;
                SkipWs(s, ref i);
                var v = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i != s.Length) throw new FormatException("Trailing JSON content at " + i);
                return v;
            }

            static object ParseValue(string s, ref int i) {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return ParseObject(s, ref i);
                if (c == '[') return ParseArray(s, ref i);
                if (c == '"') return ParseString(s, ref i);
                if (c == 't' || c == 'f') return ParseBool(s, ref i);
                if (c == 'n') { Expect(s, ref i, "null"); return null; }
                return ParseNumber(s, ref i);
            }

            static Dictionary<string, object> ParseObject(string s, ref int i) {
                var d = new Dictionary<string, object>();
                Expect(s, ref i, "{");
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true) {
                    SkipWs(s, ref i);
                    string k = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    Expect(s, ref i, ":");
                    object v = ParseValue(s, ref i);
                    d[k] = v;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }

            static List<object> ParseArray(string s, ref int i) {
                var l = new List<object>();
                Expect(s, ref i, "[");
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true) {
                    object v = ParseValue(s, ref i);
                    l.Add(v);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }

            static string ParseString(string s, ref int i) {
                Expect(s, ref i, "\"");
                var sb = new StringBuilder();
                while (i < s.Length) {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && i < s.Length) {
                        char esc = s[i++];
                        switch (esc) {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw new FormatException("Bad \\u at " + i);
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char) code);
                                i += 4;
                                break;
                            default: throw new FormatException("Bad escape '\\" + esc + "' at " + i);
                        }
                    } else {
                        sb.Append(c);
                    }
                }
                throw new FormatException("Unterminated string");
            }

            static object ParseBool(string s, ref int i) {
                if (s[i] == 't') { Expect(s, ref i, "true"); return true; }
                Expect(s, ref i, "false"); return false;
            }

            static double ParseNumber(string s, ref int i) {
                int start = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            static void Expect(string s, ref int i, string lit) {
                if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                    throw new FormatException("Expected '" + lit + "' at " + i);
                i += lit.Length;
            }

            static void SkipWs(string s, ref int i) {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }
        }
    }
}
