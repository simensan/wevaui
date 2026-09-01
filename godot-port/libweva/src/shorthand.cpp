#include "weva/shorthand.h"

#include "weva/css_value.h"

#include <array>
#include <cstring>

namespace weva {

namespace {

bool is_space(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}

bool is_digit(char c) { return c >= '0' && c <= '9'; }

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

bool istarts_with(std::string_view s, std::string_view prefix) {
    return s.size() >= prefix.size() && iequals(s.substr(0, prefix.size()), prefix);
}

bool is_number(std::string_view s) {
    double d = 0;
    return css_parse_double(s, &d);
}

// A <length> token, reproducing the reference's unit list EXACTLY — including
// that it accepts `%` and omits the newer units (cap, ic, lh, rlh, sv*, lv*,
// dv*, cq*). A `padding: 1lh` therefore fails validation, the shorthand does
// not expand, and the layout side reads it through the raw-shorthand path
// instead. Widening this list here would change which declarations reach the
// longhands at all.
bool is_length_token(std::string_view s) {
    if (s.empty()) return false;
    if (s == "0") return true;
    const char c0 = s[0];
    if (!(c0 == '-' || c0 == '+' || is_digit(c0) || c0 == '.')) return false;

    size_t i = (c0 == '+' || c0 == '-') ? 1 : 0;
    bool saw_digit = false;
    while (i < s.size() && is_digit(s[i])) { saw_digit = true; ++i; }
    if (i < s.size() && s[i] == '.') {
        ++i;
        while (i < s.size() && is_digit(s[i])) { saw_digit = true; ++i; }
    }
    if (!saw_digit) return false;
    if (i == s.size()) return false;   // a bare number is not a length

    static const char* kUnits[] = {"px", "em", "rem", "%",  "vh", "vw", "vmin", "vmax",
                                   "pt", "pc", "in",  "cm", "mm", "ch", "ex"};
    const std::string_view unit = s.substr(i);
    for (const char* u : kUnits) {
        if (iequals(unit, u)) return true;
    }
    return false;
}

bool is_percentage_token(std::string_view s) {
    return !s.empty() && s.back() == '%' && is_number(s.substr(0, s.size() - 1));
}

bool is_length_or_percentage(std::string_view s) {
    return is_length_token(s) || is_percentage_token(s);
}

// Any CSS math function, despite the name the reference kept.
bool is_math_function(std::string_view s) {
    return istarts_with(s, "calc(") || istarts_with(s, "clamp(") || istarts_with(s, "min(") ||
           istarts_with(s, "max(");
}

bool is_border_style(std::string_view s) {
    static const char* kStyles[] = {"none",   "hidden", "dotted", "dashed", "solid",
                                    "double", "groove", "ridge",  "inset",  "outset"};
    for (const char* k : kStyles) {
        if (iequals(s, k)) return true;
    }
    return false;
}

bool is_border_width_keyword(std::string_view s) {
    return iequals(s, "thin") || iequals(s, "medium") || iequals(s, "thick");
}

bool is_hex_color(std::string_view s) {
    if (s.empty() || s[0] != '#') return false;
    for (size_t i = 1; i < s.size(); ++i) {
        const char c = s[i];
        const bool hex = is_digit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (!hex) return false;
    }
    const size_t len = s.size() - 1;
    return len == 3 || len == 4 || len == 6 || len == 8;
}

bool is_color_function(std::string_view s) {
    static const char* kFns[] = {"rgb(",   "rgba(", "hsl(", "hsla(",  "hwb(",
                                 "oklab(", "oklch(", "lab(", "lch(", "color(", "color-mix("};
    for (const char* f : kFns) {
        if (istarts_with(s, f)) return true;
    }
    return false;
}

bool is_color_token(std::string_view s) {
    if (is_hex_color(s) || is_color_function(s)) return true;
    if (iequals(s, "currentcolor") || iequals(s, "transparent")) return true;
    CssColor c;
    return css_color_from_name(s, &c);
}

bool is_edge_value(std::string_view s, bool allow_auto) {
    if (allow_auto && iequals(s, "auto")) return true;
    if (s == "0") return true;
    return is_length_or_percentage(s) || is_math_function(s);
}

bool is_border_width_value(std::string_view s) {
    if (is_border_width_keyword(s)) return true;
    if (s == "0") return true;
    return is_length_token(s) || is_math_function(s);
}

void emit(std::vector<ShorthandLonghand>* out, std::string_view property, std::string_view value) {
    out->push_back({property, std::string(value)});
}

// The 1-to-4 value box fill: one value covers all sides, two split
// vertical/horizontal, three leave `left` mirroring `right`.
struct FourSides { std::string_view top, right, bottom, left; };
FourSides fill_four(const std::vector<std::string_view>& t) {
    switch (t.size()) {
        case 1: return {t[0], t[0], t[0], t[0]};
        case 2: return {t[0], t[1], t[0], t[1]};
        case 3: return {t[0], t[1], t[2], t[1]};
        default: return {t[0], t[1], t[2], t[3]};
    }
}

bool expand_edges(const std::vector<std::string_view>& t, bool allow_auto,
                  const char* top, const char* right, const char* bottom, const char* left,
                  std::vector<ShorthandLonghand>* out) {
    if (t.empty() || t.size() > 4) return true;   // malformed: expands to nothing
    for (std::string_view s : t) {
        if (!is_edge_value(s, allow_auto)) return true;
    }
    const FourSides f = fill_four(t);
    emit(out, top, f.top);
    emit(out, right, f.right);
    emit(out, bottom, f.bottom);
    emit(out, left, f.left);
    return true;
}

// `border` and its per-side forms take width, style and colour in any order,
// each at most once. Every component the author omitted resets to its INITIAL
// value rather than being left alone — which is why `border: solid` clears a
// previously declared border-width back to `medium`.
bool parse_border_triplet(const std::vector<std::string_view>& t, std::string_view* width,
                          std::string_view* style, std::string_view* color) {
    *width = "medium";
    *style = "none";
    *color = "currentcolor";
    if (t.empty() || t.size() > 3) return false;
    bool has_width = false, has_style = false, has_color = false;
    for (std::string_view s : t) {
        if (!has_style && is_border_style(s)) { *style = s; has_style = true; continue; }
        if (!has_width && is_border_width_value(s)) { *width = s; has_width = true; continue; }
        if (!has_color && is_color_token(s)) { *color = s; has_color = true; continue; }
        return false;
    }
    return true;
}

struct SideNames { const char* width; const char* style; const char* color; };
constexpr SideNames kBorderSides[4] = {
    {"border-top-width", "border-top-style", "border-top-color"},
    {"border-right-width", "border-right-style", "border-right-color"},
    {"border-bottom-width", "border-bottom-style", "border-bottom-color"},
    {"border-left-width", "border-left-style", "border-left-color"},
};

int side_index(std::string_view name) {
    if (name == "border-top") return 0;
    if (name == "border-right") return 1;
    if (name == "border-bottom") return 2;
    return 3;
}

bool expand_two_axis(const std::vector<std::string_view>& t, const char* first,
                     const char* second, bool (*valid)(std::string_view),
                     std::vector<ShorthandLonghand>* out) {
    if (t.empty() || t.size() > 2) return true;
    for (std::string_view s : t) {
        if (!valid(s)) return true;
    }
    emit(out, first, t[0]);
    emit(out, second, t.size() == 2 ? t[1] : t[0]);
    return true;
}

bool is_overflow_keyword(std::string_view s) {
    return iequals(s, "visible") || iequals(s, "hidden") || iequals(s, "scroll") ||
           iequals(s, "auto") || iequals(s, "clip");
}
bool is_overscroll_keyword(std::string_view s) {
    return iequals(s, "auto") || iequals(s, "contain") || iequals(s, "none");
}
bool is_gap_value(std::string_view s) {
    return iequals(s, "normal") || s == "0" || is_length_or_percentage(s) || is_math_function(s);
}
bool is_place_value(std::string_view s) { return s != "," && s != "/"; }
bool is_radius_value(std::string_view s) {
    return s == "0" || is_length_or_percentage(s) || is_math_function(s);
}
bool is_logical_edge_auto(std::string_view s) { return is_edge_value(s, true); }
bool is_logical_edge_no_auto(std::string_view s) { return is_edge_value(s, false); }

bool expand_border_radius(const std::vector<std::string_view>& t,
                          std::vector<ShorthandLonghand>* out) {
    if (t.empty()) return true;
    // A `/` splits horizontal radii from vertical ones, giving elliptical
    // corners.
    std::vector<std::string_view> h, v;
    bool after_slash = false;
    bool has_slash = false;
    for (std::string_view s : t) {
        if (s == "/") { after_slash = true; has_slash = true; continue; }
        (after_slash ? v : h).push_back(s);
    }
    if (h.empty() || h.size() > 4) return true;
    if (has_slash && (v.empty() || v.size() > 4)) return true;
    for (std::string_view s : h) {
        if (!is_radius_value(s)) return true;
    }
    for (std::string_view s : v) {
        if (!is_radius_value(s)) return true;
    }
    // Corner fill order is TL, TR, BR, BL — not the top/right/bottom/left of
    // the edge shorthands.
    const FourSides hc = fill_four(h);
    const FourSides vc = has_slash ? fill_four(v) : hc;
    const std::string_view hx[4] = {hc.top, hc.right, hc.bottom, hc.left};
    const std::string_view vy[4] = {vc.top, vc.right, vc.bottom, vc.left};
    static const char* kCorners[4] = {"border-top-left-radius", "border-top-right-radius",
                                      "border-bottom-right-radius", "border-bottom-left-radius"};
    for (int i = 0; i < 4; ++i) {
        // Collapsed to one token when the axes agree, so a circular corner
        // round-trips identically to a directly authored longhand.
        if (hx[i] == vy[i]) emit(out, kCorners[i], hx[i]);
        else out->push_back({kCorners[i], std::string(hx[i]) + " " + std::string(vy[i])});
    }
    return true;
}

} // namespace

std::vector<std::string_view> tokenize_shorthand(std::string_view v) {
    std::vector<std::string_view> out;
    size_t i = 0;
    const size_t n = v.size();
    while (i < n) {
        const char c = v[i];
        if (is_space(c)) { ++i; continue; }
        if (c == ',') { out.push_back(v.substr(i, 1)); ++i; continue; }
        if (c == '/') { out.push_back(v.substr(i, 1)); ++i; continue; }
        if (c == '"' || c == '\'') {
            const size_t start = i;
            const char quote = c;
            ++i;
            while (i < n && v[i] != quote) {
                if (v[i] == '\\' && i + 1 < n) i += 2;
                else ++i;
            }
            if (i < n) ++i;
            out.push_back(v.substr(start, i - start));
            continue;
        }
        const size_t start = i;
        int depth = 0;
        while (i < n) {
            const char ch = v[i];
            if (depth == 0 && (is_space(ch) || ch == ',' || ch == '/')) break;
            if (ch == '(') ++depth;
            else if (ch == ')' && depth > 0) --depth;
            else if ((ch == '"' || ch == '\'') && depth > 0) {
                const char q = ch;
                ++i;
                while (i < n && v[i] != q) {
                    if (v[i] == '\\' && i + 1 < n) i += 2;
                    else ++i;
                }
                if (i < n) ++i;
                continue;
            }
            ++i;
        }
        out.push_back(v.substr(start, i - start));
    }
    return out;
}

bool contains_substitution(std::string_view value) {
    // The '(' guard keeps the common case to one cheap scan.
    if (value.find('(') == std::string_view::npos) return false;
    for (size_t i = 0; i + 4 <= value.size(); ++i) {
        if (iequals(value.substr(i, 4), "var(")) return true;
    }
    for (size_t i = 0; i + 5 <= value.size(); ++i) {
        if (iequals(value.substr(i, 5), "attr(")) return true;
    }
    return false;
}

bool is_shorthand(std::string_view name) {
    std::vector<ShorthandLonghand> scratch;
    return expand_shorthand(name, "", &scratch);
}

bool expand_shorthand(std::string_view name, std::string_view value,
                      std::vector<ShorthandLonghand>* out) {
    const std::vector<std::string_view> t = tokenize_shorthand(value);

    // ---- 1-to-4 edge shorthands
    if (name == "margin") {
        return expand_edges(t, true, "margin-top", "margin-right", "margin-bottom",
                            "margin-left", out);
    }
    if (name == "padding") {
        return expand_edges(t, false, "padding-top", "padding-right", "padding-bottom",
                            "padding-left", out);
    }
    if (name == "scroll-padding") {
        return expand_edges(t, true, "scroll-padding-top", "scroll-padding-right",
                            "scroll-padding-bottom", "scroll-padding-left", out);
    }
    if (name == "scroll-margin") {
        return expand_edges(t, false, "scroll-margin-top", "scroll-margin-right",
                            "scroll-margin-bottom", "scroll-margin-left", out);
    }
    // `inset` fills the BARE side names, which is why it cannot reuse a
    // prefix-based expander.
    if (name == "inset") {
        return expand_edges(t, true, "top", "right", "bottom", "left", out);
    }

    // ---- logical two-sided box shorthands
    if (name == "margin-inline") {
        return expand_two_axis(t, "margin-inline-start", "margin-inline-end",
                               is_logical_edge_auto, out);
    }
    if (name == "margin-block") {
        return expand_two_axis(t, "margin-block-start", "margin-block-end",
                               is_logical_edge_auto, out);
    }
    if (name == "padding-inline") {
        return expand_two_axis(t, "padding-inline-start", "padding-inline-end",
                               is_logical_edge_no_auto, out);
    }
    if (name == "padding-block") {
        return expand_two_axis(t, "padding-block-start", "padding-block-end",
                               is_logical_edge_no_auto, out);
    }
    if (name == "inset-inline") {
        return expand_two_axis(t, "inset-inline-start", "inset-inline-end",
                               is_logical_edge_auto, out);
    }
    if (name == "inset-block") {
        return expand_two_axis(t, "inset-block-start", "inset-block-end",
                               is_logical_edge_auto, out);
    }

    // ---- border family
    if (name == "border") {
        std::string_view w, s, c;
        if (!parse_border_triplet(t, &w, &s, &c)) return true;
        for (const SideNames& side : kBorderSides) {
            emit(out, side.width, w);
            emit(out, side.style, s);
            emit(out, side.color, c);
        }
        return true;
    }
    if (name == "border-top" || name == "border-right" || name == "border-bottom" ||
        name == "border-left") {
        std::string_view w, s, c;
        if (!parse_border_triplet(t, &w, &s, &c)) return true;
        const SideNames& side = kBorderSides[side_index(name)];
        emit(out, side.width, w);
        emit(out, side.style, s);
        emit(out, side.color, c);
        return true;
    }
    if (name == "border-width" || name == "border-style" || name == "border-color") {
        if (t.empty() || t.size() > 4) return true;
        const bool is_w = name == "border-width";
        const bool is_s = name == "border-style";
        for (std::string_view s : t) {
            const bool ok = is_w ? is_border_width_value(s)
                                 : (is_s ? is_border_style(s) : is_color_token(s));
            if (!ok) return true;
        }
        const FourSides f = fill_four(t);
        const std::string_view vals[4] = {f.top, f.right, f.bottom, f.left};
        for (int i = 0; i < 4; ++i) {
            const SideNames& side = kBorderSides[i];
            emit(out, is_w ? side.width : (is_s ? side.style : side.color), vals[i]);
        }
        return true;
    }
    if (name == "border-radius") return expand_border_radius(t, out);

    // ---- two-value axis shorthands
    // CSS Flexbox L1 §7.1.1. The one-value forms are the ones that matter and
    // the ones that are easy to get wrong: a bare NUMBER is flex-grow with
    // basis 0%, while a bare LENGTH is the basis with grow 1 — `flex: 1` and
    // `flex: 1px` mean different things in every component.
    if (name == "flex") {
        if (t.empty() || t.size() > 3) return true;
        if (t.size() == 1 && iequals(t[0], "none")) {
            emit(out, "flex-grow", "0");
            emit(out, "flex-shrink", "0");
            emit(out, "flex-basis", "auto");
            return true;
        }
        if (t.size() == 1 && iequals(t[0], "initial")) {
            emit(out, "flex-grow", "0");
            emit(out, "flex-shrink", "1");
            emit(out, "flex-basis", "auto");
            return true;
        }
        std::string_view grow, shrink, basis;
        for (std::string_view v : t) {
            if (is_number(v) && grow.empty()) { grow = v; continue; }
            if (is_number(v) && shrink.empty()) { shrink = v; continue; }
            if (basis.empty() && (iequals(v, "auto") || iequals(v, "content") || v == "0" ||
                                  is_length_or_percentage(v) || is_math_function(v))) {
                basis = v;
                continue;
            }
            return true;   // unrecognised: the whole declaration is invalid
        }
        if (grow.empty()) return true;
        emit(out, "flex-grow", grow);
        emit(out, "flex-shrink", shrink.empty() ? "1" : shrink);
        // A one- or two-number form sets the basis to zero, NOT auto: this is
        // what makes `flex: 1` share space equally regardless of content.
        emit(out, "flex-basis", basis.empty() ? "0%" : basis);
        return true;
    }
    if (name == "gap") return expand_two_axis(t, "row-gap", "column-gap", is_gap_value, out);
    if (name == "overflow") {
        return expand_two_axis(t, "overflow-x", "overflow-y", is_overflow_keyword, out);
    }
    if (name == "overscroll-behavior") {
        return expand_two_axis(t, "overscroll-behavior-x", "overscroll-behavior-y",
                               is_overscroll_keyword, out);
    }
    // place-* takes align first, justify second — the reverse of the x/y order
    // the other two-value shorthands use.
    if (name == "place-items") {
        return expand_two_axis(t, "align-items", "justify-items", is_place_value, out);
    }
    if (name == "place-content") {
        return expand_two_axis(t, "align-content", "justify-content", is_place_value, out);
    }
    if (name == "place-self") {
        return expand_two_axis(t, "align-self", "justify-self", is_place_value, out);
    }

    // ---- outline: the border triplet with `invert` as the initial colour
    if (name == "outline") {
        std::string_view width = "medium", style = "none", color = "invert";
        if (t.empty() || t.size() > 3) return true;
        bool has_width = false, has_style = false, has_color = false;
        for (std::string_view s : t) {
            if (!has_style && is_border_style(s)) { style = s; has_style = true; continue; }
            if (!has_width && is_border_width_value(s)) { width = s; has_width = true; continue; }
            if (!has_color && (s == "invert" || is_color_token(s))) {
                color = s;
                has_color = true;
                continue;
            }
            return true;
        }
        emit(out, "outline-width", width);
        emit(out, "outline-style", style);
        emit(out, "outline-color", color);
        return true;
    }

    return false;
}

} // namespace weva
