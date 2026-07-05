using System;
using Weva.Css.Values;

namespace Weva.Paint {
    public static class GradientInterpolation {
        public static LinearColor Interpolate(LinearColor a, LinearColor b, double t, CssColorSpace space) {
            return Interpolate(a, b, t, space, CssHueInterpolationMethod.Shorter);
        }

        public static LinearColor Interpolate(LinearColor a, LinearColor b, double t, CssColorSpace space, CssHueInterpolationMethod hueMethod) {
            if (t <= 0) return a;
            if (t >= 1) return b;
            switch (space) {
                case CssColorSpace.Srgb:
                    return LerpSrgb(a, b, t);
                case CssColorSpace.LinearRgb:
                    return LerpLinear(a, b, t);
                case CssColorSpace.Oklab:
                    return LerpOklab(a, b, t);
                case CssColorSpace.Oklch:
                    return LerpOklch(a, b, t, hueMethod);
                case CssColorSpace.Hsl:
                    return LerpHsl(a, b, t, hueMethod);
                case CssColorSpace.Hwb:
                    return LerpHwb(a, b, t, hueMethod);
                default:
                    return LerpLinear(a, b, t);
            }
        }

        static LinearColor LerpLinear(LinearColor a, LinearColor b, double t) {
            float ft = (float)t;
            return new LinearColor(
                a.R + (b.R - a.R) * ft,
                a.G + (b.G - a.G) * ft,
                a.B + (b.B - a.B) * ft,
                a.A + (b.A - a.A) * ft);
        }

        static LinearColor LerpSrgb(LinearColor a, LinearColor b, double t) {
            double aR = CssColor.LinearToSrgb(a.R);
            double aG = CssColor.LinearToSrgb(a.G);
            double aB = CssColor.LinearToSrgb(a.B);
            double bR = CssColor.LinearToSrgb(b.R);
            double bG = CssColor.LinearToSrgb(b.G);
            double bB = CssColor.LinearToSrgb(b.B);
            double r = aR + (bR - aR) * t;
            double g = aG + (bG - aG) * t;
            double bl = aB + (bB - aB) * t;
            float alpha = (float)(a.A + (b.A - a.A) * t);
            return new LinearColor(
                (float)CssColor.SrgbToLinear(r),
                (float)CssColor.SrgbToLinear(g),
                (float)CssColor.SrgbToLinear(bl),
                alpha);
        }

        static LinearColor LerpOklab(LinearColor a, LinearColor b, double t) {
            CssColor.LinearRgbToOklab(a.R, a.G, a.B, out double aL, out double aA, out double aB);
            CssColor.LinearRgbToOklab(b.R, b.G, b.B, out double bL, out double bA, out double bB);
            double L = aL + (bL - aL) * t;
            double A = aA + (bA - aA) * t;
            double B = aB + (bB - aB) * t;
            CssColor.OklabToLinearRgb(L, A, B, out double lr, out double lg, out double lb);
            float alpha = (float)(a.A + (b.A - a.A) * t);
            return new LinearColor(Clamp((float)lr), Clamp((float)lg), Clamp((float)lb), alpha);
        }

        static LinearColor LerpOklch(LinearColor a, LinearColor b, double t, CssHueInterpolationMethod hueMethod) {
            CssColor.LinearRgbToOklab(a.R, a.G, a.B, out double aL, out double aA, out double aB);
            CssColor.LinearRgbToOklab(b.R, b.G, b.B, out double bL, out double bA, out double bB);
            double aC = Math.Sqrt(aA * aA + aB * aB);
            double bC = Math.Sqrt(bA * bA + bB * bB);
            double aH = Math.Atan2(aB, aA);
            double bH = Math.Atan2(bB, bA);
            double L = aL + (bL - aL) * t;
            double C = aC + (bC - aC) * t;
            double H = LerpHueRadians(aH, bH, t, hueMethod);
            double A = C * Math.Cos(H);
            double B = C * Math.Sin(H);
            CssColor.OklabToLinearRgb(L, A, B, out double lr, out double lg, out double lb);
            float alpha = (float)(a.A + (b.A - a.A) * t);
            return new LinearColor(Clamp((float)lr), Clamp((float)lg), Clamp((float)lb), alpha);
        }

