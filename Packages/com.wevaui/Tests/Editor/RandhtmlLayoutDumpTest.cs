// Headless layout dump for randhtml.html — runs the full UIDocumentBuilder
// pipeline against the on-disk randhtml.html + randhtml.css at a configurable
// viewport, walks the resulting box tree, and writes per-element absolute
// coords to a JSON file shaped identically to the Chrome dump produced by
// dump_coords.js. Pair with compare_coords.js to diff browser ground-truth
// against our engine without ever opening Unity's GameView.
//
// Invocation (from a shell):
//   "C:/Program Files/Unity/Hub/Editor/<ver>/Editor/Unity.exe" \
//       -batchmode -nographics -projectPath . \
//       -runTests -testPlatform editmode \
//       -testFilter Weva.Tests.EditorTests.RandhtmlLayoutDumpTest \
//       -testResults Logs/randhtml-test.xml -quit
//
// The test writes unity_coords.json next to the project root so the existing
// node compare_coords.js can be run unchanged afterwards.
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
#if WEVA_TMP
using UnityEditor;
using TMPro;
using Weva.Paint;
using Weva.Text.Tmp;
#endif

namespace Weva.Tests.EditorTests {
    public class RandhtmlLayoutDumpTest {
        // Override via env var WEVA_DUMP_VIEWPORT=WxH; defaults match the
        // current Game window resolution we've been comparing against.
        const double DefaultViewportW = 1434;
        const double DefaultViewportH = 781;

        [Test]
        public void DumpRandhtmlCoords() {
            DumpCoords("randhtml");
        }

        [Test]
        public void DumpDialogueCoords() {
            DumpCoords("dialogue");
        }

        [Test]
        public void DumpInventoryCoords() {
            DumpCoords("inventory");
        }

        [Test]
        public void DumpHudCoords() {
            DumpCoords("hud");
        }

        [Test]
        public void DumpStatsCoords() {
            DumpCoords("stats");
        }

        [Test]
        public void DumpVendorCoords() {
            DumpCoords("vendor");
        }

        [Test]
        public void DumpLeaderboardCoords() {
            DumpCoords("leaderboard");
        }

        [Test]
        public void DumpSettingsCoords() {
            DumpCoords("settings");
        }

        [Test]
        public void DumpMapCoords() {
            DumpCoords("map");
        }

        [Test]
        public void DumpQuestsCoords() {
            DumpCoords("quests");
        }

        [Test]
        public void DumpMatch3EndgameCoords() {
            DumpCoords("match3-endgame");
        }

