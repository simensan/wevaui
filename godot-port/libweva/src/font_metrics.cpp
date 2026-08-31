#include "weva/font_metrics.h"

#include <cstdint>

namespace weva {

namespace {

// Minimal UTF-8 decode: returns the code point and advances `i`. Malformed
// input is consumed one byte at a time so measurement always terminates.
uint32_t next_code_point(std::string_view s, size_t* i) {
    const unsigned char c = static_cast<unsigned char>(s[*i]);
    size_t len = 1;
    uint32_t cp = c;
    if ((c & 0xE0) == 0xC0) { len = 2; cp = c & 0x1Fu; }
    else if ((c & 0xF0) == 0xE0) { len = 3; cp = c & 0x0Fu; }
    else if ((c & 0xF8) == 0xF0) { len = 4; cp = c & 0x07u; }
    if (*i + len > s.size()) { ++*i; return c; }
    for (size_t k = 1; k < len; ++k) {
        const unsigned char cc = static_cast<unsigned char>(s[*i + k]);
        if ((cc & 0xC0) != 0x80) { ++*i; return c; }   // malformed
        cp = (cp << 6) | (cc & 0x3Fu);
    }
    *i += len;
    return cp;
}

// Chrome renders these from an emoji face at roughly 1.3em; the Dingbats block
// lands nearer 1.0em. The BMP allowlist is deliberate — neighbouring ranges
// (Miscellaneous Technical, most of Miscellaneous Symbols, Math) are
// text-presented and must keep the Latin advance.
bool is_wide_emoji(uint32_t cp) {
    return (cp >= 0x1F000 && cp <= 0x1FAFF) || cp == 0x26A1 || cp == 0x26D4 ||
           cp == 0x2600 || cp == 0x2614 || cp == 0x2615 || cp == 0x2618 ||
           cp == 0x2620 || cp == 0x2705;
}

bool is_medium_emoji(uint32_t cp) {
    return (cp >= 0x2700 && cp <= 0x27BF) || cp == 0x2699 || cp == 0x2298;
}

} // namespace

double MonoFontMetrics::measure(std::string_view text, double fs) const {
    double total = 0;
    size_t i = 0;
    while (i < text.size()) {
        const uint32_t cp = next_code_point(text, &i);
        const double em = is_wide_emoji(cp) ? 1.3 : (is_medium_emoji(cp) ? 1.0 : char_width_em_);
        total += em * fs;
    }
    return total;
}

} // namespace weva
