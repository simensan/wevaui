#include "weva/box_builder.h"

#include "weva/css_properties.h"

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

bool is_non_auto(std::string_view v) {
    return !v.empty() && v != "auto";
}

// CSS Multi-column L1 §2: a block container becomes a multicol container when
// column-count or column-width is non-auto. Flex, grid and table containers
// ignore the column properties.
bool is_multicol_container(const ComputedStyle* style) {
    if (!style) return false;
    return is_non_auto(get(style, "column-count")) || is_non_auto(get(style, "column-width"));
}

bool equals_ignoring_case(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

bool is_out_of_flow_position(const ComputedStyle* style) {
    const std::string_view p = get(style, "position");
    return equals_ignoring_case(p, "absolute") || equals_ignoring_case(p, "fixed");
}

bool is_floated(const ComputedStyle* style) {
    const std::string_view f = get(style, "float");
    return !f.empty() && !equals_ignoring_case(f, "none");
}

bool is_whitespace_only(std::string_view s) {
    for (char c : s) {
        if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') return false;
    }
    return true;
}

// A flex or grid container blockifies its in-flow children (CSS Flexbox §4,
// Grid §6): an inline child becomes a block-level item. Inline-flex and
// inline-grid containers do too — the inline-ness is about the OUTER display.
bool blockifies_children(DisplayKind d) {
    return d == DisplayKind::Flex || d == DisplayKind::InlineFlex ||
           d == DisplayKind::Grid || d == DisplayKind::InlineGrid;
}

DisplayKind blockified(DisplayKind d) {
    switch (d) {
        case DisplayKind::InlineBlock: return DisplayKind::Block;
        case DisplayKind::InlineFlex:  return DisplayKind::Flex;
        case DisplayKind::InlineGrid:  return DisplayKind::Grid;
        case DisplayKind::InlineTable: return DisplayKind::Table;
        default: return d;
    }
}

bool establishes_block_box(DisplayKind d) {
    return d == DisplayKind::Block || d == DisplayKind::Flex || d == DisplayKind::Grid ||
           d == DisplayKind::FlowRoot || d == DisplayKind::ListItem;
}

bool is_inline_level_block(DisplayKind d) {
    return d == DisplayKind::InlineBlock || d == DisplayKind::InlineFlex ||
           d == DisplayKind::InlineGrid || d == DisplayKind::InlineTable;
}

} // namespace

BoxId BoxBuilder::new_block_box_for(DisplayKind display, const Element* e,
                                    const ComputedStyle* style) {
    const BoxId id = tree_->create(BoxKind::Block, e, style);
    Box& b = (*tree_)[id];
    b.display = display;
    // The inline-* displays are block containers whose OUTER display is inline:
    // they lay out their contents as a block/flex/grid/table but participate in
    // the parent's inline formatting context.
    b.is_inline_block = is_inline_level_block(display);
    if ((display == DisplayKind::Block || display == DisplayKind::FlowRoot ||
         display == DisplayKind::ListItem) &&
        is_multicol_container(style)) {
        b.is_multicol = true;
    }
    return id;
}

BoxId BoxBuilder::build(const Element& root, const ComputedStyle* root_style) {
    const DisplayKind display = parse_display(get(root_style, "display"));
    if (display == DisplayKind::None) return kNoBox;

    const BoxId id = new_block_box_for(display, &root, root_style);
    build_children(root, root_style, id);
    return id;
}

BoxId BoxBuilder::build_document(const Document& doc) {
    // Neither element nor style: this stands in for the initial containing
    // block, not for `<html>`.
    const BoxId root = tree_->create(BoxKind::Block, nullptr, nullptr);
    for (const Ref<Node>& child : doc.children()) {
        append_node_as_block_child(*child, nullptr, root);
    }
    finalize_block_children(root);
    return root;
}

