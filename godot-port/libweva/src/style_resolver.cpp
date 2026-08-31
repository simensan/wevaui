#include "weva/style_resolver.h"

#include "weva/css_calc.h"

#include <cctype>
#include <string>
#include <vector>

namespace weva {

namespace {

std::string_view trim(std::string_view s) {
    const auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    size_t b = 0, e = s.size();
    while (b < e && ws(s[b])) ++b;
    while (e > b && ws(s[e - 1])) --e;
    return s.substr(b, e - b);
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

// The identifier of a keyword-or-identifier value, or empty for anything else.
// The C# accepts either because its parser canonicalises known names to
// CssKeyword and leaves the rest as CssIdentifier.
std::string_view identifier_of(const CssValue& v) {
    if (v.kind() == CssValueKind::Keyword) return static_cast<const CssKeyword&>(v).name;
    if (v.kind() == CssValueKind::Identifier) return static_cast<const CssIdentifier&>(v).name;
    return {};
}

bool font_size_keyword(std::string_view raw, double parent_fs, double* px) {
    if (raw.empty() || iequals(raw, "medium")) { *px = parent_fs; return true; }
    if (iequals(raw, "small")) { *px = parent_fs * kFontSizeSmall; return true; }
    if (iequals(raw, "large")) { *px = parent_fs * kFontSizeLarge; return true; }
    if (iequals(raw, "x-small")) { *px = parent_fs * kFontSizeXSmall; return true; }
    if (iequals(raw, "x-large")) { *px = parent_fs * kFontSizeXLarge; return true; }
    if (iequals(raw, "xx-small")) { *px = parent_fs * kFontSizeXXSmall; return true; }
    if (iequals(raw, "xx-large")) { *px = parent_fs * kFontSizeXXLarge; return true; }
    if (iequals(raw, "smaller")) { *px = parent_fs * kFontSizeSmaller; return true; }
    if (iequals(raw, "larger")) { *px = parent_fs * kFontSizeLarger; return true; }
    return false;
}

double border_width_keyword(std::string_view raw) {
    if (iequals(raw, "thin")) return 1;
    if (iequals(raw, "medium")) return 3;
    if (iequals(raw, "thick")) return 5;
    return 0;
}

// Splits on whitespace at paren depth zero, so `calc(1px + 2px) 3px` stays two
// tokens rather than four.
std::vector<std::string_view> split_top_level(std::string_view s) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        const char c = s[i];
        if (c == '(') ++depth;
        else if (c == ')') --depth;
        else if (depth == 0 && (c == ' ' || c == '\t')) {
            if (i > start) out.push_back(s.substr(start, i - start));
            start = i + 1;
        }
    }
    if (start < s.size()) out.push_back(s.substr(start));
    return out;
}

// Resolves the argument of fit-content(...) to pixels.
bool fit_content_argument(const CssValue& arg, const LayoutContext& ctx, double font_size,
                          std::optional<double> basis, double line_height, double* out) {
    switch (arg.kind()) {
        case CssValueKind::Length: {
            const auto& l = static_cast<const CssLength&>(arg);
            if (l.unit == CssLengthUnit::Percent) {
                *out = basis ? l.value * 0.01 * *basis : 0;
                return true;
            }
            return l.to_pixels(ctx.to_length_context(font_size, basis, line_height), out);
        }
        case CssValueKind::Percentage:
            *out = basis ? static_cast<const CssPercentage&>(arg).value * 0.01 * *basis : 0;
            return true;
        case CssValueKind::Number:
            *out = static_cast<const CssNumber&>(arg).value;
            return true;
        case CssValueKind::Calc: {
            std::string why;
            return static_cast<const CssCalc&>(arg).evaluate(
                ctx.to_length_context(font_size, basis, line_height), out, &why);
        }
        default:
            return false;
    }
}

} // namespace

