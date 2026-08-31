#include "weva/block_layout.h"

#include "weva/css_properties.h"
#include "weva/inline_layout.h"

#include <algorithm>

#include <cmath>
#include <string>
#include <vector>

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

// The content height of a box, or absent when it has no definite height yet.
// A percentage height needs this from its parent (CSS 2.1 §10.5).
std::optional<double> definite_content_height(const BoxTree& tree, BoxId id) {
    if (id == kNoBox) return std::nullopt;
    const Box& p = tree[id];
    if (p.height <= 0) return std::nullopt;
    const double h = p.height - p.padding_top - p.padding_bottom - p.border_top - p.border_bottom;
    return h > 0 ? h : 0.0;
}

double resolve_side_px(std::string_view raw, const LayoutContext& ctx, double font_size,
                       double basis, double line_height) {
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis, line_height);
    // Auto and unparseable both give 0 here: an auto margin contributes no
    // space of its own, and the centring rule reads the raw text instead.
    return r.kind == LengthKind::Length ? r.pixels : 0;
}

} // namespace

ResolvedSides resolve_box_sides_px(const ComputedStyle* style, std::string_view shorthand,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height) {
    const BoxSideValues sides = box_sides(style, shorthand);
    ResolvedSides r;
    // The containing block's WIDTH is the basis on all four edges — a
    // percentage margin-top resolves against width, not height.
    r.top = resolve_side_px(sides.top, ctx, font_size, containing_block_width, line_height);
    r.right = resolve_side_px(sides.right, ctx, font_size, containing_block_width, line_height);
    r.bottom = resolve_side_px(sides.bottom, ctx, font_size, containing_block_width, line_height);
    r.left = resolve_side_px(sides.left, ctx, font_size, containing_block_width, line_height);
    r.right_raw = sides.right;
    r.left_raw = sides.left;
    return r;
}

ResolvedSides resolve_border_edges(const ComputedStyle* style, const LayoutContext& ctx,
                                   double font_size) {
    const auto edge = [&](std::string_view style_prop, std::string_view width_prop) {
        const std::string_view s = get(style, style_prop);
        // `none` and `hidden` zero the edge whatever border-width says. The
        // initial border-style is `none`, so an author who sets only
        // border-width gets no border at all — which is correct, and a
        // frequent surprise.
        if (s.empty() || s == "none" || s == "hidden") return 0.0;
        return resolve_border_width(get(style, width_prop), font_size, ctx);
    };
    ResolvedSides r;
    r.top = edge("border-top-style", "border-top-width");
    r.right = edge("border-right-style", "border-right-width");
    r.bottom = edge("border-bottom-style", "border-bottom-width");
    r.left = edge("border-left-style", "border-left-width");
    return r;
}

bool is_border_box(const ComputedStyle* style) {
    return get(style, "box-sizing") == "border-box";
}

PositionType parse_position_type(std::string_view raw) {
    if (iequals(raw, "relative")) return PositionType::Relative;
    if (iequals(raw, "absolute")) return PositionType::Absolute;
    if (iequals(raw, "fixed")) return PositionType::Fixed;
    if (iequals(raw, "sticky")) return PositionType::Sticky;
    return PositionType::Static;
}

