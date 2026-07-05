// PAINT-1 regression: layout's line-box height + baseline must use the
// weight/style-aware face metrics, not the default-weight face.
//
// Before the fix, InlineLayout called metrics.LineHeight(fs) and
// metrics.Ascent(fs) unconditionally, which on SdfFontMetrics route to
// MetricsFor(DefaultFamily, Normal, DefaultWeight=400) regardless of the
// span's `font-weight`. Paint, however, uses MetricsFor(family, style,
// weight) — so a `font-weight: 900` span sized its line-box against the
// regular face while paint drew with the bold face's actual baseline.
// When the two faces diverge in ascent ratio, the per-line glyph centroid
// no longer lands at the center of the line-box layout sized for it; in
// a flex-column container that's the visible "top-heavy" symptom on a
// real play-btn.
//
// This test installs a stub IStyledFontMetrics whose 400-weight face has
// ratio 1.0 (line-height) / 0.8 (ascent) and whose 900-weight face has
// ratio 1.5 / 1.2. A `font-weight: 900` span's line-box must be sized
// for the 900 face (height = fs*1.5), not the 400 face (height = fs*1.0).
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    public class StyledMetricsLineBoxTests {
        // Stub metrics: regular face thin (lh=1.0×fs, ascent=0.8×fs);
        // bold (>=700) face tall (lh=1.5×fs, ascent=1.2×fs).
        sealed class WeightSensitiveMetrics : IStyledFontMetrics {
            public double LineHeight(double fontSize) => fontSize * 1.0;
            public double Ascent(double fontSize) => fontSize * 0.8;
            public double Descent(double fontSize) => fontSize * 0.2;
            public double Measure(string text, double fontSize) => (text?.Length ?? 0) * fontSize * 0.5;
            public double Measure(string text, int start, int length, double fontSize)
                => System.Math.Max(0, length) * fontSize * 0.5;
            public double Measure(string text, double fontSize, string family, FontStyle style, int weight)
                => (text?.Length ?? 0) * fontSize * (weight >= 700 ? 0.6 : 0.5);
            public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight)
                => System.Math.Max(0, length) * fontSize * (weight >= 700 ? 0.6 : 0.5);
            public double LineHeight(double fontSize, string family, FontStyle style, int weight)
                => fontSize * (weight >= 700 ? 1.5 : 1.0);
            public double Ascent(double fontSize, string family, FontStyle style, int weight)
                => fontSize * (weight >= 700 ? 1.2 : 0.8);
            public double Descent(double fontSize, string family, FontStyle style, int weight)
                => fontSize * (weight >= 700 ? 0.3 : 0.2);
        }

        static (Box root, LayoutContext ctx) LayoutWithStub(string html, IStyledFontMetrics stub) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(LayoutTestHelpers.BuiltinUserAgent))
            };
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(stub) {
                ViewportWidthPx = 800, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(stub);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, ctx);
        }

        [Test]
        public void Bold_text_line_box_height_uses_bold_face_metrics_not_default() {
            // A 28px font-weight:900 text run sizes its line-box using the
            // bold face's line-height ratio (1.5), giving 42px — not the
            // default face's 28px. Without the PAINT-1 fix, the line-box
            // would size to 28 because metrics.LineHeight(fs) ignores weight.
            var stub = new WeightSensitiveMetrics();
            var (root, _) = LayoutWithStub(
                "<div style=\"font-size:28px;font-weight:900\">PLAY</div>",
                stub);
            var div = FindByTag<BlockBox>(root, "div");
            Assert.That(div, Is.Not.Null, "div block expected");
            var line = FindFirst<LineBox>(div);
            Assert.That(line, Is.Not.Null, "LineBox expected inside div");
            // Expected: 28 * 1.5 = 42. Regression value (pre-fix): 28 * 1.0 = 28.
            Assert.That(line.Height, Is.EqualTo(42.0).Within(0.5),
                $"line-box height must use BOLD face line-height (1.5*28=42), not default (1.0*28=28). Got {line.Height}.");
        }

        [Test]
        public void Bold_text_line_box_baseline_uses_bold_face_ascent() {
            // Baseline = bold ascent (1.2 * 28 = 33.6). Pre-fix: 0.8 * 28 = 22.4.
            var stub = new WeightSensitiveMetrics();
            var (root, _) = LayoutWithStub(
                "<div style=\"font-size:28px;font-weight:900\">PLAY</div>",
                stub);
            var div = FindByTag<BlockBox>(root, "div");
            var line = FindFirst<LineBox>(div);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Baseline, Is.EqualTo(33.6).Within(0.5),
                $"line baseline must equal BOLD face ascent (1.2*28=33.6), not default ascent (0.8*28=22.4). Got {line.Baseline}.");
        }

        [Test]
        public void Default_weight_text_still_uses_default_face_metrics() {
            // Regression guard: default-weight (400) text still resolves
            // against the regular face metrics, not accidentally upgraded.
            var stub = new WeightSensitiveMetrics();
            var (root, _) = LayoutWithStub(
                "<div style=\"font-size:28px\">PLAY</div>",
                stub);
            var div = FindByTag<BlockBox>(root, "div");
            var line = FindFirst<LineBox>(div);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Height, Is.EqualTo(28.0).Within(0.5));
            Assert.That(line.Baseline, Is.EqualTo(22.4).Within(0.5));
        }

        static T FindByTag<T>(Box root, string tag) where T : Box {
            if (root is T t && root.Element != null
                && string.Equals(root.Element.TagName, tag, System.StringComparison.OrdinalIgnoreCase)) return t;
            foreach (var c in root.ChildList) {
                var hit = FindByTag<T>(c, tag);
                if (hit != null) return hit;
            }
            return null;
        }

        static T FindFirst<T>(Box root) where T : Box {
            if (root is T t) return t;
            foreach (var c in root.ChildList) {
                var hit = FindFirst<T>(c);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
