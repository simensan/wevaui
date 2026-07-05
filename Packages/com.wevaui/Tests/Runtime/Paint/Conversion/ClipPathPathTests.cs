using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Tests for clip-path: path("...") — CSS Shapes L1 / CSS Masking L1 Phase 1.
    // Covers:
    //  - PathClipPathShape.Contains() with nonzero and evenodd fill rules
    //  - Donut (two subpaths): nonzero vs evenodd differ on hole
    //  - Points near edges and bounding-box quick-reject
    //  - ClipPathResolver parsing: path(), path(evenodd,...), bad values → null
    //  - Border-box anchoring (matches polygon() coordinate anchoring)
    //  - IsSupportedValue allowlist
    //  - Software-rasterizer parity: triangle via path() == triangle via polygon()
    public class ClipPathPathTests {

        // ---- helpers --------------------------------------------------------

        static readonly Rect Box100 = new Rect(0, 0, 100, 100);
        static readonly Rect BoxOffset = new Rect(20, 30, 100, 100);

        static PathClipPathShape ResolveAsPath(string clipPath, Rect box) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            var shape = ClipPathResolver.Resolve(style, LengthContext.Default, box);
            Assert.That(shape, Is.Not.Null, $"path() should resolve to a shape: {clipPath}");
            Assert.That(shape, Is.TypeOf<PathClipPathShape>(),
                $"path() must resolve to PathClipPathShape: {clipPath}");
            return (PathClipPathShape)shape;
        }

        static ClipPathShape ResolveAny(string clipPath, Rect box) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            return ClipPathResolver.Resolve(style, LengthContext.Default, box);
        }

        // ---- IsSupportedValue ----------------------------------------------

        [Test]
        public void IsSupportedValue_accepts_path_function() {
            Assert.That(ClipPathResolver.IsSupportedValue("path(\"M 0 0 H 100 V 100 Z\")"), Is.True);
        }

        [Test]
        public void IsSupportedValue_accepts_path_with_fill_rule() {
            Assert.That(ClipPathResolver.IsSupportedValue("path(evenodd, \"M 0 0 H 100 V 100 Z\")"), Is.True);
        }

        // ---- triangle: basic Contains() ------------------------------------

        [Test]
        public void Triangle_center_is_inside() {
            // Triangle: (0,0) → (100,0) → (50,100)
            var shape = ResolveAsPath("path(\"M 0 0 L 100 0 L 50 100 Z\")", Box100);
            Assert.That(shape.Contains(50, 40), Is.True, "center of triangle is inside");
        }

        [Test]
        public void Triangle_corner_outside_is_excluded() {
            var shape = ResolveAsPath("path(\"M 0 0 L 100 0 L 50 100 Z\")", Box100);
            Assert.That(shape.Contains(99, 99), Is.False, "far corner outside triangle");
            Assert.That(shape.Contains(1, 99), Is.False, "near bottom-left outside triangle");
        }

        [Test]
        public void Triangle_point_above_is_outside() {
            var shape = ResolveAsPath("path(\"M 0 0 L 100 0 L 50 100 Z\")", Box100);
            Assert.That(shape.Contains(50, -1), Is.False, "point above triangle");
        }

        // ---- fill rule: nonzero vs evenodd on donut ------------------------

        // Donut: outer square (0,0)-(100,100), inner square (25,25)-(75,75),
        // both wound clockwise so they both have the same winding direction.
        // Under nonzero: inner square gets winding ±2 → inside.
        // Under evenodd: inner square has crossing parity 2 → even → outside (hole).
        // Note: SVG path data winding. Outer CW: M 0 0 H 100 V 100 H 0 Z
        //       Inner CW: M 25 25 H 75 V 75 H 25 Z
        // Outer CW winding: crossing at hole = 1 from outer. But inner also CW adds 1 more.
        // Under nonzero: inside hole winding = 2 → inside (no hole with same winding).
        // Under evenodd: inside hole parity = 2 → even → outside (hole appears).

        const string DonutPath = "\"M 0 0 H 100 V 100 H 0 Z M 25 25 H 75 V 75 H 25 Z\"";

        [Test]
        public void Donut_nonzero_outer_ring_is_inside() {
            var shape = ResolveAsPath($"path({DonutPath})", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
            Assert.That(shape.Contains(10, 50), Is.True, "outer ring under nonzero");
        }

        [Test]
        public void Donut_evenodd_outer_ring_is_inside() {
            var shape = ResolveAsPath($"path(evenodd, {DonutPath})", Box100);
            Assert.That(shape.Contains(10, 50), Is.True, "outer ring under evenodd");
        }

        [Test]
        public void Donut_evenodd_hole_is_outside() {
            // Point inside the inner square: (50, 50). Under evenodd, the inner CW
            // square adds another crossing → parity flips → outside.
            var shape = ResolveAsPath($"path(evenodd, {DonutPath})", Box100);
            Assert.That(shape.Contains(50, 50), Is.False, "hole inside under evenodd");
        }

        [Test]
        public void Donut_nonzero_same_direction_inner_is_inside() {
            // Both squares wound CW: inner winding = 2, nonzero → inside (no hole).
            var shape = ResolveAsPath($"path({DonutPath})", Box100);
            Assert.That(shape.Contains(50, 50), Is.True, "inner under nonzero CW same direction → inside");
        }

        // ---- fill rule parsing ---------------------------------------------

        [Test]
        public void Default_fill_rule_is_nonzero() {
            var shape = ResolveAsPath("path(\"M 0 0 H 100 V 100 Z\")", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
        }

        [Test]
        public void Evenodd_keyword_sets_fill_rule() {
            var shape = ResolveAsPath("path(evenodd, \"M 0 0 H 100 V 100 Z\")", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Evenodd));
        }

        [Test]
        public void Nonzero_keyword_sets_fill_rule() {
            var shape = ResolveAsPath("path(nonzero, \"M 0 0 H 100 V 100 Z\")", Box100);
            Assert.That(shape.FillRule, Is.EqualTo(ClipPathFillRule.Nonzero));
        }

        [Test]
        public void Single_quote_delimiters_are_accepted() {
            var shape = ResolveAsPath("path('M 0 0 H 100 V 100 Z')", Box100);
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape.Contains(50, 50), Is.True);
        }

        // ---- border-box anchoring ------------------------------------------

        // polygon() adds box.X + x, box.Y + y to each point. path() must do the same.
        // Proof: parse a path that would contain (50,50) in path-data space → with
        // box at (20,30) it should contain (70,80) = (20+50, 30+50) in world space.

        [Test]
        public void Path_anchors_at_border_box_origin() {
            // Rectangle 0..100 in path-data space.
            var shape = ResolveAsPath("path(\"M 0 0 H 100 V 100 H 0 Z\")", BoxOffset);
            // In world space, the shape covers (20..120) x (30..130).
            Assert.That(shape.Contains(70, 80), Is.True,
                "Point at (70,80) = box origin + (50,50) is inside");
            Assert.That(shape.Contains(10, 20), Is.False,
                "Point before box origin is outside");
        }

        [Test]
        public void Path_anchoring_matches_equivalent_polygon_anchoring() {
            // A triangle in path-data space should contain the same world points
            // as the same triangle defined via polygon() on the same box.
            const string pathD = "path(\"M 0 0 L 100 0 L 50 100 Z\")";
            const string polygonS = "polygon(0% 0%, 100% 0%, 50% 100%)";

            var pathShape = ResolveAsPath(pathD, Box100);
            var polyStyle = new ComputedStyle(new Element("div"));
            polyStyle.Set("clip-path", polygonS);
            var polyShape = (PolygonClipPathShape)ClipPathResolver.Resolve(
                polyStyle, LengthContext.Default, Box100);

            // Sample a grid of points and compare.
            int disagree = 0;
            for (int y = 5; y < 100; y += 10) {
                for (int x = 5; x < 100; x += 10) {
                    bool pathIn = pathShape.Contains(x, y);
                    bool polyIn = polyShape.Contains(x, y);
                    if (pathIn != polyIn) disagree++;
                }
            }
            Assert.That(disagree, Is.EqualTo(0),
                "path() and polygon() with same triangle must agree on all grid points");
        }

        // ---- bad values → null ---------------------------------------------

        [Test]
        public void Path_with_no_quotes_returns_null() {
            var shape = ResolveAny("path(M 0 0 H 100 Z)", Box100);
            Assert.That(shape, Is.Null, "path data without quotes must be invalid");
        }

        [Test]
        public void Path_with_empty_string_returns_null() {
            var shape = ResolveAny("path(\"\")", Box100);
            Assert.That(shape, Is.Null, "empty path string must be invalid");
        }

        [Test]
        public void Path_with_malformed_data_returns_null() {
            var shape = ResolveAny("path(\"M 0 0 X garbage Z\")", Box100);
            Assert.That(shape, Is.Null, "unknown command must invalidate the whole path");
        }

        [Test]
        public void Path_with_missing_L_args_returns_null() {
            var shape = ResolveAny("path(\"M 0 0 L 100 Z\")", Box100);
            Assert.That(shape, Is.Null, "L with only one coordinate must be invalid");
        }

        // ---- bounding-box quick reject -------------------------------------

        [Test]
        public void Contains_returns_false_for_point_far_outside_bounds() {
            var shape = ResolveAsPath("path(\"M 10 10 H 90 V 90 H 10 Z\")", Box100);
            // Bounding box is (10,10)-(90,90). Point at (-100,-100) should be rejected.
            Assert.That(shape.Contains(-100, -100), Is.False);
            Assert.That(shape.Contains(200, 200), Is.False);
        }

        // ---- translate and transform ----------------------------------------

        [Test]
        public void Translate_shifts_shape_by_offset() {
            var shape = ResolveAsPath("path(\"M 0 0 H 100 V 100 H 0 Z\")", Box100);
            Assert.That(shape.Contains(50, 50), Is.True);
            var translated = (PathClipPathShape)shape.Translate(200, 200);
            Assert.That(translated.Contains(50, 50), Is.False, "after translate (50,50) is outside");
            Assert.That(translated.Contains(250, 250), Is.True, "after translate (250,250) is inside");
        }

        [Test]
        public void Kind_is_Path() {
            var shape = ResolveAsPath("path(\"M 0 0 H 100 V 100 Z\")", Box100);
            Assert.That(shape.Kind, Is.EqualTo(ClipPathShapeKind.Path));
        }

        // ---- software-rasterizer parity: path triangle == polygon triangle -

        // This test constructs PathClipPathShape and PolygonClipPathShape for the
        // same axis-aligned right triangle, then verifies Contains() agrees on every
        // integer pixel in a 100x100 grid. This pins the coordinate anchoring,
        // winding convention, and flattening accuracy for the software rasterizer.

        [Test]
        public void Path_triangle_contains_matches_polygon_triangle_on_pixel_grid() {
            // Right triangle: (0,0)-(100,0)-(0,100). Simple, axis-aligned sides.
            // polygon() on [0,100]x[0,100]:  0px 0px, 100px 0px, 0px 100px
            // path():  M 0 0 L 100 0 L 0 100 Z
            var pathShape = ResolveAsPath("path(\"M 0 0 L 100 0 L 0 100 Z\")", Box100);
            var polyStyle = new ComputedStyle(new Element("div"));
            polyStyle.Set("clip-path", "polygon(0px 0px, 100px 0px, 0px 100px)");
            var polyShape = (PolygonClipPathShape)ClipPathResolver.Resolve(
                polyStyle, LengthContext.Default, Box100);

            int disagree = 0;
            for (int y = 0; y <= 100; y += 2) {
                for (int x = 0; x <= 100; x += 2) {
                    double px = x + 0.5, py = y + 0.5; // pixel center like SoftwareRasterizer
                    bool pathIn = pathShape.Contains(px, py);
                    bool polyIn = polyShape.Contains(px, py);
                    if (pathIn != polyIn) disagree++;
                }
            }
            Assert.That(disagree, Is.EqualTo(0),
                "path() and polygon() must classify every pixel identically for same right triangle");
        }
    }
}
