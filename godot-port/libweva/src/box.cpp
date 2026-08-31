#include "weva/box.h"

namespace weva {

BoxId BoxTree::create(BoxKind kind, const Element* element, const ComputedStyle* style) {
    const BoxId id = static_cast<BoxId>(boxes_.size());
    Box b;
    b.kind = kind;
    b.element = element;
    b.style = style;
    // An anonymous block wrapper exists precisely to hold inline content, so
    // the invariant is established at creation rather than by every caller.
    if (kind == BoxKind::AnonymousBlock) b.contains_inlines = true;
    boxes_.push_back(b);
    return id;
}

void BoxTree::append_child(BoxId parent, BoxId child) {
    if (!valid(parent) || !valid(child) || parent == child) return;
    Box& p = boxes_[static_cast<size_t>(parent)];
    Box& c = boxes_[static_cast<size_t>(child)];
    c.parent = parent;
    c.next_sibling = kNoBox;
    c.prev_sibling = p.last_child;
    if (p.last_child != kNoBox) {
        boxes_[static_cast<size_t>(p.last_child)].next_sibling = child;
    } else {
        p.first_child = child;
    }
    p.last_child = child;
}

void BoxTree::insert_child_first(BoxId parent, BoxId child) {
    if (!valid(parent) || !valid(child) || parent == child) return;
    Box& p = boxes_[static_cast<size_t>(parent)];
    Box& c = boxes_[static_cast<size_t>(child)];
    c.parent = parent;
    c.prev_sibling = kNoBox;
    c.next_sibling = p.first_child;
    if (p.first_child != kNoBox) {
        boxes_[static_cast<size_t>(p.first_child)].prev_sibling = child;
    } else {
        p.last_child = child;
    }
    p.first_child = child;
}

void BoxTree::remove_child(BoxId child) {
    if (!valid(child)) return;
    Box& c = boxes_[static_cast<size_t>(child)];
    if (c.parent == kNoBox) return;
    Box& p = boxes_[static_cast<size_t>(c.parent)];
    if (c.prev_sibling != kNoBox) {
        boxes_[static_cast<size_t>(c.prev_sibling)].next_sibling = c.next_sibling;
    } else {
        p.first_child = c.next_sibling;
    }
    if (c.next_sibling != kNoBox) {
        boxes_[static_cast<size_t>(c.next_sibling)].prev_sibling = c.prev_sibling;
    } else {
        p.last_child = c.prev_sibling;
    }
    c.parent = kNoBox;
    c.prev_sibling = kNoBox;
    c.next_sibling = kNoBox;
}

void BoxTree::replace_child(BoxId existing, BoxId replacement) {
    if (!valid(existing) || !valid(replacement) || existing == replacement) return;
    const BoxId parent = boxes_[static_cast<size_t>(existing)].parent;
    if (parent == kNoBox) return;

    Box& old = boxes_[static_cast<size_t>(existing)];
    const BoxId prev = old.prev_sibling;
    const BoxId next = old.next_sibling;

    Box& rep = boxes_[static_cast<size_t>(replacement)];
    // The replacement may already be attached somewhere; unlink it first, or
    // its old parent keeps a link to a box that now lives elsewhere.
    if (rep.parent != kNoBox) remove_child(replacement);

    rep.parent = parent;
    rep.prev_sibling = prev;
    rep.next_sibling = next;
    if (prev != kNoBox) boxes_[static_cast<size_t>(prev)].next_sibling = replacement;
    else boxes_[static_cast<size_t>(parent)].first_child = replacement;
    if (next != kNoBox) boxes_[static_cast<size_t>(next)].prev_sibling = replacement;
    else boxes_[static_cast<size_t>(parent)].last_child = replacement;

    Box& detached = boxes_[static_cast<size_t>(existing)];
    detached.parent = kNoBox;
    detached.prev_sibling = kNoBox;
    detached.next_sibling = kNoBox;
}

void BoxTree::clear_children(BoxId parent) {
    if (!valid(parent)) return;
    BoxId c = boxes_[static_cast<size_t>(parent)].first_child;
    while (c != kNoBox) {
        Box& b = boxes_[static_cast<size_t>(c)];
        const BoxId next = b.next_sibling;
        b.parent = kNoBox;
        b.prev_sibling = kNoBox;
        b.next_sibling = kNoBox;
        c = next;
    }
    boxes_[static_cast<size_t>(parent)].first_child = kNoBox;
    boxes_[static_cast<size_t>(parent)].last_child = kNoBox;
}

int BoxTree::child_count(BoxId parent) const {
    int n = 0;
    for (BoxId c : children(parent)) { (void)c; ++n; }
    return n;
}

BoxId BoxTree::child_at(BoxId parent, int index) const {
    if (index < 0) return kNoBox;
    int i = 0;
    for (BoxId c : children(parent)) {
        if (i++ == index) return c;
    }
    return kNoBox;
}

} // namespace weva
