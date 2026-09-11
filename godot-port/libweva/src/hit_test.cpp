#include "weva/hit_test.h"

#include "weva/computed_style.h"
#include "weva/dom.h"
#include "weva/form_state.h"
#include "weva/positioning.h"
#include "weva/paint.h"
#include "weva/tessellate.h"
#include <cmath>
#include <algorithm>
#include <memory>

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

bool local_transform(const BoxTree& tree, BoxId id, const LayoutContext& ctx, Transform2D* out) {
    const Box& b = tree[id];
    if (!b.style || b.width <= 0 || b.height <= 0 || b.kind == BoxKind::Text ||
        b.kind == BoxKind::Line || b.kind == BoxKind::AnonymousBlock ||
        b.kind == BoxKind::AnonymousInline) return false;
    const auto value = b.style->get("transform");
    if (value.empty() || value == "none") return false;
    const auto* parent = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    return resolve_transform(b.style, ctx, font_size_px(b.style, parent, ctx), b.width, b.height, out);
}

bool unapply(const Transform2D& t, double* x, double* y) {
    const double determinant = double(t.a) * t.d - double(t.b) * t.c;
    if (determinant == 0 || !std::isfinite(determinant)) return false;
    const double dx = *x - t.tx, dy = *y - t.ty;
    *x = (t.d * dx - t.c * dy) / determinant;
    *y = (t.a * dy - t.b * dx) / determinant;
    return std::isfinite(*x) && std::isfinite(*y);
}

bool rounded_box_contains(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                          double bx, double by, double x, double y, bool padding) {
    const Box& b = tree[id];
    double left = bx, top = by, width = b.width, height = b.height;
    if (padding) {
        left += b.border_left; top += b.border_top;
        width -= b.border_left + b.border_right; height -= b.border_top + b.border_bottom;
    }
    // Most rejected clips are outside the rectangle altogether. Resolve
    // CSS radii only when the point can reach one of its rounded corners.
    if (x < left || x >= left + width || y < top || y >= top + height) return false;
    BorderRadii radii;
    if (b.style && b.kind == BoxKind::Block) {
        const auto* parent = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        radii = clamp_radii_to_rect(resolve_border_radii(b.style, b.width, b.height, ctx,
                                   font_size_px(b.style, parent, ctx)), b.width, b.height);
    }
    if (padding)
        radii = inset_radii(radii, b.border_top, b.border_right, b.border_bottom, b.border_left);
    const auto corner = [&](const CornerRadius& radius, double cx, double cy, bool in_corner) {
        if (!in_corner || radius.x_radius <= 0 || radius.y_radius <= 0) return true;
        const double dx = (x - cx) / radius.x_radius, dy = (y - cy) / radius.y_radius;
        return dx * dx + dy * dy <= 1;
    };
    const auto& tl = radii.top_left; const auto& tr = radii.top_right;
    const auto& bl = radii.bottom_left; const auto& br = radii.bottom_right;
    return corner(tl, left + tl.x_radius, top + tl.y_radius, x < left + tl.x_radius && y < top + tl.y_radius) &&
           corner(tr, left + width - tr.x_radius, top + tr.y_radius, x > left + width - tr.x_radius && y < top + tr.y_radius) &&
           corner(bl, left + bl.x_radius, top + height - bl.y_radius, x < left + bl.x_radius && y > top + height - bl.y_radius) &&
           corner(br, left + width - br.x_radius, top + height - br.y_radius, x > left + width - br.x_radius && y > top + height - br.y_radius);
}

struct Search {
    const BoxTree& tree;
    const LayoutContext& ctx;

    struct Overflow {
        BoxId owner;
        bool outside, any_outside;
        double scroll_x, scroll_y;
        std::shared_ptr<const Overflow> parent;
    };

    std::shared_ptr<const Overflow> applicable(const std::shared_ptr<const Overflow>& node,
            BoxId cb, double* ox, double* oy) const {
        if (!node) return {};
        auto parent = applicable(node->parent, cb, ox, oy);
        if (!overflow_clip_applies(tree, node->owner, cb)) {
            *ox += node->scroll_x; *oy += node->scroll_y;
            return parent;
        }
        if (parent == node->parent) return node;
        auto copy = std::make_shared<Overflow>(*node);
        copy->parent = std::move(parent);
        copy->any_outside = copy->outside || (copy->parent && copy->parent->any_outside);
        return copy;
    }

