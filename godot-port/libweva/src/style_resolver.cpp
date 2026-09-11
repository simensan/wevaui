#include "weva/css_properties.h"
#include "weva/style_resolver.h"

#include <deque>

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

// The same lookup by id. A property id is resolved once for the
// program below rather than hashed from its name on every call --
// sampling put ComputedStyle::get and CssPropertyRegistry::id_of
// together at a quarter of a layout pass, ahead of any layout
// algorithm. Safe because the registry keeps an id stable across
// re-registration, which is what its header promises it for.
std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

// Resolved at static-init. The registry is a function-local static,
// so it is constructed on first use and these cannot outrun it.
const int kId_aspect_ratio = CssPropertyRegistry::instance().id_of("aspect-ratio");
const int kId_direction = CssPropertyRegistry::instance().id_of("direction");
const int kId_line_height = CssPropertyRegistry::instance().id_of("line-height");


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
    if (style && style->font_size_memo_absolute &&
        style->font_size_memo_version == style->version()) {
        return style->font_size_memo_px;
    }
    // The inheritance chain follows DOM elements, even when display:contents
    // or anonymous boxes change the box ancestry. The explicit parent is a
    // fallback for callers that construct styles without a linked cascade.
    const ComputedStyle* parent = style && style->inherit_parent()
        ? style->inherit_parent() : parent_style != style ? parent_style : nullptr;
    static const int kFontSize = CssPropertyRegistry::instance().id_of("font-size");
    // An inherited size is exactly the parent's result. Forward the read
    // without maintaining a redundant memo at every undeclared ancestor.
    if (!style || !style->contains(kFontSize) || style->font_size_inherited())
        return parent ? font_size_px(parent, parent->inherit_parent(), ctx) : ctx.root_font_size_px;
    const double parent_fs =
        parent ? font_size_px(parent, parent->inherit_parent(), ctx) : ctx.root_font_size_px;

    // Context inputs join the style version and resolved parent size. A fixed
    // pixel-sized parent does not change when the viewport, root metrics or
    // DPI change, while this style's vw/rem/rlh/pt (including calc) can.
    // Compare before reading the declaration, preserving the inexpensive
    // memo hit for repeated layout probes.
    const std::array<double, 5> context_key = {ctx.viewport_width_px, ctx.viewport_height_px,
        ctx.root_font_size_px, ctx.root_line_height_px, ctx.dpi_pixels_per_inch};
    if (style && style->font_size_memo_version == style->version() &&
        style->font_size_memo_parent == parent_fs &&
        style->font_size_memo_context == context_key) {
        return style->font_size_memo_px;
    }

    // Everything below derives the answer; this records it on the way out.
    // A lambda rather than a write before each of the seven returns, so a
    // later branch cannot forget.
    bool absolute = false;
    const auto remember = [&](double px) {
        if (style) {
            style->font_size_memo_parent = parent_fs;
            style->font_size_memo_context = context_key;
            style->font_size_memo_px = px;
            style->font_size_memo_version = style->version();
            style->font_size_memo_absolute = absolute;
        }
        return px;
    };

    // The id, resolved once for the whole program.
    //
    // `get(name)` and `parsed(name)` each hash the name, so this hashed
    // "font-size" twice per call -- and it is called several times for every
    // box, recursively for the parent as well. The registry keeps an id stable
    // across re-registration precisely so a cache like this is safe.
    const std::string_view raw = style->get(kFontSize);
    const double fallback = parent_fs > 0 ? parent_fs : ctx.root_font_size_px;
    if (raw.empty()) return remember(fallback);

    // Through the style's parsed cache: font_size_px is called for every box
    // several times over, and re-parsing its declaration was the single largest
    // remaining source of per-frame allocation.
    const CssValue* v = style->parsed(kFontSize);
    if (!v) return remember(fallback);

    if (const std::string_view id = identifier_of(*v); !id.empty()) {
        double px = 0;
        if (font_size_keyword(id, parent_fs, &px)) return remember(px);
        return remember(fallback);
    }
    switch (v->kind()) {
        case CssValueKind::Length: {
            double px = 0;
            absolute = style && style->contains(kFontSize) &&
                       static_cast<const CssLength&>(*v).unit == CssLengthUnit::Px;
            // Basis is the parent size: a percentage font-size resolves against
            // the parent, not against any containing block.
            if (static_cast<const CssLength&>(*v).to_pixels(
                    ctx.to_length_context(parent_fs, parent_fs), &px)) {
                return remember(px);
            }
            return remember(fallback);
        }
        case CssValueKind::Percentage:
            return remember(parent_fs * static_cast<const CssPercentage&>(*v).value * 0.01);
        case CssValueKind::Number:
            // A unitless font-size is read as pixels. Not valid CSS, but the
            // reference accepts it.
            absolute = style && style->contains(kFontSize);
            return remember(static_cast<const CssNumber&>(*v).value);
        case CssValueKind::Calc: {
            // CSS Values L4 §10: a math function resolves to a length when its
            // inputs do. Without this branch `font-size: clamp(12px, 1.5vmin,
            // 14px)` silently falls through to the inherited size.
            double px = 0;
            std::string why;
            if (static_cast<const CssCalc&>(*v).evaluate(
                    ctx.to_length_context(parent_fs, parent_fs), &px, &why)) {
                return remember(px);
            }
            return remember(fallback);
        }
        default:
            return remember(fallback);
    }
}

