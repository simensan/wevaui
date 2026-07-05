#if WEVA_URP
using System;
using UnityEngine;

namespace Weva.Rendering {
    // CSS Filter Effects 1 color-matrix decompositions. Each function returns a 4×4 matrix
    // + bias vector that operates on straight-alpha RGB (the shader unpre-multiplies, applies,
    // re-premultiplies). Matrices match the spec's `feColorMatrix` definitions in
    // CSS Filter Effects 1 §10.1.
    //
    // Color space: per Filter Effects 1 §6.1 ("Filter primitives") color-matrix primitives
    // operate in **linear-light** RGB (the `linearRGB` `color-interpolation-filters` value
    // is the spec default for filter primitives, and Chrome implements the CSS filter()
    // shorthand functions in that domain). Under URP's default Linear color space the
    // intermediate filter RT holds premultiplied linear-light values directly (see
    // `_WevaRawFilterOutput` in UIShaderLib.hlsl — content drawn into the filter RT
    // bypasses the sRGB encode), so the matrix multiplication lands in the correct domain
    // and the spec's Rec.709 luminance weights (0.2126/0.7152/0.0722) below give the
    // expected browser-matching output. **Gamma color space (URP Gamma) is a known
    // limitation** — under UNITY_COLORSPACE_GAMMA the filter RT holds gamma-encoded
    // values and the matrix would need a shader-side linearize/encode (tracked as K1's
    // gamma-URP follow-up; not handled here because the default URP pipeline runs Linear).
    public struct ColorMatrix {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;
        public Vector4 Row3;
        public Vector4 Bias;

        public static ColorMatrix Identity => new ColorMatrix {
            Row0 = new Vector4(1, 0, 0, 0),
            Row1 = new Vector4(0, 1, 0, 0),
            Row2 = new Vector4(0, 0, 1, 0),
            Row3 = new Vector4(0, 0, 0, 1),
            Bias = Vector4.zero
        };
    }

    public static class ColorMatrices {
        public static ColorMatrix Compose(ColorMatrix first, ColorMatrix second) {
            return new ColorMatrix {
                Row0 = MultiplyRow(second.Row0, first),
                Row1 = MultiplyRow(second.Row1, first),
                Row2 = MultiplyRow(second.Row2, first),
                Row3 = MultiplyRow(second.Row3, first),
                Bias = new Vector4(
                    Vector4.Dot(second.Row0, first.Bias) + second.Bias.x,
                    Vector4.Dot(second.Row1, first.Bias) + second.Bias.y,
                    Vector4.Dot(second.Row2, first.Bias) + second.Bias.z,
                    Vector4.Dot(second.Row3, first.Bias) + second.Bias.w)
            };
        }

        public static Vector4 Evaluate(ColorMatrix matrix, Vector4 value) {
            return new Vector4(
                Vector4.Dot(matrix.Row0, value) + matrix.Bias.x,
                Vector4.Dot(matrix.Row1, value) + matrix.Bias.y,
                Vector4.Dot(matrix.Row2, value) + matrix.Bias.z,
                Vector4.Dot(matrix.Row3, value) + matrix.Bias.w);
        }

        static Vector4 MultiplyRow(Vector4 row, ColorMatrix matrix) {
            return new Vector4(
                row.x * matrix.Row0.x + row.y * matrix.Row1.x + row.z * matrix.Row2.x + row.w * matrix.Row3.x,
                row.x * matrix.Row0.y + row.y * matrix.Row1.y + row.z * matrix.Row2.y + row.w * matrix.Row3.y,
                row.x * matrix.Row0.z + row.y * matrix.Row1.z + row.z * matrix.Row2.z + row.w * matrix.Row3.z,
                row.x * matrix.Row0.w + row.y * matrix.Row1.w + row.z * matrix.Row2.w + row.w * matrix.Row3.w);
        }

