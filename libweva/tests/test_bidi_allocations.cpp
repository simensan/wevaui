#include "weva/bidi.h"
#include <cstdio>
#include <cstdlib>
#include <new>

namespace {
bool counting = false;
size_t allocations = 0, bytes = 0;
int checks = 0, failures = 0;
void check(bool ok, const char* label) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL: %s\n", label); }
}
}
void* operator new(size_t size) {
    if (counting) { ++allocations; bytes += size; }
    void* p = std::malloc(size ? size : 1);
    if (!p) std::abort();
    return p;
}
void* operator new[](size_t n) { return ::operator new(n); }
void* operator new(size_t n, const std::nothrow_t&) noexcept { return ::operator new(n); }
void* operator new[](size_t n, const std::nothrow_t&) noexcept { return ::operator new(n); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }
void operator delete(void* p, const std::nothrow_t&) noexcept { std::free(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { std::free(p); }

int main() {
    using namespace weva;
    ComputedStyle normal, rtl, override_style;
    normal.set("unicode-bidi", "normal");
    rtl.set("direction", "rtl");
    override_style.set("unicode-bidi", "bidi-override");
    override_style.set("direction", "rtl");
    const std::string labels[] = {"Inventory: 12 / 40 slots", u8"Café 木材 👩‍💻", std::string("bad\xFF", 4)};
    for (const auto& label : labels) for (size_t count : {size_t(1), size_t(256)}) {
        std::vector<InlineItem> items(count);
        for (auto& item : items) { item.text = label; item.style = &normal; }
        resolve_bidi_levels(&items, &normal); // Warm registry/ICU outside accounting.
        const auto* data = items.data();
        allocations = bytes = 0;
        counting = true;
        const bool resolved = resolve_bidi_levels(&items, &normal);
        counting = false;
        check(!resolved && items.data() == data && items.size() == count, "LTR items remain untouched");
        check(allocations == 0 && bytes == 0, "LTR detection allocates no mapping storage");
        for (const auto& item : items)
            check(item.text == label && item.bidi_level == 0, "LTR content and levels preserved");
    }
    // Container direction and inline controls must not take the LTR exit.
    std::vector<InlineItem> items(1);
    items[0].text = "123";
    check(resolve_bidi_levels(&items, &rtl), "RTL paragraph invokes resolver");
    check(items.size() == 1 && items[0].bidi_level == 2, "RTL paragraph keeps numeric level");
    items.assign(3, InlineItem{});
    items[0].inline_box_start = 7; items[0].style = &override_style;
    items[1].text = "abc";
    items[2].inline_box_end = 7;
    check(resolve_bidi_levels(&items, &normal), "inline override invokes resolver");
    check(items.size() == 3 && items[1].bidi_level == 1, "inline override applies to ASCII");
    for (const char* text : {u8"abc אבג def", u8"abc ١٢٣ def", u8"abc \u202Edef\u202C", u8"abc \u2067def\u2069"}) {
        items.assign(1, InlineItem{}); items[0].text = text;
        check(resolve_bidi_levels(&items, &normal), "Unicode directional input invokes resolver");
    }
    // Markers/atoms/breaks are not paragraph text, even if a caller leaves
    // a text view on them. Their mapping behavior must match the full builder.
    items.assign(3, InlineItem{});
    items[0].atom_box = 1; items[1].break_box = 2; items[2].inline_box_end = 3;
    for (auto& item : items) item.text = u8"אבג";
    check(!resolve_bidi_levels(&items, &normal), "non-text items do not trigger paragraph resolution");
    std::printf("bidi allocations: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
