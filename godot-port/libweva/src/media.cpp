#include "weva/media.h"

#include "weva/css_properties.h"
#include "weva/css_value.h"

#include <cmath>

namespace weva {

namespace {

bool is_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}

std::string ascii_lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return o;
}

std::string trim(std::string_view s) {
    std::size_t b = 0, e = s.size();
    while (b < e && is_ws(s[b])) ++b;
    while (e > b && is_ws(s[e - 1])) --e;
    return std::string(s.substr(b, e - b));
}

// Splits on a top-level separator word (surrounded by whitespace, outside
// parens), e.g. "and" / "or". Returns false when the word is not present.
bool split_on_keyword(std::string_view s, std::string_view kw,
                      std::vector<std::string>* parts) {
    parts->clear();
    int depth = 0;
    std::size_t start = 0;
    bool found = false;
    for (std::size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '(') { ++depth; continue; }
        if (s[i] == ')') { --depth; continue; }
        if (depth != 0) continue;
        if (i + kw.size() > s.size()) continue;
        // Must be a standalone word.
        bool left_ok = i == 0 || is_ws(s[i - 1]);
        bool right_ok = i + kw.size() == s.size() || is_ws(s[i + kw.size()]);
        if (!left_ok || !right_ok) continue;
        if (ascii_lower(s.substr(i, kw.size())) != kw) continue;
        parts->push_back(trim(s.substr(start, i - start)));
        start = i + kw.size();
        i += kw.size() - 1;
        found = true;
    }
    if (!found) return false;
    parts->push_back(trim(s.substr(start)));
    return true;
}

std::vector<std::string> split_top_level_commas(std::string_view s) {
    std::vector<std::string> out;
    int depth = 0;
    std::size_t start = 0;
    for (std::size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '(') ++depth;
        else if (s[i] == ')') --depth;
        else if (s[i] == ',' && depth == 0) {
            out.push_back(trim(s.substr(start, i - start)));
            start = i + 1;
        }
    }
    out.push_back(trim(s.substr(start)));
    return out;
}

// Strips one layer of enclosing parens, if the whole string is wrapped.
bool unwrap_parens(std::string_view s, std::string* inner) {
    if (s.size() < 2 || s.front() != '(' || s.back() != ')') return false;
    int depth = 0;
    for (std::size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '(') ++depth;
        else if (s[i] == ')') {
            --depth;
            if (depth == 0 && i != s.size() - 1) return false;   // "(a) and (b)"
        }
    }
    *inner = trim(s.substr(1, s.size() - 2));
    return true;
}

// Resolves a length/number/resolution feature value to comparable units.
bool feature_value_px(std::string_view text, const MediaContext& ctx, double* out) {
    CssParseError err;
    CssValuePtr v = parse_css_value(text, &err);
    if (!v) return false;
    if (v->kind() == CssValueKind::Number) {
        *out = static_cast<const CssNumber&>(*v).value;
        return true;
    }
    if (v->kind() == CssValueKind::Length) {
        LengthContext lc;
        lc.viewport_width_px = ctx.viewport_width_px;
        lc.viewport_height_px = ctx.viewport_height_px;
        lc.dpi_pixels_per_inch = ctx.dpi_pixels_per_inch;
        return static_cast<const CssLength&>(*v).to_pixels(lc, out);
    }
    return false;
}

enum class Range { Equals, Min, Max };

bool compare_range(double actual, double expected, Range r) {
    switch (r) {
        case Range::Min: return actual >= expected;
        case Range::Max: return actual <= expected;
        case Range::Equals: return actual == expected;
    }
    return false;
}

bool evaluate_feature(std::string_view raw, const MediaContext& ctx) {
    std::string body = trim(raw);
    std::string name, value;
    if (auto colon = body.find(':'); colon != std::string::npos) {
        name = ascii_lower(trim(std::string_view(body).substr(0, colon)));
        value = trim(std::string_view(body).substr(colon + 1));
    } else {
        name = ascii_lower(body);
    }

    Range range = Range::Equals;
    std::string base = name;
    if (base.rfind("min-", 0) == 0) { range = Range::Min; base = base.substr(4); }
    else if (base.rfind("max-", 0) == 0) { range = Range::Max; base = base.substr(4); }

    const bool boolean_form = value.empty();

    if (base == "width" || base == "height") {
        double actual = base == "width" ? ctx.viewport_width_px : ctx.viewport_height_px;
        if (boolean_form) return actual != 0;
        double expected = 0;
        if (!feature_value_px(value, ctx, &expected)) return false;
        return compare_range(actual, expected, range);
    }
    if (base == "orientation") {
        if (boolean_form) return true;
        std::string v = ascii_lower(value);
        return (v == "landscape") == (ctx.orientation() == Orientation::Landscape);
    }
    if (base == "aspect-ratio") {
        if (ctx.viewport_height_px == 0) return false;
        double actual = ctx.viewport_width_px / ctx.viewport_height_px;
        if (boolean_form) return true;
        // "16/9" arrives as a list; parse the two numbers directly.
        double num = 0, den = 1;
        if (auto slash = value.find('/'); slash != std::string::npos) {
            if (!feature_value_px(trim(std::string_view(value).substr(0, slash)), ctx, &num)) return false;
            if (!feature_value_px(trim(std::string_view(value).substr(slash + 1)), ctx, &den)) return false;
        } else if (!feature_value_px(value, ctx, &num)) {
            return false;
        }
        if (den == 0) return false;
        return compare_range(actual, num / den, range);
    }
    if (base == "resolution") {
        if (boolean_form) return true;
        // dppx and x are ratios of 96dpi; dpi/dpcm are absolute.
        std::string v = ascii_lower(value);
        double n = std::atof(v.c_str());
        double actual = ctx.dpi_pixels_per_inch;
        double expected = n;
        if (v.find("dppx") != std::string::npos || v.find('x') != std::string::npos) {
            expected = n * 96.0;
        } else if (v.find("dpcm") != std::string::npos) {
            expected = n * 2.54;
        }
        return compare_range(actual, expected, range);
    }
    if (base == "prefers-color-scheme") {
        if (boolean_form) return true;
        return ascii_lower(value) == (ctx.color_scheme == ColorScheme::Dark ? "dark" : "light");
    }
    if (base == "prefers-reduced-motion") {
        if (boolean_form) return ctx.prefers_reduced_motion;
        std::string v = ascii_lower(value);
        return v == "reduce" ? ctx.prefers_reduced_motion : !ctx.prefers_reduced_motion;
    }
    if (base == "hover" || base == "any-hover") {
        bool can_hover = ctx.hover == HoverCapability::Hover;
        if (boolean_form) return can_hover;
        return (ascii_lower(value) == "hover") == can_hover;
    }
    if (base == "pointer" || base == "any-pointer") {
        if (boolean_form) return ctx.pointer != PointerCapability::None;
        std::string v = ascii_lower(value);
        if (v == "none") return ctx.pointer == PointerCapability::None;
        if (v == "coarse") return ctx.pointer == PointerCapability::Coarse;
        if (v == "fine") return ctx.pointer == PointerCapability::Fine;
        return false;
    }
    // An unknown feature is FALSE, not true: an unrecognised condition must
    // hide its block rather than apply unconditionally.
    return false;
}

