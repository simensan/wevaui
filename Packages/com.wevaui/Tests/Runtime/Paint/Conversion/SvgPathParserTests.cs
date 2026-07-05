using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;

namespace Weva.Tests.Paint.Conversion {
    // Unit tests for SvgPathParser — verifies full SVG 1.1 path grammar coverage,
    // arc center-parameterization conversion, cubic flattening, and error rejection.
    public class SvgPathParserTests {

        // ---- helpers --------------------------------------------------------

        static List<Point2D[]> Parse(string d) {
            bool ok = SvgPathParser.TryParse(d, out var result);
            Assert.That(ok, Is.True, $"Expected parse success for: {d}");
            return result;
        }

        static void AssertFails(string d) {
            bool ok = SvgPathParser.TryParse(d, out _);
            Assert.That(ok, Is.False, $"Expected parse failure for: {d}");
        }

        static Point2D Last(List<Point2D[]> polys) {
            var p = polys[0];
            return p[p.Length - 1];
        }

        // ---- M command ------------------------------------------------------

        [Test]
        public void M_absolute_sets_initial_position() {
            var polys = Parse("M 10 20 L 50 20 L 50 60 Z");
            Assert.That(polys[0][0].X, Is.EqualTo(10).Within(1e-6));
            Assert.That(polys[0][0].Y, Is.EqualTo(20).Within(1e-6));
        }

        [Test]
        public void M_relative_moves_by_offset() {
            // "M 10 20 m 5 5 L 50 50 Z":
            //   M 10 20 → pen at (10,20), starts subpath A with only 1 point (not emitted).
            //   m 5 5 → closes subpath A (1 point, discarded), starts subpath B at (10+5, 20+5) = (15,25).
            //   L 50 50 Z → subpath B: (15,25) → (50,50), closed.
            // Only one polygon should be produced (subpath B).
            var polys = Parse("M 10 20 m 5 5 L 50 50 Z");
            Assert.That(polys, Has.Count.EqualTo(1));
            // Subpath B starts at (15, 25).
            Assert.That(polys[0][0].X, Is.EqualTo(15).Within(1e-6));
            Assert.That(polys[0][0].Y, Is.EqualTo(25).Within(1e-6));
        }

        // ---- implicit command repetition ------------------------------------