LengthContext LayoutContext::to_length_context(double font_size_px_,
                                               std::optional<double> basis_px,
                                               double line_height_px_) const {
    LengthContext lc;
    lc.base_font_size_px = font_size_px_;
    lc.root_font_size_px = root_font_size_px;
    lc.viewport_width_px = viewport_width_px;
    lc.viewport_height_px = viewport_height_px;
    lc.dpi_pixels_per_inch = dpi_pixels_per_inch;
    lc.has_basis = basis_px.has_value();
    lc.basis_pixels = basis_px.value_or(0);
    lc.line_height_px = line_height_px_;
    lc.root_line_height_px = root_line_height_px;
    return lc;
}

double font_size_px(const ComputedStyle* style, const ComputedStyle* parent_style,
                    const LayoutContext& ctx) {
    // The parent's size is resolved with a NULL grandparent, so it resolves
    // against the root rather than against its own parent. `em` therefore
    // compounds for two levels and no further. This is the C#'s shape, ported
    // as-is; see PORT_PLAN.md.
    const double parent_fs =
        parent_style ? font_size_px(parent_style, nullptr, ctx) : ctx.root_font_size_px;

    const std::string_view raw = get(style, "font-size");
    const double fallback = parent_fs > 0 ? parent_fs : ctx.root_font_size_px;
    if (raw.empty()) return fallback;

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return fallback;

    if (const std::string_view id = identifier_of(*v); !id.empty()) {
        double px = 0;
        if (font_size_keyword(id, parent_fs, &px)) return px;
        return fallback;
    }
    switch (v->kind()) {
        case CssValueKind::Length: {
            double px = 0;
            // Basis is the parent size: a percentage font-size resolves against
            // the parent, not against any containing block.
            if (static_cast<const CssLength&>(*v).to_pixels(
                    ctx.to_length_context(parent_fs, parent_fs), &px)) {
                return px;
            }
            return fallback;
        }
        case CssValueKind::Percentage:
            return parent_fs * static_cast<const CssPercentage&>(*v).value * 0.01;
        case CssValueKind::Number:
            // A unitless font-size is read as pixels. Not valid CSS, but the
            // reference accepts it.
            return static_cast<const CssNumber&>(*v).value;
        case CssValueKind::Calc: {
            // CSS Values L4 §10: a math function resolves to a length when its
            // inputs do. Without this branch `font-size: clamp(12px, 1.5vmin,
            // 14px)` silently falls through to the inherited size.
            double px = 0;
            std::string why;
            if (static_cast<const CssCalc&>(*v).evaluate(
                    ctx.to_length_context(parent_fs, parent_fs), &px, &why)) {
                return px;
            }
            return fallback;
        }
        default:
            return fallback;
    }
}

double line_height_px(const ComputedStyle* style, double font_size, const LayoutContext& ctx,
                      const FontMetrics* metrics) {
    const std::string_view raw = get(style, "line-height");
    // `normal` is a UA-chosen value: the face's own line height when there is a
    // face, and the conventional 1.2 factor when there is not.
    const double fallback =
        metrics ? metrics->line_height(font_size) : font_size * kDefaultLineHeightFactor;
    if (raw.empty()) return fallback;

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return fallback;

    // `normal` — and any other keyword — falls through to the font-derived
    // default rather than resolving.
    if (!identifier_of(*v).empty()) return fallback;

    switch (v->kind()) {
        case CssValueKind::Length: {
            double px = 0;
            if (static_cast<const CssLength&>(*v).to_pixels(
                    ctx.to_length_context(font_size, font_size), &px)) {
                return px;
            }
            return fallback;
        }
        case CssValueKind::Percentage:
            return font_size * static_cast<const CssPercentage&>(*v).value * 0.01;
        case CssValueKind::Number:
            // A unitless line-height is a MULTIPLIER, unlike font-size where the
            // same syntax means pixels.
            return font_size * static_cast<const CssNumber&>(*v).value;
        case CssValueKind::Calc: {
            double px = 0;
            std::string why;
            if (static_cast<const CssCalc&>(*v).evaluate(
                    ctx.to_length_context(font_size, font_size), &px, &why)) {
                return px;
            }
            return fallback;
        }
        default:
            return fallback;
    }
}