double apply_box_model(BoxTree* tree, BoxId id, double containing_block_width,
                       const ComputedStyle* parent_style, const LayoutContext& ctx) {
    Box& box = (*tree)[id];
    const ComputedStyle* style = box.style;

    const double fs = font_size_px(style, parent_style, ctx);
    // Resolved once and threaded through everything below, so an `lh`-typed
    // padding, margin, width or height binds to the cascaded line-height
    // rather than the 1.2 x font-size fallback.
    const double lh = line_height_px(style, fs, ctx);

    const ResolvedSides pad =
        resolve_box_sides_px(style, "padding", ctx, fs, containing_block_width, lh);
    box.padding_top = pad.top;
    box.padding_right = pad.right;
    box.padding_bottom = pad.bottom;
    box.padding_left = pad.left;

    const ResolvedSides borders = resolve_border_edges(style, ctx, fs);
    box.border_top = borders.top;
    box.border_right = borders.right;
    box.border_bottom = borders.bottom;
    box.border_left = borders.left;

    const ResolvedSides mar =
        resolve_box_sides_px(style, "margin", ctx, fs, containing_block_width, lh);
    box.margin_top = mar.top;
    box.margin_right = mar.right;
    box.margin_bottom = mar.bottom;
    box.margin_left = mar.left;

    box.position = parse_position_type(get(style, "position"));

    const bool border_box = is_border_box(style);
    const double width_frame =
        box.padding_left + box.padding_right + box.border_left + box.border_right;
    const double height_frame =
        box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;

    const ResolvedLength width_r =
        resolve_length(get(style, "width"), ctx, fs, containing_block_width, lh);
    // The height is resolved WITHOUT a basis here, so a percentage surfaces as
    // Percent and is handled separately below — its basis is the parent's
    // height, not the containing block's width.
    const ResolvedLength height_r =
        resolve_length(get(style, "height"), ctx, fs, std::nullopt, lh);

    double avail = containing_block_width - (box.margin_left + box.margin_right);
    if (avail < 0) avail = 0;

    bool width_is_auto = width_r.kind == LengthKind::Auto;
    double resolved_width;

    double aspect_ratio = 0;
    const bool has_ratio = try_resolve_aspect_ratio(style, &aspect_ratio);
    const bool height_is_definite = height_r.kind == LengthKind::Length;

    if (width_is_auto && has_ratio && height_is_definite && height_r.pixels > 0) {
        // CSS Sizing L4 §5: with one of the two auto, the ratio derives the
        // other. The opposite direction (height from width) belongs to the
        // block-size finalisation, not here.
        resolved_width = height_r.pixels * aspect_ratio;
        if (resolved_width < 0) resolved_width = 0;
        width_is_auto = false;
    } else if (width_is_auto) {
        // An auto width fills the available space. Inline-blocks shrink to fit
        // instead, but that needs intrinsic sizing, so they fill here too — the
        // C# has the same two branches with the same body.
        resolved_width = avail;
    } else if (width_r.kind == LengthKind::Length) {
        resolved_width = border_box ? width_r.pixels : width_r.pixels + width_frame;
    } else if (width_r.kind == LengthKind::Percent) {
        const double base = containing_block_width * width_r.percent * 0.01;
        resolved_width = border_box ? base : base + width_frame;
    } else {
        resolved_width = avail;
    }

    // CSS 2.1 §10.3.3: remember the fill width so a later max-width clamp can
    // tell "auto width that got clamped" from "auto width still filling". The
    // difference decides whether auto margins centre.
    const double auto_fill_width = resolved_width;

    const ResolvedLength min_r =
        resolve_length(get(style, "min-width"), ctx, fs, containing_block_width, lh);
    const ResolvedLength max_r =
        resolve_length(get(style, "max-width"), ctx, fs, containing_block_width, lh);

    // CSS Sizing L3 §5.2: when min exceeds max, MIN wins. Applying max first
    // and min second gets that for free — the min clamp raises the value back
    // above a smaller max.
    //
    // min- and max-width share width's box-sizing basis, so under content-box
    // the author wrote a CONTENT bound and the frame has to be added before
    // comparing against the border-box `resolved_width`.
    const auto clamp_max = [&](double px) {
        if (!border_box) px += width_frame;
        if (resolved_width > px) resolved_width = px;
    };
    const auto clamp_min = [&](double px) {
        if (!border_box) px += width_frame;
        if (resolved_width < px) resolved_width = px;
    };
    if (max_r.kind == LengthKind::Length) clamp_max(max_r.pixels);
    else if (max_r.kind == LengthKind::Percent) {
        clamp_max(containing_block_width * max_r.percent * 0.01);
    }
    if (min_r.kind == LengthKind::Length) clamp_min(min_r.pixels);
    else if (min_r.kind == LengthKind::Percent) {
        clamp_min(containing_block_width * min_r.percent * 0.01);
    }
    box.width = resolved_width;

    // Stamp a definite height early so descendants resolving a percentage
    // height against this box see the right basis. Without it they resolve
    // against zero, because the height is not otherwise known until the
    // block size is finalised.
    if (height_r.kind == LengthKind::Length) {
        double h = height_r.pixels;
        if (!border_box) h += height_frame;
        box.height = h < 0 ? 0 : h;
    } else if (height_r.kind == LengthKind::Percent) {
        if (const std::optional<double> basis = definite_content_height(*tree, box.parent)) {
            double h = *basis * height_r.percent * 0.01;
            if (!border_box) h += height_frame;
            box.height = h < 0 ? 0 : h;
        }
    }

    // CSS 2.1 §10.3.3 auto-margin centring. Excluded for:
    //   - floats, where §9.5.1 makes an auto margin zero;
    //   - inline-blocks, which are placed by the inline formatting context;
    //   - absolute and fixed boxes, whose auto inline margins resolve against
    //     the containing block's edges and are zero unless BOTH left and right
    //     are pinned. Without this exclusion, `position:absolute; left:50%;
    //     margin:0 auto` gets the in-flow centring margin added to its offset
    //     and lands far off centre.
    const bool out_of_flow =
        box.position == PositionType::Absolute || box.position == PositionType::Fixed;
    // A still-filling auto width leaves no free space to absorb, so it does not
    // centre. An auto width that a max-width clamp shrank below its fill width
    // does: the gap is free space, which is the `width:auto; max-width:X;
    // margin:0 auto` pattern.
    const bool auto_width_clamped = width_is_auto && resolved_width < auto_fill_width - 0.01;
    if (mar.left_raw == "auto" && mar.right_raw == "auto" &&
        (!width_is_auto || auto_width_clamped) && !box.is_inline_block && !box.is_float() &&
        !out_of_flow) {
        const double extra = containing_block_width - resolved_width;
        if (extra > 0) {
            box.margin_left = extra * 0.5;
            box.margin_right = extra * 0.5;
        }
    }
    return fs;
}


