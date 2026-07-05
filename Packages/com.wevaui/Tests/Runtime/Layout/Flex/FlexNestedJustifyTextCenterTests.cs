using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (flex-playground): a flex ITEM that is itself a flex CONTAINER
    // with `justify-content: center` and a single text child. The inner flex
    // must center its text within ITS OWN resolved (content) width, not the
    // outer container's available width. The bug centered the text against the
    // grandparent flex row's width, flinging single-character labels hundreds of
    // px to the right (visible as stray "a"/"b"/"1" glyphs at the viewport edge).
    public class FlexNestedJustifyTextCenterTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        static TextRun TextUnder(Box b) => AllBoxes(b).OfType<TextRun>().FirstOrDefault();

        static double AbsX(Box b) { double x = 0; for (var p = b; p != null; p = p.Parent) x += p.X; return x; }

        [Test]
        public void Inner_flex_centers_text_in_its_own_box_not_the_outer_row() {
            const string css = @"
                .row  { display: flex; width: 600px; gap: 8px; align-items: center; }
                .cell { display: flex; align-items: center; justify-content: center;
                        min-width: 38px; height: 38px; }";
            const string html =
                @"<div class='row'><span class='cell'>a</span><span class='cell'>b</span><span class='cell'>c</span></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var cell = FirstWithClass(root, "cell");
            Assert.That(cell, Is.Not.Null);
            double cellX = AbsX(cell);
            var txt = TextUnder(cell);
            Assert.That(txt, Is.Not.Null, "cell must contain a text run");
            double txtX = AbsX(txt);

            double expected = cellX + (cell.Width - txt.Width) * 0.5;
            Assert.That(txtX, Is.EqualTo(expected).Within(1.5),
                $"text should center in its cell (cellX={cellX:F1} cellW={cell.Width:F1} -> {expected:F1}), got {txtX:F1}");
            Assert.That(txtX, Is.LessThanOrEqualTo(cellX + cell.Width + 0.5),
                $"text must not escape past the cell's right edge");
        }

        // The live flex-playground reproduces it only through the full nesting:
        // a wrapping grid → a flex:1 1 220px column card → a flex row "demo" →
        // flex-center cells. The card grows wide, the demo inherits that width,
        // and the cell's text gets centered against the demo's wide content box
        // instead of the cell's own 38px box.
        [Test]
        public void Inner_flex_text_centers_in_cell_through_grid_card_demo_chain() {
            const string css = @"
                .grid { display: flex; flex-wrap: wrap; gap: 14px; align-content: flex-start; }
                .card { flex: 1 1 220px; display: flex; flex-direction: column; gap: 10px; padding: 14px; }
                .demo { display: flex; gap: 8px; padding: 10px; min-height: 56px; }
                .demo i { display: flex; align-items: center; justify-content: center;
                          min-width: 38px; padding: 0 10px; height: 38px; font-style: normal; }";
            // SIX cards in a 1000px wrapping grid → each card shrinks to its
            // ~220px basis (NOT the full grid width). The demo inside each card
            // must size to the card's content width, not the grid's available
            // width — that's the bug: the card (a flex item resolved to ~220)
            // laid out its demo child at the pre-flex available width (~1000).
            const string html =
                @"<section class='grid'>" +
                @"<div class='card'><div class='ch'>c1</div><div class='demo'><i class='s'>a</i><i class='m'>b</i></div></div>" +
                @"<div class='card'><div class='ch'>c2</div><div class='demo'><i>x</i><i>y</i></div></div>" +
                @"<div class='card'><div class='ch'>c3</div><div class='demo'><i>x</i><i>y</i></div></div>" +
                @"<div class='card'><div class='ch'>c4</div><div class='demo'><i>x</i><i>y</i></div></div>" +
                @"<div class='card'><div class='ch'>c5</div><div class='demo'><i>x</i><i>y</i></div></div>" +
                @"<div class='card'><div class='ch'>c6</div><div class='demo'><i>x</i><i>y</i></div></div>" +
                @"</section>";
            var (root, _, _) = Build(html, css, viewportWidth: 1100);

            var demo = FirstWithClass(root, "demo");
            var card = FirstWithClass(root, "card");
            Assert.That(demo, Is.Not.Null);
            Assert.That(card, Is.Not.Null);
            // The demo must be constrained to its card, not ballooned to the grid.
            Assert.That(demo.Width, Is.LessThanOrEqualTo(card.Width + 0.5),
                $"demo ({demo.Width:F1}) must fit inside its card ({card.Width:F1}), not balloon to the grid width");

            var cell = FirstWithClass(root, "s");
            Assert.That(cell, Is.Not.Null);
            double cellX = AbsX(cell);
            var txt = TextUnder(cell);
            Assert.That(txt, Is.Not.Null, "cell must contain a text run");
            double txtX = AbsX(txt);

            // The text must sit INSIDE its own cell box, centered.
            double expected = cellX + (cell.Width - txt.Width) * 0.5;
            Assert.That(txtX, Is.EqualTo(expected).Within(1.5),
                $"text should center in its cell (cellX={cellX:F1} cellW={cell.Width:F1} -> {expected:F1}), got {txtX:F1}");
            Assert.That(txtX, Is.LessThanOrEqualTo(cellX + cell.Width + 0.5),
                $"text must not escape past the cell's right edge (cell right={cellX + cell.Width:F1}, txtX={txtX:F1})");
        }
    }
}
