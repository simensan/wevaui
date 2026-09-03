#include "weva/shorthand.h"

#include "weva/animation.h"

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

bool is_css_wide_keyword(std::string_view s) {
    return iequals(s, "inherit") || iequals(s, "initial") || iequals(s, "unset") ||
           iequals(s, "revert") || iequals(s, "revert-layer");
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

// Splits on commas at paren depth 0, so `rgba(0, 0, 0, .5)` stays one piece.
// A bare number, which `animation` uses for its iteration count.
bool is_number_token(std::string_view s) {
    if (s.empty()) return false;
    bool digit = false;
    for (size_t i = 0; i < s.size(); ++i) {
        const char c = s[i];
        if (c >= '0' && c <= '9') { digit = true; continue; }
        if (c == '.' || ((c == '+' || c == '-') && i == 0)) continue;
        return false;
    }
    return digit;
}

std::vector<std::string_view> split_commas(std::string_view v) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < v.size(); ++i) {
        if (v[i] == '(') ++depth;
        else if (v[i] == ')') { if (depth > 0) --depth; }
        else if (v[i] == ',' && depth == 0) {
            out.push_back(v.substr(start, i - start));
            start = i + 1;
        }
    }
    out.push_back(v.substr(start));
    return out;
}

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

    // CSS Flexbox L1 §5.1: `flex-flow` is `flex-direction || flex-wrap`, in
    // either order and either alone. Unexpanded, `flex-flow: column wrap` set
    // neither -- a column layout came out a row, which is not a subtle wrong.
    if (name == "flex-flow") {
        if (t.empty() || t.size() > 2) return true;
        const auto is_direction = [](std::string_view v) {
            return iequals(v, "row") || iequals(v, "row-reverse") || iequals(v, "column") ||
                   iequals(v, "column-reverse");
        };
        const auto is_wrap = [](std::string_view v) {
            return iequals(v, "nowrap") || iequals(v, "wrap") || iequals(v, "wrap-reverse");
        };
        std::string_view direction = "row";
        std::string_view wrap = "nowrap";
        bool had_direction = false, had_wrap = false;
        for (std::string_view v : t) {
            if (!had_direction && is_direction(v)) {
                direction = v;
                had_direction = true;
                continue;
            }
            if (!had_wrap && is_wrap(v)) {
                wrap = v;
                had_wrap = true;
                continue;
            }
            // A token that is neither drops the whole declaration, as an
            // invalid shorthand should.
            return true;
        }
        emit(out, "flex-direction", direction);
        emit(out, "flex-wrap", wrap);
        return true;
    }

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
    // CSS Fonts L4 §4.4: [style || variant || weight || stretch]? size [/ line-height]?
    // family. Every longhand the shorthand covers is reset, which is what
    // makes `font: bold 14px sans-serif` on a button give a 16px line rather
    // than inherit the 18.29 of the page. System-font keywords (`menu`,
    // `caption`...) are not resolved and drop the declaration.
    // CSS Backgrounds L3 §3.10: comma-separated layers, each `<bg-image> ||
    // <position> [ / <bg-size> ]? || <repeat-style> || <attachment> ||
    // <box>{1,2}`, and the colour only on the last. Attachment, origin and
    // clip are accepted and dropped. Every longhand is reset — the colour to
    // `transparent` — which is what makes `background: none` clear a box.
    if (name == "background") {
        if (t.empty()) return true;
        if (t.size() == 1 && is_css_wide_keyword(t[0])) {
            for (const char* p : {"background-color", "background-image", "background-position",
                                  "background-size", "background-repeat"}) {
                emit(out, p, t[0]);
            }
            return true;
        }
        const auto is_repeat_word = [](std::string_view s) {
            return iequals(s, "repeat") || iequals(s, "no-repeat") || iequals(s, "repeat-x") ||
                   iequals(s, "repeat-y") || iequals(s, "space") || iequals(s, "round");
        };
        const auto is_position_word = [](std::string_view s) {
            return iequals(s, "left") || iequals(s, "right") || iequals(s, "top") ||
                   iequals(s, "bottom") || iequals(s, "center");
        };
        const auto is_size_word = [](std::string_view s) {
            return iequals(s, "auto") || iequals(s, "cover") || iequals(s, "contain");
        };
        const auto is_dropped_word = [](std::string_view s) {
            return iequals(s, "scroll") || iequals(s, "fixed") || iequals(s, "local") ||
                   iequals(s, "border-box") || iequals(s, "padding-box") ||
                   iequals(s, "content-box") || iequals(s, "text");
        };
        const auto is_image = [](std::string_view s) {
            return iequals(s, "none") || istarts_with(s, "url(") ||
                   s.find("gradient(") != std::string_view::npos ||
                   istarts_with(s, "image-set(") || istarts_with(s, "cross-fade(");
        };
        std::string images, positions, sizes, repeats;
        std::string color = "transparent";
        const auto join = [](std::string* list, std::string_view piece) {
            if (!list->empty()) *list += ", ";
            list->append(piece);
        };
        size_t i = 0;
        while (i <= t.size()) {
            std::string image = "none", position, size, repeat;
            bool after_slash = false;
            for (; i < t.size() && t[i] != ","; ++i) {
                const std::string_view tok = t[i];
                if (tok == "/") { after_slash = true; continue; }
                if (after_slash && (is_size_word(tok) || is_length_or_percentage(tok))) {
                    if (!size.empty()) size += ' ';
                    size.append(tok);
                    continue;
                }
                after_slash = false;
                if (is_image(tok)) image = std::string(tok);
                else if (is_repeat_word(tok)) {
                    if (!repeat.empty()) repeat += ' ';
                    repeat.append(tok);
                } else if (is_position_word(tok) || is_length_or_percentage(tok)) {
                    if (!position.empty()) position += ' ';
                    position.append(tok);
                } else if (is_dropped_word(tok)) {
                    continue;
                } else if (is_color_token(tok)) {
                    color = std::string(tok);
                } else {
                    return false;
                }
            }
            join(&images, image);
            join(&positions, position.empty() ? "0% 0%" : position);
            join(&sizes, size.empty() ? "auto" : size);
            join(&repeats, repeat.empty() ? "repeat" : repeat);
            if (i >= t.size()) break;
            ++i;   // the comma
        }
        emit(out, "background-color", color);
        emit(out, "background-image", images);
        emit(out, "background-position", positions);
        emit(out, "background-size", sizes);
        emit(out, "background-repeat", repeats);
        return true;
    }

    if (name == "font") {
        if (t.empty()) return true;
        if (t.size() == 1 && is_css_wide_keyword(t[0])) {
            for (const char* p : {"font-style", "font-variant", "font-weight", "font-stretch",
                                  "font-size", "line-height", "font-family"}) {
                emit(out, p, t[0]);
            }
            return true;
        }
        std::string_view style, variant, weight, stretch, size, line_height;
        size_t i = 0;
        for (; i < t.size(); ++i) {
            const std::string_view v = t[i];
            if (iequals(v, "normal")) continue;   // any of the four; all default to it
            if (style.empty() && (iequals(v, "italic") || iequals(v, "oblique"))) {
                style = v;
            } else if (variant.empty() && iequals(v, "small-caps")) {
                variant = v;
            } else if (weight.empty() &&
                       (iequals(v, "bold") || iequals(v, "bolder") || iequals(v, "lighter") ||
                        (is_number(v) && v.find('.') == std::string_view::npos && v.size() == 3 &&
                         v[1] == '0' && v[2] == '0'))) {
                weight = v;
            } else if (stretch.empty() &&
                       (iequals(v, "condensed") || iequals(v, "expanded") ||
                        iequals(v, "semi-condensed") || iequals(v, "semi-expanded") ||
                        iequals(v, "extra-condensed") || iequals(v, "extra-expanded") ||
                        iequals(v, "ultra-condensed") || iequals(v, "ultra-expanded"))) {
                stretch = v;
            } else {
                break;
            }
        }
        if (i >= t.size()) return true;
        {
            const std::string_view v = t[i];
            static const char* kSizes[] = {"xx-small", "x-small", "small",  "medium",   "large",
                                           "x-large",  "xx-large", "xxx-large", "larger", "smaller"};
            bool keyword = false;
            for (const char* k : kSizes) keyword = keyword || iequals(v, k);
            if (!(keyword || is_length_or_percentage(v) || is_math_function(v))) return true;
            size = v;
            ++i;
        }
        if (i < t.size() && t[i] == "/") {
            if (i + 1 >= t.size()) return true;
            line_height = t[i + 1];
            i += 2;
        }
        if (i >= t.size()) return true;   // the family is mandatory
        std::string family;
        for (; i < t.size(); ++i) {
            if (t[i] == ",") { family += ","; continue; }
            if (!family.empty() && family.back() != ',') family += " ";
            else if (!family.empty()) family += " ";
            family += std::string(t[i]);
        }
        emit(out, "font-style", style.empty() ? "normal" : style);
        emit(out, "font-variant", variant.empty() ? "normal" : variant);
        emit(out, "font-weight", weight.empty() ? "normal" : weight);
        emit(out, "font-stretch", stretch.empty() ? "normal" : stretch);
        emit(out, "font-size", size);
        emit(out, "line-height", line_height.empty() ? "normal" : line_height);
        emit(out, "font-family", family);
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

    // ---- transition: a comma-separated list, each piece in any order
    //
    // `transition: background-color 200ms ease, width .3s` -- and the two times
    // are told apart by ORDER, not by form: the first is the duration and the
    // second the delay. Every piece is optional, so each longhand collects one
    // entry per comma group and the list lengths line up.
    if (name == "transition") {
        std::string props, durations, easings, delays;
        for (std::string_view part : split_commas(value)) {
            std::string_view prop = "all", dur = "0s", ease = "ease", delay = "0s";
            int times = 0;
            bool bad = false;
            for (std::string_view tok : tokenize_shorthand(part)) {
                double seconds = 0;
                if (parse_time_seconds(tok, &seconds)) {
                    if (times == 0) dur = tok;
                    else if (times == 1) delay = tok;
                    else { bad = true; break; }
                    ++times;
                    continue;
                }
                Easing curve;
                if (parse_easing(tok, &curve)) { ease = tok; continue; }
                // Whatever is left is the property name. `none` is legal and
                // means the entry transitions nothing.
                prop = tok;
            }
            if (bad) return true;   // a malformed entry drops the declaration
            const auto add = [](std::string* dst, std::string_view v) {
                if (!dst->empty()) *dst += ", ";
                dst->append(v);
            };
            add(&props, prop);
            add(&durations, dur);
            add(&easings, ease);
            add(&delays, delay);
        }
        if (props.empty()) return true;
        emit(out, "transition-property", props);
        emit(out, "transition-duration", durations);
        emit(out, "transition-timing-function", easings);
        emit(out, "transition-delay", delays);
        return true;
    }

    // ---- animation: like transition, but with more keywords competing for
    // the same slot. Order decides only the two times; everything else is told
    // apart by what it looks like, and whatever is left over is the name.
    if (name == "animation") {
        std::string names, durations, easings, delays, counts, directions, fills, states;
        for (std::string_view part : split_commas(value)) {
            std::string_view nm = "none", dur = "0s", ease = "ease", delay = "0s";
            std::string_view count = "1", dir = "normal", fill = "none", play = "running";
            int times = 0;
            for (std::string_view tok : tokenize_shorthand(part)) {
                double seconds = 0;
                if (parse_time_seconds(tok, &seconds)) {
                    if (times == 0) dur = tok;
                    else if (times == 1) delay = tok;
                    ++times;
                    continue;
                }
                Easing curve;
                if (parse_easing(tok, &curve)) { ease = tok; continue; }
                if (tok == "infinite" || is_number_token(tok)) { count = tok; continue; }
                if (tok == "normal" || tok == "reverse" || tok == "alternate" ||
                    tok == "alternate-reverse") {
                    dir = tok;
                    continue;
                }
                if (tok == "forwards" || tok == "backwards" || tok == "both") {
                    fill = tok;
                    continue;
                }
                if (tok == "running" || tok == "paused") { play = tok; continue; }
                if (tok == "none") continue;   // the initial name; keep looking
                nm = tok;
            }
            const auto add = [](std::string* dst, std::string_view v) {
                if (!dst->empty()) *dst += ", ";
                dst->append(v);
            };
            add(&names, nm);
            add(&durations, dur);
            add(&easings, ease);
            add(&delays, delay);
            add(&counts, count);
            add(&directions, dir);
            add(&fills, fill);
            add(&states, play);
        }
        if (names.empty()) return true;
        emit(out, "animation-name", names);
        emit(out, "animation-duration", durations);
        emit(out, "animation-timing-function", easings);
        emit(out, "animation-delay", delays);
        emit(out, "animation-iteration-count", counts);
        emit(out, "animation-direction", directions);
        emit(out, "animation-fill-mode", fills);
        emit(out, "animation-play-state", states);
        return true;
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
