using System;
using System.Collections.Generic;

namespace Weva.Paint {
    // CPU-only implementation of clip-path: path("...") for Phase 1.
    // Phase 2 (GPU rasterized mask texture) is tracked as B16.
    //
    // Coordinates: SVG path data is in px relative to the element's local
    // coordinate system. The resolver anchors the shape at the border-box
    // origin (matching polygon() behavior — see ClipPathResolver.TryParsePolygon
    // which adds box.X/box.Y to each point).
    //
    // Contains() uses a ray-cast algorithm summed across all flattened
    // sub-polygons, mirroring PolygonClipPathShape.Contains().
    public sealed class PathClipPathShape : ClipPathShape {
        // Each entry is the vertices of one closed sub-polygon (flattened from
        // curves by SvgPathParser). Sub-polygons are already translated to
        // world-space (border-box origin added at construction time).
        public IReadOnlyList<Point2D[]> SubPolygons { get; }
        public ClipPathFillRule FillRule { get; }

        readonly Rect _bounds;

        public PathClipPathShape(IList<Point2D[]> subPolygons, ClipPathFillRule fillRule) {
            FillRule = fillRule;

            // Clone the sub-polygon arrays for immutability.
            var cloned = new Point2D[subPolygons.Count][];
            for (int i = 0; i < subPolygons.Count; i++) {
                var src = subPolygons[i];
                var dst = new Point2D[src.Length];
                Array.Copy(src, dst, src.Length);
                cloned[i] = dst;
            }
            SubPolygons = cloned;
            _bounds = ComputeBounds(cloned);
        }

        public override ClipPathShapeKind Kind => ClipPathShapeKind.Path;

        public override Rect Bounds => _bounds;

        // Ray-cast across all sub-polygons, accumulating crossings.
        // Nonzero: inside if total winding != 0.
        // Evenodd: inside if total crossing parity is odd.
        public override bool Contains(double x, double y) {
            // Quick bounding-box reject.
            if (x < _bounds.X || x > _bounds.Right || y < _bounds.Y || y > _bounds.Bottom)
                return false;

            if (FillRule == ClipPathFillRule.Evenodd) {
                bool inside = false;
                foreach (var poly in SubPolygons) {
                    int count = poly.Length;
                    if (count < 2) continue;
                    for (int i = 0, j = count - 1; i < count; j = i++) {
                        var pi = poly[i];
                        var pj = poly[j];
                        bool crosses = (pi.Y > y) != (pj.Y > y);
                        if (!crosses) continue;
                        double atX = (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X;
                        if (x < atX) inside = !inside;
                    }
                }
                return inside;
            }

            // Nonzero winding.
            int winding = 0;
            foreach (var poly in SubPolygons) {
                int count = poly.Length;
                if (count < 2) continue;
                for (int i = 0, j = count - 1; i < count; j = i++) {
                    var pi = poly[i];
                    var pj = poly[j];
                    bool upward   = pj.Y <= y && pi.Y > y;
                    bool downward = pj.Y >  y && pi.Y <= y;
                    if (!upward && !downward) continue;
                    double atX = (pi.X - pj.X) * (y - pj.Y) / (pi.Y - pj.Y) + pj.X;
                    if (x < atX) {
                        if (upward) winding++;
                        else        winding--;
                    }
                }
            }
            return winding != 0;
        }

        public override ClipPathShape Translate(double dx, double dy) {
            var translated = new Point2D[SubPolygons.Count][];
            for (int i = 0; i < SubPolygons.Count; i++) {
                var src = SubPolygons[i];
                var dst = new Point2D[src.Length];
                for (int j = 0; j < src.Length; j++) dst[j] = src[j].Translate(dx, dy);
                translated[i] = dst;
            }
            return new PathClipPathShape(translated, FillRule);
        }

        public override ClipPathShape Transform(Transform2D transform) {
            if (transform.Equals(Transform2D.Identity)) return this;
            var xformed = new Point2D[SubPolygons.Count][];
            for (int i = 0; i < SubPolygons.Count; i++) {
                var src = SubPolygons[i];
                var dst = new Point2D[src.Length];
                for (int j = 0; j < src.Length; j++) dst[j] = src[j].Transform(transform);
                xformed[i] = dst;
            }
            return new PathClipPathShape(xformed, FillRule);
        }

        static Rect ComputeBounds(Point2D[][] polys) {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var poly in polys) {
                foreach (var p in poly) {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }
            if (double.IsInfinity(minX)) return default;
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
