#include "weva/css_properties.h"
#include "weva/positioning.h"

#include "weva/inline_layout.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <string>

namespace weva {

namespace {

const int kId_height = CssPropertyRegistry::instance().id_of("height");


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
    const ResolvedLength r = resolve_length(style, property, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
    return std::nullopt;
}

bool has_explicit_size(const ComputedStyle* style, std::string_view property) {
    const std::string_view raw = get(style, property);
    return !raw.empty() && !iequals(raw, "auto");
}

// A size is DEFINITE only when it resolves to a length or a percentage.
// `fit-content`, `min-content` and `max-content` are explicit but not definite,
// and the difference decides whether auto margins centre the box.
//
// The `<dialog>` UA sheet is the case that makes this matter: it pins all four
// edges with `margin: auto`, `width: fit-content` and `height: fit-content`.
// An author writing `top: 80px; left: 80px; width: 240px` gets a box centred
// HORIZONTALLY (width is definite) but sitting at top 80 (height is not), and
// treating both axes alike put the dialog 217px lower. Chrome and the reference
// agree on 80; the corpus carries Chrome's own numbers, which is how this was
// settled rather than argued.
bool is_definite_size(const ComputedStyle* style, std::string_view property,
                      const LayoutContext& ctx, double font_size, double basis) {
    const std::string_view raw = get(style, property);
    if (raw.empty() || iequals(raw, "auto")) return false;
    const ResolvedLength r = resolve_length(style, property, ctx, font_size, basis);
    return r.kind == LengthKind::Length || r.kind == LengthKind::Percent;
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

void visual_position(const BoxTree& tree, BoxId box, double* x, double* y) {
    double ax = 0, ay = 0;
    for (BoxId b = box; b != kNoBox; b = tree[b].parent) {
        ax += tree[b].x;
        ay += tree[b].y;
        const BoxId parent = tree[b].parent;
        if (parent != kNoBox) {
            ax -= tree[parent].scroll_x;
            ay -= tree[parent].scroll_y;
        }
    }
    *x = ax;
    *y = ay;
}

void content_size(const BoxTree& tree, BoxId root, const LayoutContext& ctx, double* out_width,
                  double* out_height) {
    // The viewport is the floor: a page shorter than the box it was laid out in
    // still occupies that box, and a host asking "is there anything to scroll
    // to" wants no for that case rather than a number smaller than its window.
    double w = ctx.viewport_width_px;
    double h = ctx.viewport_height_px;
    if (tree.valid(root)) {
        for (int i = 0; i < tree.size(); ++i) {
            const Box& b = tree[i];
            if (b.width <= 0 && b.height <= 0) continue;
            double ax = 0, ay = 0;
            absolute_position(tree, i, &ax, &ay);
            w = std::max(w, ax + b.width);
            h = std::max(h, ay + b.height);
        }
    }
    if (out_width) *out_width = w;
    if (out_height) *out_height = h;
}

bool clips_overflow(const Box& b) {
    if (!b.style) return false;
    // A line box, and every anonymous box, carries its CONTAINER's style so
    // that inline layout can read the inherited properties off it. Asking one
    // about `overflow` therefore gets the container's answer -- and a walk
    // that believed it stopped at the first line inside every scroller, which
    // is how a row of cards 360 wide reported nothing to scroll.
    if (b.kind != BoxKind::Block || (!b.element && !b.pseudo_host)) return false;
    for (const char* prop : {"overflow-x", "overflow-y"}) {
        const std::string_view v = get(b.style, prop);
        if (v == "hidden" || v == "clip" || v == "auto" || v == "scroll") return true;
    }
    return false;
}

bool scrollable_on_axis(const Box& b, bool vertical) {
    if (!clips_overflow(b)) return false;
    const std::string_view v = get(b.style, vertical ? "overflow-y" : "overflow-x");
    return v == "auto" || v == "scroll";
}

namespace {

// Every descendant's border box, in coordinates relative to `ox`/`oy`.
void reach_of(const BoxTree& tree, BoxId id, double ox, double oy, double* w, double* h) {
    for (BoxId c = tree[id].first_child; c != kNoBox; c = tree[c].next_sibling) {
        const Box& b = tree[c];
        // A fixed box is anchored to the viewport, not to what it sits in, so
        // it neither moves with the scroll nor makes anything scrollable.
        if (b.position == PositionType::Fixed) continue;
        const double x = ox + b.x, y = oy + b.y;
        if (b.width > 0 || b.height > 0) {
            *w = std::max(*w, x + b.width);
            *h = std::max(*h, y + b.height);
        }
        if (clips_overflow(b)) continue;
        reach_of(tree, c, x, y, w, h);
    }
}

}   // namespace

void scrollable_overflow(const BoxTree& tree, BoxId box, double* out_width, double* out_height) {
    double w = 0, h = 0;
    if (tree.valid(box)) {
        const Box& b = tree[box];
        // Children sit on the BORDER-box origin, so stepping in by the border
        // puts the measure in the padding box where the spec wants it.
        reach_of(tree, box, -b.border_left, -b.border_top, &w, &h);
        // End padding counts as part of the scrollable area (§3), which is why
        // a padded list does not end flush against its last row.
        if (w > 0) w += b.padding_right;
        if (h > 0) h += b.padding_bottom;
        w = std::max(w, b.width - b.border_left - b.border_right);
        h = std::max(h, b.height - b.border_top - b.border_bottom);
    }
    if (out_width) *out_width = w;
    if (out_height) *out_height = h;
}

void max_scroll(const BoxTree& tree, BoxId box, double* out_x, double* out_y) {
    double sw = 0, sh = 0;
    scrollable_overflow(tree, box, &sw, &sh);
    double mx = 0, my = 0;
    if (tree.valid(box)) {
        const Box& b = tree[box];
        mx = std::max(0.0, sw - (b.width - b.border_left - b.border_right));
        my = std::max(0.0, sh - (b.height - b.border_top - b.border_bottom));
    }
    if (out_x) *out_x = mx;
    if (out_y) *out_y = my;
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
    // Deliberately NOT a `Box&` held across the whole function. The relayouts
    // below create line boxes and anonymous blocks, BoxTree::create appends to a
    // vector, and any reference into it dies at that point. Holding one here
    // read a stale `offset_top` and dropped a `position: fixed; inset: 0`
    // overlay to its static position — the same shape of bug as the one in
    // layout_inline_items, which is twice now.
    const ComputedStyle* style = (*tree)[id].style;
    const BoxId parent = (*tree)[id].parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : (*tree)[parent].style;
    const double fs = font_size_px(style, ps, ctx);

    // Percentage offsets resolve against the containing block, so they are
    // re-read here now that it is known.
    const auto off = [&](std::string_view property, double basis) {
        return resolve_offset(style, property, ctx, fs, basis);
    };
    (*tree)[id].offset_left = off("left", cb.width);
    (*tree)[id].offset_right = off("right", cb.width);
    (*tree)[id].offset_top = off("top", cb.height);
    (*tree)[id].offset_bottom = off("bottom", cb.height);

    const bool horiz_pinned = (*tree)[id].offset_left.has_value() && (*tree)[id].offset_right.has_value();
    const bool vert_pinned = (*tree)[id].offset_top.has_value() && (*tree)[id].offset_bottom.has_value();

    // Both edges pinned and no explicit size: the box stretches between them.
    const bool stretch_w = horiz_pinned && !has_explicit_size(style, "width");
    const bool stretch_h = vert_pinned && !has_explicit_size(style, "height");

    // CSS 2.1 §10.3.7: an auto width with at most one horizontal edge set is
    // shrink-to-fit. Block layout sized the box against its containing block
    // as if it were in flow, which left `position: absolute; left: 0` labels
    // the full width of the page.
    if (block && !horiz_pinned && !has_explicit_size(style, "width")) {
        const double avail = std::max(
            0.0, cb.width - (*tree)[id].margin_left - (*tree)[id].margin_right);
        block->shrink_to_fit(id, avail, ps);
    }
    const double pinned_w =
        stretch_w ? std::max(0.0, cb.width - *(*tree)[id].offset_left - *(*tree)[id].offset_right -
                                      (*tree)[id].margin_left - (*tree)[id].margin_right)
                  : (*tree)[id].width;
    const double pinned_h =
        stretch_h ? std::max(0.0, cb.height - *(*tree)[id].offset_top - *(*tree)[id].offset_bottom -
                                      (*tree)[id].margin_top - (*tree)[id].margin_bottom)
                  : (*tree)[id].height;

    if (stretch_h && block) {
        // Block layout sized the children against the PROVISIONAL width, and
        // knew nothing of this height at all. Both are imposed here in one
        // pass, and the height is marked imposed so the auto-height rule does
        // not collapse it back — which is what a `position: fixed; inset: 0;
        // display: flex` overlay needs before its justify-content has anything
        // to centre in.
        (*tree)[id].width = pinned_w;
        block->relayout_at_size(id, pinned_w, pinned_h);
    } else if (stretch_w && block && std::fabs((*tree)[id].width - pinned_w) > 1e-9) {
        // Width only: without a content relayout the children keep the wider
        // provisional measure and overflow the box.
        block->relayout_at(id, pinned_w);
    }
    if (stretch_w) (*tree)[id].width = pinned_w;
    if (stretch_h) (*tree)[id].height = pinned_h;

    // Safe from here: nothing below creates a box.
    Box& box = (*tree)[id];

    // An explicit percentage height could not be resolved during block layout,
    // which does not know the containing block top-down. This is the first
    // point at which it can be.
    if (has_explicit_size(style, "height") && cb.height > 0) {
        const ResolvedLength r = resolve_length(style, kId_height, ctx, fs, cb.height);
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
    if (horiz_pinned && iequals(mar.left, "auto") && iequals(mar.right, "auto") &&
        is_definite_size(style, "width", ctx, fs, cb.width)) {
        const double slack = cb.width - *box.offset_left - *box.offset_right -
                             box.margin_left - box.margin_right - box.width;
        if (slack > 0) extra_left = slack * 0.5;
    }
    if (vert_pinned && iequals(mar.top, "auto") && iequals(mar.bottom, "auto") &&
        is_definite_size(style, "height", ctx, fs, cb.height)) {
        const double slack = cb.height - *box.offset_top - *box.offset_bottom -
                             box.margin_top - box.margin_bottom - box.height;
        if (slack > 0) extra_top = slack * 0.5;
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
    // The box itself FIRST, then its descendants. Sizing an absolutely
    // positioned box re-lays its content, and that relayout runs block layout
    // over any positioned descendants again — so a descendant handled before
    // its ancestor had its shrink-to-fit width overwritten with the containing
    // block's, and a name pill on a dialog came out 1116px wide. Descendants
    // still see final ancestor geometry, since it is decided before they run.
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
    for (BoxId c : tree->children(id)) run_recursive(tree, c, ctx, block);
}

} // namespace

void run_positioning(BoxTree* tree, BoxId root, const LayoutContext& ctx, BlockLayout* block) {
    stamp_offsets(tree, root, ctx);
    run_recursive(tree, root, ctx, block);
}

// How far this box's own decoration can reach past its border box.
//
// A cull that is slightly too generous costs a few boxes of work; one that is
// too tight loses pixels. So this OVER-estimates deliberately: it sums every
// length in the shadow and filter declarations rather than parsing them into
// offset, blur and spread, which bounds the true reach from above without
// needing the paint code's parser here.
double decoration_reach(const ComputedStyle* style) {
    if (!style) return 0;
    double reach = 0;
    const auto sum_lengths = [](std::string_view raw, double scale) {
        double total = 0;
        for (std::size_t i = 0; i < raw.size();) {
            if (!(std::isdigit(static_cast<unsigned char>(raw[i])) || raw[i] == '-' ||
                  raw[i] == '.')) {
                ++i;
                continue;
            }
            char* end = nullptr;
            const std::string text(raw.substr(i));
            const double v = std::strtod(text.c_str(), &end);
            if (end == text.c_str()) {
                ++i;
                continue;
            }
            total += std::abs(v);
            i += static_cast<std::size_t>(end - text.c_str());
        }
        return total * scale;
    };
    for (const char* prop : {"box-shadow", "text-shadow"}) {
        const std::string_view raw = style->get(prop);
        if (!raw.empty() && raw != "none") reach += sum_lengths(raw, 1.0);
    }
    const std::string_view outline = style->get("outline-width");
    if (!outline.empty()) reach += sum_lengths(outline, 1.0) + 4;
    const std::string_view filter = style->get("filter");
    // A blur reaches about three sigma, and the declaration's own numbers
    // already carry the radius.
    if (!filter.empty() && filter != "none") reach += sum_lengths(filter, 3.0);
    return reach;
}

void compute_visual_overflow(BoxTree* tree, BoxId root) {
    if (!tree || !tree->valid(root)) return;
    Box& b = (*tree)[root];
    // Its own border box, always: a box paints its background and border there
    // whatever its children do.
    double x0 = 0, y0 = 0, x1 = b.width, y1 = b.height;
    // A shadow, an outline or a filter reaches past the border box, and a box
    // with none of them needs no slack at all.
    const double slack = decoration_reach(b.style);
    const bool clips = clips_overflow(b);
    for (BoxId c : tree->children(root)) {
        compute_visual_overflow(tree, c);
        const Box& cb = (*tree)[c];
        // A clipping box's children are drawn shifted by its scroll offset,
        // and the offset moves -- so the union is taken UNSHIFTED and the clip
        // below bounds it to what the box can show. Otherwise every scroll
        // would invalidate the rect.
        if (clips) continue;
        x0 = std::min(x0, cb.x + cb.vis_x0);
        y0 = std::min(y0, cb.y + cb.vis_y0);
        x1 = std::max(x1, cb.x + cb.vis_x1);
        y1 = std::max(y1, cb.y + cb.vis_y1);
    }
    b.vis_x0 = x0 - slack;
    b.vis_y0 = y0 - slack;
    b.vis_x1 = x1 + slack;
    b.vis_y1 = y1 + slack;
}

void paint_order_children(const BoxTree& tree, BoxId container, std::vector<BoxId>* out) {
    out->clear();
    if (!tree.valid(container)) return;
    struct Entry {
        BoxId id;
        int z;
        int order;
    };
    std::vector<Entry> negative, in_flow, positioned, positive;
    const Box& b = tree[container];
    const DisplayKind pd = b.display;
    // A flex or grid item stacks by z-index without being positioned
    // (Flexbox 4.3, Grid 6.4); layout stamps z only on the positioned, so it
    // is read from the style here.
    const bool items_stack = pd == DisplayKind::Flex || pd == DisplayKind::InlineFlex ||
                             pd == DisplayKind::Grid || pd == DisplayKind::InlineGrid;
    int order = 0;
    for (BoxId c : tree.children(container)) {
        const Box& cb = tree[c];
        const bool is_positioned =
            cb.style && cb.kind == BoxKind::Block && cb.position != PositionType::Static;
        int z = 0;
        if (cb.z_index && is_positioned) {
            z = *cb.z_index;
        } else if (items_stack && cb.style && cb.kind == BoxKind::Block) {
            const std::string_view zr = cb.style->get("z-index");
            if (!zr.empty() && zr != "auto") z = std::atoi(std::string(zr).c_str());
        }
        const Entry e{c, z, order++};
        if (z < 0) negative.push_back(e);
        else if (z > 0) positive.push_back(e);
        else if (is_positioned) positioned.push_back(e);
        else in_flow.push_back(e);
    }
    const auto by_z = [](const Entry& a, const Entry& c) {
        return a.z != c.z ? a.z < c.z : a.order < c.order;
    };
    std::stable_sort(negative.begin(), negative.end(), by_z);
    std::stable_sort(positive.begin(), positive.end(), by_z);
    out->reserve(negative.size() + in_flow.size() + positioned.size() + positive.size());
    for (const auto* bucket : {&negative, &in_flow, &positioned, &positive}) {
        for (const Entry& e : *bucket) out->push_back(e.id);
    }
}

} // namespace weva
