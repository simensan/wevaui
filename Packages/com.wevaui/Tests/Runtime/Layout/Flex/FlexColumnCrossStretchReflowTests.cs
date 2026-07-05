using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression (flex-playground sidebar + nested card): a column-flex container
    // that is cross-stretched TALLER than its content by its row-flex parent's
    // align-items:stretch. Its inner main-axis free-space distribution
    // (margin-top:auto pinning, flex-grow spacers) must be recomputed at the
    // stretched height. The engine reflowed on SHRINK (ReflowIfShrunk) but not on
    // cross-stretch GROWTH, so `margin-top:auto` footers stayed near the top and
    // `flex:1 1 auto` spacers never expanded — the "72% coverage" meter sat mid-
    // sidebar instead of the bottom, and "Bottom 99" wasn't at the column bottom.
    public class FlexColumnCrossStretchReflowTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        static double AbsY(Box b) { double y = 0; for (var p = b; p != null; p = p.Parent) y += p.Y; return y; }

        [Test]
        public void Margin_top_auto_pins_footer_to_bottom_after_cross_stretch() {
            const string css = @"
                .row  { display: flex; align-items: stretch; height: 300px; }
                .col  { display: flex; flex-direction: column; flex: 0 0 200px; }
                .top  { height: 20px; }
                .foot { margin-top: auto; height: 24px; }";
            const string html =
                @"<div class='row'><div class='col'><div class='top'>t</div><div class='foot'>f</div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var col = FirstWithClass(root, "col");
            var foot = FirstWithClass(root, "foot");
            Assert.That(col, Is.Not.Null);
            Assert.That(foot, Is.Not.Null);
            Assert.That(col.Height, Is.EqualTo(300).Within(0.5), $"col stretches to row height, got {col.Height:F1}");
            // margin-top:auto must push the 24px footer to the column's bottom:
            // its top should be at colTop + 300 - 24 = colTop + 276.
            double colTop = AbsY(col);
            Assert.That(AbsY(foot), Is.EqualTo(colTop + 276).Within(1.5),
                $"foot should be pinned to the bottom (~{colTop + 276:F0}), got {AbsY(foot):F0}");
        }

        [Test]
        public void Flex_grow_spacer_expands_after_cross_stretch() {
            const string css = @"
                .row  { display: flex; align-items: stretch; height: 300px; }
                .col  { display: flex; flex-direction: column; flex: 0 0 200px; }
                .top  { height: 20px; }
                .grow { flex: 1 1 auto; }
                .bot  { height: 24px; }";
            const string html =
                @"<div class='row'><div class='col'><div class='top'>t</div><div class='grow'></div><div class='bot'>b</div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var grow = FirstWithClass(root, "grow");
            var bot = FirstWithClass(root, "bot");
            Assert.That(grow, Is.Not.Null);
            // grow should expand to fill: 300 - 20 (top) - 24 (bot) = 256.
            Assert.That(grow.Height, Is.EqualTo(256).Within(1.5),
                $"flex-grow spacer should expand to fill the stretched column (~256), got {grow.Height:F1}");
            double colBottom = AbsY(FirstWithClass(root, "col")) + 300;
            Assert.That(AbsY(bot) + bot.Height, Is.EqualTo(colBottom).Within(1.5),
                $"last row should reach the column bottom ({colBottom:F0}), got {AbsY(bot) + bot.Height:F0}");
        }
    }
}
