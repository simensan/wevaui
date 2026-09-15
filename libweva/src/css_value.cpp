#include "weva/css_calc.h"
#include "weva/css_value.h"
#include "weva/color_space.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

std::string ascii_lower(std::string_view s) {
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

bool is_hex(char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

int hex_val(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    return 10 + (c - 'A');
}

} // namespace

bool css_length_unit_from_string(std::string_view u, CssLengthUnit* out) {
    struct E { const char* n; CssLengthUnit v; };
    static const E table[] = {
        {"px", CssLengthUnit::Px},   {"em", CssLengthUnit::Em},
        {"rem", CssLengthUnit::Rem}, {"vh", CssLengthUnit::Vh},
        {"vw", CssLengthUnit::Vw},   {"vmin", CssLengthUnit::Vmin},
        {"vmax", CssLengthUnit::Vmax}, {"pt", CssLengthUnit::Pt},
        {"pc", CssLengthUnit::Pc},   {"in", CssLengthUnit::In},
        {"cm", CssLengthUnit::Cm},   {"mm", CssLengthUnit::Mm},
        {"ch", CssLengthUnit::Ch},   {"ex", CssLengthUnit::Ex},
        {"cap", CssLengthUnit::Cap}, {"ic", CssLengthUnit::Ic},
        {"lh", CssLengthUnit::Lh},   {"rlh", CssLengthUnit::Rlh},
        {"svw", CssLengthUnit::Svw}, {"lvw", CssLengthUnit::Lvw},
        {"dvw", CssLengthUnit::Dvw}, {"svh", CssLengthUnit::Svh},
        {"lvh", CssLengthUnit::Lvh}, {"dvh", CssLengthUnit::Dvh},
    };
    for (const E& e : table) {
        if (u == e.n) { *out = e.v; return true; }
    }
    // Note: "%" is deliberately absent. The tokenizer emits a Percentage token
    // for it, so CssLengthUnit::Percent is only ever reachable from code that
    // constructs one directly — matching the C#.
    *out = CssLengthUnit::Px;
    return false;
}

bool css_angle_unit_from_string(std::string_view u, CssAngleUnit* out) {
    if (u == "deg")  { *out = CssAngleUnit::Deg;  return true; }
    if (u == "rad")  { *out = CssAngleUnit::Rad;  return true; }
    if (u == "grad") { *out = CssAngleUnit::Grad; return true; }
    if (u == "turn") { *out = CssAngleUnit::Turn; return true; }
    *out = CssAngleUnit::Deg;
    return false;
}

const char* css_length_unit_suffix(CssLengthUnit u) {
    switch (u) {
        case CssLengthUnit::Px: return "px";   case CssLengthUnit::Em: return "em";
        case CssLengthUnit::Rem: return "rem"; case CssLengthUnit::Percent: return "%";
        case CssLengthUnit::Vh: return "vh";   case CssLengthUnit::Vw: return "vw";
        case CssLengthUnit::Vmin: return "vmin"; case CssLengthUnit::Vmax: return "vmax";
        case CssLengthUnit::Pt: return "pt";   case CssLengthUnit::Pc: return "pc";
        case CssLengthUnit::In: return "in";   case CssLengthUnit::Cm: return "cm";
        case CssLengthUnit::Mm: return "mm";   case CssLengthUnit::Ch: return "ch";
        case CssLengthUnit::Ex: return "ex";   case CssLengthUnit::Cap: return "cap";
        case CssLengthUnit::Ic: return "ic";   case CssLengthUnit::Lh: return "lh";
        case CssLengthUnit::Rlh: return "rlh"; case CssLengthUnit::Svw: return "svw";
        case CssLengthUnit::Lvw: return "lvw"; case CssLengthUnit::Dvw: return "dvw";
        case CssLengthUnit::Svh: return "svh"; case CssLengthUnit::Lvh: return "lvh";
        case CssLengthUnit::Dvh: return "dvh";
    }
    return "";
}

bool CssLength::to_pixels(const LengthContext& ctx, double* out) const {
    const double dpi = ctx.dpi_pixels_per_inch <= 0 ? 96.0 : ctx.dpi_pixels_per_inch;
    switch (unit) {
        case CssLengthUnit::Px:  *out = value; return true;
        case CssLengthUnit::Em:  *out = value * ctx.base_font_size_px; return true;
        case CssLengthUnit::Rem: *out = value * ctx.root_font_size_px; return true;
        case CssLengthUnit::Percent:
            if (!ctx.has_basis) return false;   // C# throws InvalidOperationException
            *out = value * 0.01 * ctx.basis_pixels;
            return true;
        case CssLengthUnit::Vh: *out = value * 0.01 * ctx.viewport_height_px; return true;
        case CssLengthUnit::Vw: *out = value * 0.01 * ctx.viewport_width_px; return true;
        case CssLengthUnit::Vmin:
            *out = value * 0.01 * std::fmin(ctx.viewport_width_px, ctx.viewport_height_px);
            return true;
        case CssLengthUnit::Vmax:
            *out = value * 0.01 * std::fmax(ctx.viewport_width_px, ctx.viewport_height_px);
            return true;
        case CssLengthUnit::Pt: *out = value * (dpi / 72.0); return true;
        case CssLengthUnit::Pc: *out = value * 12.0 * (dpi / 72.0); return true;
        case CssLengthUnit::In: *out = value * dpi; return true;
        case CssLengthUnit::Cm: *out = value * (dpi / 2.54); return true;
        case CssLengthUnit::Mm: *out = value * (dpi / 25.4); return true;
        // ch/ex/cap/ic are font-metric approximations in the C#, not real
        // metrics. Reproduced exactly so the oracle matches; revisit only when
        // the text stack can supply true values, and then in both engines.
        case CssLengthUnit::Ch:  *out = value * 0.5 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Ex:  *out = value * 0.5 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Cap: *out = value * 0.7 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Ic:  *out = value * ctx.base_font_size_px; return true;
        case CssLengthUnit::Lh:
            *out = value * (ctx.line_height_px > 0 ? ctx.line_height_px
                                                   : ctx.base_font_size_px * 1.2);
            return true;
        case CssLengthUnit::Rlh:
            *out = value * (ctx.root_line_height_px > 0 ? ctx.root_line_height_px
                                                        : ctx.root_font_size_px * 1.2);
            return true;
        case CssLengthUnit::Svw:
        case CssLengthUnit::Lvw:
        case CssLengthUnit::Dvw: *out = value * 0.01 * ctx.viewport_width_px; return true;
        case CssLengthUnit::Svh:
        case CssLengthUnit::Lvh:
        case CssLengthUnit::Dvh: *out = value * 0.01 * ctx.viewport_height_px; return true;
    }
    *out = value;
    return true;
}

double CssAngle::to_degrees() const {
    switch (unit) {
        case CssAngleUnit::Deg:  return value;
        case CssAngleUnit::Rad:  return value * 180.0 / 3.14159265358979323846;
        case CssAngleUnit::Grad: return value * 0.9;
        case CssAngleUnit::Turn: return value * 360.0;
    }
    return value;
}

bool css_color_from_hex(std::string_view body, CssColor* out) {
    if (body.empty()) return false;
    for (char c : body) {
        if (!is_hex(c)) return false;
    }
    int r, g, b;
    float a = 1.0f;
    switch (body.size()) {
        case 3:
            r = hex_val(body[0]) * 17; g = hex_val(body[1]) * 17; b = hex_val(body[2]) * 17;
            break;
        case 4:
            r = hex_val(body[0]) * 17; g = hex_val(body[1]) * 17; b = hex_val(body[2]) * 17;
            a = static_cast<float>(hex_val(body[3]) * 17) / 255.0f;
            break;
        case 6:
            r = (hex_val(body[0]) << 4) | hex_val(body[1]);
            g = (hex_val(body[2]) << 4) | hex_val(body[3]);
            b = (hex_val(body[4]) << 4) | hex_val(body[5]);
            break;
        case 8:
            r = (hex_val(body[0]) << 4) | hex_val(body[1]);
            g = (hex_val(body[2]) << 4) | hex_val(body[3]);
            b = (hex_val(body[4]) << 4) | hex_val(body[5]);
            a = static_cast<float>((hex_val(body[6]) << 4) | hex_val(body[7])) / 255.0f;
            break;
        default:
            return false;
    }
    out->r = static_cast<uint8_t>(r);
    out->g = static_cast<uint8_t>(g);
    out->b = static_cast<uint8_t>(b);
    out->a = a;
    out->raw = "#" + ascii_lower(body);
    return true;
}

// ---------------------------------------------------------------- parsing

namespace {

struct Reader {
    const std::vector<CssToken>* toks;
    std::size_t i = 0;
    CssParseError* error;
    bool failed = false;

    const CssToken& peek() const { return (*toks)[i]; }
    void advance() { if (i + 1 < toks->size()) ++i; }
    bool at_end() const { return peek().kind == CssTokenKind::Eof; }
    void skip_ws() {
        while (peek().kind == CssTokenKind::Whitespace && i + 1 < toks->size()) ++i;
    }
    bool ws_ahead() const { return peek().kind == CssTokenKind::Whitespace; }
    std::nullptr_t fail(std::string_view msg, const CssToken& at) {
        failed = true;
        if (error) *error = CssParseError{std::string(msg), at.line, at.column};
        return nullptr;
    }
};

CssValuePtr parse_single(Reader& r);
CalcNodePtr parse_calc_expression(Reader& r, int depth);

// One color-mix() component: `<color> [<percentage>]?`, as a colour value,
// an identifier (`transparent`) or a space list of the two. False when the
// colour cannot be evaluated here (currentcolor, an unresolved var()).
bool colour_mix_component(const CssValue& v, const CssColor** color, double* percent,
                          bool* has_percent, std::unique_ptr<CssColor>* scratch) {
    *has_percent = false;
    const CssValue* c = &v;
    if (v.kind() == CssValueKind::List) {
        const auto& l = static_cast<const CssValueList&>(v);
        c = nullptr;
        for (const CssValuePtr& item : l.items) {
            if (!item) continue;
            if (item->kind() == CssValueKind::Percentage) {
                *percent = static_cast<const CssPercentage&>(*item).value;
                *has_percent = true;
            } else if (!c) {
                c = item.get();
            }
        }
        if (!c) return false;
    }
    if (c->kind() == CssValueKind::Color) {
        *color = static_cast<const CssColor*>(c);
        return true;
    }
    if (c->kind() == CssValueKind::Identifier || c->kind() == CssValueKind::Keyword) {
        const std::string& name = c->kind() == CssValueKind::Identifier
                                      ? static_cast<const CssIdentifier&>(*c).name
                                      : static_cast<const CssKeyword&>(*c).name;
        if (name == "transparent") {
            *scratch = std::make_unique<CssColor>();
            (*scratch)->a = 0;
            *color = scratch->get();
            return true;
        }
    }
    return false;
}

// CSS Color 5 §3: color-mix(in <space> [<hue-method> hue]?, <c1> [p1], <c2> [p2]).
// Both colours go to the named space, mix premultiplied (the hue plain,
// along the arc the method names, a powerless hue taking the other's), and
// come back to sRGB.
CssValuePtr eval_colour_mix(const CssFunctionCall& call) {
    if (call.arguments.size() != 3 || !call.arguments[0] || !call.arguments[1] || !call.arguments[2]) return nullptr;
    ColorSpace space = ColorSpace::Srgb;
    enum class Hue { Shorter, Longer, Increasing, Decreasing } hue = Hue::Shorter;
    {
        std::vector<std::string> words;
        const auto word = [&](const CssValue& v) {
            if (v.kind() == CssValueKind::Identifier) words.push_back(ascii_lower(static_cast<const CssIdentifier&>(v).name));
            else if (v.kind() == CssValueKind::Keyword) words.push_back(ascii_lower(static_cast<const CssKeyword&>(v).name));
        };
        const CssValue& first = *call.arguments[0];
        if (first.kind() == CssValueKind::List) {
            for (const CssValuePtr& i : static_cast<const CssValueList&>(first).items) if (i) word(*i);
        } else {
            word(first);
        }
        if (words.size() < 2 || words[0] != "in" || !color_space_from_name(words[1], &space)) return nullptr;
        for (size_t k = 2; k < words.size(); ++k) {
            if (words[k] == "longer") hue = Hue::Longer;
            else if (words[k] == "increasing") hue = Hue::Increasing;
            else if (words[k] == "decreasing") hue = Hue::Decreasing;
            else if (words[k] == "shorter") hue = Hue::Shorter;
        }
    }
    const CssColor* c1 = nullptr;
    const CssColor* c2 = nullptr;
    std::unique_ptr<CssColor> s1, s2;
    double p1 = 0, p2 = 0;
    bool h1 = false, h2 = false;
    if (!colour_mix_component(*call.arguments[1], &c1, &p1, &h1, &s1)) return nullptr;
    if (!colour_mix_component(*call.arguments[2], &c2, &p2, &h2, &s2)) return nullptr;
    // §3.1 percentage normalisation.
    if (!h1 && !h2) { p1 = p2 = 50; }
    else if (h1 && !h2) { p2 = 100 - p1; }
    else if (!h1 && h2) { p1 = 100 - p2; }
    if (p1 < 0 || p2 < 0) return nullptr;
    double alpha_mult = 1;
    const double sum = p1 + p2;
    if (sum <= 0) return nullptr;
    if (sum != 100) {
        if (sum < 100) alpha_mult = sum / 100.0;
        p1 = p1 * 100.0 / sum;
        p2 = p2 * 100.0 / sum;
    }
    const double w1 = p1 / 100.0, w2 = p2 / 100.0;

    double v1[3], v2[3];
    {
        const double rgb1[3] = {c1->r / 255.0, c1->g / 255.0, c1->b / 255.0};
        const double rgb2[3] = {c2->r / 255.0, c2->g / 255.0, c2->b / 255.0};
        color_space_from_srgb(space, rgb1, v1);
        color_space_from_srgb(space, rgb2, v2);
    }
    const int hi = color_space_hue_index(space);
    if (hi >= 0) {
        // §4.4: a grey has no hue of its own and takes the other colour's;
        // a transparent colour is a grey here too.
        const bool p1less = color_space_hue_powerless(space, v1) || c1->a <= 0;
        const bool p2less = color_space_hue_powerless(space, v2) || c2->a <= 0;
        if (p1less && !p2less) v1[hi] = v2[hi];
        else if (p2less && !p1less) v2[hi] = v1[hi];
        double a1 = v1[hi], a2 = v2[hi];
        const double d = a2 - a1;
        switch (hue) {
            case Hue::Shorter:
                if (d > 180) a1 += 360; else if (d < -180) a2 += 360;
                break;
            case Hue::Longer:
                if (0 < d && d < 180) a1 += 360; else if (-180 < d && d <= 0) a2 += 360;
                break;
            case Hue::Increasing:
                if (a2 < a1) a2 += 360;
                break;
            case Hue::Decreasing:
                if (a1 < a2) a1 += 360;
                break;
        }
        v1[hi] = a1;
        v2[hi] = a2;
    }
    const double a1 = c1->a, a2 = c2->a;
    const double a = a1 * w1 + a2 * w2;
    double mixed[3] = {0, 0, 0};
    for (int i = 0; i < 3; ++i) {
        if (i == hi) {
            double h = v1[i] * w1 + v2[i] * w2;
            h = std::fmod(h, 360.0);
            mixed[i] = h < 0 ? h + 360.0 : h;
        } else if (a > 0) {
            mixed[i] = (v1[i] * a1 * w1 + v2[i] * a2 * w2) / a;
        }
    }
    double rgb[3];
    color_space_to_srgb(space, mixed, rgb);
    auto out = std::make_unique<CssColor>();
    if (a > 0) {
        out->r = static_cast<uint8_t>(std::lround(std::min(255.0, std::max(0.0, rgb[0] * 255.0))));
        out->g = static_cast<uint8_t>(std::lround(std::min(255.0, std::max(0.0, rgb[1] * 255.0))));
        out->b = static_cast<uint8_t>(std::lround(std::min(255.0, std::max(0.0, rgb[2] * 255.0))));
    }
    out->a = static_cast<float>(std::min(1.0, a * alpha_mult));
    out->raw = call.raw;
    return out;
}

// A colour function's channel: a number, a percentage (reported so each
// function applies its own reference range), an angle in degrees, or `none`
// (CSS Color 4 §4.4: a missing channel, which reads as 0 here).
struct ColourArg {
    double v = 0;
    bool pct = false;
    bool none = false;
    bool angle = false;
};

bool colour_arg(const CssValue& v, ColourArg* out) {
    *out = ColourArg{};
    switch (v.kind()) {
        case CssValueKind::Number:
            out->v = static_cast<const CssNumber&>(v).value;
            return true;
        case CssValueKind::Percentage:
            out->v = static_cast<const CssPercentage&>(v).value;
            out->pct = true;
            return true;
        case CssValueKind::Angle:
            out->v = static_cast<const CssAngle&>(v).to_degrees();
            out->angle = true;
            return true;
        case CssValueKind::Identifier:
        case CssValueKind::Keyword: {
            const std::string& name = v.kind() == CssValueKind::Identifier
                                          ? static_cast<const CssIdentifier&>(v).name
                                          : static_cast<const CssKeyword&>(v).name;
            if (ascii_lower(name) != "none") return false;
            out->none = true;
            return true;
        }
        default:
            return false;
    }
}

// Flattens a colour function's arguments into three channels and an alpha:
// the legacy comma form `rgb(1, 2, 3, 0.5)` and the modern space form
// `rgb(1 2 3 / 50%)`, whose `/` splits the alpha off. color() names its
// space first; that comes back in `leading`.
bool colour_channels(const CssFunctionCall& call, bool leading_ident, std::string* leading,
                     ColourArg ch[3], ColourArg* alpha, bool* has_alpha) {
    const bool legacy = call.arguments.size() > 1;
    if (legacy && (leading_ident || call.arguments.size() < 3 || call.arguments.size() > 4)) return false;
    std::vector<const CssValue*> flat;
    for (const CssValuePtr& a : call.arguments) {
        if (!a) return false;
        if (a->kind() == CssValueKind::List) {
            if (legacy) return false;  // comma/space syntax cannot be mixed
            for (const CssValuePtr& i : static_cast<const CssValueList&>(*a).items) {
                if (!i) return false;
                flat.push_back(i.get());
            }
        } else {
            flat.push_back(a.get());
        }
    }
    const auto ident = [](const CssValue& v, std::string* name) {
        if (v.kind() == CssValueKind::Identifier) { *name = static_cast<const CssIdentifier&>(v).name; return true; }
        if (v.kind() == CssValueKind::Keyword) { *name = static_cast<const CssKeyword&>(v).name; return true; }
        return false;
    };
    size_t i = 0;
    if (leading_ident) {
        if (flat.empty() || !ident(*flat[0], leading)) return false;
        i = 1;
    }
    *has_alpha = false;
    for (int channel = 0; channel < 3; ++channel) {
        if (i == flat.size() || !colour_arg(*flat[i++], &ch[channel])) return false;
    }
    if (i == flat.size()) return true;
    if (!legacy) {
        std::string name;
        if (!ident(*flat[i++], &name) || name != "/") return false;
    }
    if (i == flat.size() || !colour_arg(*flat[i++], alpha)) return false;
    *has_alpha = true;
    return i == flat.size();
}

// rgb()/rgba()/hsl()/hsla()/hwb()/lab()/lch()/oklab()/oklch()/color() collapse
// to a CssColor. Anything else — and any of these whose arguments don't
// evaluate (a var() or calc() inside) — stays a CssFunctionCall for a later
// pass to resolve.
CssValuePtr eval_colour_function(const CssFunctionCall& call) {
    const std::string& n = call.name;
    if (n == "color-mix") return eval_colour_mix(call);
    const bool is_rgb = (n == "rgb" || n == "rgba");
    const bool is_hsl = (n == "hsl" || n == "hsla");
    const bool is_hwb = (n == "hwb");
    const bool is_lab = (n == "lab"), is_lch = (n == "lch");
    const bool is_oklab = (n == "oklab"), is_oklch = (n == "oklch");
    const bool is_color = (n == "color");
    if (!is_rgb && !is_hsl && !is_hwb && !is_lab && !is_lch && !is_oklab && !is_oklch && !is_color) return nullptr;

    ColourArg ch[3], al;
    bool has_alpha = false;
    std::string space_name;
    if (!colour_channels(call, is_color, &space_name, ch, &al, &has_alpha)) return nullptr;
    const bool legacy = call.arguments.size() > 1;
    if (legacy && !is_rgb && !is_hsl) return nullptr;
    if (has_alpha && (al.angle || (legacy && al.none))) return nullptr;
    for (int i = 0; i < 3; ++i) {
        const bool hue = ((is_hsl || is_hwb) && i == 0) || ((is_lch || is_oklch) && i == 2);
        if ((hue && ch[i].pct) || (!hue && ch[i].angle) || (legacy && ch[i].none)) return nullptr;
    }
    if (legacy && is_rgb && (ch[0].pct != ch[1].pct || ch[0].pct != ch[2].pct)) return nullptr;
    if (legacy && is_hsl && (!ch[1].pct || !ch[2].pct)) return nullptr;
    double alpha = 1.0;
    if (has_alpha) {
        // CSS Color 4: an alpha percentage is 0-100, a number is 0-1.
        alpha = al.none ? 0.0 : (al.pct ? al.v / 100.0 : al.v);
    }
    // A channel's value with its percentage reference (`100%` = ref).
    const auto val = [](const ColourArg& a, double ref) {
        return a.none ? 0.0 : (a.pct ? a.v * ref / 100.0 : a.v);
    };

    auto out = std::make_unique<CssColor>();
    if (is_rgb) {
        // Modern RGB permits percentages and numbers in the same call.
        // Scale each channel independently before converting to byte values.
        css_color_from_rgb(val(ch[0], 255), val(ch[1], 255), val(ch[2], 255), alpha, false, out.get());
    } else if (is_hsl) {
        // The modern form takes plain numbers for saturation and lightness,
        // which mean the same as percentages.
        css_color_from_hsl(val(ch[0], 100), ch[1].none ? 0 : ch[1].v, ch[2].none ? 0 : ch[2].v, alpha, out.get());
    } else if (is_hwb) {
        css_color_from_hwb(val(ch[0], 100), ch[1].none ? 0 : ch[1].v, ch[2].none ? 0 : ch[2].v, alpha, out.get());
    } else {
        ColorSpace space = ColorSpace::Srgb;
        double c[3];
        if (is_lab) {
            space = ColorSpace::Lab;
            c[0] = std::min(100.0, std::max(0.0, val(ch[0], 100)));
            c[1] = val(ch[1], 125);
            c[2] = val(ch[2], 125);
        } else if (is_lch) {
            space = ColorSpace::Lch;
            c[0] = std::min(100.0, std::max(0.0, val(ch[0], 100)));
            c[1] = std::max(0.0, val(ch[1], 150));
            c[2] = val(ch[2], 100);
        } else if (is_oklab) {
            space = ColorSpace::Oklab;
            c[0] = std::min(1.0, std::max(0.0, val(ch[0], 1)));
            c[1] = val(ch[1], 0.4);
            c[2] = val(ch[2], 0.4);
        } else if (is_oklch) {
            space = ColorSpace::Oklch;
            c[0] = std::min(1.0, std::max(0.0, val(ch[0], 1)));
            c[1] = std::max(0.0, val(ch[1], 0.4));
            c[2] = val(ch[2], 1);
        } else {
            // color(): the predefined RGB spaces and XYZ, channels 0..1.
            if (!color_space_from_name(space_name, &space)) return nullptr;
            if (color_space_hue_index(space) >= 0 || space == ColorSpace::Lab || space == ColorSpace::Oklab) return nullptr;
            for (int i = 0; i < 3; ++i) c[i] = val(ch[i], 1);
        }
        // Out-of-gamut colours clip to sRGB. Chrome maps them by reducing
        // chroma instead (CSS Color 4 §13.2), which differs by a few levels
        // at the edge; the clip keeps hue and is exact inside the gamut.
        double rgb[3];
        color_space_to_srgb(space, c, rgb);
        css_color_from_rgb(std::min(1.0, std::max(0.0, rgb[0])) * 255.0,
                           std::min(1.0, std::max(0.0, rgb[1])) * 255.0,
                           std::min(1.0, std::max(0.0, rgb[2])) * 255.0, alpha, false, out.get());
    }
    out->raw = call.raw;
    return out;
}

// --- calc() -------------------------------------------------------------------

// Real sheets nest a couple of levels; the cap stops hostile input from
// recursing the parser off the stack, matching the C#'s MaxCalcDepth.
constexpr int kMaxCalcDepth = 64;

CalcNodePtr calc_from_value(const CssValue& v) {
    switch (v.kind()) {
        case CssValueKind::Length: {
            auto n = std::make_unique<CalcLengthNode>();
            n->value = static_cast<const CssLength&>(v).value;
            n->unit = static_cast<const CssLength&>(v).unit;
            return n;
        }
        case CssValueKind::Number: {
            auto n = std::make_unique<CalcNumberNode>();
            n->value = static_cast<const CssNumber&>(v).value;
            return n;
        }
        case CssValueKind::Percentage: {
            auto n = std::make_unique<CalcPercentageNode>();
            n->value = static_cast<const CssPercentage&>(v).value;
            return n;
        }
        case CssValueKind::Angle: {
            auto n = std::make_unique<CalcAngleNode>();
            n->degrees = static_cast<const CssAngle&>(v).to_degrees();
            return n;
        }
        default:
            return nullptr;
    }
}

CalcNodePtr parse_calc_factor(Reader& r, int depth) {
    r.skip_ws();
    const CssToken t = r.peek();

    if (t.kind == CssTokenKind::LParen) {
        r.advance();
        CalcNodePtr inner = parse_calc_expression(r, depth + 1);
        if (!inner) return nullptr;
        r.skip_ws();
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        return inner;
    }

    if (t.kind == CssTokenKind::Function) {
        std::string fn = ascii_lower(t.text);
        if (fn == "calc") {
            r.advance();
            CalcNodePtr inner = parse_calc_expression(r, depth + 1);
            if (!inner) return nullptr;
            r.skip_ws();
            if (r.peek().kind == CssTokenKind::RParen) r.advance();
            return inner;
        }
        CalcMathFn mf;
        if (fn == "min") mf = CalcMathFn::Min;
        else if (fn == "max") mf = CalcMathFn::Max;
        else if (fn == "clamp") mf = CalcMathFn::Clamp;
        else {
            // Deferred math functions are REJECTED rather than treated as an
            // opaque value: silently mis-evaluating round() or sin() would be
            // far worse than refusing the declaration.
            r.fail("calc() function '" + fn + "' is not supported yet", t);
            return nullptr;
        }
        r.advance();
        auto m = std::make_unique<CalcMathNode>();
        m->fn = mf;
        r.skip_ws();
        while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
            CalcNodePtr a = parse_calc_expression(r, depth + 1);
            if (!a) return nullptr;
            m->args.push_back(std::move(a));
            r.skip_ws();
            if (r.peek().kind == CssTokenKind::Comma) { r.advance(); r.skip_ws(); }
        }
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        if (m->args.empty()) {
            r.fail("calc() math function needs at least one argument", t);
            return nullptr;
        }
        return m;
    }

    CssValuePtr v = parse_single(r);
    if (!v) return nullptr;
    CalcNodePtr n = calc_from_value(*v);
    if (!n) {
        r.fail("calc() operand must be a number, length, percentage or angle", t);
        return nullptr;
    }
    return n;
}

CalcNodePtr parse_calc_term(Reader& r, int depth) {
    CalcNodePtr left = parse_calc_factor(r, depth);
    if (!left) return nullptr;
    for (;;) {
        std::size_t saved = r.i;
        r.skip_ws();
        const CssToken t = r.peek();
        if (t.kind != CssTokenKind::Delim || (t.text != "*" && t.text != "/")) {
            r.i = saved;
            break;
        }
        r.advance();
        CalcNodePtr right = parse_calc_factor(r, depth);
        if (!right) return nullptr;
        auto b = std::make_unique<CalcBinaryNode>();
        b->op = (t.text == "*") ? CalcOp::Mul : CalcOp::Div;
        b->left = std::move(left);
        b->right = std::move(right);
        left = std::move(b);
    }
    return left;
}

CalcNodePtr parse_calc_expression(Reader& r, int depth) {
    if (depth > kMaxCalcDepth) {
        r.fail("calc() nesting too deep", r.peek());
        return nullptr;
    }
    CalcNodePtr left = parse_calc_term(r, depth);
    if (!left) return nullptr;

    for (;;) {
        bool ws_before = r.ws_ahead();
        r.skip_ws();
        if (r.at_end()) break;
        const CssToken t = r.peek();

        // CSS Values 4 §10.1: '+' and '-' MUST be surrounded by whitespace,
        // because the tokenizer folds a leading sign into the number. So
        // `calc(1px+2px)` arrives as Dimension("1px") then Dimension("+2px"),
        // and `calc(1px -2px)` as Dimension("1px"), ws, Dimension("-2px") —
        // both are errors, not additions.
        if (t.kind == CssTokenKind::Dimension || t.kind == CssTokenKind::Number ||
            t.kind == CssTokenKind::Percentage) {
            if (!t.text.empty() && (t.text[0] == '+' || t.text[0] == '-')) {
                r.fail(std::string("calc() requires whitespace around '") + t.text[0] + "'", t);
                return nullptr;
            }
            break;
        }
        if (t.kind != CssTokenKind::Delim) break;
        if (t.text != "+" && t.text != "-") break;
        if (!ws_before) {
            r.fail("calc() requires whitespace around '" + t.text + "'", t);
            return nullptr;
        }
        r.advance();
        if (!r.ws_ahead()) {
            r.fail("calc() requires whitespace around '" + t.text + "'", t);
            return nullptr;
        }
        CalcNodePtr right = parse_calc_term(r, depth);
        if (!right) return nullptr;
        auto b = std::make_unique<CalcBinaryNode>();
        b->op = (t.text == "+") ? CalcOp::Add : CalcOp::Sub;
        b->left = std::move(left);
        b->right = std::move(right);
        left = std::move(b);
    }
    return left;
}

CssValuePtr parse_function(Reader& r) {
    const CssToken fn = r.peek();
    const std::string lower = ascii_lower(fn.text);
    if (lower == "calc") {
        r.advance();
        CalcNodePtr node = parse_calc_expression(r, 0);
        if (!node) return nullptr;
        r.skip_ws();
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        auto c = std::make_unique<CssCalc>();
        c->expression = std::move(node);
        c->raw = fn.text + "(";
        return c;
    }
    if (lower == "min" || lower == "max" || lower == "clamp") {
        // CSS Values L4 §10.3: the comparison functions are math functions in
        // their own right, not only calc() operands. The calc parser already
        // reads them from the function token on, so it is entered without
        // consuming it. `width: min(560px, 92vw)` had fallen through to a
        // FunctionCall no length resolver reads, and the width to auto.
        CalcNodePtr node = parse_calc_expression(r, 0);
        if (!node) return nullptr;
        auto c = std::make_unique<CssCalc>();
        c->expression = std::move(node);
        c->raw = fn.text + "(";
        return c;
    }
    r.advance();
    auto call = std::make_unique<CssFunctionCall>();
    call->name = ascii_lower(fn.text);
    call->raw = fn.text + "(";

    // Arguments are comma-separated groups; a group with several values
    // becomes a space-separated list, matching ParseTopLevel's shape.
    std::vector<CssValuePtr> group;
    bool saw_comma = false, empty_argument = false;
    auto flush = [&] {
        if (group.empty()) return;
        if (group.size() == 1) {
            call->arguments.push_back(std::move(group[0]));
        } else {
            auto list = std::make_unique<CssValueList>();
            list->separator = CssListSeparator::Space;
            list->items = std::move(group);
            call->arguments.push_back(std::move(list));
        }
        group.clear();
    };

    r.skip_ws();
    while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
        if (r.peek().kind == CssTokenKind::Comma) {
            saw_comma = true;
            if (group.empty()) empty_argument = true;
            r.advance();
            r.skip_ws();
            flush();
            continue;
        }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) group.push_back(std::move(v));
        r.skip_ws();
    }
    if (saw_comma && group.empty()) empty_argument = true;
    flush();
    if (!r.at_end() && r.peek().kind == CssTokenKind::RParen) r.advance();

    if (!empty_argument) {
        if (CssValuePtr colour = eval_colour_function(*call)) return colour;
    }
    return call;
}

