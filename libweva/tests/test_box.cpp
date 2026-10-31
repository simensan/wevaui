#include "check.h"
#include "weva/box.h"
#include <vector>

using namespace weva;

void test_box_tree_move_owns_text() {
    std::vector<BoxTree> trees;
    // Repeated vector growth used to select a shallow copy: deque's move is
    // not noexcept, so vector preferred the implicit copy constructor. Short
    // strings then dangled into freed deque storage before subtree import.
    for (int i = 0; i < 24; ++i) {
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block);
        for (const std::string& text : {std::string("short"), std::string(100, 'x')}) {
            const BoxId run = tree.create(BoxKind::Text);
            tree[run].text = tree.own_text(text);
            tree.append_child(root, run);
        }
        trees.push_back(std::move(tree));
    }
    for (BoxTree& source : trees) {
        BoxTree moved;
        moved = std::move(source);
        BoxTree destination;
        const BoxId root = destination.create(BoxKind::Block);
        destination.replace_subtree(root, moved, 0);
        moved.reset();
        const BoxId first = destination[root].first_child;
        CHECK(destination[first].text == "short");
        CHECK(destination[destination[first].next_sibling].text == std::string(100, 'x'));
    }
}

namespace {

// The children of `parent`, in order. Every structural test checks this AND the
// reverse chain, because a half-updated link list still walks correctly
// forwards and only shows up much later, from the other direction.
std::vector<BoxId> forward(const BoxTree& t, BoxId parent) {
    std::vector<BoxId> out;
    for (BoxId c : t.children(parent)) out.push_back(c);
    return out;
}

std::vector<BoxId> backward(const BoxTree& t, BoxId parent) {
    std::vector<BoxId> out;
    for (BoxId c = t[parent].last_child; c != kNoBox; c = t[c].prev_sibling) out.push_back(c);
    return out;
}

bool links_consistent(const BoxTree& t, BoxId parent) {
    std::vector<BoxId> f = forward(t, parent);
    std::vector<BoxId> b = backward(t, parent);
    std::vector<BoxId> rb(b.rbegin(), b.rend());
    if (f != rb) return false;
    for (BoxId c : f) {
        if (t[c].parent != parent) return false;
    }
    if (f.empty()) return t[parent].first_child == kNoBox && t[parent].last_child == kNoBox;
    return t[parent].first_child == f.front() && t[parent].last_child == f.back();
}

} // namespace

void test_box_tree() {
    BoxTree t;
    const BoxId root = t.create(BoxKind::Block);
    CHECK(root == 0 && t.valid(root) && t.size() == 1);
    CHECK(!t.valid(kNoBox) && !t.valid(1));

    // An anonymous block wrapper exists to hold inline content, so the
    // invariant holds from creation rather than being set by every caller.
    CHECK(t[t.create(BoxKind::AnonymousBlock)].contains_inlines);
    CHECK(!t[t.create(BoxKind::Block)].contains_inlines);

    // ---- append
    BoxTree u;
    const BoxId p = u.create(BoxKind::Block);
    const BoxId a = u.create(BoxKind::Block);
    const BoxId b = u.create(BoxKind::Block);
    const BoxId c = u.create(BoxKind::Block);
    u.append_child(p, a);
    u.append_child(p, b);
    u.append_child(p, c);
    CHECK(forward(u, p) == std::vector<BoxId>({a, b, c}));
    CHECK(links_consistent(u, p));
    CHECK(u.child_count(p) == 3);
    CHECK(u.child_at(p, 0) == a && u.child_at(p, 2) == c);
    CHECK(u.child_at(p, 3) == kNoBox && u.child_at(p, -1) == kNoBox);

    // ---- insert first
    const BoxId d = u.create(BoxKind::Inline);
    u.insert_child_first(p, d);
    CHECK(forward(u, p) == std::vector<BoxId>({d, a, b, c}));
    CHECK(links_consistent(u, p));

    // A box is never its own child, and neither id may be out of range.
    u.append_child(p, p);
    u.append_child(p, 999);
    u.append_child(999, a);
    CHECK(forward(u, p) == std::vector<BoxId>({d, a, b, c}));
}

void test_box_tree_removal() {
    // Removing the head, a middle child and the tail each update a different
    // pair of links, so all three are checked from both directions.
    for (int which = 0; which < 3; ++which) {
        BoxTree t;
        const BoxId p = t.create(BoxKind::Block);
        const BoxId a = t.create(BoxKind::Block);
        const BoxId b = t.create(BoxKind::Block);
        const BoxId c = t.create(BoxKind::Block);
        t.append_child(p, a);
        t.append_child(p, b);
        t.append_child(p, c);

        const BoxId target = which == 0 ? a : (which == 1 ? b : c);
        t.remove_child(target);
        CHECK(links_consistent(t, p));
        CHECK(t.child_count(p) == 2);
        for (BoxId ch : t.children(p)) CHECK(ch != target);
        // The removed box is detached but still allocated: the arena is reset
        // wholesale, never per box.
        CHECK(t[target].parent == kNoBox);
        CHECK(t[target].prev_sibling == kNoBox && t[target].next_sibling == kNoBox);
        CHECK(t.valid(target));
    }
    {
        // Removing the only child empties both ends.
        BoxTree t;
        const BoxId p = t.create(BoxKind::Block);
        const BoxId a = t.create(BoxKind::Block);
        t.append_child(p, a);
        t.remove_child(a);
        CHECK(links_consistent(t, p) && t.child_count(p) == 0);
        // Removing an unparented box is a no-op, not a corruption.
        t.remove_child(a);
        CHECK(links_consistent(t, p));
    }
    {
        BoxTree t;
        const BoxId p = t.create(BoxKind::Block);
        for (int i = 0; i < 3; ++i) t.append_child(p, t.create(BoxKind::Block));
        t.clear_children(p);
        CHECK(links_consistent(t, p) && t.child_count(p) == 0);
        // Every former child is detached, not just the first.
        for (BoxId i = 1; i <= 3; ++i) CHECK(t[i].parent == kNoBox);
    }
}

