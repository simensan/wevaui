#include "weva/block_layout.h"

#include "weva/css_properties.h"

#include <cmath>
#include <string>

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

} // namespace weva
