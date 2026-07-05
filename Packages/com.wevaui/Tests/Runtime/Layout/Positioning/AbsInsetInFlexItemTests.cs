using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Positive coverage for the combat-hud hero bar label pattern: a
    // `position:absolute; inset:0` overlay inside a SHORT bordered box that is
    // itself a FLEX ITEM of a column flex must pin to the bar's content-box top
    // (just inside the 1px border, local Y = 1). This single-pass layout is
    // correct today. NOTE: the live combat-hud still mis-positions this label
    // to the content-box bottom (Y = 11) under repeated/incremental relayout —
    // a separate multi-pass positioning bug not reproduced by a single Build()
    // (tracked outside this test).
    public class AbsInsetInFlexItemTests {
        [Test]
        public void Abs_inset_overlay_pins_to_top_inside_flex_item_bar() {
            // Mirror combat-hud: the bars live inside an ABSOLUTELY positioned
            // flex container with no explicit width (min-width only), which
            // routes the wrapper through ApplyAbsoluteAgainst's shrink-to-fit
            // RelayoutContentAt probe — the context that exposed the bug.
            const string css = @"
                * { box-sizing: border-box; }
                .frame { position: absolute; bottom: 24px; left: 24px; display: flex; gap: 12px; align-items: center; padding: 12px 16px; min-width: 280px; }
                .bars { flex: 1 1 auto; display: flex; flex-direction: column; gap: 4px; min-width: 0; }
                .bar {
                    position: relative;
                    height: 12px;
                    border: 1px solid #fff;
                    overflow: hidden;
                }
                .fill { height: 100%; width: 40%; }
                .label { position: absolute; inset: 0; display: flex; align-items: center; padding: 0 6px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"frame\"><div class=\"bars\">" +
                "<div class=\"bar\"><div class=\"fill\"></div><div class=\"label\">HP 1,440 / 8,000</div></div>" +
                "</div></div>",
                css, viewportWidth: 400, viewportHeight: 300);

            var bar = FirstByClass(root, "bar");
            var label = FirstByClass(root, "label");
            Assert.That(bar.Height, Is.EqualTo(12).Within(0.5));
            // Local Y inside the bar: content top = border-top = 1.
            Assert.That(label.Y, Is.EqualTo(1).Within(0.5),
                "abs inset:0 overlay must pin to content-box top, not bottom");
            Assert.That(label.Height, Is.EqualTo(10).Within(0.5),
                "inset:0 height = bar height - 2*border");
        }
    }
}
