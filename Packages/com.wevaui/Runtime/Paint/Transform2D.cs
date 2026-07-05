using System;
using System.Globalization;

namespace Weva.Paint {
    // Row-major affine 2D transform:
    //   [ A  B  0 ]
    //   [ C  D  0 ]
    //   [ Tx Ty 1 ]
    // A point (x, y) is transformed as:
    //   x' = A*x + C*y + Tx
    //   y' = B*x + D*y + Ty
    public readonly struct Transform2D : IEquatable<Transform2D> {
        public float A { get; }
        public float B { get; }
        public float C { get; }
        public float D { get; }
        public float Tx { get; }
        public float Ty { get; }

        public Transform2D(float a, float b, float c, float d, float tx, float ty) {
            A = a; B = b; C = c; D = d; Tx = tx; Ty = ty;
        }

        public static Transform2D Identity => new Transform2D(1, 0, 0, 1, 0, 0);

        public static Transform2D Translate(float x, float y) {
            return new Transform2D(1, 0, 0, 1, x, y);
        }

        public static Transform2D Scale(float sx, float sy) {
            return new Transform2D(sx, 0, 0, sy, 0, 0);
        }

        public static Transform2D Rotate(double degrees) {
            double r = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(r);
            float sin = (float)Math.Sin(r);
            return new Transform2D(cos, sin, -sin, cos, 0, 0);
        }

        public Transform2D Multiply(Transform2D other) {
            // result = this * other (apply this first, then other, when transforming a point)
            float a = A * other.A + B * other.C;
            float b = A * other.B + B * other.D;
            float c = C * other.A + D * other.C;
            float d = C * other.B + D * other.D;
            float tx = Tx * other.A + Ty * other.C + other.Tx;
            float ty = Tx * other.B + Ty * other.D + other.Ty;
            return new Transform2D(a, b, c, d, tx, ty);
        }

        public (double X, double Y) Apply(double x, double y) {
            double nx = A * x + C * y + Tx;
            double ny = B * x + D * y + Ty;
            return (nx, ny);
        }

        public bool Equals(Transform2D other) {
            return A == other.A && B == other.B && C == other.C && D == other.D && Tx == other.Tx && Ty == other.Ty;
        }

        public override bool Equals(object obj) => obj is Transform2D other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = A.GetHashCode();
                h = (h * 397) ^ B.GetHashCode();
                h = (h * 397) ^ C.GetHashCode();
                h = (h * 397) ^ D.GetHashCode();
                h = (h * 397) ^ Tx.GetHashCode();
                h = (h * 397) ^ Ty.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(Transform2D a, Transform2D b) => a.Equals(b);
        public static bool operator !=(Transform2D a, Transform2D b) => !a.Equals(b);

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture,
                "Transform2D({0:R}, {1:R}, {2:R}, {3:R}, {4:R}, {5:R})", A, B, C, D, Tx, Ty);
        }
    }
}