// ---- Margin collapsing ---------------------------------------------------

double collapse_margins(double a, double b) {
    // A NaN margin (a bad calc(), a NaN-producing animated length) fails both
    // sign tests and would fall through to `a + b`, which is also NaN — and
    // that NaN then propagates through the whole chain, corrupting every block
    // below. Treat it as absent instead. Finite inputs never reach these two
    // branches.
    if (std::isnan(a)) return std::isnan(b) ? 0.0 : b;
    if (std::isnan(b)) return a;
    if (a >= 0 && b >= 0) return a > b ? a : b;
    if (a <= 0 && b <= 0) return a < b ? a : b;
    return a + b;
}

bool is_out_of_flow(const Box& b) {
    return b.position == PositionType::Absolute || b.position == PositionType::Fixed;
}

bool participates_in_flow(const Box& b) {
    if (b.is_inline_block) return false;
    // CSS 2.1 §8.3.1 rule 5: a float's margins collapse with nothing. They
    // apply verbatim and the float is not part of a sibling's chain.
    if (b.is_float()) return false;
    return !is_out_of_flow(b);
}

bool establishes_new_bfc(const Box& b) {
    if (!b.style) return false;
    // The `overflow` shorthand expands to overflow-x / overflow-y, so the
    // `overflow` slot itself normally holds its initial value even when the
    // author wrote `overflow: hidden`. Both axis longhands have to be checked.
    const std::string_view ox = get(b.style, "overflow-x");
    if (!ox.empty() && ox != "visible") return true;
    const std::string_view oy = get(b.style, "overflow-y");
    if (!oy.empty() && oy != "visible") return true;

    switch (b.display) {
        case DisplayKind::FlowRoot:
        case DisplayKind::Flex:
        case DisplayKind::InlineFlex:
        case DisplayKind::Grid:
        case DisplayKind::InlineGrid:
        case DisplayKind::InlineBlock:
        case DisplayKind::Table:
        case DisplayKind::InlineTable:
        case DisplayKind::TableCell:
        case DisplayKind::TableCaption:
            return true;
        default:
            break;
    }
    if (b.position == PositionType::Absolute || b.position == PositionType::Fixed) return true;
    const std::string_view f = get(b.style, "float");
    return f == "left" || f == "right" || f == "inline-start" || f == "inline-end";
}

bool parent_top_open(const Box& b) {
    if (establishes_new_bfc(b)) return false;
    // Only padding, border or a BFC closes the top. An explicit height does
    // NOT — it blocks bottom collapsing only, which is a distinction easy to
    // lose and one the reference records having got wrong once.
    return b.padding_top <= 0 && b.border_top <= 0;
}

bool parent_bottom_open(const Box& b) {
    if (establishes_new_bfc(b)) return false;
    return b.padding_bottom <= 0 && b.border_bottom <= 0;
}

bool parent_height_auto(const Box& b, const LayoutContext& ctx, double font_size) {
    if (!b.style) return true;
    const auto blocks = [&](std::string_view property, bool percent_must_be_positive) {
        const std::string_view raw = get(b.style, property);
        if (raw.empty() || raw == "auto") return false;
        const ResolvedLength r = resolve_length(raw, ctx, font_size, std::nullopt);
        if (r.kind == LengthKind::Length && r.pixels > 0) return true;
        if (r.kind == LengthKind::Percent && (!percent_must_be_positive || r.percent > 0)) {
            return true;
        }
        return false;
    };
    if (blocks("height", false)) return false;
    if (blocks("min-height", true)) return false;
    return true;
}

bool is_self_collapsing(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                        double font_size) {
    const Box& b = tree[id];
    if (b.padding_top > 0 || b.padding_bottom > 0) return false;
    if (b.border_top > 0 || b.border_bottom > 0) return false;
    if (b.style) {
        const auto has_size = [&](std::string_view property) {
            const std::string_view raw = get(b.style, property);
            if (raw.empty() || raw == "auto" || raw == "0") return false;
            const ResolvedLength r = resolve_length(raw, ctx, font_size, std::nullopt);
            if (r.kind == LengthKind::Length && r.pixels > 0) return true;
            return r.kind == LengthKind::Percent && r.percent > 0;
        };
        if (has_size("height") || has_size("min-height")) return false;
    }
    // Any in-flow child at all disqualifies it. A child that does not
    // participate in flow (float, out-of-flow) is skipped, so a box holding
    // only floats still self-collapses.
    for (BoxId c : tree.children(id)) {
        if (tree[c].kind == BoxKind::Block && !participates_in_flow(tree[c])) continue;
        return false;
    }
    return true;
}

// ---- Block flow ----------------------------------------------------------


// ---- Floats --------------------------------------------------------------

FloatType parse_float_type(std::string_view raw) {
    if (iequals(raw, "left") || iequals(raw, "inline-start")) return FloatType::Left;
    if (iequals(raw, "right") || iequals(raw, "inline-end")) return FloatType::Right;
    return FloatType::None;
}

