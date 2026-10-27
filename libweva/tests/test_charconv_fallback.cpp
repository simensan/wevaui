// Exercise the older Apple/Android libc++ path on every build host.
#define WEVA_CHARCONV_FALLBACK 1
#include "weva/charconv_compat.h"
#include "check.h"
#include <limits>

int main() {
    struct Case { double value; const char* text; };
    const Case cases[] = {{0, "0"}, {-0.0, "-0"}, {10, "10"}, {30, "30"},
        {120, "120"}, {-120, "-120"}, {10000, "10000"}, {1000000, "1e+06"},
        {1.25, "1.25"}, {0.0001, "1e-04"}, {0.00001, "1e-05"}};
    for (const auto& entry : cases) {
        char text[64];
        auto result = weva::to_chars_double(text, text + sizeof(text), entry.value);
        CHECK(result.ec == std::errc{});
        CHECK_EQ(std::string(text, result.ptr), entry.text);
        char exact[64];
        auto fits = weva::to_chars_double(exact, exact + std::strlen(entry.text), entry.value);
        CHECK(fits.ec == std::errc{});
        auto short_buffer = weva::to_chars_double(exact, exact + std::strlen(entry.text) - 1, entry.value);
        CHECK(short_buffer.ec == std::errc::value_too_large);
    }
    for (double value : {1.2345678901234567, std::numeric_limits<double>::min(),
                         std::numeric_limits<double>::max(), std::numeric_limits<double>::denorm_min()}) {
        char text[64];
        auto result = weva::to_chars_double(text, text + sizeof(text), value);
        CHECK(result.ec == std::errc{});
        double parsed = 0;
        auto read = weva::from_chars_double(text, result.ptr, parsed);
        CHECK(read.ec == std::errc{} && read.ptr == result.ptr && parsed == value);
    }
    char text[64];
    auto precision = weva::to_chars_double(text, text + sizeof(text), 123.456, 3);
    CHECK(precision.ec == std::errc{});
    CHECK_EQ(std::string(text, precision.ptr), "123");
    return wevatest::failures ? 1 : 0;
}
