#pragma once
#include <string_view>

// CSS Color 4 §7-§10 colour spaces: the conversions the lab()/lch()/oklab()/
// oklch()/color() functions and color-mix() need. Every space goes through
// CIE XYZ D65 on its way to sRGB, with the matrices the specification's
// sample code uses, so a colour authored in any of them lands where Chrome
// puts it (to the 8-bit rounding a CssColor keeps).

namespace weva {

enum class ColorSpace {
    Srgb, SrgbLinear, DisplayP3, A98Rgb, ProphotoRgb, Rec2020,
    Xyz, XyzD50, XyzD65, Lab, Lch, Oklab, Oklch, Hsl, Hwb,
};

// The CSS name (`srgb`, `display-p3`, `oklch`, `xyz-d50`, ...) of a space;
// case-insensitive. False for anything else.
bool color_space_from_name(std::string_view name, ColorSpace* out);

// Which channel is the hue, in degrees, for the polar spaces; -1 otherwise.
// lch/oklch keep it third, hsl/hwb first.
int color_space_hue_index(ColorSpace space);

// `in` are the space's own channels (sRGB and the RGB spaces 0..1, XYZ as
// is, Lab L 0..100 with a/b unbounded, OKLab L 0..1, hue in degrees, HSL/HWB
// with hue first and percentages as 0..100). `out` is sRGB 0..1, NOT clamped:
// an out-of-gamut colour comes back outside 0..1 and the caller decides.
void color_space_to_srgb(ColorSpace from, const double in[3], double out[3]);

// The inverse: sRGB 0..1 to the space's own channels. A grey has no hue in a
// polar space and reports 0 there.
void color_space_from_srgb(ColorSpace to, const double in[3], double out[3]);

// Whether the colour's hue is powerless (CSS Color 4 §4.4): achromatic in a
// polar space, so a mix takes the other colour's hue instead.
bool color_space_hue_powerless(ColorSpace space, const double c[3]);

} // namespace weva