double line_height_px(const ComputedStyle* style, double font_size, const LayoutContext& ctx,
                      const FontMetrics* metrics) {
    // `normal` is a UA-chosen value: the face's own line height when there is a
    // face, and the conventional 1.2 factor when there is not.
    const double fallback =
        metrics ? metrics->line_height(font_size) : font_size * kDefaultLineHeightFactor;
    const ComputedStyle* source = style;
    while (source && (!source->contains(kId_line_height) || source->line_height_inherited()))
        source = source->inherit_parent();
    const CssValue* v = source ? source->parsed(kId_line_height) : nullptr;
    if (!v) return fallback;

    // `normal` — and any other keyword — falls through to the font-derived
    // default rather than resolving.
    if (!identifier_of(*v).empty()) return fallback;

    // A number inherits as a multiplier; lengths/percentages inherit the
    // computed length of the element that declared them (CSS 2.2 §10.8.1).
    // A calc() whose type is Number follows the same rule as a bare number.
    if (v->kind() == CssValueKind::Number)
        return font_size * static_cast<const CssNumber&>(*v).value;
    const CssCalc* calc = v->kind() == CssValueKind::Calc ? static_cast<const CssCalc*>(v) : nullptr;
    if (calc && calc->expression && calc_classify(*calc->expression) == CalcType::Number) {
        double multiplier = 0;
        if (calc->evaluate(ctx.to_length_context(font_size, font_size), &multiplier, nullptr))
            return font_size * std::max(0.0, multiplier);
        return fallback;
    }
    const double source_fs = source && source != style
        ? font_size_px(source, source->inherit_parent(), ctx) : font_size;

    switch (v->kind()) {
        case CssValueKind::Length: {
            double px = 0;
            if (static_cast<const CssLength&>(*v).to_pixels(
                    ctx.to_length_context(source_fs, source_fs), &px)) {
                return px;
            }
            return fallback;
        }
        case CssValueKind::Percentage:
            return source_fs * static_cast<const CssPercentage&>(*v).value * 0.01;
        case CssValueKind::Calc: {
            double px = 0;
            std::string why;
            if (static_cast<const CssCalc&>(*v).evaluate(
                    ctx.to_length_context(source_fs, source_fs), &px, &why)) {
                return std::max(0.0, px);
            }
            return fallback;
        }
        default:
            return fallback;
    }
}