    struct Positioned {
        BoxId id;
        double ox, oy;
        bool blocked;
        double x, y;
        size_t sequence;
        std::shared_ptr<const Overflow> overflow;
        bool own_only = false;
    };

    // Walks a subtree back to front and returns the deepest, latest box that
    // contains the point. `ox`/`oy` is the box's own origin in document
    // coordinates.
    //
    // Backwards because paint goes forwards: the last box drawn is the one on
    // top, and the first hit found walking in reverse is that box.
    BoxId visit(BoxId id, double ox, double oy, bool blocked, double x, double y,
                std::vector<Positioned>* table_positioned = nullptr,
                std::shared_ptr<const Overflow> overflow = {}, bool own_only = false,
                std::vector<Positioned>* table_negative = nullptr) const {
        const Box& b = tree[id];
        if (overflow && (b.position == PositionType::Absolute || b.position == PositionType::Fixed)) {
            const BoxId cb = (b.position == PositionType::Absolute
                ? resolve_absolute_containing_block(tree, id, ctx) : resolve_fixed_containing_block(tree, id, ctx)).box;
            overflow = applicable(overflow, cb, &ox, &oy);
        }
        if (table_negative && b.position != PositionType::Static && b.z_index && *b.z_index < 0) {
            table_negative->push_back({id,ox,oy,blocked,x,y,table_negative->size(),overflow});
            return kNoBox;
        }
        std::optional<Positioned> deferred_self;
        if (table_positioned && table_positioned_layer(b)) {
            const bool split = !table_positioned_isolation(b) && has_table_positioned_layer(tree,id);
            Positioned entry{id, ox, oy, blocked, x, y, table_positioned->size(), overflow, split};
            if (!split) { table_positioned->push_back(std::move(entry)); return kNoBox; }
            deferred_self = std::move(entry);
        }
        const double bx = ox + b.x + b.sticky_offset_x, by = oy + b.y + b.sticky_offset_y;
        Transform2D local;
        if (local_transform(tree, id, ctx, &local)) {
            x -= bx; y -= by;
            if (!unapply(local, &x, &y)) return kNoBox;
            x += bx; y += by;
        }
        const bool ignore = b.style ? ignores_pointer(b.style) : blocked;
        const bool clipped_self = overflow && overflow->any_outside;
        if (clipped_self && !subtree_has_overflow_escape(tree, id, ctx)) return kNoBox;
        bool visit_children = !own_only;
        // Keep the clip's owner so a descendant positioned against an external
        // containing block can escape it. Ordinary offscreen branches still prune.
        if (clips_overflow(b)) {
            const bool outside = !rounded_box_contains(tree, id, ctx, bx, by, x, y, true);
            if (outside && !subtree_has_overflow_escape(tree, id, ctx)) visit_children = false;
            if (visit_children && (outside || b.scroll_x != 0 || b.scroll_y != 0))
                overflow = std::make_shared<Overflow>(Overflow{id, outside, outside || clipped_self,
                    b.scroll_x, b.scroll_y, overflow});
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
        const ChildPaintOrder order(tree, id);
        std::vector<Positioned> local_positioned;
        std::vector<Positioned> local_negative;
        const bool owns_positioned = !own_only && !table_positioned &&
            (b.display == DisplayKind::Table || b.display == DisplayKind::InlineTable) &&
            tree.table_borders(id) && has_table_positioned_layer(tree, id);
        auto* positioned = owns_positioned ? &local_positioned : table_positioned;
        auto* negative = owns_positioned ? &local_negative : table_negative;
        if (b.position != PositionType::Static && b.z_index && *b.z_index < 0) positioned = nullptr;
        BoxId ordinary_hit = kNoBox;
        for (auto i = order.rbegin(); visit_children && i != order.rend(); ++i) {
            const BoxId hit = visit(*i, cx, cy, ignore, x, y, positioned, overflow, false, negative);
            if (hit != kNoBox && ordinary_hit == kNoBox) ordinary_hit = hit;
            if (!positioned && ordinary_hit != kNoBox) return ordinary_hit;
        }
        if (deferred_self) {
            deferred_self->sequence = table_positioned->size();
            table_positioned->push_back(std::move(*deferred_self));
        }
        if (owns_positioned) {
            // Collected in reverse paint order already; preserve that order
            // among equal z values while testing higher layers first.
            std::sort(local_positioned.begin(), local_positioned.end(), [&](const auto& a, const auto& c) {
                const int az = tree[a.id].z_index.value_or(0), cz = tree[c.id].z_index.value_or(0);
                return az != cz ? az > cz : a.sequence < c.sequence;
            });
            for (const auto& entry : local_positioned) {
                const BoxId hit = visit(entry.id, entry.ox, entry.oy, entry.blocked, entry.x, entry.y, nullptr, entry.overflow, entry.own_only);
                if (hit != kNoBox) return hit;
            }
        }
        if (ordinary_hit != kNoBox) return ordinary_hit;
        const auto self_hit = [&]() -> BoxId {
        // Internal table tracks contribute backgrounds, but cells (or the
        // table when no cell is above it) are the pointer targets. Events on
        // a cell still bubble through its row and row group in the DOM.
        switch (b.display) {
            case DisplayKind::TableRow: case DisplayKind::TableRowGroup:
            case DisplayKind::TableHeaderGroup: case DisplayKind::TableFooterGroup:
            case DisplayKind::TableColumn: case DisplayKind::TableColumnGroup:
                return kNoBox;
            default: break;
        }
        if (deferred_self || ignore || clipped_self) return kNoBox;
        if (b.style && is_invisible(b.style)) return kNoBox;
        if (b.width <= 0 || b.height <= 0) return kNoBox;
        if (x < bx || x >= bx + b.width || y < by || y >= by + b.height) return kNoBox;
        if (!rounded_box_contains(tree, id, ctx, bx, by, x, y, false)) return kNoBox;
        // Anonymous text/line boxes belong to their nearest element. Use DOM
        // ancestry there: promoted popovers retain inertness, modals escape it.
        BoxId owner = id;
        while (owner != kNoBox && !tree[owner].element) owner = tree[owner].parent;
        if (owner != kNoBox && form_is_inert(*tree[owner].element)) return kNoBox;
        return id;
        };
        const BoxId self = self_hit();
        if (self != kNoBox) return self;
        if (owns_positioned) {
            std::sort(local_negative.begin(),local_negative.end(),[&](const auto& a,const auto& c) {
                const int az=tree[a.id].z_index.value_or(0),cz=tree[c.id].z_index.value_or(0);
                return az != cz ? az > cz : a.sequence < c.sequence;
            });
            for (const auto& entry : local_negative) {
                const BoxId hit=visit(entry.id,entry.ox,entry.oy,entry.blocked,entry.x,entry.y,nullptr,entry.overflow);
                if (hit != kNoBox) return hit;
            }
        }
        return kNoBox;
    }
};

}   // namespace