void test_box_tree_replace() {
    for (int which = 0; which < 3; ++which) {
        BoxTree t;
        const BoxId p = t.create(BoxKind::Block);
        const BoxId a = t.create(BoxKind::Block);
        const BoxId b = t.create(BoxKind::Block);
        const BoxId c = t.create(BoxKind::Block);
        const BoxId r = t.create(BoxKind::Inline);
        t.append_child(p, a);
        t.append_child(p, b);
        t.append_child(p, c);

        const BoxId target = which == 0 ? a : (which == 1 ? b : c);
        t.replace_child(target, r);
        CHECK(links_consistent(t, p));
        CHECK(t.child_count(p) == 3);
        CHECK(t.child_at(p, which) == r);
        CHECK(t[target].parent == kNoBox);
    }
    {
        // Replacing with a box that is ALREADY attached elsewhere has to unlink
        // it first — otherwise its old parent keeps a link to a box that now
        // lives under a different one, and both chains walk through it.
        BoxTree t;
        const BoxId p1 = t.create(BoxKind::Block);
        const BoxId p2 = t.create(BoxKind::Block);
        const BoxId a = t.create(BoxKind::Block);
        const BoxId x = t.create(BoxKind::Block);
        const BoxId y = t.create(BoxKind::Block);
        t.append_child(p1, a);
        t.append_child(p2, x);
        t.append_child(p2, y);

        t.replace_child(a, x);
        CHECK(links_consistent(t, p1) && links_consistent(t, p2));
        CHECK(forward(t, p1) == std::vector<BoxId>({x}));
        CHECK(forward(t, p2) == std::vector<BoxId>({y}));
    }
    {
        BoxTree t;
        const BoxId a = t.create(BoxKind::Block);
        const BoxId b = t.create(BoxKind::Block);
        // An unparented box has no position to replace.
        t.replace_child(a, b);
        CHECK(t[b].parent == kNoBox);
    }
}

void test_box_tree_replacement_recycles_storage() {
    BoxTree tree, replacement;
    const auto parent=tree.create(BoxKind::Block);
    const auto before=tree.create(BoxKind::Block);
    const auto root=tree.create(BoxKind::Block);
    const auto after=tree.create(BoxKind::Block);
    tree.append_child(parent,before); tree.append_child(parent,root); tree.append_child(parent,after);
    const auto discarded=tree.create(BoxKind::Text);
    const auto retained=tree.create(BoxKind::Block);
    const auto leaf=tree.create(BoxKind::Text);
    tree.append_child(root,discarded); tree.append_child(root,retained); tree.append_child(retained,leaf);
    tree[discarded].text=tree.own_text("discarded"); tree[discarded].width=90;
    tree[discarded].column_sets={{1,2},{3,4}};
    tree[leaf].text=tree.own_text("retained text");
    const auto source=replacement.create(BoxKind::Block);
    const auto keep=replacement.create(BoxKind::Block);
    replacement[keep]=tree[retained];
    replacement[keep].parent=replacement[keep].first_child=replacement[keep].last_child=kNoBox;
    replacement[keep].retained_from=retained;
    replacement[keep].column_sets={{5,6}};
    replacement.append_child(source,keep);
    tree.replace_subtree(root,replacement,source);
    replacement.reset();
    CHECK(links_consistent(tree,parent));
    CHECK((forward(tree,parent)==std::vector<BoxId>{before,root,after}));
    CHECK(links_consistent(tree,root));
    CHECK(tree[root].first_child==retained && tree[root].last_child==retained);
    CHECK(tree[retained].first_child==leaf && tree[retained].last_child==leaf);
    CHECK(tree[leaf].text=="retained text");
    CHECK(tree[retained].column_sets.size()==1 && tree[retained].column_sets[0].first==5);
    const auto recycled=tree.create(BoxKind::AnonymousBlock);
    CHECK(recycled==discarded);
    CHECK(tree[recycled].contains_inlines && tree[recycled].kind==BoxKind::AnonymousBlock);
    CHECK(tree[recycled].text.empty() && tree[recycled].width==0 && tree[recycled].column_sets.empty());
    CHECK(tree[recycled].parent==kNoBox && tree[recycled].first_child==kNoBox);
}