ClearType parse_clear_type(std::string_view raw) {
    if (iequals(raw, "left") || iequals(raw, "inline-start")) return ClearType::Left;
    if (iequals(raw, "right") || iequals(raw, "inline-end")) return ClearType::Right;
    if (iequals(raw, "both")) return ClearType::Both;
    return ClearType::None;
}

double FloatContext::left_extent_at(double y) const {
    double max = 0;
    for (const Entry& f : floats_) {
        if (f.side != FloatType::Left) continue;
        // Half-open in y: a float ending exactly at `y` no longer intrudes.
        if (y < f.top || y >= f.bottom) continue;
        if (f.right > max) max = f.right;
    }
    return max;
}

double FloatContext::right_extent_at(double y, double cb_width) const {
    double max = 0;
    for (const Entry& f : floats_) {
        if (f.side != FloatType::Right) continue;
        if (y < f.top || y >= f.bottom) continue;
        const double inward = cb_width - f.left;
        if (inward > max) max = inward;
    }
    return max;
}

double FloatContext::find_placement_y(double y, double width, FloatType side,
                                      double cb_width) const {
    (void)side;   // both sides compete for the same free horizontal band
    if (width <= 0) return y;
    double current = y;
    for (;;) {
        const double avail = cb_width - left_extent_at(current) - right_extent_at(current, cb_width);
        if (avail >= width) return current;
        // Step down to the next row where an intruding float ends, since that
        // is the only place free space can increase.
        double next = 0;
        bool found = false;
        for (const Entry& f : floats_) {
            if (f.bottom > current && (!found || f.bottom < next)) {
                next = f.bottom;
                found = true;
            }
        }
        // Nothing left to wait for: the float overflows at the last row tried,
        // which is what the spec asks for rather than an error.
        if (!found) return current;
        current = next;
    }
}

double FloatContext::clear_bottom(ClearType c) const {
    if (c == ClearType::None) return 0;
    double max = 0;
    for (const Entry& f : floats_) {
        const bool match = c == ClearType::Both ||
                           (c == ClearType::Left && f.side == FloatType::Left) ||
                           (c == ClearType::Right && f.side == FloatType::Right);
        if (match && f.bottom > max) max = f.bottom;
    }
    return max;
}

double FloatContext::max_bottom() const {
    double max = 0;
    for (const Entry& f : floats_) {
        if (f.bottom > max) max = f.bottom;
    }
    return max;
}

void BlockLayout::layout_float_box(BoxId id, double containing_block_width) {
    const BoxId parent = (*tree_)[id].parent;
    const ComputedStyle* parent_style = parent == kNoBox ? nullptr : (*tree_)[parent].style;
    // §9.5.1: a float with `width: auto` shrinks to fit. Letting it take the
    // containing block's width would make it fill the line and defeat the
    // point of floating it.
    shrink_to_fit(id, containing_block_width, parent_style);
}

void BlockLayout::place_float(BoxId container, BoxId float_box, double top_y,
                              double content_w) {
    Box& f = (*tree_)[float_box];
    const Box& c = (*tree_)[container];

    // A float honours `clear` too: its top margin edge sits below any matching
    // float already in this BFC.
    if (current_floats_) {
        const double clear_local = current_floats_->clear_bottom(f.clear) - bfc_origin_y_;
        if (clear_local > top_y) top_y = clear_local;
    }

    const double bfc_content_left = bfc_origin_x_ + c.padding_left + c.border_left;
    const double margin_box_w = f.margin_left + f.width + f.margin_right;
    const double margin_box_h = f.margin_top + f.height + f.margin_bottom;
    double bfc_top = bfc_origin_y_ + top_y;

    if (current_floats_) {
        bfc_top = current_floats_->find_placement_y(bfc_top, margin_box_w, f.float_type,
                                                    content_w);
    }
    const double left_in = current_floats_ ? current_floats_->left_extent_at(bfc_top) : 0;
    const double right_in =
        current_floats_ ? current_floats_->right_extent_at(bfc_top, content_w) : 0;

    const double bfc_x = f.float_type == FloatType::Left
                             ? bfc_content_left + left_in + f.margin_left
                             : bfc_content_left + content_w - right_in - margin_box_w +
                                   f.margin_left;

    // Written back in coordinates local to the container, which is what every
    // other box in the tree uses.
    f.x = bfc_x - bfc_origin_x_;
    f.y = bfc_top - bfc_origin_y_ + f.margin_top;

    if (current_floats_) {
        FloatContext::Entry e;
        e.box = float_box;
        e.side = f.float_type;
        e.left = bfc_x - f.margin_left;
        e.right = e.left + margin_box_w;
        e.top = bfc_top;
        e.bottom = e.top + margin_box_h;
        current_floats_->add(e);
    }
}


