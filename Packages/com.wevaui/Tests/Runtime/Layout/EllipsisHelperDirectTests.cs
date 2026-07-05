using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // Direct unit tests for EllipsisHelper (CODE_AUDIT_FINDINGS TG10).
    //
    // CODE_AUDIT_FINDINGS TG10 flagged that only TextOverflowEllipsisTests
    // exercises the integration path (LayoutEngine -> InlineLayout ->
    // EllipsisHelper.ApplyIfNeeded). None of the helper's internal logic
    // (LargestPrefixThatFits binary search, ellipsis-glyph measurement,
    // donor-run selection, degenerate-width branch) is unit-tested.
    //
    // This file constructs minimal BlockBox / LineBox / TextRun fixtures
    // by hand and calls EllipsisHelper.ApplyIfNeeded directly so the trim
    // / measure path is exercised without the rest of the layout engine in
    // the loop. Deterministic numbers come from MonoFontMetrics (the
    // parameterless ctor — 0.5em per glyph, see MonoFontMetricsTests).
    //
    // At default font-size 16px and MonoFontMetrics:
    //   - ASCII char width: 0.5 * 16 = 8 px
    //   - U+2026 "…" width: 0.5 * 16 = 8 px (single BMP codepoint, not emoji)
    public class EllipsisHelperDirectTests {
        const string Ellipsis = "…";
        const double FontSize = 16.0;
        const double CharW = 8.0;    // MonoFontMetrics: 0.5em * 16px
        const double EllipsisW = 8.0; // same — "…" is one BMP char

        // Build a ComputedStyle wired with text-overflow:ellipsis +
        // white-space:nowrap + overflow:hidden so ShouldApply() returns true.
        static ComputedStyle EllipsisStyle() {
            var s = new ComputedStyle(null);
            s.Set(CssProperties.TextOverflowId, "ellipsis");
            s.Set(CssProperties.WhiteSpaceId, "nowrap");
            s.Set(CssProperties.OverflowId, "hidden");
            return s;
        }

        // Build a BlockBox whose ContentWidth = width (no padding/border) and
        // a single LineBox child containing one TextRun.
        static (BlockBox container, LineBox line, TextRun run) MakeFixture(string text, double containerWidth) {
            var style = EllipsisStyle();
            var container = new BlockBox();
            container.Style = style;
            container.Width = containerWidth;
            // No padding/border so Width == ContentWidth.

            var line = new LineBox();
            var run = new TextRun(text, style, null, null);
            run.FontSize = FontSize;
            run.X = 0;
            run.Width = text.Length * CharW;
            run.Height = FontSize * 1.2;
            line.AddChild(run);
            line.Width = run.Width;
            container.AddChild(line);
            return (container, line, run);
        }

        static LayoutContext MakeContext() {
            return new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
        }

        // -----------------------------------------------------------------
        // 1. Text that fits exactly — no ellipsis, helper returns unchanged.
        // -----------------------------------------------------------------
        [Test]
        public void Text_that_fits_exactly_is_unchanged() {
            // 10 chars * 8 = 80 px in an 80 px container — exact fit.
            var (container, line, run) = MakeFixture("abcdefghij", 80.0);
            var ctx = MakeContext();
            var pool = new BoxPool();

            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            Assert.That(run.Text, Is.EqualTo("abcdefghij"),
                "Exact-fit text must not be truncated or appended with ellipsis.");
            Assert.That(run.Text.Contains(Ellipsis), Is.False);
            Assert.That(line.Children.Count, Is.EqualTo(1),
                "Helper must not insert extra runs when line already fits.");
            Assert.That(run.Width, Is.EqualTo(80.0).Within(1e-9));
            Assert.That(line.Width, Is.LessThanOrEqualTo(80.0 + 1e-9));
        }

        // -----------------------------------------------------------------
        // 2. Text that overflows by 1 char — trims enough to fit "…".
        // -----------------------------------------------------------------
        [Test]
        public void Text_that_overflows_by_one_char_trims_for_ellipsis() {
            // 11 chars (88 px) in 80 px container. Budget = 80 - 8 = 72 px =>
            // 9 chars survive + "…" = 10 glyphs = 80 px total.
            var (container, line, run) = MakeFixture("abcdefghijk", 80.0);
            var ctx = MakeContext();
            var pool = new BoxPool();

            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            Assert.That(run.Text.EndsWith(Ellipsis), Is.True,
                "Trimmed run must end with U+2026. Got: " + run.Text);
            // Original 11 chars overflowed by 1 -> keep 9 ASCII + 1 ellipsis = 10 glyphs.
            Assert.That(run.Text, Is.EqualTo("abcdefghi" + Ellipsis),
                "Helper must trim exactly enough characters that kept+ellipsis fit content width.");
            Assert.That(line.Width, Is.LessThanOrEqualTo(80.0 + 1e-9),
                "Final line width must not exceed content width.");
        }

        // -----------------------------------------------------------------
        // 3. Empty text — helper is a no-op (no crash, no extra runs).
        // -----------------------------------------------------------------
        [Test]
        public void Empty_text_is_noop() {
            var (container, line, run) = MakeFixture("", 80.0);
            var ctx = MakeContext();
            var pool = new BoxPool();

            // Empty run width is already 0, so line.Width <= contentW and the
            // helper's `line.Width <= contentW + eps` early-out fires.
            Assert.DoesNotThrow(() =>
                EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool));

            Assert.That(run.Text, Is.EqualTo(""),
                "Empty-text run must remain empty (no ellipsis appended).");
            Assert.That(run.Text.Contains(Ellipsis), Is.False);
            Assert.That(line.Children.Count, Is.EqualTo(1));
            Assert.That(line.Width, Is.EqualTo(0).Within(1e-9));
        }

        // -----------------------------------------------------------------
        // 4. Ellipsis glyph width is correctly measured against IFontMetrics.
        // -----------------------------------------------------------------
        [Test]
        public void Ellipsis_glyph_width_matches_active_font_metrics() {
            // Verify the helper's "budget = contentW - ellipsisWidth" matches
            // what IFontMetrics.Measure("…") returns. Mono metrics report
            // 8 px for "…" at 16 px font-size (single BMP codepoint, 0.5em).
            //
            // We pick a container width such that the budget admits an exact
            // integer number of donor chars. Container 56 px -> budget =
            // 56 - 8 = 48 px = 6 chars; with the appended ellipsis the line
            // is exactly 48 + 8 = 56 px wide.
            var metrics = new MonoFontMetrics();
            double measured = metrics.Measure(Ellipsis, FontSize);
            Assert.That(measured, Is.EqualTo(EllipsisW).Within(1e-9),
                "MonoFontMetrics.Measure(\"…\", 16) must equal 8 px (0.5em).");

            var (container, line, run) = MakeFixture("abcdefghij", 56.0);
            var ctx = MakeContext();
            var pool = new BoxPool();
            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            Assert.That(run.Text, Is.EqualTo("abcdef" + Ellipsis),
                "Helper must trim to budget = contentW - measured ellipsis width.");
            // 7 glyphs (6 ASCII + "…") × 8 = 56 px — exactly the container width.
            Assert.That(line.Width, Is.EqualTo(56.0).Within(1e-9));
        }

        // -----------------------------------------------------------------
        // 5. Multi-line container (multiple LineBoxes, e.g. produced by a
        //    hard <br>): helper truncates the first-line content.
        // -----------------------------------------------------------------
        [Test]
        public void Multi_line_container_truncates_overflowing_first_line() {
            // EllipsisHelper iterates container.Children and applies to
            // every LineBox whose Width > ContentWidth. With nowrap a hard
            // line-break would still produce multiple LineBoxes; we
            // construct that shape directly: line A overflows, line B fits.
            var style = EllipsisStyle();
            var container = new BlockBox();
            container.Style = style;
            container.Width = 80.0;

            // First line: 15 chars * 8 = 120 px > 80 px (overflows).
            var lineA = new LineBox();
            var runA = new TextRun("aaaaaaaaaaaaaaa", style, null, null);
            runA.FontSize = FontSize;
            runA.X = 0;
            runA.Width = 15 * CharW;
            runA.Height = FontSize * 1.2;
            lineA.AddChild(runA);
            lineA.Width = runA.Width;
            container.AddChild(lineA);

            // Second line: 3 chars * 8 = 24 px < 80 px (fits as-is).
            var lineB = new LineBox();
            var runB = new TextRun("bbb", style, null, null);
            runB.FontSize = FontSize;
            runB.X = 0;
            runB.Width = 3 * CharW;
            runB.Height = FontSize * 1.2;
            lineB.AddChild(runB);
            lineB.Width = runB.Width;
            container.AddChild(lineB);

            var ctx = MakeContext();
            var pool = new BoxPool();
            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            // First line truncated with ellipsis (9 a's + "…" = 80 px).
            Assert.That(runA.Text, Is.EqualTo("aaaaaaaaa" + Ellipsis),
                "Overflowing first line must receive an ellipsis. Got: " + runA.Text);
            Assert.That(lineA.Width, Is.LessThanOrEqualTo(80.0 + 1e-9));

            // Second line was already within budget — helper must leave it alone.
            Assert.That(runB.Text, Is.EqualTo("bbb"),
                "Non-overflowing subsequent line must remain unchanged.");
            Assert.That(runB.Text.Contains(Ellipsis), Is.False);
        }

        // -----------------------------------------------------------------
        // 6. Degenerate width (< ellipsis itself): pin observed behavior.
        // -----------------------------------------------------------------
        [Test]
        public void Width_smaller_than_ellipsis_yields_only_ellipsis_glyph() {
            // contentW = 4 px, ellipsisW = 8 px. budget = max(0, 4-8) = 0
            // so LargestPrefixThatFits returns 0. The donor run is replaced
            // with a pure ellipsis run sitting at trS.X (= 0). Width of the
            // run becomes the full ellipsis width (8 px) which is GREATER
            // than the container's 4 px — that's the documented degraded
            // behavior: the ellipsis itself always renders, even when it
            // overflows the container. Pin it.
            var (container, line, run) = MakeFixture("abcdef", 4.0);
            var ctx = MakeContext();
            var pool = new BoxPool();
            Assert.DoesNotThrow(() =>
                EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool));

            Assert.That(run.Text, Is.EqualTo(Ellipsis),
                "When budget=0 the donor run must be replaced with a pure ellipsis.");
            Assert.That(run.Width, Is.EqualTo(EllipsisW).Within(1e-9),
                "Ellipsis-only run keeps its measured glyph width (degraded — exceeds container).");
            Assert.That(line.Children.Count, Is.EqualTo(1),
                "Trailing runs (none here, but any) must be discarded.");
            Assert.That(line.Width, Is.EqualTo(EllipsisW).Within(1e-9));
        }

        // -----------------------------------------------------------------
        // 7. ShouldApply gate — helper is a no-op when text-overflow is not
        //    ellipsis even if the line clearly overflows.
        // -----------------------------------------------------------------
        [Test]
        public void Helper_skips_when_text_overflow_not_ellipsis() {
            var (container, line, run) = MakeFixture("abcdefghijk", 80.0);
            // Override text-overflow to clip — predicate must reject.
            container.Style.Set(CssProperties.TextOverflowId, "clip");
            var ctx = MakeContext();
            var pool = new BoxPool();

            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            Assert.That(run.Text, Is.EqualTo("abcdefghijk"),
                "text-overflow:clip must leave the run untouched.");
            Assert.That(run.Text.Contains(Ellipsis), Is.False);
        }

        // -----------------------------------------------------------------
        // 8. Multiple runs on one line, snip lands in second run — verifies
        //    LargestPrefixThatFits + trailing-run removal.
        // -----------------------------------------------------------------
        [Test]
        public void Multi_run_line_truncates_in_second_run_and_drops_trailing_runs() {
            // Line: [run0 "aa" @ x=0 w=16] [run1 "bbbbbbbb" @ x=16 w=64]
            //       [run2 "cc" @ x=80 w=16]. Total line width = 96 px.
            // Container = 80 px -> budget = 80 - 8 = 72.
            //   run0 right edge = 16 <= 72 -> continue.
            //   run1 right edge = 80 > 72 -> snip here.
            //     LargestPrefixThatFits over "bbbbbbbb" with maxWidth = 72 - 16
            //     = 56 px -> keep 7 chars ("bbbbbbb"), width 56.
            //   run1 becomes "bbbbbbb…" (width 56 + 8 = 64); run2 removed.
            var style = EllipsisStyle();
            var container = new BlockBox();
            container.Style = style;
            container.Width = 80.0;

            var line = new LineBox();
            container.AddChild(line);

            TextRun r0 = new TextRun("aa", style, null, null);
            r0.FontSize = FontSize; r0.X = 0; r0.Width = 2 * CharW; r0.Height = FontSize * 1.2;
            line.AddChild(r0);

            TextRun r1 = new TextRun("bbbbbbbb", style, null, null);
            r1.FontSize = FontSize; r1.X = 16; r1.Width = 8 * CharW; r1.Height = FontSize * 1.2;
            line.AddChild(r1);

            TextRun r2 = new TextRun("cc", style, null, null);
            r2.FontSize = FontSize; r2.X = 80; r2.Width = 2 * CharW; r2.Height = FontSize * 1.2;
            line.AddChild(r2);

            line.Width = 96.0;

            var ctx = MakeContext();
            var pool = new BoxPool();
            EllipsisHelper.ApplyIfNeeded(container, container.ContentWidth, ctx, pool);

            Assert.That(r0.Text, Is.EqualTo("aa"),
                "Run preceding the snip point must remain intact.");
            Assert.That(r1.Text, Is.EqualTo("bbbbbbb" + Ellipsis),
                "Snip run must be trimmed and have the ellipsis appended.");
            Assert.That(line.Children.Count, Is.EqualTo(2),
                "Runs after the snip point must be discarded.");
            Assert.That(line.Width, Is.LessThanOrEqualTo(80.0 + 1e-9));
        }
    }
}
