using System;
using System.Collections.Generic;
using Weva.Paint;

namespace Weva.Rendering {
    // Pure-C# mesh accumulator. Headlessly testable; the URP backend wraps Unity's Mesh
    // API around it.
    //
    // Vertex layout (interleaved, but exposed as parallel arrays for ease of upload):
    //   position : Vector3   (z carries effect-id float — 0 = solid, 1 = gradient, 2 = text, 3 = shadow)
    //   uv       : Vector2   (0..1 inside the quad — used by the SDF)
    //   color    : LinearColor (premultiplied; uploaded as half4 by the backend)
    //   tangent  : Vector4   (radiusX, radiusY, halfWidthPx, halfHeightPx)
    //
    // The "tangent" channel name is Unity's traditional carrier for arbitrary per-vertex
    // float4 data when using the legacy mesh attributes; the backend can re-bind it to
    // TEXCOORD2 if a custom VertexAttributeDescriptor is preferred at upload time.
    //
    // Winding: counter-clockwise (Unity's default front-face), so quads survive default
    // back-face culling regardless of pipeline.
    public sealed class MeshBuilder {
        public readonly struct Vertex : IEquatable<Vertex> {
            public readonly float Px;
            public readonly float Py;
            public readonly float Pz;
            public readonly float Uvx;
            public readonly float Uvy;
            public readonly LinearColor Color;
            public readonly float Tx;
            public readonly float Ty;
            public readonly float Tz;
            public readonly float Tw;

            public Vertex(float px, float py, float pz, float uvx, float uvy, LinearColor color, float tx, float ty, float tz, float tw) {
                Px = px; Py = py; Pz = pz;
                Uvx = uvx; Uvy = uvy;
                Color = color;
                Tx = tx; Ty = ty; Tz = tz; Tw = tw;
            }

            public bool Equals(Vertex other) {
                return Px == other.Px && Py == other.Py && Pz == other.Pz
                    && Uvx == other.Uvx && Uvy == other.Uvy
                    && Color == other.Color
                    && Tx == other.Tx && Ty == other.Ty && Tz == other.Tz && Tw == other.Tw;
            }

            public override bool Equals(object obj) => obj is Vertex v && Equals(v);
            public override int GetHashCode() {
                unchecked {
                    int h = Px.GetHashCode();
                    h = (h * 397) ^ Py.GetHashCode();
                    h = (h * 397) ^ Pz.GetHashCode();
                    h = (h * 397) ^ Uvx.GetHashCode();
                    h = (h * 397) ^ Uvy.GetHashCode();
                    h = (h * 397) ^ Color.GetHashCode();
                    h = (h * 397) ^ Tx.GetHashCode();
                    h = (h * 397) ^ Ty.GetHashCode();
                    h = (h * 397) ^ Tz.GetHashCode();
                    h = (h * 397) ^ Tw.GetHashCode();
                    return h;
                }
            }
        }

        public const float EffectIdSolid = 0f;
        public const float EffectIdGradient = 1f;
        public const float EffectIdText = 2f;
        public const float EffectIdShadow = 3f;

        readonly List<Vertex> vertices = new List<Vertex>(1024);
        readonly List<int> indices = new List<int>(1536);

        public IReadOnlyList<Vertex> Vertices => vertices;
        public IReadOnlyList<int> Indices => indices;

        public int VertexCount => vertices.Count;
        public int IndexCount => indices.Count;

        public void Reset() {
            vertices.Clear();
            indices.Clear();
        }

