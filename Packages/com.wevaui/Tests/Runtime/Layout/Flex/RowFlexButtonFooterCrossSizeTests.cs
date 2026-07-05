using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Regression for settings.html footer (#14): the row-flex footer is a grid
    // item in a `grid-template-rows: auto 1fr auto` panel. The grid auto-row
    // measured the footer's intrinsic cross via FlexIntrinsicCross, which for a
    // FlexBox grandchild (inline-flex <button>) returned the button's live
    // STRETCHED .Height — feeding the stretch back into the grid row so the
    // footer (and its buttons) came out ~2x too tall (35 -> 70). The fix in
    // IntrinsicCrossOfPureInlineBlock derives a stretch-immune content cross
    // for auto-height flex children. Mirrors the live settings layout.
    public class RowFlexButtonFooterCrossSizeTests {
        [Test]
        public void Footer_buttons_in_grid_auto_row_are_single_line_height() {
            const string css = @"
                * { box-sizing: border-box; }
                .prefs { height: 100vh; }
                .panel {
                    display: grid;
                    grid-template-rows: auto 1fr auto;
                    height: 100%;
                    min-height: 0;
                    overflow: hidden;
                }
                .panel-body { overflow-y: auto; }
                .panel-foot {
                    display: flex;
                    gap: 12px;
                    justify-content: flex-end;
                    padding: 12px 24px;
                    border-top: 1px solid #000;
                }
                .btn { padding: 8px 16px; font-size: 13px; }
            ";
            var (root, _, _) = Build(
                "<main class=\"prefs\"><section class=\"panel\">" +
                "<header class=\"panel-head\">Display</header>" +
                "<div class=\"panel-body\">body</div>" +
                "<footer class=\"panel-foot\">" +
                "<button class=\"btn\">Reset Defaults</button><button class=\"btn\">Apply &amp; Save</button>" +
                "</footer></section></main>",
                css, viewportWidth: 1000, viewportHeight: 800);

            var foot = FirstByClass(root, "panel-foot");
            var btn = FirstByClass(root, "btn");
            // A single 13px text line (~17) + 8+8 padding ≈ 33. Must NOT be the
            // 2-button stacked (~66) feedback value.
            Assert.That(btn.Height, Is.LessThan(45),
                $"footer button should be single-line height, was {btn.Height:F1}");
            double footContent = foot.Height - foot.PaddingTop - foot.PaddingBottom - foot.BorderTop - foot.BorderBottom;
            Assert.That(footContent, Is.EqualTo(btn.Height).Within(1.5),
                $"footer content (grid auto-row) should equal one button row, was {footContent:F1} vs btn {btn.Height:F1}");
        }
    }
}