        static void DumpCoords(string fixtureName) {
            string root = ProjectRoot();
            string htmlPath = Path.Combine(root, $"Assets/UI/{fixtureName}.html");
            string cssPath = Path.Combine(root, $"Assets/UI/{fixtureName}.css");
            Assert.That(File.Exists(htmlPath), $"missing {htmlPath}");
            Assert.That(File.Exists(cssPath), $"missing {cssPath}");

            (double vw, double vh) = ViewportFromEnv();

            // Use the same TMP_FontAsset (LiberationSans SDF) the production
            // runtime registers in UitestController so headless dumps measure
            // text with real proportional advances + kerning, not the 0.5em
            // monospace approximation MonoFontMetrics would return. Wrap it
            // in a fallback that defers to MonoFontMetrics for any codepoint
            // the TMP atlas doesn't cover (the LiberationSans atlas omits
            // emoji + many symbol glyphs that the randhtml fixture uses).
            // Without that fallback those glyphs measure to 0 and the dump
            // regresses. This mirrors what SdfBootstrap.TryCreateTmp wires up
            // in production via SdfTextRunBaker's CharacterFallback chain.
            IFontMetrics fontMetrics = new MonoFontMetrics();
#if WEVA_TMP
            // Prefer Segoe UI (matches Chrome's default sans-serif on Windows)
            // when its TMP atlas has been generated. LiberationSans is the
            // bundled fallback; the engine compiled fine before SegoeUI SDF
            // was generated, but compare scores were ~330 because of font-
            // metric mismatch with Chrome's getBoundingClientRect.
            var tmpAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/UI/Fonts/SegoeUI SDF.asset");
            if (tmpAsset == null) {
                tmpAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                    "Assets/TextMesh Pro/Fonts/LiberationSans SDF.asset");
            }
            if (tmpAsset != null) {
                fontMetrics = new TmpWithMonoFallbackMetrics(
                    new TmpFontMetrics(new TmpFontAssetSource(tmpAsset)),
                    new MonoFontMetrics());
            }
            // NOTE: an emoji TmpFontMetrics tier was tried as a layer above
            // MonoFontMetrics so 🛡 ⚔ ⚡ would measure to their SegoeUIEmoji
            // advance widths. It regressed the compare score from ~330 to
            // ~337 because Segoe UI Emoji's typographic advances at 90pt do
            // not match Chrome's emoji-shaping advances; MonoFontMetrics's
            // 0.5em placeholder happens to be closer to Chrome's measured
            // width on average. Leaving the simpler chain in place; emoji
            // visibility at render-time is handled via the runtime
            // TmpFontAssetRegistry.AddFallback chain on UitestController.
#endif
            var builder = new UIDocumentBuilder {
                DocumentSource = File.ReadAllText(htmlPath),
                StylesheetSources = new List<string> { File.ReadAllText(cssPath) },
                MediaContext = MediaContext.Default(vw, vh),
                FontMetricsOverride = fontMetrics
            };
            var state = builder.Build();
            UIDocumentLifecycle.RunLayout(state);

            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            var emitted = new HashSet<Element>();
            Walk(state.RootBox, 0, 0, 0, sb, ref first, emitted);
            sb.Append("\n]");
            // randhtml keeps its historical naming (unity_coords_<w>x<h>.json
            // in repo root) so existing compare_coords.js works unchanged.
            // Other fixtures write next to the source HTML for the diff scripts
            // (Tools/Layout/diff-<fixture>.mjs reads
            // Assets/UI/<fixture>.unity-layout.json).
            string outPath = fixtureName == "randhtml"
                ? Path.Combine(root, $"unity_coords_{(int)vw}x{(int)vh}.json")
                : Path.Combine(root, $"Assets/UI/{fixtureName}.unity-layout.json");
            File.WriteAllText(outPath, sb.ToString());
            UnityEngine.Debug.Log($"[RandhtmlLayoutDumpTest] wrote {outPath} (viewport {vw}x{vh})");
        }

        static (double, double) ViewportFromEnv() {
            string raw = System.Environment.GetEnvironmentVariable("WEVA_DUMP_VIEWPORT");
            if (string.IsNullOrEmpty(raw) || !raw.Contains("x")) return (DefaultViewportW, DefaultViewportH);
            var parts = raw.Split('x');
            if (parts.Length != 2) return (DefaultViewportW, DefaultViewportH);
            if (!double.TryParse(parts[0], out double w)) w = DefaultViewportW;
            if (!double.TryParse(parts[1], out double h)) h = DefaultViewportH;
            return (w, h);
        }

        static string ProjectRoot() {
            // Application.dataPath ends in /Assets; project root is its parent.
            return Path.GetDirectoryName(UnityEngine.Application.dataPath);
        }

