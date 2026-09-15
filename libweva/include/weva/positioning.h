#pragma once
#include "weva/block_layout.h"
#include "weva/box.h"
#include "weva/style_resolver.h"

#include <vector>

namespace weva {

// Ports Runtime/Layout/Positioning — `position: relative | absolute | fixed`.
//
// Runs AFTER block layout, because an out-of-flow box is placed against its
// containing block's final geometry, and that is only known once the in-flow
// pass has sized everything.

struct ContainingBlock {
    BoxId box = kNoBox;
    double x = 0, y = 0, width = 0, height = 0;
    bool is_viewport = false;
};

// The containing block of an absolutely positioned box is the PADDING box of
// its nearest positioned ancestor — inside the border edge, so `inset: 0` on a
// child of a bordered box lands two border-widths smaller.
ContainingBlock resolve_absolute_containing_block(const BoxTree& tree, BoxId box,
                                                  const LayoutContext& ctx);
// `fixed` normally resolves against the viewport, but the same properties that
// capture an absolute box capture a fixed one too: a transform changes how
// viewport coordinates map to local ones, so a transformed ancestor becomes the
// containing block for both.
ContainingBlock resolve_fixed_containing_block(const BoxTree& tree, BoxId box,
                                               const LayoutContext& ctx);

// True when this box establishes a containing block for absolutely positioned
// descendants: it is positioned, OR it has a transform, filter, perspective,
// `will-change` naming one of those, or layout/paint containment. Missing the
// second group is how an `inset: 0` child of `transform: scale(1)` ends up
// filling the viewport instead of its parent.
bool establishes_absolute_containing_block(const Box& b);
bool establishes_fixed_containing_block(const Box& b);
// A positioned descendant escapes overflow between itself and its containing
// block. The containing block itself, and its ancestors, still clip it.
bool overflow_clip_applies(const BoxTree& tree, BoxId clip, BoxId containing_block);
bool subtree_has_overflow_escape(const BoxTree& tree, BoxId root, const LayoutContext& ctx);

// Where a box is DRAWN: the same sum with every ancestor's scroll offset taken
// off. A host asking where an element is on screen wants this one; layout, which
// runs before anything is scrolled, wants the other.
void visual_position(const BoxTree& tree, BoxId box, double* x, double* y);

// Whether a block promoted by block-in-inline splitting contributes a client
// fragment to this inline element. Does not include descendant overflow.
bool is_promoted_inline_fragment(const Box& box, const Element* element);

// The anonymous continuation's rectangle in its parent's coordinates. It
// fills the containing block, independently of the promoted child's width or
// relative/transform offset.
void promoted_inline_rect(const BoxTree& tree, BoxId box,
                          double* x, double* y, double* width, double* height);

// Root-relative origin, summing local offsets up the tree.
void absolute_position(const BoxTree& tree, BoxId box, double* x, double* y);

// How far the laid-out document actually reaches, as the union of every box's
// border box in root-relative coordinates.
//
// This is not the viewport, and on half of the sample corpus it is much taller
// than one: a host that embeds a document needs it to know whether there is
// anything to scroll to, and how far. Never smaller than the viewport, so a
// short page reports the box it was laid out in rather than the ink in it.
void content_size(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                  double* out_width, double* out_height);

// True when this box clips what overflows it, including `overflow: clip`.
bool clips_overflow(const Box& b);

// A scroll container uses hidden/auto/scroll overflow. `clip` alone clips
// paint but cannot scroll or capture a descendant's sticky/snap positioning.
bool establishes_scroll_container(const Box& b);

// True when this axis is `auto` or `scroll` -- the two that a wheel drives and
// that grow a scrollbar. `hidden` clips and can still be scrolled by a script
// (as it can in a browser), but it is not a scroller to the user, and painting
// a bar on every clipped box would put one on half of a page's decoration.
bool scrollable_on_axis(const Box& b, bool vertical);

// How far the contents of one scroll container reach, measured from its
// PADDING box -- CSS Overflow L3 §3, which is what `scrollWidth`/`scrollHeight`
// report. Never smaller than the padding box itself, so a container with room
// to spare reports no room to scroll rather than a negative one.
//
// The walk stops at a nested scroll container: what IT clips is its own
// business, and its border box is the whole of its contribution.
void scrollable_overflow(const BoxTree& tree, BoxId box, double* out_width, double* out_height);

// The furthest this container can be scrolled, in each axis. Zero when its
// contents fit, and never negative.
void max_scroll(const BoxTree& tree, BoxId box, double* out_x, double* out_y);

// Reads `top`/`right`/`bottom`/`left` and `z-index` onto every box. An absent
// offset stays absent — `auto` is not zero, and the two lead to different
// placement.
void stamp_offsets(BoxTree* tree, BoxId root, const LayoutContext& ctx);

// Places every positioned box in the tree. `block` is used to re-lay an
// out-of-flow box's content once its width is known.
void run_positioning(BoxTree* tree, BoxId root, const LayoutContext& ctx,
                     BlockLayout* block);

// The order a container's children are PAINTED in, per CSS 2.1 Appendix E:
// negative z-index stacking contexts, then in-flow children in tree order,
// then positioned children with z-index auto or 0 in tree order, then positive
// z ascending, ties by tree order.
//
// Shared rather than reimplemented because paint and HIT TESTING have to agree
// on it. They did not: paint sorted, hit testing walked the child list
// backwards, so a `position: fixed` popover declared before an in-flow sibling
// was drawn on top of it and could not be clicked -- every click over it went
// to the element underneath.
void paint_order_children(const BoxTree& tree, BoxId container, std::vector<BoxId>* out);

// Collapsed table borders belong below nonnegative positioned descendants.
// Shared by painting and pointer routing; negative contexts retain their own
// descendants instead of promoting those descendants through the context.
bool table_positioned_layer(const Box& box);
bool table_positioned_isolation(const Box& box);
bool has_table_positioned_layer(const BoxTree& tree, BoxId root);
bool has_table_negative_layer(const BoxTree& tree, BoxId root);

// Traverses the existing sibling links when they already follow paint order.
// Otherwise owns one sorted list for this walk. The tree and its styles must
// stay unchanged until traversal finishes; nothing is cached between walks.
class ChildPaintOrder {
public:
    ChildPaintOrder(const BoxTree& tree, BoxId container);
    class Iterator {
    public:
        Iterator(const ChildPaintOrder* order, int cursor, bool reverse)
            : order_(order), cursor_(cursor), reverse_(reverse) {}
        BoxId operator*() const {
            return order_->sorted_.empty() ? cursor_ : order_->sorted_[cursor_].id;
        }
        Iterator& operator++() {
            if (order_->sorted_.empty()) {
                const Box& b = order_->tree_[cursor_];
                cursor_ = reverse_ ? b.prev_sibling : b.next_sibling;
            } else {
                cursor_ += reverse_ ? -1 : 1;
            }
            return *this;
        }
        bool operator!=(const Iterator& other) const { return cursor_ != other.cursor_; }
    private:
        const ChildPaintOrder* order_;
        int cursor_;
        bool reverse_;
    };
    Iterator begin() const { return {this, sorted_.empty() ? first_ : 0, false}; }
    Iterator end() const { return {this, sorted_.empty() ? kNoBox : count_, false}; }
    Iterator rbegin() const { return {this, sorted_.empty() ? last_ : count_ - 1, true}; }
    Iterator rend() const { return {this, kNoBox, true}; }
    int size() const { return count_; }
private:
    struct Entry { BoxId id; int z; int sequence; bool positioned_zero; uint64_t top_order; };
    const BoxTree& tree_;
    BoxId first_ = kNoBox, last_ = kNoBox;
    int count_ = 0;
    std::vector<Entry> sorted_;
};

// Fills every box's visual-overflow rectangle: the area it and its descendants
// can paint into, relative to its own origin. One bottom-up pass, run after
// layout and positioning have placed everything.
//
// Paint uses it to skip a subtree that cannot reach the current clip, which is
// what keeps a long scrolled list cheap: the work is the number of boxes you
// can SEE rather than the number that exist.
void compute_visual_overflow(BoxTree* tree, BoxId root);
// Re-union one ancestor after a subtree changed, using its children's caches.
void update_visual_overflow(BoxTree* tree, BoxId root);

} // namespace weva