        // CSS brightness(amount): out.rgb = in.rgb * amount.
        public static ColorMatrix Brightness(double amount) {
            float a = (float)Math.Max(0, amount);
            return new ColorMatrix {
                Row0 = new Vector4(a, 0, 0, 0),
                Row1 = new Vector4(0, a, 0, 0),
                Row2 = new Vector4(0, 0, a, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = Vector4.zero
            };
        }

        // CSS contrast(amount): out.rgb = (in.rgb - 0.5) * amount + 0.5.
        // The SoftwareRasterizer impl uses 128 (byte) which is the 0.5 midpoint.
        public static ColorMatrix Contrast(double amount) {
            float a = (float)Math.Max(0, amount);
            float bias = 0.5f * (1f - a);
            return new ColorMatrix {
                Row0 = new Vector4(a, 0, 0, 0),
                Row1 = new Vector4(0, a, 0, 0),
                Row2 = new Vector4(0, 0, a, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = new Vector4(bias, bias, bias, 0)
            };
        }

        // CSS grayscale(amount): per-channel lerp between identity and luminance.
        // amount=0 → identity, amount=1 → full luminance.
        // CSS Filter Effects 1 §10.1 fixes the luminance weights to Rec.709
        // (0.2126/0.7152/0.0722) applied in linear-light RGB; the matrix collapses each
        // row to the same luminance at t=1 and matches `saturate(0)` exactly.
        public static ColorMatrix Grayscale(double amount) {
            float t = (float)Math.Max(0, Math.Min(1, amount));
            // Rec.709 / sRGB linear-light luminance weights per CSS Filter Effects 1 §10.1.
            float r = 0.2126f, g = 0.7152f, b = 0.0722f;
            return new ColorMatrix {
                Row0 = new Vector4(r * t + (1 - t), g * t, b * t, 0),
                Row1 = new Vector4(r * t, g * t + (1 - t), b * t, 0),
                Row2 = new Vector4(r * t, g * t, b * t + (1 - t), 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = Vector4.zero
            };
        }

        // CSS sepia(amount). Matrix from the spec.
        public static ColorMatrix Sepia(double amount) {
            float t = (float)Math.Max(0, Math.Min(1, amount));
            float ot = 1 - t;
            // Identity blended with the canonical sepia matrix.
            return new ColorMatrix {
                Row0 = new Vector4(ot + t * 0.393f, t * 0.769f, t * 0.189f, 0),
                Row1 = new Vector4(t * 0.349f, ot + t * 0.686f, t * 0.168f, 0),
                Row2 = new Vector4(t * 0.272f, t * 0.534f, ot + t * 0.131f, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = Vector4.zero
            };
        }

        // CSS invert(amount): out = (1 - in)*amount + in*(1-amount) = in*(1-2t) + t.
        public static ColorMatrix Invert(double amount) {
            float t = (float)Math.Max(0, Math.Min(1, amount));
            float diag = 1f - 2f * t;
            return new ColorMatrix {
                Row0 = new Vector4(diag, 0, 0, 0),
                Row1 = new Vector4(0, diag, 0, 0),
                Row2 = new Vector4(0, 0, diag, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = new Vector4(t, t, t, 0)
            };
        }

        // CSS saturate(amount). amount=0 = full desaturation (grayscale), amount=1 = identity,
        // amount > 1 = boost. Matrix from feColorMatrix type=saturate.
        public static ColorMatrix Saturate(double amount) {
            float s = (float)Math.Max(0, amount);
            // Per spec feColorMatrix saturate matrix.
            float a = 0.213f + 0.787f * s;
            float b = 0.715f - 0.715f * s;
            float c = 0.072f - 0.072f * s;
            float d = 0.213f - 0.213f * s;
            float e = 0.715f + 0.285f * s;
            float f = 0.072f - 0.072f * s;
            float g = 0.213f - 0.213f * s;
            float h = 0.715f - 0.715f * s;
            float i = 0.072f + 0.928f * s;
            return new ColorMatrix {
                Row0 = new Vector4(a, b, c, 0),
                Row1 = new Vector4(d, e, f, 0),
                Row2 = new Vector4(g, h, i, 0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = Vector4.zero
            };
        }

        // CSS hue-rotate(angle). Matrix from feColorMatrix type=hueRotate.
        public static ColorMatrix HueRotate(double angleDegrees) {
            double rad = angleDegrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            // SVG/CSS hueRotate matrix from the spec.
            return new ColorMatrix {
                Row0 = new Vector4(
                    0.213f + cos * 0.787f - sin * 0.213f,
                    0.715f - cos * 0.715f - sin * 0.715f,
                    0.072f - cos * 0.072f + sin * 0.928f,
                    0),
                Row1 = new Vector4(
                    0.213f - cos * 0.213f + sin * 0.143f,
                    0.715f + cos * 0.285f + sin * 0.140f,
                    0.072f - cos * 0.072f - sin * 0.283f,
                    0),
                Row2 = new Vector4(
                    0.213f - cos * 0.213f - sin * 0.787f,
                    0.715f - cos * 0.715f + sin * 0.715f,
                    0.072f + cos * 0.928f + sin * 0.072f,
                    0),
                Row3 = new Vector4(0, 0, 0, 1),
                Bias = Vector4.zero
            };
        }

        // CSS opacity(amount). Scales alpha; the shader's color-matrix pass operates on
        // straight-alpha rgb, so we encode this as identity + alpha row scaling. The shader
        // multiplies bias.w into the output alpha pre-premul; doing it via Bias is the
        // cleanest formulation here.
        public static ColorMatrix Opacity(double amount) {
            // Implement by scaling the rgb output by amount (since the shader re-premultiplies
            // by src.a, scaling rgb is equivalent for premul-display). The shader keeps src.a
            // untouched.
            float a = (float)Math.Max(0, Math.Min(1, amount));
            return new ColorMatrix {
                Row0 = new Vector4(a, 0, 0, 0),
                Row1 = new Vector4(0, a, 0, 0),
                Row2 = new Vector4(0, 0, a, 0),
                Row3 = new Vector4(0, 0, 0, a),
                Bias = Vector4.zero
            };
        }
    }
}
#endif
