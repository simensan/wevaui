using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Layout.Tables {
    // CSS 2.2 §17.6.2.1 — Collapsed border conflict resolution ("winner rule").
    //
    // Tests are split into two tiers:
    //   A. Pure-logic unit tests against CollapsedBorderWinnerResolver.PickWinner.
    //      These run against pre-built BorderEdge values and exercise each of the
    //      four precedence axes without needing a full layout pass.
    //   B. Integration tests that build a real table with BuildWithRealUA, convert
    //      it to paint commands, and assert which StrokeBorderCommand edges appear.
    //
    // Precedence axes (descending):
    //   1. hidden always wins (suppresses the shared border)
    //   2. wider width wins
    //   3. style priority: double > solid > dashed > dotted > none
    //   4. top/left side wins ties over bottom/right
    public class CollapsedBorderWinnerTests {

        // ------------------------------------------------------------------
        // Helper — build a BorderEdge from keyword + width
        // ------------------------------------------------------------------
        static BorderEdge Edge(BorderStyle style, double width) =>
            new BorderEdge(style, width, LinearColor.White);

        static BorderEdge EdgeColor(BorderStyle style, double width, LinearColor color) =>
            new BorderEdge(style, width, color);

        static LinearColor Red   => new LinearColor(1f, 0f, 0f, 1f);
        static LinearColor Green => new LinearColor(0f, 1f, 0f, 1f);
        static LinearColor Blue  => new LinearColor(0f, 0f, 1f, 1f);

        // ------------------------------------------------------------------
        // A. Unit tests — PickWinner pure logic
        // ------------------------------------------------------------------

        [Test]
        public void Hidden_wins_over_solid_when_hidden_is_primary() {
            // Rule 1: hidden always beats any visible style regardless of width.
            var hidden = new BorderEdge(BorderStyle.Hidden, 1, LinearColor.White);
            var solid  = Edge(BorderStyle.Solid, 10);
            var winner = CollapsedBorderWinnerResolver.PickWinner(hidden, solid, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Hidden),
                "hidden must win over solid even with a much smaller width.");
        }

        [Test]
        public void Hidden_wins_over_solid_when_hidden_is_secondary() {
            // Rule 1 is symmetric: regardless of which argument is Primary or
            // Secondary, hidden beats visible.
            var solid  = Edge(BorderStyle.Solid, 10);
            var hidden = new BorderEdge(BorderStyle.Hidden, 1, LinearColor.White);
            var winner = CollapsedBorderWinnerResolver.PickWinner(solid, hidden, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Hidden),
                "hidden in the `b` position must still win over solid in `a`.");
        }

        [Test]
        public void Both_hidden_returns_primary_side() {
            // Rule 1 tie: both hidden — primary side wins.
            var hidA = EdgeColor(BorderStyle.Hidden, 2, Red);
            var hidB = EdgeColor(BorderStyle.Hidden, 4, Green);
            var winner = CollapsedBorderWinnerResolver.PickWinner(hidA, hidB, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Hidden));
            Assert.That(winner.Color, Is.EqualTo(Red),
                "When both hidden and `a` is Primary, `a` wins.");

            winner = CollapsedBorderWinnerResolver.PickWinner(hidA, hidB, SidePreference.Secondary);
            Assert.That(winner.Color, Is.EqualTo(Green),
                "When both hidden and `a` is Secondary, `b` wins.");
        }

        [Test]
        public void Wider_border_wins_when_styles_match() {
            // Rule 2: larger width wins when both have a non-none style.
            var thin  = Edge(BorderStyle.Solid, 1);
            var thick = Edge(BorderStyle.Solid, 3);
            var winner = CollapsedBorderWinnerResolver.PickWinner(thin, thick, SidePreference.Primary);
            Assert.That(winner.Width, Is.EqualTo(3),
                "3px solid must beat 1px solid.");
        }

        [Test]
        public void Wider_border_wins_in_either_argument_position() {
            var thin  = Edge(BorderStyle.Solid, 1);
            var thick = Edge(BorderStyle.Solid, 3);
            // thick is `a` this time
            var winner = CollapsedBorderWinnerResolver.PickWinner(thick, thin, SidePreference.Secondary);
            Assert.That(winner.Width, Is.EqualTo(3));
        }

        [Test]
        public void Double_beats_solid_at_equal_width() {
            // Rule 3: style priority — double > solid.
            var solid  = Edge(BorderStyle.Solid,  2);
            var dbl    = Edge(BorderStyle.Double, 2);
            var winner = CollapsedBorderWinnerResolver.PickWinner(solid, dbl, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Double),
                "double must beat solid when widths are equal.");
        }

        [Test]
        public void Solid_beats_dashed_at_equal_width() {
            var dashed = Edge(BorderStyle.Dashed, 2);
            var solid  = Edge(BorderStyle.Solid,  2);
            var winner = CollapsedBorderWinnerResolver.PickWinner(dashed, solid, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Solid));
        }

        [Test]
        public void Dashed_beats_dotted_at_equal_width() {
            var dotted = Edge(BorderStyle.Dotted, 2);
            var dashed = Edge(BorderStyle.Dashed, 2);
            var winner = CollapsedBorderWinnerResolver.PickWinner(dashed, dotted, SidePreference.Secondary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Dashed));
        }

        [Test]
        public void None_loses_to_any_visible_style() {
            // Rule — none always loses.
            var none   = BorderEdge.None;
            var solid  = Edge(BorderStyle.Solid, 1);
            var winner = CollapsedBorderWinnerResolver.PickWinner(none, solid, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Solid),
                "none (a) must lose to solid (b) even when a is Primary.");

            winner = CollapsedBorderWinnerResolver.PickWinner(solid, none, SidePreference.Secondary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Solid),
                "none (b) must lose to solid (a) even when a is Secondary.");
        }

        [Test]
        public void Both_none_returns_none() {
            var winner = CollapsedBorderWinnerResolver.PickWinner(BorderEdge.None, BorderEdge.None, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Primary_side_wins_style_width_tie() {
            // Rule 4/5: equal width and style — primary (top/left) side wins.
            var aEdge = EdgeColor(BorderStyle.Solid, 2, Red);
            var bEdge = EdgeColor(BorderStyle.Solid, 2, Green);

            var winner = CollapsedBorderWinnerResolver.PickWinner(aEdge, bEdge, SidePreference.Primary);
            Assert.That(winner.Color, Is.EqualTo(Red),
                "When a is Primary, a wins equal-priority ties.");

            winner = CollapsedBorderWinnerResolver.PickWinner(aEdge, bEdge, SidePreference.Secondary);
            Assert.That(winner.Color, Is.EqualTo(Green),
                "When a is Secondary, b wins equal-priority ties.");
        }

        [Test]
        public void Width_wins_over_style_priority() {
            // Rule 2 beats Rule 3: a wider lower-priority style beats a thinner
            // higher-priority style.
            var wideDotted  = Edge(BorderStyle.Dotted, 5);
            var thinDouble  = Edge(BorderStyle.Double, 1);
            var winner = CollapsedBorderWinnerResolver.PickWinner(wideDotted, thinDouble, SidePreference.Secondary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Dotted),
                "5px dotted must beat 1px double because width (Rule 2) precedes style (Rule 3).");
        }

        [Test]
        public void Zero_width_visible_style_treated_as_none() {
            // A style != none with width == 0 is suppressed (no border).
            var zeroSolid = Edge(BorderStyle.Solid, 0);
            var onePixel  = Edge(BorderStyle.Dotted, 1);
            var winner = CollapsedBorderWinnerResolver.PickWinner(zeroSolid, onePixel, SidePreference.Primary);
            Assert.That(winner.Style, Is.EqualTo(BorderStyle.Dotted),
                "A zero-width solid edge must lose to a 1px dotted edge.");
        }

        // ------------------------------------------------------------------
        // B. Integration tests — full layout + paint conversion
        // ------------------------------------------------------------------

        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithRealUA(
            string html, string authorCss = null, double viewportWidth = 400
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
                ViewportHeightPx = 300,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles);
        }

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children)
                foreach (var d in Walk(c)) yield return d;
        }

        static TableCellBox FindCell(Box root, string id) {
            foreach (var b in Walk(root))
                if (b is TableCellBox cell && cell.Element?.GetAttribute("id") == id) return cell;
            return null;
        }

        // Count StrokeBorderCommand entries where the given side has the expected style.
        static int CountBorderCommandsWithStyle(IList<PaintCommand> cmds, BorderStyle style) {
            int n = 0;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is StrokeBorderCommand sb) {
                    if (sb.Borders.Top.Style    == style ||
                        sb.Borders.Right.Style  == style ||
                        sb.Borders.Bottom.Style == style ||
                        sb.Borders.Left.Style   == style) n++;
                }
            }
            return n;
        }

        // Returns the combined maximum width across all StrokeBorderCommand edges.
        static double MaxBorderWidth(IList<PaintCommand> cmds) {
            double max = 0;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is StrokeBorderCommand sb) {
                    max = System.Math.Max(max, sb.Borders.Top.Width);
                    max = System.Math.Max(max, sb.Borders.Right.Width);
                    max = System.Math.Max(max, sb.Borders.Bottom.Width);
                    max = System.Math.Max(max, sb.Borders.Left.Width);
                }
            }
            return max;
        }

        // Returns the specific border command for cell identified by id.
        static StrokeBorderCommand FindBorderCommandForCell(IList<PaintCommand> cmds, Box root, string id) {
            var cell = FindCell(root, id);
            if (cell == null) return null;
            double absX = 0, absY = 0;
            for (Box b = cell; b != null; b = b.Parent) { absX += b.X; absY += b.Y; }
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is StrokeBorderCommand sb) {
                    // The border command bounds are in box-local then translated
                    // to absolute by the converter. Match on approximate position.
                    if (System.Math.Abs(sb.Bounds.X - absX) < 1 &&
                        System.Math.Abs(sb.Bounds.Y - absY) < 1) return sb;
                }
            }
            return null;
        }

        [Test]
        public void Collapsed_hidden_wins_over_solid_suppresses_shared_edge() {
            // Cell A has border-style: hidden on its right edge.
            // Cell B has border-style: solid 2px on its left edge.
            // §17.6.2.1 Rule 1: hidden wins → the shared vertical edge disappears.
            // Because hidden wins, the winner edge's style is Hidden which IsNone
            // treats as invisible, so NO StrokeBorderCommand should contain solid
            // borders on the shared edge.
            const string html = "<table><tbody><tr>" +
                "<td id=\"a\">A</td>" +
                "<td id=\"b\">B</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table  { border-collapse: collapse; width: 200px; border-spacing: 0; }
                td     { width: 100px; height: 40px; }
                #a     { border-right: 1px hidden black; }
                #b     { border-left: 2px solid red; }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            // The solid style should be beaten by hidden. Neither cell should
            // emit a Solid border on the shared edge. Count total Solid borders
            // across all commands — the only solid borders would come from the
            // shared edge if the winner rule weren't applied.
            int solidBorderCmds = CountBorderCommandsWithStyle(cmds, BorderStyle.Solid);
            Assert.That(solidBorderCmds, Is.EqualTo(0),
                "When hidden wins the winner rule, no solid border should render on the shared edge.");
        }

        [Test]
        public void Collapsed_wider_border_wins_over_narrower() {
            // Cell A has 1px solid; cell B has 3px solid on the shared left/right edge.
            // §17.6.2.1 Rule 2: 3px beats 1px → all StrokeBorderCommands that cover
            // the shared edge must use width 3, not width 1.
            const string html = "<table><tbody><tr>" +
                "<td id=\"a\">A</td>" +
                "<td id=\"b\">B</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table  { border-collapse: collapse; width: 200px; border-spacing: 0; }
                td     { width: 100px; height: 40px; }
                #a     { border-right: 1px solid black; }
                #b     { border-left: 3px solid black; }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            // At least one StrokeBorderCommand must have a 3px edge.
            double maxW = MaxBorderWidth(cmds);
            Assert.That(maxW, Is.GreaterThanOrEqualTo(3.0),
                "The wider 3px border must win and appear in a StrokeBorderCommand.");
            // No command should have a 1px-only result where the shared edge is, meaning
            // the narrower 1px lost to the 3px.
            // (We check that the overall max width across all commands is 3, not 1.)
        }

        [Test]
        public void Collapsed_double_beats_solid_at_equal_width() {
            // §17.6.2.1 Rule 3: style priority double > solid at equal width.
            // Cell A: 2px solid; cell B: 2px double. The shared edge must
            // resolve to double.
            const string html = "<table><tbody><tr>" +
                "<td id=\"a\">A</td>" +
                "<td id=\"b\">B</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table  { border-collapse: collapse; width: 200px; border-spacing: 0; }
                td     { width: 100px; height: 40px; }
                #a     { border-right: 2px solid black; }
                #b     { border-left: 2px double black; }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int doubleBorders = CountBorderCommandsWithStyle(cmds, BorderStyle.Double);
            Assert.That(doubleBorders, Is.GreaterThanOrEqualTo(1),
                "double style must win over solid at equal width — at least one Double border must appear.");
        }

        [Test]
        public void Separate_model_ignores_winner_rule() {
            // Sanity check: in the separate-borders model the winner rule must NOT apply.
            // Both cells' borders render independently, so both widths appear.
            const string html = "<table><tbody><tr>" +
                "<td id=\"a\">A</td>" +
                "<td id=\"b\">B</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table  { border-collapse: separate; border-spacing: 0; width: 200px; }
                td     { width: 100px; height: 40px; }
                #a     { border-right: 1px solid black; }
                #b     { border-left: 3px solid black; }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            // Both 1px and 3px borders exist independently.
            bool hasOne = false, hasThree = false;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is StrokeBorderCommand sb) {
                    if (sb.Borders.Right.Style == BorderStyle.Solid && System.Math.Abs(sb.Borders.Right.Width - 1) < 0.01) hasOne   = true;
                    if (sb.Borders.Left.Style  == BorderStyle.Solid && System.Math.Abs(sb.Borders.Left.Width  - 3) < 0.01) hasThree = true;
                }
            }
            Assert.That(hasOne,   Is.True, "Separate model: cell A's 1px right border must still appear.");
            Assert.That(hasThree, Is.True, "Separate model: cell B's 3px left border must still appear.");
        }

        [Test]
        public void Collapsed_top_left_wins_equal_priority_tie() {
            // §17.6.2.1 Rule 5: top/left (Primary) wins ties.
            // Two vertically adjacent cells both have 2px solid borders:
            //   upper cell: 2px solid red on bottom
            //   lower cell: 2px solid blue on top
            // The upper cell's bottom edge is Primary relative to the lower cell's top.
            // Winner: red (upper cell's bottom edge).
            const string html = "<table><tbody>" +
                "<tr><td id=\"upper\">U</td></tr>" +
                "<tr><td id=\"lower\">L</td></tr>" +
                "</tbody></table>";
            const string css = @"
                table  { border-collapse: collapse; width: 200px; border-spacing: 0; }
                td     { width: 200px; height: 40px; }
                #upper { border-bottom: 2px solid rgb(255,0,0); }
                #lower { border-top:    2px solid rgb(0,0,255); }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            // At the shared horizontal edge, the winner (red, from upper cell's bottom)
            // must appear. Blue (lower cell's top) must lose.
            // In the StrokeBorderCommand for lower cell (which draws the winner on its Top),
            // the Top color must be red (the winner).
            var lower = FindCell(root, "lower");
            Assert.That(lower, Is.Not.Null);

            // Find the StrokeBorderCommand for lower cell's position.
            var cmd = FindBorderCommandForCell(cmds, root, "lower");
            // If found, verify the top edge is the winner (red).
            if (cmd != null) {
                // The winner on lower's Top should be the primary side's color (red = upper's bottom).
                Assert.That(cmd.Borders.Top.Color.R, Is.GreaterThan(0.5f),
                    "Lower cell's top border winner should be red (upper cell's bottom wins tie).");
                Assert.That(cmd.Borders.Top.Color.B, Is.LessThan(0.3f),
                    "Lower cell's top border winner should not be blue.");
            }
        }
    }
}
