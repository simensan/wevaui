#include "weva/image_store.h"
#include "weva/block_layout.h"

#include "weva/flex.h"
#include "weva/grid.h"
#include "weva/table_layout.h"
#include "weva/multicol.h"
#include "weva/positioning.h"

#include "weva/css_properties.h"
#include "weva/inline_layout.h"
#include "weva/form_state.h"

#include <algorithm>

#include <cmath>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace weva {

namespace {

struct LayoutProfile {
    struct Part { double ms = 0; size_t calls = 0; };
    Part parts[6];
};
thread_local LayoutProfile* active_layout_profile = nullptr;

struct LayoutProfileRoot {
    LayoutProfile profile;
    LayoutProfile* previous = active_layout_profile;
    bool enabled;
    LayoutProfileRoot() {
        static const bool log = std::getenv("WEVA_LAYOUT_LOG") != nullptr;
        enabled = log;
        if (enabled) active_layout_profile = &profile;
    }
    ~LayoutProfileRoot() {
        if (!enabled) return;
        active_layout_profile = previous;
        static constexpr const char* names[] = {"box model", "inline", "finalize",
                                               "collect", "atoms", "line build"};
        for (size_t i = 0; i < 6; ++i)
            std::fprintf(stderr, "    layout work: %-10s %.3f ms; %zu calls (inclusive)\n",
                names[i], profile.parts[i].ms, profile.parts[i].calls);
    }
};

struct LayoutProfilePart {
    using Clock = std::chrono::steady_clock;
    LayoutProfile::Part* part;
    Clock::time_point start;
    explicit LayoutProfilePart(size_t index)
        : part(active_layout_profile ? &active_layout_profile->parts[index] : nullptr) {
        if (part) { ++part->calls; start = Clock::now(); }
    }
    ~LayoutProfilePart() {
        if (part) part->ms += std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    }
};

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

// The same lookup by id. A property id is resolved once for the
// program below rather than hashed from its name on every call --
// sampling put ComputedStyle::get and CssPropertyRegistry::id_of
// together at a quarter of a layout pass, ahead of any layout
// algorithm. Safe because the registry keeps an id stable across
// re-registration, which is what its header promises it for.
const int kId_max_height = CssPropertyRegistry::instance().id_of("max-height");
const int kId_max_width = CssPropertyRegistry::instance().id_of("max-width");
const int kId_min_height = CssPropertyRegistry::instance().id_of("min-height");
const int kId_min_width = CssPropertyRegistry::instance().id_of("min-width");
const int kId_width = CssPropertyRegistry::instance().id_of("width");

const int kId_padding = CssPropertyRegistry::instance().id_of("padding");
const int kId_direction = CssPropertyRegistry::instance().id_of("direction");
const int kId_margin_left = CssPropertyRegistry::instance().id_of("margin-left");
const int kId_margin_right = CssPropertyRegistry::instance().id_of("margin-right");
const int kId_margin = CssPropertyRegistry::instance().id_of("margin");

std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

// Resolved at static-init. The registry is a function-local static,
// so it is constructed on first use and these cannot outrun it.
const int kId_clear = CssPropertyRegistry::instance().id_of("clear");
const int kId_contain = CssPropertyRegistry::instance().id_of("contain");
const int kId_container_type = CssPropertyRegistry::instance().id_of("container-type");
const int kId_contain_intrinsic_height = CssPropertyRegistry::instance().id_of("contain-intrinsic-height");
const int kId_contain_intrinsic_size = CssPropertyRegistry::instance().id_of("contain-intrinsic-size");
const int kId_contain_intrinsic_width = CssPropertyRegistry::instance().id_of("contain-intrinsic-width");
const int kId_content_visibility = CssPropertyRegistry::instance().id_of("content-visibility");
const int kId_float = CssPropertyRegistry::instance().id_of("float");
const int kId_height = CssPropertyRegistry::instance().id_of("height");
const int kId_overflow_x = CssPropertyRegistry::instance().id_of("overflow-x");
const int kId_overflow_y = CssPropertyRegistry::instance().id_of("overflow-y");
const int kId_border_top_style = CssPropertyRegistry::instance().id_of("border-top-style");
const int kId_border_right_style = CssPropertyRegistry::instance().id_of("border-right-style");
const int kId_border_bottom_style = CssPropertyRegistry::instance().id_of("border-bottom-style");
const int kId_border_left_style = CssPropertyRegistry::instance().id_of("border-left-style");
const int kId_border_top_width = CssPropertyRegistry::instance().id_of("border-top-width");
const int kId_border_right_width = CssPropertyRegistry::instance().id_of("border-right-width");
const int kId_border_bottom_width = CssPropertyRegistry::instance().id_of("border-bottom-width");
const int kId_border_left_width = CssPropertyRegistry::instance().id_of("border-left-width");


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

} // namespace

ResolvedSides resolve_box_sides_px(const ComputedStyle* style, std::string_view shorthand,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height) {
    return resolve_box_sides_px(style, CssPropertyRegistry::instance().id_of(shorthand), ctx,
                                font_size, containing_block_width, line_height);
}

ResolvedSides resolve_box_sides_px(const ComputedStyle* style, int shorthand_id,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height) {
    // A box with no style has no margins and no padding, and answering that
    // costs nothing. It was costing four parses: box_sides substitutes "0" for
    // every absent value, "0" is not a keyword, and with a null style there is
    // no memo to put the parsed result in -- so an anonymous box re-parsed the
    // same four zeroes on every layout pass. On randhtml that was 3,192 of the
    // pass's allocations, more than a fifth of the whole.
    if (!style) return ResolvedSides{};
    const BoxSideValues sides = box_sides(style, shorthand_id);

    // Nearly every box declares no margin and no padding, and box_sides
    // substitutes "0" for an absent side -- so the common case is four zeroes
    // that each go through keyword matching, a parsed-value lookup and a
    // length resolution to arrive back at zero. Recognising them costs four
    // string compares.
    //
    // `0` and `` only: `0px` and `0%` go the long way, because reading a unit
    // off a string is how the border-width fast path below grew subtle, and
    // the two forms that matter here carry no unit at all.
    const auto is_zero = [](std::string_view v) { return v.empty() || v == "0"; };
    if (is_zero(sides.top) && is_zero(sides.right) && is_zero(sides.bottom) &&
        is_zero(sides.left)) {
        ResolvedSides zero;
        zero.right_raw = sides.right;
        zero.left_raw = sides.left;
        return zero;
    }
    ResolvedSides r;
    // The containing block's WIDTH is the basis on all four edges — a
    // percentage margin-top resolves against width, not height.
    const auto side = [&](std::string_view raw, int id, int part) {
        ResolvedLength v;
        // A side from the shorthand reads the shorthand's memoised parse; a
        // side from its own longhand reads that longhand's. Only a keyword or
        // a value with neither -- which the fallback below covers -- is parsed
        // at all, and never twice.
        if (const CssValue* component = shorthand_component(style, sides.shorthand_id, part)) {
            // A keyword component -- `margin: auto` -- parses to a Keyword and
            // resolve_length_value answers `auto` for it, so there is no
            // separate keyword branch to keep in step with the other path.
            v = resolve_length_value(component, ctx, font_size, containing_block_width,
                                     line_height);
        } else {
            v = resolve_length_cached(style, id, raw, ctx, font_size, containing_block_width,
                                      line_height);
        }
        // Auto and unparseable both give 0: an auto margin contributes no space
        // of its own, and the centring rule reads the raw text instead.
        return v.kind == LengthKind::Length ? v.pixels : 0.0;
    };
    r.top = side(sides.top, sides.top_id, sides.top_part);
    r.right = side(sides.right, sides.right_id, sides.right_part);
    r.bottom = side(sides.bottom, sides.bottom_id, sides.bottom_part);
    r.left = side(sides.left, sides.left_id, sides.left_part);
    r.right_raw = sides.right;
    r.left_raw = sides.left;
    return r;
}

