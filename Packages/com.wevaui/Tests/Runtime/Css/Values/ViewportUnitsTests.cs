using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // Spec coverage for `vmin` / `vmax` viewport units (CSS Values & Units L4 §6.1.1).
    //   vmin = 1% of the smaller of the viewport's width or height.
    //   vmax = 1% of the larger  of the viewport's width or height.
    // The dimension chosen is the absolute min/max — it does NOT depend on which
    // axis is wider; flipping orientation must yield the same vmin/vmax pixel value.
    public class ViewportUnitsTests {
        static LengthContext Viewport(double w, double h) {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            c.ViewportWidthPx = w;
            c.ViewportHeightPx = h;
            c.DpiPixelsPerInch = 96;
            return c;
        }

        [Test]
        public void Vmin_800x600_picks_smaller_height() {
            // 1vmin = 1% of min(800, 600) = 6px
            var l = new CssLength(1, CssLengthUnit.Vmin);
            Assert.That(l.ToPixels(Viewport(800, 600)), Is.EqualTo(6).Within(1e-9));
        }

        [Test]
        public void Vmax_800x600_picks_larger_width() {
            // 1vmax = 1% of max(800, 600) = 8px
            var l = new CssLength(1, CssLengthUnit.Vmax);
            Assert.That(l.ToPixels(Viewport(800, 600)), Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void Vmin_600x800_still_picks_smaller_dimension() {
            // Orientation swap: min(600, 800) = 600, so 1vmin still = 6px.
            var l = new CssLength(1, CssLengthUnit.Vmin);
            Assert.That(l.ToPixels(Viewport(600, 800)), Is.EqualTo(6).Within(1e-9));
        }

        [Test]
        public void Vmax_600x800_still_picks_larger_dimension() {
            // Orientation swap: max(600, 800) = 800, so 1vmax still = 8px.
            var l = new CssLength(1, CssLengthUnit.Vmax);
            Assert.That(l.ToPixels(Viewport(600, 800)), Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void Vmin_equals_vw_when_width_is_smaller() {
            var ctx = Viewport(600, 800);
            var vmin = new CssLength(10, CssLengthUnit.Vmin).ToPixels(ctx);
            var vw = new CssLength(10, CssLengthUnit.Vw).ToPixels(ctx);
            Assert.That(vmin, Is.EqualTo(vw).Within(1e-9));
        }

        [Test]
        public void Vmax_equals_vh_when_height_is_larger() {
            var ctx = Viewport(600, 800);
            var vmax = new CssLength(10, CssLengthUnit.Vmax).ToPixels(ctx);
            var vh = new CssLength(10, CssLengthUnit.Vh).ToPixels(ctx);
            Assert.That(vmax, Is.EqualTo(vh).Within(1e-9));
        }

        [Test]
        public void Square_viewport_vmin_equals_vmax() {
            // When width == height, vmin and vmax coincide.
            var ctx = Viewport(500, 500);
            var vmin = new CssLength(20, CssLengthUnit.Vmin).ToPixels(ctx);
            var vmax = new CssLength(20, CssLengthUnit.Vmax).ToPixels(ctx);
            Assert.That(vmin, Is.EqualTo(100).Within(1e-9));
            Assert.That(vmax, Is.EqualTo(100).Within(1e-9));
        }
    }
}
