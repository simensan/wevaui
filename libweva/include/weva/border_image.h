#pragma once
#include "weva/computed_style.h"
#include "weva/image_decode.h"
#include "weva/style_resolver.h"

#include <cstdint>
#include <vector>

namespace weva {

// CSS Backgrounds L3 §6: a nine-slice border drawn from an image.
//
// The reason a game UI cares: a panel frame, a button frame and a tooltip
// bubble are one sprite each, sliced so the corners stay crisp while the edges
// and middle stretch to whatever size the content needs. It is the single most
// common way a game's art reaches its UI, and `9slice-demo` in the sample
// corpus is three screens of it -- drawing nothing at all until now, because
// `border-image-source` was registered and read by nobody.

// How an edge fills the run between two corners.
enum class BorderImageRepeat {
    Stretch,   // one copy, scaled to the whole run
    Repeat,    // whole tiles at their natural size, centred, clipped at the ends
    Round,     // tiles scaled so a whole number of them fits exactly
    Space      // whole tiles at natural size, the slack spread between them
};

struct BorderImageSlices {
    // In SOURCE pixels, resolved from `border-image-slice`'s numbers and
    // percentages before it gets here.
    double top = 0, right = 0, bottom = 0, left = 0;
    // `fill` keeps the middle of the source and paints it over the padding
    // box. Without it the centre is left alone, which is what lets a frame sit
    // over a background of its own.
    bool fill = false;
};

struct BorderImageWidths {
    // In destination pixels: what `border-image-width` resolved to, which is a
    // MULTIPLE of the border width when it is a bare number, and the border
    // width itself by default.
    double top = 0, right = 0, bottom = 0, left = 0;
};

struct BorderImage {
    const DecodedImage* image = nullptr;
    BorderImageSlices slice;
    BorderImageWidths width;
    BorderImageRepeat repeat_x = BorderImageRepeat::Stretch;
    BorderImageRepeat repeat_y = BorderImageRepeat::Stretch;

    bool valid() const { return image && image->valid(); }
};

// Reads the five longhands off a style. `border_*` are the used border widths,
// which `border-image-width`'s bare numbers multiply. Returns false when there
// is no image to draw.
bool resolve_border_image(const ComputedStyle* style, const DecodedImage* image,
                          double border_top, double border_right, double border_bottom,
                          double border_left, double box_width, double box_height,
                          const LayoutContext& ctx, double font_size, BorderImage* out);

// Draws the nine pieces into a `tex_w` x `tex_h` straight-alpha sRGB buffer
// covering a `dest_w` x `dest_h` border-image area.
void rasterize_border_image(const BorderImage& bi, double dest_w, double dest_h, int tex_w,
                            int tex_h, std::vector<uint8_t>* out_rgba);

} // namespace weva
