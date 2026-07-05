using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // glass.html regression: shrinking the window squashed the `.player`
    // glass card to its fr share while Chrome held a minimum width. Per CSS
    // Grid L1 §7.2.4 a bare `<flex>` track is minmax(auto, <flex>), and the
    // AUTO minimum is the items' min-content contribution (§6.6) — an fr
    // track must not shrink below the largest min-content of its items
    // (Chrome verified: 600px board, cols 1.25fr/1fr/0.95fr, player with a
    // fixed 168px art → player column 305.6px, fair share would be 218.75).
    public class GridFrAutoMinimumTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        const string Css =
            ".board { display: grid; grid-template-columns: 1.25fr 1fr 0.95fr; gap: 20px; width: 600px; }" +
            ".player { display: flex; gap: 24px; padding: 26px; }" +
            ".art { width: 168px; height: 168px; flex: none; }" +
            ".info { flex: 1; min-width: 0; }" +
            ".other { height: 50px; }";
        const string Html =
            "<div class='board'>" +
            "<div class='player'><div class='art'></div><div class='info'>Midnight Prism</div></div>" +
            "<div class='other'></div><div class='other'></div>" +
            "</div>";

        [Test]
        public void Fr_track_does_not_shrink_below_item_min_content() {
            var (root, _, _) = Build(Html, Css, viewportWidth: 700, viewportHeight: 400);
            var player = FirstWithClass(root, "player");
            var art = FirstWithClass(root, "art");
            Assert.That(player, Is.Not.Null);
            // The fixed 168px art + 24 gap + 52 horizontal padding = 244 is
            // the hard floor even ignoring the text's min-content. The fair
            // fr share (218.75) is BELOW that → the track must grow to the
            // min-content contribution, not squash the card.
            Assert.That(player.Width, Is.GreaterThanOrEqualTo(244 - 1),
                $"fr track must respect the item's min-content floor, got {player.Width:F1}");
            // The art itself must never be squashed (flex:none inside).
            Assert.That(art.Width, Is.EqualTo(168).Within(0.5));
            // Chrome reference for the full structure: 305.6 (includes the
            // text min-content). Allow font-metric wiggle on the text part.
            Assert.That(player.Width, Is.EqualTo(305.6).Within(12.0),
                $"Chrome floors this column at ~305.6, got {player.Width:F1}");
        }

        [Test]
        public void Nested_grid_item_does_not_inflate_its_fr_track() {
            // Regression guard (glass.html): a nested grid whose min-content
            // INLINE size is far BELOW its fair fr share must NOT inflate the
            // track beyond the proportional share.
            //
            // With the real grid min-content (§5.2/§12.4): the nested `.tiles`
            // grid has two 1fr sub-columns + 14px gap. Under a min-content
            // constraint each tile gets its longest-word width from MonoFontMetrics
            // (~7px per char). "Wi-Fi" → ~35px, "Bluetooth" → ~63px per tile;
            // the padded tile width ≈ 95px; the two-column min-content ≈
            // 95+95+14 = 204px + 32px tile padding = ~236px min-content for
            // the tiles grid. This is well BELOW the 268.75 fair share
            // (860px / 3.2fr × 1fr), so no floor pressure: the track stays
            // at the proportional 268.75.
            //
            // The critical non-regression: the old stub returned 0 (tiles
            // floored at nothing → proportional share); with a real min-content
            // the answer is still proportional because min-content < share.
            // Both old and new code produce 268.75 for this case.
            const string css =
                ".board { display: grid; grid-template-columns: 1.25fr 1fr 0.95fr; gap: 20px; width: 900px; }" +
                ".player { display: flex; gap: 24px; padding: 26px; }" +
                ".art { width: 168px; height: 168px; flex: none; }" +
                ".tiles { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }" +
                ".tile { padding: 14px 16px; }" +
                ".notes { }";
            const string html =
                "<div class='board'>" +
                "<div class='player'><div class='art'></div></div>" +
                "<div class='tiles'>" +
                "<div class='tile'>Wi-Fi with a fairly long natural single line of text</div>" +
                "<div class='tile'>Bluetooth devices listing line</div>" +
                "</div>" +
                "<div class='notes'>n</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 1000, viewportHeight: 400);

            var board = FirstWithClass(root, "board");
            var tiles = FirstWithClass(root, "tiles");
            // Fair shares at 900: content 860 over 3.2fr → tiles (1fr) ≈
            // 268.75. The nested grid's true min-content is ~below 244px
            // (two tile columns with short words + 14px gap + padding)
            // — well BELOW 268.75 → no floor pressure → tiles stays at
            // its proportional share of 268.75.
            Assert.That(tiles.Width, Is.EqualTo(268.75).Within(2.0),
                $"nested grid must not inflate its fr track, got {tiles.Width:F1}");
            // And nothing overflows the board.
            Assert.That(tiles.X + tiles.Width, Is.LessThanOrEqualTo(board.Width + 1));
        }

        [Test]
        public void Fr_tracks_share_normally_when_space_suffices() {
            // Control: a wide board has leftover ≥ all minimums → plain
            // proportional fr distribution must be unchanged.
            var (root, _, _) = Build(Html.Replace("width: 600px", "width: 600px"),
                Css.Replace("width: 600px", "width: 1200px"), viewportWidth: 1400, viewportHeight: 400);
            var player = FirstWithClass(root, "player");
            // 1200 − 40 gaps = 1160; 1.25/3.2 × 1160 = 453.125.
            Assert.That(player.Width, Is.EqualTo(453.125).Within(1.0),
                $"unconstrained fr share must stay proportional, got {player.Width:F1}");
        }
    }
}
