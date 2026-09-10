#include "weva/color.h"

#include <cmath>
#include <array>

namespace weva {

namespace {
float compute_srgb_byte(uint8_t v) {
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

const std::array<float, 256>& srgb_channels() {
    // Immutable byte-domain mapping: retain the exact former arithmetic once
    // for every input, including its float exponent and exact endpoints.
    // Initialization is thread-safe and uses no heap allocation.
    static const auto channels = [] {
        std::array<float, 256> values{};
        for (size_t i = 0; i < values.size(); ++i)
            values[i] = compute_srgb_byte(static_cast<uint8_t>(i));
        return values;
    }();
    return channels;
}
} // namespace

float srgb_byte_to_linear(uint8_t v) { return srgb_channels()[v]; }

LinearColor LinearColor::from_srgb(uint8_t r, uint8_t g, uint8_t b, float alpha) {
    const auto& channels = srgb_channels();
    return LinearColor(channels[r], channels[g], channels[b], alpha);
}

} // namespace weva