void BlockLayout::size_atoms(std::vector<InlineItem>* items, double available_width,
                             const ComputedStyle* parent_style) {
    for (InlineItem& it : *items) {
        if (!it.is_atom()) continue;
        shrink_to_fit(it.atom_box, available_width, parent_style);
        const Box& a = (*tree_)[it.atom_box];
        it.atom_outer_width = a.margin_left + a.width + a.margin_right;
        // CSS 2.1 §10.8.1: an inline-block's baseline is the baseline of its
        // last line box, or — when it has none, or its overflow is not visible
        // — its bottom MARGIN edge. Only the second case is handled here; the
        // first needs the atom's own line boxes to be inspected, which is a
        // later slice.
        it.atom_baseline = a.margin_top + a.height + a.margin_bottom;
    }
}

double BlockLayout::shrink_to_fit(BoxId id, double available_width,
                                  const ComputedStyle* parent_style) {
    const double fs = apply_box_model(tree_, id, available_width, parent_style, ctx_);
    const ComputedStyle* style = (*tree_)[id].style;
    const ResolvedLength w =
        resolve_length(get(style, "width"), ctx_, fs, available_width);
    if (w.kind != LengthKind::Auto) {
        // An explicit width needs no probing: apply_box_model already resolved
        // it, so just lay the contents out inside it.
        layout_content(id, fs, available_width, parent_style);
        return fs;
    }

    // CSS 2.1 §10.3.5 shrink-to-fit:
    //     min(max-content, max(min-content, available))
    // Both intrinsic sizes are measured by laying the content out at an extreme
    // width and reading back what it wanted. A huge probe makes every line as
    // long as it can be (max-content); a probe of 1 forces a break at every
    // opportunity, so the widest line is the longest unbreakable run
    // (min-content).
    const Box& b = (*tree_)[id];
    const double frame = b.padding_left + b.padding_right + b.border_left + b.border_right;
    const double margin_x = b.margin_left + b.margin_right;
    double avail = available_width - margin_x;
    if (avail < 0) avail = 0;

    relayout_content_at(id, 1e6, fs, parent_style);
    double max_content = max_content_width(*tree_, id) + frame;
    relayout_content_at(id, 1, fs, parent_style);
    double min_content = max_content_width(*tree_, id) + frame;
    if (max_content < frame) max_content = frame;
    if (min_content < frame) min_content = frame;

    double fitted = std::min(max_content, std::max(min_content, avail));
    if (fitted > avail) fitted = avail;
    if (fitted < 0) fitted = 0;

    // §10.3.5: the shrink-to-fit result is still clamped by min- and max-width,
    // which share width's box-sizing basis.
    const bool border_box = is_border_box(style);
    const ResolvedLength min_r =
        resolve_length(get(style, "min-width"), ctx_, fs, available_width);
    const ResolvedLength max_r =
        resolve_length(get(style, "max-width"), ctx_, fs, available_width);
    const auto to_border_box = [&](const ResolvedLength& r) {
        double px = r.kind == LengthKind::Percent ? available_width * r.percent * 0.01
                                                  : r.pixels;
        if (!border_box) px += frame;
        return px;
    };
    if (max_r.kind == LengthKind::Length || max_r.kind == LengthKind::Percent) {
        const double px = to_border_box(max_r);
        if (fitted > px) fitted = px;
    }
    if (min_r.kind == LengthKind::Length || min_r.kind == LengthKind::Percent) {
        const double px = to_border_box(min_r);
        if (fitted < px) fitted = px;
    }

    relayout_content_at(id, fitted, fs, parent_style);
    return fs;
}

void BlockLayout::relayout_content_at(BoxId id, double width, double font_size,
                                      const ComputedStyle* parent_style) {
    (*tree_)[id].width = width;
    // The first inline pass replaced the container's children with line boxes,
    // so the source runs can no longer be walked. The collected items are
    // cached per container for the duration of the pass and reused, which is
    // both cheaper and simpler than snapshotting and restoring the child list.
    if ((*tree_)[id].contains_inlines) {
        if (metrics_) {
            auto it = inline_items_.find(id);
            if (it == inline_items_.end()) {
                std::vector<InlineItem> items = collect_inline_items(*tree_, id, ctx_);
                size_atoms(&items, width, parent_style);
                it = inline_items_.emplace(id, std::move(items)).first;
            }
            const double h = layout_inline_items(tree_, id, it->second,
                                                 (*tree_)[id].content_width(), ctx_, *metrics_);
            finalize_block_size(id, font_size, (*tree_)[id].padding_top +
                                                   (*tree_)[id].border_top + h);
        }
        return;
    }
    layout_content(id, font_size, width, parent_style);
}

void BlockLayout::layout_root(BoxId root, double viewport_width, double viewport_height) {
    Box& b = (*tree_)[root];
    b.x = 0;
    b.y = 0;
    b.width = viewport_width;
    // Seeding the height is what makes `html, body { height: 100% }` work: a
    // percentage height resolves only against a DEFINITE basis, so without this
    // the chain has no starting point and body falls through to content height.
    // finalize_block_size collapses the root back to its content afterwards.
    b.height = viewport_height;
    layout_content(root, ctx_.root_font_size_px, viewport_width, nullptr);
}

