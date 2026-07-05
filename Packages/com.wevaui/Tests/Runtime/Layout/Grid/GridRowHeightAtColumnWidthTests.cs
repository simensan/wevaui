using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Regression (weva-landing .feat cards): a grid auto/content row must size
    // to the item's height when laid out at its RESOLVED column width, not at
    // the wider container width BlockLayout gave the item pre-grid. Pre-grid an
    // item with no explicit width inherits the container width, so its text
    // wraps to a single line; the row then sized to that short height and the
    // item — re-flowed to N lines in its narrow column — overflowed the row
    // (the card's body text painted past its bottom border). CSS Grid §11.5.
    public class GridRowHeightAtColumnWidthTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Auto_row_sizes_to_item_height_at_resolved_column_width() {
            // Container 400 wide; single 100px column. The item text wraps to
            // several lines at 100px but would be one line at 400px. The row —
            // and the auto-height grid — must be tall enough for the wrapped
            // content, with the item fully contained.
            const string css =
                ".g { display: grid; grid-template-columns: 100px; }" +
                ".item { font-size: 16px; }";
            const string html =
                "<div class='g'><div class='item'>alpha beta gamma delta epsilon zeta eta theta</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            var item = FirstWithClass(root, "item");
            var grid = FirstWithClass(root, "g");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.Width, Is.EqualTo(100).Within(0.5), "item fills the 100px column");
            // The text must have wrapped to multiple lines at 100px — well more
            // than a single ~19px line.
            Assert.That(item.Height, Is.GreaterThan(40),
                $"item should wrap to several lines at the 100px column width, got H={item.Height:F1}");
            // The grid (auto height) must contain the item — no overflow.
            Assert.That(grid.Height, Is.GreaterThanOrEqualTo(item.Height - 0.5),
                $"auto-height grid must fit its item (row sized at column width); grid H={grid.Height:F1} item H={item.Height:F1}");
        }

        [Test]
        public void Tallest_item_drives_shared_row_height_at_column_width() {
            // Two columns; the left item wraps to several lines at 90px while
            // the right is a single word. The shared auto-row height must come
            // from the TALLER item's height AT THE COLUMN WIDTH (multi-line),
            // not from the single-line height the item had at the wide
            // container pre-grid. With align-items:stretch (default) both cells
            // stretch to that row height, so they end equal — the regression
            // signal is that the row is multi-line tall, not ~1 line.
            const string css =
                ".g { display: grid; grid-template-columns: 90px 90px; }" +
                ".item { font-size: 16px; }";
            const string html =
                "<div class='g'>" +
                "<div class='item a'>alpha beta gamma delta epsilon zeta</div>" +
                "<div class='item b'>short</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            var grid = FirstWithClass(root, "g");
            var a = FirstWithClass(root, "a");
            var b = FirstWithClass(root, "b");
            // Row is sized for the multi-line left item (≥3 lines at ~19px),
            // not the single-line height it would have at the 400px container.
            Assert.That(grid.Height, Is.GreaterThan(50),
                $"row must size to the tall item's column-width height, got grid H={grid.Height:F1}");
            // Both stretched cells equal the row height — neither overflows.
            Assert.That(a.Height, Is.EqualTo(b.Height).Within(0.5),
                "align-items:stretch makes both cells share the row height");
        }
    }
}