CssValuePtr parse_paren_group(Reader& r) {
    r.advance();   // '('
    auto list = std::make_unique<CssValueList>();
    list->separator = CssListSeparator::Space;
    r.skip_ws();
    while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
        if (r.peek().kind == CssTokenKind::Comma) { r.advance(); r.skip_ws(); continue; }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) list->items.push_back(std::move(v));
        r.skip_ws();
    }
    if (!r.at_end() && r.peek().kind == CssTokenKind::RParen) r.advance();
    if (list->items.size() == 1) return std::move(list->items[0]);
    return list;
}

CssValuePtr parse_single(Reader& r) {
    const CssToken t = r.peek();
    switch (t.kind) {
        case CssTokenKind::Ident: {
            r.advance();
            std::string lower = ascii_lower(t.text);
            // `currentcolor` stays a keyword — it resolves against the cascade,
            // not here.
            if (lower != "currentcolor") {
                auto c = std::make_unique<CssColor>();
                if (css_color_from_name(lower, c.get())) {
                    c->raw = t.text;
                    return c;
                }
            }
            auto k = std::make_unique<CssKeyword>();
            k->name = t.text;
            k->raw = t.text;
            return k;
        }
        case CssTokenKind::Number: {
            r.advance();
            auto n = std::make_unique<CssNumber>();
            n->value = t.number;
            n->raw = t.text;
            return n;
        }
        case CssTokenKind::Dimension: {
            r.advance();
            std::string unit_lower = ascii_lower(t.unit);
            CssAngleUnit au;
            if (css_angle_unit_from_string(unit_lower, &au)) {
                // CSS Values 4 §6.1: angles are their own type so length
                // consumers never have to special-case unit categories.
                auto a = std::make_unique<CssAngle>();
                a->value = t.number;
                a->unit = au;
                a->raw = t.text;
                return a;
            }
            CssLengthUnit lu;
            if (!css_length_unit_from_string(unit_lower, &lu)) {
                return r.fail("Unknown length unit '" + t.unit + "'", t);
            }
            auto l = std::make_unique<CssLength>();
            l->value = t.number;
            l->unit = lu;
            l->raw = t.text;
            return l;
        }
        case CssTokenKind::Percentage: {
            r.advance();
            auto p = std::make_unique<CssPercentage>();
            p->value = t.number;
            p->raw = t.text;
            return p;
        }
        case CssTokenKind::Hash: {
            r.advance();
            auto c = std::make_unique<CssColor>();
            if (!css_color_from_hex(t.text, c.get())) {
                return r.fail("Invalid hex color '#" + t.text + "'", t);
            }
            return c;
        }
        case CssTokenKind::String: {
            r.advance();
            auto s = std::make_unique<CssString>();
            s->text = t.text;
            s->raw = t.text;
            return s;
        }
        case CssTokenKind::Url: {
            r.advance();
            auto u = std::make_unique<CssUrl>();
            u->url = t.text;
            u->raw = t.text;
            return u;
        }
        case CssTokenKind::Function:
            return parse_function(r);
        case CssTokenKind::Delim: {
            r.advance();
            auto id = std::make_unique<CssIdentifier>();
            id->name = t.text;
            id->raw = t.text;
            return id;
        }
        case CssTokenKind::LParen:
            return parse_paren_group(r);
        default:
            return r.fail("Unexpected token '" + t.text + "'", t);
    }
}

} // namespace

