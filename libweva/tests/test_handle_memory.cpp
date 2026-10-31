#include "weva_c.h"
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstdio>
#include <cstdint>
#include <new>
#include <string>
#include <cstring>
#include <array>
#include <cstddef>
// Count requested live C++ storage, not allocator arenas/RSS. The header also
// handles aligned allocations; counters are atomic for prepared raster workers.
static std::atomic<size_t> live{0};
struct Header { void* raw; size_t size; };
static void* alloc(size_t size, size_t alignment) {
    alignment = std::max(alignment, alignof(Header));
    if (size > SIZE_MAX - alignment - sizeof(Header)) std::abort();
    void* raw = std::malloc(size + alignment + sizeof(Header));
    if (!raw) std::abort();
    const auto address = (reinterpret_cast<uintptr_t>(raw) + sizeof(Header) + alignment - 1) & ~(alignment - 1);
    auto* header = reinterpret_cast<Header*>(address) - 1;
    header->raw = raw;
    header->size = size;
    live += size;
    return reinterpret_cast<void*>(address);
}
void* operator new(size_t size) { return alloc(size, alignof(std::max_align_t)); }
void* operator new[](size_t size) { return ::operator new(size); }
void* operator new(size_t size, std::align_val_t alignment) { return alloc(size, static_cast<size_t>(alignment)); }
void* operator new[](size_t size, std::align_val_t alignment) { return ::operator new(size, alignment); }
void operator delete(void* pointer) noexcept {
    if (!pointer) return;
    auto* header = static_cast<Header*>(pointer) - 1;
    live -= header->size;
    std::free(header->raw);
}
void operator delete[](void* p) noexcept { ::operator delete(p); }
void operator delete(void* p, size_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, size_t) noexcept { ::operator delete(p); }
void operator delete(void* p, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete(void* p, size_t, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, size_t, std::align_val_t) noexcept { ::operator delete(p); }

static int checks = 0, failures = 0;
static void check(bool passed, const char* what) {
    ++checks;
    if (!passed) { ++failures; std::printf("FAIL %s\n", what); }
}
static void ok(weva_status status) { check(status == WEVA_OK, "ABI operation succeeds"); }

int main() {
    std::string body;
    for (int i = 0; i < 32; ++i) body += "<div id=item" + std::to_string(i) + ">Item</div>";
    const std::string page = "<main id=root>" + body + "</main>";
    weva_config cfg{};
    cfg.viewport_width = 640;
    cfg.viewport_height = 480;
    const auto create = [&] {
        auto document = weva_document_create(&cfg);
        check(document != nullptr, "create document");
        if (!document) std::abort();
        ok(weva_document_load_html(document, page.data(), page.size()));
        ok(weva_document_update(document, 0));
        return document;
    };
    auto document = create();
    weva_document_destroy(document);
    const size_t warm = live.load();
    document = create();
    const auto root = weva_document_query(document, "#root");
    std::array<weva_element_t, 64> retired{};
    size_t settled = 0;
    for (int i = 0; i < 4096; ++i) {
        if (i % 64 == 0) retired[static_cast<size_t>(i) / 64] = weva_document_query(document, "#item0");
        ok(weva_element_set_html(document, root, body.data(), body.size()));
        ok(weva_document_update(document, 0));
        if (i == 255) settled = live.load();
        if (i % 256 == 0)
            check(weva_document_query(document, "#root") == root, "surviving root keeps its handle");
    }
    const size_t after = live.load();
    check(after <= settled + 64 * 1024, "retired handles do not retain lifetime-sized storage");
    for (auto handle : retired) {
        check(handle != WEVA_ELEMENT_NONE, "retired handle was valid");
        check(weva_element_bounds(document, handle, nullptr, nullptr, nullptr, nullptr) == WEVA_ERR_NOT_FOUND,
              "removed handles stay invalid across reclaimed pages");
    }
    const auto child = weva_document_query(document, "#item0");
    ok(weva_document_reload_html(document, page.data(), page.size()));
    ok(weva_document_update(document, 0));
    check(weva_document_query(document, "#item0") == child, "matched reload preserves surviving handles");
    ok(weva_document_load_html(document, page.data(), page.size()));
    ok(weva_document_update(document, 0));
    check(weva_document_query(document, "#root") != root, "full load does not recycle old handles");
    check(weva_element_set_attribute(document, root, "class", "stale") == WEVA_ERR_NOT_FOUND,
          "old full-load handle cannot mutate its replacement");
    check(weva_element_bounds(document, child, nullptr, nullptr, nullptr, nullptr) == WEVA_ERR_NOT_FOUND,
          "old full-load child is invalid");
    weva_document_destroy(document);
    check(live.load() <= warm + 16 * 1024, "document destruction releases handle storage");
    std::printf("handle memory: settled %zu bytes, after 4096 replacements %zu bytes, destroyed %zu bytes (warm %zu)\n",
                settled, after, live.load(), warm);
    std::printf("handle memory: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
