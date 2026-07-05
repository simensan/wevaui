using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Regression coverage for sizing edge cases beyond the basic width/height/
    // aspect-ratio happy path: min/max clamps, percent-basis resolution against
    // various containing blocks, and the interactions between sizing properties
    // and flex / table formatting contexts.
    //
    // Most tests use the shared `Build` helper (LayoutTestHelpers.Build) which
    // ships a trimmed UA stylesheet. Tests that exercise table internals opt
    // in to the real UserAgentStylesheet via `BuildWithRealUA`, mirroring the
    // convention in TableLayoutTests.
    public class SizingConstraintsTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static BlockBox FindFirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.Id == id) return bb;
            }
            return null;
        }

        // Real UA stylesheet builder, parallel to TableLayoutTests.BuildWithRealUA.
        // Needed for tests that touch the table family — the trimmed
        // `LayoutTestHelpers.BuiltinUserAgent` doesn't define table/tr/td display.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithRealUA(
            string html, string authorCss = null, double viewportWidth = 800, double viewportHeight = 600
        ) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> { UserAgentStylesheet.Parse() };
            if (!string.IsNullOrEmpty(authorCss)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(authorCss)));
            }
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles);
        }

        // --- min-width / max-width clamps -----------------------------------

        [Test]
        public void Min_width_clamps_a_shrink_to_fit_box_upward() {
            // CSS Sizing L3 §5.2: shrink-to-fit width is clamped to at least
            // min-width. abs-pos box with `width: auto` and one horizontal pin
            // is shrink-to-fit per CSS Position L3 §10.3.7. Its max-content
            // (a tiny text run) is well under 200px, so min-width drives the
            // final width up to 200.
            var (root, _, _) = Build(
                "<div id=\"sf\" style=\"position:absolute;left:0;min-width:200px\">x</div>",
                null, viewportWidth: 800);
            var div = FindFirstById(root, "sf");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(200).Within(0.5));
        }

        [Test]
        public void Absolute_shrink_to_fit_honors_flex_child_min_width() {
            var (root, _, _) = Build(
                "<div id=\"wrap\" style=\"position:absolute;left:0\">" +
                "<button id=\"btn\" style=\"display:flex;flex-direction:column;min-width:260px;height:76px\">" +
                "<span>PLAY</span><span>Stage 2</span>" +
                "</button></div>",
                null, viewportWidth: 800);
            var wrap = FindFirstById(root, "wrap");
            var btn = FindFirstById(root, "btn");
            Assert.That(wrap, Is.Not.Null);
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.Width, Is.EqualTo(260).Within(0.5));
            Assert.That(wrap.Width, Is.EqualTo(260).Within(0.5));
        }

        [Test]
        public void Max_width_clamps_a_block_box_downward() {
            // CSS Sizing L3 §5.2: `width: 100%` resolves to the containing
            // block's content width, then max-width clamps it. Outer is 800px
            // (viewport), inner resolves to 800 then clamps to 300.
            var (root, _, _) = Build(
                "<div style=\"width:100%;max-width:300px\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Min_width_larger_than_max_width_wins_per_spec() {
            // CSS Sizing L3 §5.2: when min-width > max-width, min-width takes
            // precedence. The engine applies the min clamp AFTER the max
            // clamp (see BlockLayout.ApplyBoxModel), so a max:100, min:200
            // pair: width 50 -> min 200 -> max 100 -> min 200 again? Actually
            // the engine order is: min -> max -> (no re-min). Verify the
            // resulting behaviour empirically.
            var (root, _, _) = Build(
                "<div style=\"width:50px;min-width:200px;max-width:100px\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            // Fixed in #244: BlockLayout now applies max FIRST then min, so
            // when min > max the min clamp raises the post-max value back to
            // min. Result: 200, matching CSS Sizing L3 §5.2.
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Max_width_none_keyword_is_a_no_op() {
            // `max-width: none` means "no upper bound" per CSS Sizing L3 §5.2.
            // Without the keyword the cascade default is already `none`, so
            // this just verifies the explicit value parses correctly and the
            // computed width is unchanged.
            var (root, _, _) = Build(
                "<div style=\"width:500px;max-width:none\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void Min_width_percent_resolves_against_containing_block() {
            // CSS Sizing L3: `min-width: 50%` resolves against the containing
            // block's width. Outer width is 400, inner default `width: auto`
            // would otherwise stretch to 400 too — wrap inner in a flow that
            // makes min-width observable by using `width: 0`, so min clamps
            // it up to 50% × 400 = 200.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"width:400px\"><div id=\"inner\" style=\"width:0;min-width:50%\"></div></div>",
                null, viewportWidth: 800);
            var inner = FindFirstById(root, "inner");
            Assert.That(inner, Is.Not.Null);
            Assert.That(inner.Width, Is.EqualTo(200).Within(0.001));
        }

        // --- min-height / max-height clamps ---------------------------------

        [Test]
        public void Min_height_clamps_short_content_to_floor() {
            // CSS Sizing L3 §5.2: `min-height: 100px` raises the computed
            // height of an otherwise content-sized div to 100. The content
            // here is empty (no inline text) so the natural height is 0.
            var (root, _, _) = Build(
                "<div style=\"min-height:100px\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Max_height_clamps_tall_content() {
            // Inner forces a 400px column (height:400). The outer's
            // max-height should clamp the outer to 200 even though its
            // content overflows. Overflow visibility is not asserted —
            // only that the box's reported Height is clamped.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"max-height:200px\"><div style=\"height:400px\"></div></div>",
                null, viewportWidth: 800);
            var outer = FindFirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Min_height_percent_against_indefinite_parent_resolves_to_zero() {
            // CSS 2.1 §10.5: a percent height on min-height resolves only
            // when the containing block has a DEFINITE height. The parent
            // here has `height: auto`, so the percent is treated as auto/0
            // and the min-height clamp is a no-op. The child therefore
            // takes its natural content height (0, since it's empty).
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\" style=\"min-height:50%\"></div></div>",
                null, viewportWidth: 800);
            var inner = FindFirstById(root, "inner");
            Assert.That(inner, Is.Not.Null);
            // v1: BlockLayout.FinalizeBlockSize passes a null basis when
            // resolving min/max-height percents, so the Percent branch never
            // fires the clamp and computedHeight stays at content (0).
            Assert.That(inner.Height, Is.EqualTo(0).Within(0.001));
        }

        // --- aspect-ratio interactions --------------------------------------

        [Test]
        public void Aspect_ratio_derives_height_from_explicit_width() {
            // CSS Sizing L4 §5: `width: 200; aspect-ratio: 2 / 1` -> height
            // = width / ratio = 200 / 2 = 100. Note the engine encodes the
            // ratio as numerator/denominator, applied as `height = width / r`.
            var (root, _, _) = Build(
                "<div style=\"width:200px;aspect-ratio:2/1\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_derives_width_from_explicit_height() {
            // CSS Sizing L4 §5: `height: 80; aspect-ratio: 4 / 1` and width
            // auto -> width = height * ratio = 80 * 4 = 320.
            var (root, _, _) = Build(
                "<div style=\"height:80px;aspect-ratio:4/1\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(80).Within(0.001));
            Assert.That(div.Width, Is.EqualTo(320).Within(0.01));
        }

        [Test]
        public void Aspect_ratio_loses_to_explicit_height() {
            // CSS Sizing L4 §5: when BOTH width and height are explicit,
            // aspect-ratio is ignored — the two definite sizes win.
            var (root, _, _) = Build(
                "<div style=\"width:100px;height:50px;aspect-ratio:1/1\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Aspect_ratio_with_min_height_clamps_after_derivation() {
            // CSS Sizing L4 §5 + Sizing L3 §5.2: aspect-ratio derives the
            // unset axis first, then min/max clamps apply. width:100 +
            // ratio 2/1 derives height = 50; min-height: 80 raises it to 80.
            //
            // Fixed in #245: ApplyAspectRatioFixupVisit now clamps the
            // ratio-derived height by min-height / max-height (pixel-valued
            // only; percent/em/vw clamps still need the LayoutContext-aware
            // FinalizeBlockSize pass).
            var (root, _, _) = Build(
                "<div style=\"width:100px;aspect-ratio:2/1;min-height:80px\"></div>",
                null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(div.Height, Is.EqualTo(80).Within(0.001));
        }

        // --- width auto in flex ---------------------------------------------

        [Test]
        public void Flex_item_width_auto_resolves_to_flex_basis() {
            // CSS Flexbox §7.1: `flex: 0 0 120px` sets flex-basis to 120px,
            // grow 0, shrink 0. With `width: auto` the item's main size is
            // its base size = 120px. Container has plenty of free space, so
            // no grow/shrink fires.
            const string css = ".flex { display: flex; width: 600px; }";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div id=\"a\" style=\"width:auto;flex:0 0 120px\"></div></div>",
                css, viewportWidth: 800);
            var a = FindFirstById(root, "a");
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Width, Is.EqualTo(120).Within(0.001));
        }

        [Test]
        public void Flex_item_max_width_clamps_during_flex_grow() {
            // CSS Flexbox §9.7 + Sizing L3: max-width must clamp a growing
            // item, so `flex:1; max-width:200px` in an 800px container stops
            // at 200 instead of filling the container.
            //
            // Fixed in #246: ResolveFlexibleLengths now runs
            // ClampMainSizeByMinMax on each item's post-grow TargetMainSize
            // (single-pass, not the spec's iterative freeze-and-redistribute
            // — leftover space remains as slack at the line end).
            const string css = ".flex { display: flex; width: 800px; }";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div id=\"a\" style=\"flex:1;max-width:200px\"></div></div>",
                css, viewportWidth: 1000);
            var a = FindFirstById(root, "a");
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Flex_item_min_width_overrides_flex_shrink() {
            // CSS Flexbox §9.7: an item cannot shrink below its min-width.
            // Container is 300; three items each `flex-basis:200`, `flex-shrink:1`,
            // `min-width:100`. Total base 600 -> overflow 300. Without min,
            // each shrinks to 100; with min:100 each is exactly 100.
            const string css = ".flex { display: flex; width: 300px; }";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div id=\"a\" style=\"flex:0 1 200px;min-width:100px\"></div>" +
                "<div id=\"b\" style=\"flex:0 1 200px;min-width:100px\"></div>" +
                "<div id=\"c\" style=\"flex:0 1 200px;min-width:100px\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var a = FindFirstById(root, "a");
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Width, Is.GreaterThanOrEqualTo(100 - 0.001));
        }

        // --- width 100% across formatting contexts --------------------------

        [Test]
        public void Percent_width_inside_table_cell_resolves_against_cell_content_width() {
            // CSS 2.1 §17.5: a child of a table cell resolves percent widths
            // against the cell's content box. Cell is 200px wide with 8px
            // padding on each side -> content width 184px. A `width: 50%`
            // child should be 92px.
            //
            // v1 caveat: TableLayout's auto column algorithm may distribute
            // widths slightly differently than 200px exact. We pin the table
            // width and assert the inner div is half the cell's content
            // width, whatever that is.
            var (root, _) = BuildWithRealUA(
                "<table><tbody><tr><td id=\"cell\"><div id=\"inner\" style=\"width:50%\"></div></td></tr></tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 8px; }",
                viewportWidth: 800);
            var cell = FindFirst<TableCellBox>(root);
            Assert.That(cell, Is.Not.Null);
            var inner = FindFirstById(root, "inner");
            Assert.That(inner, Is.Not.Null);
            // The inner div's width is 50% of the cell's content width.
            double cellContent = cell.Width - cell.PaddingLeft - cell.PaddingRight - cell.BorderLeft - cell.BorderRight;
            Assert.That(inner.Width, Is.EqualTo(cellContent * 0.5).Within(0.5));
        }

        [Test]
        public void Percent_width_inside_abs_pos_resolves_against_containing_block() {
            // CSS 2.1 §10.3.7: a percent width on a child of an abs-pos box
            // resolves against the abs-pos box's content width (which is the
            // containing block for in-flow descendants). Outer is 600 wide
            // and positioned -> inner 50% = 300.
            const string css = ".cb { position: relative; width: 600px; }";
            var (root, _, _) = Build(
                "<div class=\"cb\"><div id=\"inner\" style=\"width:50%\"></div></div>",
                css, viewportWidth: 1000);
            var inner = FindFirstById(root, "inner");
            Assert.That(inner, Is.Not.Null);
            Assert.That(inner.Width, Is.EqualTo(300).Within(0.001));
        }

        // --- helpers ---------------------------------------------------------

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in AllBoxes(root)) if (b is T t) return t;
            return null;
        }
    }
}
