using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Regression for the combat-hud ability bar: an absolutely positioned
    // flex ROW container pinned with `bottom` and NO explicit height. Its
    // BlockLayout pre-pass stacks the flex items vertically (sum of item
    // heights), then FlexLayout collapses the container to a single row's
    // cross-size. If the bottom-edge `absY = cb.Y + cb.Height - bottom -
    // box.Height` is computed against the stale pre-flex (tall) height and
    // not recomputed after the flex pass shrinks the container, the bar
    // floats up into the middle of the viewport instead of sitting at the
    // bottom.
    public class AbsoluteBottomFlexHeightTests {
        [Test]
        public void Bottom_pinned_flex_row_sits_at_bottom_after_flex_collapses_height() {
            const string css = @"
                .arena { position: relative; width: 1280px; height: 800px; }
                .bar {
                    position: absolute;
                    bottom: 24px;
                    left: 50%;
                    display: flex;
                    gap: 8px;
                }
                .slot { width: 64px; height: 72px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"arena\">" +
                "<ol class=\"bar\">" +
                "<li class=\"slot\"></li><li class=\"slot\"></li><li class=\"slot\"></li>" +
                "<li class=\"slot\"></li><li class=\"slot\"></li><li class=\"slot\"></li>" +
                "</ol></div>",
                css, viewportWidth: 1280, viewportHeight: 800);

            var bar = FirstByClass(root, "bar");
            // Row flex of 72px-tall slots → container height collapses to 72.
            Assert.That(bar.Height, Is.EqualTo(72).Within(0.5), "container height should collapse to one row");
            var (_, ay) = AbsoluteOriginOf(bar);
            // bottom:24 in an 800-tall CB with a 72-tall box → top = 800-24-72 = 704.
            Assert.That(ay, Is.EqualTo(704).Within(0.5), "bottom-pinned bar should sit at the viewport bottom");
        }
    }
}