        static LinearColor LerpHsl(LinearColor a, LinearColor b, double t, CssHueInterpolationMethod hueMethod) {
            double aR = CssColor.LinearToSrgb(a.R);
            double aG = CssColor.LinearToSrgb(a.G);
            double aB = CssColor.LinearToSrgb(a.B);
            double bR = CssColor.LinearToSrgb(b.R);
            double bG = CssColor.LinearToSrgb(b.G);
            double bB = CssColor.LinearToSrgb(b.B);
            ColorMath.RgbToHsl(aR, aG, aB, out double aH, out double aS, out double aL);
            ColorMath.RgbToHsl(bR, bG, bB, out double bH, out double bS, out double bL);
            double H = LerpHueDegrees(aH, bH, t, hueMethod);
            double S = aS + (bS - aS) * t;
            double L = aL + (bL - aL) * t;
            ColorMath.HslToRgb01(H, S, L, out double r, out double g, out double bl);
            float alpha = (float)(a.A + (b.A - a.A) * t);
            return new LinearColor(
                (float)CssColor.SrgbToLinear(r),
                (float)CssColor.SrgbToLinear(g),
                (float)CssColor.SrgbToLinear(bl),
                alpha);
        }

        static LinearColor LerpHwb(LinearColor a, LinearColor b, double t, CssHueInterpolationMethod hueMethod) {
            double aR = CssColor.LinearToSrgb(a.R);
            double aG = CssColor.LinearToSrgb(a.G);
            double aB = CssColor.LinearToSrgb(a.B);
            double bR = CssColor.LinearToSrgb(b.R);
            double bG = CssColor.LinearToSrgb(b.G);
            double bB = CssColor.LinearToSrgb(b.B);
            ColorMath.RgbToHwb(aR, aG, aB, out double aH, out double aW, out double aBk);
            ColorMath.RgbToHwb(bR, bG, bB, out double bH, out double bW, out double bBk);
            double H = LerpHueDegrees(aH, bH, t, hueMethod);
            double W = aW + (bW - aW) * t;
            double Bk = aBk + (bBk - aBk) * t;
            ColorMath.HwbToRgb01(H, W, Bk, out double r, out double g, out double bl);
            float alpha = (float)(a.A + (b.A - a.A) * t);
            return new LinearColor(
                (float)CssColor.SrgbToLinear(r),
                (float)CssColor.SrgbToLinear(g),
                (float)CssColor.SrgbToLinear(bl),
                alpha);
        }

        // CSS Color 4 §12.3 hue-interpolation-method. Shorter is the spec default.
        static double LerpHueRadians(double a, double b, double t, CssHueInterpolationMethod method) {
            const double TWO_PI = Math.PI * 2;
            double diff = b - a;
            switch (method) {
                case CssHueInterpolationMethod.Shorter:
                    while (diff > Math.PI) diff -= TWO_PI;
                    while (diff < -Math.PI) diff += TWO_PI;
                    break;
                case CssHueInterpolationMethod.Longer:
                    while (diff > Math.PI) diff -= TWO_PI;
                    while (diff < -Math.PI) diff += TWO_PI;
                    if (diff > 0 && diff < Math.PI) diff -= TWO_PI;
                    else if (diff < 0 && diff > -Math.PI) diff += TWO_PI;
                    break;
                case CssHueInterpolationMethod.Increasing:
                    while (diff < 0) diff += TWO_PI;
                    while (diff >= TWO_PI) diff -= TWO_PI;
                    break;
                case CssHueInterpolationMethod.Decreasing:
                    while (diff > 0) diff -= TWO_PI;
                    while (diff <= -TWO_PI) diff += TWO_PI;
                    break;
            }
            return a + diff * t;
        }

        static double LerpHueDegrees(double a, double b, double t, CssHueInterpolationMethod method) {
            double diff = b - a;
            switch (method) {
                case CssHueInterpolationMethod.Shorter:
                    while (diff > 180) diff -= 360;
                    while (diff < -180) diff += 360;
                    break;
                case CssHueInterpolationMethod.Longer:
                    while (diff > 180) diff -= 360;
                    while (diff < -180) diff += 360;
                    if (diff > 0 && diff < 180) diff -= 360;
                    else if (diff < 0 && diff > -180) diff += 360;
                    break;
                case CssHueInterpolationMethod.Increasing:
                    while (diff < 0) diff += 360;
                    while (diff >= 360) diff -= 360;
                    break;
                case CssHueInterpolationMethod.Decreasing:
                    while (diff > 0) diff -= 360;
                    while (diff <= -360) diff += 360;
                    break;
            }
            double r = a + diff * t;
            return ((r % 360) + 360) % 360;
        }

        static float Clamp(float v) {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        // HSL/HWB conversion helpers moved to ColorMath (LA6 consolidation).
    }
}
