#pragma once
#include "weva/css_token.h"

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Values/{CssValue,CssLength,CssAngle,CssColor,LengthContext}.cs
// and the ParseTopLevel/ParseSingle path of CssValueParser.cs.
//
// Deliberately NOT ported: the parse cache, CssValuePool, CssValueStableCopy
// and the negative-result cache. All four exist to make a GC-allocated value
// tree cheap to re-derive, and CssValueStableCopy exists specifically because
// pool-rented leaves would otherwise be mutated under the process-lifetime
// cache. With arena allocation the whole cluster collapses: values are bump-
// allocated per pass and dropped wholesale. That is one of the concrete places
// the port pays for itself (README, "the port is the fix").
//
// Deferred to the next slice: named colors (a 148-entry table), calc()
// evaluation, and rgb()/hsl()/color-mix() argument interpretation. Functions
// round-trip as CssFunctionCall with their arguments parsed, so nothing is
// lost from the token stream.

namespace weva {

enum class CssValueKind {
    Keyword, Number, Length, Percentage, Color, String, FunctionCall,
    VariableReference, Calc, List, Url, Identifier, Angle, Ratio,
};

enum class CssLengthUnit {
    Px, Em, Rem, Percent, Vh, Vw, Vmin, Vmax, Pt, Pc, In, Cm, Mm,
    Ch, Ex, Cap, Ic, Lh, Rlh, Svw, Lvw, Dvw, Svh, Lvh, Dvh,
};

enum class CssAngleUnit { Deg, Rad, Grad, Turn };

// Everything a relative length needs to resolve to pixels.
struct LengthContext {
    double base_font_size_px = 16;
    double root_font_size_px = 16;
    double viewport_width_px = 1920;
    double viewport_height_px = 1080;
    double dpi_pixels_per_inch = 96;
    bool has_basis = false;          // C# uses double? BasisPixels
    double basis_pixels = 0;
    double line_height_px = 0;
    double root_line_height_px = 0;

    LengthContext with_basis(double px) const {
        LengthContext c = *this;
        c.has_basis = true;
        c.basis_pixels = px;
        return c;
    }
};

struct CssValue {
    virtual ~CssValue() = default;
    virtual CssValueKind kind() const = 0;
    std::string raw;
};

using CssValuePtr = std::unique_ptr<CssValue>;

struct CssKeyword : CssValue {
    CssValueKind kind() const override { return CssValueKind::Keyword; }
    std::string name;   // as authored; compare case-insensitively
};

struct CssIdentifier : CssValue {
    CssValueKind kind() const override { return CssValueKind::Identifier; }
    std::string name;
};

struct CssNumber : CssValue {
    CssValueKind kind() const override { return CssValueKind::Number; }
    double value = 0;
};

struct CssPercentage : CssValue {
    CssValueKind kind() const override { return CssValueKind::Percentage; }
    double value = 0;
};

struct CssLength : CssValue {
    CssValueKind kind() const override { return CssValueKind::Length; }
    double value = 0;
    CssLengthUnit unit = CssLengthUnit::Px;

    // Resolving a percent without a basis is a programming error, not a parse
    // error: C# throws InvalidOperationException. Here it returns false so the
    // caller decides, since nothing throws across the ABI.
    bool to_pixels(const LengthContext& ctx, double* out) const;
};

struct CssAngle : CssValue {
    CssValueKind kind() const override { return CssValueKind::Angle; }
    double value = 0;
    CssAngleUnit unit = CssAngleUnit::Deg;
    double to_degrees() const;
};

struct CssString : CssValue {
    CssValueKind kind() const override { return CssValueKind::String; }
    std::string text;
    char quote = '"';
};

struct CssUrl : CssValue {
    CssValueKind kind() const override { return CssValueKind::Url; }
    std::string url;
};

struct CssColor : CssValue {
    CssValueKind kind() const override { return CssValueKind::Color; }
    uint8_t r = 0, g = 0, b = 0;
    float a = 1.0f;
};

enum class CssListSeparator { Space, Comma };

struct CssValueList : CssValue {
    CssValueKind kind() const override { return CssValueKind::List; }
    std::vector<CssValuePtr> items;
    CssListSeparator separator = CssListSeparator::Space;
};

struct CssFunctionCall : CssValue {
    CssValueKind kind() const override { return CssValueKind::FunctionCall; }
    std::string name;                    // ASCII-lowercased
    std::vector<CssValuePtr> arguments;  // comma-separated groups
};

bool css_length_unit_from_string(std::string_view unit, CssLengthUnit* out);
bool css_angle_unit_from_string(std::string_view unit, CssAngleUnit* out);
const char* css_length_unit_suffix(CssLengthUnit u);

// Mirrors CssColor.FromHex: accepts 3, 4, 6 or 8 hex digits (no leading '#').
bool css_color_from_hex(std::string_view body, CssColor* out);

// Mirrors CssNamedColors.TryGet — case-insensitive, 168 entries including
// `transparent` (0,0,0,0).
bool css_color_from_name(std::string_view name, CssColor* out);

// Mirrors CssColor.FromRgb / FromHsl / FromHwb. Channel values are clamped and
// rounded HALF-TO-EVEN, matching C#'s parameterless Math.Round — note this is
// the opposite of the away-from-zero rule used for layout dumps.
void css_color_from_rgb(double r, double g, double b, double alpha,
                        bool channels_are_percent, CssColor* out);
void css_color_from_hsl(double hue_deg, double sat_pct, double light_pct,
                        double alpha, CssColor* out);
void css_color_from_hwb(double hue_deg, double white_pct, double black_pct,
                        double alpha, CssColor* out);

// Mirrors CssValueParser.Parse. Returns null and fills `error` on failure —
// C# throws CssValueParseException.
CssValuePtr parse_css_value(std::string_view text, CssParseError* error);

} // namespace weva
