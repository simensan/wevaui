#pragma once
#include <cstdint>

namespace weva {

// Ports Runtime/Paint/LinearColor.cs. PLAN.md §9 locks linear color space;
// the sRGB->linear curve is the IEC 61966-2-1 piecewise one, not gamma 2.2,
// because the cascade emits 8-bit sRGB bytes and the round trip must be
// well-defined.
struct LinearColor {
    float r = 0, g = 0, b = 0, a = 0;

    constexpr LinearColor() = default;
    constexpr LinearColor(float r_, float g_, float b_, float a_) : r(r_), g(g_), b(b_), a(a_) {}

    static constexpr LinearColor transparent() { return LinearColor(0, 0, 0, 0); }
    static constexpr LinearColor black() { return LinearColor(0, 0, 0, 1); }
    static constexpr LinearColor white() { return LinearColor(1, 1, 1, 1); }

    static LinearColor from_srgb(uint8_t r, uint8_t g, uint8_t b, float alpha);

    constexpr LinearColor premultiplied() const { return LinearColor(r * a, g * a, b * a, a); }

    friend constexpr bool operator==(const LinearColor& x, const LinearColor& y) {
        return x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;
    }
    friend constexpr bool operator!=(const LinearColor& x, const LinearColor& y) { return !(x == y); }
};

float srgb_byte_to_linear(uint8_t v);

} // namespace weva
