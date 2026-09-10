#include "weva/box.h"

namespace weva {

BoxId BoxTree::create(BoxKind kind, const Element* element, const ComputedStyle* style) {
    const BoxId id = free_boxes_.empty() ? static_cast<BoxId>(boxes_.size()) : free_boxes_.back();
    Box b;
    b.kind = kind;
    b.element = element;
    b.style = style;
    // An anonymous block wrapper exists precisely to hold inline content, so
    // the invariant is established at creation rather than by every caller.
    if (kind == BoxKind::AnonymousBlock) b.contains_inlines = true;
    if (free_boxes_.empty()) boxes_.push_back(b);
    else { free_boxes_.pop_back(); boxes_[static_cast<size_t>(id)] = b; }
    return id;
}

void BoxTree::release_subtree(BoxId root) {
    if (!table_borders_.empty()) table_borders_.erase(root);
    for (BoxId c = (*this)[root].first_child; c != kNoBox;) {
        const BoxId next = (*this)[c].next_sibling;
        release_subtree(c);
        c = next;
    }
    imported_text_.erase(root);
    (*this)[root] = Box{};
    free_boxes_.push_back(root);
}

void BoxTree::import_subtree(BoxId into, const BoxTree& source, BoxId from) {
    if (const auto* borders = source.table_borders(from)) table_borders_[into] = *borders;
    else if (!table_borders_.empty()) table_borders_.erase(into);
    (*this)[into] = source[from];
    Box& b = (*this)[into];
    b.parent = b.first_child = b.last_child = b.prev_sibling = b.next_sibling = kNoBox;
    // A textarea's caret uses offsets into its DOM text. Keep those views;
    // only transformed/collapsed text owned by scratch needs transferring.
    bool dom_text = false;
    if (b.source_node) {
        const auto& source_text = b.source_node->data();
        const uintptr_t start = reinterpret_cast<uintptr_t>(source_text.data());
        const uintptr_t run = reinterpret_cast<uintptr_t>(b.text.data());
        dom_text = run >= start && run + b.text.size() <= start + source_text.size();
    }
    if (b.source_control) {
        const auto source_text = b.source_control->form_edit_value();
        const uintptr_t start = reinterpret_cast<uintptr_t>(source_text.data());
        const uintptr_t run = reinterpret_cast<uintptr_t>(b.text.data());
        dom_text = run >= start && run + b.text.size() <= start + source_text.size();
    }
    if (!b.text.empty() && !dom_text) {
        imported_text_[into] = std::string(b.text);
        b.text = imported_text_[into];
    }
    for (BoxId c : source.children(from)) {
        if (source[c].retained_from != kNoBox) {
            const BoxId child = source[c].retained_from;
            const Box prior = (*this)[child];
            (*this)[child] = source[c];
            (*this)[child].retained_from = kNoBox;
            (*this)[child].first_child = prior.first_child;
            (*this)[child].last_child = prior.last_child;
            (*this)[child].parent = (*this)[child].prev_sibling = (*this)[child].next_sibling = kNoBox;
            append_child(into, child);
            continue;
        }
        const BoxId child = create(source[c].kind);
        import_subtree(child, source, c);
        append_child(into, child);
    }
}

void BoxTree::replace_subtree(BoxId root, const BoxTree& source, BoxId source_root) {
    // Retained descendants must be detached before releasing their old
    // ancestors. Their IDs, children and owned text stay in this arena.
    const auto detach_retained = [&](const auto& self, BoxId id) -> void {
        if (source[id].retained_from != kNoBox) {
            remove_child(source[id].retained_from);
            return;
        }
        for (BoxId c : source.children(id)) self(self, c);
    };
    detach_retained(detach_retained, source_root);
    const Box old = (*this)[root];
    for (BoxId c = old.first_child; c != kNoBox;) {
        const BoxId next = (*this)[c].next_sibling;
        release_subtree(c);
        c = next;
    }
    imported_text_.erase(root);
    import_subtree(root, source, source_root);
    (*this)[root].parent = old.parent;
    (*this)[root].prev_sibling = old.prev_sibling;
    (*this)[root].next_sibling = old.next_sibling;
}

void BoxTree::append_child(BoxId parent, BoxId child) {
    if (!valid(parent) || !valid(child) || parent == child) return;
    // A child that is still attached somewhere else must be unlinked first, or
    // its old parent's chain runs through a box that now lives under a
    // different one — and clearing either chain then walks into the other.
    // Inline layout moves an inline-block atom from its block container onto a
    // line box exactly this way.
    if (boxes_[static_cast<size_t>(child)].parent != kNoBox) remove_child(child);
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
    if (boxes_[static_cast<size_t>(child)].parent != kNoBox) remove_child(child);
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
    // As in append_child: unlink an already-attached replacement first.
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


namespace {

// Sorted by first character to keep the common cases (block, inline, flex,
// grid, none) at the front of their chains; the whole thing is a handful of
// compares either way.
struct DisplayEntry { const char* name; DisplayKind kind; };
constexpr DisplayEntry kDisplays[] = {
    {"none", DisplayKind::None},
    {"contents", DisplayKind::Contents},
    {"inline", DisplayKind::Inline},
    {"block", DisplayKind::Block},
    {"flow-root", DisplayKind::FlowRoot},
    {"list-item", DisplayKind::ListItem},
    {"flex", DisplayKind::Flex},
    {"grid", DisplayKind::Grid},
    {"table", DisplayKind::Table},
    {"table-row-group", DisplayKind::TableRowGroup},
    {"table-header-group", DisplayKind::TableHeaderGroup},
    {"table-footer-group", DisplayKind::TableFooterGroup},
    {"table-row", DisplayKind::TableRow},
    {"table-cell", DisplayKind::TableCell},
    {"table-caption", DisplayKind::TableCaption},
    {"table-column", DisplayKind::TableColumn},
    {"table-column-group", DisplayKind::TableColumnGroup},
    {"inline-block", DisplayKind::InlineBlock},
    {"inline-flex", DisplayKind::InlineFlex},
    {"inline-grid", DisplayKind::InlineGrid},
    {"inline-table", DisplayKind::InlineTable},
    // The legacy flexbox, which survives for one reason: it is the only
    // context Chrome applies `-webkit-line-clamp` in, and clamping a card
    // description to two lines is written that way in every stylesheet that
    // does it. With `-webkit-box-orient: vertical` -- the only orientation a
    // clamp is ever used with -- it stacks its children like a block, so a
    // block is what it becomes here. Treated as an unknown display it fell
    // back to `inline`, and a clamped div came out 490x16 instead of 250x50.
    {"-webkit-box", DisplayKind::Block},
    {"-webkit-inline-box", DisplayKind::InlineBlock},
};

} // namespace

bool try_parse_display(std::string_view value, DisplayKind* out) {
    // Trim and lowercase in place rather than allocating: this runs once per
    // element per build.
    const auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    size_t b = 0, e = value.size();
    while (b < e && ws(value[b])) ++b;
    while (e > b && ws(value[e - 1])) --e;
    value = value.substr(b, e - b);
    if (value.empty() || !out) return false;

    for (const DisplayEntry& d : kDisplays) {
        const std::string_view name(d.name);
        if (name.size() != value.size()) continue;
        bool eq = true;
        for (size_t i = 0; i < name.size(); ++i) {
            char c = value[i];
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            if (c != name[i]) { eq = false; break; }
        }
        if (eq) { *out = d.kind; return true; }
    }
    return false;
}

DisplayKind parse_display(std::string_view value) {
    DisplayKind result;
    if (try_parse_display(value, &result)) return result;
    // An unrecognised display behaves as the initial value rather than
    // suppressing the box — a typo should not delete content.
    return DisplayKind::Inline;
}

const char* display_name(DisplayKind d) {
    for (const DisplayEntry& e : kDisplays) {
        if (e.kind == d) return e.name;
    }
    return "inline";
}

bool is_table_display(DisplayKind d) {
    switch (d) {
        case DisplayKind::Table:
        case DisplayKind::InlineTable:
        case DisplayKind::TableRowGroup:
        case DisplayKind::TableHeaderGroup:
        case DisplayKind::TableFooterGroup:
        case DisplayKind::TableRow:
        case DisplayKind::TableCell:
        case DisplayKind::TableCaption:
        case DisplayKind::TableColumn:
        case DisplayKind::TableColumnGroup:
            return true;
        default:
            return false;
    }
}

} // namespace weva
