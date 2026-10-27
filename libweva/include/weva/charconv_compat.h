#pragma once
// Floating-point <charconv> where the standard library has it, and a
// faithful stand-in where it does not. libc++ before version 20 (Android
// NDK 27, older Apple toolchains) declares the double overloads of
// std::from_chars / std::to_chars deleted, and the core must build for those
// targets: the Unity plugin ships to Android and iOS.
//
// The stand-in keeps from_chars' contract that callers rely on: no leading
// whitespace or '+', the number ends where the text stops matching, and the
// result says where. to_chars' "shortest round-trip" form is found by
// trying rising precisions, which is what the callers' C# counterpart
// ("R" formatting) produces.
#include <charconv>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <system_error>

#ifndef WEVA_CHARCONV_FALLBACK
#if defined(_LIBCPP_VERSION) && _LIBCPP_VERSION < 200000
#define WEVA_CHARCONV_FALLBACK 1
#else
#define WEVA_CHARCONV_FALLBACK 0
#endif
#endif

namespace weva {

inline std::from_chars_result from_chars_double(const char* first, const char* last, double& value) {
#if WEVA_CHARCONV_FALLBACK
    if (first == last || *first == '+' || *first == ' ' || *first == '\t' || *first == '\n') {
        return {first, std::errc::invalid_argument};
    }
    // strtod needs a terminator; the texts here are short attribute and CSS
    // tokens, so a bounded copy costs nothing worth measuring.
    char local[128];
    std::string heap;
    const size_t n = static_cast<size_t>(last - first);
    const char* text;
    if (n < sizeof(local)) {
        std::memcpy(local, first, n);
        local[n] = '\0';
        text = local;
    } else {
        heap.assign(first, n);
        text = heap.c_str();
    }
    // strtod also reads "inf", "nan" and hex floats; from_chars' general
    // format does not read hex, and the callers reject non-finite values.
    if ((text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) ||
        (text[0] == '-' && text[1] == '0' && (text[2] == 'x' || text[2] == 'X'))) {
        return {first, std::errc::invalid_argument};
    }
    char* end = nullptr;
    const double v = std::strtod(text, &end);
    if (end == text) return {first, std::errc::invalid_argument};
    value = v;
    return {first + (end - text), std::errc{}};
#else
    return std::from_chars(first, last, value);
#endif
}

// precision < 0: the shortest text that round-trips (std::to_chars(v)).
inline std::to_chars_result to_chars_double(char* first, char* last, double value, int precision = -1) {
#if WEVA_CHARCONV_FALLBACK
    char buffer[64];
    int written = 0;
    if (precision >= 0) {
        written = std::snprintf(buffer, sizeof(buffer), "%.*g", precision, value);
    } else {
        int digits = 17;
        for (int p = 1; p <= 17; ++p) {
            written = std::snprintf(buffer, sizeof(buffer), "%.*g", p, value);
            if (written > 0 && std::strtod(buffer, nullptr) == value) { digits = p; break; }
        }
        // %g minimizes significant digits, not text length: %.1g renders 30
        // as "3e+01". Compare fixed and scientific forms at that round-trip
        // precision, preferring fixed on ties as std::to_chars does.
        char candidate[64];
        int count = std::snprintf(candidate, sizeof(candidate), "%.*e", digits - 1, value);
        const char* exponent = std::strchr(candidate, 'e');
        if (exponent) {
            const int fraction = digits - 1 - std::atoi(exponent + 1);
            if (count > 0 && count < written && std::strtod(candidate, nullptr) == value) {
                std::memcpy(buffer, candidate, static_cast<size_t>(count + 1));
                written = count;
            }
            if (fraction < static_cast<int>(sizeof(candidate))) {
                count = std::snprintf(candidate, sizeof(candidate), "%.*f", fraction > 0 ? fraction : 0, value);
                if (count > 0 && count <= written && count < static_cast<int>(sizeof(candidate)) &&
                    std::strtod(candidate, nullptr) == value) {
                    std::memcpy(buffer, candidate, static_cast<size_t>(count + 1));
                    written = count;
                }
            }
        }
    }
    if (written <= 0 || written >= static_cast<int>(sizeof(buffer)) ||
        static_cast<size_t>(written) > static_cast<size_t>(last - first))
        return {last, std::errc::value_too_large};
    std::memcpy(first, buffer, static_cast<size_t>(written));
    return {first + written, std::errc{}};
#else
    return precision >= 0 ? std::to_chars(first, last, value, std::chars_format::general, precision)
                          : std::to_chars(first, last, value);
#endif
}

}  // namespace weva
