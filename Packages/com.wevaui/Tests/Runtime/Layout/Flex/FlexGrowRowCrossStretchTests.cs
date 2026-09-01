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
        // CSS Flexbox L1 §9.4: a flex line's cross size is the largest item's
        // outer hypothetical cross size, and only a DEFINITE container cross
        // size replaces it. A `min-height` on an otherwise auto-height row
        // container is a floor, not a definite size — it must let the line
        // grow past it, never cap it.
        //
        // The engine treated the min-floored cross as definite and capped each
        // item at `container.ContentHeight`, which on that pass still held the
        // pre-flex BlockLayout value. That pass stacks a column-flex child's
        // children WITHOUT their row gaps, so the child was capped back to the
        // gap-less sum: its children stayed correctly spaced but its own box
        // (and background) stopped short by one gap per child. Found by the
        // godot-port oracle on form-demo, where a 14-gap card lost 308px.
        [Test]
        public void Min_height_row_container_does_not_cap_a_gapped_column_child() {
            var (root, _, _) = Build(
                "<div class=\"page\"><div class=\"card\">"
                + "<div class=\"b\"></div><div class=\"b\"></div><div class=\"b\"></div>"
                + "</div></div>",
                // The content (340) is TALLER than the floor (200), so stretch
                // has nothing to add and the card must land on its own height.
                ".page { display: flex; min-height: 200px; }"
                + ".card { display: flex; flex-direction: column; gap: 20px; }"
                + ".b { height: 100px; }",
                viewportWidth: 800);

            var card = FirstWithClass(root, "card");
            Assert.That(card, Is.Not.Null);
            // 3 x 100 content + 2 x 20 gap. The gaps are real space between the
            // children, so the parent that holds them must include them.
            Assert.That(card.Height, Is.EqualTo(340).Within(0.001));

            var bars = AllBoxes(root)
                .Where(b => b.Element != null
                    && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains("b"))
                .ToList();
            Assert.That(bars.Count, Is.EqualTo(3));
            Assert.That(bars[2].Y - bars[0].Y, Is.EqualTo(240).Within(0.001),
                "children were always spaced with the gaps; only the parent lost them");
        }

        // The floor half of the same rule: when the items are SHORTER than the
        // min-height, the line still fills it, so align-items has room.
        [Test]
        public void Min_height_row_container_still_floors_a_short_line() {
            var (root, _, _) = Build(
                "<div class=\"page\"><div class=\"card\"></div></div>",
                ".page { display: flex; min-height: 400px; align-items: stretch; }"
                + ".card { display: flex; flex-direction: column; }",
                viewportWidth: 800);
            var card = FirstWithClass(root, "card");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Height, Is.EqualTo(400).Within(0.001),
                "a stretched item must fill the min-floored cross");
        }

    }
}
