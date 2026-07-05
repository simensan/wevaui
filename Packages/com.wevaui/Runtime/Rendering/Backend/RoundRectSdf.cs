using System;
using Weva.Paint;

namespace Weva.Rendering {
    // Pure-C# math helpers for rounded-rectangle signed distance fields.
    //
    // The "rounded rect" we describe is centered on the origin with extents (halfW, halfH).
    // Each corner has its own (rx, ry). The SDF is negative inside the shape, zero on the
    // boundary, and positive outside; |SDF| approaches the Euclidean distance to the boundary.
    //
    // The shader uses a single-corner radius pair per quad (radiusX, radiusY) packed into
    // the vertex tangent so that fragments interpolate halfWidth/halfHeight in the UV plane;
    // however when emitting per-corner radii we pick the radii of the corner the fragment is
    // closest to. PickCorner does this selection in C# so unit tests can validate it.
    public static class RoundRectSdf {
        // Standard rounded box SDF (uniform radii). Coordinates are in box-local space where
        // the box spans [-halfW, +halfW] x [-halfH, +halfH].
        public static double Sample(double localX, double localY, double halfW, double halfH, double radius) {
            return SamplePerAxis(localX, localY, halfW, halfH, radius, radius);
        }

        public static double SamplePerAxis(double localX, double localY, double halfW, double halfH, double rx, double ry) {
            // Clamp radii to the half-extents.
            if (rx < 0) rx = 0;
            if (ry < 0) ry = 0;
            if (rx > halfW) rx = halfW;
            if (ry > halfH) ry = halfH;

            // Mirror of the GPU Weva_RoundedBoxSdfPerAxis: the elliptical
            // distance is used ONLY inside the corner quadrant; everywhere else
            // (straight edges + interior) the plain box SDF applies, so the
            // straight edges are independent of the corner radii. The previous
            // form scaled the edge distance by min(rx,ry)/r, which placed a
            // shared straight edge at slightly different positions on either
            // side of centre when adjacent corners had different radii — a
            // visible vertical seam down the fill. The corner branch uses the
            // gradient-corrected ellipse SDF k1*(k1-1)/k2, which meets the box
            // SDF exactly at each quadrant boundary (C0-continuous) and reduces
            // to length(e)-r when rx == ry.
            double apx = Math.Abs(localX);
            double apy = Math.Abs(localY);
            double ex = apx - (halfW - rx);
            double ey = apy - (halfH - ry);
            if (ex > 0.0 && ey > 0.0 && rx > 1e-4 && ry > 1e-4) {
                double enx = ex / rx, eny = ey / ry;
                double k1 = Math.Sqrt(enx * enx + eny * eny);
                double egx = ex / (rx * rx), egy = ey / (ry * ry);
                double k2 = Math.Max(Math.Sqrt(egx * egx + egy * egy), 1e-6);
                return k1 * (k1 - 1.0) / k2;
            }
            double dx = apx - halfW;
            double dy = apy - halfH;
            double outsideX = Math.Max(dx, 0.0);
            double outsideY = Math.Max(dy, 0.0);
            return Math.Min(Math.Max(dx, dy), 0.0)
                 + Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
        }

        public static double SamplePerCorner(
            double localX, double localY,
            double halfW, double halfH,
            double rTopLeftX, double rTopLeftY,
            double rTopRightX, double rTopRightY,
            double rBottomRightX, double rBottomRightY,
            double rBottomLeftX, double rBottomLeftY) {
            // Pick the radii of the corner the point lies in (split by sign). This matches
            // the shader's per-corner radii path which selects via the sign of localX/localY.
            double rx, ry;
            if (localX >= 0 && localY < 0) { rx = rTopRightX; ry = rTopRightY; }
            else if (localX < 0 && localY < 0) { rx = rTopLeftX; ry = rTopLeftY; }
            else if (localX < 0 && localY >= 0) { rx = rBottomLeftX; ry = rBottomLeftY; }
            else { rx = rBottomRightX; ry = rBottomRightY; }
            return SamplePerAxis(localX, localY, halfW, halfH, rx, ry);
        }

        public static (double rx, double ry) PickCornerRadii(
            double localX, double localY,
            double rTopLeftX, double rTopLeftY,
            double rTopRightX, double rTopRightY,
            double rBottomRightX, double rBottomRightY,
            double rBottomLeftX, double rBottomLeftY) {
            if (localX >= 0 && localY < 0) return (rTopRightX, rTopRightY);
            if (localX < 0 && localY < 0) return (rTopLeftX, rTopLeftY);
            if (localX < 0 && localY >= 0) return (rBottomLeftX, rBottomLeftY);
            return (rBottomRightX, rBottomRightY);
        }

        // Antialiasing band width in pixels. The shader fwidth() reproduces this; for
        // headless math we expose a constant so the unit tests have a known reference.
        public const double DefaultAaPixels = 1.0;

        public static double Coverage(double sdf, double aaPixels = DefaultAaPixels) {
            // Smoothstep from sdf = +aa/2 (fully outside, coverage 0) to -aa/2 (fully inside, 1).
            double half = aaPixels * 0.5;
            double t = (half - sdf) / aaPixels;
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // Pack per-corner radii from a BorderRadii into the two-axis (rx, ry) pair we use in
        // the simplest vertex layout (uniform radii for the whole quad). When radii differ,
        // the backend should split the quad into four sub-quads or use SamplePerCorner above.
        public static (float rx, float ry) PackUniform(BorderRadii radii) {
            // If all four corners share the same radius pair, pick that. Otherwise fall back
            // to the largest, leaving per-corner radii to a later quad-split.
            var tl = radii.TopLeft;
            var tr = radii.TopRight;
            var br = radii.BottomRight;
            var bl = radii.BottomLeft;
            bool uniform =
                tl.XRadius == tr.XRadius && tr.XRadius == br.XRadius && br.XRadius == bl.XRadius &&
                tl.YRadius == tr.YRadius && tr.YRadius == br.YRadius && br.YRadius == bl.YRadius;
            if (uniform) return ((float)tl.XRadius, (float)tl.YRadius);
            double rx = Math.Max(Math.Max(tl.XRadius, tr.XRadius), Math.Max(br.XRadius, bl.XRadius));
            double ry = Math.Max(Math.Max(tl.YRadius, tr.YRadius), Math.Max(br.YRadius, bl.YRadius));
            return ((float)rx, (float)ry);
        }

        public static bool HasUniformRadii(BorderRadii radii) {
            var tl = radii.TopLeft;
            var tr = radii.TopRight;
            var br = radii.BottomRight;
            var bl = radii.BottomLeft;
            return tl.XRadius == tr.XRadius && tr.XRadius == br.XRadius && br.XRadius == bl.XRadius &&
                   tl.YRadius == tr.YRadius && tr.YRadius == br.YRadius && br.YRadius == bl.YRadius;
        }

    }
}