void BlockLayout::layout_block(BoxId id, double available_width,
                               const ComputedStyle* parent_style) {
    double fs;
    if ((*tree_)[id].style) {
        fs = apply_box_model(tree_, id, available_width, parent_style, ctx_);
    } else {
        (*tree_)[id].width = available_width;
        fs = ctx_.root_font_size_px;
    }
    const Box& b = (*tree_)[id];
    if (b.first_child == kNoBox) {
        finalize_block_size(id, fs, b.padding_top + b.border_top);
        return;
    }
    layout_content(id, fs, available_width, parent_style);
}

void BlockLayout::layout_content(BoxId id, double font_size, double containing_block_width,
                                 const ComputedStyle* parent_style) {
    // Both are the CALLER's context; children resolve against this box's own
    // style and content width instead. Kept in the signature to mirror the
    // reference, where the float and inline paths still need them.
    (void)containing_block_width;
    (void)parent_style;
    const double top_inner = (*tree_)[id].padding_top + (*tree_)[id].border_top;
    const double content_w = (*tree_)[id].content_width();
    const ComputedStyle* own_style = (*tree_)[id].style;

    // A container of inline content is laid out by the inline formatting
    // context, which is a later slice. Branching here rather than letting the
    // block loop walk inline children keeps the limitation explicit: such a box
    // reports ZERO content height until inline layout lands, instead of a
    // number derived from the wrong algorithm.
    //
    // It also keeps the block loop's inline-block branch honest. The
    // anonymous-block pass classifies an inline-block as inline, so it is
    // always wrapped — meaning that branch is unreachable from here, in this
    // port and in the reference alike, and exists defensively.
    if ((*tree_)[id].contains_inlines) {
        // Without a font backend nothing can be measured, so the box reports
        // zero content height rather than a number derived from guessing.
        double inline_h = 0;
        if (metrics_) {
            std::vector<InlineItem> items = collect_inline_items(*tree_, id, ctx_);
            size_atoms(&items, content_w, own_style);
            inline_h = layout_inline_items(tree_, id, items, content_w, ctx_, *metrics_);
        }
        finalize_block_size(id, font_size, top_inner + inline_h);
        return;
    }
    // Nothing below this point may return early without restoring the float
    // context — the single exit at the end of the function is what guarantees
    // it, so keep it that way.

    // A new block formatting context gets its own float context; otherwise this
    // box's children join the ancestor BFC's, so a float placed here is still
    // avoided by content further down that BFC.
    const bool establishes_bfc =
        !own_style || establishes_new_bfc((*tree_)[id]) || (*tree_)[id].is_float() ||
        (*tree_)[id].is_inline_block;
    FloatContext* const prev_floats = current_floats_;
    const double prev_bfc_x = bfc_origin_x_;
    const double prev_bfc_y = bfc_origin_y_;
    FloatContext own_floats;
    if (establishes_bfc) {
        current_floats_ = &own_floats;
        bfc_origin_x_ = 0;
        bfc_origin_y_ = 0;
    } else {
        // Where this box sits inside the BFC, so a float it records lands in
        // the BFC's frame rather than its own.
        bfc_origin_x_ = prev_bfc_x + (*tree_)[id].x;
        bfc_origin_y_ = prev_bfc_y + (*tree_)[id].y;
    }

    // Children are laid out first so their heights are known before any of
    // them is placed. Floats are also PLACED here, against a running estimate
    // of the cursor, so the float context is populated before a later sibling
    // lays out content that has to flow around them. The estimate ignores
    // margin collapsing, which the placement loop below applies exactly; the
    // cost of a mismatch is at most one float row, and the reference accepts
    // the same trade.
    std::vector<BoxId> inflow;
    double approx_cursor = top_inner;
    for (BoxId c : tree_->children(id)) {
        // Only block-level children reach here: a container holding inline
        // content returned above.
        if (tree_->operator[](c).kind != BoxKind::Block &&
            tree_->operator[](c).kind != BoxKind::AnonymousBlock) {
            continue;
        }
        // Stamped before layout: the placement loop reads them, and
        // apply_box_model consults is_float() to skip auto-margin centring.
        {
            Box& cb = (*tree_)[c];
            cb.float_type = parse_float_type(get(cb.style, "float"));
            cb.clear = parse_clear_type(get(cb.style, "clear"));
        }
        if ((*tree_)[c].is_float()) {
            layout_float_box(c, content_w);
            if (current_floats_) place_float(id, c, approx_cursor, content_w);
        } else {
            // Descendants may query the float context during their own layout
            // and need a meaningful BFC-local y, so the estimate is stamped
            // before recursing. The placement loop replaces it with the exact
            // value.
            (*tree_)[c].y = approx_cursor + (*tree_)[c].margin_top;
            layout_block(c, content_w, own_style);
            const Box& cb = (*tree_)[c];
            approx_cursor += cb.margin_top + cb.height + cb.margin_bottom;
        }
        inflow.push_back(c);
    }

    // The synthetic root has no style and no margins of its own: it is the
    // viewport edge, and nothing collapses through it.
    const bool parent_participates = own_style && participates_in_flow((*tree_)[id]);
    const bool top_open = parent_participates && parent_top_open((*tree_)[id]);
    const bool bottom_open = parent_participates && parent_bottom_open((*tree_)[id]) &&
                             parent_height_auto((*tree_)[id], ctx_, font_size);

    double cursor = top_inner;

    // CSS 2.1 §8.3.1: across a chain of N adjoining margins the result is
    // max(positives) + min(negatives). Folding pairwise with collapse_margins()
    // is associative for a same-sign chain but WRONG for a mixed-sign chain
    // longer than two — {+20, -15, +10, -25} folds left to -10 where the spec
    // gives -5. So the running max and min are tracked and combined once, when
    // the chain closes.
    double chain_max_pos = 0;
    double chain_min_neg = 0;
    // A leading chain attaches to the parent's own margin-top when the parent's
    // top is open; otherwise it is a literal gap before the first child.
    bool chain_attaches_to_parent_top = top_open;
    if (top_open) {
        const double mt = (*tree_)[id].margin_top;
        if (mt > 0) chain_max_pos = mt;
        else if (mt < 0) chain_min_neg = mt;
    }
    bool any_collapsible_seen = false;

    for (BoxId cid : inflow) {
        Box& c = (*tree_)[cid];
        const double left_inner = (*tree_)[id].padding_left + (*tree_)[id].border_left;

        // A float was placed above. It does not advance the cursor, and it does
        // not join the collapse chain either — the chain continues through to
        // the next in-flow box as if the float were not there (§8.3.1 rule 5).
        if (c.is_float()) {
            any_collapsible_seen = true;
            continue;
        }
        // §9.5.2: `clear` pushes the box's TOP MARGIN edge below the bottom of
        // any matching float, even where margin collapsing would place it
        // higher. The chain that has accumulated so far is spent on the move
        // rather than added to it, so it resets.
        if (c.clear != ClearType::None && current_floats_) {
            const double clear_local = current_floats_->clear_bottom(c.clear) - bfc_origin_y_;
            const double would_be_top = cursor + chain_max_pos + chain_min_neg;
            if (clear_local > would_be_top) {
                if (chain_attaches_to_parent_top) {
                    (*tree_)[id].margin_top = chain_max_pos + chain_min_neg;
                    chain_attaches_to_parent_top = false;
                }
                cursor = clear_local;
                chain_max_pos = 0;
                chain_min_neg = 0;
            }
        }
        // An out-of-flow box's margins apply verbatim and never collapse.
        if (is_out_of_flow(c)) {
            c.x = left_inner + c.margin_left;
            c.y = cursor + c.margin_top;
            continue;
        }
        // An inline-block participates in the flow but its margins do NOT
        // collapse — they apply as written on both sides.
        if (c.is_inline_block) {
            double gap = chain_max_pos + chain_min_neg;
            if (chain_attaches_to_parent_top) {
                (*tree_)[id].margin_top = gap;
                gap = 0;
                chain_attaches_to_parent_top = false;
            }
            c.x = left_inner + c.margin_left;
            c.y = cursor + gap + c.margin_top;
            cursor = c.y + c.height + c.margin_bottom;
            chain_max_pos = 0;
            chain_min_neg = 0;
            any_collapsible_seen = true;
            continue;
        }

        const double child_top = c.margin_top;
        const double child_bottom = c.margin_bottom;
        if (child_top > chain_max_pos) chain_max_pos = child_top;
        if (child_top < chain_min_neg) chain_min_neg = child_top;

        // A self-collapsing block adds BOTH its margins to the active chain and
        // contributes no height, so the chain passes straight through it.
        if (is_self_collapsing(*tree_, cid, ctx_, font_size)) {
            if (child_bottom > chain_max_pos) chain_max_pos = child_bottom;
            if (child_bottom < chain_min_neg) chain_min_neg = child_bottom;
            c.x = left_inner + c.margin_left;
            c.y = chain_attaches_to_parent_top ? cursor : cursor + chain_max_pos + chain_min_neg;
            any_collapsible_seen = true;
            continue;
        }

        // The chain closes here: realise the collapsed gap and place the child.
        const double gap = chain_max_pos + chain_min_neg;
        if (chain_attaches_to_parent_top) {
            // The combined margin lives OUTSIDE the parent, on its margin-top,
            // and the child sits flush against the inner edge.
            (*tree_)[id].margin_top = gap;
            c.y = cursor;
            chain_attaches_to_parent_top = false;
        } else {
            c.y = cursor + gap;
        }
        c.x = left_inner + c.margin_left;
        cursor = c.y + c.height;
        // The next chain starts with this child's bottom margin.
        chain_max_pos = child_bottom > 0 ? child_bottom : 0;
        chain_min_neg = child_bottom < 0 ? child_bottom : 0;
        any_collapsible_seen = true;
    }

    // Whatever is left of the chain either collapses into the parent's own
    // bottom margin, or sits as a literal gap that pushes the content bottom
    // down.
    const double trailing = chain_max_pos + chain_min_neg;
    double content_bottom;
    if (bottom_open && any_collapsible_seen) {
        Box& p = (*tree_)[id];
        if (chain_attaches_to_parent_top) {
            // Every in-flow child self-collapsed and the parent's top was open,
            // so one chain spans the parent top to bottom: the parent's own two
            // margins collapse together with it.
            if (p.margin_bottom > chain_max_pos) chain_max_pos = p.margin_bottom;
            if (p.margin_bottom < chain_min_neg) chain_min_neg = p.margin_bottom;
            p.margin_top = chain_max_pos + chain_min_neg;
            p.margin_bottom = 0;
        } else {
            if (p.margin_bottom > 0 && p.margin_bottom > chain_max_pos) {
                chain_max_pos = p.margin_bottom;
            }
            if (p.margin_bottom < 0 && p.margin_bottom < chain_min_neg) {
                chain_min_neg = p.margin_bottom;
            }
            p.margin_bottom = chain_max_pos + chain_min_neg;
        }
        content_bottom = cursor;
    } else if (chain_attaches_to_parent_top) {
        // The top chain never closed — there were no placed children — so it
        // becomes the parent's margin-top and the cursor stays at the inner
        // edge.
        (*tree_)[id].margin_top = trailing;
        content_bottom = cursor;
    } else {
        content_bottom = cursor + trailing;
    }

    // §10.6.7: a BFC root grows to enclose the floats inside it. Since the root
    // of this BFC is this box, BFC-local y and box-local y coincide.
    if (establishes_bfc && current_floats_) {
        const double fb = current_floats_->max_bottom();
        if (fb > content_bottom) content_bottom = fb;
    }

    finalize_block_size(id, font_size, content_bottom);

    current_floats_ = prev_floats;
    bfc_origin_x_ = prev_bfc_x;
    bfc_origin_y_ = prev_bfc_y;
}

