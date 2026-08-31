#pragma once
#include <cstdio>
#include <cstring>
#include <string>
namespace wevatest {
inline int failures = 0;
inline int checks = 0;
inline void check(bool cond, const char* expr, const char* file, int line) {
    ++checks;
    if (!cond) {
        ++failures;
        std::printf("FAIL %s:%d  %s\n", file, line, expr);
    }
}

// Value-printing equality check. A bare CHECK(a == b) reports only the
// expression, which for string comparisons means re-running under a debugger
// to learn what was actually produced.
inline void check_eq(const std::string& got, const std::string& want, const char* expr,
                     const char* file, int line) {
    ++checks;
    if (got != want) {
        ++failures;
        std::printf("FAIL %s:%d  %s\n     got  \"%s\"\n     want \"%s\"\n", file, line, expr,
                    got.c_str(), want.c_str());
    }
}
} // namespace wevatest
#define CHECK(x) ::wevatest::check((x), #x, __FILE__, __LINE__)
#define CHECK_EQ(a, b) ::wevatest::check_eq((a), (b), #a " == " #b, __FILE__, __LINE__)