        static void Walk(Box box, double parentX, double parentY, int depth, StringBuilder sb, ref bool first, HashSet<Element> emitted) {
            if (box == null) return;
            double x = parentX + box.X;
            double y = parentY + box.Y;
            // Apply CSS transform translate() into our cumulative coords so
            // the dump matches Chrome's getBoundingClientRect (post-transform).
            // Weva applies transforms only at paint time, so layout-space
            // Box.X/Y for `transform: translateX(-50%)` does NOT include the
            // offset. Parse just translate*() forms here — sufficient for
            // diff parity with the randhtml fixture.
            if (box.Style != null) {
                string tx = box.Style.Get("transform");
                if (!string.IsNullOrEmpty(tx) && tx != "none") {
                    ApplyTransformTranslate(tx, box.Width, box.Height, ref x, ref y);
                }
            }
            // Emit-rect copy: rotate (if any) only affects the rect we report
            // for THIS element to mirror Chrome's getBoundingClientRect. Keep
            // (x, y) for descendant recursion — children of a rotated element
            // are positioned from the un-rotated parent origin in layout space.
            double emitX = x;
            double emitY = y;
            double emitW = box.Width;
            double emitH = box.Height;
            if (box.Style != null) {
                string tx = box.Style.Get("transform");
                if (!string.IsNullOrEmpty(tx) && tx != "none") {
                    ApplyTransformRotateBounds(tx, ref emitX, ref emitY, ref emitW, ref emitH);
                }
            }
            // Skip text-run boxes — Chrome's getBoundingClientRect doesn't
            // surface them as separate rects; we want one entry per element.
            bool isText = box.GetType().Name == "TextRun";
            if (!isText && box.Element != null && !emitted.Contains(box.Element)) {
                emitted.Add(box.Element);
                if (!first) sb.Append(",\n");
                first = false;
                string tag = (box.Element.TagName ?? "").ToLower();
                string cls = JsonEscape(box.Element.ClassName ?? "");
                string id = JsonEscape(box.Element.Id ?? "");
                sb.Append("{\"depth\":").Append(depth)
                  .Append(",\"tag\":\"").Append(tag).Append("\"")
                  .Append(",\"cls\":\"").Append(cls).Append("\"")
                  .Append(",\"id\":\"").Append(id).Append("\"")
                  .Append(",\"x\":").Append(emitX.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(emitY.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"w\":").Append(emitW.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"h\":").Append(emitH.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("}");
            }
            // Inline-element synthesis: when we walk into a LineBox, the box
            // tree's surviving descendants are TextRuns / atom blocks — the
            // original `<span>` InlineBox parents were consumed by inline
            // layout (CollectInline flattens them, then container.ClearChildren
            // detaches the InlineBox). To restore element-rect parity with
            // Chrome's getBoundingClientRect, synthesize an entry per distinct
            // TextRun-Element under the LineBox, with bounds = union of those
            // TextRuns' rects. Skip elements that were already emitted by an
            // ancestor (anonymous-block wrapping or repeated traversal).
            if (box is LineBox) {
                SynthesizeInlineElementEntries(box, x, y, depth + 1, sb, ref first, emitted);
            }
            int nextDepth = isText ? depth : depth + 1;
            foreach (var c in box.Children) Walk(c, x, y, nextDepth, sb, ref first, emitted);
        }

        // Walks a LineBox's children in order, grouping TextRuns by their
        // Element pointer, and emits a synthetic span entry per distinct
        // Element whose bounds are the axis-aligned union of its TextRun rects.
        // The line-breaker may interleave TextRuns from different inline
        // ancestors when a span is followed by sibling text inside the same
        // line; iterating in order and bucketing by Element captures exactly
        // the contiguous-or-not fragments that belong to each `<span>`.
        static void SynthesizeInlineElementEntries(Box lineBox, double lineX, double lineY,
                int depth, StringBuilder sb, ref bool first, HashSet<Element> emitted) {
            // Per-element bounds accumulator. The Element keys here are the
            // span elements assigned to TextRuns by BuildInlineChildren /
            // AppendInlineChild (run.Element = parent.Element when the parent
            // is an InlineBox-backed by a real DOM element).
            Dictionary<Element, (double x0, double y0, double x1, double y1)> bounds = null;
            foreach (var child in lineBox.Children) {
                if (!(child is TextRun tr)) continue;
                var el = tr.Element;
                if (el == null) continue;
                // The TextRun's Element points either to the enclosing span
                // (when it's nested in an inline element) or to the line/div
                // (when it's a direct text child of the block). The latter is
                // already emitted by the outer Walk for the BlockBox; we only
                // synthesize for spans that haven't been emitted yet. Defer
                // the contains-check until after we've grouped — emitting
                // would otherwise interleave with the line traversal in a
                // way that depends on ordering.
                double rx = lineX + tr.X;
                double ry = lineY + tr.Y;
                double rw = tr.Width;
                double rh = tr.Height;
                bounds ??= new Dictionary<Element, (double, double, double, double)>();
                if (bounds.TryGetValue(el, out var prev)) {
                    bounds[el] = (System.Math.Min(prev.x0, rx), System.Math.Min(prev.y0, ry),
                                  System.Math.Max(prev.x1, rx + rw), System.Math.Max(prev.y1, ry + rh));
                } else {
                    bounds[el] = (rx, ry, rx + rw, ry + rh);
                }
            }
            if (bounds == null) return;
            foreach (var kv in bounds) {
                var el = kv.Key;
                if (emitted.Contains(el)) continue;
                // Match the chrome dump filter (compare_coords.js): bare
                // `<span>` wrappers without class/id are stripped from the
                // randhtml baseline, so synthesizing entries for them here
                // only creates phantom matches that drift the index-based
                // pairwise comparator. For other fixtures (dialogue.html,
                // stats.html) the chrome extract walks every non-display:none
                // element and DOES include bare em/strong/b/i tags — those
                // have semantic styling distinct from their parent so they
                // need to appear. Skip only bare <span>: keep formatting
                // tags so per-fixture diffs aren't missing rows.
                string tagLower = (el.TagName ?? "").ToLower();
                bool hasIdentity = !string.IsNullOrEmpty(el.ClassName) || !string.IsNullOrEmpty(el.Id);
                bool isFormattingTag = tagLower == "em" || tagLower == "strong"
                    || tagLower == "b" || tagLower == "i" || tagLower == "code"
                    || tagLower == "u" || tagLower == "kbd" || tagLower == "small";
                if (!hasIdentity && !isFormattingTag) continue;
                emitted.Add(el);
                var (x0, y0, x1, y1) = kv.Value;
                if (!first) sb.Append(",\n");
                first = false;
                string tag = (el.TagName ?? "").ToLower();
                string cls = JsonEscape(el.ClassName ?? "");
                string id = JsonEscape(el.Id ?? "");
                sb.Append("{\"depth\":").Append(depth)
                  .Append(",\"tag\":\"").Append(tag).Append("\"")
                  .Append(",\"cls\":\"").Append(cls).Append("\"")
                  .Append(",\"id\":\"").Append(id).Append("\"")
                  .Append(",\"x\":").Append(x0.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(y0.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"w\":").Append((x1 - x0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"h\":").Append((y1 - y0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("}");
            }
        }

        static void ApplyTransformTranslate(string raw, double w, double h, ref double x, ref double y) {
            // Walk function calls "translate*( ... )" and accumulate offsets.
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
                    if (args.Length >= 1) x += ParseTranslateAxis(args[0], w);
                    if (name == "translate" && args.Length >= 2) y += ParseTranslateAxis(args[1], h);
                } else if (name == "translatey") {
                    if (args.Length >= 1) y += ParseTranslateAxis(args[0], h);
                }
                i = parenEnd + 1;
                while (i < raw.Length && (raw[i] == ' ' || raw[i] == ',')) i++;
            }
        }

        // Expands (x, y, w, h) into the axis-aligned bounding box of the
        // rotated rect. Chrome's getBoundingClientRect returns this expanded
        // box for any element with `transform: rotate(...)`. The rotation
        // pivots around the rect center (default `transform-origin: 50% 50%`),
        // so the bbox center is unchanged; only width/height grow and the
        // top-left shifts by half the size delta. Handles rotate(<angle>),
        // rotateZ(<angle>), and rotate3d(0,0,1,<angle>). Other rotate3d axes
        // (which would tilt the rect out-of-plane) are conservatively ignored.
        static void ApplyTransformRotateBounds(string raw, ref double x, ref double y, ref double w, ref double h) {
            double angleRad = 0;
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
                if (name == "rotate" || name == "rotatez") {
                    if (args.Length >= 1) angleRad += ParseAngleRad(args[0]);
                } else if (name == "rotate3d") {
                    // Only the Z-axis rotation affects our 2D bbox. Treat
                    // (0,0,Z,angle) as a plain rotate; ignore other axes.
                    if (args.Length >= 4) {
                        double ax = ParseNumber(args[0]);
                        double ay = ParseNumber(args[1]);
                        double az = ParseNumber(args[2]);
                        if (System.Math.Abs(ax) < 1e-6 && System.Math.Abs(ay) < 1e-6 && System.Math.Abs(az) > 1e-6) {
                            angleRad += ParseAngleRad(args[3]) * System.Math.Sign(az);
                        }
                    }
                }
                i = parenEnd + 1;
                while (i < raw.Length && (raw[i] == ' ' || raw[i] == ',')) i++;
            }
            if (angleRad == 0) return;
            double cs = System.Math.Abs(System.Math.Cos(angleRad));
            double sn = System.Math.Abs(System.Math.Sin(angleRad));
            double newW = w * cs + h * sn;
            double newH = w * sn + h * cs;
            // Center is preserved; shift top-left by half the size delta.
            x -= (newW - w) * 0.5;
            y -= (newH - h) * 0.5;
            w = newW;
            h = newH;
        }

        static double ParseAngleRad(string token) {
            if (string.IsNullOrEmpty(token)) return 0;
            token = token.Trim().ToLowerInvariant();
            double scale = System.Math.PI / 180.0; // default deg
            if (token.EndsWith("deg")) { token = token.Substring(0, token.Length - 3); scale = System.Math.PI / 180.0; }
            else if (token.EndsWith("rad")) { token = token.Substring(0, token.Length - 3); scale = 1.0; }
            else if (token.EndsWith("turn")) { token = token.Substring(0, token.Length - 4); scale = 2 * System.Math.PI; }
            else if (token.EndsWith("grad")) { token = token.Substring(0, token.Length - 4); scale = System.Math.PI / 200.0; }
            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)) {
                return v * scale;
            }
            return 0;
        }

        static double ParseNumber(string token) {
            if (string.IsNullOrEmpty(token)) return 0;
            if (double.TryParse(token.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)) return v;
            return 0;
        }

        static double ParseTranslateAxis(string token, double basis) {
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
            // Strip "px" suffix if present.
            if (token.EndsWith("px")) token = token.Substring(0, token.Length - 2);
            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)) {
                return v;
            }
            return 0;
        }

        static string JsonEscape(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

#if WEVA_TMP
        // Composite metrics: prefer TmpFontMetrics (real SDF atlas advances +
        // kerning) for any codepoint the atlas covers, fall back to
        // MonoFontMetrics's 0.5em-per-char approximation for codepoints the
        // atlas doesn't have (emoji, exotic symbols). Mirrors the production
        // bootstrap's CharacterFallback chain shape, but for headless layout-
        // dump tests we don't need the full SDF rasterizer hookup — only
        // metrics. Using the mono fallback (instead of returning 0) keeps
        // emoji-bearing rects width-stable and prevents the dump from
        // regressing relative to the all-mono baseline.
        sealed class TmpWithMonoFallbackMetrics : IStyledFontMetrics {
            readonly TmpFontMetrics primary;
            readonly IFontMetrics fallback;
            public TmpWithMonoFallbackMetrics(TmpFontMetrics primary, IFontMetrics fallback) {
                this.primary = primary;
                this.fallback = fallback;
            }
            // Expose the primary TMP source for the optional emoji-tier
            // wrapper layered above this class.
            public TmpFontAssetSource PrimarySource => primary.Source;
            public double LineHeight(double fontSize) {
                double v = primary.LineHeight(fontSize);
                return v > 0 ? v : fallback.LineHeight(fontSize);
            }
            public double Ascent(double fontSize) {
                double v = primary.Ascent(fontSize);
                return v > 0 ? v : fallback.Ascent(fontSize);
            }
            public double Descent(double fontSize) {
                double v = primary.Descent(fontSize);
                return v > 0 ? v : fallback.Descent(fontSize);
            }
            // Per-codepoint dispatch so each char picks the best source.
            // Mirrors TmpFontMetrics.Measure but routes uncovered codepoints
            // through the mono fallback's per-char advance instead of zero.
            public double Measure(string text, double fontSize) {
                if (string.IsNullOrEmpty(text)) return 0;
                double total = 0;
                int i = 0;
                int n = text.Length;
                uint prevCp = 0;
                var src = primary.Source;
                while (i < n) {
                    char c = text[i];
                    int len = 1;
                    uint cp = c;
                    if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) {
                        cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                        len = 2;
                    }
                    if (src != null && src.TryGetGlyph(cp, fontSize, out _, out var gm)) {
                        total += gm.AdvanceX;
                        if (prevCp != 0) total += src.GetKern(prevCp, cp, fontSize);
                    } else {
                        // Mono fallback: 0.5em per char (or per surrogate pair).
                        total += fallback.Measure(text.Substring(i, len), fontSize);
                    }
                    prevCp = cp;
                    i += len;
                }
                return total;
            }

            public double Measure(string text, double fontSize, string family, FontStyle style, int weight) {
                double styled = primary.Measure(text, fontSize, family, style, weight);
                return styled > 0 ? styled : Measure(text, fontSize);
            }
            public double Measure(string text, int start, int length, double fontSize) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
                return Measure(slice, fontSize);
            }
            public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
                return Measure(slice, fontSize, family, style, weight);
            }
            // PAINT-1: weight-aware overloads. The TMP fallback chain binds to a
            // single TmpFontMetrics primary source (which itself is single-weight),
            // so style/weight don't change the resolved face — delegate.
            public double LineHeight(double fontSize, string family, FontStyle style, int weight) => LineHeight(fontSize);
            public double Ascent(double fontSize, string family, FontStyle style, int weight) => Ascent(fontSize);
            public double Descent(double fontSize, string family, FontStyle style, int weight) => Descent(fontSize);
        }

        // Layered metrics with three tiers: primaryChain (LiberationSans
        // -> Mono) is consulted first; codepoints that LiberationSans lacks
        // get probed against the emoji source before MonoFontMetrics's tofu
        // box width takes over. Wraps the existing TmpWithMonoFallbackMetrics
        // wholesale — its Measure already does per-codepoint dispatch — and
        // prepends a second TMP probe for emoji widths.
        sealed class TmpWithTmpFallbackMetrics : IStyledFontMetrics {
            readonly TmpWithMonoFallbackMetrics primaryChain;
            readonly TmpFontMetrics emojiTier;
            public TmpWithTmpFallbackMetrics(TmpWithMonoFallbackMetrics primaryChain, TmpFontMetrics emojiTier) {
                this.primaryChain = primaryChain;
                this.emojiTier = emojiTier;
            }
            public double LineHeight(double fontSize) => primaryChain.LineHeight(fontSize);
            public double Ascent(double fontSize) => primaryChain.Ascent(fontSize);
            public double Descent(double fontSize) => primaryChain.Descent(fontSize);
            // PAINT-1: weight-aware overloads. Same single-source binding rationale.
            public double LineHeight(double fontSize, string family, FontStyle style, int weight) => primaryChain.LineHeight(fontSize);
            public double Ascent(double fontSize, string family, FontStyle style, int weight) => primaryChain.Ascent(fontSize);
            public double Descent(double fontSize, string family, FontStyle style, int weight) => primaryChain.Descent(fontSize);
            public double Measure(string text, double fontSize) {
                if (string.IsNullOrEmpty(text)) return 0;
                // Per-codepoint walk. For each codepoint, prefer the primary
                // source if it has the glyph; else try the emoji tier; else
                // fall through to the mono-backed primaryChain (which itself
                // already has a mono fallback for misses).
                double total = 0;
                int i = 0;
                int n = text.Length;
                var primarySrc = primaryChain.PrimarySource;
                var emojiSrc = emojiTier.Source;
                while (i < n) {
                    char c = text[i];
                    int len = 1;
                    uint cp = c;
                    if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) {
                        cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                        len = 2;
                    }
                    if (primarySrc != null && primarySrc.TryGetGlyph(cp, fontSize, out _, out var gm)) {
                        total += gm.AdvanceX;
                    } else if (emojiSrc != null && emojiSrc.TryGetGlyph(cp, fontSize, out _, out var egm)) {
                        total += egm.AdvanceX;
                    } else {
                        // Defer to the existing mono-backed chain for the
                        // single codepoint slice. Its Measure handles
                        // surrogate pairs correctly.
                        total += primaryChain.Measure(text.Substring(i, len), fontSize);
                    }
                    i += len;
                }
                return total;
            }

            public double Measure(string text, double fontSize, string family, FontStyle style, int weight) {
                double styled = primaryChain.Measure(text, fontSize, family, style, weight);
                return styled > 0 ? styled : Measure(text, fontSize);
            }
            public double Measure(string text, int start, int length, double fontSize) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
                return Measure(slice, fontSize);
            }
            public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
                return Measure(slice, fontSize, family, style, weight);
            }
        }
#endif
    }
}