ResolvedLength resolve_length(std::string_view raw, const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px, double line_height) {
    if (raw.empty()) return ResolvedLength::automatic();
    if (raw == "auto") return ResolvedLength::automatic();
    if (raw == "none") return ResolvedLength::none();
    // CSS Sizing L3 §5 intrinsic keywords. Nothing outside shrink-to-fit
    // computes intrinsic sizes yet, so they degrade to auto — checked before
    // parsing to keep the common case off the value-parser path.
    if (raw == "min-content" || raw == "max-content" || raw == "fit-content") {
        return ResolvedLength::automatic();
    }

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return ResolvedLength::invalid();

    if (const std::string_view id = identifier_of(*v); !id.empty()) {
        if (iequals(id, "auto")) return ResolvedLength::automatic();
        if (iequals(id, "none")) return ResolvedLength::none();
        if (iequals(id, "min-content") || iequals(id, "max-content") ||
            iequals(id, "fit-content")) {
            return ResolvedLength::automatic();
        }
        return ResolvedLength::invalid();
    }

    switch (v->kind()) {
        case CssValueKind::Length: {
            const auto& l = static_cast<const CssLength&>(*v);
            // A percent-typed length needs a basis. Without one it surfaces as
            // Percent so the caller keeps its own fallback rather than
            // silently resolving against zero.
            if (l.unit == CssLengthUnit::Percent) {
                if (basis_px) return ResolvedLength::pixel(l.value * 0.01 * *basis_px);
                return ResolvedLength::percent_of(l.value);
            }
            double px = 0;
            if (!l.to_pixels(ctx.to_length_context(font_size, basis_px, line_height), &px)) {
                return ResolvedLength::invalid();
            }
            return ResolvedLength::pixel(px);
        }
        case CssValueKind::Percentage: {
            const double pct = static_cast<const CssPercentage&>(*v).value;
            if (basis_px) return ResolvedLength::pixel(pct * 0.01 * *basis_px);
            return ResolvedLength::percent_of(pct);
        }
        case CssValueKind::Number:
            return ResolvedLength::pixel(static_cast<const CssNumber&>(*v).value);
        case CssValueKind::Calc: {
            double px = 0;
            std::string why;
            if (!static_cast<const CssCalc&>(*v).evaluate(
                    ctx.to_length_context(font_size, basis_px, line_height), &px, &why)) {
                return ResolvedLength::invalid();
            }
            return ResolvedLength::pixel(px);
        }
        case CssValueKind::FunctionCall: {
            // CSS Sizing L3 §5.1 fit-content(<length-percentage>). The argument
            // resolves to a definite value here; the caller probes min-content
            // and max-content and applies the clamp.
            const auto& fn = static_cast<const CssFunctionCall&>(*v);
            if (fn.name != "fit-content" || fn.arguments.size() != 1) {
                return ResolvedLength::invalid();
            }
            double arg_px = 0;
            if (!fit_content_argument(*fn.arguments[0], ctx, font_size, basis_px, line_height,
                                      &arg_px)) {
                return ResolvedLength::automatic();
            }
            if (arg_px < 0) arg_px = 0;
            return ResolvedLength::fit_content_arg(arg_px);
        }
        default:
            return ResolvedLength::invalid();
    }
}

double resolve_length_px(std::string_view raw, double fallback, const LayoutContext& ctx,
                         double font_size, std::optional<double> basis_px) {
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis_px);
    // Auto, none and an unresolved percentage all mean "the caller decides".
    return r.kind == LengthKind::Length ? r.pixels : fallback;
}

