#include "weva/animation.h"

#include "weva/css_value.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace weva {

namespace {

std::string_view trim(std::string_view s) {
    const auto space = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    while (!s.empty() && space(s.front())) s.remove_prefix(1);
    while (!s.empty() && space(s.back())) s.remove_suffix(1);
    return s;
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

bool parse_number(std::string_view s, double* out) {
    s = trim(s);
    if (s.empty() || s.size() > 63) return false;
    char buf[64];
    std::memcpy(buf, s.data(), s.size());
    buf[s.size()] = '\0';
    char* end = nullptr;
    const double v = std::strtod(buf, &end);
    if (end == buf || *end != '\0') return false;
    *out = v;
    return true;
}

// Splits on a separator at paren depth 0.
std::vector<std::string_view> split(std::string_view s, char sep) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '(') ++depth;
        else if (s[i] == ')') { if (depth > 0) --depth; }
        else if (depth == 0 && (sep == ' ' ? (s[i] == ' ' || s[i] == '\t') : s[i] == sep)) {
            const std::string_view piece = trim(s.substr(start, i - start));
            if (!piece.empty()) out.push_back(piece);
            start = i + 1;
        }
    }
    const std::string_view last = trim(s.substr(start));
    if (!last.empty()) out.push_back(last);
    return out;
}

// A number formatted the way CSS reads it: no trailing zeros, no exponent.
std::string num(double v) {
    if (std::fabs(v - std::round(v)) < 1e-9) {
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%lld", static_cast<long long>(std::llround(v)));
        return buf;
    }
    char buf[48];
    std::snprintf(buf, sizeof(buf), "%.4f", v);
    std::string s = buf;
    while (!s.empty() && s.back() == '0') s.pop_back();
    if (!s.empty() && s.back() == '.') s.pop_back();
    return s;
}

const char* unit_name(CssLengthUnit u) {
    switch (u) {
        case CssLengthUnit::Px: return "px";
        case CssLengthUnit::Em: return "em";
        case CssLengthUnit::Rem: return "rem";
        case CssLengthUnit::Percent: return "%";
        case CssLengthUnit::Vh: return "vh";
        case CssLengthUnit::Vw: return "vw";
        case CssLengthUnit::Vmin: return "vmin";
        case CssLengthUnit::Vmax: return "vmax";
        case CssLengthUnit::Pt: return "pt";
        case CssLengthUnit::Pc: return "pc";
        case CssLengthUnit::In: return "in";
        case CssLengthUnit::Cm: return "cm";
        case CssLengthUnit::Mm: return "mm";
        case CssLengthUnit::Ch: return "ch";
        case CssLengthUnit::Ex: return "ex";
        default: return nullptr;   // a unit with no round trip is left discrete
    }
}

const char* angle_unit_name(CssAngleUnit u) {
    switch (u) {
        case CssAngleUnit::Deg: return "deg";
        case CssAngleUnit::Rad: return "rad";
        case CssAngleUnit::Grad: return "grad";
        case CssAngleUnit::Turn: return "turn";
    }
    return nullptr;
}

double lerp(double a, double b, double t) { return a + (b - a) * t; }