ResolvedSides resolve_border_edges(const ComputedStyle* style, const LayoutContext& ctx,
                                   double font_size) {
    // By id. Each of these was a name hashed and probed in the registry index,
    // eight of them for every box on every pass -- caller attribution put this
    // lambda at the top of what still reached id_of once the bulk rewrite was
    // done, because the rewrite matched `get(style, "literal")` and these
    // names arrive as parameters.
    const auto edge = [&](int style_id, int width_id) {
        const std::string_view s = get(style, style_id);
        // `none` and `hidden` zero the edge whatever border-width says. The
        // initial border-style is `none`, so an author who sets only
        // border-width gets no border at all — which is correct, and a
        // frequent surprise.
        if (s.empty() || s == "none" || s == "hidden") return 0.0;
        return resolve_border_width(style, width_id, font_size, ctx);
    };
    ResolvedSides r;
    r.top = edge(kId_border_top_style, kId_border_top_width);
    r.right = edge(kId_border_right_style, kId_border_right_width);
    r.bottom = edge(kId_border_bottom_style, kId_border_bottom_width);
    r.left = edge(kId_border_left_style, kId_border_left_width);
    return r;
}

PositionType parse_position_type(std::string_view raw) {
    if (iequals(raw, "relative")) return PositionType::Relative;
    if (iequals(raw, "absolute")) return PositionType::Absolute;
    if (iequals(raw, "fixed")) return PositionType::Fixed;
    if (iequals(raw, "sticky")) return PositionType::Sticky;
    return PositionType::Static;
}

double resolve_block_inline_offset(Box& box, double containing_width, bool rtl) {
    const bool auto_left = get(box.style, kId_margin_left) == "auto";
    const bool auto_right = get(box.style, kId_margin_right) == "auto";
    if (auto_left != auto_right) {
        // A single auto margin absorbs positive remaining space. In overflow
        // it becomes zero; the containing block's direction chooses the edge.
        const double fixed = auto_left ? box.margin_right : box.margin_left;
        const double remaining = std::max(0.0, containing_width - box.width - fixed);
        if (auto_left) box.margin_left = remaining;
        else box.margin_right = remaining;
    }
    return rtl ? containing_width - box.width - box.margin_right : box.margin_left;
}

double apply_box_model(BoxTree* tree, BoxId id, double containing_block_width,
                       const ComputedStyle* parent_style, const LayoutContext& ctx) {
    LayoutProfilePart profile(0);
    Box& box = (*tree)[id];
    const ComputedStyle* style = box.style;

    const double fs = font_size_px(style, parent_style, ctx);
    // Resolved once and threaded through everything below, so an `lh`-typed
    // padding, margin, width or height binds to the cascaded line-height
    // rather than the 1.2 x font-size fallback.
    const double lh = line_height_px(style, fs, ctx);

    const ResolvedSides pad =
        resolve_box_sides_px(style, kId_padding, ctx, fs, containing_block_width, lh);
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
        resolve_box_sides_px(style, kId_margin, ctx, fs, containing_block_width, lh);
    box.margin_top = mar.top;
    box.margin_right = mar.right;
    box.margin_bottom = mar.bottom;
    box.margin_left = mar.left;

    static const int kPosition = CssPropertyRegistry::instance().id_of("position");
    box.position =
        parse_position_type(style ? style->get(kPosition) : std::string_view());
    // A top-layer box establishes its containing block at the document root.
    // Non-absolute/fixed author positions have absolute used positioning.
    const Element* top_host = box.element ? box.element : box.pseudo_host;
    if (box.kind == BoxKind::Block && box.parent != kNoBox && (*tree)[box.parent].parent == kNoBox &&
        top_host && top_host->top_layer_order() && box.position != PositionType::Fixed)
        box.position = PositionType::Absolute;

    const bool border_box = is_border_box(style);
    const double width_frame =
        box.padding_left + box.padding_right + box.border_left + box.border_right;
    const double height_frame =
        box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;
    // A collapsed table's authored frame is not its used frame: padding is
    // ignored and winning shared borders contribute half widths. Defer that
    // floor until layout_table resolves the grid, retaining the requested width.
    static const int kBorderCollapse = CssPropertyRegistry::instance().id_of("border-collapse");
    const bool collapsed_table = (box.display == DisplayKind::Table || box.display == DisplayKind::InlineTable) &&
        iequals(get(style, kBorderCollapse), "collapse");
    const double width_floor = collapsed_table ? 0.0 : width_frame;

    const ResolvedLength width_r =
        resolve_length(style, kId_width, ctx, fs, containing_block_width, lh);
    // The height is resolved WITHOUT a basis here, so a percentage surfaces as
    // Percent and is handled separately below — its basis is the parent's
    // height, not the containing block's width.
    const ResolvedLength height_r =
        resolve_length(style, kId_height, ctx, fs, std::nullopt, lh);

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
        // block-size finalisation, not here. The ratio relates the boxes that
        // box-sizing names (§5.1): under content-box the authored content
        // height gives a content width and the frame goes on top.
        resolved_width = height_r.pixels * aspect_ratio;
        if (!border_box) resolved_width += width_frame;
        if (resolved_width < 0) resolved_width = 0;
        width_is_auto = false;
    } else if (width_is_auto) {
        // An auto width fills the available space. Inline-blocks shrink to fit
        // instead, but that needs intrinsic sizing, so they fill here too — the
        // C# has the same two branches with the same body.
        resolved_width = avail;
    } else if (width_r.kind == LengthKind::Length) {
        // CSS Box Sizing L3 §4.1: under `border-box` the used value is floored
        // so the CONTENT box does not go negative -- so the border box can
        // never be narrower than its own padding and border.
        //
        // Without the floor, `width: 0` with a 6px border each side gave a box
        // 0 wide instead of 12. That is not a corner case: it is the CSS
        // triangle idiom (`width: 0; height: 0` with transparent side borders)
        // under the `* { box-sizing: border-box }` that nearly every modern
        // stylesheet opens with, so the marker vanished entirely. The same
        // floor applies to a percentage, and to the height below.
        resolved_width = border_box ? std::max(width_r.pixels, width_floor)
                                    : width_r.pixels + width_frame;
    } else if (width_r.kind == LengthKind::Percent) {
        const double base = containing_block_width * width_r.percent * 0.01;
        resolved_width = border_box ? std::max(base, width_floor) : base + width_frame;
    } else {
        resolved_width = avail;
    }

    // CSS 2.1 §10.3.3: remember the fill width so a later max-width clamp can
    // tell "auto width that got clamped" from "auto width still filling". The
    // difference decides whether auto margins centre.
    const double auto_fill_width = resolved_width;

    const ResolvedLength min_r =
        resolve_length(style, kId_min_width, ctx, fs, containing_block_width, lh);
    const ResolvedLength max_r =
        resolve_length(style, kId_max_width, ctx, fs, containing_block_width, lh);

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
    // An intrinsic keyword as min-width / max-width is applied by
    // shrink_to_fit, which has the probes; layout_block routes there.
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

// Whether a box clips its overflow, which decides whether it exposes its
// contents' baseline. Both axis longhands are checked for the same reason
// establishes_new_bfc checks them: the shorthand expands, so the `overflow`
// slot itself usually still holds its initial value.
// CSS Containment L2 §2.2 / §4. Size containment makes a box size as though it
// had no contents; `content-visibility: hidden` additionally skips laying the
// contents out at all (it implies `contain: size layout style paint`).
//
// `contain` is a space-separated list, so a substring match on the whole value
// is what reads it — `contain: layout size` and `contain: strict` both apply.
bool has_size_containment(const ComputedStyle* style) {
    if (!style) return false;
    if (iequals(get(style, kId_container_type), "size")) return true;
    const std::string_view cv = get(style, kId_content_visibility);
    if (iequals(cv, "hidden")) return true;
    const std::string_view contain = get(style, kId_contain);
    if (contain.empty() || iequals(contain, "none")) return false;
    // `content` expands to layout/style/paint, deliberately excluding size.
    // Treating it as strict collapses auto-sized game panels around their UI.
    if (iequals(contain, "strict")) return true;
    // Word-boundary search, so `contain: inline-size` does not read as `size`.
    size_t at = contain.find("size");
    while (at != std::string_view::npos) {
        const bool start_ok = at == 0 || contain[at - 1] == ' ';
        const size_t end = at + 4;
        const bool end_ok = end == contain.size() || contain[end] == ' ';
        if (start_ok && end_ok) return true;
        at = contain.find("size", at + 1);
    }
    return false;
}

// CSS Containment L3 §2.1 and Containment L3 §3.1: containment of the INLINE
// axis only. The box has no contents to take a width from; its height still
// comes from them.
//
// Query containers also apply inline-size containment. Their descendant rules
// are settled by the layout owner after this pass produces container sizes.
bool has_inline_size_containment(const ComputedStyle* style) {
    if (!style) return false;
    const auto has_token = [](std::string_view list, std::string_view word) {
        size_t at = list.find(word);
        while (at != std::string_view::npos) {
            const bool start_ok = at == 0 || list[at - 1] == ' ';
            const size_t end = at + word.size();
            const bool end_ok = end == list.size() || list[end] == ' ';
            if (start_ok && end_ok) return true;
            at = list.find(word, at + 1);
        }
        return false;
    };
    return has_token(get(style, kId_contain), "inline-size") ||
        iequals(get(style, kId_container_type), "inline-size");
}

