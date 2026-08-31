#include "weva/positioning.h"

#include "weva/inline_layout.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

// Whitespace- or comma-separated token match, for `will-change` and `contain`.
bool has_token(std::string_view value, std::string_view token) {
    size_t i = 0;
    while (i < value.size()) {
        while (i < value.size() && (value[i] == ' ' || value[i] == ',' || value[i] == '\t')) ++i;
        const size_t start = i;
        while (i < value.size() && value[i] != ' ' && value[i] != ',' && value[i] != '\t') ++i;
        if (iequals(value.substr(start, i - start), token)) return true;
    }
    return false;
}

bool set_and_not_none(const ComputedStyle* s, std::string_view property) {
    const std::string_view v = get(s, property);
    return !v.empty() && !iequals(v, "none");
}

// CSS Transforms L1 §6.1 and Positioned Layout L3 §4.3: these capture
// absolutely positioned descendants even on a static ancestor.
bool has_containing_block_property(const Box& b) {
    if (!b.style) return false;
    if (set_and_not_none(b.style, "transform")) return true;
    if (set_and_not_none(b.style, "filter")) return true;
    if (set_and_not_none(b.style, "perspective")) return true;
    const std::string_view wc = get(b.style, "will-change");
    if (has_token(wc, "transform") || has_token(wc, "filter") || has_token(wc, "perspective")) {
        return true;
    }
    const std::string_view contain = get(b.style, "contain");
    return has_token(contain, "layout") || has_token(contain, "paint") ||
           has_token(contain, "strict") || has_token(contain, "content");
}

ContainingBlock viewport(const LayoutContext& ctx) {
    ContainingBlock cb;
    cb.width = ctx.viewport_width_px;
    cb.height = ctx.viewport_height_px;
    cb.is_viewport = true;
    return cb;
}

// The padding box of `p`, in root-relative coordinates.
ContainingBlock padding_box_of(const BoxTree& tree, BoxId p) {
    double ax = 0, ay = 0;
    absolute_position(tree, p, &ax, &ay);
    const Box& b = tree[p];
    ContainingBlock cb;
    cb.box = p;
    cb.x = ax + b.border_left;
    cb.y = ay + b.border_top;
    cb.width = std::max(0.0, b.width - b.border_left - b.border_right);
    cb.height = std::max(0.0, b.height - b.border_top - b.border_bottom);
    return cb;
}

std::optional<double> resolve_offset(const ComputedStyle* style, std::string_view property,
                                     const LayoutContext& ctx, double font_size, double basis) {
    const std::string_view raw = get(style, property);
    // `auto` is absent, not zero: an absent offset falls back to the static
    // position, a zero one pins to the containing block's edge.
    if (raw.empty() || iequals(raw, "auto")) return std::nullopt;
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
    return std::nullopt;
}

bool has_explicit_size(const ComputedStyle* style, std::string_view property) {
    const std::string_view raw = get(style, property);
    return !raw.empty() && !iequals(raw, "auto");
}

} // namespace

void absolute_position(const BoxTree& tree, BoxId box, double* x, double* y) {
    double ax = 0, ay = 0;
    for (BoxId b = box; b != kNoBox; b = tree[b].parent) {
        ax += tree[b].x;
        ay += tree[b].y;
    }
    *x = ax;
    *y = ay;
}

bool establishes_absolute_containing_block(const Box& b) {
    if (b.position != PositionType::Static) return true;
    return has_containing_block_property(b);
}

ContainingBlock resolve_absolute_containing_block(const BoxTree& tree, BoxId box,
                                                  const LayoutContext& ctx) {
    for (BoxId p = tree[box].parent; p != kNoBox; p = tree[p].parent) {
        if (establishes_absolute_containing_block(tree[p])) return padding_box_of(tree, p);
    }
    return viewport(ctx);
}

