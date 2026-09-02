#pragma once
#include "weva/color.h"
#include "weva/computed_style.h"
#include "weva/geometry.h"
#include "weva/style_resolver.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

// CSS Backgrounds L3 §3 and CSS Images L3 §3: the background layers of a box
// and the gradients they carry, rasterized into one texture per box.
//
// Ported     `background-color` under `background-image` layers (first layer
//            on top); linear-gradient with an angle or `to <side/corner>`
//            (magic corners); radial-gradient with circle/ellipse, the four
//            size keywords, explicit radii and `at <position>`; conic-gradient
//            with `from <angle>` and `at <position>`; the repeating- forms;
//            colour stops in %, px or bare numbers, double-position stops and
//            colour hints, interpolated in premultiplied sRGB (Images L4 §3.4.1);
//            `background-position`, `-size` (lengths, %, cover/contain, auto)
//            and `-repeat` per layer.
//
// NOT ported url() images (the layer is kept and skipped), `background-origin`
//            / `-clip` / `-attachment`, interpolation in other colour spaces
//            (`in oklab` is parsed and ignored), image-set() and cross-fade().

namespace weva {

struct GradientStop {
    LinearColor color;
    double position = 0;      // fraction of the gradient line / turn, or px
    bool has_position = false;
    bool is_px = false;
    bool is_hint = false;     // a bare position between two stops (§3.4.3)
};

struct Gradient {
    enum class Kind { Linear, Radial, Conic } kind = Kind::Linear;
    bool repeating = false;

    // Linear: the angle, or a corner (§3.1.1 "magic corners") resolved against
    // the box at raster time. corner bits: 1 left, 2 right, 4 top, 8 bottom.
    double angle_deg = 180;
    int corner = 0;

    // Radial: shape and size (§3.2.1); explicit radii kept raw so % resolves
    // against the box.
    enum class Sizing { ClosestSide, FarthestSide, ClosestCorner, FarthestCorner, Explicit };
    bool circle = false;
    Sizing sizing = Sizing::FarthestCorner;
    std::string radius_x_raw, radius_y_raw;

    // Radial and conic: centre, raw <position> components.
    std::string pos_x_raw = "50%", pos_y_raw = "50%";
    // Conic: the start angle.
    double from_deg = 0;

    std::vector<GradientStop> stops;
};

// Parses one `<gradient>()` image. `current_color` resolves `currentcolor`
// stops. False when the text is not a gradient this port reads.
bool parse_gradient(std::string_view raw, const LinearColor& current_color, Gradient* out);

struct BackgroundLayer {
    bool is_gradient = false;
    Gradient gradient;
    std::string url;                            // an image layer, not yet painted
    std::string pos_x = "0%", pos_y = "0%";     // background-position, raw
    std::string size_x = "auto", size_y = "auto";   // background-size, raw
    bool repeat_x = true, repeat_y = true;
};

// The box's `background-image` layers with their position/size/repeat, top
// layer first as in the property. Empty when there is no image.
std::vector<BackgroundLayer> resolve_background_layers(const ComputedStyle* style,
                                                       const LinearColor& current_color);

// Rasterizes `color` under `layers` over a `width` x `height` painting area
// into `tex_w` x `tex_h` straight-alpha sRGB RGBA8 texels. Gradient boxes are
// the layer tiles; positions and sizes resolve with `ctx` and `font_size`.
void rasterize_background(const std::vector<BackgroundLayer>& layers, const LinearColor& color,
                          double width, double height, int tex_w, int tex_h,
                          const LayoutContext& ctx, double font_size,
                          std::vector<uint8_t>* out_rgba);

// The same, drawn `pad` texels inside a larger texture with the box's rounded
// corners applied as a mask, for painting that needs room around the box (a
// blur). `radii` may be null for square corners.
void rasterize_background_padded(const std::vector<BackgroundLayer>& layers,
                                 const LinearColor& color, double width, double height,
                                 int tex_w, int tex_h, int pad, const struct BorderRadii* radii,
                                 const LayoutContext& ctx, double font_size,
                                 std::vector<uint8_t>* out_rgba);

// Coverage of a rounded rectangle at a point, with the box's origin at (0,0)
// and `radii` its corners. Antialiased: half a texel either side of the edge,
// which is exact for a circular corner and within a percent for the
// eccentricities a border-radius produces. Null radii means square corners.
double rounded_rect_coverage(double px, double py, double w, double h,
                             const struct BorderRadii* radii);

// A Gaussian blur of straight-alpha RGBA8 texels (blurred premultiplied, so
// colour does not bleed from transparent texels), sigma in texels. Three box
// passes per axis.
void blur_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma);

// The colour of `g` at (x, y) inside a `width` x `height` gradient box, as
// straight-alpha sRGB in [0, 1]. Exposed for tests.
void sample_gradient(const Gradient& g, double x, double y, double width, double height,
                     const LayoutContext& ctx, double font_size, float out_srgb[4]);

} // namespace weva
