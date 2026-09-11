// Does a gradient's TEXELS depend on the box size, or only on the texture size?
//
// The background texture cache keys on the box's width and height, so a
// one-pixel resize regenerates the texture -- 1024x1024 texels, about 28 ms on
// grid-playground, on every frame of a resize. The question this answers is
// whether that key is asking something the pixels actually answer.
//
// They do not, for the common case. A gradient whose geometry is entirely
// percentages resolves to the same texels at any box size, because every
// coordinate divides the size straight back out: rx = 0.8w and cx = 0.5w make
// ex = ((px + 0.5) / tex_w - 0.5) / 0.8, with no w left in it.
//
// This is the test any fix has to pass. Keying on the resolved, size-normalised
// gradient parameters instead of the box size would let the first row share a
// texture -- and MUST NOT let the others, which differ by up to 58 of 255.
// Anything that makes the first row hit and the rest miss is correct; anything
// that makes a later row hit is a wrong texture on screen.
#include "weva/background.h"
#include "weva/computed_style.h"
#include "weva/css_properties.h"
#include "weva/font_metrics.h"

#include <cstdio>
#include <string>
#include <vector>
using namespace weva;

static std::vector<uint8_t> raster(const char* css, double w, double h, int tw, int th) {
    LinearColor base{0, 0, 0, 0};
    LayoutContext ctx;
    ctx.viewport_width_px = 1280;
    ctx.viewport_height_px = 720;
    ComputedStyle style;
    style.set(CssPropertyRegistry::instance().id_of("background-image"), css);
    const std::vector<BackgroundLayer> layers = resolve_background_layers(&style, base);
    std::vector<uint8_t> out;
    rasterize_background(layers, base, w, h, tw, th, ctx, 16, &out);
    return out;
}

static void compare(const char* name, const char* css, double w1, double w2) {
    // The texture size is what the cache would hold; both boxes cap to the
    // same one, which is the whole premise.
    const auto a = raster(css, w1, 1135, 1024, 1024);
    const auto b = raster(css, w2, 1135, 1024, 1024);
    if (a.size() != b.size()) {
        std::printf("%-26s DIFFERENT SIZE %zu vs %zu\n", name, a.size(), b.size());
        return;
    }
    size_t differing = 0;
    int worst = 0;
    for (size_t i = 0; i < a.size(); ++i) {
        const int d = a[i] > b[i] ? a[i] - b[i] : b[i] - a[i];
        if (d) { ++differing; if (d > worst) worst = d; }
    }
    std::printf("%-26s %8zu of %zu bytes differ, worst %d\n", name, differing, a.size(), worst);
}

int main() {
    compare("radial, all percentages",
            "radial-gradient(ellipse 80% 60% at 50% 0%, #16223a 0%, rgba(22,34,58,0) 55%)",
            1280, 1269);
    compare("radial, px radius",
            "radial-gradient(ellipse 400px 300px at 50% 0%, #16223a 0%, rgba(22,34,58,0) 55%)",
            1280, 1269);
    compare("linear, percentages",
            "linear-gradient(135deg, #36c2ff 0%, #5b8cff 100%)", 1280, 1269);
    compare("linear, px stop",
            "linear-gradient(90deg, #36c2ff 0px, #5b8cff 300px)", 1280, 1269);
    return 0;
}
