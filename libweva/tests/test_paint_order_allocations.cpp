// Paint and hit testing must retain identical order when the sibling list is
// traversed directly. Count allocations in a separate executable so the test
// runner's bookkeeping cannot hide an accidental per-container allocation.
#include "weva/positioning.h"
#include "weva/hit_test.h"
#include "weva/dom.h"
#include "weva_c.h"
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <numeric>
#include <string>

namespace {
bool counting = false;
size_t allocations = 0;
int checks = 0, failures = 0;
void check(bool pass, const char* label) {
    ++checks;
    if (!pass && failures++ < 20) std::printf("FAIL %s\n", label);
}
template<class F> size_t measure(F call) {
    allocations = 0;
    counting = true;
    call();
    counting = false;
    return allocations;
}
}
void* operator new(size_t size, const std::nothrow_t&) noexcept {
    if (counting) ++allocations;
    return std::malloc(size ? size : 1);
}
void* operator new(size_t size) {
    if (void* p = ::operator new(size, std::nothrow)) return p;
    std::abort();
}
// The frozen stable_sort reference allocates with nothrow new on libstdc++.
// Keep that allocation paired with these malloc/free replacements under ASan.
void* operator new[](size_t size, const std::nothrow_t&) noexcept { return ::operator new(size, std::nothrow); }
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }
void operator delete(void* p, const std::nothrow_t&) noexcept { std::free(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { std::free(p); }

int main() {
    using namespace weva;
    // These ranks express the expected stacking groups for deliberately mixed
    // kinds, null styles, static z-index, positioned auto/zero and tied z-index.
    const int block_rank[] = {10,20,0,10,30,0,10,20,10,10,10,10};
    const int item_rank[]  = {10,20,0,40,30,0,10,20,5,10,10,40};
    const char* z[] = {"auto","auto","-2","4","1","-2","0","0","-1","9","-100","4"};
    const bool positioned[] = {false,true,true,false,true,true,false,true,false,true,true,false};
    constexpr size_t n = 12;
    uint32_t random = 0x57a31e9u;
    for (DisplayKind display : {DisplayKind::Block, DisplayKind::Flex, DisplayKind::InlineFlex,
                                DisplayKind::Grid, DisplayKind::InlineGrid}) {
        BoxTree tree;
        std::array<ComputedStyle, n> styles;
        const BoxId root = tree.create(BoxKind::Block);
        tree[root].display = display;
        std::array<BoxId, n> ids;
        for (size_t i = 0; i < n; ++i) {
            styles[i].set("z-index", z[i]);
            ids[i] = tree.create(i == 9 ? BoxKind::Inline : BoxKind::Block,
                                nullptr, i == 10 ? nullptr : &styles[i]);
            tree[ids[i]].width = tree[ids[i]].height = 100;
            tree[ids[i]].position = positioned[i] ? PositionType::Absolute : PositionType::Static;
            if (positioned[i] && i != 1) tree[ids[i]].z_index = std::atoi(z[i]);
        }
        std::array<int, n> sequence;
        std::iota(sequence.begin(), sequence.end(), 0);
        const int* ranks = display == DisplayKind::Block ? block_rank : item_rank;
        for (int run = 0; run < 512; ++run) {
            // Reuse the boxes, but reorder the sibling links on every walk.
            for (size_t i = n - 1; i > 0; --i) {
                random = random * 1664525u + 1013904223u;
                std::swap(sequence[i], sequence[random % (i + 1)]);
            }
            tree.clear_children(root);
            for (int i : sequence) tree.append_child(root, ids[i]);
            auto expected = sequence;
            std::stable_sort(expected.begin(), expected.end(), [&](int a, int b) { return ranks[a] < ranks[b]; });
            const bool direct = expected == sequence;
            const size_t count = measure([&] {
                const ChildPaintOrder order(tree, root);
                check(order.size() == n, "child count");
                size_t i = 0;
                for (BoxId id : order) {
                    check(i < n && id == ids[expected[i]], "forward stacking order");
                    ++i;
                }
                check(i == n, "forward traversal length");
                i = n;
                for (auto it = order.rbegin(); it != order.rend(); ++it) {
                    check(i > 0 && *it == ids[expected[i - 1]], "reverse stacking order");
                    if (i) --i;
                }
                check(i == 0, "reverse traversal length");
            });
            check(count == (direct ? 0u : 1u), "one allocation only when sorting is needed (positive control)");
            check(box_at_point(tree, root, 50, 50) == ids[expected.back()], "hit follows top painted child");
            std::vector<BoxId> compatibility;
            paint_order_children(tree, root, &compatibility);
            for (size_t i = 0; i < n; ++i) check(compatibility[i] == ids[expected[i]], "vector API order");
        }
        // Every stacking category can use the direct path if already ordered.
        std::stable_sort(sequence.begin(), sequence.end(), [&](int a, int b) { return ranks[a] < ranks[b]; });
        tree.clear_children(root);
        for (int i : sequence) tree.append_child(root, ids[i]);
        check(measure([&] {
            const ChildPaintOrder order(tree, root);
            size_t i = 0;
            for (BoxId id : order) check(id == ids[sequence[i++]], "ordered mixed siblings");
        }) == 0, "ordered mixed siblings allocate nothing");
    }
    for (int n_children : {0, 1, 2, 16, 256, 4096}) {
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block);
        for (int i = 0; i < n_children; ++i) {
            const BoxId child = tree.create(BoxKind::Block);
            tree[child].width = tree[child].height = 100;
            tree.append_child(root, child);
        }
        check(measure([&] {
            const ChildPaintOrder order(tree, root);
            check(order.size() == n_children, "ordinary child count");
            int i = 1;
            for (BoxId id : order) check(id == i++, "ordinary forward walk");
            i = n_children;
            for (auto it = order.rbegin(); it != order.rend(); ++it) check(*it == i--, "ordinary reverse walk");
            check(i == 0, "ordinary reverse length");
            const BoxId hit = box_at_point(tree, root, 50, 50);
            check(hit == (n_children ? n_children : kNoBox), "ordinary hit target");
        }) == 0, "ordinary paint and hit walk allocate nothing");
        for (BoxId invalid : {kNoBox, tree.size()}) {
            const ChildPaintOrder empty(tree, invalid);
            check(empty.size() == 0 && !(empty.begin() != empty.end()) && !(empty.rbegin() != empty.rend()), "invalid parent is empty");
        }
    }
    {
        BoxTree tree;
        ComputedStyle style;
        Element root_element("div");
        style.set("overflow-x","hidden"); style.set("overflow-y","hidden");
        style.set("border-top-left-radius","30px");
        const BoxId root = tree.create(BoxKind::Block,&root_element,&style);
        tree[root].width = tree[root].height = 100;
        tree[root].position = PositionType::Relative;
        const BoxId child = tree.create(BoxKind::Block);
        tree[child].width = tree[child].height = 40;
        tree[child].position = PositionType::Absolute;
        tree.append_child(root,child);
        check(box_at_point(tree,root,1,1) == kNoBox,"rounded corner rejects captured absolute child");
        check(measure([&] {
            for (int i=0;i<100;++i) check(box_at_point(tree,root,1,1) == kNoBox,"rounded corner remains outside");
        }) == 0,"rejected rounded-clip hit allocates no clip history");
    }
    {
        // A single overlay must not disable replay for the other 39 cells.
        // The initial whole-table fallback took 119 allocations here; keep a
        // generous bound around selective replay and require idle to stay free.
        std::string html = "<table>";
        for (int i = 0; i < 20; ++i) html += i == 0
            ? "<tr><td><div id=target></div></td><td><div></div></td></tr>"
            : "<tr><td><div></div></td><td><div></div></td></tr>";
        html += "</table><div id=after></div>";
        const std::string css = "html,body{margin:0}table{width:400px;table-layout:fixed;border-collapse:collapse}"
            "td{padding:0;border:4px solid red}td>div{height:20px;background:blue}"
            "#target{position:relative;width:240px}#after{height:20px;background:green}";
        weva_config cfg{};
        cfg.viewport_width = 1280; cfg.viewport_height = 720; cfg.use_user_agent_stylesheet = 1;
        const auto doc = weva_document_create(&cfg);
        check(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK, "table paint fixture loads");
        check(weva_document_set_css(doc, css.data(), css.size()) == WEVA_OK, "table paint fixture styles");
        const auto target = weva_document_query(doc, "#target");
        const auto after = weva_document_query(doc, "#after");
        for (int i = 0; i < 20; ++i) {
            weva_element_set_attribute(doc, target, "style", i % 2 ? "background:#123457" : "background:#123456");
            weva_document_update(doc, 0);
        }
        const size_t changed = measure([&] {
            weva_element_set_attribute(doc, target, "style", "background:#123456");
            weva_document_update(doc, 0);
        });
        check(changed <= 80, "one table overlay retains unaffected cell paint ranges");
        check(measure([&] { weva_document_update(doc, 0); }) == 0, "positioned table idle allocates nothing");
        const size_t unrelated = measure([&] {
            weva_element_set_attribute(doc, after, "style", "background:#123456");
            weva_document_update(doc, 0);
        });
        check(unrelated <= 30, "unrelated update retains complete positioned table range");
        std::printf("positioned table allocations: changed %zu, unrelated %zu\n", changed, unrelated);
        weva_document_destroy(doc);
    }
    std::printf("paint order: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
