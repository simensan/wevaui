#pragma once

#include "weva/dom.h"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <string>

namespace weva {

inline std::string form_number_text(double value) {
    char text[64];
    const auto written = std::to_chars(text, text + sizeof(text), value == 0 ? 0 : value,
                                      std::chars_format::general, 15);
    return written.ec == std::errc{} ? std::string(text, written.ptr) : std::string();
}

inline double form_number(const Element& e, const char* name, double fallback) {
    const auto raw = e.get_attribute(name);
    if (raw.empty()) return fallback;
    double value = 0;
    const auto parsed = std::from_chars(raw.data(), raw.data() + raw.size(), value);
    return parsed.ec == std::errc{} && parsed.ptr == raw.data() + raw.size() && std::isfinite(value)
        ? value : fallback;
}

// The same range value drives painting, reads and pointer/keyboard edits.
// Alignment is relative to min, then the value attribute, then zero (HTML's
// step base). Ties choose the larger permitted value.
struct RangeValue {
    double min = 0, max = 100, step = 1, base = 0, value = 50;
    bool any = false;

    double normalize(double v) const {
        v = std::clamp(v, min, max);
        if (any || max == min) return v;
        const double first = std::ceil((min - base) / step - 1e-9);
        const double last = std::floor((max - base) / step + 1e-9);
        if (first > last) return v;
        const double at = std::clamp(std::floor((v - base) / step + 0.5), first, last);
        return std::clamp(base + at * step, min, max);
    }

    explicit RangeValue(const Element& e) {
        *this = RangeValue(e, e.form_value());
    }
    RangeValue(const Element& e, std::string_view raw) {
        min = form_number(e, "min", 0);
        max = std::max(min, form_number(e, "max", 100));
        base = form_number(e, "min", form_number(e, "value", 0));
        any = e.get_attribute("step") == "any";
        step = form_number(e, "step", 1);
        if (step <= 0) step = 1;
        double supplied = min / 2 + max / 2;
        double parsed_value = 0;
        if (!raw.empty()) {
            const auto parsed = std::from_chars(raw.data(), raw.data() + raw.size(), parsed_value);
            if (parsed.ec == std::errc{} && parsed.ptr == raw.data() + raw.size() && std::isfinite(parsed_value)) supplied = parsed_value;
        }
        value = normalize(supplied);
    }
};

} // namespace weva
