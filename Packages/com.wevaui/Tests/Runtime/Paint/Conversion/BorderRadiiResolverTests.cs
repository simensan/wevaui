using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    public class BorderRadiiResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;
        static Rect Bounds() => new Rect(0, 0, 200, 100);

        [Test]
        public void Single_value_applies_to_all_corners() {
            var s = Style();
            s.Set("border-radius", "10px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(10).Within(1e-6));
        }

        [Test]
        public void Two_values_pair_diagonal_corners() {
            var s = Style();
            s.Set("border-radius", "10px 20px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(20).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(20).Within(1e-6));
        }

        [Test]
        public void Four_values_assign_each_corner() {
            var s = Style();
            s.Set("border-radius", "1px 2px 3px 4px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(1).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(2).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(3).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(4).Within(1e-6));
        }

        [Test]
        public void Per_corner_longhands_take_priority() {
            var s = Style();
            s.Set("border-top-left-radius", "5px");
            s.Set("border-top-right-radius", "10px");
            s.Set("border-bottom-right-radius", "15px");
            s.Set("border-bottom-left-radius", "20px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(5).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(15).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(20).Within(1e-6));
        }

        [Test]
        public void Percentage_resolved_against_axis() {
            var s = Style();
            s.Set("border-radius", "50%");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), new Rect(0, 0, 200, 100));
            // 50% of 200 = 100 for x; 50% of 100 = 50 for y
            // Then clamped: top sum = 100+100=200 = width; bottom same. Left sum = 50+50=100 = height. f=1.
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.TopLeft.YRadius, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Radii_clamped_when_too_large() {
            var s = Style();
            s.Set("border-radius", "100px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), new Rect(0, 0, 100, 100));
            // Sum on top edge = 100+100 = 200 > 100 width. f = 0.5.
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(50).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(50).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(50).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Zero_radii_isZero() {
            var s = Style();
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.IsZero, Is.True);
        }

        [Test]
        public void Three_values_assign_tl_trbl_br() {
            var s = Style();
            s.Set("border-radius", "1px 2px 3px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(1).Within(1e-6));
            Assert.That(r.TopRight.XRadius, Is.EqualTo(2).Within(1e-6));
            Assert.That(r.BottomLeft.XRadius, Is.EqualTo(2).Within(1e-6));
            Assert.That(r.BottomRight.XRadius, Is.EqualTo(3).Within(1e-6));
        }

        [Test]
        public void Slash_separated_x_y_radii() {
            var s = Style();
            s.Set("border-radius", "10px / 20px");
            var r = BorderRadiiResolver.ResolveBorderRadii(s, Ctx(), Bounds());
            Assert.That(r.TopLeft.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.TopLeft.YRadius, Is.EqualTo(20).Within(1e-6));
        }
    }
}
