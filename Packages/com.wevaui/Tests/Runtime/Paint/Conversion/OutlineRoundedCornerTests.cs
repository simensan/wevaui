using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // §2.4 — outline follows border-radius when rounded (CSS UI L4 §3.5).
    // Per the spec: the outline's corner shape follows the border-box path
    // expanded outward by outline-offset. Each corner radius on the outline
    // equals the corresponding border-radius corner PLUS the outline-offset
    // value (clamped to zero so a large negative offset cannot yield a
    // negative radius). A zero border-radius produces a rectangular outline
    // regardless of offset.
    public class OutlineRoundedCornerTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        static BlockBox Box(double w, double h, ComputedStyle s) {
            var b = new BlockBox();
            b.Style = s;
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            return b;
        }

        static System.Collections.Generic.List<PaintCommand> Convert(BlockBox box)
            => new BoxToPaintConverter().Convert(box).Commands;

        // --- Rectangular outline (no border-radius) ---

        [Test]
        public void No_border_radius_outline_has_zero_radii() {
            // When border-radius is absent (zero) the outline must be
            // rectangular regardless of outline-offset — zero radii on all
            // four corners of the StrokeBorderCommand.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "red");
            // No border-radius set.
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Radii.IsZero, Is.True,
                "A zero-radius box must produce a rectangular (zero-radii) outline");
        }

        [Test]
        public void No_border_radius_with_positive_offset_outline_still_rectangular() {
            // Even with outline-offset > 0, a box with no border-radius must
            // keep a rectangular outline (radii stay at 0).
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "blue");
            s.Set("outline-offset", "4px");
            // No border-radius set.
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Radii.IsZero, Is.True,
                "Positive offset on a non-rounded box must not introduce radii");
        }

        // --- Rounded outline tracks border-radius ---

        [Test]
        public void Border_radius_8px_zero_offset_outline_radii_match_border_radius() {
            // With outline-offset: 0 (default) the outline corner radius
            // equals the border-radius (8px). Each corner of the outline
            // StrokeBorderCommand must report XRadius = YRadius = 8.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "red");
            s.Set("border-radius", "8px");
            // outline-offset defaults to 0.
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Radii.TopLeft.XRadius, Is.EqualTo(8).Within(1e-4),
                "TopLeft XRadius must equal border-radius when offset is 0");
            Assert.That(outline.Radii.TopLeft.YRadius, Is.EqualTo(8).Within(1e-4),
                "TopLeft YRadius must equal border-radius when offset is 0");
            Assert.That(outline.Radii.TopRight.XRadius, Is.EqualTo(8).Within(1e-4));
            Assert.That(outline.Radii.BottomRight.XRadius, Is.EqualTo(8).Within(1e-4));
            Assert.That(outline.Radii.BottomLeft.XRadius, Is.EqualTo(8).Within(1e-4));
        }

        [Test]
        public void Border_radius_8px_offset_4px_outline_radii_are_expanded() {
            // CSS UI L4 §3.5: outline corner radius = border-radius + outline-offset.
            // With border-radius 8px and outline-offset 4px each outline corner = 12px.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "4px");
            s.Set("outline-color", "red");
            s.Set("outline-offset", "4px");
            s.Set("border-radius", "8px");
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Radii.TopLeft.XRadius, Is.EqualTo(12).Within(1e-4),
                "TopLeft XRadius = border-radius 8 + outline-offset 4 = 12");
            Assert.That(outline.Radii.BottomRight.XRadius, Is.EqualTo(12).Within(1e-4));
        }

        [Test]
        public void Negative_offset_shrinks_outline_radii_clamped_to_zero() {
            // A large negative outline-offset (e.g. -20px) with border-radius 8px
            // results in 8 + (-20) = -12, which must clamp to 0 (no negative radii).
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "blue");
            s.Set("outline-offset", "-20px");
            s.Set("border-radius", "8px");
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Radii.TopLeft.XRadius, Is.EqualTo(0).Within(1e-4),
                "Outline radius must clamp to 0 when border-radius + offset < 0");
        }

        // --- Ellipse (border-radius: 50%) ---

        [Test]
        public void Border_radius_50_percent_non_square_outline_uses_ellipse_radii() {
            // border-radius: 50% on a 100x60 box gives x-radius = 50px,
            // y-radius = 30px on every corner (clamped to fit). With
            // outline-offset: 0 the outline corner radii match exactly.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "green");
            s.Set("border-radius", "50%");
            // No outline-offset (default 0).
            var cmds = Convert(Box(100, 60, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            // After CSS clamping each corner on a 100x60 box with 50%:
            // x-radius = 50, y-radius = 30 per corner (no overlap because
            // top+bottom = 60 = height and left+right = 100 = width).
            Assert.That(outline.Radii.TopLeft.XRadius, Is.EqualTo(50).Within(1e-4),
                "TopLeft XRadius (50% of width=100) = 50");
            Assert.That(outline.Radii.TopLeft.YRadius, Is.EqualTo(30).Within(1e-4),
                "TopLeft YRadius (50% of height=60) = 30");
        }

        // --- Bounds geometry is unaffected by the radii fix ---

        [Test]
        public void Outline_bounds_geometry_unchanged_by_rounded_corner_fix() {
            // The fix to radii must NOT affect the outline bounds rect.
            // With outline-offset: 2px and outline-width: 2px the outline is
            // positioned 4px out from the box origin on every side:
            //   X = -4, Y = -4, Width = box.Width + 8, Height = box.Height + 8.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "red");
            s.Set("outline-offset", "2px");
            s.Set("border-radius", "8px");
            var cmds = Convert(Box(100, 50, s));
            var outline = cmds.OfType<StrokeBorderCommand>().First();
            Assert.That(outline.Bounds.X, Is.EqualTo(-4).Within(1e-4));
            Assert.That(outline.Bounds.Y, Is.EqualTo(-4).Within(1e-4));
            Assert.That(outline.Bounds.Width, Is.EqualTo(108).Within(1e-4));
            Assert.That(outline.Bounds.Height, Is.EqualTo(58).Within(1e-4));
        }
    }
}
