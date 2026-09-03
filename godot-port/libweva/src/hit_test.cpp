#include "weva/hit_test.h"

#include "weva/computed_style.h"
#include "weva/dom.h"
#include "weva/positioning.h"

#include <vector>

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

// CSS UI L4 §6: `pointer-events: none` makes the box and its descendants
// invisible to hit testing. A descendant may take it back, so this is asked
// per box on the way down rather than pruning the subtree.
bool ignores_pointer(const ComputedStyle* style) {
    return get(style, "pointer-events") == "none";
}

// CSS 2.1 §11.2: a hidden box is not a hit target. It still lays out, and its
// children may set `visible` again, so this too is per box.
bool is_invisible(const ComputedStyle* style) { return get(style, "visibility") == "hidden"; }

struct Search {
    const BoxTree& tree;
    double x, y;

    // Walks a subtree back to front and returns the deepest, latest box that
    // contains the point. `ox`/`oy` is the box's own origin in document
    // coordinates.
    //
    // Backwards because paint goes forwards: the last box drawn is the one on
    // top, and the first hit found walking in reverse is that box.
    BoxId visit(BoxId id, double ox, double oy, bool blocked) const {
        const Box& b = tree[id];
        const double bx = ox + b.x, by = oy + b.y;
        const bool ignore = b.style ? ignores_pointer(b.style) : blocked;
        // A clipping box stops the search at its padding box, so a scrolled-out
        // child is not hit where it is not drawn.
        if (clips_overflow(b)) {
            const double px0 = bx + b.border_left, py0 = by + b.border_top;
            const double px1 = bx + b.width - b.border_right;
            const double py1 = by + b.height - b.border_bottom;
            if (x < px0 || x >= px1 || y < py0 || y >= py1) return kNoBox;
        }
        // Children sit on the parent's BORDER-BOX origin. Layout has already
        // baked the padding into each child's own x and y -- which is what
        // absolute_position assumes when it simply sums the chain, and what
        // paint assumes when it hands its own x and y down untouched.
        //
        // Adding the padding here as well double-counted it, so every child of
        // a padded box was tested at the wrong place. It went unnoticed because
        // the first tests used `margin: 0` markup with no padding anywhere; the
        // demo panel has `padding: 20px` and hit testing its button returned
        // the progress bar thirty pixels above it.
        //
        // A scroll container's children are drawn shifted by its offset, so
        // they are hit there too: the point is over what you can SEE at it.
        const double cx = bx - b.scroll_x, cy = by - b.scroll_y;
        // The REVERSE of paint order, not the reverse of tree order: what you
        // click is what is drawn on top, and the two are different whenever a
        // positioned element is declared before an in-flow sibling. Walking
        // the child list backwards meant a `position: fixed` popover drawn
        // over a later <div> was not clickable anywhere it overlapped it --
        // every click went to the div underneath.
        std::vector<BoxId> order;
        paint_order_children(tree, id, &order);
        for (size_t i = order.size(); i-- > 0;) {
            const BoxId hit = visit(order[i], cx, cy, ignore);
            if (hit != kNoBox) return hit;
        }
        if (ignore) return kNoBox;
        if (b.style && is_invisible(b.style)) return kNoBox;
        if (b.width <= 0 || b.height <= 0) return kNoBox;
        if (x < bx || x >= bx + b.width || y < by || y >= by + b.height) return kNoBox;
        return id;
    }
};

}   // namespace

BoxId box_at_point(const BoxTree& tree, BoxId root, double x, double y) {
    if (root == kNoBox || root >= tree.size()) return kNoBox;
    const Search search{tree, x, y};
    return search.visit(root, 0, 0, false);
}

const Element* element_at_point(const BoxTree& tree, BoxId root, double x, double y) {
    BoxId hit = box_at_point(tree, root, x, y);
    // Anonymous boxes, line boxes and text runs are not elements. The element
    // hit is the nearest one that encloses them, which is why hovering a word
    // hovers the paragraph it is set in.
    while (hit != kNoBox && !tree[hit].element) hit = tree[hit].parent;
    return hit == kNoBox ? nullptr : tree[hit].element;
}

}   // namespace weva
