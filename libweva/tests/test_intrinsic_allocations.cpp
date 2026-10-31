#include "weva/inline_layout.h"
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
    ComputedStyle style;
    style.set("width", "10px");
    LayoutContext ctx;
    size_t local_bytes = 0;
    for (int count : {32, 4096}) {
        BoxTree tree;
        tree.reserve(count);
        for (int i = 0; i < count; ++i) {
            const auto id = tree.create(BoxKind::Block, nullptr, &style);
            tree[id].width = i + 1;
        }
        // Warm registry/static initialization outside the measured scope.
        block_intrinsic_contributions(tree, count - 1, &ctx);
        allocations = bytes = 0;
        counting = true;
        bool correct = true;
        {
            const IntrinsicContributionScope memo(tree, &ctx);
            for (int repeat = 0; repeat < 4; ++repeat)
                for (BoxId id : {BoxId{0}, BoxId{count / 2}, BoxId{count - 1}}) {
                    const auto value = block_intrinsic_contributions(tree, id, &ctx);
                    correct = correct && value.first == id + 1 && value.second == id + 1;
                }
        }
        counting = false;
        check(correct, "sparse contributions match box widths");
        check(allocations <= 1 && bytes <= 4096, "local scope has bounded small storage");
        if (count == 32) local_bytes = bytes;
        else check(bytes == local_bytes, "local memo allocation does not scale with document size");
        std::printf("local intrinsic scope: %d boxes, %zu allocations, %zu bytes\n", count, allocations, bytes);

        // Populate all three variants past the small-table limit, then hit
        // them in reverse order. Nested scopes must restore the outer memo.
        const IntrinsicContributionScope outer(tree, &ctx);
        for (int round = 0; round < 2; ++round) {
            bool exact = true;
            for (int i = 0; i < count; ++i) {
                const BoxId id = round ? count - i - 1 : i;
                const auto both = block_intrinsic_contributions(tree, id, &ctx);
                exact = exact && both.first == id + 1 && both.second == id + 1 &&
                    block_intrinsic_contribution(tree, id, &ctx, true) == id + 1 &&
                    block_intrinsic_contribution(tree, id, &ctx, false) == id + 1;
                if (i == count / 2) {
                    const IntrinsicContributionScope nested(tree, &ctx);
                    exact = exact && block_intrinsic_contributions(tree, id, &ctx) == both;
                }
            }
            check(exact, "promoted and nested memos preserve all variants");
        }
    }
    // A root miss can recursively fill and promote the table before its own
    // result is inserted. Exercise that transition for all three variants.
    BoxTree nested;
    ComputedStyle automatic;
    BoxId parent = kNoBox;
    for (int i = 0; i < 64; ++i) {
        const BoxId id = nested.create(BoxKind::Block, nullptr, i == 63 ? &style : &automatic);
        nested[id].width = 10;
        if (parent != kNoBox) nested.append_child(parent, id);
        parent = id;
    }
    {
        const IntrinsicContributionScope memo(nested, &ctx);
        const auto both = block_intrinsic_contributions(nested, 0, &ctx);
        check(both.first == 10 && both.second == 10 &&
              block_intrinsic_contribution(nested, 0, &ctx, true) == 10 &&
              block_intrinsic_contribution(nested, 0, &ctx, false) == 10,
              "recursive promotion preserves pending ancestor results");
    }
    std::printf("intrinsic allocations: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
