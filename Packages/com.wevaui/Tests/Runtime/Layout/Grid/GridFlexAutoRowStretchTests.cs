using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Audit (advanced-dashboard): a `flex: 1 1 auto` grid inside a
    // `min-height: 100vh` flex column inflated its single auto row to 1625px
    // when the tallest item content was ~530px — the page grew to 1820 vs
    // Chrome's 781. The auto row must size to the largest item contribution
    // (CSS Grid L1 §11.5); any stretch comes from distributing the DEFINITE
    // leftover, never from feeding a stretched size back into the row's
    // intrinsic contribution.
    public class GridFlexAutoRowStretchTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        [Test]
        public void Aspect_ratio_descendants_do_not_inflate_the_row_hint() {
            // The actual advanced-dashboard failure: the grid ITEM is a plain
            // block whose DESCENDANTS are aspect-ratio cards. The item's
            // pre-grid BlockLayout pass measures at the container's full
            // width (1378), the 2-col aspect-ratio cards come out ~670 tall
            // each, and that width-stale height fed the auto row (1625 vs
            // real content ~530) — then align-stretch wrote the inflated row
            // back into the item height, locking the loop across converge
            // passes (page 1820 vs Chrome 781).
            const string css =
                "html, body { margin: 0; padding: 0; }" +
                ".dashboard { display: flex; flex-direction: column; min-height: 100vh; }" +
                ".hdr { height: 88px; flex: none; }" +
                ".grid { display: grid; " +
                "        grid-template-columns: minmax(0, 280px) minmax(0, 1fr) minmax(0, 320px); " +
                "        gap: 16px; padding: 16px 28px 28px; flex: 1 1 auto; }" +
                ".cards { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }" +
                ".card { aspect-ratio: 1 / 1; }" +
                ".filler { height: 200px; }";
            const string html =
                "<div class='dashboard'>" +
                "<header class='hdr'></header>" +
                "<main class='grid'>" +
                "<section class='stats'><ol class='cards'>" +
                "<li class='card'></li><li class='card'></li><li class='card'></li><li class='card'></li>" +
                "</ol></section>" +
                "<section class='mid'><div class='filler'></div></section>" +
                "<section class='side'><div class='filler'></div></section>" +
                "</main></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            var dash = FirstWithClass(root, "dashboard");
            var cards = FirstWithClass(root, "cards");
            System.Console.WriteLine($"dash H={dash.Height:F0} cards H={cards.Height:F0}");
            // Stats column = 280; 2-col cards → each ~136 wide/tall → 2 rows
            // ≈ 280. The page must stay at the viewport, NOT inflate to the
            // full-width aspect measure (~1400).
            Assert.That(dash.Height, Is.EqualTo(781).Within(2.0),
                $"aspect-ratio descendants must not inflate the page, got {dash.Height:F0}");
            Assert.That(cards.Height, Is.LessThan(320),
                $"2-col aspect cards at a 280px column are ~280 tall, got {cards.Height:F0}");
        }

        [Test]
        public void Flex_grow_grid_auto_row_sizes_to_content_not_feedback() {
            const string css =
                "html, body { margin: 0; padding: 0; }" +
                ".dashboard { display: flex; flex-direction: column; min-height: 100vh; }" +
                ".hdr { height: 88px; flex: none; }" +
                ".grid { display: grid; " +
                "        grid-template-columns: minmax(0, 280px) minmax(0, 1fr) minmax(0, 320px); " +
                "        gap: 16px; padding: 16px 28px 28px; flex: 1 1 auto; }" +
                ".stats { } .activity { } .achievements { }" +
                ".filler { height: 500px; }";
            const string html =
                "<div class='dashboard'>" +
                "<header class='hdr'></header>" +
                "<main class='grid'>" +
                "<section class='stats'><div class='filler'></div></section>" +
                "<section class='activity'><div class='filler'></div></section>" +
                "<section class='achievements'><div class='filler'></div></section>" +
                "</main></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1434, viewportHeight: 781);

            var dash = FirstWithClass(root, "dashboard");
            var grid = FirstWithClass(root, "grid");
            var stats = FirstWithClass(root, "stats");
            System.Console.WriteLine(
                $"dash H={dash.Height:F0} grid H={grid.Height:F0} stats H={stats.Height:F0}");

            // Content rows: 500px fillers → row ≈ 500. The grid gets flex:1 of
            // the viewport (781−88 = 693 incl. padding) and the row may
            // STRETCH into that definite leftover (Chrome: items stretch to
            // 649) — but must never exceed the flex container's extent.
            Assert.That(dash.Height, Is.EqualTo(781).Within(2.0),
                $"min-height:100vh page with smaller content stays at the viewport, got {dash.Height:F0}");
            Assert.That(grid.Height, Is.EqualTo(693).Within(2.0),
                $"flex:1 grid fills the leftover, got {grid.Height:F0}");
            Assert.That(stats.Height, Is.EqualTo(649).Within(2.0),
                $"items stretch to the definite row (693−16−28 pad), got {stats.Height:F0}");
        }
    }
}
