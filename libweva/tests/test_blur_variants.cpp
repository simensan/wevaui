#include "weva/background.h"
#include <cstdio>
#include <utility>
#include <vector>

namespace weva {
void portable_blur_rgba(std::vector<uint8_t>*, int, int, double);
void portable_blur_flat_rgba(std::vector<uint8_t>*, int, int, double);
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
}