ContainingBlock resolve_fixed_containing_block(const BoxTree& tree, BoxId box,
                                               const LayoutContext& ctx) {
    for (BoxId p = tree[box].parent; p != kNoBox; p = tree[p].parent) {
        if (has_containing_block_property(tree[p])) return padding_box_of(tree, p);
    }
    return viewport(ctx);
}

void stamp_offsets(BoxTree* tree, BoxId root, const LayoutContext& ctx) {
    Box& b = (*tree)[root];
    if (b.style) {
        const BoxId parent = b.parent;
        const ComputedStyle* ps = parent == kNoBox ? nullptr : (*tree)[parent].style;
        const double fs = font_size_px(b.style, ps, ctx);
        // A percentage offset resolves against the containing block, which is
        // not known until placement — so the containing block's own dimensions
        // are used as the basis at placement time instead. Here only the
        // lengths are resolved; percentages are re-read against the real basis.
        const double basis = parent == kNoBox ? ctx.viewport_width_px : (*tree)[parent].width;
        b.offset_top = resolve_offset(b.style, "top", ctx, fs, basis);
        b.offset_right = resolve_offset(b.style, "right", ctx, fs, basis);
        b.offset_bottom = resolve_offset(b.style, "bottom", ctx, fs, basis);
        b.offset_left = resolve_offset(b.style, "left", ctx, fs, basis);

        const std::string_view z = get(b.style, "z-index");
        if (!z.empty() && !iequals(z, "auto")) {
            double v = 0;
            if (css_parse_double(z, &v)) b.z_index = static_cast<int>(v);
        }
    }
    for (BoxId c : tree->children(root)) stamp_offsets(tree, c, ctx);
}

