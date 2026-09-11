#include "weva/arena.h"

#include <algorithm>
#include <cstdlib>
#include <cstdio>
#include <new>
#include <type_traits>

namespace weva {

Arena::Arena(std::size_t block_size) : block_size_(block_size) {}

Arena::~Arena() {
    for (Block& b : blocks_) std::free(b.data);
}

void Arena::add_block(std::size_t min_bytes) {
    std::size_t size = std::max(block_size_, min_bytes);
    void* mem = std::malloc(size);
    if (!mem) {
        // Deliberate: allocation failure is not recoverable (see status.h).
        // Threading bad_alloc through every layout call site would cost more
        // than it could ever buy on the platforms we target.
        std::fprintf(stderr, "weva: arena allocation of %zu bytes failed\n", size);
        std::abort();
    }
    blocks_.push_back(Block{static_cast<uint8_t*>(mem), size, 0});
    current_ = blocks_.size() - 1;
}

void* Arena::allocate(std::size_t bytes, std::size_t align) {
    if (blocks_.empty()) add_block(bytes);

    for (;;) {
        Block& b = blocks_[current_];
        std::size_t addr = reinterpret_cast<std::size_t>(b.data + b.used);
        std::size_t pad = (align - (addr % align)) % align;
        if (b.used + pad + bytes <= b.size) {
            uint8_t* out = b.data + b.used + pad;
            b.used += pad + bytes;
            used_total_ += pad + bytes;
            return out;
        }
        // Retained blocks are reused after reset(), so walk forward before
        // allocating a new one — this is what makes the steady state zero-alloc.
        if (current_ + 1 < blocks_.size()) {
            ++current_;
            continue;
        }
        add_block(bytes + align);
    }
}

void Arena::reset() {
    for (Block& b : blocks_) b.used = 0;
    current_ = 0;
    used_total_ = 0;
}

std::size_t Arena::bytes_reserved() const {
    std::size_t n = 0;
    for (const Block& b : blocks_) n += b.size;
    return n;
}

} // namespace weva