// One value, when both sides are the same kind and that kind has a meaningful
// midpoint. Returns false for everything else, which the caller turns into a
// discrete flip.
bool interpolate_one(const CssValue& a, const CssValue& b, double t, std::string* out) {
    if (a.kind() != b.kind()) return false;
    switch (a.kind()) {
        case CssValueKind::Number:
            *out = num(lerp(static_cast<const CssNumber&>(a).value,
                            static_cast<const CssNumber&>(b).value, t));
            return true;
        case CssValueKind::Percentage:
            *out = num(lerp(static_cast<const CssPercentage&>(a).value,
                            static_cast<const CssPercentage&>(b).value, t)) + "%";
            return true;
        case CssValueKind::Length: {
            const auto& la = static_cast<const CssLength&>(a);
            const auto& lb = static_cast<const CssLength&>(b);
            // Mixed units need a resolution context this does not have -- `1em`
            // to `20px` depends on the font size. Same-unit is the case that
            // covers real stylesheets, and the rest stays discrete rather than
            // guessing.
            if (la.unit != lb.unit) return false;
            const char* u = unit_name(la.unit);
            if (!u) return false;
            *out = num(lerp(la.value, lb.value, t)) + u;
            return true;
        }
        case CssValueKind::Angle: {
            const auto& aa = static_cast<const CssAngle&>(a);
            const auto& ab = static_cast<const CssAngle&>(b);
            if (aa.unit != ab.unit) return false;
            const char* u = angle_unit_name(aa.unit);
            if (!u) return false;
            *out = num(lerp(aa.value, ab.value, t)) + u;
            return true;
        }
        case CssValueKind::Color: {
            const auto& ca = static_cast<const CssColor&>(a);
            const auto& cb = static_cast<const CssColor&>(b);
            // Premultiplied, so a fade to `transparent` does not travel through
            // black on the way: `transparent` is rgba(0,0,0,0), and mixing its
            // UNpremultiplied channels darkens everything it touches.
            const double aa = ca.a, ba = cb.a;
            const double alpha = lerp(aa, ba, t);
            const auto channel = [&](double x, double y) {
                if (alpha <= 0) return 0.0;
                return lerp(x * aa, y * ba, t) / alpha;
            };
            const auto byte = [](double v) {
                return static_cast<int>(std::lround(std::clamp(v, 0.0, 255.0)));
            };
            char buf[64];
            std::snprintf(buf, sizeof(buf), "rgba(%d, %d, %d, %s)",
                          byte(channel(ca.r, cb.r)), byte(channel(ca.g, cb.g)),
                          byte(channel(ca.b, cb.b)), num(alpha).c_str());
            *out = buf;
            return true;
        }
        default:
            return false;
    }
}

}   // namespace

double Easing::operator()(double t) const {
    t = std::clamp(t, 0.0, 1.0);
    if (kind == Kind::Steps) {
        const int n = std::max(1, steps);
        double i = std::floor(t * n);
        if (step_position == StepPosition::Start || step_position == StepPosition::JumpBoth) i += 1;
        double denom = n;
        if (step_position == StepPosition::JumpNone) denom = n - 1 > 0 ? n - 1 : 1;
        if (step_position == StepPosition::JumpBoth) denom = n + 1;
        return std::clamp(i / denom, 0.0, 1.0);
    }
    // Newton on x(u) = t, then read y(u). The curve is a function of a
    // parameter, not of x, so x has to be inverted before y means anything.
    const auto bezier = [](double p1, double p2, double u) {
        const double v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    };
    const auto slope = [](double p1, double p2, double u) {
        const double v = 1 - u;
        return 3 * v * v * (p1) + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
    };
    double u = t;
    for (int i = 0; i < 8; ++i) {
        const double x = bezier(x1, x2, u) - t;
        if (std::fabs(x) < 1e-6) break;
        const double d = slope(x1, x2, u);
        if (std::fabs(d) < 1e-9) break;
        u -= x / d;
        u = std::clamp(u, 0.0, 1.0);
    }
    return bezier(y1, y2, u);
}