void BoxBuilder::append_node_as_block_child(const Node& node, const ComputedStyle* parent_style,
                                            BoxId parent) {
    if (node.node_type() == NodeType::Text) {
        const auto& tn = static_cast<const TextNode&>(node);
        const BoxId id = tree_->create(BoxKind::Text, (*tree_)[parent].element, parent_style);
        Box& b = (*tree_)[id];
        b.text = tn.data();
        b.source_node = &tn;
        tree_->append_child(parent, id);
        return;
    }
    if (node.node_type() != NodeType::Element) return;

    const auto& e = static_cast<const Element&>(node);
    const ComputedStyle* style = styles_ ? styles_->style_of(e) : nullptr;
    DisplayKind disp = parse_display(get(style, "display"));
    if (disp == DisplayKind::None) return;

    const bool blockify = blockifies_children((*tree_)[parent].display);

    // CSS 2.1 §9.7: an out-of-flow or floated element with an inline outer
    // display is blockified. Authors routinely write
    // `<span style="float:left">` and expect block-flow semantics; without
    // this the box would be an inline box and block layout would never see it
    // as a float.
    //
    // Excluded inside a flex or grid container, whose items cannot float and
    // whose children are blockified below anyway (CSS Flexbox §3, Grid §6.4).
    if (!blockify && disp == DisplayKind::Inline) {
        if (is_out_of_flow_position(style) || is_floated(style)) disp = DisplayKind::Block;
    }

    if (establishes_block_box(disp) || is_inline_level_block(disp) || is_table_display(disp)) {
        // A flex or grid container forces its children's OUTER display to
        // block. Without this, the anonymous-block pass sees the is_inline_block
        // flag and sweeps consecutive items into ONE anonymous wrapper, so a
        // row of flex items becomes a single item and per-item sizing is never
        // applied to any of them.
        const DisplayKind used = blockify ? blockified(disp) : disp;
        const BoxId bb = new_block_box_for(used, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    if (disp == DisplayKind::Contents) {
        // The element generates no box; its children take its place, styled by
        // it for inheritance purposes.
        for (const Ref<Node>& c : e.children()) append_node_as_block_child(*c, style, parent);
        return;
    }

    if (blockify) {
        // `display: inline` inside a flex or grid container becomes a
        // block-level item.
        const BoxId bb = new_block_box_for(DisplayKind::Block, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    const BoxId ib = tree_->create(BoxKind::Inline, &e, style);
    (*tree_)[ib].display = disp;
    build_inline_children(e, style, ib);
    tree_->append_child(parent, ib);
}

void BoxBuilder::build_children(const Element& element, const ComputedStyle* style,
                                BoxId parent) {
    for (const Ref<Node>& c : element.children()) {
        append_node_as_block_child(*c, style, parent);
    }
    finalize_block_children(parent);
}

void BoxBuilder::build_inline_children(const Element& element, const ComputedStyle* style,
                                       BoxId parent) {
    for (const Ref<Node>& c : element.children()) {
        append_inline_child(*c, style, parent);
    }
}

void BoxBuilder::append_inline_child(const Node& node, const ComputedStyle* parent_style,
                                     BoxId parent) {
    if (node.node_type() == NodeType::Text) {
        const auto& tn = static_cast<const TextNode&>(node);
        const BoxId id = tree_->create(BoxKind::Text, (*tree_)[parent].element, parent_style);
        Box& b = (*tree_)[id];
        b.text = tn.data();
        b.source_node = &tn;
        tree_->append_child(parent, id);
        return;
    }
    if (node.node_type() != NodeType::Element) return;

    const auto& e = static_cast<const Element&>(node);
    const ComputedStyle* style = styles_ ? styles_->style_of(e) : nullptr;
    DisplayKind disp = parse_display(get(style, "display"));
    if (disp == DisplayKind::None) return;

    if (disp == DisplayKind::Contents) {
        for (const Ref<Node>& c : e.children()) append_inline_child(*c, style, parent);
        return;
    }

    // Same §9.7 blockification as the block path: a float nested inside an
    // inline box must still reach float layout rather than being folded into
    // the inline stream.
    if (disp == DisplayKind::Inline) {
        if (is_out_of_flow_position(style) || is_floated(style)) disp = DisplayKind::Block;
    }

    if (establishes_block_box(disp) || is_inline_level_block(disp) || is_table_display(disp)) {
        const BoxId bb = new_block_box_for(disp, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    const BoxId ib = tree_->create(BoxKind::Inline, &e, style);
    (*tree_)[ib].display = disp;
    build_inline_children(e, style, ib);
    tree_->append_child(parent, ib);
}

void BoxBuilder::flush_anonymous(BoxId parent, std::vector<BoxId>* inlines) {
    // A run that is nothing but whitespace generates no box. This is what stops
    // the newlines between block-level siblings in formatted HTML from
    // producing an empty anonymous block between every pair of them.
    bool all_whitespace = true;
    for (BoxId id : *inlines) {
        const Box& b = (*tree_)[id];
        if (b.kind != BoxKind::Text || !is_whitespace_only(b.text)) {
            all_whitespace = false;
            break;
        }
    }
    if (all_whitespace) return;

    const BoxId anon = tree_->create(BoxKind::AnonymousBlock, nullptr, nullptr);
    for (BoxId id : *inlines) tree_->append_child(anon, id);
    tree_->append_child(parent, anon);
}

void BoxBuilder::finalize_block_children(BoxId parent) {
    // CSS 2.1 §9.2.1.1: a block container holds either only inline-level boxes
    // or only block-level ones. Where an author mixes them, each run of
    // consecutive inline children is wrapped in an anonymous block, so every
    // layout pass below can assume one case or the other.
    if ((*tree_)[parent].first_child == kNoBox) {
        (*tree_)[parent].contains_inlines = false;
        return;
    }

    const auto is_block_level = [this](BoxId id) {
        const Box& b = (*tree_)[id];
        // An anonymous block is deliberately excluded: it is a product of this
        // pass, never an input to the classification.
        return b.kind == BoxKind::Block && !b.is_inline_block;
    };
    const auto is_inline_level = [this](BoxId id) {
        const Box& b = (*tree_)[id];
        return b.kind == BoxKind::Inline || b.kind == BoxKind::AnonymousInline ||
               b.kind == BoxKind::Text || (b.kind == BoxKind::Block && b.is_inline_block);
    };

    bool any_block = false;
    bool any_inline = false;
    for (BoxId c : tree_->children(parent)) {
        if (is_block_level(c)) any_block = true;
        else if (is_inline_level(c)) any_inline = true;
    }

    if (!any_block) {
        // CSS Flexbox §4 / Grid §6: text directly inside a flex or grid
        // container is wrapped in an anonymous item. Element children were
        // blockified on the way in; raw text bypasses that branch, and without
        // the wrap the container sees zero items and collapses to its padding.
        if (any_inline && blockifies_children((*tree_)[parent].display)) {
            existing_.clear();
            for (BoxId c : tree_->children(parent)) existing_.push_back(c);
            tree_->clear_children(parent);
            flush_anonymous(parent, &existing_);
            existing_.clear();
            (*tree_)[parent].contains_inlines = false;
            return;
        }
        (*tree_)[parent].contains_inlines = true;
        return;
    }
    if (!any_inline) {
        (*tree_)[parent].contains_inlines = false;
        return;
    }

    existing_.clear();
    for (BoxId c : tree_->children(parent)) existing_.push_back(c);
    tree_->clear_children(parent);

    current_inlines_.clear();
    for (BoxId c : existing_) {
        if (is_block_level(c)) {
            if (!current_inlines_.empty()) {
                flush_anonymous(parent, &current_inlines_);
                current_inlines_.clear();
            }
            tree_->append_child(parent, c);
        } else {
            current_inlines_.push_back(c);
        }
    }
    if (!current_inlines_.empty()) {
        flush_anonymous(parent, &current_inlines_);
        current_inlines_.clear();
    }
    existing_.clear();
    (*tree_)[parent].contains_inlines = false;
}

} // namespace weva