namespace {

void apply_absolute(BoxTree* tree, BoxId id, const ContainingBlock& cb,
                    const LayoutContext& ctx, BlockLayout* block) {
    Box& box = (*tree)[id];
    const ComputedStyle* style = box.style;
    const BoxId parent = box.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : (*tree)[parent].style;
    const double fs = font_size_px(style, ps, ctx);

    // Percentage offsets resolve against the containing block, so they are
    // re-read here now that it is known.
    const auto off = [&](std::string_view property, double basis) {
        return resolve_offset(style, property, ctx, fs, basis);
    };
    box.offset_left = off("left", cb.width);
    box.offset_right = off("right", cb.width);
    box.offset_top = off("top", cb.height);
    box.offset_bottom = off("bottom", cb.height);

    const bool horiz_pinned = box.offset_left.has_value() && box.offset_right.has_value();
    const bool vert_pinned = box.offset_top.has_value() && box.offset_bottom.has_value();

    // Both edges pinned and no explicit size: the box stretches between them.
    if (horiz_pinned && !has_explicit_size(style, "width")) {
        const double w = std::max(0.0, cb.width - *box.offset_left - *box.offset_right -
                                           box.margin_left - box.margin_right);
        if (block && std::fabs(box.width - w) > 1e-9) {
            // Block layout sized the children against the PROVISIONAL width —
            // the containing block's content width — before this pass derived
            // the pinned one. Without a content relayout they keep the wider
            // measure and overflow the box.
            block->relayout_at(id, w);
        }
        box.width = w;
    }
    if (vert_pinned && !has_explicit_size(style, "height")) {
        box.height = std::max(0.0, cb.height - *box.offset_top - *box.offset_bottom -
                                       box.margin_top - box.margin_bottom);
    }

    // An explicit percentage height could not be resolved during block layout,
    // which does not know the containing block top-down. This is the first
    // point at which it can be.
    if (has_explicit_size(style, "height") && cb.height > 0) {
        const ResolvedLength r = resolve_length(get(style, "height"), ctx, fs, cb.height);
        if (r.kind == LengthKind::Length) {
            double h = r.pixels;
            if (!is_border_box(style)) {
                h += box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;
            }
            box.height = std::max(0.0, h);
        }
    }

    // CSS 2.1 §10.3.7 / §10.6.4: with BOTH offsets on an axis pinned, a
    // definite size, and BOTH margins auto, the slack is split evenly — the box
    // centres. This is what makes `inset: 0; margin: auto` centre a dialog.
    double extra_left = 0, extra_top = 0;
    const BoxSideValues mar = box_sides(style, "margin");
    if (horiz_pinned && iequals(mar.left, "auto") && iequals(mar.right, "auto")) {
        const double slack = cb.width - *box.offset_left - *box.offset_right -
                             box.margin_left - box.margin_right - box.width;
        if (slack > 0) extra_left = slack * 0.5;
    }
    if (vert_pinned) {
        const BoxSideValues m2 = box_sides(style, "margin");
        if (iequals(m2.top, "auto") && iequals(m2.bottom, "auto")) {
            const double slack = cb.height - *box.offset_top - *box.offset_bottom -
                                 box.margin_top - box.margin_bottom - box.height;
            if (slack > 0) extra_top = slack * 0.5;
        }
    }

    double parent_x = 0, parent_y = 0;
    if (parent != kNoBox) absolute_position(*tree, parent, &parent_x, &parent_y);

    double abs_x, abs_y;
    if (box.offset_left) {
        abs_x = cb.x + *box.offset_left + box.margin_left + extra_left;
    } else if (box.offset_right) {
        abs_x = cb.x + cb.width - *box.offset_right - box.margin_right - box.width;
    } else {
        // Neither edge given: the box stays at its STATIC position — where it
        // would have been in flow — rather than snapping to the containing
        // block's edge.
        abs_x = parent_x + box.x;
    }
    if (box.offset_top) {
        abs_y = cb.y + *box.offset_top + box.margin_top + extra_top;
    } else if (box.offset_bottom) {
        abs_y = cb.y + cb.height - *box.offset_bottom - box.margin_bottom - box.height;
    } else {
        abs_y = parent_y + box.y;
    }

    // Boxes are stored local to their parent, so the absolute origin is
    // converted back before it is written.
    box.x = abs_x - parent_x;
    box.y = abs_y - parent_y;
}

// `position: relative` offsets the box from its in-flow position WITHOUT
// affecting the flow: everything around it stays where it was. An `auto` offset
// is zero here, and when both edges of an axis are given, the start edge wins in
// LTR (§9.4.3, over-constrained).
void apply_relative(BoxTree* tree, BoxId id) {
    Box& box = (*tree)[id];
    double dx = 0, dy = 0;
    if (box.offset_left) dx = *box.offset_left;
    else if (box.offset_right) dx = -*box.offset_right;
    if (box.offset_top) dy = *box.offset_top;
    else if (box.offset_bottom) dy = -*box.offset_bottom;
    box.x += dx;
    box.y += dy;
}

void run_recursive(BoxTree* tree, BoxId id, const LayoutContext& ctx, BlockLayout* block) {
    // Children first, so an ancestor's geometry is final before a descendant
    // resolves against it — except that placing the ancestor then moves the
    // subtree with it, which is exactly what parent-relative coordinates give.
    for (BoxId c : tree->children(id)) run_recursive(tree, c, ctx, block);

    const Box& b = (*tree)[id];
    switch (b.position) {
        case PositionType::Relative:
            apply_relative(tree, id);
            break;
        case PositionType::Absolute:
            apply_absolute(tree, id, resolve_absolute_containing_block(*tree, id, ctx), ctx,
                           block);
            break;
        case PositionType::Fixed:
            apply_absolute(tree, id, resolve_fixed_containing_block(*tree, id, ctx), ctx, block);
            break;
        default:
            // Static and sticky: sticky needs a scroll position, which is a
            // later slice, so it is left in flow.
            break;
    }
}

} // namespace

void run_positioning(BoxTree* tree, BoxId root, const LayoutContext& ctx, BlockLayout* block) {
    stamp_offsets(tree, root, ctx);
    run_recursive(tree, root, ctx, block);
}

} // namespace weva