        // Adds a screen-aligned quad in CSS top-left coordinates. The transform parameter
        // applies before the quad is added; the caller is responsible for converting CSS
        // coordinates to whatever target space the shader expects (typically pixel space,
        // with a final orthographic projection done shader-side).
        //
        // The four corners are stamped in CCW order: TL, BL, BR, TR (with two triangles
        // TL/BL/BR and TL/BR/TR).
        public int AddQuad(
            Rect bounds,
            LinearColor color,
            float effectId,
            float radiusX,
            float radiusY,
            Transform2D transform) {
            float halfW = (float)(bounds.Width * 0.5);
            float halfH = (float)(bounds.Height * 0.5);

            double tlx = bounds.X;
            double tly = bounds.Y;
            double trx = bounds.Right;
            double tryy = bounds.Y;
            double brx = bounds.Right;
            double bry = bounds.Bottom;
            double blx = bounds.X;
            double bly = bounds.Bottom;

            var (atlx, atly) = transform.Apply(tlx, tly);
            var (atrx, atry) = transform.Apply(trx, tryy);
            var (abrx, abry) = transform.Apply(brx, bry);
            var (ablx, ably) = transform.Apply(blx, bly);

            int i0 = vertices.Count;

            vertices.Add(new Vertex((float)atlx, (float)atly, effectId, 0f, 0f, color, radiusX, radiusY, halfW, halfH));
            vertices.Add(new Vertex((float)ablx, (float)ably, effectId, 0f, 1f, color, radiusX, radiusY, halfW, halfH));
            vertices.Add(new Vertex((float)abrx, (float)abry, effectId, 1f, 1f, color, radiusX, radiusY, halfW, halfH));
            vertices.Add(new Vertex((float)atrx, (float)atry, effectId, 1f, 0f, color, radiusX, radiusY, halfW, halfH));

            // Two triangles, CCW (Unity is left-handed but with default back-face culling
            // configured for CCW in 2D ortho; the test verifies winding numerically).
            indices.Add(i0 + 0);
            indices.Add(i0 + 1);
            indices.Add(i0 + 2);

            indices.Add(i0 + 0);
            indices.Add(i0 + 2);
            indices.Add(i0 + 3);

            return i0;
        }

        public int AddQuad(Rect bounds, LinearColor color) {
            return AddQuad(bounds, color, EffectIdSolid, 0f, 0f, Transform2D.Identity);
        }

        // Convenience for textured quads (text glyphs). uv0..uv3 supply atlas-space UVs at
        // each corner in TL, BL, BR, TR order.
        public int AddTexturedQuad(
            Rect bounds,
            LinearColor color,
            float effectId,
            (float u, float v) uvTL,
            (float u, float v) uvBL,
            (float u, float v) uvBR,
            (float u, float v) uvTR,
            Transform2D transform) {
            double tlx = bounds.X;
            double tly = bounds.Y;
            double trx = bounds.Right;
            double tryy = bounds.Y;
            double brx = bounds.Right;
            double bry = bounds.Bottom;
            double blx = bounds.X;
            double bly = bounds.Bottom;

            var (atlx, atly) = transform.Apply(tlx, tly);
            var (atrx, atry) = transform.Apply(trx, tryy);
            var (abrx, abry) = transform.Apply(brx, bry);
            var (ablx, ably) = transform.Apply(blx, bly);

            int i0 = vertices.Count;

            vertices.Add(new Vertex((float)atlx, (float)atly, effectId, uvTL.u, uvTL.v, color, 0f, 0f, 0f, 0f));
            vertices.Add(new Vertex((float)ablx, (float)ably, effectId, uvBL.u, uvBL.v, color, 0f, 0f, 0f, 0f));
            vertices.Add(new Vertex((float)abrx, (float)abry, effectId, uvBR.u, uvBR.v, color, 0f, 0f, 0f, 0f));
            vertices.Add(new Vertex((float)atrx, (float)atry, effectId, uvTR.u, uvTR.v, color, 0f, 0f, 0f, 0f));

            indices.Add(i0 + 0);
            indices.Add(i0 + 1);
            indices.Add(i0 + 2);
            indices.Add(i0 + 0);
            indices.Add(i0 + 2);
            indices.Add(i0 + 3);

            return i0;
        }

        // Computes the signed area of a triangle (positive when CCW in CSS-like top-left
        // coordinates where +Y is down — flipped relative to math-textbook CCW).
        public static double SignedTriangleArea(double x0, double y0, double x1, double y1, double x2, double y2) {
            return 0.5 * ((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0));
        }
    }
}
