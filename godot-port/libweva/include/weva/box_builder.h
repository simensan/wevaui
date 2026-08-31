#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include "weva/dom.h"

#include <vector>

// Ports Runtime/Layout/BoxBuilder.cs and Layout/Snapshot/BoxFinalize.cs — DOM
// plus computed style in, box tree out.
//
// Two jobs, and the second is the subtle one:
//   1. Map each element's `display` onto a box, applying the blockification
//      rules that promote an inline element to block-level.
//   2. Give every block container a uniform child list, by wrapping each run of
//      consecutive inline children in an anonymous block box (CSS 2.1 §9.2.1.1).
//      Layout downstream may then assume a container's children are either all
//      inline or all block-level, never mixed.

namespace weva {

// Supplies the cascaded style for an element. The C# passes a
// `Func<Element, ComputedStyle>` so the engine can rebind it per pass without
// reallocating the builder; the same intent here.
class StyleProvider {
public:
    virtual ~StyleProvider() = default;
    // May return null, which the builder treats as "no declarations", i.e. the
    // initial value of every property.
    virtual const ComputedStyle* style_of(const Element& e) = 0;
};

class BoxBuilder {
public:
    BoxBuilder(BoxTree* tree, StyleProvider* styles) : tree_(tree), styles_(styles) {}

    // Builds the box for one element and its subtree. Returns kNoBox for
    // `display: none`.
    BoxId build(const Element& root, const ComputedStyle* root_style);

    // Builds an anonymous root box holding the document's element children.
    // The root box has neither element nor style: it is the initial containing
    // block's stand-in, not a box for `<html>`.
    BoxId build_document(const Document& doc);

private:
    void append_node_as_block_child(const Node& node, const ComputedStyle* parent_style,
                                    BoxId parent);
    void build_children(const Element& element, const ComputedStyle* style, BoxId parent);
    void build_inline_children(const Element& element, const ComputedStyle* style, BoxId parent);
    void append_inline_child(const Node& node, const ComputedStyle* parent_style, BoxId parent);
    BoxId new_block_box_for(DisplayKind display, const Element* e, const ComputedStyle* style);
    void finalize_block_children(BoxId parent);
    void flush_anonymous(BoxId parent, std::vector<BoxId>* inlines);

    BoxTree* tree_;
    StyleProvider* styles_;

    // Scratch for the anonymous-block pass, reused across calls rather than
    // allocated per container.
    //
    // Safe despite the recursion: build_children finalizes a container only
    // after every recursive build_children beneath it has already returned, so
    // no two finalize calls are ever in flight at once. The C# relies on the
    // same argument for its shared LayoutScratch buffers.
    std::vector<BoxId> existing_;
    std::vector<BoxId> current_inlines_;
};

} // namespace weva
