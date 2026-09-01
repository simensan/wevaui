#pragma once
#include "weva/computed_style.h"
#include "weva/css_value.h"
#include "weva/font_metrics.h"

#include <optional>
#include <string_view>

// Ports the length- and font-resolution half of Runtime/Layout/StyleResolver.cs
// — the layer between the cascade's strings and layout's pixels.

namespace weva {

// Document-scoped inputs every relative length resolves against. The C#
// LayoutContext also carries the anchor registry, font-metrics cache and the
// incremental-layout boundary; those belong to their own slices.
struct LayoutContext {
    double viewport_width_px = 1920;
    double viewport_height_px = 1080;
    double root_font_size_px = 16;
    // The document root's resolved line-height, for `rlh` lengths. Zero means
    // unset, in which case CssLength falls back to root_font_size * 1.2.
    double root_line_height_px = 0;
    double dpi_pixels_per_inch = 96;

    LengthContext to_length_context(double font_size_px,
                                    std::optional<double> basis_px = std::nullopt,
                                    double line_height_px = 0) const;
};

// CSS Values L4 §6.2: `normal` line-height is UA-chosen, conventionally 1.2×
// the font size. One constant so every fallback agrees — missing font metrics,
// paint-time centring, ellipsis row height.
constexpr double kDefaultLineHeightFactor = 1.2;

// CSS Fonts L3 §3.5 absolute-size keywords, and §3.7 relative-size keywords.
// The two tables are kept separate even where the numbers coincide: the
// coincidence is not spec-guaranteed, and merging them would silently drift
// one when the other is tuned.
constexpr double kFontSizeXXSmall = 0.6;
constexpr double kFontSizeXSmall = 0.75;
constexpr double kFontSizeSmall = 0.85;
constexpr double kFontSizeMedium = 1.0;
constexpr double kFontSizeLarge = 1.2;
constexpr double kFontSizeXLarge = 1.5;
constexpr double kFontSizeXXLarge = 2.0;
constexpr double kFontSizeSmaller = 0.85;
constexpr double kFontSizeLarger = 1.2;

enum class LengthKind {
    Length,
    Auto,
    Percent,
    None,
    // CSS Sizing L3 §5.1 fit-content(<length-percentage>). `pixels` carries the
    // resolved argument; the caller probes min-content and max-content and
    // clamps: used = min(max-content, max(min-content, pixels)).
    FitContent,
};

struct ResolvedLength {
    LengthKind kind = LengthKind::Auto;
    double pixels = 0;
    double percent = 0;

    static ResolvedLength automatic() { return {LengthKind::Auto, 0, 0}; }
    static ResolvedLength none() { return {LengthKind::None, 0, 0}; }
    static ResolvedLength pixel(double px) { return {LengthKind::Length, px, 0}; }
    static ResolvedLength percent_of(double pct) { return {LengthKind::Percent, 0, pct}; }
    static ResolvedLength fit_content_arg(double px) { return {LengthKind::FitContent, px, 0}; }
    // Unparseable input collapses to auto. Every caller treated a dedicated
    // Invalid kind as auto anyway, so it carried no signal; the named factory
    // keeps "could not parse" readable at the call sites.
    static ResolvedLength invalid() { return automatic(); }
};

// Resolves an element's used font-size.
//
// NOTE the shape of this, because it is load-bearing and surprising: the
// parent's own font-size is resolved with a NULL grandparent, i.e. against the
// root. So `em` compounds correctly for two levels and stops. Ported as the C#
// has it — see PORT_PLAN.md, where it is flagged for oracle confirmation.
double font_size_px(const ComputedStyle* style, const ComputedStyle* parent_style,
                    const LayoutContext& ctx);

// `normal` resolves through the FONT's own metrics when a backend is supplied,
// and falls back to kDefaultLineHeightFactor x font_size when it is not. That
// distinction is load-bearing: a host that registers a face expects its line
// height to follow that face, not a constant.
double line_height_px(const ComputedStyle* style, double font_size, const LayoutContext& ctx,
                      const FontMetrics* metrics = nullptr);

// `basis_px` is the percentage basis. Without one, a percentage surfaces as
// LengthKind::Percent rather than resolving, so the caller can decide.
ResolvedLength resolve_length(std::string_view raw, const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px, double line_height = 0);

// The same, over a value the caller already has parsed. resolve_length's string
// form parses on every call, which tools/weva_bench measured as 97% of a layout
// pass's heap allocations.
ResolvedLength resolve_length_value(const CssValue* value, const LayoutContext& ctx,
                                    double font_size,
                                    std::optional<double> basis_px = std::nullopt,
                                    double line_height = 0);

// The form layout should use: reads the property through the style's parsed
// cache, so a value is parsed once per style rather than once per read.
ResolvedLength resolve_length(const ComputedStyle* style, std::string_view property,
                              const LayoutContext& ctx, double font_size,
                              std::optional<double> basis_px = std::nullopt,
                              double line_height = 0);

// By id, which also skips the registry name lookup. kCustomPropertyId reads as
// "no cached slot" and falls back to parsing `raw`.
ResolvedLength resolve_length_cached(const ComputedStyle* style, int property_id,
                                     std::string_view raw, const LayoutContext& ctx,
                                     double font_size,
                                     std::optional<double> basis_px = std::nullopt,
                                     double line_height = 0);
// Auto, none and an unresolved percentage all yield `fallback`.
double resolve_length_px(std::string_view raw, double fallback, const LayoutContext& ctx,
                         double font_size, std::optional<double> basis_px);

// thin/medium/thick are 1/3/5px. An unparseable value is 0, not the initial
// `medium` — a border that fails to parse should not appear.
double resolve_border_width(std::string_view raw, double font_size, const LayoutContext& ctx);

struct BoxSideValues {
    std::string_view top, right, bottom, left;
    // The longhand ids the four values came from, or kCustomPropertyId when the
    // value came from the shorthand fallback instead. A caller with an id can
    // read the style's PARSED cache rather than re-parsing the string.
    int top_id = kCustomPropertyId, right_id = kCustomPropertyId;
    int bottom_id = kCustomPropertyId, left_id = kCustomPropertyId;
};
// Longhands win; the shorthand is consulted only when all four longhands are
// at their initial value. Returned views borrow the style's storage.
BoxSideValues box_sides(const ComputedStyle* style, std::string_view shorthand);

// CSS Sizing L4 §5. True for `<number>` or `<number> / <number>` with a
// positive result; false for `auto`, empty, and non-positive ratios. v1 ignores
// the `auto <ratio>` form and uses the explicit ratio when both are present.
bool try_resolve_aspect_ratio(const ComputedStyle* style, double* ratio);

bool is_rtl(const ComputedStyle* style);

} // namespace weva
