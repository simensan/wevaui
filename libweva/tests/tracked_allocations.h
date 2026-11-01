#pragma once
// Include in one translation unit of a standalone allocation regression.
// All replaceable allocation/deallocation forms share the same header; notably,
// ASan's nothrow allocator must not hand a headerless block to our delete.
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <new>

namespace weva::test_allocations {
inline std::atomic<size_t> live{0};
inline std::atomic<size_t> allocations{0};
struct Header { void* raw; size_t size; };
inline void* allocate(size_t size, size_t alignment, bool may_fail = false) {
    alignment = std::max(alignment, alignof(Header));
    if (alignment > SIZE_MAX - sizeof(Header) || size > SIZE_MAX - alignment - sizeof(Header)) {
        if (may_fail) return nullptr;
        std::abort();
    }
    void* raw = std::malloc(size + alignment + sizeof(Header));
    if (!raw) {
        if (may_fail) return nullptr;
        std::abort();
    }
    const auto address = (reinterpret_cast<uintptr_t>(raw) + sizeof(Header) + alignment - 1) & ~(alignment - 1);
    auto* header = reinterpret_cast<Header*>(address) - 1;
    header->raw = raw;
    header->size = size;
    live.fetch_add(size, std::memory_order_relaxed);
    allocations.fetch_add(1, std::memory_order_relaxed);
    return reinterpret_cast<void*>(address);
}
inline void release(void* pointer) noexcept {
    if (!pointer) return;
    const auto* header = static_cast<Header*>(pointer) - 1;
    live.fetch_sub(header->size, std::memory_order_relaxed);
    std::free(header->raw);
}
} // namespace weva::test_allocations

void* operator new(size_t n) { return weva::test_allocations::allocate(n, alignof(std::max_align_t)); }
void* operator new[](size_t n) { return ::operator new(n); }
void* operator new(size_t n, std::align_val_t a) { return weva::test_allocations::allocate(n, static_cast<size_t>(a)); }
void* operator new[](size_t n, std::align_val_t a) { return ::operator new(n, a); }
void* operator new(size_t n, const std::nothrow_t&) noexcept { return weva::test_allocations::allocate(n, alignof(std::max_align_t), true); }
void* operator new[](size_t n, const std::nothrow_t& t) noexcept { return ::operator new(n, t); }
void* operator new(size_t n, std::align_val_t a, const std::nothrow_t&) noexcept { return weva::test_allocations::allocate(n, static_cast<size_t>(a), true); }
void* operator new[](size_t n, std::align_val_t a, const std::nothrow_t& t) noexcept { return ::operator new(n, a, t); }
void operator delete(void* p) noexcept { weva::test_allocations::release(p); }
void operator delete[](void* p) noexcept { ::operator delete(p); }
void operator delete(void* p, size_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, size_t) noexcept { ::operator delete(p); }
void operator delete(void* p, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete(void* p, size_t, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete[](void* p, size_t, std::align_val_t) noexcept { ::operator delete(p); }
void operator delete(void* p, const std::nothrow_t&) noexcept { ::operator delete(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { ::operator delete(p); }
void operator delete(void* p, std::align_val_t, const std::nothrow_t&) noexcept { ::operator delete(p); }
void operator delete[](void* p, std::align_val_t, const std::nothrow_t&) noexcept { ::operator delete(p); }
