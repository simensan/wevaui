using NUnit.Framework;
using Weva.Layout.Positioning;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Sticky positioning is now implemented via StickyResolver running after
    // BlockLayout / FlexLayout. PositioningPass leaves sticky-element layout
    // coordinates untouched (the natural in-flow position) and the resolver
    // writes Box.StickyOffsetX/Y for the paint converter to apply.
    public class StickyPositionTests {
        [Test]
        public void Sticky_position_type_is_recognized() {
            const string css = ".sticky { position: sticky; top: 25px; height: 30px; width: 100px; }";
            var (root, _, _) = Build("<div class=\"sticky\"></div>", css, viewportWidth: 800);
            var sticky = FirstByClass(root, "sticky");
            Assert.That(sticky.Position, Is.EqualTo(PositionType.Sticky));
        }

        [Test]
        public void Sticky_layout_y_is_natural_in_flow_position() {
            // Sticky leaves the natural in-flow Y untouched; the visual shift
            // is carried entirely by the paint-time offset (Box.StickyOffsetY).
            // With no scrollable ancestor, sticky degrades to "relative" per
            // spec, so the offset reflects the specified `top: 25px`.
            const string css = ".sticky { position: sticky; top: 25px; height: 30px; width: 100px; }";
            var (root, _, _) = Build("<div class=\"sticky\"></div>", css, viewportWidth: 800);
            var sticky = FirstByClass(root, "sticky");
            Assert.That(sticky.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(25).Within(0.001));
        }

        [Test]
        public void Sticky_does_not_shift_subsequent_siblings() {
            const string css = @"
                #s { position: sticky; top: 50px; height: 40px; }
                #after { height: 30px; }
            ";
            var (root, _, _) = Build("<div id=\"s\"></div><div id=\"after\"></div>", css, viewportWidth: 800);
            var after = FirstById(root, "after");
            // Sticky never shifts its successors in flow.
            Assert.That(after.Y, Is.EqualTo(40).Within(0.001));
        }
    }
}
