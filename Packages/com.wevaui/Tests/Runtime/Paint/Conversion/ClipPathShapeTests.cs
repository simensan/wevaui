using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Tests for clip-path: shape(...) — CSS Shapes 2 §3.
    // Covers:
    //  - Basic grammar: from + line to/by; hline/vline; curve (quadratic/cubic);
    //    smooth curve; arc (large/small + cw/ccw); close; multi-subpath via move.
    //  - Unit resolution: %, px, calc() against the reference box.
    //  - by vs to coordinate modes.
    //  - Fill-rule: nonzero (default) and evenodd.
    //  - Equivalence triangle pin: shape() == path() == polygon() on a pixel grid.
    //  - Bad input → null (missing from, malformed command, truncated args).
    //  - IsSupportedValue allowlist.
    public class ClipPathShapeTests {

        // ---- helpers --------------------------------------------------------

        static readonly Rect Box100 = new Rect(0, 0, 100, 100);
        static readonly Rect Box200x100 = new Rect(0, 0, 200, 100);
        static readonly Rect BoxOffset = new Rect(10, 20, 200, 100);

        static PathClipPathShape ResolveAsShape(string clipPath, Rect box) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            var shape = ClipPathResolver.Resolve(style, LengthContext.Default, box);
            Assert.That(shape, Is.Not.Null, $"shape() should resolve to a non-null shape: {clipPath}");
            Assert.That(shape, Is.TypeOf<PathClipPathShape>(),
                $"shape() must resolve to PathClipPathShape, got {shape?.GetType().Name ?? "null"}: {clipPath}");
            return (PathClipPathShape)shape;
        }

        static ClipPathShape ResolveAny(string clipPath, Rect box) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            return ClipPathResolver.Resolve(style, LengthContext.Default, box);
        }

        // ---- IsSupportedValue ----------------------------------------------

        [Test]
        public void IsSupportedValue_accepts_shape_function() {
            Assert.That(ClipPathResolver.IsSupportedValue("shape(from 0 0, line to 100 0, close)"), Is.True);
        }

        [Test]
        public void IsSupportedValue_accepts_shape_with_fill_rule() {
            Assert.That(ClipPathResolver.IsSupportedValue("shape(evenodd, from 0 0, line to 100 0, close)"), Is.True);
        }

        // ---- direct parser call (bypasses resolver) -------------------------

        [Test]
        public void Direct_parser_call_succeeds_for_valid_body() {
            // Call ShapeCommandParser.TryParse directly (bypassing ClipPathResolver)
            // to verify the parser works independently of the resolver path.
            bool ok = ShapeCommandParser.TryParse(
                "from 0px 0px, line to 100px 0px, line to 50px 100px, close",
                LengthContext.Default,
                Box100,
                out ClipPathShape directShape);
            Assert.That(ok, Is.True, "direct ShapeCommandParser.TryParse must return true for valid body");
            Assert.That(directShape, Is.Not.Null, "direct call must produce a non-null shape");
        }

        [Test]
        public void Open_path_without_close_is_valid() {
            // An open path (no close command) with two or more points is valid —
            // the sub-polygon is flushed at the end of parsing (same as SvgPathParser).
            bool ok = ShapeCommandParser.TryParse(
                "from 0px 0px, line to 100px 100px",
                LengthContext.Default,
                Box100,
                out ClipPathShape s);
            Assert.That(ok, Is.True, "open path with from + line should produce a valid shape");
            Assert.That(s, Is.Not.Null);
        }

        // ---- basic grammar: from + line to ----------------------------------

        [Test]
        public void Triangle_line_to_center_is_inside() {
            // Triangle: (0,0) → (100,0) → (50,100) → close
            var shape = ResolveAsShape("shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.Contains(50, 40), Is.True, "center of triangle is inside");
        }

        [Test]
        public void Triangle_line_to_corner_outside_is_excluded() {
            var shape = ResolveAsShape("shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.Contains(99, 99), Is.False, "far bottom-right is outside triangle");
            Assert.That(shape.Contains(1, 99), Is.False, "bottom-left corner is outside triangle");
        }

        [Test]
        public void Triangle_line_by_relative_coords() {
            // Same triangle but using 'by' for deltas: start (0,0), +100 x, +50x+100y (relative).
            // from 0 0 → (0,0)
            // line by 100px 0px  → (100,0)
            // line by -50px 100px → (50,100)
            var shape = ResolveAsShape("shape(from 0px 0px, line by 100px 0px, line by -50px 100px, close)", Box100);
            Assert.That(shape.Contains(50, 40), Is.True, "by-relative triangle center inside");
            Assert.That(shape.Contains(99, 99), Is.False, "by-relative corner outside");
        }

        // ---- hline and vline -----------------------------------------------

        [Test]
        public void Rectangle_hline_vline_contains_center() {
            // Rectangle via hline/vline: from (10,10), hline to 90, vline to 90, hline to 10, close
            var shape = ResolveAsShape(
                "shape(from 10px 10px, hline to 90px, vline to 90px, hline to 10px, close)",
                Box100);
            Assert.That(shape.Contains(50, 50), Is.True, "center inside rectangle via hline/vline");
            Assert.That(shape.Contains(5, 50), Is.False, "left of rectangle is outside");
        }

        [Test]
        public void Hline_vline_by_relative() {
            // Same rectangle using 'by' offsets.
            var shape = ResolveAsShape(
                "shape(from 10px 10px, hline by 80px, vline by 80px, hline by -80px, close)",
                Box100);
            Assert.That(shape.Contains(50, 50), Is.True, "by-relative hline/vline rectangle center inside");
            Assert.That(shape.Contains(5, 50), Is.False, "outside left edge");
        }

        // ---- percentage coords resolve against reference box ---------------

        [Test]
        public void Percentage_x_resolves_against_box_width() {
            // On a 200×100 box, line to 50% 50% → absolute (100, 50).
            // Triangle: from 0 0, line to 50% 0%, line to 50% 50%, close
            var shape = ResolveAsShape(
                "shape(from 0% 0%, line to 100% 0%, line to 50% 100%, close)",
                Box200x100);
            // The triangle spans the full box 200×100. Center at (100, 50) should be inside.
            Assert.That(shape.Contains(100, 50), Is.True, "percentage 50%x/50%y resolves to box center");
            Assert.That(shape.Contains(190, 10), Is.False, "top-right corner outside triangle");
        }

        [Test]
        public void Percentage_50pct_on_200x100_box_produces_correct_point() {
            // line to 50% 50% on Box200x100 → x=100, y=50.
            // Square from (0,0) to (100,50): line to 50% 0%, line to 50% 50%, line to 0% 50%
            var shape = ResolveAsShape(
                "shape(from 0% 0%, line to 50% 0%, line to 50% 50%, line to 0% 50%, close)",
                Box200x100);
            // Rectangle covers x∈[0,100], y∈[0,50].
            Assert.That(shape.Contains(50, 25), Is.True, "center of half-box rectangle inside");
            Assert.That(shape.Contains(150, 25), Is.False, "right half outside");
        }

        // ---- calc() coords -------------------------------------------------

        [Test]
        public void Calc_coords_resolve_correctly() {
            // line to calc(50px + 25px) = 75px
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to calc(50px + 25px) 0px, line to 50px 100px, close)",
                Box100);
            // Triangle: (0,0)-(75,0)-(50,100). Point (40,20) is inside.
            Assert.That(shape.Contains(40, 20), Is.True, "calc() coord resolves and point is inside");
        }

        // ---- curve (quadratic and cubic) -----------------------------------

        [Test]
        public void Curve_quadratic_one_control_point_produces_non_null() {
            // curve to <endpoint> with <cp>  → quadratic bezier.
            // Path: (0,50) → quadratic arc with cp (50,0) → (100,50) → close.
            // The quadratic Bezier midpoint at t=0.5 is at y=25 (not 0 — the cp
            // is NOT on the curve; it's a quadratic handle). The arc peak is (50,25).
            // The closed shape is a "lens" bounded by the arc on top (y≈25..50) and
            // the closing segment at y=50. Point (50, 35) is inside.
            var shape = ResolveAsShape(
                "shape(from 0px 50px, curve to 100px 50px with 50px 0px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "quadratic curve should produce a shape");
            Assert.That(shape.Kind, Is.EqualTo(ClipPathShapeKind.Path), "shape kind must be Path");
            // Arc peak is at approximately (50, 25). Point (50, 35) is between the arc
            // and the y=50 baseline closing segment → inside.
            Assert.That(shape.Contains(50, 35), Is.True, "inside quadratic curve arc");
            // Point above the arc peak is outside.
            Assert.That(shape.Contains(50, 15), Is.False, "above arc peak is outside");
        }

        [Test]
        public void Curve_cubic_two_control_points_produces_non_null() {
            // curve to <endpoint> with <cp1> / <cp2>  → cubic bezier.
            // Path: (0,50) → cubic with cp1=(25,0) cp2=(75,0) → (100,50) → close.
            // Cubic midpoint at t=0.5 is at y=12.5. Arc peak is around y=12..15.
            // The closed lens shape spans roughly y=[12.5..50].
            // Point (50, 20) is between the arc peak and y=50 → inside.
            var shape = ResolveAsShape(
                "shape(from 0px 50px, curve to 100px 50px with 25px 0px / 75px 0px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "cubic curve should produce a shape");
            Assert.That(shape.Kind, Is.EqualTo(ClipPathShapeKind.Path), "shape kind must be Path");
            // Arc peak ~ y=12.5. Point (50, 20) is inside the lens.
            Assert.That(shape.Contains(50, 20), Is.True, "inside cubic curve arc");
            // Point above the arc peak is outside.
            Assert.That(shape.Contains(50, 5), Is.False, "above arc peak is outside");
        }

        // ---- smooth curve --------------------------------------------------

        [Test]
        public void Smooth_without_with_acts_as_smooth_quadratic() {
            // After a curve (quadratic) command, smooth reflects the quadratic cp.
            // Two-segment ribbon: first quadratic cp (25,0), then reflected smooth.
            var shape = ResolveAsShape(
                "shape(from 0px 50px, curve to 50px 50px with 25px 0px, smooth to 100px 50px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "smooth without 'with' (smooth quadratic) should parse");
            Assert.That(shape.Kind, Is.EqualTo(ClipPathShapeKind.Path), "kind must be Path");
            // The shape has valid subpolygon data (curves flattened into segments).
            Assert.That(shape.SubPolygons.Count, Is.GreaterThanOrEqualTo(1),
                "smooth quadratic should produce at least one subpolygon");
            // The bounding box must encompass the curve geometry.
            Assert.That(shape.Bounds.Width, Is.GreaterThan(0), "non-zero width");
            Assert.That(shape.Bounds.Height, Is.GreaterThan(0), "non-zero height");
        }

        [Test]
        public void Smooth_with_explicit_cp_acts_as_smooth_cubic() {
            // After a curve (cubic) command, smooth reflects cp2 and adds explicit cp2.
            var shape = ResolveAsShape(
                "shape(from 0px 50px, curve to 50px 50px with 15px 0px / 35px 0px, smooth to 100px 50px with 85px 0px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "smooth with 'with' (smooth cubic) should parse");
        }

        // ---- arc (all four large/small + cw/ccw combos) --------------------

        [Test]
        public void Arc_small_ccw_produces_non_null() {
            var shape = ResolveAsShape(
                "shape(from 50px 0px, arc to 100px 50px of 50px small ccw, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "arc small ccw");
        }

        [Test]
        public void Arc_small_cw_produces_non_null() {
            var shape = ResolveAsShape(
                "shape(from 50px 0px, arc to 100px 50px of 50px small cw, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "arc small cw");
        }

        [Test]
        public void Arc_large_ccw_produces_non_null() {
            var shape = ResolveAsShape(
                "shape(from 50px 0px, arc to 100px 50px of 50px large ccw, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "arc large ccw");
        }

        [Test]
        public void Arc_large_cw_produces_non_null() {
            var shape = ResolveAsShape(
                "shape(from 50px 0px, arc to 100px 50px of 50px large cw, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "arc large cw");
        }

        [Test]
        public void Arc_with_separate_rx_ry_produces_non_null() {
            // of <rx> <ry> — elliptical arc with two radii.
            var shape = ResolveAsShape(
                "shape(from 0px 50px, arc to 100px 50px of 60px 30px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null, "arc with rx ry produces shape");
        }

        // ---- close ---------------------------------------------------------

        [Test]
        public void Close_without_trailing_commands_is_valid() {
            // shape() with only a triangle and close, no further commands.
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)",
                Box100);
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape.SubPolygons.Count, Is.GreaterThanOrEqualTo(1));
        }

        // ---- multi-subpath via move ----------------------------------------

        [Test]
        public void Move_command_starts_new_subpath() {
            // Two subpaths: top square (0,0)→(50,0)→(50,50)→(0,50), then
            // bottom square (60,60)→(100,60)→(100,100)→(60,100).
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to 50px 0px, line to 50px 50px, line to 0px 50px, close," +
                " move to 60px 60px, line to 100px 60px, line to 100px 100px, line to 60px 100px, close)",
                Box100);
            Assert.That(shape.SubPolygons.Count, Is.EqualTo(2), "two subpaths from move");
            Assert.That(shape.Contains(25, 25), Is.True, "inside first subpath");
            Assert.That(shape.Contains(80, 80), Is.True, "inside second subpath");
            Assert.That(shape.Contains(55, 55), Is.False, "gap between subpaths");
        }

        // ---- fill-rule parsing ---------------------------------------------

        [Test]
        public void Default_fill_rule_is_nonzero() {
            var shape = ResolveAsShape("shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
        }

        [Test]
        public void Evenodd_prefix_sets_fill_rule() {
            var shape = ResolveAsShape("shape(evenodd, from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Evenodd));
        }

        [Test]
        public void Nonzero_prefix_sets_fill_rule() {
            var shape = ResolveAsShape("shape(nonzero, from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
        }

        // ---- evenodd donut (same winding) ----------------------------------

        // Two concentric squares, both wound the same way.
        // Under evenodd the inner square is a hole; under nonzero it is filled.

        [Test]
        public void Shape_evenodd_donut_hole_is_outside() {
            const string clipPath =
                "shape(evenodd, from 0px 0px," +
                " line to 100px 0px, line to 100px 100px, line to 0px 100px, close," +
                " move to 25px 25px," +
                " line to 75px 25px, line to 75px 75px, line to 25px 75px, close)";
            var shape = ResolveAsShape(clipPath, Box100);
            Assert.That(shape.Contains(10, 50), Is.True, "outer ring is inside under evenodd");
            Assert.That(shape.Contains(50, 50), Is.False, "inner square is a hole under evenodd");
        }

        [Test]
        public void Shape_nonzero_donut_inner_is_inside() {
            const string clipPath =
                "shape(nonzero, from 0px 0px," +
                " line to 100px 0px, line to 100px 100px, line to 0px 100px, close," +
                " move to 25px 25px," +
                " line to 75px 25px, line to 75px 75px, line to 25px 75px, close)";
            var shape = ResolveAsShape(clipPath, Box100);
            Assert.That(shape.Contains(10, 50), Is.True, "outer ring inside under nonzero");
            Assert.That(shape.Contains(50, 50), Is.True, "inner square inside under nonzero (same winding)");
        }

        // ---- border-box anchoring ------------------------------------------

        [Test]
        public void Shape_anchors_at_border_box_origin() {
            // Box at (10,20). A full-box rectangle in shape() coords (0,0)→(200,100)
            // should contain world point (110, 70) = (10+100, 20+50).
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to 200px 0px, line to 200px 100px, line to 0px 100px, close)",
                BoxOffset);
            Assert.That(shape.Contains(110, 70), Is.True, "box-origin offset point is inside");
            Assert.That(shape.Contains(5, 10), Is.False, "point before box origin is outside");
        }

        // ---- bad values → null ---------------------------------------------

        [Test]
        public void Missing_from_returns_null() {
            var shape = ResolveAny("shape(line to 100px 0px, close)", Box100);
            Assert.That(shape, Is.Null, "shape() without 'from' must be invalid");
        }

        [Test]
        public void Missing_from_args_returns_null() {
            var shape = ResolveAny("shape(from 0px, line to 100px 0px, close)", Box100);
            Assert.That(shape, Is.Null, "from with only one coordinate must be invalid");
        }

        [Test]
        public void Unknown_command_returns_null() {
            var shape = ResolveAny("shape(from 0px 0px, jump 100px 0px, close)", Box100);
            Assert.That(shape, Is.Null, "unknown command must invalidate whole shape()");
        }

        [Test]
        public void Malformed_length_in_line_returns_null() {
            // 'abc' is not a valid length.
            var shape = ResolveAny("shape(from 0px 0px, line to abc 0px, close)", Box100);
            Assert.That(shape, Is.Null, "non-length value in line command must be invalid");
        }

        [Test]
        public void Arc_missing_of_keyword_returns_null() {
            var shape = ResolveAny("shape(from 0px 0px, arc to 100px 100px 50px, close)", Box100);
            Assert.That(shape, Is.Null, "arc without 'of' keyword must be invalid");
        }

        [Test]
        public void Curve_missing_with_keyword_returns_null() {
            var shape = ResolveAny("shape(from 0px 0px, curve to 100px 100px 50px 50px, close)", Box100);
            Assert.That(shape, Is.Null, "curve without 'with' keyword must be invalid");
        }

        [Test]
        public void Empty_body_returns_null() {
            var shape = ResolveAny("shape()", Box100);
            Assert.That(shape, Is.Null, "empty shape() body must be invalid");
        }

        // ---- triangle equivalence pin: shape() == path() == polygon() ------
        //
        // This is the key regression guard: a triangle described via shape(),
        // path(), and polygon() must produce identical Contains() results on
        // a dense pixel grid. Proves that shape() reuses the proven geometry
        // pipeline without introducing coordinate divergence.

        [Test]
        public void Triangle_shape_matches_path_on_pixel_grid() {
            // Triangle via shape(): (0,0)-(100,0)-(50,100).
            var shapeViaShape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)",
                Box100);
            // Same triangle via path().
            var styleForPath = new ComputedStyle(new Element("div"));
            styleForPath.Set("clip-path", "path(\"M 0 0 L 100 0 L 50 100 Z\")");
            var shapeViaPath = (PathClipPathShape)ClipPathResolver.Resolve(
                styleForPath, LengthContext.Default, Box100);

            int disagree = 0;
            for (int y = 5; y < 100; y += 10) {
                for (int x = 5; x < 100; x += 10) {
                    double px = x + 0.5, py = y + 0.5;
                    bool viaShape = shapeViaShape.Contains(px, py);
                    bool viaPath  = shapeViaPath.Contains(px, py);
                    if (viaShape != viaPath) disagree++;
                }
            }
            Assert.That(disagree, Is.EqualTo(0),
                "shape() and path() must agree on all sampled pixels for the same triangle");
        }

        [Test]
        public void Triangle_shape_matches_polygon_on_pixel_grid() {
            // Same triangle via shape() and polygon().
            var shapeViaShape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)",
                Box100);
            var styleForPoly = new ComputedStyle(new Element("div"));
            styleForPoly.Set("clip-path", "polygon(0% 0%, 100% 0%, 50% 100%)");
            var shapeViaPoly = (PolygonClipPathShape)ClipPathResolver.Resolve(
                styleForPoly, LengthContext.Default, Box100);

            int disagree = 0;
            for (int y = 5; y < 100; y += 10) {
                for (int x = 5; x < 100; x += 10) {
                    double px = x + 0.5, py = y + 0.5;
                    bool viaShape = shapeViaShape.Contains(px, py);
                    bool viaPoly  = shapeViaPoly.Contains(px, py);
                    if (viaShape != viaPoly) disagree++;
                }
            }
            Assert.That(disagree, Is.EqualTo(0),
                "shape() and polygon() must agree on all sampled pixels for the same triangle");
        }

        [Test]
        public void Triangle_all_three_agree_on_dense_pixel_grid() {
            // Dense pixel-center sampling across the full 100×100 box.
            var shapeViaShape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)",
                Box100);

            var styleForPath = new ComputedStyle(new Element("div"));
            styleForPath.Set("clip-path", "path(\"M 0 0 L 100 0 L 50 100 Z\")");
            var shapeViaPath = (PathClipPathShape)ClipPathResolver.Resolve(
                styleForPath, LengthContext.Default, Box100);

            var styleForPoly = new ComputedStyle(new Element("div"));
            styleForPoly.Set("clip-path", "polygon(0px 0px, 100px 0px, 50px 100px)");
            var shapeViaPoly = (PolygonClipPathShape)ClipPathResolver.Resolve(
                styleForPoly, LengthContext.Default, Box100);

            int disagree = 0;
            for (int y = 0; y <= 100; y += 2) {
                for (int x = 0; x <= 100; x += 2) {
                    double px = x + 0.5, py = y + 0.5;
                    bool vs = shapeViaShape.Contains(px, py);
                    bool vp = shapeViaPath.Contains(px, py);
                    bool vq = shapeViaPoly.Contains(px, py);
                    if (vs != vp || vs != vq) disagree++;
                }
            }
            Assert.That(disagree, Is.EqualTo(0),
                "shape(), path(), and polygon() must all agree on every pixel for the same triangle");
        }

        // ---- Kind + Bounds -------------------------------------------------

        [Test]
        public void Kind_is_Path() {
            var shape = ResolveAsShape("shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)", Box100);
            Assert.That(shape.Kind, Is.EqualTo(ClipPathShapeKind.Path));
        }

        [Test]
        public void Bounds_are_correct_for_triangle() {
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 50px 100px, close)",
                Box100);
            // Bounding box of triangle (0,0)-(100,0)-(50,100): x=0..100, y=0..100.
            var bounds = shape.Bounds;
            Assert.That(bounds.X, Is.EqualTo(0).Within(0.5));
            Assert.That(bounds.Y, Is.EqualTo(0).Within(0.5));
            Assert.That(bounds.Width,  Is.EqualTo(100).Within(1.0));
            Assert.That(bounds.Height, Is.EqualTo(100).Within(1.0));
        }

        // ---- Translate preserves geometry ----------------------------------

        [Test]
        public void Translate_shifts_shape_by_offset() {
            var shape = ResolveAsShape(
                "shape(from 0px 0px, line to 100px 0px, line to 100px 100px, line to 0px 100px, close)",
                Box100);
            Assert.That(shape.Contains(50, 50), Is.True);
            var translated = (PathClipPathShape)shape.Translate(200, 200);
            Assert.That(translated.Contains(50, 50), Is.False, "after translate, (50,50) is outside");
            Assert.That(translated.Contains(250, 250), Is.True, "after translate, (250,250) is inside");
        }
    }
}
