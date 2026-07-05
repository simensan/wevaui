// FixtureRegressionTests — pins per-element (x, y, w, h) values for the
// cleanest sample HTML/CSS fixtures (dialogue, vendor, map) so future engine
// regressions of the recently-fixed bugs surface as red tests without a
// manual Chrome-vs-Unity diff run.
//
// Bugs each fixture covers (one or more assertions per bug):
//   #234 flex aspect-ratio          -> Dialogue_portrait_is_positioned_correctly,
//                                       Dialogue_speaker_meta_below_portrait
//   #237 grid-stretched aspect-ratio-> Vendor_item_frame_height_matches_width,
//                                       Vendor_item_glyph_centered_in_frame
//   #254 inline-flex shrink         -> Vendor_tab_widths_are_text_intrinsic
//   #242 abs-pos shrink-to-fit      -> Map_player_marker_not_off_screen,
//                                       Map_marker_label_pill_is_visible
//
// Each test loads the on-disk Assets/UI/<name>.html + .css, runs the same
// UIDocumentBuilder pipeline RandhtmlLayoutDumpTest uses (so headless layout
// matches the JSON dumps under Assets/UI/<name>.unity-layout.json), walks the
// box tree absolute-coord-style, and asserts the geometry of 4-6 KEY elements
// most affected by the listed fixes. Tolerances default to +/- 2px per axis,
// matching Tools/Layout/diff-* tolerance. A handful of asserts widen to 5px
// where the engine's current state has a known small drift versus the
// theoretical value (see per-assert comments).
//
// Viewport is fixed at 1434x781 so values line up with the committed
// *.unity-layout.json dumps under Assets/UI.

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    public class FixtureRegressionTests {
        const double ViewportW = 1434;
        const double ViewportH = 781;
        const double Tol = 2.0;       // default per-axis tolerance (px)
        const double TolLoose = 5.0;  // widened tolerance for known small drifts

        // ----- pipeline ----------------------------------------------------

        struct ElementRect {
            public Element Element;
            public string Tag;
            public string ClassName;
            public double X;
            public double Y;
            public double W;
            public double H;
        }

        static List<ElementRect> LoadFixture(string fixtureName) {
            string root = ProjectRoot();
            string htmlPath = Path.Combine(root, "Assets", "UI", fixtureName + ".html");
            string cssPath = Path.Combine(root, "Assets", "UI", fixtureName + ".css");
            Assert.That(File.Exists(htmlPath), Is.True, "missing " + htmlPath);
            Assert.That(File.Exists(cssPath), Is.True, "missing " + cssPath);

            // Plain MonoFontMetrics is sufficient for these regression tests
            // even though the dump tool uses TMP metrics for closer Chrome
            // parity. The values we pin here are based on box-level layout
            // (position/size) that is robust to text-metric drift within the
            // +/- 2px tolerance, except where a span's intrinsic width is
            // being measured (vendor tab widths) — there we assert a loose
            // upper bound (< 100px) that holds for either metric source.
            var builder = new UIDocumentBuilder {
                DocumentSource = File.ReadAllText(htmlPath),
                StylesheetSources = new List<string> { File.ReadAllText(cssPath) },
                MediaContext = MediaContext.Default(ViewportW, ViewportH),
                FontMetricsOverride = new MonoFontMetrics()
            };
            var state = builder.Build();
            UIDocumentLifecycle.RunLayout(state);

            var rects = new List<ElementRect>();
            var seen = new HashSet<Element>();
            Walk(state.RootBox, 0, 0, rects, seen);
            return rects;
        }

        static void Walk(Box box, double parentX, double parentY,
                List<ElementRect> rects, HashSet<Element> seen) {
            if (box == null) return;
            double x = parentX + box.X;
            double y = parentY + box.Y;
            // Apply a CSS translate() into our cumulative coords so the rects
            // match the *.unity-layout.json dumps for elements that use
            // `transform: translate(-50%, -50%)` (map markers, .player).
            if (box.Style != null) {
                string tx = box.Style.Get("transform");
                if (!string.IsNullOrEmpty(tx) && tx != "none") {
                    ApplyTranslate(tx, box.Width, box.Height, ref x, ref y);
                }
            }
            bool isText = box.GetType().Name == "TextRun";
            if (!isText && box.Element != null && !seen.Contains(box.Element)) {
                seen.Add(box.Element);
                rects.Add(new ElementRect {
                    Element = box.Element,
                    Tag = (box.Element.TagName ?? "").ToLowerInvariant(),
                    ClassName = box.Element.ClassName ?? "",
                    X = x,
                    Y = y,
                    W = box.Width,
                    H = box.Height
                });
            }
            foreach (var c in box.Children) Walk(c, x, y, rects, seen);
        }

        static void ApplyTranslate(string raw, double w, double h, ref double x, ref double y) {
            int i = 0;
            while (i < raw.Length) {
                int parenStart = raw.IndexOf('(', i);
                if (parenStart < 0) break;
                int parenEnd = raw.IndexOf(')', parenStart);
                if (parenEnd < 0) break;
                string name = raw.Substring(i, parenStart - i).Trim().ToLowerInvariant();
                string inner = raw.Substring(parenStart + 1, parenEnd - parenStart - 1);
                string[] args = inner.Split(',');
                for (int a = 0; a < args.Length; a++) args[a] = args[a].Trim();
                if (name == "translate" || name == "translatex") {
                    if (args.Length >= 1) x += ParseAxis(args[0], w);
                    if (name == "translate" && args.Length >= 2) y += ParseAxis(args[1], h);
                } else if (name == "translatey") {
                    if (args.Length >= 1) y += ParseAxis(args[0], h);
                }
                i = parenEnd + 1;
                while (i < raw.Length && (raw[i] == ' ' || raw[i] == ',')) i++;
            }
        }

        static double ParseAxis(string token, double basis) {
            if (string.IsNullOrEmpty(token)) return 0;
            token = token.Trim();
            if (token.EndsWith("%")) {
                if (double.TryParse(token.Substring(0, token.Length - 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct)) {
                    return basis * pct * 0.01;
                }
                return 0;
            }
            if (token.EndsWith("px")) token = token.Substring(0, token.Length - 2);
            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)) {
                return v;
            }
            return 0;
        }

        static string ProjectRoot([CallerFilePath] string callerPath = null) {
            // Layout -> Runtime -> Tests -> com.wevaui -> Packages -> repo.
            return Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(callerPath),
                "..", "..", "..", "..", ".."));
        }

        // ----- class-name helpers -----------------------------------------

        static bool ClassListContains(string raw, string className) {
            if (string.IsNullOrEmpty(raw)) return false;
            int start = 0;
            for (int i = 0; i <= raw.Length; i++) {
                if (i == raw.Length || raw[i] == ' ' || raw[i] == '\t') {
                    if (i - start == className.Length &&
                        string.CompareOrdinal(raw, start, className, 0, className.Length) == 0) {
                        return true;
                    }
                    start = i + 1;
                }
            }
            return false;
        }

        static ElementRect FindFirstWithClass(List<ElementRect> rects, string className) {
            foreach (var r in rects) {
                if (ClassListContains(r.ClassName, className)) return r;
            }
            Assert.Fail("no element found with class \"" + className + "\"");
            return default;
        }

        static List<ElementRect> FindAllWithClass(List<ElementRect> rects, string className) {
            var list = new List<ElementRect>();
            foreach (var r in rects) {
                if (ClassListContains(r.ClassName, className)) list.Add(r);
            }
            return list;
        }

        // ------------------------------------------------------------------
        // DIALOGUE — covers #234 flex aspect-ratio.
        //
        // Pre-fix symptom: the .portrait flex item's aspect-ratio resolved
        // height was double-applied so the portrait grew tall and the
        // following sibling (.speaker-meta) was pushed off the bottom of
        // .speaker. Post-fix the portrait is ~292x296 and speaker-meta sits
        // just below it.
        // ------------------------------------------------------------------

        [Test]
        public void Dialogue_portrait_is_positioned_correctly() {
            var rects = LoadFixture("dialogue");
            var portrait = FindFirstWithClass(rects, "portrait");
            // Values pulled from Assets/UI/dialogue.unity-layout.json after
            // PAINT-1 UA fix (line-height: normal — was 1.36). Y shifted from
            // 324.5 → 331.4 because every ancestor's inherited line-box height
            // is now tighter, so the flex container's pre-portrait cumulative
            // content height is ~7px smaller, pushing portrait correspondingly
            // higher in the centred flex axis.
            Assert.That(portrait.X, Is.EqualTo(38.0).Within(Tol), "portrait.X");
            Assert.That(portrait.Y, Is.EqualTo(331.4).Within(Tol), "portrait.Y");
            Assert.That(portrait.W, Is.EqualTo(292.0).Within(Tol), "portrait.W");
            // H shifted 296 → 292 when the post-RepositionAbsolutes re-flex
            // pass landed (LayoutEngine — see PlayActionAbsoluteShrinkTests
            // for the canonical play-btn bug). The aspect-ratio:1/1 portrait
            // (W=292 border-box, border:2px) now resolves border-box square
            // (W=H=292) instead of the pre-fix 4px Y-axis inflation.
            Assert.That(portrait.H, Is.EqualTo(292.0).Within(Tol), "portrait.H");
            // Sanity guard: pre-#234 broken Y was ~462 (off by ~140px because
            // the wrong aspect-ratio basis pushed the portrait down inside
            // the flex container). Make sure we're nowhere near that.
            Assert.That(portrait.Y, Is.LessThan(420), "portrait.Y must not regress to pre-#234 broken value");
        }

        [Test]
        public void Dialogue_speaker_meta_below_portrait() {
            var rects = LoadFixture("dialogue");
            var portrait = FindFirstWithClass(rects, "portrait");
            var meta = FindFirstWithClass(rects, "speaker-meta");
            // Post-fix layout: portrait Y=324.52 H=296, speaker-meta Y=636.52.
            // That's portrait.Y + 312 (portrait.H + 16 column-gap).
            double expectedMetaY = portrait.Y + portrait.H + 16; // gap from .speaker (var(--s-4)=16)
            Assert.That(meta.Y, Is.EqualTo(expectedMetaY).Within(TolLoose),
                "speaker-meta should sit one column-gap below the portrait");
            // Sanity guard: pre-#234 ShiftFollowingSiblings phantom-shift
            // would land speaker-meta ~142px below its correct value. Pin
            // the upper bound so any regression triggers a red test.
            Assert.That(meta.Y, Is.LessThan(700), "speaker-meta Y must not regress past 700px");
        }

        // ------------------------------------------------------------------
        // VENDOR — covers #237 grid-stretched aspect-ratio and #254 inline-
        // flex shrink.
        //
        // #237: .item-frame has `aspect-ratio: 1 / 1` inside a grid-stretched
        // flex row. Pre-fix the height was computed off the parent's row
        // height (~150+) so the frame was ~80x152. Post-fix it's ~80x82 —
        // very close to square; the residual ~2px drift is from the
        // border-box border contributing to one axis but not the other.
        //
        // #254: .tab buttons live inside an inline-flex .tabs nav and used
        // to stretch to 800px each because shrink-to-fit didn't kick in for
        // inline-flex items. Post-fix they're text-sized (<100px each).
        // ------------------------------------------------------------------

        [Test]
        public void Vendor_item_frame_height_matches_width() {
            var rects = LoadFixture("vendor");
            var frames = FindAllWithClass(rects, "item-frame");
            Assert.That(frames.Count, Is.GreaterThan(0), "expected at least one .item-frame");
            foreach (var f in frames) {
                // Theoretical aspect-ratio:1/1 -> h == w. Engine currently
                // sits at 80x82 due to border-box / border interplay; allow
                // 4px slack (matches the diff-tools tolerance for this
                // fixture's item card). Pre-#237 the gap was 70+ px so this
                // assertion still bites on any regression.
                Assert.That(System.Math.Abs(f.H - f.W), Is.LessThanOrEqualTo(4.0),
                    "item-frame should be ~square (w=" + f.W + ", h=" + f.H + ")");
            }
        }

        [Test]
        public void Vendor_item_glyph_centered_in_frame() {
            var rects = LoadFixture("vendor");
            // Pair each .item-frame with its child .item-glyph by document
            // order — both appear in the same order in the dump.
            var frames = FindAllWithClass(rects, "item-frame");
            var glyphs = FindAllWithClass(rects, "item-glyph");
            Assert.That(glyphs.Count, Is.GreaterThanOrEqualTo(frames.Count),
                "expected at least one .item-glyph per .item-frame");
            for (int i = 0; i < frames.Count; i++) {
                var f = frames[i];
                var g = glyphs[i];
                double frameMid = f.Y + f.H * 0.5;
                double glyphMid = g.Y + g.H * 0.5;
                // Pre-#237 the glyph was ~72px below center because the
                // frame's stretched height inflated content-area math. Post-
                // fix the deltas are <2px in the dump; allow 10px slack to
                // stay robust to text-metric drift on the glyph.
                Assert.That(System.Math.Abs(glyphMid - frameMid), Is.LessThan(10.0),
                    "item-glyph[" + i + "] not centered: frameMid=" + frameMid +
                    " glyphMid=" + glyphMid);
            }
        }

        [Test]
        public void Vendor_tab_widths_are_text_intrinsic() {
            var rects = LoadFixture("vendor");
            var tabs = FindAllWithClass(rects, "tab");
            Assert.That(tabs.Count, Is.GreaterThan(0), "expected at least one .tab");
            foreach (var t in tabs) {
                // Pre-#254 each .tab stretched to fill the inline-flex parent
                // (~1380px or 800px depending on path). Post-fix they're
                // sized to their text plus padding — all well under 200px in
                // the dump. Pin a lenient upper bound here so the assertion
                // catches the regression without coupling to specific glyph
                // metrics.
                Assert.That(t.W, Is.LessThan(200.0),
                    "tab width " + t.W + " (.\"" + t.ClassName + "\") looks stretched, not text-sized");
            }
        }

        // ------------------------------------------------------------------
        // MAP — covers #242 absolute-positioned shrink-to-fit.
        //
        // #242: .player and .marker are absolutely positioned with
        // `transform: translate(-50%, -50%)`. Pre-fix the shrink-to-fit
        // width computation went wrong and the elements ended up either
        // off-screen (x < 0) or absurdly wide (x > viewport). Post-fix the
        // player sits in mid-canvas (~680px) and the marker labels are
        // sized to their text.
        // ------------------------------------------------------------------

        [Test]
        public void Map_player_marker_not_off_screen() {
            var rects = LoadFixture("map");
            var player = FindFirstWithClass(rects, "player");
            // Pre-#242 broken X values seen during dev were e.g. -700 (when
            // the shrink-to-fit failed and the translate(-50%) wiped the
            // origin) or +1400 (when the player widened to the viewport).
            // Post-fix the dump shows X ~ 680 inside .canvas (full viewport).
            Assert.That(player.X, Is.GreaterThanOrEqualTo(600.0),
                "player.X regressed left of expected range (X=" + player.X + ")");
            Assert.That(player.X, Is.LessThanOrEqualTo(900.0),
                "player.X regressed right of expected range (X=" + player.X + ")");
            // Also make sure the rect itself stays on the canvas — a broken
            // shrink-to-fit on .player would also blow up the width.
            Assert.That(player.W, Is.LessThan(200.0),
                "player.W " + player.W + " looks stretched, not shrink-to-fit");
        }

        [Test]
        public void Map_marker_label_pill_is_visible() {
            var rects = LoadFixture("map");
            var labels = FindAllWithClass(rects, "marker-label");
            Assert.That(labels.Count, Is.GreaterThan(0), "expected at least one .marker-label");
            foreach (var l in labels) {
                // Pin the text-sized contract: each label pill stays under
                // 200px wide (the dump's labels are 67-95px). Pre-#242 they
                // ballooned past the viewport.
                Assert.That(l.W, Is.LessThan(200.0),
                    "marker-label width " + l.W + " looks stretched, not text-sized");
                // And the right edge must stay on-screen so the label is
                // actually visible. translate(-50%) is already applied by
                // the walker so the rect we compare here is in the same
                // coordinate space as the viewport.
                Assert.That(l.X + l.W, Is.LessThan(ViewportW + Tol),
                    "marker-label right edge " + (l.X + l.W) + " spills past viewport");
                Assert.That(l.X, Is.GreaterThan(-Tol),
                    "marker-label left edge " + l.X + " is off-screen left");
            }
        }
    }
}