namespace {

// The keyword fast path, shared by both entry points. It is checked before any
// parsed value is consulted because `auto` is by far the commonest declaration
// in a document and there is nothing to gain from looking at its parse.
//
// Returns true when `raw` alone settles the answer.
bool resolve_length_keyword(std::string_view raw, ResolvedLength* out) {
    if (raw.empty() || raw == "auto") { *out = ResolvedLength::automatic(); return true; }
    if (raw == "none") { *out = ResolvedLength::none(); return true; }
    // CSS Sizing L3 §5 intrinsic keywords resolve as `auto` here; block
    // layout reads the keyword itself and routes the box through the
    // shrink-to-fit probes (BlockLayout::has_intrinsic_width_keyword), and
    // everywhere else `auto` is the right degrade.
    if (raw == "min-content" || raw == "max-content" || raw == "fit-content") {
        *out = ResolvedLength::automatic();
        return true;
    }
    return false;
}

} // namespace

// The resolve half, over an ALREADY-PARSED value. Split out so a caller with a
// cached parse never re-parses: re-parsing here was 97% of a layout pass's heap
// allocations (see tools/weva_bench).
ResolvedLength resolve_length_value(const CssValue* value, const LayoutContext& ctx,
                                    double font_size, std::optional<double> basis_px,
                                    double line_height) {
    if (!value) return ResolvedLength::invalid();
    const CssValue* v = value;

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

ResolvedLength resolve_length(std::string_view raw, const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px, double line_height) {
    // A bare zero, without going through the parser.
    //
    // This overload takes the value as CHARACTERS, so it has no parsed-value
    // memo to read and calls parse_css_value -- which allocates a CssValue --
    // every single time. `0` is the initial value of text-indent and
    // letter-spacing and word-spacing, and what box_sides substitutes for an
    // absent edge, so the overwhelmingly common input is exactly the one that
    // needs no parsing at all. Sampling put this line in the inline layout's
    // per-container path on vendor.
    //
    // `0` and `0px` only, and NOT the empty string: an empty value falls
    // through parse_css_value to `auto`, which is a different answer from zero
    // in every caller that distinguishes them. `0%` is a Percent to its
    // callers rather than a Length, and that difference decides how the value
    // resolves against its basis.
    if (raw == "0" || raw == "0px") return ResolvedLength::pixel(0);
    ResolvedLength keyword;
    if (resolve_length_keyword(raw, &keyword)) return keyword;
    CssParseError err;
    const CssValuePtr v = parse_css_value(raw, &err);
    return resolve_length_value(v.get(), ctx, font_size, basis_px, line_height);
}

// One component of a shorthand that has already been parsed and memoised.
//
// `margin: 10px 20px` parses to a two-item list; `padding: 8px` to a single
// value that every side shares. Either way the parse happens once per style
// rather than once per side per layout pass.
const CssValue* shorthand_component(const ComputedStyle* style, int shorthand_id, int part) {
    if (!style || shorthand_id == kCustomPropertyId || part < 0) return nullptr;
    const CssValue* v = style->parsed(shorthand_id);
    if (!v) return nullptr;
    if (v->kind() != CssValueKind::List) return part == 0 ? v : nullptr;
    const auto& list = static_cast<const CssValueList&>(*v);
    if (static_cast<size_t>(part) >= list.items.size()) return nullptr;
    return list.items[static_cast<size_t>(part)].get();
}

ResolvedLength resolve_length_cached(const ComputedStyle* style, int property_id,
                                     std::string_view raw, const LayoutContext& ctx,
                                     double font_size, std::optional<double> basis_px,
                                     double line_height) {
    ResolvedLength keyword;
    if (resolve_length_keyword(raw, &keyword)) return keyword;
    if (!style || property_id == kCustomPropertyId) {
        CssParseError err;
        const CssValuePtr v = parse_css_value(raw, &err);
        return resolve_length_value(v.get(), ctx, font_size, basis_px, line_height);
    }
    return resolve_length_value(style->parsed(property_id), ctx, font_size, basis_px,
                                line_height);
}

ResolvedLength resolve_length(const ComputedStyle* style, int property_id,
                              const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px, double line_height) {
    if (!style || property_id == kCustomPropertyId) return ResolvedLength::automatic();
    const std::string_view raw = style->get(property_id);
    ResolvedLength keyword;
    if (resolve_length_keyword(raw, &keyword)) return keyword;
    return resolve_length_value(style->parsed(property_id), ctx, font_size, basis_px,
                                line_height);
}

ResolvedLength resolve_length(const ComputedStyle* style, std::string_view property,
                              const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px, double line_height) {
    if (!style) return ResolvedLength::automatic();
    // The name is hashed ONCE and the id used for both reads.
    //
    // get(name) and parsed(name) each resolve the name themselves, so every
    // length in the document hashed its property twice -- and a layout pass
    // resolves a length for every width, height, margin, padding and gap on
    // every box. Sampling put ComputedStyle::get and CssPropertyRegistry::id_of
    // together at better than a third of a pass on randhtml, ahead of any
    // layout algorithm.
    const int id = CssPropertyRegistry::instance().id_of(property);
    if (id == kCustomPropertyId) {
        // A custom property has no slot; it resolves through the name.
        const std::string_view raw = style->get(property);
        ResolvedLength keyword;
        if (resolve_length_keyword(raw, &keyword)) return keyword;
        return resolve_length_value(style->parsed(property), ctx, font_size, basis_px,
                                    line_height);
    }
    const std::string_view raw = style->get(id);
    ResolvedLength keyword;
    if (resolve_length_keyword(raw, &keyword)) return keyword;
    return resolve_length_value(style->parsed(id), ctx, font_size, basis_px, line_height);
}

double resolve_length_px(std::string_view raw, double fallback, const LayoutContext& ctx,
                         double font_size, std::optional<double> basis_px) {
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis_px);
    // Auto, none and an unresolved percentage all mean "the caller decides".
    return r.kind == LengthKind::Length ? r.pixels : fallback;
}