bool parse_easing(std::string_view raw, Easing* out) {
    raw = trim(raw);
    if (raw.empty() || !out) return false;
    const auto cubic = [&](double a, double b, double c, double d) {
        out->kind = Easing::Kind::CubicBezier;
        out->x1 = a; out->y1 = b; out->x2 = c; out->y2 = d;
        return true;
    };
    if (iequals(raw, "linear")) return cubic(0, 0, 1, 1);
    if (iequals(raw, "ease")) return cubic(0.25, 0.1, 0.25, 1);
    if (iequals(raw, "ease-in")) return cubic(0.42, 0, 1, 1);
    if (iequals(raw, "ease-out")) return cubic(0, 0, 0.58, 1);
    if (iequals(raw, "ease-in-out")) return cubic(0.42, 0, 0.58, 1);
    if (iequals(raw, "step-start")) {
        out->kind = Easing::Kind::Steps;
        out->steps = 1;
        out->step_position = Easing::StepPosition::Start;
        return true;
    }
    if (iequals(raw, "step-end")) {
        out->kind = Easing::Kind::Steps;
        out->steps = 1;
        out->step_position = Easing::StepPosition::End;
        return true;
    }
    const size_t open = raw.find('(');
    if (open == std::string_view::npos || raw.back() != ')') return false;
    const std::string_view name = trim(raw.substr(0, open));
    const std::vector<std::string_view> args =
        split(raw.substr(open + 1, raw.size() - open - 2), ',');
    if (iequals(name, "cubic-bezier")) {
        if (args.size() != 4) return false;
        double v[4];
        for (int i = 0; i < 4; ++i) {
            if (!parse_number(args[static_cast<size_t>(i)], &v[i])) return false;
        }
        // x must stay in 0..1 or the curve is not a function of time.
        if (v[0] < 0 || v[0] > 1 || v[2] < 0 || v[2] > 1) return false;
        return cubic(v[0], v[1], v[2], v[3]);
    }
    if (iequals(name, "steps")) {
        if (args.empty() || args.size() > 2) return false;
        double n = 0;
        if (!parse_number(args[0], &n) || n < 1) return false;
        out->kind = Easing::Kind::Steps;
        out->steps = static_cast<int>(n);
        out->step_position = Easing::StepPosition::End;
        if (args.size() == 2) {
            const std::string_view p = args[1];
            if (iequals(p, "start") || iequals(p, "jump-start")) {
                out->step_position = Easing::StepPosition::Start;
            } else if (iequals(p, "end") || iequals(p, "jump-end")) {
                out->step_position = Easing::StepPosition::End;
            } else if (iequals(p, "jump-none")) {
                out->step_position = Easing::StepPosition::JumpNone;
            } else if (iequals(p, "jump-both")) {
                out->step_position = Easing::StepPosition::JumpBoth;
            } else {
                return false;
            }
        }
        return true;
    }
    return false;
}

bool parse_time_seconds(std::string_view raw, double* out) {
    raw = trim(raw);
    if (raw.empty() || !out) return false;
    double scale = 0;
    if (raw.size() > 2 && iequals(raw.substr(raw.size() - 2), "ms")) {
        raw.remove_suffix(2);
        scale = 0.001;
    } else if (raw.size() > 1 && (raw.back() == 's' || raw.back() == 'S')) {
        raw.remove_suffix(1);
        scale = 1.0;
    } else {
        // A unitless zero is the one time that needs no unit.
        double z = 0;
        if (parse_number(raw, &z) && z == 0) { *out = 0; return true; }
        return false;
    }
    double v = 0;
    if (!parse_number(raw, &v)) return false;
    *out = v * scale;
    return true;
}

bool interpolate_css(std::string_view from, std::string_view to, double t, std::string* out) {
    if (!out) return false;
    from = trim(from);
    to = trim(to);
    if (from == to) { *out = std::string(to); return true; }

    // The discrete rule, which is also the fallback for everything that cannot
    // be mixed: CSS Transitions L1 §4 flips at the halfway point.
    const auto discrete = [&] {
        *out = std::string(t < 0.5 ? from : to);
        return true;
    };
    if (from.empty() || to.empty()) return discrete();

    // Lists interpolate item by item when they have the same shape, which is
    // what carries `padding: 4px 8px` and a multi-stop shadow. A different
    // length means a different shape, and that is discrete.
    for (const char sep : {',', ' '}) {
        const std::vector<std::string_view> a = split(from, sep);
        const std::vector<std::string_view> b = split(to, sep);
        if (a.size() < 2 || a.size() != b.size()) continue;
        std::string joined;
        for (size_t i = 0; i < a.size(); ++i) {
            std::string piece;
            if (!interpolate_css(a[i], b[i], t, &piece)) return discrete();
            if (i) joined += (sep == ',' ? ", " : " ");
            joined += piece;
        }
        *out = joined;
        return true;
    }

    CssParseError err;
    const CssValuePtr va = parse_css_value(from, &err);
    const CssValuePtr vb = parse_css_value(to, &err);
    if (!va || !vb) return discrete();
    if (interpolate_one(*va, *vb, t, out)) return true;
    return discrete();
}

}   // namespace weva
