#include "weva/color_space.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace weva {

namespace {

constexpr double kPi = 3.14159265358979323846;

// Matrices from CSS Color 4's sample conversion code, row-major.
using Mat = double[3][3];

const Mat kLinSrgbToXyz = {
    {0.41239079926595934, 0.357584339383878, 0.1804807884018343},
    {0.21263900587151027, 0.715168678767756, 0.07219231536073371},
    {0.01933081871559182, 0.11919477979462598, 0.9505321522496607}};
const Mat kXyzToLinSrgb = {
    {3.2409699419045226, -1.537383177570094, -0.4986107602930034},
    {-0.9692436362808796, 1.8759675015077202, 0.04155505740717559},
    {0.05563007969699366, -0.20397695888897652, 1.0569715142428786}};
const Mat kLinP3ToXyz = {
    {0.4865709486482162, 0.26566769316909306, 0.1982172852343625},
    {0.2289745640697488, 0.6917385218365064, 0.079286914093745},
    {0.0, 0.04511338185890264, 1.043944368900976}};
const Mat kXyzToLinP3 = {
    {2.493496911941425, -0.9313836179191239, -0.40271078445071684},
    {-0.8294889695615747, 1.7626640603183463, 0.023624685841943577},
    {0.03584583024378447, -0.07617238926804182, 0.9568845240076872}};
const Mat kLinA98ToXyz = {
    {0.5766690429101305, 0.1855582379065463, 0.1882286462349947},
    {0.29734497525053605, 0.6273635662554661, 0.07529145849399788},
    {0.02703136138641234, 0.07068885253582723, 0.9913375368376388}};
const Mat kXyzToLinA98 = {
    {2.0415879038107465, -0.5650069742788596, -0.34473135077832956},
    {-0.9692436362808795, 1.8759675015077202, 0.04155505740717557},
    {0.013444280632031142, -0.11836239223101838, 1.0151749943912054}};
const Mat kLinProphotoToXyzD50 = {
    {0.7977604896723027, 0.13518583717574031, 0.0313493495815248},
    {0.2880711282292934, 0.7118432196290773, 0.00008565396060525902},
    {0.0, 0.0, 0.8251046025104602}};
const Mat kXyzD50ToLinProphoto = {
    {1.3457989731028281, -0.25558010007997534, -0.05110628506753401},
    {-0.5446224939028347, 1.5082327413132781, 0.02053603239147973},
    {0.0, 0.0, 1.2119675456389454}};
const Mat kLinRec2020ToXyz = {
    {0.6369580483012914, 0.14461690358620832, 0.1688809751641721},
    {0.2627002120112671, 0.6779980715188708, 0.05930171646986196},
    {0.0, 0.028072693049087428, 1.060985057710791}};
const Mat kXyzToLinRec2020 = {
    {1.716651187971268, -0.355670783776392, -0.25336628137365974},
    {-0.6666843518324892, 1.6164812366349395, 0.01576854581391113},
    {0.017639857445310783, -0.042770613257808524, 0.9421031212354738}};
const Mat kD65ToD50 = {
    {1.0479298208405488, 0.022946793341019088, -0.05019222954313557},
    {0.029627815688159344, 0.990434484573249, -0.01707382457882837},
    {-0.009243058152591178, 0.015055144896577895, 0.7518742899580008}};
const Mat kD50ToD65 = {
    {0.9554734527042182, -0.023098536874261423, 0.0632593086610217},
    {-0.028369706963208136, 1.0099954580058226, 0.021041398966943008},
    {0.012314001688319899, -0.020507696433477912, 1.3303659366080753}};
const Mat kXyzToLms = {
    {0.8190224432164319, 0.3619062562801221, -0.12887378261216414},
    {0.0329836671980271, 0.9292868468965546, 0.03614466816999844},
    {0.048177199566046255, 0.26423952494422764, 0.6335478258136937}};
const Mat kLmsToXyz = {
    {1.2268798733741557, -0.5578149965554813, 0.28139105017721583},
    {-0.04057576262431372, 1.1122868293970594, -0.07171106666151701},
    {-0.07637294974672142, -0.4214933239627914, 1.5869240244272418}};
const Mat kLmsToOklab = {
    {0.2104542553, 0.7936177850, -0.0040720468},
    {1.9779984951, -2.4285922050, 0.4505937099},
    {0.0259040371, 0.7827717662, -0.8086757660}};
const Mat kOklabToLms = {
    {0.99999999845051981432, 0.39633779217376785678, 0.21580375806075880339},
    {1.0000000088817607767, -0.1055613423236563494, -0.063854174771705903402},
    {1.0000000546724109177, -0.089484182094965759684, -1.2914855378640917399}};

// The D50 white point Lab is defined against.
const double kD50[3] = {0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585};

void mul(const Mat& m, const double in[3], double out[3]) {
    double t[3];
    for (int i = 0; i < 3; ++i) t[i] = m[i][0] * in[0] + m[i][1] * in[1] + m[i][2] * in[2];
    std::memcpy(out, t, sizeof t);
}

double sign(double v) { return v < 0 ? -1.0 : 1.0; }

// sRGB and display-p3 share the sRGB transfer curve.
double srgb_decode(double c) {
    const double a = std::fabs(c);
    return a <= 0.04045 ? c / 12.92 : sign(c) * std::pow((a + 0.055) / 1.055, 2.4);
}
double srgb_encode(double c) {
    const double a = std::fabs(c);
    return a <= 0.0031308 ? c * 12.92 : sign(c) * (1.055 * std::pow(a, 1.0 / 2.4) - 0.055);
}
double a98_decode(double c) { return sign(c) * std::pow(std::fabs(c), 563.0 / 256.0); }
double a98_encode(double c) { return sign(c) * std::pow(std::fabs(c), 256.0 / 563.0); }
double prophoto_decode(double c) {
    const double a = std::fabs(c);
    return a <= 16.0 / 512.0 ? c / 16.0 : sign(c) * std::pow(a, 1.8);
}
double prophoto_encode(double c) {
    const double a = std::fabs(c);
    return a >= 1.0 / 512.0 ? sign(c) * std::pow(a, 1.0 / 1.8) : c * 16.0;
}
double rec2020_decode(double c) {
    const double alpha = 1.09929682680944, beta = 0.018053968510807;
    const double a = std::fabs(c);
    return a < beta * 4.5 ? c / 4.5 : sign(c) * std::pow((a + alpha - 1) / alpha, 1.0 / 0.45);
}
double rec2020_encode(double c) {
    const double alpha = 1.09929682680944, beta = 0.018053968510807;
    const double a = std::fabs(c);
    return a > beta ? sign(c) * (alpha * std::pow(a, 0.45) - (alpha - 1)) : c * 4.5;
}

double norm_hue(double h) {
    h = std::fmod(h, 360.0);
    return h < 0 ? h + 360.0 : h;
}

// CSS Color 4 §7 HSL -> sRGB, hue in degrees, s/l 0..1.
void hsl_to_rgb(double h, double s, double l, double out[3]) {
    h = norm_hue(h);
    const auto f = [&](double n) {
        const double k = std::fmod(n + h / 30.0, 12.0);
        const double a = s * std::min(l, 1 - l);
        return l - a * std::max(-1.0, std::min(std::min(k - 3, 9 - k), 1.0));
    };
    out[0] = f(0); out[1] = f(8); out[2] = f(4);
}

void rgb_to_hsl(const double in[3], double out[3]) {
    const double r = in[0], g = in[1], b = in[2];
    const double mx = std::max(r, std::max(g, b)), mn = std::min(r, std::min(g, b));
    const double l = (mx + mn) / 2, d = mx - mn;
    double h = 0, s = 0;
    if (d > 1e-12) {
        s = (l == 0 || l == 1) ? 0 : (mx - l) / std::min(l, 1 - l);
        if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }
    out[0] = norm_hue(h); out[1] = s * 100; out[2] = l * 100;
}

void hwb_to_rgb(double h, double w, double bk, double out[3]) {
    if (w + bk >= 1) {
        const double grey = w / (w + bk);
        out[0] = out[1] = out[2] = grey;
        return;
    }
    hsl_to_rgb(h, 1, 0.5, out);
    for (int i = 0; i < 3; ++i) out[i] = out[i] * (1 - w - bk) + w;
}

void rgb_to_hwb(const double in[3], double out[3]) {
    double hsl[3];
    rgb_to_hsl(in, hsl);
    const double w = std::min(in[0], std::min(in[1], in[2]));
    const double bk = 1 - std::max(in[0], std::max(in[1], in[2]));
    out[0] = hsl[0]; out[1] = w * 100; out[2] = bk * 100;
}

void xyz_d50_to_lab(const double xyz[3], double lab[3]) {
    const double e = 216.0 / 24389.0, k = 24389.0 / 27.0;
    double f[3];
    for (int i = 0; i < 3; ++i) {
        const double v = xyz[i] / kD50[i];
        f[i] = v > e ? std::cbrt(v) : (k * v + 16) / 116;
    }
    lab[0] = 116 * f[1] - 16;
    lab[1] = 500 * (f[0] - f[1]);
    lab[2] = 200 * (f[1] - f[2]);
}

void lab_to_xyz_d50(const double lab[3], double xyz[3]) {
    const double k = 24389.0 / 27.0, e = 216.0 / 24389.0;
    double f[3];
    f[1] = (lab[0] + 16) / 116;
    f[0] = lab[1] / 500 + f[1];
    f[2] = f[1] - lab[2] / 200;
    const double f0c = f[0] * f[0] * f[0], f2c = f[2] * f[2] * f[2];
    xyz[0] = (f0c > e ? f0c : (116 * f[0] - 16) / k) * kD50[0];
    xyz[1] = (lab[0] > k * e ? std::pow((lab[0] + 16) / 116, 3) : lab[0] / k) * kD50[1];
    xyz[2] = (f2c > e ? f2c : (116 * f[2] - 16) / k) * kD50[2];
}

void xyz_d65_to_oklab(const double xyz[3], double lab[3]) {
    double lms[3];
    mul(kXyzToLms, xyz, lms);
    for (double& v : lms) v = std::cbrt(v);
    mul(kLmsToOklab, lms, lab);
}

void oklab_to_xyz_d65(const double lab[3], double xyz[3]) {
    double lms[3];
    mul(kOklabToLms, lab, lms);
    for (double& v : lms) v = v * v * v;
    mul(kLmsToXyz, lms, xyz);
}

void lab_to_lch(const double lab[3], double lch[3]) {
    const double c = std::hypot(lab[1], lab[2]);
    double h = std::atan2(lab[2], lab[1]) * 180 / kPi;
    lch[0] = lab[0]; lch[1] = c; lch[2] = c < 1e-9 ? 0 : norm_hue(h);
}

void lch_to_lab(const double lch[3], double lab[3]) {
    lab[0] = lch[0];
    lab[1] = lch[1] * std::cos(lch[2] * kPi / 180);
    lab[2] = lch[1] * std::sin(lch[2] * kPi / 180);
}

// Every non-polar space to and from XYZ D65.
void to_xyz_d65(ColorSpace from, const double in[3], double xyz[3]) {
    double lin[3];
    switch (from) {
        case ColorSpace::Srgb:
            for (int i = 0; i < 3; ++i) lin[i] = srgb_decode(in[i]);
            mul(kLinSrgbToXyz, lin, xyz);
            return;
        case ColorSpace::SrgbLinear:
            mul(kLinSrgbToXyz, in, xyz);
            return;
        case ColorSpace::DisplayP3:
            for (int i = 0; i < 3; ++i) lin[i] = srgb_decode(in[i]);
            mul(kLinP3ToXyz, lin, xyz);
            return;
        case ColorSpace::A98Rgb:
            for (int i = 0; i < 3; ++i) lin[i] = a98_decode(in[i]);
            mul(kLinA98ToXyz, lin, xyz);
            return;
        case ColorSpace::ProphotoRgb: {
            for (int i = 0; i < 3; ++i) lin[i] = prophoto_decode(in[i]);
            double d50[3];
            mul(kLinProphotoToXyzD50, lin, d50);
            mul(kD50ToD65, d50, xyz);
            return;
        }
        case ColorSpace::Rec2020:
            for (int i = 0; i < 3; ++i) lin[i] = rec2020_decode(in[i]);
            mul(kLinRec2020ToXyz, lin, xyz);
            return;
        case ColorSpace::Xyz:
        case ColorSpace::XyzD65:
            std::memcpy(xyz, in, 3 * sizeof(double));
            return;
        case ColorSpace::XyzD50:
            mul(kD50ToD65, in, xyz);
            return;
        case ColorSpace::Lab: {
            double d50[3];
            lab_to_xyz_d50(in, d50);
            mul(kD50ToD65, d50, xyz);
            return;
        }
        case ColorSpace::Lch: {
            double lab[3], d50[3];
            lch_to_lab(in, lab);
            lab_to_xyz_d50(lab, d50);
            mul(kD50ToD65, d50, xyz);
            return;
        }
        case ColorSpace::Oklab:
            oklab_to_xyz_d65(in, xyz);
            return;
        case ColorSpace::Oklch: {
            double lab[3];
            lch_to_lab(in, lab);
            oklab_to_xyz_d65(lab, xyz);
            return;
        }
        case ColorSpace::Hsl:
            hsl_to_rgb(in[0], in[1] / 100, in[2] / 100, lin);
            for (double& v : lin) v = srgb_decode(v);
            mul(kLinSrgbToXyz, lin, xyz);
            return;
        case ColorSpace::Hwb:
            hwb_to_rgb(in[0], in[1] / 100, in[2] / 100, lin);
            for (double& v : lin) v = srgb_decode(v);
            mul(kLinSrgbToXyz, lin, xyz);
            return;
    }
}

void from_xyz_d65(ColorSpace to, const double xyz[3], double out[3]) {
    switch (to) {
        case ColorSpace::Srgb:
            mul(kXyzToLinSrgb, xyz, out);
            for (int i = 0; i < 3; ++i) out[i] = srgb_encode(out[i]);
            return;
        case ColorSpace::SrgbLinear:
            mul(kXyzToLinSrgb, xyz, out);
            return;
        case ColorSpace::DisplayP3:
            mul(kXyzToLinP3, xyz, out);
            for (int i = 0; i < 3; ++i) out[i] = srgb_encode(out[i]);
            return;
        case ColorSpace::A98Rgb:
            mul(kXyzToLinA98, xyz, out);
            for (int i = 0; i < 3; ++i) out[i] = a98_encode(out[i]);
            return;
        case ColorSpace::ProphotoRgb: {
            double d50[3];
            mul(kD65ToD50, xyz, d50);
            mul(kXyzD50ToLinProphoto, d50, out);
            for (int i = 0; i < 3; ++i) out[i] = prophoto_encode(out[i]);
            return;
        }
        case ColorSpace::Rec2020:
            mul(kXyzToLinRec2020, xyz, out);
            for (int i = 0; i < 3; ++i) out[i] = rec2020_encode(out[i]);
            return;
        case ColorSpace::Xyz:
        case ColorSpace::XyzD65:
            std::memcpy(out, xyz, 3 * sizeof(double));
            return;
        case ColorSpace::XyzD50:
            mul(kD65ToD50, xyz, out);
            return;
        case ColorSpace::Lab: {
            double d50[3];
            mul(kD65ToD50, xyz, d50);
            xyz_d50_to_lab(d50, out);
            return;
        }
        case ColorSpace::Lch: {
            double d50[3], lab[3];
            mul(kD65ToD50, xyz, d50);
            xyz_d50_to_lab(d50, lab);
            lab_to_lch(lab, out);
            return;
        }
        case ColorSpace::Oklab:
            xyz_d65_to_oklab(xyz, out);
            return;
        case ColorSpace::Oklch: {
            double lab[3];
            xyz_d65_to_oklab(xyz, lab);
            lab_to_lch(lab, out);
            return;
        }
        case ColorSpace::Hsl: {
            double rgb[3];
            mul(kXyzToLinSrgb, xyz, rgb);
            for (double& v : rgb) v = std::min(1.0, std::max(0.0, srgb_encode(v)));
            rgb_to_hsl(rgb, out);
            return;
        }
        case ColorSpace::Hwb: {
            double rgb[3];
            mul(kXyzToLinSrgb, xyz, rgb);
            for (double& v : rgb) v = std::min(1.0, std::max(0.0, srgb_encode(v)));
            rgb_to_hwb(rgb, out);
            return;
        }
    }
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

} // namespace

bool color_space_from_name(std::string_view name, ColorSpace* out) {
    struct Entry { const char* name; ColorSpace space; };
    static const Entry kEntries[] = {
        {"srgb", ColorSpace::Srgb},           {"srgb-linear", ColorSpace::SrgbLinear},
        {"display-p3", ColorSpace::DisplayP3}, {"a98-rgb", ColorSpace::A98Rgb},
        {"prophoto-rgb", ColorSpace::ProphotoRgb}, {"rec2020", ColorSpace::Rec2020},
        {"xyz", ColorSpace::Xyz},             {"xyz-d50", ColorSpace::XyzD50},
        {"xyz-d65", ColorSpace::XyzD65},      {"lab", ColorSpace::Lab},
        {"lch", ColorSpace::Lch},             {"oklab", ColorSpace::Oklab},
        {"oklch", ColorSpace::Oklch},         {"hsl", ColorSpace::Hsl},
        {"hwb", ColorSpace::Hwb},
    };
    for (const Entry& e : kEntries) {
        if (iequals(name, e.name)) { *out = e.space; return true; }
    }
    return false;
}

int color_space_hue_index(ColorSpace space) {
    switch (space) {
        case ColorSpace::Lch:
        case ColorSpace::Oklch: return 2;
        case ColorSpace::Hsl:
        case ColorSpace::Hwb: return 0;
        default: return -1;
    }
}

void color_space_to_srgb(ColorSpace from, const double in[3], double out[3]) {
    if (from == ColorSpace::Srgb) { std::memcpy(out, in, 3 * sizeof(double)); return; }
    if (from == ColorSpace::Hsl) { hsl_to_rgb(in[0], in[1] / 100, in[2] / 100, out); return; }
    if (from == ColorSpace::Hwb) { hwb_to_rgb(in[0], in[1] / 100, in[2] / 100, out); return; }
    double xyz[3];
    to_xyz_d65(from, in, xyz);
    from_xyz_d65(ColorSpace::Srgb, xyz, out);
}

void color_space_from_srgb(ColorSpace to, const double in[3], double out[3]) {
    if (to == ColorSpace::Srgb) { std::memcpy(out, in, 3 * sizeof(double)); return; }
    if (to == ColorSpace::Hsl) { rgb_to_hsl(in, out); return; }
    if (to == ColorSpace::Hwb) { rgb_to_hwb(in, out); return; }
    double xyz[3];
    to_xyz_d65(ColorSpace::Srgb, in, xyz);
    from_xyz_d65(to, xyz, out);
}

bool color_space_hue_powerless(ColorSpace space, const double c[3]) {
    switch (space) {
        case ColorSpace::Lch: return c[1] < 1e-6;
        case ColorSpace::Oklch: return c[1] < 1e-9;
        case ColorSpace::Hsl: return c[1] < 1e-9 || c[2] <= 0 || c[2] >= 100;
        case ColorSpace::Hwb: return c[1] + c[2] >= 100 - 1e-9;
        default: return false;
    }
}

} // namespace weva
