#include "weva/background.h"
#include "weva/box.h"
#include <cstdio>
#include <utility>
#include <vector>

namespace weva {
void portable_blur_rgba(std::vector<uint8_t>*, int, int, double);
void portable_blur_flat_rgba(std::vector<uint8_t>*, int, int, double);
void portable_rasterize_background(const std::vector<BackgroundLayer>&, const LinearColor&,
    double, double, int, int, const LayoutContext&, double, std::vector<uint8_t>*);
}

int main() {
    unsigned checks = 0;
    for (const auto& size : {std::pair<int,int>{0,0}, {1,1}, {1,41}, {41,1},
                            {3,2}, {5,3}, {17,7}, {127,29}, {128,31}, {129,33},
                            {513,257}, {1024,656}}) {
        const int w = size.first, h = size.second;
        for (double sigma : {0.0, 0.3, 0.31, 2.5, 30.356, 90.0}) {
            if (w > 500 && sigma != 2.5 && sigma != 30.356) continue;
            for (int pattern = 0; pattern < 3; ++pattern) {
                std::vector<uint8_t> source(static_cast<size_t>(w) * h * 4);
                if (pattern != 0) {
                    uint32_t seed = 17;
                    for (auto& v : source) { seed = 1664525u * seed + 1013904223u; v = seed >> 24; }
                }
                if (pattern == 2) {
                    for (size_t i = 0; i < source.size(); i += 4) {
                        source[i] = 200; source[i + 1] = 37; source[i + 2] = 91;
                    }
                }
                const bool flat = pattern == 2;
                auto normal = source, portable = source;
                (flat ? weva::blur_flat_rgba : weva::blur_rgba)(&normal, w, h, sigma);
                (flat ? weva::portable_blur_flat_rgba : weva::portable_blur_rgba)(&portable, w, h, sigma);
                ++checks;
                if (normal != portable) {
                    std::fprintf(stderr, "blur variants differ: %dx%d sigma=%g pattern=%d\n", w, h, sigma, pattern);
                    return 1;
                }
            }
        }
    }
    std::printf("blur variants: %u checks, exact bytes\n", checks);
    // Worker partitions must preserve blending, masks, wrap boundaries, hard
    // stops and odd final rows. Repeat calls exercise worker teardown and reuse.
    // Preparation uses the core's single-threaded parser; only the prepared
    // pixel loop is allowed to run concurrently.
    unsigned raster_checks = 0;
    const char* gradients[] = {
        "radial-gradient(ellipse at 18% 12%, #b388ff38, transparent 55%)",
        "conic-gradient(from -90deg, red 270deg, #1234 270deg)",
        "repeating-linear-gradient(118deg, #0004 0px 4px, #abcd 4px 8px)",
        "linear-gradient(180deg, #123, #789 60%, #456)"
    };
    for (int mode = 0; mode < 6; ++mode) {
        weva::LayoutContext ctx;
        std::vector<weva::BackgroundLayer> layers(2);
        for (int i = 0; i < 2; ++i) {
            layers[i].is_gradient = true;
            if (!weva::parse_gradient(gradients[(mode + i) % 4],
                                     weva::LinearColor::black(), &layers[i].gradient)) return 2;
        }
        if (mode == 1) { layers[0].is_mask = true; layers[0].mask_luminance = true; }
        if (mode == 2) { layers[0].blend = weva::BlendMode::Screen; }
        if (mode == 3) { layers[0].size_x = "71px"; layers[0].size_y = "53px"; }
        if (mode == 4) { layers[0].repeat_x = false; layers[0].pos_x = "20px"; }
        for (const auto& size : {std::pair<int, int>{64, 33}, {513, 257}}) {
            const int w = size.first, h = size.second;
            std::vector<uint8_t> reference, actual[2];
            weva::portable_rasterize_background(layers, weva::LinearColor::black(),
                w, h, w, h, ctx, 16, &reference);
            const auto render = [&](int index) {
                weva::rasterize_background(layers, weva::LinearColor::black(),
                    w, h, w, h, ctx, 16, &actual[index]);
            };
            render(0);
            render(1);
            for (const auto& pixels : actual) {
                ++raster_checks;
                if (pixels != reference) {
                    std::fprintf(stderr, "raster variants differ: mode=%d %dx%d\n", mode, w, h);
                    for (size_t j = 0; j < pixels.size(); ++j) if (pixels[j] != reference[j]) {
                        std::fprintf(stderr, "byte %zu: %u vs %u\n", j, pixels[j], reference[j]);
                        break;
                    }
                    return 1;
                }
            }
        }
    }
    std::printf("raster variants: %u checks, exact bytes\n", raster_checks);
}
