using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint {
    public class ClipPathShapeTests {
        [Test]
        public void Inset_clip_preserves_rounded_radii_for_axis_aligned_transform() {
            var shape = new InsetClipPathShape(
                new Rect(10, 20, 100, 50),
                new BorderRadii(
                    new CornerRadius(8, 10),
                    new CornerRadius(12, 14),
                    new CornerRadius(16, 18),
                    new CornerRadius(20, 22)));

            var transformed = shape.Transform(new Transform2D(2f, 0f, 0f, 3f, 5f, 7f));

            Assert.That(transformed, Is.TypeOf<InsetClipPathShape>());
            var inset = (InsetClipPathShape)transformed;
            Assert.That(inset.Rect.X, Is.EqualTo(25).Within(1e-6));
            Assert.That(inset.Rect.Y, Is.EqualTo(67).Within(1e-6));
            Assert.That(inset.Rect.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(inset.Rect.Height, Is.EqualTo(150).Within(1e-6));
            Assert.That(inset.Radii.TopLeft.XRadius, Is.EqualTo(16).Within(1e-6));
            Assert.That(inset.Radii.TopLeft.YRadius, Is.EqualTo(30).Within(1e-6));
            Assert.That(inset.Radii.BottomLeft.XRadius, Is.EqualTo(40).Within(1e-6));
            Assert.That(inset.Radii.BottomLeft.YRadius, Is.EqualTo(66).Within(1e-6));
        }

        [Test]
        public void Inset_clip_uses_polygon_fallback_for_rotation() {
            var shape = new InsetClipPathShape(new Rect(0, 0, 100, 50), BorderRadii.Uniform(12));

            var transformed = shape.Transform(Transform2D.Rotate(15));

            Assert.That(transformed, Is.TypeOf<PolygonClipPathShape>());
        }

        const string PentagramPoints =
            "50px 10px, 88px 88px, 12px 38px, 88px 38px, 12px 88px";

        static PolygonClipPathShape ResolvePolygon(string clipPath) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            var shape = ClipPathResolver.Resolve(style, LengthContext.Default, new Rect(0, 0, 100, 100));
            Assert.That(shape, Is.TypeOf<PolygonClipPathShape>());
            return (PolygonClipPathShape)shape;
        }

        [Test]
        public void Polygon_nonzero_keyword_treats_self_intersecting_center_as_inside() {
            var shape = ResolvePolygon($"polygon(nonzero, {PentagramPoints})");
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
            Assert.That(shape.Contains(50, 50), Is.True);
        }

        [Test]
        public void Polygon_evenodd_keyword_treats_self_intersecting_center_as_outside() {
            var shape = ResolvePolygon($"polygon(evenodd, {PentagramPoints})");
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Evenodd));
            Assert.That(shape.Contains(50, 50), Is.False);
        }

        [Test]
        public void Polygon_default_fill_rule_is_nonzero() {
            var shape = ResolvePolygon($"polygon({PentagramPoints})");
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
            Assert.That(shape.Contains(50, 50), Is.True);
        }
    }
}
