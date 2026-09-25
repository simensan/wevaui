// A separate executable keeps the allocation counter out of the general
// test runner. Property reads must not allocate after the style tree is built.
#include "weva/computed_style.h"
#include <array>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>

namespace {
bool counting = false;
size_t allocations = 0;
}

void* operator new(size_t size) {
    if (counting) ++allocations;
    void* p = std::malloc(size ? size : 1);
    if (!p) std::abort();
    return p;
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

int main() {
    std::array<weva::ComputedStyle, 32> styles;
    for (size_t i = 1; i < styles.size(); ++i) styles[i].set_inherit_parent(&styles[i - 1]);
    const std::string name = "--component-primary-accent-color";
    const std::string case_name = "--component-primary-Accent-color";
    const std::string missing = name + "-suffix";
    const std::string_view slice(missing.data(), name.size());
    styles.front().set(name, "root value");
    styles[15].set(case_name, "middle value");
    styles.front().set("color", "red");
    const auto& leaf = styles.back();
    // Prove the counter intercepts allocations inside the linked core.
    counting = true;
    styles.front().set("--allocation-counter-positive-control", "set");
    counting = false;
    bool valid = allocations > 0;
    allocations = 0;
    counting = true;
    for (int i = 0; i < 1000; ++i) {
        valid &= leaf.get(slice) == "root value";
        valid &= leaf.get(case_name) == "middle value";
        valid &= leaf.get(missing).empty();
        valid &= leaf.contains(slice);
        valid &= !leaf.contains(missing);
        valid &= !leaf.contains_own(slice);
        valid &= styles.front().contains_own(slice);
        valid &= leaf.get("color") == "red";
    }
    counting = false;
    std::printf("style lookup: 8000 reads, %zu allocations, values %s\n", allocations, valid ? "match" : "DIFFER");
    return valid && allocations == 0 ? 0 : 1;
}