namespace {

double border_width_value(const CssValue* v, double font_size, const LayoutContext& ctx) {
    if (!v) return 0;
    if (const std::string_view id = identifier_of(*v); !id.empty()) {
        return border_width_keyword(id);
    }
    switch (v->kind()) {
        case CssValueKind::Length: {
            const auto& length = static_cast<const CssLength&>(*v);
            if (length.unit == CssLengthUnit::Px) return length.value;
            double px = 0;
            // Percentage border widths are invalid CSS. The existing resolver
            // uses a zero basis; retain that contract here.
            return length.to_pixels(ctx.to_length_context(font_size, 0.0), &px) ? px : 0;
        }
        case CssValueKind::Number:
            return static_cast<const CssNumber&>(*v).value;
        case CssValueKind::Calc: {
            double px = 0;
            std::string why;
            return static_cast<const CssCalc&>(*v).evaluate(
                ctx.to_length_context(font_size, 0.0), &px, &why) ? px : 0;
        }
        default:
            return 0;
    }
}

} // namespace

double resolve_border_width(const ComputedStyle* style, int property_id,
                            double font_size, const LayoutContext& ctx) {
    const std::string_view raw = style ? style->get(property_id) : std::string_view();
    if (raw.empty()) return 0;
    if (raw == "thin") return 1;
    if (raw == "medium") return 3;
    if (raw == "thick") return 5;
    // Only owned declarations are invalidated by this style's writes. A
    // registry initial value can change without advancing its style version.
    if (!style->contains(property_id)) return resolve_border_width(raw, font_size, ctx);
    return border_width_value(style->parsed(property_id), font_size, ctx);
}

