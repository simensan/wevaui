#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace weva {

// Per-pass bump allocator.
//
// This is the direct replacement for the C# side's BoxPool, PaintListPool,
// ArrayPool rentals and CssValuePool — all of which exist to approximate an
// arena inside a GC. The C# steady state is 1.42 MB/call for layout and
// 1.10-2.19 MB/call for paint against a stated target of zero; here the target
// is reachable, so it is a regression gate rather than an aspiration.
//
// Lifetime: everything allocated from an Arena dies at reset(). Nothing may
// outlive the pass that allocated it. Boxes and paint commands are therefore
// referenced by stable index, never by pointer (see docs/CONVENTIONS.md) —
// with an arena, a retained pointer is a use-after-free waiting for the next
// frame.
class Arena {
public:
    explicit Arena(std::size_t block_size = 64 * 1024);
    ~Arena();

    Arena(const Arena&) = delete;
    Arena& operator=(const Arena&) = delete;

    void* allocate(std::size_t bytes, std::size_t align = alignof(std::max_align_t));

    template <typename T>
    T* alloc_array(std::size_t n) {
        static_assert(std::is_trivially_destructible<T>::value,
                      "Arena never runs destructors; store trivially destructible types only.");
        return static_cast<T*>(allocate(sizeof(T) * n, alignof(T)));
    }

    // Frees nothing to the OS — blocks are retained and reused, so a steady
    // state reaches zero allocations per frame once the high-water mark is hit.
    void reset();

    std::size_t bytes_used() const { return used_total_; }
    std::size_t bytes_reserved() const;
    std::size_t block_count() const { return blocks_.size(); }

private:
    struct Block {
        uint8_t* data;
        std::size_t size;
        std::size_t used;
    };

    void add_block(std::size_t min_bytes);

    std::vector<Block> blocks_;
    std::size_t current_ = 0;
    std::size_t block_size_;
    std::size_t used_total_ = 0;
};

} // namespace weva