BoxId box_at_point(const BoxTree& tree, BoxId root, double x, double y, const LayoutContext* context) {
    if (root == kNoBox || root >= tree.size()) return kNoBox;
    const LayoutContext fallback;
    const Search search{tree, context ? *context : fallback};
    return search.visit(root, 0, 0, false, x, y);
}

const Element* element_at_point(const BoxTree& tree, BoxId root, double x, double y, const LayoutContext* context) {
    BoxId hit = box_at_point(tree, root, x, y, context);
    // Anonymous boxes, line boxes and text runs are not elements. The element
    // hit is the nearest one that encloses them, which is why hovering a word
    // hovers the paragraph it is set in.
    while (hit != kNoBox && !tree[hit].element) hit = tree[hit].parent;
    return hit == kNoBox ? nullptr : tree[hit].element;
}

bool point_to_layout(const BoxTree& tree, BoxId box, const LayoutContext& context, double* x, double* y) {
    if (box == kNoBox || box >= tree.size() || !x || !y) return false;
    Transform2D combined;
    for (BoxId id = box; id != kNoBox; id = tree[id].parent) {
        Transform2D local;
        if (!local_transform(tree, id, context, &local)) continue;
        double ox = 0, oy = 0;
        visual_position(tree, id, &ox, &oy);
        combined = combined.multiply(Transform2D::translate(float(-ox), float(-oy))
            .multiply(local).multiply(Transform2D::translate(float(ox), float(oy))));
    }
    return unapply(combined, x, y);
}

}   // namespace weva
