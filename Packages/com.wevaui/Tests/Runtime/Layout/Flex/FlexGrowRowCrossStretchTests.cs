using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (flex-playground nested footer): a ROW-flex container whose
    // HEIGHT comes from flex-grow (its cross axis, set by a column parent's
    // main-axis distribution) must use that grown height as its single-line
    // cross size so align-items:stretch children fill it. The engine computed
    // the line cross size from item content (0 for empty bars), collapsing
    // stretched children to 0. An EXPLICIT height already works; only the
    // flex-grow-derived height was missed (FLEX-GROW-ROW-CROSS-STRETCH).
    public class FlexGrowRowCrossStretchTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        // Control: an EXPLICIT-height row stretches its children — must keep working.
        [Test]
        public void Explicit_height_row_stretches_children() {
            const string css = @"
                .row { display: flex; align-items: stretch; height: 120px; width: 200px; }
                .bar { flex: 0 0 30px; }";
            const string html = @"<div class='row'><div class='bar'></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            var bar = FirstWithClass(root, "bar");
            Assert.That(bar, Is.Not.Null);
            Assert.That(bar.Height, Is.EqualTo(120).Within(0.5),
                $"child should stretch to the explicit row height (120), got {bar.Height:F1}");
        }

        [Test]
        public void Flex_grow_height_row_stretches_children() {
            const string css = @"
                .col { display: flex; flex-direction: column; height: 200px; width: 200px; }
                .top { height: 20px; }
                .row { display: flex; flex: 1 1 auto; align-items: stretch; }
                .bar { flex: 0 0 30px; }";
            const string html =
                @"<div class='col'><div class='top'></div><div class='row'><div class='bar'></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            var row = FirstWithClass(root, "row");
            var bar = FirstWithClass(root, "bar");
            Assert.That(row, Is.Not.Null);
            Assert.That(bar, Is.Not.Null);
            // .row grows to fill the column: 200 - 20 (top) = 180.
            Assert.That(row.Height, Is.EqualTo(180).Within(0.5), $"row grows to fill column, got {row.Height:F1}");
            // align-items:stretch → the child fills the grown row height.
            Assert.That(bar.Height, Is.EqualTo(180).Within(0.5),
                $"child should stretch to the flex-grown row height (180), not collapse to content (0) — got {bar.Height:F1}");
        }
    }
}