double resolve_border_width(std::string_view raw, double font_size, const LayoutContext& ctx) {
    if (raw.empty()) return 0;
    if (raw == "thin") return 1;
    if (raw == "medium") return 3;
    if (raw == "thick") return 5;

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return 0;
    if (const std::string_view id = identifier_of(*v); !id.empty()) {
        return border_width_keyword(id);
    }
    switch (v->kind()) {
        case CssValueKind::Length: {
            double px = 0;
            // Basis 0: a percentage border-width is not valid CSS, and
            // resolving it against zero matches the reference.
            if (static_cast<const CssLength&>(*v).to_pixels(
                    ctx.to_length_context(font_size, 0.0), &px)) {
                return px;
            }
            return 0;
        }
        case CssValueKind::Number:
            return static_cast<const CssNumber&>(*v).value;
        case CssValueKind::Calc: {
            double px = 0;
            std::string why;
            if (static_cast<const CssCalc&>(*v).evaluate(ctx.to_length_context(font_size, 0.0),
                                                         &px, &why)) {
                return px;
            }
            return 0;
        }
        default:
            return 0;
    }
}

BoxSideValues box_sides(const ComputedStyle* style, std::string_view shorthand) {
    const std::string sh_name(shorthand);
    BoxSideValues r;
    r.top = get(style, sh_name + "-top");
    r.right = get(style, sh_name + "-right");
    r.bottom = get(style, sh_name + "-bottom");
    r.left = get(style, sh_name + "-left");

    // The shorthand is a fallback, not an override: a longhand that is set to
    // anything but its initial value wins outright. Shorthand expansion is not
    // done at cascade time, which is why this exists at all.
    const auto is_initial = [](std::string_view v) { return v.empty() || v == "0"; };
    if (is_initial(r.top) && is_initial(r.right) && is_initial(r.bottom) && is_initial(r.left)) {
        const std::string_view sh = get(style, shorthand);
        if (!sh.empty() && sh != "0") {
            const std::vector<std::string_view> parts = split_top_level(sh);
            switch (parts.size()) {
                case 1: return {parts[0], parts[0], parts[0], parts[0]};
                case 2: return {parts[0], parts[1], parts[0], parts[1]};
                case 3: return {parts[0], parts[1], parts[2], parts[1]};
                case 4: return {parts[0], parts[1], parts[2], parts[3]};
                default: break;
            }
        }
    }
    const auto or_zero = [](std::string_view v) { return v.empty() ? std::string_view("0") : v; };
    return {or_zero(r.top), or_zero(r.right), or_zero(r.bottom), or_zero(r.left)};
}

bool try_resolve_aspect_ratio(const ComputedStyle* style, double* ratio) {
    *ratio = 0;
    std::string_view raw = trim(get(style, "aspect-ratio"));
    if (raw.empty() || iequals(raw, "auto")) return false;

    // `auto <ratio>` and `<ratio> auto`: the explicit ratio takes precedence,
    // so the keyword is simply stripped.
    if (raw.size() > 5 && iequals(raw.substr(0, 5), "auto ")) raw = trim(raw.substr(5));
    else if (raw.size() > 5 && iequals(raw.substr(raw.size() - 5), " auto")) {
        raw = trim(raw.substr(0, raw.size() - 5));
    }
    if (raw.empty()) return false;

    // <number> or <number> / <number>.
    const size_t slash = raw.find('/');
    if (slash == std::string_view::npos) {
        double n = 0;
        if (!css_parse_double(trim(raw), &n) || n <= 0) return false;
        *ratio = n;
        return true;
    }
    double num = 0, den = 0;
    if (!css_parse_double(trim(raw.substr(0, slash)), &num)) return false;
    if (!css_parse_double(trim(raw.substr(slash + 1)), &den)) return false;
    if (den == 0) return false;
    const double v = num / den;
    if (!(v > 0)) return false;
    *ratio = v;
    return true;
}

bool is_rtl(const ComputedStyle* style) {
    return iequals(trim(get(style, "direction")), "rtl");
}

} // namespace weva