void BlockLayout::finalize_block_size(BoxId id, double font_size, double content_bottom_y) {
    Box& box = (*tree_)[id];
    if (!box.style) {
        // The synthetic root was seeded with the viewport height so percentage
        // heights had a basis; now that its children are placed it must
        // collapse back to their actual bottom, or a two-div page would report
        // the full viewport height. An anonymous wrapper keeps a height that
        // was already stamped for it.
        if (box.parent == kNoBox || box.height == 0) {
            box.height = content_bottom_y + box.padding_bottom + box.border_bottom;
        }
        return;
    }

    // CSS 2.1 §10.5: a percentage height resolves only against a containing
    // block with a DEFINITE height; an indefinite parent makes it compute to
    // auto. An out-of-flow box's containing block is not known here at all, so
    // it gets no basis rather than the wrong one.
    const std::optional<double> basis =
        is_out_of_flow(box) ? std::nullopt : definite_content_height(*tree_, box.parent);
    const ResolvedLength height_r =
        resolve_length(get(box.style, "height"), ctx_, font_size, basis);

    const bool border_box = is_border_box(box.style);
    const double frame =
        box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;

    double computed;
    double aspect_ratio = 0;
    if (height_r.kind == LengthKind::Length) {
        computed = border_box ? height_r.pixels : height_r.pixels + frame;
    } else if (try_resolve_aspect_ratio(box.style, &aspect_ratio) && aspect_ratio > 0 &&
               box.width > 0) {
        // Width set, height auto: the ratio derives the height. As on the width
        // side, box-sizing is ignored for the derivation.
        computed = box.width / aspect_ratio;
    } else {
        computed = content_bottom_y + box.padding_bottom + box.border_bottom;
    }

    // min-/max-height share height's box-sizing basis, so a content-box bound
    // needs the frame added before it is compared with the border-box value.
    const ResolvedLength min_r =
        resolve_length(get(box.style, "min-height"), ctx_, font_size, std::nullopt);
    const ResolvedLength max_r =
        resolve_length(get(box.style, "max-height"), ctx_, font_size, std::nullopt);
    if (min_r.kind == LengthKind::Length) {
        const double px = border_box ? min_r.pixels : min_r.pixels + frame;
        if (computed < px) computed = px;
    }
    if (max_r.kind == LengthKind::Length) {
        const double px = border_box ? max_r.pixels : max_r.pixels + frame;
        if (computed > px) computed = px;
    }
    box.height = computed;

    // A <button> vertically centres a single line of content inside an explicit
    // height, matching Chrome. A button with an author `display: flex/grid` is
    // laid out elsewhere, so this only ever sees the default display; and for an
    // auto-height button the delta is zero, making it a no-op.
    if (box.element && box.element->tag_name() == "button" && box.first_child != kNoBox) {
        const double content_box_h = computed - frame;
        const double natural_h = content_bottom_y - (box.padding_top + box.border_top);
        const double delta = (content_box_h - natural_h) * 0.5;
        if (delta > 0.5) {
            for (BoxId c : tree_->children(id)) (*tree_)[c].y += delta;
        }
    }
}

} // namespace weva
