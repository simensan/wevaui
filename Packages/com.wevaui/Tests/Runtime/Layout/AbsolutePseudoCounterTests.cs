using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Diagnostics;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // GAP-1 from advanced-dashboard audit (2026-06-01) — `::before` with
    // `content: counter(name)` AND `position: absolute` produces a
    // BlockBox with width=0 / height=0. The text content resolves
    // correctly via CounterContext (covered by `CounterScopeTests`), but
    // the absolute box doesn't surface the text run in the box tree.
    //
    // CSS Lists L3 §2.1: counter() must produce the formatted value.
    // CSS 2.1 §10.3.7: absolutely positioned shrink-to-fit width is
    //                  max(preferred minimum width, min(available, preferred width)).
    //                  Empty content → 0 width; text content → text width.
    public class AbsolutePseudoCounterTests {
        // BoxBuilder-only path: tree shape + text content captured but no
        // layout pass runs. Use FindTextRun against this output.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithPseudos(
            string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);
            return (bb.BuildDocument(doc), styles);
        }

        // Full pipeline (cascade → BoxBuilder → LayoutEngine.Layout with
        // pseudo-element resolvers wired). Use this for tests that assert
        // box X/Y/W/H (which require the layout pass to have run).
        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) BuildLaidOutWithPseudos(
            string html, string css, double viewportWidth = 400) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var fm = new MonoFontMetrics();
            var ctx = new LayoutContext(fm) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(fm);
            le.BeforeStyleOf = e => engine.ComputeBefore(e);
            le.AfterStyleOf  = e => engine.ComputeAfter(e);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            LayoutInvariants.Check(root);
            return (root, styles, ctx);
        }

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.ChildList) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        static TextRun FindTextRun(Box root, string expected) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text == expected) return tr;
            }
            return null;
        }

        // Find a pseudo BlockBox (Element==null, contains the text "expected"
        // somewhere in its descendant subtree — the run may be wrapped in
        // an inline LineBox).
        static Box FindPseudoBoxByText(Box root, string expected) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null) continue;
                if (!(b is BlockBox)) continue;
                foreach (var d in AllBoxes(b)) {
                    if (d is TextRun tr && tr.Text == expected) return b;
                }
            }
            return null;
        }

        // ── 1. Repro: absolute ::before with counter() loses text ─────────

        [Test]
        public void Absolute_pseudo_before_with_counter_value_emits_text_run() {
            // Repro the dashboard topology exactly: list resets counter,
            // each item increments and uses an absolutely-positioned
            // ::before to print the counter in the corner.
            const string css = @"
                .list { counter-reset: c; position: relative; }
                .item { counter-increment: c; position: relative; padding: 12px; }
                .item::before {
                    content: counter(c);
                    position: absolute;
                    top: 4px;
                    right: 4px;
                    font-size: 10px;
                }
            ";
            const string html = @"
                <div class=""list"">
                    <div class=""item"">a</div>
                    <div class=""item"">b</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            // Each pseudo must contain a TextRun with the counter value
            // "1", "2" — proving counter() resolved and was emitted.
            var run1 = FindTextRun(root, "1");
            var run2 = FindTextRun(root, "2");
            Assert.That(run1, Is.Not.Null, "first ::before must contain TextRun '1'");
            Assert.That(run2, Is.Not.Null, "second ::before must contain TextRun '2'");
        }

        // ── 2. Repro: dashboard stat-card topology ────────────────────────

        [Test]
        public void Dashboard_stat_card_counter_emits_decimal_leading_zero() {
            // The exact CSS pattern that fails in the advanced-dashboard
            // probe: counter with decimal-leading-zero style on an
            // absolutely-positioned ::before.
            const string css = @"
                .stat-cards { counter-reset: stat; }
                .stat-card { counter-increment: stat; position: relative; padding: 12px; }
                .stat-card::before {
                    content: counter(stat, decimal-leading-zero);
                    position: absolute;
                    top: 8px;
                    right: 10px;
                    font-size: 10px;
                }
            ";
            const string html = @"
                <ol class=""stat-cards"">
                    <li class=""stat-card"">a</li>
                    <li class=""stat-card"">b</li>
                </ol>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            // decimal-leading-zero pads single-digit counters to 2 digits.
            var pseudo01 = FindPseudoBoxByText(root, "01");
            var pseudo02 = FindPseudoBoxByText(root, "02");
            Assert.That(pseudo01, Is.Not.Null, "first ::before pseudo must contain text '01'");
            Assert.That(pseudo02, Is.Not.Null, "second ::before pseudo must contain text '02'");
        }

        // ── 3. Width: the absolute pseudo must shrink-to-fit the text ────

        [Test]
        public void Absolute_pseudo_with_text_content_has_nonzero_width() {
            // CSS 2.1 §10.3.7: an absolutely positioned box without
            // explicit width or left+right pair sizes to the shrink-to-fit
            // of its content. Text content "1" at 14px font is ~7px wide
            // in MonoFont metrics. Box width MUST be > 0.
            const string css = @"
                .list { counter-reset: c; }
                .item { counter-increment: c; position: relative; padding: 20px; }
                .item::before {
                    content: counter(c);
                    position: absolute;
                    top: 2px;
                    right: 2px;
                    font-size: 14px;
                }
            ";
            var (root, _, _) = BuildLaidOutWithPseudos(
                "<div class=\"list\"><div class=\"item\">x</div></div>", css);
            var pseudo = FindPseudoBoxByText(root, "1");
            Assert.That(pseudo, Is.Not.Null, "pseudo must contain TextRun '1'");
            Assert.That(pseudo.Width, Is.GreaterThan(0),
                $"absolute pseudo with text content must have width > 0 (shrink-to-fit); got W={pseudo.Width}");
            Assert.That(pseudo.Height, Is.GreaterThan(0),
                $"absolute pseudo with text content must have height > 0; got H={pseudo.Height}");
        }
    }
}
