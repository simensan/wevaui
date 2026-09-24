#include "weva/css_properties.h"
#include "weva/paint.h"
#include "weva/anchor.h"
#include "weva/positioning.h"

#include "weva/inline_layout.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <string>

namespace weva {

bool is_promoted_inline_fragment(const Box& box, const Element* element) {
    if (!box.split_inline_owner) return false;
    const Node* node = box.element ? box.element->parent() : box.pseudo_host;
    for (; node; node = node->parent()) {
        if (node == element) return true;
        if (node == box.split_inline_owner) break;
    }
    return false;
}

void promoted_inline_rect(const BoxTree& tree, BoxId id,
                          double* x, double* y, double* width, double* height) {
    const Box& box = tree[id];
    const Box& parent = tree[box.parent];
    *x = parent.border_left + parent.padding_left;
    *y = box.y;
    if (box.position == PositionType::Relative)
        *y -= box.offset_top.value_or(-box.offset_bottom.value_or(0));
    *width = parent.content_width();
    *height = box.height;
}

namespace {

const int kId_height = CssPropertyRegistry::instance().id_of("height");
// run_positioning visits every box in the tree, and these are read on each
// visit -- by name, which hashed the name and probed the registry index every
// time. Resolved once for the program instead; the registry keeps an id stable
// across re-registration precisely so this is safe.
const int kId_contain = CssPropertyRegistry::instance().id_of("contain");
const int kId_filter = CssPropertyRegistry::instance().id_of("filter");
const int kId_margin = CssPropertyRegistry::instance().id_of("margin");
const int kId_outline_width = CssPropertyRegistry::instance().id_of("outline-width");
const int kId_overflow_x = CssPropertyRegistry::instance().id_of("overflow-x");
const int kId_overflow_y = CssPropertyRegistry::instance().id_of("overflow-y");
const int kId_will_change = CssPropertyRegistry::instance().id_of("will-change");
const int kId_z_index = CssPropertyRegistry::instance().id_of("z-index");
const int kId_top = CssPropertyRegistry::instance().id_of("top");
const int kId_right = CssPropertyRegistry::instance().id_of("right");
const int kId_bottom = CssPropertyRegistry::instance().id_of("bottom");
const int kId_left = CssPropertyRegistry::instance().id_of("left");
const int kId_transform = CssPropertyRegistry::instance().id_of("transform");
const int kId_perspective = CssPropertyRegistry::instance().id_of("perspective");
const int kId_box_shadow = CssPropertyRegistry::instance().id_of("box-shadow");
const int kId_text_shadow = CssPropertyRegistry::instance().id_of("text-shadow");


std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
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

bool set_and_not_none(const ComputedStyle* s, int property) {
    const std::string_view v = get(s, property);
    return !v.empty() && !iequals(v, "none");
}

// CSS Transforms L1 §6.1 and Positioned Layout L3 §4.3: these capture
// absolutely positioned descendants even on a static ancestor.
bool has_containing_block_property(const Box& b) {
    if (!b.style) return false;
    if (set_and_not_none(b.style, kId_transform)) return true;
    if (set_and_not_none(b.style, kId_filter)) return true;
    if (set_and_not_none(b.style, kId_perspective)) return true;
    const std::string_view wc = get(b.style, kId_will_change);
    if (has_token(wc, "transform") || has_token(wc, "filter") || has_token(wc, "perspective")) {
        return true;
    }
    const std::string_view contain = get(b.style, kId_contain);
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

// By id: stamp_offsets reads all four insets of every box on every layout.
std::optional<double> resolve_offset(const ComputedStyle* style, int property,
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

// Intrinsic sizes have a used size after layout. With both insets specified,
// auto margins distribute the remaining space for those sizes too.
bool has_used_size(const ComputedStyle* style, std::string_view property) {
    return has_explicit_size(style, property);
}

} // namespace

bool positioning_replaces_layout(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    const Box& b = tree[id];
    if (b.position != PositionType::Absolute && b.position != PositionType::Fixed) return false;
    return insets_replace_layout(b.style, ctx);
}

namespace {
bool compute_insets_replace_layout(const ComputedStyle* style, const LayoutContext& ctx);
}

bool insets_replace_layout(const ComputedStyle* style, const LayoutContext& ctx) {
    if (!style) return false;
    if (style->insets_memo_version != style->version()) {
        style->insets_memo = compute_insets_replace_layout(style, ctx);
        style->insets_memo_version = style->version();
    }
    return style->insets_memo;
}

namespace {
bool compute_insets_replace_layout(const ComputedStyle* style, const LayoutContext& ctx) {
    // By id: this runs for every out-of-flow box on every layout of its
    // container, which inside a grid that measures its items is many times a
    // pass. Looking the six properties up by name cost match3 a fifth of its
    // layout.
    static const int kIds[6] = {kId_left, kId_right, kId_top, kId_bottom,
                                CssPropertyRegistry::instance().id_of("width"), kId_height};
    std::string_view raw[6];
    const auto declared = [&](int i) { return !raw[i].empty() && !iequals(raw[i], "auto"); };
    // A declared inset can still fail to resolve, so declarations bound the
    // answer from above. Most out-of-flow boxes -- a static position on one
    // axis, or a stated width with no bottom inset -- are settled here from
    // the first few declarations, without resolving a length.
    for (int i = 0; i < 4; ++i) raw[i] = style->get(kIds[i]);
    if (!(declared(0) || declared(1)) || !(declared(2) || declared(3))) return false;
    raw[4] = style->get(kIds[4]);
    raw[5] = style->get(kIds[5]);
    if (declared(4) && !(declared(2) && declared(3) && !declared(5))) return false;
    for (const std::string_view value : raw)
        if (looks_like_anchor_function(value)) return false;
    // Mirrors resolve_offset: present unless empty, `auto`, or not a length or
    // percentage. Which of those it is depends on neither the font size nor
    // the basis, so neither is computed here.
    const auto inset = [&](int i) {
        if (!declared(i)) return false;
        const LengthKind kind = resolve_length(style, kIds[i], ctx, ctx.root_font_size_px, 0.0).kind;
        return kind == LengthKind::Length || kind == LengthKind::Percent;
    };
    const bool left = inset(0), right = inset(1);
    if (!(left || right)) return false;
    const bool top = inset(2), bottom = inset(3);
    if (!(top || bottom)) return false;
    const bool shrinks = !(left && right) && !declared(4);
    const bool stretches = top && bottom && !declared(5);
    return shrinks || stretches;
}
}  // namespace

void relative_offset(const Box& box, double* dx, double* dy) {
    *dx = *dy = 0;
    if (box.position != PositionType::Relative) return;
    if (box.offset_left) *dx = *box.offset_left;
    else if (box.offset_right) *dx = -*box.offset_right;
    if (box.offset_top) *dy = *box.offset_top;
    else if (box.offset_bottom) *dy = -*box.offset_bottom;
}

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
    BoxId containing = kNoBox;
    bool skip_intermediate_scroll = false;
    for (BoxId b = box; b != kNoBox; b = tree[b].parent) {
        ax += tree[b].x;
        ay += tree[b].y;
        if (tree[b].position == PositionType::Absolute || tree[b].position == PositionType::Fixed) {
            containing = kNoBox;
            for (BoxId p = tree[b].parent; p != kNoBox; p = tree[p].parent) {
                if (tree[b].position == PositionType::Absolute
                        ? establishes_absolute_containing_block(tree[p])
                        : establishes_fixed_containing_block(tree[p])) { containing = p; break; }
            }
            skip_intermediate_scroll = true;
        }
        const BoxId parent = tree[b].parent;
        if (parent == containing) skip_intermediate_scroll = false;
        if (parent != kNoBox && !skip_intermediate_scroll) {
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
        const auto visit = [&](auto&& self, BoxId i) -> void {
            const Box& b = tree[i];
            double ax = 0, ay = 0;
            absolute_position(tree, i, &ax, &ay);
            w = std::max(w, ax + b.width);
            h = std::max(h, ay + b.height);
            for (BoxId c : tree.children(i)) self(self, c);
        };
        visit(visit, root);
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
    const std::string_view v = get(b.style, vertical ? kId_overflow_y : kId_overflow_x);
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
        b.offset_top = resolve_offset(b.style, kId_top, ctx, fs, basis);
        b.offset_right = resolve_offset(b.style, kId_right, ctx, fs, basis);
        b.offset_bottom = resolve_offset(b.style, kId_bottom, ctx, fs, basis);
        b.offset_left = resolve_offset(b.style, kId_left, ctx, fs, basis);

        const std::string_view z = get(b.style, kId_z_index);
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
    const auto off = [&](int property, double basis) {
        return resolve_offset(style, property, ctx, fs, basis);
    };
    (*tree)[id].offset_left = off(kId_left, cb.width);
    (*tree)[id].offset_right = off(kId_right, cb.width);
    (*tree)[id].offset_top = off(kId_top, cb.height);
    (*tree)[id].offset_bottom = off(kId_bottom, cb.height);

    // CSS Anchor Positioning L1: an `anchor()` inset or an `anchor-size()`
    // extent is a position on ANOTHER element's border box, so it replaces
    // what the ordinary length path just resolved. One cold call rather than a
    // test per property -- see the note on apply_anchor_overrides.
    bool anchor_width_auto = false;
    if (apply_anchor_overrides(tree, id, cb, &anchor_width_auto) && block) {
        block->relayout_at(id, (*tree)[id].width);
    }


    const bool horiz_pinned = (*tree)[id].offset_left.has_value() && (*tree)[id].offset_right.has_value();
    const bool vert_pinned = (*tree)[id].offset_top.has_value() && (*tree)[id].offset_bottom.has_value();

    // Both edges pinned and no explicit size: the box stretches between them.
    const bool stretch_w = horiz_pinned && (!has_explicit_size(style, "width") || anchor_width_auto);
    const bool stretch_h = vert_pinned && !has_explicit_size(style, "height");

    // CSS 2.1 §10.3.7: an auto width with at most one horizontal edge set is
    // shrink-to-fit. Block layout sized the box against its containing block
    // as if it were in flow, which left `position: absolute; left: 0` labels
    // the full width of the page.
    if (block && !horiz_pinned && (!has_explicit_size(style, "width") || anchor_width_auto)) {
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
            const double frame =
                box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;
            // CSS Box Sizing L3 §4.1, the same floor block layout applies: a
            // border box can never be shorter than its own padding and border,
            // because the CONTENT box is what is floored at zero. Missing here,
            // an absolutely positioned CSS triangle -- `height: 0` with a 10px
            // bottom border, under the `* { box-sizing: border-box }` that
            // opens most stylesheets -- came out zero tall and drew nothing,
            // while the same element in flow was already correct.
            const double h = is_border_box(style) ? std::max(r.pixels, frame) : r.pixels + frame;
            box.height = std::max(0.0, h);
        }
    }

    // CSS 2.1 §10.3.7 / §10.6.4: with BOTH offsets on an axis pinned, a
    // definite size, and BOTH margins auto, the slack is split evenly — the box
    // centres. This is what makes `inset: 0; margin: auto` centre a dialog.
    double extra_left = 0, extra_top = 0;
    const BoxSideValues mar = box_sides(style, kId_margin);
    if (horiz_pinned && iequals(mar.left, "auto") && iequals(mar.right, "auto") &&
        has_used_size(style, "width")) {
        const double slack = cb.width - *box.offset_left - *box.offset_right -
                             box.margin_left - box.margin_right - box.width;
        if (slack > 0) extra_left = slack * 0.5;
    }
    if (vert_pinned && iequals(mar.top, "auto") && iequals(mar.bottom, "auto") &&
        has_used_size(style, "height")) {
        const double slack = cb.height - *box.offset_top - *box.offset_bottom -
                             box.margin_top - box.margin_bottom - box.height;
        extra_top = slack * 0.5;
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
    relative_offset(box, &dx, &dy);
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

void position_descendants(BoxTree* tree, BoxId root, const LayoutContext& ctx, BlockLayout* block) {
    for (BoxId c : tree->children(root)) run_recursive(tree, c, ctx, block);
}

void run_positioning(BoxTree* tree, BoxId root, const LayoutContext& ctx, BlockLayout* block) {
    stamp_offsets(tree, root, ctx);
    // The registry is built lazily inside the walk, but it must be built as a
    // WHOLE when it is: an `anchor-name` can be declared on an element that
    // comes after the one referring to it, so no partial, as-you-go collection
    // would find it.
    begin_anchor_pass(*tree, root);
    run_recursive(tree, root, ctx, block);
    end_anchor_pass();
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
    for (const int prop : {kId_box_shadow, kId_text_shadow}) {
        const std::string_view raw = style->get(prop);
        if (!raw.empty() && raw != "none") reach += sum_lengths(raw, 1.0);
    }
    const std::string_view outline = style->get(kId_outline_width);
    if (!outline.empty()) reach += sum_lengths(outline, 1.0) + 4;
    const std::string_view filter = style->get(kId_filter);
    // A blur reaches about three sigma, and the declaration's own numbers
    // already carry the radius.
    if (!filter.empty() && filter != "none") reach += sum_lengths(filter, 3.0);
    return reach;
}

void compute_visual_overflow(BoxTree* tree, BoxId root) {
    if (!tree || !tree->valid(root)) return;
    for (BoxId c : tree->children(root)) compute_visual_overflow(tree, c);
    update_visual_overflow(tree, root);
}

void update_visual_overflow(BoxTree* tree, BoxId root) {
    if (!tree || !tree->valid(root)) return;
    Box& b = (*tree)[root];
    if (b.retained_from != kNoBox) return; // includes the deferred children
    // Its own border box, always: a box paints its background and border there
    // whatever its children do.
    double x0 = 0, y0 = 0, x1 = b.width, y1 = b.height;
    // A shadow, an outline or a filter reaches past the border box, and a box
    // with none of them needs no slack at all.
    const double slack = decoration_reach(b.style);
    const bool clips = clips_overflow(b);
    for (BoxId c : tree->children(root)) {
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

ChildPaintOrder::ChildPaintOrder(const BoxTree& tree, BoxId container) : tree_(tree) {
    if (!tree.valid(container)) return;
    const Box& b = tree[container];
    first_ = b.first_child;
    last_ = b.last_child;
    if (first_ == last_) {
        count_ = first_ == kNoBox ? 0 : 1;
        return;
    }
    const DisplayKind pd = b.display;
    // A flex or grid item stacks by z-index without being positioned
    // (Flexbox 4.3, Grid 6.4); layout stamps z only on the positioned, so it
    // is read from the style here.
    const bool items_stack = pd == DisplayKind::Flex || pd == DisplayKind::InlineFlex ||
                             pd == DisplayKind::Grid || pd == DisplayKind::InlineGrid;
    const auto entry = [&](BoxId c, int sequence) {
        const Box& cb = tree[c];
        const bool is_positioned =
            cb.style && cb.kind == BoxKind::Block && cb.position != PositionType::Static;
        int z = 0;
        if (cb.z_index && is_positioned) {
            z = *cb.z_index;
        } else if (items_stack && cb.style && cb.kind == BoxKind::Block) {
            const std::string_view zr = cb.style->get(kId_z_index);
            if (!zr.empty() && zr != "auto") z = std::atoi(std::string(zr).c_str());
        }
        // Promoted hosts/backdrops are root siblings. Their opening order
        // outranks every author z-index, with the backdrop preceding its host.
        const Element* host = cb.element ? cb.element : cb.pseudo_host;
        const uint64_t top_order = b.parent == kNoBox && cb.kind == BoxKind::Block && host
            ? host->top_layer_order() : 0;
        return Entry{c, z, sequence, z == 0 && is_positioned, top_order};
    };
    const auto by_z = [](const Entry& a, const Entry& c) {
        if (a.top_order != c.top_order) return a.top_order < c.top_order;
        if (a.top_order) return a.sequence < c.sequence;
        if (a.z != c.z) return a.z < c.z;
        if (a.positioned_zero != c.positioned_zero) return !a.positioned_zero;
        return a.sequence < c.sequence;
    };
    Entry previous{};
    bool ordered = true;
    for (BoxId c : tree.children(container)) {
        const Entry current = entry(c, count_);
        if (count_ && by_z(current, previous)) ordered = false;
        previous = current;
        ++count_;
    }
    if (ordered) return;
    sorted_.reserve(static_cast<size_t>(count_));
    int sequence = 0;
    for (BoxId c : tree.children(container)) sorted_.push_back(entry(c, sequence++));
    // The explicit tree sequence breaks every tie, so sort needs neither
    // stability nor the temporary allocation used by stable_sort.
    std::sort(sorted_.begin(), sorted_.end(), by_z);
}

bool establishes_fixed_containing_block(const Box& b) { return has_containing_block_property(b); }

bool overflow_clip_applies(const BoxTree& tree, BoxId clip, BoxId containing_block) {
    for (BoxId id = containing_block; id != kNoBox; id = tree[id].parent)
        if (id == clip) return true;
    return false;
}

bool subtree_has_overflow_escape(const BoxTree& tree, BoxId root, const LayoutContext& ctx) {
    const Box& b = tree[root];
    if (b.position == PositionType::Absolute || b.position == PositionType::Fixed) {
        const BoxId cb = (b.position == PositionType::Absolute
            ? resolve_absolute_containing_block(tree, root, ctx)
            : resolve_fixed_containing_block(tree, root, ctx)).box;
        for (BoxId p = b.parent; p != cb && p != kNoBox; p = tree[p].parent)
            if (clips_overflow(tree[p])) return true;
    }
    for (const BoxId child : tree.children(root))
        if (subtree_has_overflow_escape(tree, child, ctx)) return true;
    return false;
}

bool table_positioned_isolation(const Box& box) {
    if (box.z_index || box.position == PositionType::Fixed || box.position == PositionType::Sticky ||
        has_containing_block_property(box)) return true;
    if (!box.style) return false;
    return resolve_opacity(box.style) < 1 || box.style->get("isolation") == "isolate" ||
        (!box.style->get("mix-blend-mode").empty() && box.style->get("mix-blend-mode") != "normal");
}

bool table_positioned_layer(const Box& box) {
    return box.kind == BoxKind::Block && box.position != PositionType::Static &&
           (!box.z_index || *box.z_index >= 0);
}

bool has_table_positioned_layer(const BoxTree& tree, BoxId root) {
    for (const BoxId child : tree.children(root)) {
        const Box& box = tree[child];
        if (box.kind == BoxKind::Block && box.position != PositionType::Static) return true;
        if (has_table_positioned_layer(tree, child)) return true;
    }
    return false;
}

bool has_table_negative_layer(const BoxTree& tree, BoxId root) {
    for (const BoxId child : tree.children(root)) {
        const Box& box = tree[child];
        if (box.position != PositionType::Static && box.z_index && *box.z_index < 0) return true;
        if (!table_positioned_isolation(box) && has_table_negative_layer(tree, child)) return true;
    }
    return false;
}

void paint_order_children(const BoxTree& tree, BoxId container, std::vector<BoxId>* out) {
    out->clear();
    const ChildPaintOrder order(tree, container);
    out->reserve(static_cast<size_t>(order.size()));
    for (BoxId c : order) out->push_back(c);
}

} // namespace weva