bool evaluate_condition(std::string_view raw, const MediaContext& ctx);

bool evaluate_media_type_or_condition(std::string_view raw, const MediaContext& ctx) {
    std::string s = trim(raw);
    if (s.empty()) return true;

    std::string inner;
    if (unwrap_parens(s, &inner)) {
        // A parenthesised group may itself be a compound condition.
        std::vector<std::string> parts;
        if (split_on_keyword(inner, "and", &parts) || split_on_keyword(inner, "or", &parts)) {
            return evaluate_condition(inner, ctx);
        }
        return evaluate_feature(inner, ctx);
    }

    std::string lower = ascii_lower(s);
    if (lower == "all") return true;
    if (lower == "screen") return ctx.type == MediaType::Screen;
    if (lower == "print") return ctx.type == MediaType::Print;
    // Unknown media types (tv, speech, ...) do not match.
    if (lower.find('(') == std::string::npos) return false;
    return evaluate_feature(s, ctx);
}

bool evaluate_condition(std::string_view raw, const MediaContext& ctx) {
    std::string s = trim(raw);
    if (s.empty()) return true;

    std::vector<std::string> parts;
    // `or` binds looser than `and`, so split on it first.
    if (split_on_keyword(s, "or", &parts)) {
        for (const auto& p : parts) {
            if (evaluate_condition(p, ctx)) return true;
        }
        return false;
    }
    if (split_on_keyword(s, "and", &parts)) {
        for (const auto& p : parts) {
            if (!evaluate_condition(p, ctx)) return false;
        }
        return true;
    }
    std::string lower = ascii_lower(s);
    if (lower.rfind("not ", 0) == 0) {
        return !evaluate_condition(std::string_view(s).substr(4), ctx);
    }
    if (lower.rfind("only ", 0) == 0) {
        // `only` exists to hide the query from CSS2 UAs; it has no effect here.
        return evaluate_condition(std::string_view(s).substr(5), ctx);
    }
    return evaluate_media_type_or_condition(s, ctx);
}

bool evaluate_supports_condition(std::string_view raw) {
    std::string s = trim(raw);
    if (s.empty()) return false;

    std::vector<std::string> parts;
    if (split_on_keyword(s, "or", &parts)) {
        for (const auto& p : parts) {
            if (evaluate_supports_condition(p)) return true;
        }
        return false;
    }
    if (split_on_keyword(s, "and", &parts)) {
        for (const auto& p : parts) {
            if (!evaluate_supports_condition(p)) return false;
        }
        return true;
    }
    if (ascii_lower(s).rfind("not ", 0) == 0) {
        return !evaluate_supports_condition(std::string_view(s).substr(4));
    }

    std::string inner;
    if (unwrap_parens(s, &inner)) {
        std::vector<std::string> sub;
        if (split_on_keyword(inner, "and", &sub) || split_on_keyword(inner, "or", &sub) ||
            ascii_lower(inner).rfind("not ", 0) == 0) {
            return evaluate_supports_condition(inner);
        }
        auto colon = inner.find(':');
        if (colon == std::string::npos) return false;
        std::string prop = ascii_lower(trim(std::string_view(inner).substr(0, colon)));
        std::string value = trim(std::string_view(inner).substr(colon + 1));
        if (prop.empty() || value.empty()) return false;
        // Supported means: we know the property AND we can parse the value.
        // A custom property is always "supported" per the spec.
        if (CssPropertyRegistry::is_custom_property(prop)) return true;
        if (CssPropertyRegistry::instance().id_of(prop) == kCustomPropertyId) return false;
        CssParseError err;
        return parse_css_value(value, &err) != nullptr;
    }
    return false;
}

} // namespace

bool evaluate_media_query(std::string_view query, const MediaContext& ctx) {
    std::string s = trim(query);
    if (s.empty()) return true;   // `@media { }` applies
    // A comma-separated list is an OR.
    for (const auto& part : split_top_level_commas(s)) {
        if (evaluate_condition(part, ctx)) return true;
    }
    return false;
}

bool evaluate_supports(std::string_view condition) {
    return evaluate_supports_condition(condition);
}

} // namespace weva
