using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    public class BackgroundResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds() => new Rect(0, 0, 100, 50);

        [Test]
        public void Solid_color_yields_solid_brush() {
            var s = Style();
            s.Set("background-color", "red");
            s.Set("color", "black");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.SolidColor));
            Assert.That(brush.Color.A, Is.GreaterThan(0.99f));
            Assert.That(brush.Color.R, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Linear_gradient_with_angle_and_two_stops_parses() {
            var s = Style();
            s.Set("background-image", "linear-gradient(45deg, red, blue)");
            s.Set("color", "black");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.AngleDegrees, Is.EqualTo(45).Within(1e-6));
            Assert.That(lg.Stops.Count, Is.EqualTo(2));
            Assert.That(lg.Stops[0].Position, Is.EqualTo(0).Within(1e-6));
            Assert.That(lg.Stops[1].Position, Is.EqualTo(1).Within(1e-6));
        }

        // CSS Images 4: the <color-interpolation-method> (`in <space>`) shares
        // the gradient's first comma-segment with the optional direction, in
        // EITHER order. The parser previously only honored the STANDALONE
        // first-arg form (`linear-gradient(in oklab, …)`); the common
        // `to right in oklab` / `45deg in oklab` / `in oklab to right` forms
        // were silently dropped to sRGB (G1b). These pin all four.
        [Test]
        public void Linear_gradient_direction_then_in_oklab_sets_oklab_space() {
            var s = Style();
            s.Set("background-image", "linear-gradient(to right in oklab, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(Weva.Css.Values.CssColorSpace.Oklab));
            Assert.That(lg.AngleDegrees, Is.EqualTo(90).Within(1e-6), "to right => 90deg, direction must survive");
            Assert.That(lg.Stops.Count, Is.EqualTo(2));
        }

        [Test]
        public void Linear_gradient_in_oklab_then_direction_sets_oklab_space() {
            var s = Style();
            s.Set("background-image", "linear-gradient(in oklab to right, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(Weva.Css.Values.CssColorSpace.Oklab));
            Assert.That(lg.AngleDegrees, Is.EqualTo(90).Within(1e-6), "in-before-direction must still parse the direction");
        }

        [Test]
        public void Linear_gradient_angle_then_in_oklab_sets_oklab_space() {
            var s = Style();
            s.Set("background-image", "linear-gradient(45deg in oklab, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(Weva.Css.Values.CssColorSpace.Oklab));
            Assert.That(lg.AngleDegrees, Is.EqualTo(45).Within(1e-6));
        }

        [Test]
        public void Linear_gradient_standalone_in_oklab_still_sets_oklab_space() {
            // The pre-existing standalone form must keep working (default 180deg).
            var s = Style();
            s.Set("background-image", "linear-gradient(in oklab, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(Weva.Css.Values.CssColorSpace.Oklab));
            Assert.That(lg.AngleDegrees, Is.EqualTo(180).Within(1e-6), "no direction => default 180deg");
            Assert.That(lg.Stops.Count, Is.EqualTo(2));
        }

        [Test]
        public void Linear_gradient_no_interpolation_clause_defaults_to_srgb() {
            // Control: plain gradient with a direction must stay sRGB (the
            // project's deliberate default) and not be misread as having an
            // interpolation clause.
            var s = Style();
            s.Set("background-image", "linear-gradient(to right, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(Weva.Css.Values.CssColorSpace.Srgb));
            Assert.That(lg.AngleDegrees, Is.EqualTo(90).Within(1e-6));
        }

        [Test]
        public void Radial_gradient_parses_with_center_and_stops() {
            var s = Style();
            s.Set("background-image", "radial-gradient(circle at 50% 50%, white, black)");
            s.Set("color", "black");
            var brush = BackgroundResolver.ResolveBackground(s, new Rect(0, 0, 200, 100));
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
            var rg = (RadialGradient)brush.GradientValue;
            Assert.That(rg.Shape, Is.EqualTo(RadialGradientShape.Circle));
            Assert.That(rg.CenterX, Is.EqualTo(100).Within(1e-6));
            Assert.That(rg.CenterY, Is.EqualTo(50).Within(1e-6));
            Assert.That(rg.RadiusX, Is.EqualTo(System.Math.Sqrt(100 * 100 + 50 * 50)).Within(1e-6));
            Assert.That(rg.RadiusY, Is.EqualTo(rg.RadiusX).Within(1e-6));
            Assert.That(rg.Stops.Count, Is.EqualTo(2));
        }

        [Test]
        public void Currentcolor_resolves_to_color_property() {
            var s = Style();
            s.Set("color", "blue");
            s.Set("background-color", "currentcolor");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Color.B, Is.GreaterThan(0.5f));
            Assert.That(brush.Color.R, Is.LessThan(0.1f));
        }

        [Test]
        public void Transparent_yields_null_brush() {
            var s = Style();
            s.Set("background-color", "transparent");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Null);
        }

        [Test]
        public void Missing_background_color_yields_null() {
            var s = Style();
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Null);
        }

        [Test]
        public void Hex_rgb_named_colors_all_resolve() {
            var s1 = Style();
            s1.Set("background-color", "#ff0000");
            var b1 = BackgroundResolver.ResolveBackground(s1, Bounds());
            Assert.That(b1, Is.Not.Null);
            Assert.That(b1.Color.R, Is.GreaterThan(0.5f));

            var s2 = Style();
            s2.Set("background-color", "rgb(0, 128, 0)");
            var b2 = BackgroundResolver.ResolveBackground(s2, Bounds());
            Assert.That(b2, Is.Not.Null);
            Assert.That(b2.Color.G, Is.GreaterThan(0.05f));
            Assert.That(b2.Color.R, Is.LessThan(0.05f));

            var s3 = Style();
            s3.Set("background-color", "rebeccapurple");
            var b3 = BackgroundResolver.ResolveBackground(s3, Bounds());
            Assert.That(b3, Is.Not.Null);
        }

        [Test]
        public void Background_image_takes_precedence_over_background_color() {
            var s = Style();
            s.Set("background-color", "red");
            s.Set("background-image", "linear-gradient(white, black)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
        }

        [Test]
        public void Linear_gradient_with_to_keyword_resolves_angle() {
            var s = Style();
            s.Set("background-image", "linear-gradient(to right, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.AngleDegrees, Is.EqualTo(90).Within(1e-6));
        }

        [Test]
        public void Linear_gradient_no_angle_defaults_to_180() {
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.AngleDegrees, Is.EqualTo(180).Within(1e-6));
        }

        // CSS Images 3 §3.7.1 — radial-gradient size keywords. Box is 200x100
        // with the gradient centered at (50, 50): nearX=50, farX=150,
        // nearY=50, farY=50.
        [Test]
        public void Radial_gradient_size_keywords_apply_per_spec() {
            const double Sqrt2 = 1.4142135623730951;
            var bounds = new Rect(0, 0, 200, 100);

            RadialGradient Resolve(string image) {
                var s = Style();
                s.Set("background-image", image);
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, bounds);
                Assert.That(brush, Is.Not.Null, image);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient), image);
                return (RadialGradient)brush.GradientValue;
            }

            // circle closest-side: min(50, 150, 50, 50) = 50
            var cClosestSide = Resolve("radial-gradient(circle closest-side at 50px 50px, red, blue)");
            Assert.That(cClosestSide.Shape, Is.EqualTo(RadialGradientShape.Circle));
            Assert.That(cClosestSide.RadiusX, Is.EqualTo(50).Within(1e-6));
            Assert.That(cClosestSide.RadiusY, Is.EqualTo(50).Within(1e-6));

            // circle closest-corner: sqrt(50^2 + 50^2)
            var cClosestCorner = Resolve("radial-gradient(circle closest-corner at 50px 50px, red, blue)");
            double expectedClosestCornerR = System.Math.Sqrt(50 * 50 + 50 * 50);
            Assert.That(cClosestCorner.RadiusX, Is.EqualTo(expectedClosestCornerR).Within(1e-6));
            Assert.That(cClosestCorner.RadiusY, Is.EqualTo(expectedClosestCornerR).Within(1e-6));

            // circle farthest-side: max(50, 150, 50, 50) = 150
            var cFarthestSide = Resolve("radial-gradient(circle farthest-side at 50px 50px, red, blue)");
            Assert.That(cFarthestSide.RadiusX, Is.EqualTo(150).Within(1e-6));
            Assert.That(cFarthestSide.RadiusY, Is.EqualTo(150).Within(1e-6));

            // circle farthest-corner (regression guard): sqrt(150^2 + 50^2)
            var cFarthestCorner = Resolve("radial-gradient(circle farthest-corner at 50px 50px, red, blue)");
            double expectedFarthestCornerR = System.Math.Sqrt(150 * 150 + 50 * 50);
            Assert.That(cFarthestCorner.RadiusX, Is.EqualTo(expectedFarthestCornerR).Within(1e-6));
            Assert.That(cFarthestCorner.RadiusY, Is.EqualTo(expectedFarthestCornerR).Within(1e-6));

            // ellipse closest-side: per-axis (nearX, nearY) = (50, 50)
            var eClosestSide = Resolve("radial-gradient(ellipse closest-side at 50px 50px, red, blue)");
            Assert.That(eClosestSide.Shape, Is.EqualTo(RadialGradientShape.Ellipse));
            Assert.That(eClosestSide.RadiusX, Is.EqualTo(50).Within(1e-6));
            Assert.That(eClosestSide.RadiusY, Is.EqualTo(50).Within(1e-6));

            // ellipse farthest-side: (farX, farY) = (150, 50)
            var eFarthestSide = Resolve("radial-gradient(ellipse farthest-side at 50px 50px, red, blue)");
            Assert.That(eFarthestSide.RadiusX, Is.EqualTo(150).Within(1e-6));
            Assert.That(eFarthestSide.RadiusY, Is.EqualTo(50).Within(1e-6));

            // ellipse farthest-corner: (farX*sqrt2, farY*sqrt2)
            var eFarthestCorner = Resolve("radial-gradient(ellipse farthest-corner at 50px 50px, red, blue)");
            Assert.That(eFarthestCorner.RadiusX, Is.EqualTo(150 * Sqrt2).Within(1e-6));
            Assert.That(eFarthestCorner.RadiusY, Is.EqualTo(50 * Sqrt2).Within(1e-6));
        }

        // CSS Images 3 §3.6.2 — percentage radii resolve against the
        // corresponding axis (rx against width, ry against height), not
        // the axis average.
        [Test]
        public void Radial_gradient_percent_radii_resolve_per_axis() {
            RadialGradient Resolve(string image, Rect bounds) {
                var s = Style();
                s.Set("background-image", image);
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, bounds);
                Assert.That(brush, Is.Not.Null, image);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient), image);
                return (RadialGradient)brush.GradientValue;
            }

            var rg5050 = Resolve("radial-gradient(ellipse 50% 50%, red, blue)", new Rect(0, 0, 200, 100));
            Assert.That(rg5050.Shape, Is.EqualTo(RadialGradientShape.Ellipse));
            Assert.That(rg5050.RadiusX, Is.EqualTo(100).Within(1e-6));
            Assert.That(rg5050.RadiusY, Is.EqualTo(50).Within(1e-6));

            var rg2575 = Resolve("radial-gradient(ellipse 25% 75%, red, blue)", new Rect(0, 0, 200, 100));
            Assert.That(rg2575.RadiusX, Is.EqualTo(50).Within(1e-6));
            Assert.That(rg2575.RadiusY, Is.EqualTo(75).Within(1e-6));

            var rgSquare = Resolve("radial-gradient(ellipse 20% 80%, red, blue)", new Rect(0, 0, 100, 100));
            Assert.That(rgSquare.RadiusX, Is.EqualTo(20).Within(1e-6));
            Assert.That(rgSquare.RadiusY, Is.EqualTo(80).Within(1e-6));

            var rgFixed = Resolve("radial-gradient(circle 50px, red, blue)", new Rect(0, 0, 200, 100));
            Assert.That(rgFixed.Shape, Is.EqualTo(RadialGradientShape.Circle));
            Assert.That(rgFixed.RadiusX, Is.EqualTo(50).Within(1e-6));
            Assert.That(rgFixed.RadiusY, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Linear_gradient_stop_calc_evaluates_through_full_math_evaluator() {
            LinearGradient Resolve(string image, Rect bounds) {
                var s = Style();
                s.Set("background-image", image);
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, bounds);
                Assert.That(brush, Is.Not.Null, image);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient), image);
                return (LinearGradient)brush.GradientValue;
            }

            var literal = Resolve("linear-gradient(red, blue 50%)", new Rect(0, 0, 200, 50));
            Assert.That(literal.Stops.Count, Is.EqualTo(2));
            Assert.That(literal.Stops[1].Position, Is.EqualTo(0.5).Within(1e-6));

            var pctCalc = Resolve("linear-gradient(red 0%, blue calc(20% + 10%))", new Rect(0, 0, 200, 50));
            Assert.That(pctCalc.Stops.Count, Is.EqualTo(2));
            Assert.That(pctCalc.Stops[0].Position, Is.EqualTo(0).Within(1e-6));
            Assert.That(pctCalc.Stops[1].Position, Is.EqualTo(0.3).Within(1e-6));

            // px stops resolve to a fraction of the gradient-line length. A
            // default (180deg, vertical) gradient in a 200x50 box has a 50px
            // line, so 10px → 0.2 and calc(20px+5px)=25px → 0.5.
            var pxCalc = Resolve("linear-gradient(red 10px, blue calc(20px + 5px))", new Rect(0, 0, 200, 50));
            Assert.That(pxCalc.Stops.Count, Is.EqualTo(2));
            Assert.That(pxCalc.Stops[0].Position, Is.EqualTo(10.0 / 50.0).Within(1e-6));
            Assert.That(pxCalc.Stops[1].Position, Is.EqualTo(25.0 / 50.0).Within(1e-6));

            var mixedCalc = Resolve("linear-gradient(red 0%, blue calc(50% + 5px))", new Rect(0, 0, 200, 50));
            Assert.That(mixedCalc.Stops.Count, Is.EqualTo(2));
            Assert.That(mixedCalc.Stops[1].Position, Is.EqualTo(5.5).Within(1e-6));

            Assert.DoesNotThrow(() => {
                var s = Style();
                s.Set("background-image", "linear-gradient(red, blue calc(red))");
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, new Rect(0, 0, 200, 50));
                Assert.That(brush, Is.Not.Null);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
                var lg = (LinearGradient)brush.GradientValue;
                Assert.That(lg.Stops.Count, Is.EqualTo(2));
                Assert.That(double.IsNaN(lg.Stops[0].Position) || double.IsFinite(lg.Stops[0].Position));
                Assert.That(double.IsNaN(lg.Stops[1].Position) || double.IsFinite(lg.Stops[1].Position));
            });
        }

        [Test]
        public void Linear_gradient_stops_accept_color_lab_lch_functional_forms() {
            LinearGradient Resolve(string image) {
                var s = Style();
                s.Set("background-image", image);
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, Bounds());
                Assert.That(brush, Is.Not.Null, image);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient), image);
                return (LinearGradient)brush.GradientValue;
            }

            var p3 = Resolve("linear-gradient(red, color(display-p3 0 1 0))");
            Assert.That(p3.Stops.Count, Is.EqualTo(2));
            Assert.That(p3.Stops[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(p3.Stops[1].Color.G, Is.GreaterThan(p3.Stops[1].Color.R));
            Assert.That(p3.Stops[1].Color.G, Is.GreaterThan(p3.Stops[1].Color.B));
            Assert.That(p3.Stops[1].Color.A, Is.GreaterThan(0.99f));

            var labLch = Resolve("linear-gradient(lab(50% 0 0), lch(50% 50 270))");
            Assert.That(labLch.Stops.Count, Is.EqualTo(2));
            Assert.That(labLch.Stops[0].Color.R, Is.EqualTo(labLch.Stops[0].Color.G).Within(0.05f));
            Assert.That(labLch.Stops[0].Color.G, Is.EqualTo(labLch.Stops[0].Color.B).Within(0.05f));
            Assert.That(labLch.Stops[0].Color.A, Is.GreaterThan(0.99f));
            Assert.That(labLch.Stops[1].Color.B, Is.GreaterThan(labLch.Stops[1].Color.R));
            Assert.That(labLch.Stops[1].Color.A, Is.GreaterThan(0.99f));

            var baseline = Resolve("linear-gradient(red, blue)");
            Assert.That(baseline.Stops.Count, Is.EqualTo(2));
            Assert.That(baseline.Stops[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(baseline.Stops[1].Color.B, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Repeating_conic_and_radial_gradients_set_IsRepeating_flag() {
            Gradient Resolve(string image) {
                var s = Style();
                s.Set("background-image", image);
                s.Set("color", "black");
                var brush = BackgroundResolver.ResolveBackground(s, new Rect(0, 0, 200, 100));
                Assert.That(brush, Is.Not.Null, image);
                Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient), image);
                return brush.GradientValue;
            }

            var repeatingConic = Resolve("repeating-conic-gradient(red 0deg 30deg, blue 30deg 60deg)");
            Assert.That(repeatingConic, Is.InstanceOf<ConicGradient>());
            Assert.That(((ConicGradient)repeatingConic).IsRepeating, Is.True);

            var repeatingRadial = Resolve("repeating-radial-gradient(circle at 50% 50%, red 0, blue 20px)");
            Assert.That(repeatingRadial, Is.InstanceOf<RadialGradient>());
            Assert.That(((RadialGradient)repeatingRadial).IsRepeating, Is.True);

            var plainConic = Resolve("conic-gradient(red, blue)");
            Assert.That(plainConic, Is.InstanceOf<ConicGradient>());
            Assert.That(((ConicGradient)plainConic).IsRepeating, Is.False);

            var plainRadial = Resolve("radial-gradient(circle at 50% 50%, red, blue)");
            Assert.That(plainRadial, Is.InstanceOf<RadialGradient>());
            Assert.That(((RadialGradient)plainRadial).IsRepeating, Is.False);
        }
    }
}