// The substitute content size a size-contained box uses, from
// `contain-intrinsic-size: <width> <height>` or its longhands. Negative means
// none was given, which makes the contained size zero.
//
// The WIDTH half. Size containment sizes a box as though it had no contents,
// and that is both axes -- CSS Containment L2 §3.1. It shows up only where the
// width comes from the contents in the first place: an inline-block, a float,
// an absolutely positioned box, a grid or flex item left to its own size. A
// block in normal flow takes its width from its containing block either way,
// which is why this was missing for so long without anything noticing.
double contain_intrinsic_width(const ComputedStyle* style, const LayoutContext& ctx,
                               double font_size) {
    if (!style) return -1;
    std::string_view raw = get(style, kId_contain_intrinsic_width);
    if (raw.empty() || iequals(raw, "none")) {
        raw = get(style, kId_contain_intrinsic_size);
        if (raw.empty() || iequals(raw, "none")) return -1;
        // Two values are width then height, so the width is the FIRST -- the
        // opposite end from the height's reading of the same declaration.
        const size_t space = raw.find(' ');
        if (space != std::string_view::npos) raw = raw.substr(0, space);
    }
    const ResolvedLength r = resolve_length(raw, ctx, font_size, std::nullopt);
    return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : -1;
}

double contain_intrinsic_height(const ComputedStyle* style, const LayoutContext& ctx,
                                double font_size) {
    if (!style) return -1;
    std::string_view raw = get(style, kId_contain_intrinsic_height);
    if (raw.empty() || iequals(raw, "none")) {
        raw = get(style, kId_contain_intrinsic_size);
        if (raw.empty() || iequals(raw, "none")) return -1;
        // Two values are width then height; one applies to both axes.
        size_t space = raw.find(' ');
        if (space != std::string_view::npos) raw = raw.substr(space + 1);
    }
    const ResolvedLength r = resolve_length(raw, ctx, font_size, std::nullopt);
    return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : -1;
}

bool has_non_visible_overflow(const Box& b) {
    if (!b.style) return false;
    const std::string_view ox = get(b.style, kId_overflow_x);
    if (!ox.empty() && ox != "visible") return true;
    const std::string_view oy = get(b.style, kId_overflow_y);
    return !oy.empty() && oy != "visible";
}

namespace {
bool is_flex_grid_item(const BoxTree& tree, const Box& b) {
    // Flex/grid items establish independent formatting contexts too. Their
    // children's margins stay inside them and their floats cannot escape.
    // The box parent handles display:contents and retained subtree probes
    // without storing another derived flag on the box.
    if (tree.valid(b.parent)) {
        const DisplayKind parent_display = tree[b.parent].display;
        if (parent_display == DisplayKind::Flex || parent_display == DisplayKind::InlineFlex ||
            parent_display == DisplayKind::Grid || parent_display == DisplayKind::InlineGrid)
            return true;
    }
    return false;
}
} // namespace

