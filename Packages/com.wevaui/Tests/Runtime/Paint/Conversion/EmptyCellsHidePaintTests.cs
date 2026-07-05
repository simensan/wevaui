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

namespace Weva.Tests.Paint.Conversion {
    // CSS 2.1 §17.6.1.1 / CSS Tables L3 §11: in the separate-borders model,
    // `empty-cells: hide` must suppress the borders and backgrounds of a
    // cell that has no in-flow content. The collapsed-borders model
    // ignores the property. empty-cells is inherited so authors typically
    // set it on the <table>.
    public class EmptyCellsHidePaintTests {
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
            foreach (var c in root.Children) {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        static int CountFillsWithColor(IList<PaintCommand> cmds, char channel) {
            int n = 0;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor) {
                    var c = fr.Brush.Color;
                    switch (channel) {
                        case 'R': if (c.R > 0.5f && c.G < 0.3f && c.B < 0.3f) n++; break;
                        case 'G': if (c.G > 0.5f && c.R < 0.3f && c.B < 0.3f) n++; break;
                        case 'B': if (c.B > 0.5f && c.R < 0.3f && c.G < 0.3f) n++; break;
                    }
                }
            }
            return n;
        }

        static int CountBorderStrokesAtX(IList<PaintCommand> cmds, double minX, double maxX) {
            int n = 0;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is StrokeBorderCommand sb) {
                    if (sb.Bounds.X >= minX && sb.Bounds.X <= maxX) n++;
                }
            }
            return n;
        }

        static double AbsoluteX(Box box) {
            double x = 0;
            for (Box b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        static TableCellBox FindCell(Box root, string id) {
            foreach (var b in Walk(root)) {
                if (b is TableCellBox cell && cell.Element?.GetAttribute("id") == id) return cell;
            }
            return null;
        }

        [Test]
        public void Hide_suppresses_empty_cell_background_and_border_in_separate_mode() {
            // Empty <td id="e"> next to populated <td id="f">. Each cell has
            // a unique solid background and a thick border. With
            // empty-cells:hide and border-collapse:separate, the empty cell
            // must NOT emit either; the filled cell must emit both.
            const string html =
                "<table><tbody><tr>" +
                "<td id=\"e\" class=\"empty\"></td>" +
                "<td id=\"f\" class=\"filled\">X</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table { border-collapse: separate; empty-cells: hide; border-spacing: 0; width: 200px; }
                td { width: 100px; height: 40px; border: 4px solid black; }
                td.empty  { background-color: rgb(255, 0, 0); }
                td.filled { background-color: rgb(0, 255, 0); }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            var emptyCell = FindCell(root, "e");
            var filledCell = FindCell(root, "f");
            Assert.That(emptyCell, Is.Not.Null);
            Assert.That(filledCell, Is.Not.Null);

            int redFills = CountFillsWithColor(cmds, 'R');
            int greenFills = CountFillsWithColor(cmds, 'G');
            Assert.That(redFills, Is.EqualTo(0),
                "Empty cell's red background must be suppressed when empty-cells:hide.");
            Assert.That(greenFills, Is.GreaterThanOrEqualTo(1),
                "Populated cell's green background must still paint.");

            // The empty cell's border sits at the first column's absolute X;
            // the populated cell's at the second column's absolute X. Split
            // the X axis between them so each range catches exactly one
            // column regardless of the UA's body margin.
            double emptyAbsX = AbsoluteX(emptyCell);
            double filledAbsX = AbsoluteX(filledCell);
            double splitX = (emptyAbsX + filledAbsX) * 0.5;
            int emptyColBorders = CountBorderStrokesAtX(cmds, double.MinValue, splitX);
            int filledColBorders = CountBorderStrokesAtX(cmds, splitX, double.MaxValue);
            Assert.That(emptyColBorders, Is.EqualTo(0),
                "Empty cell's border must be suppressed when empty-cells:hide.");
            Assert.That(filledColBorders, Is.GreaterThanOrEqualTo(1),
                "Populated cell's border must still paint.");
        }

        [Test]
        public void Show_default_still_paints_both_cell_decorations() {
            // Regression guard: with `empty-cells: show` (the default), the
            // empty cell's decorations must paint exactly like the
            // populated cell's.
            const string html =
                "<table><tbody><tr>" +
                "<td id=\"e\" class=\"empty\"></td>" +
                "<td id=\"f\" class=\"filled\">X</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table { border-collapse: separate; empty-cells: show; border-spacing: 0; width: 200px; }
                td { width: 100px; height: 40px; border: 4px solid black; }
                td.empty  { background-color: rgb(255, 0, 0); }
                td.filled { background-color: rgb(0, 255, 0); }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            var emptyCell = FindCell(root, "e");
            var filledCell = FindCell(root, "f");
            int redFills = CountFillsWithColor(cmds, 'R');
            int greenFills = CountFillsWithColor(cmds, 'G');
            Assert.That(redFills, Is.GreaterThanOrEqualTo(1),
                "empty-cells:show must keep the empty cell's background.");
            Assert.That(greenFills, Is.GreaterThanOrEqualTo(1),
                "empty-cells:show must keep the populated cell's background.");

            double splitX = (AbsoluteX(emptyCell) + AbsoluteX(filledCell)) * 0.5;
            int emptyColBorders = CountBorderStrokesAtX(cmds, double.MinValue, splitX);
            int filledColBorders = CountBorderStrokesAtX(cmds, splitX, double.MaxValue);
            Assert.That(emptyColBorders, Is.GreaterThanOrEqualTo(1),
                "empty-cells:show must keep the empty cell's border.");
            Assert.That(filledColBorders, Is.GreaterThanOrEqualTo(1),
                "empty-cells:show must keep the populated cell's border.");
        }

        [Test]
        public void Collapse_mode_ignores_empty_cells_hide() {
            // CSS 2.1 §17.6.1.1: empty-cells only applies to the
            // separate-borders model. With border-collapse:collapse, the
            // engine must NOT suppress the empty cell's background.
            const string html =
                "<table><tbody><tr>" +
                "<td id=\"e\" class=\"empty\"></td>" +
                "<td id=\"f\" class=\"filled\">X</td>" +
                "</tr></tbody></table>";
            const string css = @"
                table { border-collapse: collapse; empty-cells: hide; width: 200px; }
                td { width: 100px; height: 40px; }
                td.empty  { background-color: rgb(255, 0, 0); }
                td.filled { background-color: rgb(0, 255, 0); }
            ";
            var (root, _) = BuildWithRealUA(html, css);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int redFills = CountFillsWithColor(cmds, 'R');
            int greenFills = CountFillsWithColor(cmds, 'G');
            Assert.That(redFills, Is.GreaterThanOrEqualTo(1),
                "border-collapse:collapse must ignore empty-cells:hide — empty cell background must still paint.");
            Assert.That(greenFills, Is.GreaterThanOrEqualTo(1),
                "Populated cell background must paint regardless of mode.");
        }
    }
}
