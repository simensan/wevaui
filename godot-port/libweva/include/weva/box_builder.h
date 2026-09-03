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
    // The computed style of `e`'s ::before / ::after (`name` without colons),
    // or null when no rule targets that pseudo on that element — which is
    // "no box", not "an empty box". The default generates no pseudo boxes,
    // so providers that never cascade pseudos need not know about them.
    virtual const ComputedStyle* pseudo_style_of(const Element& /*e*/,
                                                 std::string_view /*name*/) {
        return nullptr;
    }
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
    // CSS 2.1 §12.1: generates `host`'s ::before or ::after box as a child of
    // `parent`, when the pseudo has a style and its `content` resolves to
    // something other than none/normal.
    // The `::backdrop` behind a modal dialog or an open popover.
    void maybe_inject_backdrop(const Element& host, BoxId parent);
    void inject_pseudo(const Element& host, const ComputedStyle* host_style, BoxId parent,
                       std::string_view name);

    // CSS 2.1 §12.4 counters and Generated Content §3 quotes, tracked in the
    // document-order walk the builder already makes (ports
    // Css/Cascade/CounterContext.cs). A counter-reset opens a scope that
    // lives while its element's subtree is built; counter-increment / -set
    // act on the innermost scope of that name, creating one when none is
    // open. Quote depth counts open-quote / close-quote in generated text.
    struct CounterScope {
        std::string name;
        int value = 0;
        int depth = 0;   // the creating element's depth: popped when it closes
    };
    void apply_counters(const ComputedStyle* style, int depth);
    void close_counters(int depth);
    // `content` of a pseudo as text, with counter()/counters()/attr()/quotes
    // resolved against the current state. False for none/normal.
    bool resolve_content(const ComputedStyle* pseudo_style, const Element& host, std::string* out);
    std::vector<CounterScope> counters_;
    int element_depth_ = 0;
    int quote_depth_ = 0;
    void finalize_block_children(BoxId parent);
    std::string_view transformed_text(std::string_view text, const ComputedStyle* style);
    // §9.2.1.1: breaks an inline box around the in-flow blocks it holds. `out`
    // receives the pieces and the blocks in order; the first piece is the box
    // itself, later ones clones carrying its element and style.
    void split_inline_around_blocks(BoxId inline_box, std::vector<BoxId>* out);
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