bool establishes_new_bfc(const Box& b) {
    if (!b.style) return false;
    // CSS Writing Modes §7.3: an orthogonal flow root is an independent
    // formatting context.
    if (b.orthogonal_root) return true;
    const auto container_type=get(b.style, kId_container_type);
    if (iequals(container_type,"size") || iequals(container_type,"inline-size")) return true;
    // The `overflow` shorthand expands to overflow-x / overflow-y, so the
    // `overflow` slot itself normally holds its initial value even when the
    // author wrote `overflow: hidden`. Both axis longhands have to be checked.
    const std::string_view ox = get(b.style, kId_overflow_x);
    if (!ox.empty() && ox != "visible") return true;
    const std::string_view oy = get(b.style, kId_overflow_y);
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
    const std::string_view f = get(b.style, kId_float);
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

// The content height a flex or grid container can distribute, or -1 when it has
// none.
//
// NOT `!parent_height_auto`: that test also reports "not auto" for a box whose
// only vertical constraint is `min-height`, and a min-height gives no USED
// height at this point — `box.height` has not been computed yet. Treating it as
// definite handed the container an available main size of zero, and a column
// flex container then shrank every item to nothing. `min-height: 100vh` on a
// page shell is a common enough shape that this was five harvested cases at
// once.
//
// A height imposed by an enclosing flex line counts, because that one IS
// already stamped.
double definite_flow_content_height(const BoxTree& tree, const Box& b, const LayoutContext& ctx,
                                    double font_size) {
    if (b.cross_size_imposed) return b.content_height();
    if (!b.style) return -1;
    const std::string_view raw = get(b.style, kId_height);
    if (raw.empty() || raw == "auto") {
        // css-sizing-4 §4.2: an auto height derived from aspect-ratio and a
        // known width IS definite. A square `display: flex; align-items:
        // center` portrait centres its glyph in Chrome and the reference;
        // treating the height as content-derived left the glyph at the top.
        double ratio = 0;
        if (try_resolve_aspect_ratio(b.style, &ratio) && ratio > 0 && b.width > 0) {
            // The ratio relates the boxes box-sizing names (css-sizing-4
            // §5.1): border box to border box, or content box to content box.
            if (is_border_box(b.style)) {
                const double frame =
                    b.padding_top + b.padding_bottom + b.border_top + b.border_bottom;
                return std::max(0.0, b.width / ratio - frame);
            }
            const double w_frame =
                b.padding_left + b.padding_right + b.border_left + b.border_right;
            return std::max(0.0, (b.width - w_frame) / ratio);
        }
        return -1;
    }
    const ResolvedLength r = resolve_length(b.style, kId_height, ctx, font_size, std::nullopt);
    if (r.kind == LengthKind::Percent) {
        // CSS 2.1 §10.5: a percentage against an indefinite parent computes to
        // auto. Reading the box's own (not yet computed) height here handed a
        // `height: 100%` flex container a definite height of zero, and the
        // line took it. Only when the parent is definite has apply_box_model
        // already stamped the resolved height onto the box.
        if (!definite_content_height(tree, b.parent)) return -1;
        return b.content_height();
    }
    if (r.kind != LengthKind::Length) return -1;
    return b.content_height();
}

bool parent_height_auto(const Box& b, const LayoutContext& ctx, double font_size) {
    if (!b.style) return true;
    const auto blocks = [&](std::string_view property, bool percent_must_be_positive) {
        const std::string_view raw = get(b.style, property);
        if (raw.empty() || raw == "auto") return false;
        const ResolvedLength r = resolve_length(b.style, property, ctx, font_size, std::nullopt);
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
    // A deferred grid owns nonempty in-flow children in the retained arena.
    // Its empty proxy vector is not evidence of an empty/self-collapsing box.
    if (b.retained_from != kNoBox) return false;
    if (b.padding_top > 0 || b.padding_bottom > 0) return false;
    if (b.border_top > 0 || b.border_bottom > 0) return false;
    if (b.style) {
        const auto has_size = [&](std::string_view property) {
            const std::string_view raw = get(b.style, property);
            if (raw.empty() || raw == "auto" || raw == "0") return false;
            const ResolvedLength r =
                resolve_length(b.style, property, ctx, font_size, std::nullopt);
            if (r.kind == LengthKind::Length && r.pixels > 0) return true;
            return r.kind == LengthKind::Percent && r.percent > 0;
        };
        if (has_size("height") || has_size("min-height")) return false;
    }
    // Empty descendants can collapse through their parent as well. A footer
    // containing only an empty paragraph must not trap its margin inside an
    // otherwise auto-height section.
    if (establishes_new_bfc(b)) return false;
    for (BoxId c : tree.children(id)) {
        if (tree[c].kind == BoxKind::Block && !participates_in_flow(tree[c])) continue;
        if (tree[c].kind == BoxKind::Block && is_self_collapsing(tree, c, ctx, font_size)) continue;
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

double FloatContext::available_band(double top, double bottom, double* left, double* right) const {
    double next = 0;
    for (const Entry& f : floats_) {
        if (f.bottom <= top || f.top >= std::max(top + 0.001, bottom)) continue;
        if (f.side == FloatType::Left) *left = std::max(*left, f.right);
        if (f.side == FloatType::Right) *right = std::min(*right, f.left);
        if (next == 0 || f.bottom < next) next = f.bottom;
    }
    return next;
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

double FloatContext::clear_bottom(ClearType c, bool* found) const {
    if (found) *found = false;
    if (c == ClearType::None) return 0;
    double max = 0;
    bool matched = false;
    for (const Entry& f : floats_) {
        const bool match = c == ClearType::Both ||
                           (c == ClearType::Left && f.side == FloatType::Left) ||
                           (c == ClearType::Right && f.side == FloatType::Right);
        if (match) {
            if (!matched || f.bottom > max) max = f.bottom;
            matched = true;
        }
    }
    if (found) *found = matched;
    return max;
}

double FloatContext::max_bottom() const {
    double max = 0;
    for (const Entry& f : floats_) {
        if (f.bottom > max) max = f.bottom;
    }
    return max;
}

double FloatContext::max_top() const {
    double top = 0;
    for (const Entry& f : floats_) top = std::max(top, f.top);
    return top;
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
        bool found;
        const double clear_local = current_floats_->clear_bottom(f.clear, &found) - bfc_origin_y_;
        if (found && clear_local > top_y) top_y = clear_local;
    }

    const double bfc_content_left = bfc_origin_x_ + c.padding_left + c.border_left;
    const double margin_box_w = f.margin_left + f.width + f.margin_right;
    const double margin_box_h = f.margin_top + f.height + f.margin_bottom;
    double bfc_top = bfc_origin_y_ + top_y;

    if (current_floats_) bfc_top = std::max(bfc_top, current_floats_->max_top());
    double left = bfc_content_left, right = bfc_content_left + content_w;
    for (;;) {
        left = bfc_content_left; right = bfc_content_left + content_w;
        const double next = current_floats_ ? current_floats_->available_band(
            bfc_top, bfc_top + margin_box_h, &left, &right) : 0;
        if (right - left >= margin_box_w || next <= bfc_top) break;
        bfc_top = next;
    }
    // Band edges are already BFC-local. Adding the content inset again made
    // every subsequent float drift by another padding/border width.
    const double bfc_x = (f.float_type == FloatType::Left ? left : right - margin_box_w) + f.margin_left;

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



double BlockLayout::layout_inline_content(BoxId id, double content_width,
                                          const ComputedStyle* parent_style) {
    LayoutProfilePart work_profile(1);
    if (!metrics_) return 0;
    // The items are collected ONCE per container and cached: a shrink-to-fit
    // probe lays the same container out three times, and the first pass
    // replaces its children with line boxes, so a second collect would walk
    // line boxes and find nothing. Atom sizes DO depend on the width, so they
    // are re-derived on every pass.
    auto it = inline_items_.find(id);
    if (it == inline_items_.end()) {
        LayoutProfilePart collect_profile(3);
        InlineLayoutEntry entry;
        entry.items = collect_inline_items(*tree_, id, ctx_, metrics_);
        entry.plain_text = std::all_of(entry.items.begin(), entry.items.end(), [](const InlineItem& item) {
            return item.source_run != kNoBox && item.inline_parent == kNoBox &&
                   !item.is_atom() && !item.is_break() && !item.is_marker();
        });
        it = inline_items_.emplace(id, std::move(entry)).first;
    }
    auto& entry = it->second;
    {
        LayoutProfilePart atoms_profile(4);
        size_atoms(&entry.items, content_width, parent_style);
    }

    // The container's own offset inside the BFC. bfc_origin_ still refers to
    // the PARENT here — it is updated further down layout_block, past the
    // inline early return — so the container's own y is added. That y is the
    // placement loop's running estimate, stamped before recursing for exactly
    // this reason.
    InlineFloatEnv env;
    if (current_floats_ && current_floats_->count() > 0) {
        env.floats = current_floats_;
        env.bfc_content_top = bfc_origin_y_ + (*tree_)[id].y + (*tree_)[id].padding_top +
                              (*tree_)[id].border_top;
    }
    const Box& before = (*tree_)[id];
    InlineLayoutEntry::Input input;
    input.width = content_width;
    input.top = before.padding_top + before.border_top;
    input.left = before.padding_left + before.border_left;
    input.style = before.style;
    input.parent_style = before.parent != kNoBox ? (*tree_)[before.parent].style : nullptr;
    input.style_version = input.style ? input.style->version() : 0;
    input.parent_version = input.parent_style ? input.parent_style->version() : 0;
    static const bool enabled = std::getenv("WEVA_DISABLE_INLINE_REUSE") == nullptr;
    static const bool profile = std::getenv("WEVA_INLINE_LOG") != nullptr;
    // The collected text/style/font inputs belong to this BlockLayout pass.
    // Plain text cannot reparent atoms or inline fragments during a probe;
    // its detached line boxes remain intact in the arena. Floats add an
    // external position-dependent constraint, so use the ordinary walk there.
    const bool reusable = enabled && entry.plain_text && !env.floats;
    if (reusable) for (const auto& result : entry.results) {
        if (!result.ready || !(result.input == input)) continue;
        bool intact = true;
        for (size_t i = 0; i < result.count; ++i) {
            const BoxId line = result.lines[i].id;
            if (!tree_->valid(line) || (*tree_)[line].kind != BoxKind::Line ||
                ((*tree_)[line].parent != kNoBox && (*tree_)[line].parent != id)) {
                intact = false;
                break;
            }
        }
        if (!intact) continue;
        tree_->clear_children(id);
        for (size_t i = 0; i < result.count; ++i) {
            const auto& saved = result.lines[i];
            // Button centering and table-cell vertical alignment adjust the
            // lines after inline layout. Restore the unadjusted output so
            // finalization can apply the current container's alignment once.
            (*tree_)[saved.id].x = saved.x;
            (*tree_)[saved.id].y = saved.y;
            tree_->append_child(id, saved.id);
        }
        if (profile) std::fprintf(stderr, "inline reuse: %d width %.9g hit 1\n", id, content_width);
        return result.height;
    }
    if (profile) std::fprintf(stderr, "inline reuse: %d width %.9g hit 0\n", id, content_width);
    double height;
    {
        LayoutProfilePart lines_profile(5);
        height = layout_inline_items(tree_, id, entry.items, content_width, ctx_, *metrics_,
                                     env.floats ? &env : nullptr);
    }
    if (reusable) {
        InlineLayoutEntry::Result result;
        result.input = input;
        result.height = height;
        result.ready = true;
        for (BoxId line : tree_->children(id)) {
            if (result.count == result.lines.size()) { result.ready = false; break; }
            result.lines[result.count++] = {line, (*tree_)[line].x, (*tree_)[line].y};
        }
        if (result.ready) {
            entry.results[entry.next_result] = result;
            entry.next_result = (entry.next_result + 1) % entry.results.size();
        }
    }
    return height;
}

void BlockLayout::size_atoms(std::vector<InlineItem>* items, double available_width,
                             const ComputedStyle* parent_style) {
    for (InlineItem& it : *items) {
        if (!it.is_atom()) continue;
        shrink_to_fit(it.atom_box, available_width, parent_style);
        const Box& a = (*tree_)[it.atom_box];
        it.atom_outer_width = a.margin_left + a.width + a.margin_right;
        // First derive the baseline from the top border edge. Form controls
        // expose their native content baseline; generic inline-blocks follow
        // CSS 2.1 §10.8.1 below. Convert to the top margin edge afterward.
        //
        //  1. overflow other than visible: the bottom margin edge, because a
        //     clipping box does not expose its contents' baseline;
        //  2. otherwise its LAST line box's baseline;
        //  3. otherwise (only block children, or empty) the bottom of the
        //     content area, which is what Blink does.
        //
        // Case 2 was previously unimplemented and case 1 was applied to
        // everything, which put the baseline at the atom's bottom edge and made
        // every line holding an inline-block one text-descent too tall. The
        // oracle caught it; no self-written test had.
        const FontMetrics* const fm = it.metrics ? it.metrics : metrics_;
        const auto input_type = a.element && a.element->tag_name() == "input"
                                    ? form_input_type(*a.element) : std::string_view();
        if (a.element && form_is_text_entry(*a.element) && fm) {
            // No child LineBox carries the overlaid value. Expose its centered
            // baseline even for an empty value, a short field or clipped overflow.
            const double top = a.border_top + a.padding_top;
            const double content_height = a.height - top - a.padding_bottom - a.border_bottom;
            it.atom_baseline = top + fm->leading_above(content_height, it.font_size) +
                               fm->ascent(it.font_size);
        } else if (input_type == "checkbox" || input_type == "radio" || input_type == "range") {
            it.atom_baseline = a.height;
        } else if (input_type == "image" || has_non_visible_overflow(a)) {
            it.atom_baseline = a.height + a.margin_bottom;
        } else {
            BoxId last_line = kNoBox;
            for (BoxId c : tree_->children(it.atom_box)) {
                if ((*tree_)[c].kind == BoxKind::Line) last_line = c;
            }
            if (last_line != kNoBox) {
                it.atom_baseline = (*tree_)[last_line].y + (*tree_)[last_line].baseline;
            } else if (a.display == DisplayKind::Flex || a.display == DisplayKind::InlineFlex) {
                // Inline flex exposes the first item's first baseline, including
                // an anonymous flex item created around raw text.
                const auto first_line = [&](auto&& self, BoxId id, double y) -> std::optional<double> {
                    const Box& child = (*tree_)[id];
                    y += child.y;
                    if (child.kind == BoxKind::Line) return y + child.baseline;
                    for (BoxId nested : tree_->children(id))
                        if (auto baseline = self(self, nested, y)) return baseline;
                    return std::nullopt;
                };
                it.atom_baseline = a.height;
                for (BoxId child : tree_->children(it.atom_box)) {
                    if (auto baseline = first_line(first_line, child, 0)) {
                        it.atom_baseline = *baseline;
                        break;
                    }
                }
            } else {
                const double content_bottom = a.height - a.padding_bottom - a.border_bottom;
                it.atom_baseline = content_bottom < 0 ? a.height : content_bottom;
            }
        }
        // Inline layout measures extents from the top margin edge, then adds
        // margin_top to position the border box. Include it once in the metric.
        it.atom_baseline += a.margin_top;
    }
}

// The image an <img> shows, or null.
//
// Resolved here rather than in the box builder because the builder has no
// LayoutContext and so no way to reach the store -- and this is the only place
// the answer is needed, since a replaced element's `auto` size IS its
// intrinsic size and nothing else in layout can supply one.
// Sizes a replaced element from its intrinsic size, and says whether it did.
//
// CSS 2.2 s10.3.2 and s10.6.2: an `auto` width is the intrinsic width, an
// `auto` height the intrinsic height, and when exactly one is stated the other
// follows through the intrinsic ratio.
//
// Shared, because an <img> reaches layout through more than one door: a block
// or inline-block shrinking to fit, and a FLEX ITEM, which flex measures
// through layout_block and never through shrink_to_fit. Sizing it in only one
// of them left every image inside a flex row at zero height while the same
// image in a plain div was right -- the shape of bug that survives a unit
// suite and dies the moment someone renders a page and looks at it.
bool size_replaced_box(Box& b, const DecodedImage& image, const LayoutContext& ctx,
                       double font_size) {
    const double iw = std::max(1, image.width);
    const double ih = std::max(1, image.height);
    const double width_frame = b.padding_left + b.padding_right + b.border_left + b.border_right;
    const double height_frame = b.padding_top + b.padding_bottom + b.border_top + b.border_bottom;
    const ResolvedLength w = resolve_length(b.style, kId_width, ctx, font_size, std::nullopt);
    const ResolvedLength h = resolve_length(b.style, kId_height, ctx, font_size, std::nullopt);
    const bool have_w = w.kind == LengthKind::Length || w.kind == LengthKind::Percent;
    const bool have_h = h.kind == LengthKind::Length;
    // Keep the content contribution independent of later flexed sizes. An
    // image has no child boxes for min-/max-content measurement to walk.
    b.intrinsic_width = have_h ? std::max(0.0, b.height - height_frame) * iw / ih : iw;
    b.intrinsic_height = ih;
    if (have_w && have_h) return false;   // both stated; nothing to infer
    if (have_w) {
        b.height = b.content_width() * (ih / iw) + height_frame;
    } else if (have_h) {
        b.width = b.intrinsic_width + width_frame;
    } else {
        b.width = iw + width_frame;
        b.height = ih + height_frame;
    }
    return true;
}

const DecodedImage* replaced_image(const Box& box, const LayoutContext& ctx) {
    if (!ctx.images) return nullptr;
    // A list-style-image marker is an image with no element of its own.
    if (!box.list_marker_image.empty()) return ctx.images->get(box.list_marker_image);
    if (!box.element) return nullptr;
    if (box.element->tag_name() != "img") return nullptr;
    const std::string_view src = box.element->get_attribute("src");
    if (src.empty()) return nullptr;
    return ctx.images->get(src);
}

namespace {
// How shrink_to_fit picks between the two probes (CSS Sizing L3 §5.2).
enum class IntrinsicPick {
    Fit,      // min(max-content, max(min-content, available)): auto on a float, `fit-content`
    Min,      // `min-content`
    Max,      // `max-content`
    FitArg,   // fit-content(<length>): the available width replaced by the argument
};

IntrinsicPick intrinsic_pick_of(std::string_view raw) {
    if (iequals(raw, "min-content")) return IntrinsicPick::Min;
    if (iequals(raw, "max-content")) return IntrinsicPick::Max;
    return IntrinsicPick::Fit;
}
} // namespace

namespace {
bool is_intrinsic_keyword(std::string_view raw) {
    if (iequals(raw, "min-content") || iequals(raw, "max-content") || iequals(raw, "fit-content")) return true;
    return raw.size() > 12 && iequals(raw.substr(0, 12), "fit-content(");
}
} // namespace

bool BlockLayout::has_intrinsic_width_keyword(BoxId id) const {
    const ComputedStyle* style = (*tree_)[id].style;
    // CSS Sizing L3 §5.2: the keywords as min-width / max-width need the same
    // probes as a keyword width, whatever the width itself says.
    return is_intrinsic_keyword(get(style, kId_width)) || is_intrinsic_keyword(get(style, kId_min_width)) ||
           is_intrinsic_keyword(get(style, kId_max_width));
}

double BlockLayout::shrink_to_fit(BoxId id, double available_width,
                                  const ComputedStyle* parent_style) {
    // A vertical float or inline-block hugs its content on its own axes:
    // the orthogonal layout fits it against the block size available to it
    // and its physical width is its content's block size.
    if (vertical_mode_of((*tree_)[id].style) != VerticalMode::None) {
        layout_orthogonal(id, parent_style, std::nullopt, std::nullopt);
        return font_size_px((*tree_)[id].style, parent_style, ctx_);
    }
    const double fs = apply_box_model(tree_, id, available_width, parent_style, ctx_);
    const ComputedStyle* style = (*tree_)[id].style;
    const ResolvedLength w =
        resolve_length(style, kId_width, ctx_, fs, available_width);
    IntrinsicPick pick = intrinsic_pick_of(get(style, kId_width));
    double fit_arg = 0;
    const bool bound_keyword =
        is_intrinsic_keyword(get(style, kId_min_width)) || is_intrinsic_keyword(get(style, kId_max_width));
    // A stated width with a keyword bound: the probes still run, and the
    // stated width (as apply_box_model resolved it) is what they clamp.
    const bool explicit_width = w.kind != LengthKind::Auto && w.kind != LengthKind::FitContent && bound_keyword;
    const double stated_width = (*tree_)[id].width;   // before the probes overwrite it
    if (w.kind == LengthKind::FitContent) {
        pick = IntrinsicPick::FitArg;
        fit_arg = w.pixels;
    } else if (w.kind != LengthKind::Auto && !bound_keyword) {
        // A replaced element with a stated width and an auto height takes its
        // height from the intrinsic ratio -- the case every `img { width: 100% }`
        // in every stylesheet relies on.
        if (const DecodedImage* image = replaced_image((*tree_)[id], ctx_)) {
            size_replaced_box((*tree_)[id], *image, ctx_, fs);
            return fs;
        }
        // An explicit width needs no probing: apply_box_model already resolved
        // it, so just lay the contents out inside it.
        layout_content(id, fs, available_width, parent_style);
        return fs;
    }

    // CSS Containment L2 §3.1: a size-contained box is sized as though it had
    // no contents, so there is nothing to shrink TO. Its width is whatever
    // contain-intrinsic-size states, or zero when it states nothing.
    //
    // Checked before the content probes below, which is the whole point: those
    // probes measure the contents this box is defined not to have. Without
    // this an `<span style="display:inline-block; contain:size">` came out the
    // width of its text -- 307px where the reference and a browser both say
    // 100 -- and everything after it on the line moved with it.
    if (has_size_containment(style) || has_inline_size_containment(style)) {
        const double intrinsic = contain_intrinsic_width(style, ctx_, fs);
        const double used = intrinsic >= 0 ? intrinsic : 0.0;
        Box& b = (*tree_)[id];
        b.width = used + b.padding_left + b.padding_right + b.border_left + b.border_right;
        layout_content(id, fs, available_width, parent_style);
        return fs;
    }

    // A replaced element does not shrink to fit its contents; it has none.
    // CSS 2.2 §10.3.2: an `auto` width on a replaced element with an
    // intrinsic width IS that width, and an `auto` height follows from the
    // used width through the intrinsic ratio. Probing instead measured an
    // empty box and laid every <img> out at zero, which is why none of them
    // have ever been visible.
    if (const DecodedImage* image = replaced_image((*tree_)[id], ctx_)) {
        size_replaced_box((*tree_)[id], *image, ctx_, fs);
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
    double max_content = max_content_width(*tree_, id, &ctx_) + frame;
    relayout_content_at(id, 1, fs, parent_style);
    double min_content = min_content_width(*tree_, id, &ctx_) + frame;
    if (max_content < frame) max_content = frame;
    if (min_content < frame) min_content = frame;

    const bool border_box = is_border_box(style);
    double fitted;
    switch (pick) {
        case IntrinsicPick::Min: fitted = min_content; break;
        case IntrinsicPick::Max: fitted = max_content; break;
        case IntrinsicPick::FitArg:
            // The argument is a size in width's own box-sizing basis.
            fitted = std::min(max_content, std::max(min_content, border_box ? fit_arg : fit_arg + frame));
            break;
        case IntrinsicPick::Fit:
        default:
            fitted = std::min(max_content, std::max(min_content, avail));
            break;
    }
    if (explicit_width) fitted = stated_width;
    if (fitted < 0) fitted = 0;

    // §10.3.5: the shrink-to-fit result is still clamped by min- and max-width,
    // which share width's box-sizing basis. A keyword bound is the probe's
    // own number; `fit-content` measures against the available width.
    const auto keyword_bound = [&](int prop) -> double {
        const std::string_view raw = get(style, prop);
        if (iequals(raw, "min-content")) return min_content;
        if (iequals(raw, "max-content")) return max_content;
        if (iequals(raw, "fit-content")) return std::min(max_content, std::max(min_content, avail));
        if (raw.size() > 12 && iequals(raw.substr(0, 12), "fit-content(")) {
            const ResolvedLength r = resolve_length(style, prop, ctx_, fs, available_width);
            if (r.kind == LengthKind::FitContent) {
                return std::min(max_content, std::max(min_content, border_box ? r.pixels : r.pixels + frame));
            }
        }
        return -1;
    };
    const ResolvedLength min_r =
        resolve_length(style, kId_min_width, ctx_, fs, available_width);
    const ResolvedLength max_r =
        resolve_length(style, kId_max_width, ctx_, fs, available_width);
    const auto to_border_box = [&](const ResolvedLength& r) {
        double px = r.kind == LengthKind::Percent ? available_width * r.percent * 0.01
                                                  : r.pixels;
        if (!border_box) px += frame;
        return px;
    };
    if (max_r.kind == LengthKind::Length || max_r.kind == LengthKind::Percent) {
        const double px = to_border_box(max_r);
        if (fitted > px) fitted = px;
    } else {
        const double max_i = keyword_bound(kId_max_width);
        if (max_i >= 0 && fitted > max_i) fitted = max_i;
    }
    if (min_r.kind == LengthKind::Length || min_r.kind == LengthKind::Percent) {
        const double px = to_border_box(min_r);
        if (fitted < px) fitted = px;
    } else {
        const double min_i = keyword_bound(kId_min_width);
        if (min_i >= 0 && fitted < min_i) fitted = min_i;
    }

    // CSS 2.1 §10.3.3: `margin: 0 auto` centres the FITTED width. The box
    // model computed the margins for the width it knew; a keyword changes it.
    {
        Box& fb = (*tree_)[id];
        const bool out_of_flow =
            fb.position == PositionType::Absolute || fb.position == PositionType::Fixed;
        if (get(style, kId_margin_left) == "auto" && get(style, kId_margin_right) == "auto" &&
            !fb.is_inline_block && !fb.is_float() && !out_of_flow) {
            const double extra = std::max(0.0, available_width - fitted);
            fb.margin_left = extra * 0.5;
            fb.margin_right = extra * 0.5;
        }
    }
    relayout_content_at(id, fitted, fs, parent_style);
    return fs;
}

void BlockLayout::relayout_at(BoxId id, double width) {
    // Only the width is imposed here; the height is recomputed from it.
    (*tree_)[id].cross_size_imposed = false;
    const BoxId parent = (*tree_)[id].parent;
    const ComputedStyle* parent_style = parent == kNoBox ? nullptr : (*tree_)[parent].style;
    if (vertical_mode_of((*tree_)[id].style) != VerticalMode::None) {
        layout_orthogonal(id, parent_style, width, std::nullopt);
        return;
    }
    const double fs = font_size_px((*tree_)[id].style, parent_style, ctx_);
    relayout_content_at(id, width, fs, parent_style);
}

void BlockLayout::relayout_at_size(BoxId id, double width, double height) {
    const BoxId parent = (*tree_)[id].parent;
    const ComputedStyle* parent_style = parent == kNoBox ? nullptr : (*tree_)[parent].style;
    if (vertical_mode_of((*tree_)[id].style) != VerticalMode::None) {
        layout_orthogonal(id, parent_style, width, height);
        return;
    }
    const double fs = font_size_px((*tree_)[id].style, parent_style, ctx_);
    (*tree_)[id].height = height;
    (*tree_)[id].cross_size_imposed = true;
    relayout_content_at(id, width, fs, parent_style);
    (*tree_)[id].height = height;
}

void BlockLayout::relayout_content_at(BoxId id, double width, double font_size,
                                      const ComputedStyle* parent_style) {
    (*tree_)[id].width = width;
    if (reuse_ && reuse_->reuse_layout(tree_, id)) return;
    Box& b = (*tree_)[id];
    if (b.intrinsic_height > 0) {
        // Flex/grid can impose a new width after initial image measurement.
        // There is no text content to reflow, but an auto height follows the
        // intrinsic ratio unless the parent also imposed the height.
        const ResolvedLength h = resolve_length(b.style, kId_height, ctx_, font_size, std::nullopt);
        if (!b.cross_size_imposed && h.kind == LengthKind::Auto && b.intrinsic_width > 0)
            b.height = b.content_width() * b.intrinsic_height / b.intrinsic_width +
                       b.padding_top + b.padding_bottom + b.border_top + b.border_bottom;
        return;
    }
    // The first inline pass replaced the container's children with line boxes,
    // so the source runs can no longer be walked. The collected items are
    // cached per container for the duration of the pass and reused, which is
    // both cheaper and simpler than snapshotting and restoring the child list.
    // Enter through layout_content so reflow preserves the same independent
    // float context and owning style as the initial inline pass.
    layout_content(id, font_size, width, parent_style);
}

void BlockLayout::layout_orthogonal(BoxId id, const ComputedStyle* parent_style,
                                    std::optional<double> imposed_width,
                                    std::optional<double> imposed_height) {
    const VerticalMode mode = vertical_mode_of((*tree_)[id].style);
    // CSS Writing Modes §7.3.2: the space available to the orthogonal flow's
    // inline axis is the containing block's block size when that is
    // definite, else the initial containing block's. `html, body { height:
    // 100% }` makes body definite; an auto-height div is not, so a vertical
    // section in one fits against the viewport height, as it does in Chrome.
    double available = ctx_.viewport_height_px;
    const BoxId parent = (*tree_)[id].parent;
    static const int kPosition = CssPropertyRegistry::instance().id_of("position");
    const PositionType position = parse_position_type(get((*tree_)[id].style, kPosition));
    if (position == PositionType::Absolute || position == PositionType::Fixed) {
        // An out-of-flow box's containing block is its nearest positioned
        // ancestor's padding box, not its parent; a wrapper in between must
        // not send it to the viewport.
        const ContainingBlock cb = position == PositionType::Absolute
            ? resolve_absolute_containing_block(*tree_, id, ctx_)
            : resolve_fixed_containing_block(*tree_, id, ctx_);
        if (cb.box != kNoBox && !cb.is_viewport && cb.height > 0) available = cb.height;
    } else if (parent != kNoBox && (*tree_)[parent].style) {
        const Box& p = (*tree_)[parent];
        const BoxId grandparent = p.parent;
        const ComputedStyle* gp_style = grandparent == kNoBox ? nullptr : (*tree_)[grandparent].style;
        const double pfs = font_size_px(p.style, gp_style, ctx_);
        const double definite = definite_flow_content_height(*tree_, p, ctx_, pfs);
        if (definite >= 0) available = definite;
    }

    if (!orthogonal_) orthogonal_ = std::make_unique<OrthogonalFlowStyles>();
    orthogonal_->rotate(tree_, id, mode);
    (*tree_)[id].orthogonal_root = true;
    if (imposed_width && imposed_height) {
        // Both axes from outside, as a flex line stretches an item: the
        // physical width is the rotated block size, the physical height the
        // rotated inline size.
        const double fs = apply_box_model(tree_, id, available, parent_style, ctx_);
        (*tree_)[id].height = *imposed_width;
        (*tree_)[id].cross_size_imposed = true;
        relayout_content_at(id, *imposed_height, fs, parent_style);
        (*tree_)[id].height = *imposed_width;
    } else {
        (*tree_)[id].cross_size_imposed = false;
        shrink_to_fit(id, available, parent_style);
        if (imposed_width) (*tree_)[id].height = *imposed_width;
    }
    transpose_orthogonal_flow(tree_, id, mode);
    orthogonal_->restore(tree_, id);
}

void BlockLayout::layout_root(BoxId root, double viewport_width, double viewport_height) {
    LayoutProfileRoot profile;
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
    // A fresh layout imposes nothing: the flag a flex line or grid area set
    // in an EARLIER pass must not survive into this one, or finalize keeps a
    // stale height. A square item in a grid inside a column flex was 199.85
    // tall — its provisional first-pass height — instead of 80.
    (*tree_)[id].cross_size_imposed = false;
    // A vertical box in this horizontal flow is laid out on its side and
    // turned back (writing_mode.h). Inside that layout every style is a
    // horizontal copy, so this never recurses. A box laid out in an earlier
    // pass as part of a vertical flow starts clean: the transposition marks
    // the subtree again if it still is one.
    (*tree_)[id].orthogonal_root = false;
    (*tree_)[id].vertical_text = 0;
    if (vertical_mode_of((*tree_)[id].style) != VerticalMode::None) {
        layout_orthogonal(id, parent_style, std::nullopt, std::nullopt);
        return;
    }
    double fs;
    if ((*tree_)[id].style) {
        fs = apply_box_model(tree_, id, available_width, parent_style, ctx_);
    } else {
        (*tree_)[id].width = available_width;
        fs = ctx_.root_font_size_px;
    }

    if (reuse_ && reuse_->reuse_layout(tree_, id)) return;

    // A replaced element has no contents to lay out; its size comes from the
    // image. This is the path a FLEX ITEM takes -- flex measures its items
    // through layout_block, never through shrink_to_fit.
    if (const DecodedImage* image = replaced_image((*tree_)[id], ctx_)) {
        size_replaced_box((*tree_)[id], *image, ctx_, fs);
        return;
    }

    const Box& b = (*tree_)[id];
    if (b.element && b.element->tag_name() == "select" &&
        !select_is_listbox(*b.element) && get(b.style, kId_width) == "auto") {
        // Closed controls keep their natural width when displayed as blocks.
        // Flex/grid may subsequently impose their resolved item width.
        shrink_to_fit(id, available_width, parent_style);
        return;
    }
    // CSS Sizing L3 §5: `width: max-content` and friends on a block-level
    // box are the float's probes with a different pick at the end.
    if (b.style && has_intrinsic_width_keyword(id)) {
        shrink_to_fit(id, available_width, parent_style);
        return;
    }
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
        // This branch precedes block-child float setup: independent inline
        // content must not wrap around floats in an ancestor's context.
        FloatContext* const previous_floats = current_floats_;
        if (current_floats_ && current_floats_->count() > 0 &&
            (is_flex_grid_item(*tree_, (*tree_)[id]) || establishes_new_bfc((*tree_)[id]) ||
             (*tree_)[id].is_float() || (*tree_)[id].is_inline_block))
            current_floats_ = nullptr;
        const double inline_h = layout_inline_content(id, content_w, own_style);
        current_floats_ = previous_floats;
        finalize_block_size(id, font_size, top_inner + inline_h);
        return;
    }

    // A flex container lays its children out itself. It establishes a block
    // formatting context, but its items never consult a float context — each
    // item is a BFC root in its own right — so returning before the float
    // bookkeeping below is safe rather than an omission.
    if ((*tree_)[id].is_multicol) {
        const double h = layout_multicol(tree_, id, content_w, font_size, ctx_, this);
        finalize_block_size(id, font_size, top_inner + h);
        return;
    }
    if ((*tree_)[id].display == DisplayKind::Grid ||
        (*tree_)[id].display == DisplayKind::InlineGrid) {
        const double definite_h =
            definite_flow_content_height(*tree_, (*tree_)[id], ctx_, font_size);
        const double h = layout_grid(tree_, id, content_w, definite_h, ctx_, this);
        finalize_block_size(id, font_size, top_inner + h);
        return;
    }
    // A table lays out its rows, groups and cells itself (§17.5); before this
    // its subtree was stacked as plain blocks, every cell a full-width block
    // under the next, and the table stood 1,941px tall for 332px of rows.
    if ((*tree_)[id].display == DisplayKind::Table ||
        (*tree_)[id].display == DisplayKind::InlineTable) {
        const double h = layout_table(tree_, id, content_w, ctx_, this);
        // Collapsed tables resolve their outer half-borders during layout.
        finalize_block_size(id, font_size, (*tree_)[id].padding_top + (*tree_)[id].border_top + h);
        return;
    }
    if ((*tree_)[id].display == DisplayKind::Flex ||
        (*tree_)[id].display == DisplayKind::InlineFlex) {
        // A definite content height lets the cross axis centre against the
        // container; a negative one means "content-derived".
        const double definite_h =
            definite_flow_content_height(*tree_, (*tree_)[id], ctx_, font_size);
        const double h = layout_flex(tree_, id, content_w, definite_h, ctx_, this);
        finalize_block_size(id, font_size, top_inner + h);
        return;
    }
    // Nothing below this point may return early without restoring the float
    // context — the single exit at the end of the function is what guarantees
    // it, so keep it that way.

    // A new block formatting context gets its own float context; otherwise this
    // box's children join the ancestor BFC's, so a float placed here is still
    // avoided by content further down that BFC.
    const bool item_context = is_flex_grid_item(*tree_, (*tree_)[id]);
    const bool establishes_bfc = !own_style || item_context || establishes_new_bfc((*tree_)[id]) ||
                                 (*tree_)[id].is_float() || (*tree_)[id].is_inline_block;
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

    // Keep sibling IDs stable while child layout creates anonymous/line boxes.
    // Lay out in source order: later floats must see the actual preceding flow.
    std::vector<BoxId> inflow;
    for (BoxId c : tree_->children(id)) {
        if ((*tree_)[c].kind == BoxKind::Block || (*tree_)[c].kind == BoxKind::AnonymousBlock)
            inflow.push_back(c);
    }

    // The synthetic root has no style and no margins of its own: it is the
    // viewport edge, and nothing collapses through it.
    const bool parent_participates = own_style && participates_in_flow((*tree_)[id]);
    const bool top_open = parent_participates && !item_context && parent_top_open((*tree_)[id]);
    const bool bottom_open = parent_participates && !item_context && parent_bottom_open((*tree_)[id]) &&
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
        (*tree_)[cid].float_type = parse_float_type(get((*tree_)[cid].style, kId_float));
        (*tree_)[cid].clear = parse_clear_type(get((*tree_)[cid].style, kId_clear));
        if ((*tree_)[cid].is_float()) {
            layout_float_box(cid, content_w);
            place_float(id, cid, cursor + (chain_attaches_to_parent_top ? 0 : chain_max_pos + chain_min_neg), content_w);
            any_collapsible_seen = true;
            continue;
        }
        // Resolve the provisional border top before descendants inspect floats.
        double provisional_top = cursor;
        if (current_floats_ && current_floats_->count()) {
            apply_box_model(tree_, cid, content_w, own_style, ctx_);
            if (!chain_attaches_to_parent_top)
                provisional_top += std::max(chain_max_pos, (*tree_)[cid].margin_top) +
                                   std::min(chain_min_neg, (*tree_)[cid].margin_top);
            if ((*tree_)[cid].clear != ClearType::None) {
                bool found;
                const double clear_top = current_floats_->clear_bottom((*tree_)[cid].clear, &found) - bfc_origin_y_;
                if (found) provisional_top = std::max(provisional_top, clear_top);
            }
        }
        (*tree_)[cid].y = provisional_top;
        layout_block(cid, content_w, own_style);
        Box& c = (*tree_)[cid];
        const double left_inner = (*tree_)[id].padding_left + (*tree_)[id].border_left;

        // A float was placed above. It does not advance the cursor, and it does
        // not join the collapse chain either — the chain continues through to
        // the next in-flow box as if the float were not there (§8.3.1 rule 5).
        if (c.is_float()) {
            any_collapsible_seen = true;
            continue;
        }
        // Clearance positions the border edge below matching floats. Test the
        // hypothetical collapsed position first: an already sufficient margin
        // needs no clearance, and a margin adjoining the parent lives outside it.
        if (c.clear != ClearType::None && current_floats_ && !is_out_of_flow(c)) {
            bool found;
            const double clear_local = current_floats_->clear_bottom(c.clear, &found) - bfc_origin_y_;
            const double would_be_top = cursor + (chain_attaches_to_parent_top ? 0 :
                std::max(chain_max_pos, c.margin_top) + std::min(chain_min_neg, c.margin_top));
            if (found && clear_local > would_be_top) {
                if (chain_attaches_to_parent_top) {
                    (*tree_)[id].margin_top = chain_max_pos + chain_min_neg;
                    chain_attaches_to_parent_top = false;
                }
                // The child's margin is folded in below; absorb it here rather
                // than adding it a second time on top of the float's bottom.
                cursor = clear_local - c.margin_top;
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
            c.x = left_inner + resolve_block_inline_offset(c, content_w, get(own_style, kId_direction) == "rtl");
            c.y = chain_attaches_to_parent_top ? cursor : cursor + chain_max_pos + chain_min_neg;
            if (child_bottom > chain_max_pos) chain_max_pos = child_bottom;
            if (child_bottom < chain_min_neg) chain_min_neg = child_bottom;
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
        c.x = left_inner + resolve_block_inline_offset(c, content_w, get(own_style, kId_direction) == "rtl");
        if (current_floats_ && current_floats_->count() && establishes_new_bfc(c)) {
            const double fs = font_size_px(c.style, own_style, ctx_);
            const auto margins = resolve_box_sides_px(c.style, kId_margin, ctx_, fs, content_w);
            const bool auto_left = margins.left_raw == "auto", auto_right = margins.right_raw == "auto";
            const double ml = auto_left ? 0 : c.margin_left, mr = auto_right ? 0 : c.margin_right;
            const double frame = c.padding_left + c.padding_right + c.border_left + c.border_right;
            const auto min_width = resolve_length(c.style, kId_min_width, ctx_, fs, content_w);
            const double minimum = min_width.kind == LengthKind::Length || min_width.kind == LengthKind::Percent
                ? std::max(frame, (min_width.kind == LengthKind::Percent ? content_w * min_width.percent * 0.01 : min_width.pixels) + (get(c.style, "box-sizing") == "border-box" ? 0 : frame)) : frame;
            double ratio = 0;
            const auto height = resolve_length(c.style, kId_height, ctx_, fs, std::nullopt);
            const bool has_ratio = try_resolve_aspect_ratio(c.style, &ratio) &&
                                   height.kind == LengthKind::Length && height.pixels > 0;
            const bool fill = resolve_length(c.style, kId_width, ctx_, fs, content_w).kind == LengthKind::Auto &&
                              !has_ratio && c.intrinsic_width <= 0;
            const double natural_width = c.width;
            for (;;) {
                // Reflow can rebuild descendants and invalidate Box references.
                Box& candidate = (*tree_)[cid];
                const double origin = bfc_origin_x_ + left_inner;
                double left = origin, right = origin + content_w;
                const double next = current_floats_->available_band(bfc_origin_y_ + candidate.y,
                    bfc_origin_y_ + candidate.y + candidate.height, &left, &right);
                left = std::max(left, origin + ml);
                right = std::min(right, origin + content_w - mr);
                const double space = std::max(0.0, right - left);
                const double width = fill ? std::max(minimum, std::min(natural_width, space)) : natural_width;
                if (width > space + 0.001 && next > bfc_origin_y_ + candidate.y) {
                    candidate.y = next - bfc_origin_y_;
                    continue;
                }
                candidate.x = left - bfc_origin_x_;
                const double extra = std::max(0.0, space - width);
                if (auto_left) candidate.x += auto_right ? extra * 0.5 : extra;
                if (std::abs(candidate.width - width) > 0.001) {
                    const double previous_height = candidate.height;
                    relayout_at(cid, width);
                    // Wrapping may extend into a lower preceding float. Check the
                    // enlarged border box before accepting this band. A smaller
                    // height cannot add an intersection and needs no retry.
                    if ((*tree_)[cid].height > previous_height + 0.001) continue;
                }
                break;
            }
        }
        cursor = (*tree_)[cid].y + (*tree_)[cid].height;
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
    LayoutProfilePart profile(2);
    Box& box = (*tree_)[id];
    // A flex line already decided this box's height; keep it.
    if (box.cross_size_imposed) return;

    // CSS Containment L2: a size-contained box is sized as though it had no
    // contents. Applied HERE rather than by skipping the children's layout,
    // because the children are still laid out and still have real geometry —
    // Chrome and the reference both report normal rects for the subtree of a
    // `content-visibility: hidden` box. Only the box's own contribution goes.
    if (has_size_containment(box.style)) {
        const double intrinsic = contain_intrinsic_height(box.style, ctx_, font_size);
        content_bottom_y = box.padding_top + box.border_top + (intrinsic >= 0 ? intrinsic : 0.0);
    }
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
        resolve_length(box.style, kId_height, ctx_, font_size, basis);

    const bool border_box = is_border_box(box.style);
    const double frame =
        box.padding_top + box.padding_bottom + box.border_top + box.border_bottom;

    double computed;
    double aspect_ratio = 0;
    if (height_r.kind == LengthKind::Length) {
        computed = border_box ? std::max(height_r.pixels, frame) : height_r.pixels + frame;
    } else if (try_resolve_aspect_ratio(box.style, &aspect_ratio) && aspect_ratio > 0 &&
               box.width > 0) {
        // Width set, height auto: the ratio derives the height, between the
        // boxes box-sizing names (css-sizing-4 §5.1). A 288px-wide 3/4
        // portrait with a 1px border is 383.33 tall under content-box (286
        // of content → 381.33, plus the border) and 384 under border-box;
        // Chrome and the reference both say so.
        if (border_box) {
            computed = box.width / aspect_ratio;
        } else {
            const double w_frame =
                box.padding_left + box.padding_right + box.border_left + box.border_right;
            computed = std::max(0.0, box.width - w_frame) / aspect_ratio + frame;
        }
    } else {
        computed = content_bottom_y + box.padding_bottom + box.border_bottom;
    }

    // min-/max-height share height's box-sizing basis, so a content-box bound
    // needs the frame added before it is compared with the border-box value.
    const ResolvedLength min_r =
        resolve_length(box.style, kId_min_height, ctx_, font_size, std::nullopt);
    const ResolvedLength max_r =
        resolve_length(box.style, kId_max_height, ctx_, font_size, std::nullopt);
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
    // height, matching Chrome. For an auto-height button the delta is zero,
    // making it a no-op.
    //
    // This models what Chrome does to a button's anonymous FLOW content, so it
    // must not touch a button that establishes some other formatting context.
    // The claim that a `display: flex` button "is laid out elsewhere" was
    // wrong — finish_height still runs for it, and the pass then centred a
    // second time on top of the flex algorithm's own `justify-content: center`.
    // audit-validation's `.play-btn` (a column flex, height 64) put its label
    // at 28.14 instead of 19.43, and the whole button's contents with it.
    const bool button_flow_content =
        box.display == DisplayKind::Block || box.display == DisplayKind::FlowRoot ||
        box.display == DisplayKind::InlineBlock || box.display == DisplayKind::ListItem;
    if (box.element && box.element->tag_name() == "button" && box.first_child != kNoBox &&
        button_flow_content) {
        const double content_box_h = computed - frame;
        const double natural_h = content_bottom_y - (box.padding_top + box.border_top);
        const double delta = (content_box_h - natural_h) * 0.5;
        if (delta > 0.5) {
            for (BoxId c : tree_->children(id)) (*tree_)[c].y += delta;
        }
    }
}

} // namespace weva
