#pragma once
#include <cstdio>
#include <cstring>
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
} // namespace wevatest
#define CHECK(x) ::wevatest::check((x), #x, __FILE__, __LINE__)
