using System;
using System.Globalization;

namespace Weva.Css.Values {
    public enum CssHueInterpolationMethod {
        Shorter,
        Longer,
        Increasing,
        Decreasing,
    }

    public static class ColorMixer {
        public static CssColor Mix(CssColor a, CssColor b, CssColorSpace space, double weightA, double weightB, string raw) {
            return Mix(a, b, space, weightA, weightB, CssHueInterpolationMethod.Shorter, raw);
        }

        public static CssColor Mix(CssColor a, CssColor b, CssColorSpace space, double weightA, double weightB, CssHueInterpolationMethod hueMethod, string raw) {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            // CSS Color 5 §3: weights are normalized so they sum to 1; if they don't sum to 1
            // (and neither was omitted), the result alpha is multiplied by the sum.
            double sum = weightA + weightB;
            if (sum <= 0) return new CssColor(a.R, a.G, a.B, a.A, raw);
            double wA = weightA / sum;
            double wB = weightB / sum;
            double alphaScale = sum > 1.0 ? 1.0 : sum;

            double aA = a.A;
            double aB = b.A;
            double resultA = aA * wA + aB * wB;
            double resultAFinal = Clamp01(resultA * alphaScale);

            // Premultiplied-by-alpha mixing when both have alpha; CSS Color 5 §3.
            double pA = aA * wA;
            double pB = aB * wB;
            double pSum = pA + pB;
            double mixA, mixB, mixC;
            if (space == CssColorSpace.Srgb) {
                if (pSum <= 0) { mixA = mixB = mixC = 0; }
                else {
                    mixA = (a.R * pA + b.R * pB) / (pSum * 255.0);
                    mixB = (a.G * pA + b.G * pB) / (pSum * 255.0);
                    mixC = (a.B * pA + b.B * pB) / (pSum * 255.0);
                }
                byte rB = ToByte(mixA * 255.0);
                byte gB = ToByte(mixB * 255.0);
                byte bB = ToByte(mixC * 255.0);
                return new CssColor(rB, gB, bB, (float)resultAFinal, raw);
            }
            if (space == CssColorSpace.LinearRgb) {
                double aR = CssColor.SrgbToLinear(a.R / 255.0);
                double aG = CssColor.SrgbToLinear(a.G / 255.0);
                double aB2 = CssColor.SrgbToLinear(a.B / 255.0);
                double bR = CssColor.SrgbToLinear(b.R / 255.0);
                double bG = CssColor.SrgbToLinear(b.G / 255.0);
                double bB2 = CssColor.SrgbToLinear(b.B / 255.0);
                if (pSum <= 0) { mixA = mixB = mixC = 0; }
                else {
                    mixA = (aR * pA + bR * pB) / pSum;
                    mixB = (aG * pA + bG * pB) / pSum;
                    mixC = (aB2 * pA + bB2 * pB) / pSum;
                }
                return ToCssColor(CssColor.LinearToSrgb(mixA), CssColor.LinearToSrgb(mixB), CssColor.LinearToSrgb(mixC), resultAFinal, raw);
            }
            if (space == CssColorSpace.Oklab) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearRgbToOklab(aR, aG, aBl, out double aL, out double aA1, out double aB1);
                CssColor.LinearRgbToOklab(bR, bG, bBl, out double bL, out double bA1, out double bB1);
                double L = aL * wA + bL * wB;
                double A1 = aA1 * wA + bA1 * wB;
                double B1 = aB1 * wA + bB1 * wB;
                CssColor.OklabToLinearRgb(L, A1, B1, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            if (space == CssColorSpace.Oklch) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearRgbToOklab(aR, aG, aBl, out double aL, out double aA1, out double aB1);
                CssColor.LinearRgbToOklab(bR, bG, bBl, out double bL, out double bA1, out double bB1);
                double aC = Math.Sqrt(aA1 * aA1 + aB1 * aB1);
                double bC = Math.Sqrt(bA1 * bA1 + bB1 * bB1);
                double aH = Math.Atan2(aB1, aA1);
                double bH = Math.Atan2(bB1, bA1);
                // CSS Color 4 §12: a chromatic-coordinate of ~0 means the
                // hue is undefined ("powerless"); use the other endpoint's
                // hue rather than blending atan2 noise from the near-grey
                // side. Without this, mixing oklch(...) with white/black
                // produced a spurious hue rotation through the achromatic
                // pole instead of a clean lightness ramp.
                const double kHueDeadzone = 1e-4;
                if (aC < kHueDeadzone) aH = bH;
                else if (bC < kHueDeadzone) bH = aH;
                double L = aL * wA + bL * wB;
                double C = aC * wA + bC * wB;
                double H = LerpHueRadians(aH, bH, wB, hueMethod);
                double A1m = C * Math.Cos(H);
                double B1m = C * Math.Sin(H);
                CssColor.OklabToLinearRgb(L, A1m, B1m, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            if (space == CssColorSpace.Hsl) {
                ColorMath.RgbToHsl(a.R / 255.0, a.G / 255.0, a.B / 255.0, out double aH, out double aS, out double aL);
                ColorMath.RgbToHsl(b.R / 255.0, b.G / 255.0, b.B / 255.0, out double bH, out double bS, out double bL);
                // Powerless-hue rule, HSL flavor: when saturation is ~0
                // the hue is undefined.
                const double kHueDeadzone = 1e-4;
                if (aS < kHueDeadzone) aH = bH;
                else if (bS < kHueDeadzone) bH = aH;
                double H = LerpHueDegrees(aH, bH, wB, hueMethod);
                double S = aS * wA + bS * wB;
                double L = aL * wA + bL * wB;
                ColorMath.HslToRgb01(H, S, L, out double r2, out double g2, out double b2);
                return ToCssColor(r2, g2, b2, resultAFinal, raw);
            }
            if (space == CssColorSpace.Hwb) {
                ColorMath.RgbToHwb(a.R / 255.0, a.G / 255.0, a.B / 255.0, out double aH, out double aW, out double aBk);
                ColorMath.RgbToHwb(b.R / 255.0, b.G / 255.0, b.B / 255.0, out double bH, out double bW, out double bBk);
                // Powerless-hue rule, HWB flavor: when W+B ≈ 1 the color is
                // achromatic (white-to-black ramp) and hue is undefined.
                const double kHueDeadzone = 1e-4;
                if (1.0 - aW - aBk < kHueDeadzone) aH = bH;
                else if (1.0 - bW - bBk < kHueDeadzone) bH = aH;
                double H = LerpHueDegrees(aH, bH, wB, hueMethod);
                double W = aW * wA + bW * wB;
                double Bk = aBk * wA + bBk * wB;
                ColorMath.HwbToRgb01(H, W, Bk, out double r2, out double g2, out double b2);
                return ToCssColor(r2, g2, b2, resultAFinal, raw);
            }
            // CSS Color 4 §10 — CIELab: convert to Lab, lerp, convert back.
            if (space == CssColorSpace.Lab) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToLab(aR, aG, aBl, out double aL, out double aA1, out double aB1);
                CssColor.LinearSrgbToLab(bR, bG, bBl, out double bL, out double bA1, out double bB1);
                double Lm = aL * wA + bL * wB;
                double Am = aA1 * wA + bA1 * wB;
                double Bm = aB1 * wA + bB1 * wB;
                CssColor.LabToLinearSrgb(Lm, Am, Bm, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §10 — CIELch (cylindrical Lab): lerp L+C, hue-interpolate h.
            if (space == CssColorSpace.Lch) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToLab(aR, aG, aBl, out double aL, out double aA1, out double aB1);
                CssColor.LinearSrgbToLab(bR, bG, bBl, out double bL, out double bA1, out double bB1);
                double aC = Math.Sqrt(aA1 * aA1 + aB1 * aB1);
                double bC = Math.Sqrt(bA1 * bA1 + bB1 * bB1);
                double aH = Math.Atan2(aB1, aA1);
                double bH = Math.Atan2(bB1, bA1);
                // Powerless-hue rule: near-zero chroma → hue is undefined.
                const double kHueDzLch = 1e-4;
                if (aC < kHueDzLch) aH = bH;
                else if (bC < kHueDzLch) bH = aH;
                double Lm = aL * wA + bL * wB;
                double Cm = aC * wA + bC * wB;
                double Hm = LerpHueRadians(aH, bH, wB, hueMethod);
                double Am = Cm * Math.Cos(Hm);
                double Bm = Cm * Math.Sin(Hm);
                CssColor.LabToLinearSrgb(Lm, Am, Bm, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §15 — display-p3: convert to linear P3, lerp, convert back.
            if (space == CssColorSpace.DisplayP3) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToDisplayP3Linear(aR, aG, aBl, out double aPr, out double aPg, out double aPb);
                CssColor.LinearSrgbToDisplayP3Linear(bR, bG, bBl, out double bPr, out double bPg, out double bPb);
                if (pSum <= 0) {
                    return ToCssColor(0, 0, 0, resultAFinal, raw);
                }
                double mPr = (aPr * pA + bPr * pB) / pSum;
                double mPg = (aPg * pA + bPg * pB) / pSum;
                double mPb = (aPb * pA + bPb * pB) / pSum;
                CssColor.DisplayP3LinearToLinearSrgb(mPr, mPg, mPb, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §15 — rec2020: convert to linear Rec.2020, lerp, convert back.
            if (space == CssColorSpace.Rec2020) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToRec2020Linear(aR, aG, aBl, out double aR2, out double aG2, out double aB2);
                CssColor.LinearSrgbToRec2020Linear(bR, bG, bBl, out double bR2, out double bG2, out double bB2);
                if (pSum <= 0) {
                    return ToCssColor(0, 0, 0, resultAFinal, raw);
                }
                double mR2 = (aR2 * pA + bR2 * pB) / pSum;
                double mG2 = (aG2 * pA + bG2 * pB) / pSum;
                double mB2 = (aB2 * pA + bB2 * pB) / pSum;
                CssColor.Rec2020LinearToLinearSrgb(mR2, mG2, mB2, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §15 — a98-rgb: convert to linear A98, lerp, convert back.
            if (space == CssColorSpace.A98Rgb) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToA98Linear(aR, aG, aBl, out double aAr, out double aAg, out double aAb);
                CssColor.LinearSrgbToA98Linear(bR, bG, bBl, out double bAr, out double bAg, out double bAb);
                if (pSum <= 0) {
                    return ToCssColor(0, 0, 0, resultAFinal, raw);
                }
                double mAr = (aAr * pA + bAr * pB) / pSum;
                double mAg = (aAg * pA + bAg * pB) / pSum;
                double mAb = (aAb * pA + bAb * pB) / pSum;
                CssColor.A98LinearToLinearSrgb(mAr, mAg, mAb, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §15 — prophoto-rgb: convert to linear ProPhoto, lerp, convert back.
            if (space == CssColorSpace.ProPhotoRgb) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToProPhotoLinear(aR, aG, aBl, out double aPr, out double aPg, out double aPb);
                CssColor.LinearSrgbToProPhotoLinear(bR, bG, bBl, out double bPr, out double bPg, out double bPb);
                if (pSum <= 0) {
                    return ToCssColor(0, 0, 0, resultAFinal, raw);
                }
                double mPr = (aPr * pA + bPr * pB) / pSum;
                double mPg = (aPg * pA + bPg * pB) / pSum;
                double mPb = (aPb * pA + bPb * pB) / pSum;
                CssColor.ProPhotoLinearToLinearSrgb(mPr, mPg, mPb, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §17 — xyz / xyz-d65: lerp directly in XYZ(D65) space.
            if (space == CssColorSpace.XyzD65) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToXyzD65(aR, aG, aBl, out double aX, out double aY, out double aZ);
                CssColor.LinearSrgbToXyzD65(bR, bG, bBl, out double bX, out double bY, out double bZ);
                double mX = aX * wA + bX * wB;
                double mY = aY * wA + bY * wB;
                double mZ = aZ * wA + bZ * wB;
                CssColor.XyzD65ToLinearSrgb(mX, mY, mZ, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            // CSS Color 4 §17 — xyz-d50: lerp directly in XYZ(D50) space.
            if (space == CssColorSpace.XyzD50) {
                ToLinear(a, out double aR, out double aG, out double aBl);
                ToLinear(b, out double bR, out double bG, out double bBl);
                CssColor.LinearSrgbToXyzD65(aR, aG, aBl, out double aX65, out double aY65, out double aZ65);
                CssColor.LinearSrgbToXyzD65(bR, bG, bBl, out double bX65, out double bY65, out double bZ65);
                CssColor.BradfordD65ToD50(aX65, aY65, aZ65, out double aX, out double aY, out double aZ);
                CssColor.BradfordD65ToD50(bX65, bY65, bZ65, out double bX, out double bY, out double bZ);
                double mX = aX * wA + bX * wB;
                double mY = aY * wA + bY * wB;
                double mZ = aZ * wA + bZ * wB;
                CssColor.BradfordD50ToD65(mX, mY, mZ, out double mX65, out double mY65, out double mZ65);
                CssColor.XyzD65ToLinearSrgb(mX65, mY65, mZ65, out double rOut, out double gOut, out double blOut);
                return ToCssColor(CssColor.LinearToSrgb(rOut), CssColor.LinearToSrgb(gOut), CssColor.LinearToSrgb(blOut), resultAFinal, raw);
            }
            return new CssColor(a.R, a.G, a.B, a.A, raw);
        }

        static void ToLinear(CssColor c, out double r, out double g, out double b) {
            r = CssColor.SrgbToLinear(c.R / 255.0);
            g = CssColor.SrgbToLinear(c.G / 255.0);
            b = CssColor.SrgbToLinear(c.B / 255.0);
        }

        static CssColor ToCssColor(double r, double g, double b, double alpha, string raw) {
            byte rB = ToByte(r * 255.0);
            byte gB = ToByte(g * 255.0);
            byte bB = ToByte(b * 255.0);
            return new CssColor(rB, gB, bB, (float)alpha, raw);
        }

        static byte ToByte(double v) {
            if (double.IsNaN(v) || v <= 0) return 0;
            if (v >= 255) return 255;
            return (byte)Math.Round(v);
        }

        static double Clamp01(double v) {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        // CSS Color 4 §12.3: hue-interpolation-method controls which arc of the
        // hue circle is walked. Shorter is the spec default.
        static double LerpHueRadians(double aH, double bH, double t, CssHueInterpolationMethod method) {
            const double TWO_PI = Math.PI * 2;
            double a = aH;
            double b = bH;
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

        static double LerpHueDegrees(double aH, double bH, double t, CssHueInterpolationMethod method) {
            double a = aH;
            double b = bH;
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
            r = ((r % 360) + 360) % 360;
            return r;
        }

        // HSL/HWB conversion helpers moved to ColorMath (LA6 consolidation).

        public static bool TryParseSpaceName(string name, out CssColorSpace space) {
            space = CssColorSpace.Oklab;
            if (string.IsNullOrEmpty(name)) return false;
            switch (CssStringUtil.ToLowerInvariantOrSame(name)) {
                case "srgb":          space = CssColorSpace.Srgb;        return true;
                case "srgb-linear":   space = CssColorSpace.LinearRgb;   return true;
                case "linear-rgb":    space = CssColorSpace.LinearRgb;   return true;
                case "oklab":         space = CssColorSpace.Oklab;       return true;
                case "oklch":         space = CssColorSpace.Oklch;       return true;
                case "hsl":           space = CssColorSpace.Hsl;         return true;
                case "hwb":           space = CssColorSpace.Hwb;         return true;
                // CSS Color 4 §10 — CIELab / CIELCh (A1 fix)
                case "lab":           space = CssColorSpace.Lab;         return true;
                case "lch":           space = CssColorSpace.Lch;         return true;
                // CSS Color 4 §15/§17 — wide-gamut color() spaces (A1 fix)
                case "display-p3":    space = CssColorSpace.DisplayP3;   return true;
                case "rec2020":       space = CssColorSpace.Rec2020;     return true;
                case "a98-rgb":       space = CssColorSpace.A98Rgb;      return true;
                case "prophoto-rgb":  space = CssColorSpace.ProPhotoRgb; return true;
                case "xyz":
                case "xyz-d65":       space = CssColorSpace.XyzD65;      return true;
                case "xyz-d50":       space = CssColorSpace.XyzD50;      return true;
            }
            return false;
        }

        public static bool TryParseHueMethod(string name, out CssHueInterpolationMethod method) {
            method = CssHueInterpolationMethod.Shorter;
            if (string.IsNullOrEmpty(name)) return false;
            switch (CssStringUtil.ToLowerInvariantOrSame(name)) {
                case "shorter": method = CssHueInterpolationMethod.Shorter; return true;
                case "longer": method = CssHueInterpolationMethod.Longer; return true;
                case "increasing": method = CssHueInterpolationMethod.Increasing; return true;
                case "decreasing": method = CssHueInterpolationMethod.Decreasing; return true;
            }
            return false;
        }

        public static bool IsCylindricalSpace(CssColorSpace space) {
            return space == CssColorSpace.Hsl
                || space == CssColorSpace.Hwb
                || space == CssColorSpace.Oklch
                || space == CssColorSpace.Lch;
        }

        public static string FormatSpace(CssColorSpace space) {
            switch (space) {
                case CssColorSpace.Srgb:        return "srgb";
                case CssColorSpace.LinearRgb:   return "srgb-linear";
                case CssColorSpace.Oklab:        return "oklab";
                case CssColorSpace.Oklch:        return "oklch";
                case CssColorSpace.Hsl:          return "hsl";
                case CssColorSpace.Hwb:          return "hwb";
                case CssColorSpace.Lab:          return "lab";
                case CssColorSpace.Lch:          return "lch";
                case CssColorSpace.DisplayP3:    return "display-p3";
                case CssColorSpace.Rec2020:      return "rec2020";
                case CssColorSpace.A98Rgb:       return "a98-rgb";
                case CssColorSpace.ProPhotoRgb:  return "prophoto-rgb";
                case CssColorSpace.XyzD65:       return "xyz-d65";
                case CssColorSpace.XyzD50:       return "xyz-d50";
            }
            return "oklab";
        }

        internal static string FormatNumber(double v) {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
