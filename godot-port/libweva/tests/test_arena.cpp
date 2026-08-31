#include "check.h"
#include "weva/arena.h"
#include <cstdint>

void test_arena() {
    weva::Arena a(1024);

    // Alignment holds for over-aligned types.
    auto* p = a.alloc_array<double>(3);
    CHECK(reinterpret_cast<uintptr_t>(p) % alignof(double) == 0);
    p[0] = 1.5; p[2] = 2.5;
    CHECK(p[0] == 1.5 && p[2] == 2.5);

    // An allocation larger than the block size still succeeds.
    auto* big = a.alloc_array<char>(4096);
    CHECK(big != nullptr);
    big[4095] = 7;
    CHECK(big[4095] == 7);

    // The property the whole design rests on: after reset, a repeated
    // workload must not grow reserved memory. This is the zero-alloc
    // steady state the C# implementation could not reach.
    std::size_t reserved_before = a.bytes_reserved();
    for (int frame = 0; frame < 50; ++frame) {
        a.reset();
        for (int i = 0; i < 40; ++i) (void)a.alloc_array<char>(100);
    }
    CHECK(a.bytes_reserved() == reserved_before);
    CHECK(a.bytes_used() == 40 * 100);

    a.reset();
    CHECK(a.bytes_used() == 0);
}
