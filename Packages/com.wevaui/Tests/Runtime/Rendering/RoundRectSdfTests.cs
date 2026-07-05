using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class RoundRectSdfTests {
        const double Eps = 1e-3;

        [Test]
        public void Sdf_at_center_is_most_negative() {
            double center = RoundRectSdf.Sample(0, 0, 50, 30, 8);
            double offCenter = RoundRectSdf.Sample(20, 10, 50, 30, 8);
            Assert.That(center, Is.LessThan(offCenter));
            Assert.That(center, Is.LessThan(0));
        }

        [Test]
        public void Sdf_at_edge_is_zero_within_epsilon() {
            // On the right edge, far from any rounded corner — distance should be 0.
            double d = RoundRectSdf.Sample(50, 0, 50, 30, 8);
            Assert.That(d, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Sdf_far_outside_approaches_distance() {
            double d = RoundRectSdf.Sample(150, 0, 50, 30, 8);
            // Distance from (150, 0) to the right edge of a [-50..50] x [-30..30] box with
            // small radius is ~ 100 px (modulo the radius pull-in).
            Assert.That(d, Is.GreaterThan(90));
            Assert.That(d, Is.LessThan(105));
        }

        [Test]
        public void Sdf_inside_far_from_edge_is_strongly_negative() {
            double d = RoundRectSdf.Sample(0, 0, 100, 100, 4);
            Assert.That(d, Is.LessThan(-90));
        }

        [Test]
        public void Sdf_zero_radius_matches_axis_aligned_box() {
            // At a corner of a box with no radius, distance to the boundary is 0.
            double d = RoundRectSdf.Sample(50, 30, 50, 30, 0);
            Assert.That(d, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void PerCorner_picks_top_right_for_positive_x_negative_y() {
            // Top-right corner has a much larger radius. Sampling near that corner should
            // produce a noticeably different SDF than sampling near top-left.
            double tr = RoundRectSdf.SamplePerCorner(40, -25, 50, 30,
                rTopLeftX: 4, rTopLeftY: 4,
                rTopRightX: 20, rTopRightY: 20,
                rBottomRightX: 4, rBottomRightY: 4,
                rBottomLeftX: 4, rBottomLeftY: 4);
            double tl = RoundRectSdf.SamplePerCorner(-40, -25, 50, 30,
                rTopLeftX: 4, rTopLeftY: 4,
                rTopRightX: 20, rTopRightY: 20,
                rBottomRightX: 4, rBottomRightY: 4,
                rBottomLeftX: 4, rBottomLeftY: 4);
            Assert.That(tr, Is.Not.EqualTo(tl).Within(0.1));
        }

        [Test]
        public void PickCornerRadii_returns_correct_corner() {
            var (rx, ry) = RoundRectSdf.PickCornerRadii(40, -25,
                1, 1, 2, 2, 3, 3, 4, 4);
            Assert.That(rx, Is.EqualTo(2));
            Assert.That(ry, Is.EqualTo(2));

            (rx, ry) = RoundRectSdf.PickCornerRadii(-40, 25,
                1, 1, 2, 2, 3, 3, 4, 4);
            Assert.That(rx, Is.EqualTo(4));
            Assert.That(ry, Is.EqualTo(4));
        }

        [Test]
        public void Coverage_smoothsteps_across_one_pixel_band() {
            double full = RoundRectSdf.Coverage(-10);
            double zero = RoundRectSdf.Coverage(10);
            double mid = RoundRectSdf.Coverage(0);
            Assert.That(full, Is.EqualTo(1).Within(1e-6));
            Assert.That(zero, Is.EqualTo(0).Within(1e-6));
            Assert.That(mid, Is.EqualTo(0.5).Within(1e-6));
        }

        [Test]
        public void Coverage_is_monotonic() {
            double a = RoundRectSdf.Coverage(-0.4);
            double b = RoundRectSdf.Coverage(-0.2);
            double c = RoundRectSdf.Coverage(0.0);
            double d = RoundRectSdf.Coverage(0.2);
            Assert.That(a, Is.GreaterThanOrEqualTo(b));
            Assert.That(b, Is.GreaterThanOrEqualTo(c));
            Assert.That(c, Is.GreaterThanOrEqualTo(d));
        }

        [Test]
        public void PackUniform_matches_for_uniform_radii() {
            var radii = BorderRadii.Uniform(8);
            var (rx, ry) = RoundRectSdf.PackUniform(radii);
            Assert.That(rx, Is.EqualTo(8f));
            Assert.That(ry, Is.EqualTo(8f));
            Assert.That(RoundRectSdf.HasUniformRadii(radii), Is.True);
        }

        [Test]
        public void PackUniform_falls_back_to_max_for_mixed() {
            var radii = new BorderRadii(
                new CornerRadius(2, 2),
                new CornerRadius(8, 4),
                new CornerRadius(1, 1),
                new CornerRadius(0, 0));
            var (rx, ry) = RoundRectSdf.PackUniform(radii);
            Assert.That(rx, Is.EqualTo(8f));
            Assert.That(ry, Is.EqualTo(4f));
            Assert.That(RoundRectSdf.HasUniformRadii(radii), Is.False);
        }

        [Test]
        public void Straight_edge_distance_is_independent_of_corner_radii() {
            // Regression for load-game's `.thumb` seam: a 300x132 box (halfW=150,
            // halfH=66) with adjacent corners of different radii (TL 14x40, TR
            // 70x30). A point on the top STRAIGHT edge, away from both corners,
            // must have the same signed distance regardless of which corner's
            // radii is sampled — otherwise the shared edge lands at different
            // sub-pixel positions either side of centre and a vertical seam
            // appears down the fill.
            double withTL = RoundRectSdf.SamplePerAxis(0, -60, 150, 66, 14, 40);
            double withTR = RoundRectSdf.SamplePerAxis(0, -60, 150, 66, 70, 30);
            Assert.That(withTL, Is.EqualTo(withTR).Within(Eps),
                "straight-edge distance must not depend on corner radii");
            // …and it equals the true perpendicular distance: 6px inside the top edge.
            Assert.That(withTL, Is.EqualTo(-6).Within(Eps));
        }

        [Test]
        public void PerAxis_is_continuous_across_corner_quadrant_boundary() {
            // TR corner 70x30 on a 300x132 box: the elliptical corner quadrant
            // begins at |localX| = halfW - rx = 80. The SDF must be continuous
            // there — the gradient-corrected ellipse meets the box SDF exactly.
            double justEdge = RoundRectSdf.SamplePerAxis(79, -60, 150, 66, 70, 30);
            double justCorner = RoundRectSdf.SamplePerAxis(81, -60, 150, 66, 70, 30);
            Assert.That(justCorner, Is.EqualTo(justEdge).Within(0.2),
                "no discontinuity at the corner-quadrant boundary");
        }

        [Test]
        public void PerAxis_reduces_to_circular_when_axes_equal() {
            // rx == ry must be bit-for-bit the circular SDF so symmetric corners
            // never regress.
            for (int i = 0; i < 4; i++) {
                double lx = new[] { 0, 45, 50, 60 }[i];
                double ly = new[] { 0, 28, 30, 35 }[i];
                double perAxis = RoundRectSdf.SamplePerAxis(lx, ly, 50, 30, 8, 8);
                double circular = RoundRectSdf.Sample(lx, ly, 50, 30, 8);
                Assert.That(perAxis, Is.EqualTo(circular).Within(1e-9),
                    $"per-axis must equal circular at ({lx},{ly})");
            }
        }
    }
}