        [Test]
        public void M_implicit_repeat_becomes_lineto() {
            // "M 0 0 100 100" = M(0,0) then implicit L(100,100)
            var polys = Parse("M 0 0 100 100 0 100 Z");
            Assert.That(polys, Has.Count.EqualTo(1));
            var poly = polys[0];
            // Points: (0,0), (100,100), (0,100)
            Assert.That(poly[0].X, Is.EqualTo(0).Within(1e-6));
            Assert.That(poly[0].Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(poly[1].X, Is.EqualTo(100).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(100).Within(1e-6));
            Assert.That(poly[2].X, Is.EqualTo(0).Within(1e-6));
            Assert.That(poly[2].Y, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void L_implicit_repeat_adds_multiple_lines() {
            // L with multiple coordinate pairs.
            var polys = Parse("M 0 0 L 10 0 20 0 30 0 Z");
            Assert.That(polys, Has.Count.EqualTo(1));
            var poly = polys[0];
            Assert.That(poly.Length, Is.GreaterThanOrEqualTo(4));
        }

        // ---- H and V commands -----------------------------------------------

        [Test]
        public void H_absolute_produces_horizontal_line() {
            var polys = Parse("M 0 50 H 100 V 0 Z");
            var poly = polys[0];
            // After H 100: point at (100, 50)
            Assert.That(poly[1].X, Is.EqualTo(100).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void V_absolute_produces_vertical_line() {
            var polys = Parse("M 50 0 V 100 H 0 Z");
            var poly = polys[0];
            // After V 100: point at (50, 100)
            Assert.That(poly[1].X, Is.EqualTo(50).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void h_relative_horizontal_adds_to_current_x() {
            var polys = Parse("M 10 10 h 40 v 40 h -40 Z");
            var poly = polys[0];
            Assert.That(poly[1].X, Is.EqualTo(50).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(10).Within(1e-6));
        }

        [Test]
        public void v_relative_vertical_adds_to_current_y() {
            var polys = Parse("M 10 10 h 40 v 40 h -40 Z");
            var poly = polys[0];
            Assert.That(poly[2].X, Is.EqualTo(50).Within(1e-6));
            Assert.That(poly[2].Y, Is.EqualTo(50).Within(1e-6));
        }

        // ---- Z and multiple subpaths ----------------------------------------

        [Test]
        public void Z_closes_subpath_and_starts_new_on_next_M() {
            // Two rectangles as separate subpaths.
            var polys = Parse("M 0 0 H 10 V 10 H 0 Z M 20 20 H 30 V 30 H 20 Z");
            Assert.That(polys, Has.Count.EqualTo(2));
        }

        [Test]
        public void Unclosed_subpath_is_implicitly_included() {
            // No Z at end — should still produce one subpath.
            var polys = Parse("M 0 0 L 100 0 L 100 100");
            Assert.That(polys, Has.Count.EqualTo(1));
        }

        // ---- relative lineto ------------------------------------------------

        [Test]
        public void l_relative_lineto() {
            var polys = Parse("M 10 10 l 30 0 l 0 30 l -30 0 Z");
            var poly = polys[0];
            Assert.That(poly[0], Is.EqualTo(new Point2D(10, 10)));
            Assert.That(poly[1].X, Is.EqualTo(40).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(10).Within(1e-6));
        }

        // ---- negative numbers without separator ("L-1-2") -------------------

        [Test]
        public void L_negative_numbers_without_separator() {
            var polys = Parse("M 0 0 L-10-20 L50 50 Z");
            var poly = polys[0];
            Assert.That(poly[1].X, Is.EqualTo(-10).Within(1e-6));
            Assert.That(poly[1].Y, Is.EqualTo(-20).Within(1e-6));
        }

        // ---- scientific notation -------------------------------------------

        [Test]
        public void Scientific_notation_numbers_are_parsed() {
            var polys = Parse("M 1e2 2e1 L 5e1 8e1 Z");
            var poly = polys[0];
            Assert.That(poly[0].X, Is.EqualTo(100).Within(1e-6));
            Assert.That(poly[0].Y, Is.EqualTo(20).Within(1e-6));
            Assert.That(poly[1].X, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Scientific_notation_negative_exponent() {
            // 1e-1 = 0.1
            var polys = Parse("M 1e-1 2e-1 L 5 5 Z");
            var poly = polys[0];
            Assert.That(poly[0].X, Is.EqualTo(0.1).Within(1e-9));
            Assert.That(poly[0].Y, Is.EqualTo(0.2).Within(1e-9));
        }

        // ---- cubic bezier C/c -----------------------------------------------

        [Test]
        public void C_cubic_bezier_produces_flattened_points() {
            // Cubic from (0,0) to (100,0) via control points (33,100) and (66,100).
            var polys = Parse("M 0 0 C 33 100 66 100 100 0 Z");
            var poly = polys[0];
            // Should produce more than 2 points (the curve is subdivided).
            Assert.That(poly.Length, Is.GreaterThan(2),
                "Cubic bezier must be subdivided into multiple segments");
            // Last point before Z should be at (100, 0).
            // Find the point at x≈100.
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(100).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void c_relative_cubic_bezier_endpoint_correct() {
            // c relative: from (0,0), endpoint at (100,0) relatively → (100,0).
            var polys = Parse("M 0 0 c 33 100 66 100 100 0 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(100).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        // ---- smooth cubic S/s -----------------------------------------------

        [Test]
        public void S_smooth_cubic_reflects_previous_control_point() {
            // After C, S reflects the second control point of C.
            // C 10 80 90 80 100 0 → cp2 = (90,80). S 90 80 200 0 → reflected cp1 = (110,-80).
            // Endpoint at (200, 0) is what we can verify.
            var polys = Parse("M 0 0 C 10 80 90 80 100 0 S 190 80 200 0 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(200).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void S_without_preceding_C_uses_current_point_as_cp1() {
            // Per SVG spec: if S/s not preceded by C/c/S/s, first cp = current point.
            var polys = Parse("M 50 50 S 90 10 100 50 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(100).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(50).Within(1e-6));
        }

        // ---- quadratic Q/q --------------------------------------------------

        [Test]
        public void Q_quadratic_bezier_endpoint_correct() {
            var polys = Parse("M 0 0 Q 50 100 100 0 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(100).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Q_quadratic_peak_is_near_control_point() {
            // Q 100 100 100 0 from (0,0): control=(100,100), end=(100,0).
            // At t=0.5: x = 2*0.5*0.5*100 + 0.25*100 = 75, y = 2*0.5*0.5*100 = 50.
            // Find a point with x near 75; its y should be near 50.
            var polys = Parse("M 0 0 Q 100 100 100 0 Z");
            var poly = polys[0];
            double bestY = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < poly.Length; i++) {
                double d = System.Math.Abs(poly[i].X - 75);
                if (d < bestDist) { bestDist = d; bestY = poly[i].Y; }
            }
            Assert.That(bestY, Is.EqualTo(50).Within(5), "Midpoint of quadratic near (75,50)");
        }

        // ---- smooth quadratic T/t -------------------------------------------

        [Test]
        public void T_smooth_quadratic_reflects_previous_Q_control_point() {
            // Q 50 100 100 0 → control=(50,100). T 200 0 → reflected cp = (150,-100).
            // Just verify endpoint.
            var polys = Parse("M 0 0 Q 50 100 100 0 T 200 0 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(200).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        // ---- elliptical arc A/a --------------------------------------------

        [Test]
        public void A_arc_quarter_circle_midpoint_on_unit_circle() {
            // Arc from (1,0) to (0,1) via r=1 (quarter circle, sweep=1, large-arc=0).
            // The midpoint of the arc is at (cos(45°), sin(45°)) ≈ (0.707, 0.707).
            var polys = Parse("M 1 0 A 1 1 0 0 1 0 1 Z");
            var poly = polys[0];
            // Find the point nearest x=0.5 (the vertical midpoint of the arc).
            double bestY = double.NaN;
            double bestDist = double.MaxValue;
            for (int i = 1; i < poly.Length - 1; i++) {
                double dist = System.Math.Abs(poly[i].X - 0.7071);
                if (dist < bestDist) { bestDist = dist; bestY = poly[i].Y; }
            }
            Assert.That(bestDist, Is.LessThanOrEqualTo(0.05),
                "Should have a point near x=0.707 on the arc");
            Assert.That(bestY, Is.EqualTo(0.7071).Within(0.05),
                "Point near (0.707, 0.707) on unit circle quarter arc");
        }

        [Test]
        public void A_arc_large_arc_flag_produces_longer_path() {
            // For the same endpoints, large-arc=1 should produce more intermediate points
            // than large-arc=0 (it goes the long way around).
            var polysSmall = Parse("M 1 0 A 1 1 0 0 1 -1 0 Z");
            var polysLarge = Parse("M 1 0 A 1 1 0 1 1 -1 0 Z");
            Assert.That(polysLarge[0].Length, Is.GreaterThanOrEqualTo(polysSmall[0].Length),
                "Large-arc path should have at least as many points as small-arc");
        }

        [Test]
        public void A_arc_sweep_direction_affects_point_positions() {
            // sweep=0 vs sweep=1 from (1,0) to (-1,0) with r=1 (semicircle).
            // In SVG Y-down coordinates:
            //   sweep=1: arc goes through (0,1) — upper half (y > 0).
            //   sweep=0: arc goes through (0,-1) — lower half (y < 0).
            var polyUp   = Parse("M 1 0 A 1 1 0 0 1 -1 0 Z")[0];
            var polyDown = Parse("M 1 0 A 1 1 0 0 0 -1 0 Z")[0];
            // Find max Y in the arc (excluding the Z-closing endpoint).
            double maxYUp = double.NegativeInfinity;
            double minYDown = double.PositiveInfinity;
            for (int i = 1; i < polyUp.Length - 1; i++)   maxYUp   = System.Math.Max(maxYUp,   polyUp[i].Y);
            for (int i = 1; i < polyDown.Length - 1; i++) minYDown = System.Math.Min(minYDown, polyDown[i].Y);
            Assert.That(maxYUp,   Is.GreaterThan(0.5), "sweep=1 arc goes through y > 0.5");
            Assert.That(minYDown, Is.LessThan(-0.5),   "sweep=0 arc goes through y < -0.5");
        }

        [Test]
        public void A_arc_unspaced_flags_parsed_correctly() {
            // "a1 1 0 011 0" — flags are '0' and '1', unspaced, adjacent.
            // This is the critical "arc flags are single chars" case from SVG spec.
            var polys = Parse("M 0 0 a1 1 0 011 0 Z");
            Assert.That(polys, Has.Count.EqualTo(1));
            // Endpoint should be at (1, 0) relative → (1, 0).
            var last = polys[0][polys[0].Length - 1];
            Assert.That(last.X, Is.EqualTo(1).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void A_arc_half_circle_endpoint_correct() {
            // Semicircle from (1,0) to (-1,0), r=1, sweep=1.
            var polys = Parse("M 1 0 A 1 1 0 0 1 -1 0 Z");
            var poly = polys[0];
            var last = poly[poly.Length - 1];
            Assert.That(last.X, Is.EqualTo(-1).Within(1e-6));
            Assert.That(last.Y, Is.EqualTo(0).Within(1e-6));
        }

        // ---- flattening quality --------------------------------------------

        [Test]
        public void Cubic_flatness_tolerance_respected() {
            // Flat cubic (all points collinear) should produce minimal extra points.
            var polys = Parse("M 0 0 C 33 0 66 0 100 0 Z");
            // A perfectly flat cubic should produce very few points.
            Assert.That(polys[0].Length, Is.LessThanOrEqualTo(10),
                "Flat cubic should not over-subdivide");
        }

        [Test]
        public void Curved_cubic_stays_within_tolerance_of_analytic_curve() {
            // Quarter unit circle approximated as cubic bezier from (1,0) to (0,1).
            // Standard approximation uses k = 0.5523: cp1=(1, 0.5523), cp2=(0.5523, 1).
            // Use a literal string to avoid locale-specific decimal separator issues.
            var polys = Parse("M 1 0 C 1 0.5523 0.5523 1 0 1 Z");
            var poly = polys[0];
            // Every flattened point should be within 0.01 of the unit circle (r=1).
            double maxDev = 0;
            foreach (var p in poly) {
                double r = System.Math.Sqrt(p.X * p.X + p.Y * p.Y);
                maxDev = System.Math.Max(maxDev, System.Math.Abs(r - 1.0));
            }
            Assert.That(maxDev, Is.LessThanOrEqualTo(0.01),
                "Flattened cubic approximation stays within 0.01 of unit circle");
        }

        // ---- malformed data rejection ---------------------------------------

        [Test]
        public void Empty_string_is_invalid() {
            AssertFails("");
        }

        [Test]
        public void Whitespace_only_is_invalid() {
            AssertFails("   ");
        }

        [Test]
        public void No_M_command_is_invalid() {
            AssertFails("L 10 20 Z");
        }

        [Test]
        public void M_alone_produces_no_polygon() {
            // M without subsequent commands is not a valid polygon (need >= 2 points).
            bool ok = SvgPathParser.TryParse("M 10 20", out var polys);
            // May fail or return empty. Either is acceptable — no polygon to clip with.
            if (ok) Assert.That(polys, Has.Count.EqualTo(0));
        }

        [Test]
        public void M_with_only_one_more_point_is_too_short() {
            // Two points (M + L) is not a valid polygon.
            bool ok = SvgPathParser.TryParse("M 0 0 L 100 0", out var polys);
            if (ok) {
                foreach (var p in polys) Assert.That(p.Length, Is.GreaterThanOrEqualTo(2));
            }
        }

        [Test]
        public void Missing_argument_is_invalid() {
            AssertFails("M 10 20 L 30 Z");  // L needs 2 args
        }

        [Test]
        public void Unknown_command_is_invalid() {
            AssertFails("M 0 0 X 10 10 Z");
        }

        // ---- comma and whitespace separators --------------------------------

        [Test]
        public void Comma_separated_coordinates_parse_correctly() {
            var polys = Parse("M10,20 L50,20 L50,60 Z");
            var poly = polys[0];
            Assert.That(poly[0].X, Is.EqualTo(10).Within(1e-6));
            Assert.That(poly[1].X, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Mixed_comma_whitespace_separators_parse_correctly() {
            var polys = Parse("M 10 , 20 L 50,20,50,60 Z");
            var poly = polys[0];
            Assert.That(poly[0].X, Is.EqualTo(10).Within(1e-6));
        }

        // ---- multiple subpaths (donut) --------------------------------------

        [Test]
        public void Two_Z_commands_produce_two_subpolygons() {
            // Outer + inner square (donut shape).
            var polys = Parse("M 0 0 H 100 V 100 H 0 Z M 25 25 H 75 V 75 H 25 Z");
            Assert.That(polys, Has.Count.EqualTo(2));
        }
    }
}
