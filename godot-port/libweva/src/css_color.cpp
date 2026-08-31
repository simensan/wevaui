#include "weva/css_value.h"

#include <cmath>
#include <cstring>
#include <string>
#include <unordered_map>

namespace weva {

namespace {

struct NamedColor {
    const char* name;
    struct { int r, g, b; float a; } v;
};

#include "generated/named_colors.inc"

std::string ascii_lower(std::string_view s) {
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

double clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

// Mirrors CssColor.ChannelByte.
//
// The rounding mode matters and is NOT the same as the layout dump's. C#'s
// parameterless Math.Round is banker's rounding (MidpointRounding.ToEven), so
// 0.5 -> 0 and 2.5 -> 2. std::round is away-from-zero and would give 1 and 3,
// putting every midpoint channel one off. std::nearbyint under the default
// FE_TONEAREST is half-to-even and matches.
//
// Phase 0 hit the mirror image of this: BaselineGen's Round2 *is* away from
// zero, so weva_dump needed an explicit away-from-zero round. Two different
// C# rounding conventions in one codebase, and picking the wrong one is silent
// either way.
uint8_t channel_byte(double v, bool percent) {
    if (percent) v = v * 2.55;
    if (v < 0) v = 0;
    if (v > 255) v = 255;
    return static_cast<uint8_t>(std::nearbyint(v));
}

// CSS Color 4 §7 HSL -> sRGB, 0..1 in and out.
void hsl_to_rgb01(double h, double s, double l, double* r, double* g, double* b) {
    h = std::fmod(std::fmod(h, 360.0) + 360.0, 360.0);
    if (s < 0) s = 0;
    if (s > 1) s = 1;
    if (l < 0) l = 0;
    if (l > 1) l = 1;
    double c = (1 - std::fabs(2 * l - 1)) * s;
    double hp = h / 60.0;
    double x = c * (1 - std::fabs(std::fmod(hp, 2.0) - 1));
    double r1 = 0, g1 = 0, b1 = 0;
    if (hp < 1)      { r1 = c; g1 = x; b1 = 0; }
    else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
    else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
    else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
    else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
    else             { r1 = c; g1 = 0; b1 = x; }
    double m = l - c / 2.0;
    *r = r1 + m; *g = g1 + m; *b = b1 + m;
}

} // namespace

bool css_color_from_name(std::string_view name, CssColor* out) {
    static const std::unordered_map<std::string, const NamedColor*> index = [] {
        std::unordered_map<std::string, const NamedColor*> m;
        // The table carries the CSS system colours with their authored casing
        // ("AccentColor", "ButtonFace"). C# uses an OrdinalIgnoreCase
        // dictionary; here the key is lowercased on both insert and lookup, or
        // every system colour would silently fail to resolve.
        for (const NamedColor& c : kNamedColors) m.emplace(ascii_lower(c.name), &c);
        return m;
    }();
    auto it = index.find(ascii_lower(name));
    if (it == index.end()) return false;
    out->r = static_cast<uint8_t>(it->second->v.r);
    out->g = static_cast<uint8_t>(it->second->v.g);
    out->b = static_cast<uint8_t>(it->second->v.b);
    out->a = it->second->v.a;
    return true;
}

void css_color_from_rgb(double r, double g, double b, double alpha,
                        bool channels_are_percent, CssColor* out) {
    out->r = channel_byte(r, channels_are_percent);
    out->g = channel_byte(g, channels_are_percent);
    out->b = channel_byte(b, channels_are_percent);
    out->a = static_cast<float>(clamp01(alpha));
}

void css_color_from_hsl(double hue_deg, double sat_pct, double light_pct,
                        double alpha, CssColor* out) {
    double h = std::fmod(std::fmod(hue_deg, 360.0) + 360.0, 360.0);
    double s = clamp01(sat_pct / 100.0);
    double l = clamp01(light_pct / 100.0);
    double rd, gd, bd;
    hsl_to_rgb01(h, s, l, &rd, &gd, &bd);
    out->r = channel_byte(rd * 255.0, false);
    out->g = channel_byte(gd * 255.0, false);
    out->b = channel_byte(bd * 255.0, false);
    out->a = static_cast<float>(clamp01(alpha));
}

void css_color_from_hwb(double hue_deg, double white_pct, double black_pct,
                        double alpha, CssColor* out) {
    double h = std::fmod(std::fmod(hue_deg, 360.0) + 360.0, 360.0);
    double w = clamp01(white_pct / 100.0);
    double bk = clamp01(black_pct / 100.0);
    // CSS Color 4 §10: w + b >= 1 collapses to grey at w/(w+b).
    if (w + bk >= 1.0) {
        uint8_t gv = channel_byte((w / (w + bk)) * 255.0, false);
        out->r = out->g = out->b = gv;
        out->a = static_cast<float>(clamp01(alpha));
        return;
    }
    double rd, gd, bd;
    hsl_to_rgb01(h, 1.0, 0.5, &rd, &gd, &bd);   // pure-hue baseline
    rd = rd * (1 - w - bk) + w;
    gd = gd * (1 - w - bk) + w;
    bd = bd * (1 - w - bk) + w;
    out->r = channel_byte(rd * 255.0, false);
    out->g = channel_byte(gd * 255.0, false);
    out->b = channel_byte(bd * 255.0, false);
    out->a = static_cast<float>(clamp01(alpha));
}

} // namespace weva
