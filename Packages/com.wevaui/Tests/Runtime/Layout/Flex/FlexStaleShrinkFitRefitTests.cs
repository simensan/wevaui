using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (inputtest tile grid): the column-flex fit-content shrink in
    // ComputeLineCrossSize was ONE-WAY. An early/nested layout pass fit the
    // anonymous text item against a transiently-narrow container and cached
    // that width; once the tile reached its real ~243px track width the
    // over-stretch tell (`width >= container width`) never fired again and
    // the text stayed at the stale one-glyph fit, painting ~26px right of
    // center ("Crafting" under its icon). fit-content is a function of the
    // available size (CSS Sizing 3 §5.1): when avail changes for an item this
    // path previously fit, it must re-fit — growing too.
    public class FlexStaleShrinkFitRefitTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        static void AssertTileTextCentred(Box root, string id) {
            var tile = AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && b.Element.GetAttribute("id") == id);
            Assert.That(tile, Is.Not.Null, $"#{id} exists");
            var anon = tile.Children.OfType<BlockBox>().FirstOrDefault(ch => ch.Element == null);
            Assert.That(anon, Is.Not.Null, "anonymous text item exists");

            // The line inside must FIT the item — the stale fit's symptom was
            // a line wider than its box (text spilling right of centre).
            var line = anon.Children.FirstOrDefault(c => c is LineBox);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.LessThanOrEqualTo(anon.Width + 1.0),
                $"text line ({line.Width:F1}) must not overflow the anon item ({anon.Width:F1})");

            // align-items:center geometry: the anon item's centre must be the
            // tile's content centre.
            double contentLeft = tile.PaddingLeft + tile.BorderLeft;
            double contentW = tile.Width - tile.PaddingLeft - tile.PaddingRight - tile.BorderLeft - tile.BorderRight;
            double expectedX = contentLeft + (contentW - anon.Width) * 0.5;
            Assert.That(anon.X, Is.EqualTo(expectedX).Within(1.5),
                $"anon item centred: X={anon.X:F1} expected≈{expectedX:F1} (W={anon.Width:F1} in {tile.Width:F1} tile)");
        }

        // The real page shape: grid of tiles INSIDE an outer column flex
        // (.screen). This is the configuration that produced the stale fit.
        const string ScreenCss =
            ".screen { width: 100%; min-height: 100vh; box-sizing: border-box; padding: 32px 40px; " +
            "          display: flex; flex-direction: column; gap: 26px; font-size: 16px; }" +
            ".toolbar { display: flex; gap: 12px; flex-wrap: wrap; }" +
            ".btn { padding: 12px 22px; border: 1px solid #444; }" +
            ".grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; max-width: 760px; }" +
            ".tile { display: flex; flex-direction: column; align-items: center; gap: 12px; " +
            "        padding: 26px 12px; font-size: 16px; border: 1px solid #444; }" +
            ".t-ic { width: 44px; height: 44px; }";

        const string ScreenHtml =
            "<main class='screen'>" +
            "<section class='toolbar'>" +
            "<button class='btn'>Profile</button><button class='btn'>Inventory</button>" +
            "</section>" +
            "<section class='grid'>" +
            "<button class='tile' id='t1'><span class='t-ic'></span>Quests</button>" +
            "<button class='tile' id='t2'><span class='t-ic'></span>Skills</button>" +
            "<button class='tile' id='t3'><span class='t-ic'></span>Crafting</button>" +
            "<button class='tile' id='t4'><span class='t-ic'></span>Party</button>" +
            "<button class='tile' id='t5'><span class='t-ic'></span>Store</button>" +
            "<button class='tile' id='t6'><span class='t-ic'></span>Mail</button>" +
            "</section>" +
            "</main>";

        [Test]
        public void Tile_text_centres_inside_column_flex_screen_wrapper() {
            var (root, _, _) = Build(ScreenHtml, ScreenCss, viewportWidth: 1434, viewportHeight: 781);
            var tile = AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && b.Element.GetAttribute("id") == "t3");
            // (760 - 2*16) / 3 = 242.67 per track.
            Assert.That(tile.Width, Is.EqualTo(242.67).Within(2.0), "tile takes its 1fr track");
            AssertTileTextCentred(root, "t3");
            AssertTileTextCentred(root, "t1");
            AssertTileTextCentred(root, "t6");
        }

        [Test]
        public void Tile_text_centres_in_bare_grid() {
            // Same grid without the outer column flex — control case.
            const string css =
                ".grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; max-width: 760px; }" +
                ".tile { display: flex; flex-direction: column; align-items: center; gap: 12px; " +
                "        padding: 26px 12px; font-size: 16px; border: 1px solid #444; }" +
                ".t-ic { width: 44px; height: 44px; }";
            const string html =
                "<section class='grid'>" +
                "<button class='tile' id='t1'><span class='t-ic'></span>Quests</button>" +
                "<button class='tile' id='t2'><span class='t-ic'></span>Skills</button>" +
                "<button class='tile' id='t3'><span class='t-ic'></span>Crafting</button>" +
                "</section>";
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);
            AssertTileTextCentred(root, "t3");
        }

        [Test]
        public void Refit_still_shrinks_when_container_narrows() {
            // Control for the other direction: the existing over-stretch
            // shrink must keep working (column flex item hugging its content
            // below the container width).
            const string css =
                ".col { display: flex; flex-direction: column; align-items: center; width: 400px; font-size: 16px; }";
            const string html = "<div class='col'>Hi</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var col = FirstWithClass(root, "col");
            var anon = col.Children.OfType<BlockBox>().FirstOrDefault(ch => ch.Element == null);
            Assert.That(anon, Is.Not.Null);
            Assert.That(anon.Width, Is.LessThan(60),
                $"bare text hugs content under align-items:center, got W={anon.Width:F1}");
            double expectedX = (400 - anon.Width) * 0.5;
            Assert.That(anon.X, Is.EqualTo(expectedX).Within(1.0),
                $"centred: X={anon.X:F1} expected≈{expectedX:F1}");
        }
    }
}
