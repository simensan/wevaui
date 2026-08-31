#include "weva/color.h"

#include <cmath>

namespace weva {

float srgb_byte_to_linear(uint8_t v) {
    // Endpoints are exact in linear space. Without these the pow() path gives
    // 255 -> 1.00000012f and White stops round-tripping to 1.0 — the C# side
    // special-cases them for the same reason.
    if (v == 0) return 0.0f;
    if (v == 255) return 1.0f;

    float f = v / 255.0f;
    if (f <= 0.04045f) return f / 12.92f;

    // Subtle, and worth spelling out. C# writes Math.Pow(x, 2.4f); Math.Pow has
    // no float overload, so the literal widens to (double)2.4f =
    // 2.400000095367431640625, NOT 2.4. Writing a bare 2.4 here would give a
    // slightly different exponent and a scatter of one-ULP color differences
    // that look like a real bug in the cascade. Match the widening exactly.
    const double exponent = static_cast<double>(2.4f);
    double base = static_cast<double>((f + 0.055f) / 1.055f);
    return static_cast<float>(std::pow(base, exponent));
}

LinearColor LinearColor::from_srgb(uint8_t r, uint8_t g, uint8_t b, float alpha) {
    return LinearColor(srgb_byte_to_linear(r), srgb_byte_to_linear(g),
                       srgb_byte_to_linear(b), alpha);
}

} // namespace weva