double resolve_border_width(std::string_view raw, double font_size, const LayoutContext& ctx) {
    if (raw.empty()) return 0;
    if (raw == "thin") return 1;
    if (raw == "medium") return 3;
    if (raw == "thick") return 5;

    // `1px` and `0` are nearly every border in a real stylesheet, and this runs
    // for every bordered box on every layout pass. Building a CssValue for them
    // was 828 of vendor.html's heap allocations a pass, so the plain
    // `<number>px` and bare-zero forms are read straight off the string. Any
    // other unit, a calc() or a var() falls through to the parser below.
    {
        std::string_view v = raw;
        while (!v.empty() && (v.front() == ' ' || v.front() == '	')) v.remove_prefix(1);
        while (!v.empty() && (v.back() == ' ' || v.back() == '	')) v.remove_suffix(1);
        std::string_view digits = v;
        bool px = false;
        if (digits.size() > 2 && (digits.substr(digits.size() - 2) == "px" ||
                                  digits.substr(digits.size() - 2) == "PX")) {
            digits.remove_suffix(2);
            px = true;
        }
        if (!digits.empty() && digits.size() < 32) {
            bool plain = true;
            for (const char c : digits) {
                if (!((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+')) {
                    plain = false;
                    break;
                }
            }
            if (plain) {
                double n = 0;
                // A bare number is only a width when it is zero; `border-width:
                // 2` is invalid CSS, and the parser below decides what it means.
                if (css_parse_double(digits, &n) && (px || n == 0)) return n;
            }
        }
    }

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    return border_width_value(v.get(), font_size, ctx);
}

// The four longhand ids for a shorthand, resolved once per shorthand rather
// than per call. Building the names here cost four std::string concatenations
// and four registry lookups on every box for margin AND padding — the same trap
// the logical-property tables already exist to avoid.
const BoxSideValues& box_side_ids(int shorthand_id) {
    // Keyed by the shorthand's OWN id, so the lookup is an index rather than a
    // walk comparing strings. It was a deque scanned linearly and compared by
    // name, run twice per box per pass -- and the name it compared had to be
    // hashed to get here in the first place.
    //
    // A deque, not a vector: the returned reference has to survive a later
    // insertion, and a vector would move its elements out from under one.
    struct Entry { int id; BoxSideValues ids; };
    static std::deque<Entry> cache;
    for (const Entry& e : cache) {
        if (e.id == shorthand_id) return e.ids;
    }
    auto& reg = CssPropertyRegistry::instance();
    const std::string sh(reg.name_of(shorthand_id));
    BoxSideValues ids;
    ids.top_id = reg.id_of(sh + "-top");
    ids.right_id = reg.id_of(sh + "-right");
    ids.bottom_id = reg.id_of(sh + "-bottom");
    ids.left_id = reg.id_of(sh + "-left");
    cache.push_back({shorthand_id, ids});
    return cache.back().ids;
}

const BoxSideValues& box_side_ids(std::string_view shorthand) {
    return box_side_ids(CssPropertyRegistry::instance().id_of(shorthand));
}

BoxSideValues box_sides(const ComputedStyle* style, std::string_view shorthand) {
    return box_sides(style, CssPropertyRegistry::instance().id_of(shorthand));
}

BoxSideValues box_sides(const ComputedStyle* style, int shorthand_id) {
    BoxSideValues r = box_side_ids(shorthand_id);
    r.top = style ? style->get(r.top_id) : std::string_view();
    r.right = style ? style->get(r.right_id) : std::string_view();
    r.bottom = style ? style->get(r.bottom_id) : std::string_view();
    r.left = style ? style->get(r.left_id) : std::string_view();

    // The shorthand is a fallback, not an override: a longhand that is set to
    // anything but its initial value wins outright. Shorthand expansion is not
    // done at cascade time, which is why this exists at all.
    const auto is_initial = [](std::string_view v) { return v.empty() || v == "0"; };
    if (is_initial(r.top) && is_initial(r.right) && is_initial(r.bottom) && is_initial(r.left)) {
        const std::string_view sh = style ? style->get(shorthand_id) : std::string_view();
        if (!sh.empty() && sh != "0") {
            const std::vector<std::string_view> parts = split_top_level(sh);
            // The strings still come back, because callers that want the raw
            // text (auto-margin centring) read them. But the shorthand's own
            // slot and each side's COMPONENT INDEX come too, so a caller that
            // wants a number can take it from the memoised parse instead of
            // parsing the substring again on every pass.
            BoxSideValues from_shorthand = r;
            from_shorthand.shorthand_id = shorthand_id;
            const auto fill = [&](int t, int rr, int b, int l) {
                from_shorthand.top = parts[static_cast<size_t>(t)];
                from_shorthand.right = parts[static_cast<size_t>(rr)];
                from_shorthand.bottom = parts[static_cast<size_t>(b)];
                from_shorthand.left = parts[static_cast<size_t>(l)];
                from_shorthand.top_part = t;
                from_shorthand.right_part = rr;
                from_shorthand.bottom_part = b;
                from_shorthand.left_part = l;
                // The longhand ids no longer describe these values.
                from_shorthand.top_id = kCustomPropertyId;
                from_shorthand.right_id = kCustomPropertyId;
                from_shorthand.bottom_id = kCustomPropertyId;
                from_shorthand.left_id = kCustomPropertyId;
                return from_shorthand;
            };
            switch (parts.size()) {
                case 1: return fill(0, 0, 0, 0);
                case 2: return fill(0, 1, 0, 1);
                case 3: return fill(0, 1, 2, 1);
                case 4: return fill(0, 1, 2, 3);
                default: break;
            }
        }
    }
    // Filled in place rather than returning a fresh aggregate: brace-init here
    // listed only the four strings and silently left the ids at their defaults,
    // so every caller took the uncached parse path and the memo did nothing for
    // margin or padding.
    const auto or_zero = [](std::string_view v) { return v.empty() ? std::string_view("0") : v; };
    r.top = or_zero(r.top);
    r.right = or_zero(r.right);
    r.bottom = or_zero(r.bottom);
    r.left = or_zero(r.left);
    return r;
}

bool try_resolve_aspect_ratio(const ComputedStyle* style, double* ratio) {
    *ratio = 0;
    std::string_view raw = trim(get(style, kId_aspect_ratio));
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
    return iequals(trim(get(style, kId_direction)), "rtl");
}

} // namespace weva

namespace weva {

namespace {

std::string normalize_family(std::string_view s) {
    // Trim, strip one pair of quotes, lowercase — the reference's
    // NormalizeFamily + StripFamilyQuotes.
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    if (s.size() >= 2 && (s.front() == '"' || s.front() == '\'') && s.back() == s.front()) {
        s = s.substr(1, s.size() - 2);
    }
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

} // namespace

void LayoutContext::register_font(std::string_view family, const FontMetrics* metrics) {
    if (family.empty() || !metrics) return;
    const std::string key = normalize_family(family);
    for (auto& f : fonts) {
        if (f.first == key) { f.second = metrics; return; }
    }
    fonts.emplace_back(key, metrics);
}

const FontMetrics* LayoutContext::font_for(std::string_view stack) const {
    if (fonts.empty() || stack.empty()) return nullptr;
    size_t from = 0;
    while (from <= stack.size()) {
        const size_t comma = stack.find(',', from);
        const std::string_view head =
            stack.substr(from, comma == std::string_view::npos ? std::string_view::npos : comma - from);
        const std::string key = normalize_family(head);
        if (!key.empty()) {
            for (const auto& f : fonts) {
                if (f.first == key) return f.second;
            }
        }
        if (comma == std::string_view::npos) break;
        from = comma + 1;
    }
    return nullptr;
}

} // namespace weva

namespace weva {

int resolve_font_weight(const ComputedStyle* style) {
    if (!style) return 400;
    std::string_view v = style->get("font-weight");
    while (!v.empty() && v.front() == ' ') v.remove_prefix(1);
    while (!v.empty() && v.back() == ' ') v.remove_suffix(1);
    if (v.empty() || v == "normal") return 400;
    if (v == "bold" || v == "bolder") return 700;
    if (v == "lighter") return 300;
    int n = 0;
    for (char c : v) {
        if (c < '0' || c > '9') return 400;
        n = n * 10 + (c - '0');
    }
    return n >= 1 && n <= 1000 ? n : 400;
}

bool resolve_font_italic(const ComputedStyle* style) {
    if (!style) return false;
    std::string_view v = style->get("font-style");
    while (!v.empty() && v.front() == ' ') v.remove_prefix(1);
    return v.size() >= 6 && (v.substr(0, 6) == "italic" || v.substr(0, 7) == "oblique");
}

} // namespace weva