CssValuePtr parse_css_value(std::string_view text, CssParseError* error) {
    // The token buffer is reused across calls rather than grown from empty each
    // time. Profiling a layout pass (Tools/weva_bench --profile) put its
    // reallocation at the top of every allocation site: a fresh vector doubles
    // 1-2-4-8 on every parse, and a document's declarations are short enough
    // that the growth IS the cost.
    //
    // Reused, not shared: parse_css_value recurses through parse_single for
    // function arguments, so a nested call must not reset the buffer its caller
    // is reading. The depth counter keeps the reuse to the outermost call, and
    // the engine is single-threaded by design (as are the property registry and
    // the match cache).
    // One buffer per nesting level, kept between calls. A nested call must not
    // reset the buffer its caller is still reading, and giving the nested level
    // a fresh vector would just move the allocation rather than remove it.
    static std::vector<std::vector<CssToken>> scratch;
    static size_t depth = 0;
    if (depth >= scratch.size()) scratch.resize(depth + 1);
    std::vector<CssToken>& tokens = scratch[depth];
    tokens.clear();

    struct DepthGuard {
        size_t& d;
        explicit DepthGuard(size_t& x) : d(x) { ++d; }
        ~DepthGuard() { --d; }
    } depth_guard(depth);

    CssTokenizer tokenizer(text, /*strict=*/true);
    if (!tokenizer.tokenize(&tokens, error)) return nullptr;

    Reader r{&tokens, 0, error};
    std::vector<std::vector<CssValuePtr>> segments;
    std::vector<CssValuePtr> current;

    r.skip_ws();
    // Most computed properties contain one value. Parse it using the normal
    // grammar, and avoid constructing two temporary list buffers when it is
    // the entire input. Whitespace, functions and errors follow the same Reader.
    if (!r.at_end() && r.peek().kind != CssTokenKind::Comma) {
        CssValuePtr first = parse_single(r);
        if (r.failed) return nullptr;
        r.skip_ws();
        if (first && r.at_end()) return first;
        if (first) current.push_back(std::move(first));
    }
    while (!r.at_end()) {
        if (r.peek().kind == CssTokenKind::Comma) {
            r.advance();
            r.skip_ws();
            segments.push_back(std::move(current));
            current.clear();
            continue;
        }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) current.push_back(std::move(v));
        r.skip_ws();
    }
    segments.push_back(std::move(current));

    auto join = [](std::vector<CssValuePtr> items) -> CssValuePtr {
        if (items.size() == 1) return std::move(items[0]);
        auto list = std::make_unique<CssValueList>();
        list->separator = CssListSeparator::Space;
        list->items = std::move(items);
        return list;
    };

    if (segments.size() == 1) {
        if (segments[0].empty()) {
            if (error) *error = CssParseError{"Empty value", 1, 1};
            return nullptr;
        }
        return join(std::move(segments[0]));
    }

    auto out = std::make_unique<CssValueList>();
    out->separator = CssListSeparator::Comma;
    for (auto& seg : segments) {
        if (seg.empty()) {
            if (error) *error = CssParseError{"Empty value between commas", 1, 1};
            return nullptr;
        }
        out->items.push_back(join(std::move(seg)));
    }
    return out;
}

} // namespace weva
