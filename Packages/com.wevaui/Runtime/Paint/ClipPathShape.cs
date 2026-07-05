using System;

namespace Weva.Paint {
    public enum ClipPathShapeKind {
        None = 0,
        Inset = 1,
        Circle = 2,
        Ellipse = 3,
        Polygon = 4,
        Path = 5,
    }

    public enum ClipPathFillRule {
        Nonzero = 0,
        Evenodd = 1,
    }

    public readonly struct Point2D {
        public readonly double X;
        public readonly double Y;

        public Point2D(double x, double y) {
            X = x;
            Y = y;
        }

        public Point2D Translate(double dx, double dy) => new Point2D(X + dx, Y + dy);
        public Point2D Transform(Transform2D transform) {
            var (x, y) = transform.Apply(X, Y);
            return new Point2D(x, y);
        }
    }

    public abstract class ClipPathShape {
        public abstract ClipPathShapeKind Kind { get; }
        public abstract Rect Bounds { get; }
        public abstract bool Contains(double x, double y);
        public abstract ClipPathShape Translate(double dx, double dy);
        public abstract ClipPathShape Transform(Transform2D transform);

        protected static Rect BoundsOf(Point2D[] points) {
            if (points == null || points.Length == 0) return default;
            double minX = points[0].X;
            double minY = points[0].Y;
            double maxX = minX;
            double maxY = minY;
            for (int i = 1; i < points.Length; i++) {
                var p = points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public sealed class InsetClipPathShape : ClipPathShape {
        public Rect Rect { get; }
        public BorderRadii Radii { get; }

        public InsetClipPathShape(Rect rect, BorderRadii radii) {
            Rect = rect;
            Radii = radii;
        }

        public override ClipPathShapeKind Kind => ClipPathShapeKind.Inset;
        public override Rect Bounds => Rect;

        public override bool Contains(double x, double y) {
            if (x < Rect.X || x > Rect.Right || y < Rect.Y || y > Rect.Bottom) return false;
            if (Radii.IsZero) return true;
            double cx = Rect.X + Rect.Width * 0.5;
            double cy = Rect.Y + Rect.Height * 0.5;
            double halfW = Rect.Width * 0.5;
            double halfH = Rect.Height * 0.5;
            double localX = x - cx;
            double localY = y - cy;
            CornerRadius c;
            if (localX < 0 && localY < 0) c = Radii.TopLeft;
            else if (localX >= 0 && localY < 0) c = Radii.TopRight;
            else if (localX >= 0) c = Radii.BottomRight;
            else c = Radii.BottomLeft;
            double rx = Math.Max(0, c.XRadius);
            double ry = Math.Max(0, c.YRadius);
            if (rx <= 0 || ry <= 0) return true;
            double qx = Math.Abs(localX) - (halfW - rx);
            double qy = Math.Abs(localY) - (halfH - ry);
            if (qx <= 0 || qy <= 0) return true;
            double nx = qx / rx;
            double ny = qy / ry;
            return nx * nx + ny * ny <= 1.0;
        }

        public override ClipPathShape Translate(double dx, double dy) {
            return new InsetClipPathShape(new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height), Radii);
        }

        public override ClipPathShape Transform(Transform2D transform) {
            if (transform.Equals(Transform2D.Identity)) return this;
            const double Epsilon = 1e-9;
            if (Math.Abs(transform.B) <= Epsilon && Math.Abs(transform.C) <= Epsilon) {
                var p0 = new Point2D(Rect.X, Rect.Y).Transform(transform);
                var p1 = new Point2D(Rect.Right, Rect.Bottom).Transform(transform);
                double left = Math.Min(p0.X, p1.X);
                double top = Math.Min(p0.Y, p1.Y);
                double right = Math.Max(p0.X, p1.X);
                double bottom = Math.Max(p0.Y, p1.Y);
                double sx = Math.Abs(transform.A);
                double sy = Math.Abs(transform.D);
                return new InsetClipPathShape(
                    new Rect(left, top, right - left, bottom - top),
                    ScaleRadii(Radii, sx, sy));
            }
            return PolygonClipPathShape.FromRect(Rect, 16).Transform(transform);
        }

        static BorderRadii ScaleRadii(BorderRadii radii, double sx, double sy) {
            if (radii.IsZero) return radii;
            return new BorderRadii(
                Scale(radii.TopLeft, sx, sy),
                Scale(radii.TopRight, sx, sy),
                Scale(radii.BottomRight, sx, sy),
                Scale(radii.BottomLeft, sx, sy));
        }

        static CornerRadius Scale(CornerRadius radius, double sx, double sy) {
            return new CornerRadius(radius.XRadius * sx, radius.YRadius * sy);
        }
    }

    public sealed class CircleClipPathShape : ClipPathShape {
        public double CenterX { get; }
        public double CenterY { get; }
        public double Radius { get; }

        public CircleClipPathShape(double centerX, double centerY, double radius) {
            CenterX = centerX;
            CenterY = centerY;
            Radius = Math.Max(0, radius);
        }

        public override ClipPathShapeKind Kind => ClipPathShapeKind.Circle;
        public override Rect Bounds => new Rect(CenterX - Radius, CenterY - Radius, Radius * 2, Radius * 2);

        public override bool Contains(double x, double y) {
            double dx = x - CenterX;
            double dy = y - CenterY;
            return dx * dx + dy * dy <= Radius * Radius;
        }

        public override ClipPathShape Translate(double dx, double dy) {
            return new CircleClipPathShape(CenterX + dx, CenterY + dy, Radius);
        }

        public override ClipPathShape Transform(Transform2D transform) {
            if (transform.Equals(Transform2D.Identity)) return this;
            return PolygonClipPathShape.FromEllipse(CenterX, CenterY, Radius, Radius, 24).Transform(transform);
        }
    }

    public sealed class EllipseClipPathShape : ClipPathShape {
        public double CenterX { get; }
        public double CenterY { get; }
        public double RadiusX { get; }
        public double RadiusY { get; }

        public EllipseClipPathShape(double centerX, double centerY, double radiusX, double radiusY) {
            CenterX = centerX;
            CenterY = centerY;
            RadiusX = Math.Max(0, radiusX);
            RadiusY = Math.Max(0, radiusY);
        }

        public override ClipPathShapeKind Kind => ClipPathShapeKind.Ellipse;
        public override Rect Bounds => new Rect(CenterX - RadiusX, CenterY - RadiusY, RadiusX * 2, RadiusY * 2);

        public override bool Contains(double x, double y) {
            if (RadiusX <= 0 || RadiusY <= 0) return false;
            double nx = (x - CenterX) / RadiusX;
            double ny = (y - CenterY) / RadiusY;
            return nx * nx + ny * ny <= 1.0;
        }

        public override ClipPathShape Translate(double dx, double dy) {
            return new EllipseClipPathShape(CenterX + dx, CenterY + dy, RadiusX, RadiusY);
        }

        public override ClipPathShape Transform(Transform2D transform) {
            if (transform.Equals(Transform2D.Identity)) return this;
            return PolygonClipPathShape.FromEllipse(CenterX, CenterY, RadiusX, RadiusY, 24).Transform(transform);
        }
    }

    public sealed class PolygonClipPathShape : ClipPathShape {
        public Point2D[] Points { get; }
        public ClipPathFillRule FillRule { get; }

        public PolygonClipPathShape(Point2D[] points)
            : this(points, ClipPathFillRule.Nonzero) {
        }

        public PolygonClipPathShape(Point2D[] points, ClipPathFillRule fillRule) {
            Points = points != null ? (Point2D[])points.Clone() : Array.Empty<Point2D>();
            FillRule = fillRule;
        }

        public override ClipPathShapeKind Kind => ClipPathShapeKind.Polygon;
        public override Rect Bounds => BoundsOf(Points);

        public override bool Contains(double x, double y) {
            int count = Points.Length;
            if (count < 3) return false;
            if (FillRule == ClipPathFillRule.Evenodd) {
                bool inside = false;
                for (int i = 0, j = count - 1; i < count; j = i++) {
                    var pi = Points[i];
                    var pj = Points[j];
                    bool crosses = (pi.Y > y) != (pj.Y > y);
                    if (!crosses) continue;
                    double atX = (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X;
                    if (x < atX) inside = !inside;
                }
                return inside;
            }
            int winding = 0;
            for (int i = 0, j = count - 1; i < count; j = i++) {
                var pi = Points[i];
                var pj = Points[j];
                bool upward = pj.Y <= y && pi.Y > y;
                bool downward = pj.Y > y && pi.Y <= y;
                if (!upward && !downward) continue;
                double atX = (pi.X - pj.X) * (y - pj.Y) / (pi.Y - pj.Y) + pj.X;
                if (x < atX) {
                    if (upward) winding++;
                    else winding--;
                }
            }
            return winding != 0;
        }

        public override ClipPathShape Translate(double dx, double dy) {
            var pts = new Point2D[Points.Length];
            for (int i = 0; i < Points.Length; i++) pts[i] = Points[i].Translate(dx, dy);
            return new PolygonClipPathShape(pts, FillRule);
        }

        public override ClipPathShape Transform(Transform2D transform) {
            if (transform.Equals(Transform2D.Identity)) return this;
            var pts = new Point2D[Points.Length];
            for (int i = 0; i < Points.Length; i++) pts[i] = Points[i].Transform(transform);
            return new PolygonClipPathShape(pts, FillRule);
        }

        public static PolygonClipPathShape FromRect(Rect rect, int segmentsPerCorner) {
            return new PolygonClipPathShape(new[] {
                new Point2D(rect.X, rect.Y),
                new Point2D(rect.Right, rect.Y),
                new Point2D(rect.Right, rect.Bottom),
                new Point2D(rect.X, rect.Bottom),
            });
        }

        public static PolygonClipPathShape FromEllipse(double cx, double cy, double rx, double ry, int segments) {
            int n = Math.Max(8, segments);
            var pts = new Point2D[n];
            for (int i = 0; i < n; i++) {
                double a = (Math.PI * 2.0 * i) / n;
                pts[i] = new Point2D(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry);
            }
            return new PolygonClipPathShape(pts);
        }
    }
}
