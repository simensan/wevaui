using System;
using System.Globalization;

namespace Weva.Css.Values {
    public sealed class CssColor : CssValue {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public float A { get; }

        public override CssValueKind Kind => CssValueKind.Color;

        public CssColor(byte r, byte g, byte b, float a) {
            R = r; G = g; B = b; A = a;
            Raw = BuildRaw(r, g, b, a);
        }

        public CssColor(byte r, byte g, byte b, float a, string raw) {
            R = r; G = g; B = b; A = a;
            Raw = raw;
        }

        static string BuildRaw(byte r, byte g, byte b, float a) {
            if (a >= 1f - 0.0001f) {
                return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, b);
            }
            return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3})", r, g, b, a);
        }

        public static bool TryFromName(string name, out CssColor color) {
            if (CssNamedColors.TryGet(name, out byte r, out byte g, out byte b, out float a)) {
                color = new CssColor(r, g, b, a);
                return true;
            }
            color = null;
            return false;
        }

        public static CssColor FromHex(string hexBody, int errorCol) {
            if (string.IsNullOrEmpty(hexBody)) {
                throw new CssValueParseException("Empty hex color", errorCol);
            }
            for (int i = 0; i < hexBody.Length; i++) {
                if (!IsHex(hexBody[i])) {
                    throw new CssValueParseException("Invalid hex digit '" + hexBody[i] + "' in color", errorCol);
                }
            }
            byte r, g, b;
            float a = 1f;
            switch (hexBody.Length) {
                case 3:
                    r = (byte)(HexVal(hexBody[0]) * 17);
                    g = (byte)(HexVal(hexBody[1]) * 17);
                    b = (byte)(HexVal(hexBody[2]) * 17);
                    break;
                case 4:
                    r = (byte)(HexVal(hexBody[0]) * 17);
                    g = (byte)(HexVal(hexBody[1]) * 17);
                    b = (byte)(HexVal(hexBody[2]) * 17);
                    a = (HexVal(hexBody[3]) * 17) / 255f;
                    break;
                case 6:
                    r = (byte)((HexVal(hexBody[0]) << 4) | HexVal(hexBody[1]));
                    g = (byte)((HexVal(hexBody[2]) << 4) | HexVal(hexBody[3]));
                    b = (byte)((HexVal(hexBody[4]) << 4) | HexVal(hexBody[5]));
                    break;
                case 8:
                    r = (byte)((HexVal(hexBody[0]) << 4) | HexVal(hexBody[1]));
                    g = (byte)((HexVal(hexBody[2]) << 4) | HexVal(hexBody[3]));
                    b = (byte)((HexVal(hexBody[4]) << 4) | HexVal(hexBody[5]));
                    a = ((HexVal(hexBody[6]) << 4) | HexVal(hexBody[7])) / 255f;
                    break;
                default:
                    throw new CssValueParseException("Hex color must be 3, 4, 6, or 8 digits", errorCol);
            }
            return new CssColor(r, g, b, a, "#" + CssStringUtil.ToLowerInvariantOrSame(hexBody));
        }

        public static CssColor FromRgb(double rChan, double gChan, double bChan, double alpha, bool rgbPercent, string raw) {
            byte r = ChannelByte(rChan, rgbPercent);
            byte g = ChannelByte(gChan, rgbPercent);
            byte b = ChannelByte(bChan, rgbPercent);
            float a = (float)Clamp01(alpha);
            return new CssColor(r, g, b, a, raw);
        }

        public static CssColor FromHsl(double hueDeg, double satPct, double lightPct, double alpha, string raw) {
            double h = ((hueDeg % 360) + 360) % 360;
            double s = Clamp01(satPct / 100.0);
            double l = Clamp01(lightPct / 100.0);
            // D2: HSL->RGB math lives in ColorMath (LA6 consolidation); CssColor's
            // public FromHsl/FromHwb wrappers handle the CSS-spec 0..100 input
            // scale + alpha packing, then delegate the 0..1 conversion.
            ColorMath.HslToRgb01(h, s, l, out double rD, out double gD, out double bD);
            byte r = ChannelByte(rD * 255.0, false);
            byte g = ChannelByte(gD * 255.0, false);
            byte b = ChannelByte(bD * 255.0, false);
            float a = (float)Clamp01(alpha);
            return new CssColor(r, g, b, a, raw);
        }

        public static CssColor FromHwb(double hueDeg, double whitePct, double blackPct, double alpha, string raw) {
            double h = ((hueDeg % 360) + 360) % 360;
            double w = Clamp01(whitePct / 100.0);
            double bk = Clamp01(blackPct / 100.0);
            // CSS Color 4 §10: if w + b >= 1 the result is a shade of grey at w/(w+b).
            if (w + bk >= 1.0) {
                double grey = w / (w + bk);
                byte gv = ChannelByte(grey * 255.0, false);
                return new CssColor(gv, gv, gv, (float)Clamp01(alpha), raw);
            }
            // D2: delegate to shared ColorMath (LA6) — H=hue, S=1, L=0.5 yields the
            // pure-hue baseline used by CSS Color 4 §7's HWB mixing formula.
            ColorMath.HslToRgb01(h, 1.0, 0.5, out double rD, out double gD, out double bD);
            rD = rD * (1 - w - bk) + w;
            gD = gD * (1 - w - bk) + w;
            bD = bD * (1 - w - bk) + w;
            byte r = ChannelByte(rD * 255.0, false);
            byte g = ChannelByte(gD * 255.0, false);
            byte b = ChannelByte(bD * 255.0, false);
            return new CssColor(r, g, b, (float)Clamp01(alpha), raw);
        }

        public static CssColor FromOklab(double lightness, double aAxis, double bAxis, double alpha, string raw) {
            OklabToLinearRgb(lightness, aAxis, bAxis, out double lr, out double lg, out double lb);
            byte r = ChannelByte(LinearToSrgb(lr) * 255.0, false);
            byte g = ChannelByte(LinearToSrgb(lg) * 255.0, false);
            byte b = ChannelByte(LinearToSrgb(lb) * 255.0, false);
            return new CssColor(r, g, b, (float)Clamp01(alpha), raw);
        }

        // CSS Color 4 §10.1: CIELab is D50-referenced. The pipeline is
        // Lab -> XYZ(D50) -> Bradford(D50->D65) -> linear sRGB -> encoded sRGB.
        // This is intentionally separate from the OKLab path: oklab uses a
        // single matrix pair into linear sRGB (D65 throughout) and bypasses
        // chromatic adaptation, so reusing its helper would silently shift
        // whites.
        public static CssColor FromLab(double L, double aAxis, double bAxis, double alpha, string raw) {
            LabToLinearRgb(L, aAxis, bAxis, out double lr, out double lg, out double lb);
            byte r = ChannelByte(LinearToSrgb(lr) * 255.0, false);
            byte g = ChannelByte(LinearToSrgb(lg) * 255.0, false);
            byte b = ChannelByte(LinearToSrgb(lb) * 255.0, false);
            return new CssColor(r, g, b, (float)Clamp01(alpha), raw);
        }

        public static CssColor FromLch(double L, double chroma, double hueDeg, double alpha, string raw) {
            if (chroma < 0) chroma = 0;
            double h = hueDeg * Math.PI / 180.0;
            double a = chroma * Math.Cos(h);
            double b = chroma * Math.Sin(h);
            return FromLab(L, a, b, alpha, raw);
        }

        public static CssColor FromOklch(double lightness, double chroma, double hueDeg, double alpha, string raw) {
            // CSS Color 4 §10.3: "If the chroma given is less than 0, it is
            // clamped to 0." Without the clamp a negative chroma flips the
            // hue 180° (since `-c·cos(θ)` = `c·cos(θ+π)`) — e.g.
            // `oklch(0.5 -0.1 0)` rendered as green-blue rather than the
            // spec-mandated neutral grey. Negative values can surface via
            // calc(), interpolation, or animation overlays.
            if (chroma < 0) chroma = 0;
            double h = hueDeg * Math.PI / 180.0;
            double a = chroma * Math.Cos(h);
            double b = chroma * Math.Sin(h);
            return FromOklab(lightness, a, b, alpha, raw);
        }

        // CSS Color 4 §15/§17: `color(<colorspace> <c1> <c2> <c3> [ / <alpha> ])`.
        // Supported spaces: srgb, srgb-linear, display-p3, rec2020, a98-rgb,
        // prophoto-rgb (D50), xyz / xyz-d65, xyz-d50. All paths converge on linear
        // sRGB (D65), then encode via LinearToSrgb. Out-of-gamut channels clamp.
        public static CssColor FromColorFunction(string spaceLower, double c1, double c2, double c3, double alpha, string raw) {
            if (spaceLower == "srgb") {
                byte r = ChannelByte(c1 * 255.0, false);
                byte g = ChannelByte(c2 * 255.0, false);
                byte b = ChannelByte(c3 * 255.0, false);
                return new CssColor(r, g, b, (float)Clamp01(alpha), raw);
            }
            if (spaceLower == "srgb-linear") {
                return EncodeLinearSrgb(c1, c2, c3, alpha, raw);
            }
            if (spaceLower == "display-p3") {
                // P3 uses the sRGB transfer function on encoded channels.
                double lr = SrgbToLinear(c1);
                double lg = SrgbToLinear(c2);
                double lb = SrgbToLinear(c3);
                DisplayP3LinearToXyzD65(lr, lg, lb, out double x, out double y, out double z);
                XyzD65ToLinearSrgb(x, y, z, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            if (spaceLower == "rec2020") {
                double lr = Rec2020ToLinear(c1);
                double lg = Rec2020ToLinear(c2);
                double lb = Rec2020ToLinear(c3);
                Rec2020LinearToXyzD65(lr, lg, lb, out double x, out double y, out double z);
                XyzD65ToLinearSrgb(x, y, z, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            if (spaceLower == "a98-rgb") {
                double lr = A98ToLinear(c1);
                double lg = A98ToLinear(c2);
                double lb = A98ToLinear(c3);
                A98LinearToXyzD65(lr, lg, lb, out double x, out double y, out double z);
                XyzD65ToLinearSrgb(x, y, z, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            if (spaceLower == "prophoto-rgb") {
                // ProPhoto is D50-referenced; route through Bradford to D65.
                double lr = ProPhotoToLinear(c1);
                double lg = ProPhotoToLinear(c2);
                double lb = ProPhotoToLinear(c3);
                ProPhotoLinearToXyzD50(lr, lg, lb, out double x50, out double y50, out double z50);
                BradfordD50ToD65(x50, y50, z50, out double x65, out double y65, out double z65);
                XyzD65ToLinearSrgb(x65, y65, z65, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            if (spaceLower == "xyz" || spaceLower == "xyz-d65") {
                XyzD65ToLinearSrgb(c1, c2, c3, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            if (spaceLower == "xyz-d50") {
                BradfordD50ToD65(c1, c2, c3, out double x65, out double y65, out double z65);
                XyzD65ToLinearSrgb(x65, y65, z65, out double r, out double g, out double b);
                return EncodeLinearSrgb(r, g, b, alpha, raw);
            }
            throw new CssValueParseException("Unsupported color() colorspace '" + spaceLower + "'", 1);
        }

        static CssColor EncodeLinearSrgb(double lr, double lg, double lb, double alpha, string raw) {
            byte r = ChannelByte(LinearToSrgb(lr) * 255.0, false);
            byte g = ChannelByte(LinearToSrgb(lg) * 255.0, false);
            byte b = ChannelByte(LinearToSrgb(lb) * 255.0, false);
            return new CssColor(r, g, b, (float)Clamp01(alpha), raw);
        }

        // CSS Color 4 §17: linear Display-P3 -> XYZ(D65).
        static void DisplayP3LinearToXyzD65(double r, double g, double b, out double x, out double y, out double z) {
            x = 0.4865709486482162 * r + 0.26566769316909306 * g + 0.1982172852343625  * b;
            y = 0.2289745640697488 * r + 0.6917385218365064  * g + 0.079286914093745   * b;
            z = 0.0000000000000000 * r + 0.04511338185890264 * g + 1.043944368900976   * b;
        }

        // CSS Color 4 §17: linear Rec.2020 -> XYZ(D65).
        static void Rec2020LinearToXyzD65(double r, double g, double b, out double x, out double y, out double z) {
            x = 0.6369580483012914 * r + 0.14461690358620832 * g + 0.1688809751641721  * b;
            y = 0.2627002120112671 * r + 0.6779980715188708  * g + 0.05930171646986196 * b;
            z = 0.0000000000000000 * r + 0.028072693049087428 * g + 1.060985057710791  * b;
        }

        // CSS Color 4 §17: linear Adobe RGB (1998) -> XYZ(D65).
        static void A98LinearToXyzD65(double r, double g, double b, out double x, out double y, out double z) {
            x = 0.5766690429101305  * r + 0.1855582379065463  * g + 0.1882286462349947  * b;
            y = 0.29734497525053605 * r + 0.6273635662554661  * g + 0.07529145849399788 * b;
            z = 0.02703136138641234 * r + 0.07068885253582723 * g + 0.9913375368376388  * b;
        }

        // CSS Color 4 §17: linear ProPhoto RGB -> XYZ(D50). ProPhoto is D50-native.
        static void ProPhotoLinearToXyzD50(double r, double g, double b, out double x, out double y, out double z) {
            x = 0.7977604896723027  * r + 0.13518583717574031 * g + 0.0313493495815248   * b;
            y = 0.2880711282292934  * r + 0.7118432178101014  * g + 0.00008565396060525902 * b;
            z = 0.0                 * r + 0.0                 * g + 0.8251046025104601   * b;
        }

        // CSS Color 4 §17: XYZ(D65) -> linear sRGB (sRGB IEC 61966-2-1 inverse matrix).
        internal static void XyzD65ToLinearSrgb(double x, double y, double z, out double r, out double g, out double b) {
            r =  3.2409699419045226 * x + -1.5373831775700939 * y + -0.4986107602930034 * z;
            g = -0.9692436362808796 * x +  1.8759675015077202 * y +  0.0415550574071756 * z;
            b =  0.0556300796969936 * x + -0.2039769588889765 * y +  1.0569715142428784 * z;
        }

        // Bradford D50 -> D65 chromatic adaptation (CSS Color 4 §17). Mirrors the
        // matrix already used inline in LabToLinearRgb; lifted to a helper so the
        // ProPhoto and xyz-d50 paths can reuse it.
        internal static void BradfordD50ToD65(double x50, double y50, double z50, out double x65, out double y65, out double z65) {
            x65 =  0.9554734527042182  * x50 + -0.0230985368742614 * y50 +  0.0632593086610217 * z50;
            y65 = -0.0283697069632081  * x50 +  1.0099954580058226 * y50 +  0.0210413821353089 * z50;
            z65 =  0.0123140016883199  * x50 + -0.0205076964334779 * y50 +  1.3303659366080753 * z50;
        }

        // Bradford D65 -> D50 chromatic adaptation (inverse of BradfordD50ToD65).
        // Required for color-mix() in lab/lch: linear sRGB → XYZ(D65) → D50 → CIELab.
        internal static void BradfordD65ToD50(double x65, double y65, double z65, out double x50, out double y50, out double z50) {
            x50 =  1.0478112677588977 * x65 +  0.0228765975696867 * y65 + -0.0501923017570389 * z65;
            y50 =  0.0295424410940386 * x65 +  0.9904844032088797 * y65 + -0.0170490933832448 * z65;
            z50 = -0.0092344997461073 * x65 +  0.0150436558061093 * y65 +  0.7521316686031975 * z65;
        }

        // linear sRGB -> XYZ(D65) (inverse of XyzD65ToLinearSrgb).
        internal static void LinearSrgbToXyzD65(double r, double g, double b, out double x, out double y, out double z) {
            x = 0.4123907992659595 * r + 0.3575843393838160 * g + 0.1804807884018343 * b;
            y = 0.2126390058715102 * r + 0.7151686787677560 * g + 0.0721923153607337 * b;
            z = 0.0193308187155918 * r + 0.1191947797946259 * g + 0.9505321522496608 * b;
        }

        // XYZ(D65) -> linear Display-P3 (inverse of DisplayP3LinearToXyzD65).
        internal static void XyzD65ToDisplayP3Linear(double x, double y, double z, out double r, out double g, out double b) {
            r =  2.4934969119414245 * x + -0.9313836179191240 * y + -0.4027107844507168 * z;
            g = -0.8294889695615748 * x +  1.7626640603183463 * y +  0.0236246858419436 * z;
            b =  0.0358458302437845 * x + -0.0761723892680418 * y +  0.9568845240076872 * z;
        }

        // XYZ(D65) -> linear Rec.2020 (inverse of Rec2020LinearToXyzD65).
        internal static void XyzD65ToRec2020Linear(double x, double y, double z, out double r, out double g, out double b) {
            r =  1.7166511879712674 * x + -0.3556708179658125 * y + -0.2533662813736598 * z;
            g = -0.6666843518324892 * x +  1.6164812366349393 * y +  0.0157685458139172 * z;
            b =  0.0176398574453108 * x + -0.0427706132578085 * y +  0.9421031212354738 * z;
        }

        // XYZ(D65) -> linear Adobe RGB (1998) (inverse of A98LinearToXyzD65).
        internal static void XyzD65ToA98Linear(double x, double y, double z, out double r, out double g, out double b) {
            r =  2.0415879038107327 * x + -0.5650069742788596 * y + -0.3447313507783295 * z;
            g = -0.9692436362808795 * x +  1.8759675015077205 * y +  0.0415550574071756 * z;
            b =  0.0134442806320312 * x + -0.1183623922310184 * y +  1.0151749944210190 * z;
        }

        // XYZ(D50) -> linear ProPhoto RGB (inverse of ProPhotoLinearToXyzD50).
        internal static void XyzD50ToProPhotoLinear(double x, double y, double z, out double r, out double g, out double b) {
            r =  1.3457989731016764 * x + -0.2555801068097805 * y + -0.0511115744019041 * z;
            g = -0.5446224939028347 * x +  1.5082327413132781 * y +  0.0205224229958984 * z;
            b =  0.0000000000000000 * x +  0.0000000000000000 * y +  1.2118492020399579 * z;
        }

        // XYZ(D50) -> CIELab. Inverse of the Lab→XYZ(D50) step in LabToLinearRgb.
        // X50n,Y50n,Z50n = D50 reference white (same as LabToLinearRgb).
        internal static void XyzD50ToLab(double x50, double y50, double z50, out double L, out double a, out double b) {
            const double Xn = 0.96422;
            const double Yn = 1.00000;
            const double Zn = 0.82521;
            double fx = LabF(x50 / Xn);
            double fy = LabF(y50 / Yn);
            double fz = LabF(z50 / Zn);
            L = 116.0 * fy - 16.0;
            a = 500.0 * (fx - fy);
            b = 200.0 * (fy - fz);
        }

        // CIELab forward transfer: f(t) = cbrt(t) if t > delta^3, else (t/(3*delta^2)) + 4/29.
        static double LabF(double t) {
            const double delta = 6.0 / 29.0;
            const double delta3 = delta * delta * delta; // ≈ 0.008856
            if (t > delta3) return Cbrt(t);
            return t / (3.0 * delta * delta) + 16.0 / 116.0;
        }

        // Linear sRGB -> CIELab (via XYZ(D65) -> Bradford D65->D50 -> XYZ(D50) -> Lab).
        // Used by ColorMixer for color-mix() in lab/lch spaces.
        internal static void LinearSrgbToLab(double r, double g, double b, out double L, out double a, out double bAxis) {
            LinearSrgbToXyzD65(r, g, b, out double x65, out double y65, out double z65);
            BradfordD65ToD50(x65, y65, z65, out double x50, out double y50, out double z50);
            XyzD50ToLab(x50, y50, z50, out L, out a, out bAxis);
        }

        // CIELab -> linear sRGB (inverse path: Lab -> XYZ(D50) -> Bradford D50->D65 -> XYZ(D65) -> linear sRGB).
        // Already implemented as LabToLinearRgb — alias provided for symmetry in ColorMixer.
        internal static void LabToLinearSrgb(double L, double a, double b, out double r, out double g, out double bl) {
            LabToLinearRgb(L, a, b, out r, out g, out bl);
        }

        // sRGB -> linear P3 (encode-decode round-trip needed for color-mix() in display-p3).
        // Step: sRGB bytes -> linear sRGB -> XYZ(D65) -> linear P3.
        internal static void LinearSrgbToDisplayP3Linear(double r, double g, double b, out double pr, out double pg, out double pb) {
            LinearSrgbToXyzD65(r, g, b, out double x, out double y, out double z);
            XyzD65ToDisplayP3Linear(x, y, z, out pr, out pg, out pb);
        }

        // linear sRGB -> linear Rec.2020.
        internal static void LinearSrgbToRec2020Linear(double r, double g, double b, out double r2, out double g2, out double b2) {
            LinearSrgbToXyzD65(r, g, b, out double x, out double y, out double z);
            XyzD65ToRec2020Linear(x, y, z, out r2, out g2, out b2);
        }

        // linear sRGB -> linear Adobe RGB (1998).
        internal static void LinearSrgbToA98Linear(double r, double g, double b, out double ar, out double ag, out double ab) {
            LinearSrgbToXyzD65(r, g, b, out double x, out double y, out double z);
            XyzD65ToA98Linear(x, y, z, out ar, out ag, out ab);
        }

        // linear sRGB -> linear ProPhoto RGB (via XYZ(D65) -> Bradford D65->D50 -> XYZ(D50) -> ProPhoto).
        internal static void LinearSrgbToProPhotoLinear(double r, double g, double b, out double pr, out double pg, out double pb) {
            LinearSrgbToXyzD65(r, g, b, out double x65, out double y65, out double z65);
            BradfordD65ToD50(x65, y65, z65, out double x50, out double y50, out double z50);
            XyzD50ToProPhotoLinear(x50, y50, z50, out pr, out pg, out pb);
        }

        // linear ProPhoto -> linear sRGB (inverse path).
        internal static void ProPhotoLinearToLinearSrgb(double pr, double pg, double pb, out double r, out double g, out double b) {
            ProPhotoLinearToXyzD50(pr, pg, pb, out double x50, out double y50, out double z50);
            BradfordD50ToD65(x50, y50, z50, out double x65, out double y65, out double z65);
            XyzD65ToLinearSrgb(x65, y65, z65, out r, out g, out b);
        }

        // linear Display-P3 -> linear sRGB.
        internal static void DisplayP3LinearToLinearSrgb(double pr, double pg, double pb, out double r, out double g, out double b) {
            DisplayP3LinearToXyzD65(pr, pg, pb, out double x, out double y, out double z);
            XyzD65ToLinearSrgb(x, y, z, out r, out g, out b);
        }

        // linear Rec.2020 -> linear sRGB.
        internal static void Rec2020LinearToLinearSrgb(double r2, double g2, double b2, out double r, out double g, out double b) {
            Rec2020LinearToXyzD65(r2, g2, b2, out double x, out double y, out double z);
            XyzD65ToLinearSrgb(x, y, z, out r, out g, out b);
        }

        // linear Adobe RGB (1998) -> linear sRGB.
        internal static void A98LinearToLinearSrgb(double ar, double ag, double ab, out double r, out double g, out double b) {
            A98LinearToXyzD65(ar, ag, ab, out double x, out double y, out double z);
            XyzD65ToLinearSrgb(x, y, z, out r, out g, out b);
        }

        // Rec.2020 transfer function inverse (BT.2020 OETF^-1). CSS Color 4 §17.
        static double Rec2020ToLinear(double v) {
            const double alpha = 1.09929682680944;
            const double beta = 0.018053968510807;
            double sign = v < 0 ? -1.0 : 1.0;
            double a = Math.Abs(v);
            if (a < beta * 4.5) return v / 4.5;
            return sign * Math.Pow((a + alpha - 1.0) / alpha, 1.0 / 0.45);
        }

        // Adobe RGB (1998) transfer function inverse: simple 2.19921875 gamma.
        static double A98ToLinear(double v) {
            double sign = v < 0 ? -1.0 : 1.0;
            return sign * Math.Pow(Math.Abs(v), 563.0 / 256.0);
        }

        // ProPhoto RGB transfer function inverse (1.8 gamma with linear toe).
        static double ProPhotoToLinear(double v) {
            const double et2 = 16.0 / 512.0;
            double sign = v < 0 ? -1.0 : 1.0;
            double a = Math.Abs(v);
            if (a <= et2) return v / 16.0;
            return sign * Math.Pow(a, 1.8);
        }

        // HSL->RGB consolidated into ColorMath.HslToRgb01 in LA6 (D2). The two
        // local callers (FromHsl / FromHwb) divide the spec's 0..100 inputs by
        // 100 and clamp/wrap before delegating; the math itself is identical.

        // OKLab <-> linear sRGB via the matrices in CSS Color 4 §11 (Björn Ottosson, 2020).
        public static void OklabToLinearRgb(double L, double a, double b, out double r, out double g, out double bl) {
            double lP = L + 0.3963377774 * a + 0.2158037573 * b;
            double mP = L - 0.1055613458 * a - 0.0638541728 * b;
            double sP = L - 0.0894841775 * a - 1.2914855480 * b;
            double l = lP * lP * lP;
            double m = mP * mP * mP;
            double s = sP * sP * sP;
            r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        }

        public static void LinearRgbToOklab(double r, double g, double b, out double L, out double a, out double bAxis) {
            double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
            double lP = Cbrt(l);
            double mP = Cbrt(m);
            double sP = Cbrt(s);
            L = 0.2104542553 * lP + 0.7936177850 * mP - 0.0040720468 * sP;
            a = 1.9779984951 * lP - 2.4285922050 * mP + 0.4505937099 * sP;
            bAxis = 0.0259040371 * lP + 0.7827717662 * mP - 0.8086757660 * sP;
        }

        // CIELab -> XYZ(D50) per CSS Color 4 §10.1 / CIE 15:2004.
        // f^-1(t) = t^3 if t > δ, else (t - 16/116) * 3δ^2, with δ = 6/29.
        // X = Xn·f^-1((L+16)/116 + a/500), Y = Yn·f^-1((L+16)/116),
        // Z = Zn·f^-1((L+16)/116 - b/200). Xn,Yn,Zn = D50 reference white.
        public static void LabToLinearRgb(double L, double a, double b, out double r, out double g, out double bl) {
            const double Xn = 0.96422;
            const double Yn = 1.00000;
            const double Zn = 0.82521;
            double fy = (L + 16.0) / 116.0;
            double fx = fy + a / 500.0;
            double fz = fy - b / 200.0;
            double x50 = Xn * LabFInv(fx);
            double y50 = Yn * LabFInv(fy);
            double z50 = Zn * LabFInv(fz);

            // Bradford D50 -> D65 chromatic adaptation (CSS Color 4 §17 reference matrix).
            double x65 =  0.9554734527042182  * x50 + -0.0230985368742614 * y50 +  0.0632593086610217 * z50;
            double y65 = -0.0283697069632081  * x50 +  1.0099954580058226 * y50 +  0.0210413821353089 * z50;
            double z65 =  0.0123140016883199  * x50 + -0.0205076964334779 * y50 +  1.3303659366080753 * z50;

            // XYZ(D65) -> linear sRGB (sRGB IEC 61966-2-1 inverse matrix, CSS Color 4 §17).
            r  =  3.2409699419045226 * x65 + -1.5373831775700939 * y65 + -0.4986107602930034 * z65;
            g  = -0.9692436362808796 * x65 +  1.8759675015077202 * y65 +  0.0415550574071756 * z65;
            bl = +0.0556300796969936 * x65 + -0.2039769588889765 * y65 +  1.0569715142428784 * z65;
        }

        static double LabFInv(double t) {
            const double delta = 6.0 / 29.0;
            if (t > delta) return t * t * t;
            return (t - 16.0 / 116.0) * 3.0 * delta * delta;
        }

        public static double LinearToSrgb(double v) {
            if (double.IsNaN(v)) return 0;
            if (v <= 0) return 0;
            if (v >= 1) return 1;
            if (v <= 0.0031308) return v * 12.92;
            return 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
        }

        public static double SrgbToLinear(double v) {
            if (v <= 0) return 0;
            if (v >= 1) return 1;
            if (v <= 0.04045) return v / 12.92;
            return Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Cbrt(double v) {
            if (v < 0) return -Math.Pow(-v, 1.0 / 3.0);
            return Math.Pow(v, 1.0 / 3.0);
        }

        static byte ChannelByte(double v, bool percent) {
            if (percent) v = v * 2.55;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)Math.Round(v);
        }

        static double Clamp01(double v) {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        static int HexVal(char c) {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            return 10 + (c - 'A');
        }
    }
}
