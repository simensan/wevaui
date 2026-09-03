#include "weva/flex.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"
#include "weva/css_value.h"
#include "weva/inline_layout.h"

#include <algorithm>
#include <memory>
#include <cmath>
#include <optional>
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
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

// Read through the style's parsed cache: flex-grow, flex-shrink and order are
// read for every item on every pass.
double number_or(const ComputedStyle* style, std::string_view property, double fallback) {
    if (!style) return fallback;
    const CssValue* v = style->parsed(property);
    if (!v || v->kind() != CssValueKind::Number) return fallback;
    return static_cast<const CssNumber&>(*v).value;
}

// `gap` expands to row-gap/column-gap, and `normal` means zero for flex.
double gap_px(const ComputedStyle* style, std::string_view property, const LayoutContext& ctx,
              double font_size, double basis) {
    const std::string_view raw = get(style, property);
    if (raw.empty() || iequals(raw, "normal")) return 0;
    const ResolvedLength r = resolve_length(style, property, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return std::max(0.0, r.pixels);
    if (r.kind == LengthKind::Percent) return std::max(0.0, basis * r.percent * 0.01);
    return 0;
}

struct Item {
    BoxId box = kNoBox;
    int order = 0;
    int source_index = 0;
    double grow = 0;
    double shrink = 1;
    // The flex base size and the hypothetical main size (base clamped by
    // min/max), both OUTER — margins included — because every sum in §9.7 is
    // over outer sizes.
    double base = 0;
    double hypothetical = 0;
    double main = 0;
    double main_margins = 0;
    double cross_margins = 0;
    bool frozen = false;
    double min_main = 0;
    double max_main = -1;   // negative means none
    // `margin: auto` on the main axis, resolved in §9.5 step 1 before
    // justify-content sees any free space.
    bool auto_margin_start = false;
    bool auto_margin_end = false;
};

bool is_auto(std::string_view raw) { return iequals(raw, "auto"); }

struct Line {
    size_t begin = 0, end = 0;
    double cross = 0;       // the line's cross size
    double cross_pos = 0;   // offset of the line's cross-start from the content edge
    double main_pos = 0;    // where the first item starts (justify-content)
    double between = 0;     // main gap between items, justify included
};

// The buffers one call to layout_flex works in. Locals before, so every flex
// container in the tree built three vectors and freed them again -- 870 of
// vendor.html's heap allocations a pass. Nothing here outlives the call.
struct FlexScratch {
    std::vector<Item> items;
    std::vector<BoxId> out_of_flow;
    std::vector<Line> lines;
};

// A pool, because a flex item can itself be a flex container and is laid out
// inside its parent's call. Thread-local, so two documents do not share it.
std::vector<std::unique_ptr<FlexScratch>>& flex_scratch_pool() {
    thread_local std::vector<std::unique_ptr<FlexScratch>> pool;
    return pool;
}

class FlexScratchLease {
public:
    FlexScratchLease() {
        auto& pool = flex_scratch_pool();
        if (pool.empty()) {
            owned_ = std::make_unique<FlexScratch>();
        } else {
            owned_ = std::move(pool.back());
            pool.pop_back();
        }
        owned_->items.clear();
        owned_->out_of_flow.clear();
        owned_->lines.clear();
    }
    // Handed back with capacity intact; the next borrower empties them.
    ~FlexScratchLease() { flex_scratch_pool().push_back(std::move(owned_)); }
    FlexScratchLease(const FlexScratchLease&) = delete;
    FlexScratchLease& operator=(const FlexScratchLease&) = delete;
    FlexScratch& operator*() const { return *owned_; }

private:
    std::unique_ptr<FlexScratch> owned_;
};

} // namespace

double layout_flex(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    FlexScratchLease lease;
    FlexScratch& scratch = *lease;
    const ComputedStyle* style = (*tree)[container].style;
    const double font_size = (*tree)[container].font_size > 0 ? (*tree)[container].font_size
                                                              : ctx.root_font_size_px;

    const std::string_view direction = get(style, "flex-direction");
    const bool column = iequals(direction, "column") || iequals(direction, "column-reverse");
    bool reverse = iequals(direction, "row-reverse") || iequals(direction, "column-reverse");

    // `direction: rtl` reverses the INLINE axis, so it swaps row and
    // row-reverse and leaves a column container alone. Only in a horizontal
    // writing mode: in a vertical one the inline axis is the other one, and
    // `direction` is already spoken for there.
    //
    // Ports FlexProperties.ApplyDirectionality. Without it a right-to-left
    // flex row packed itself from the left, which is what the oracle's
    // cov-logical case caught -- Chrome and the reference agreed against us on
    // all three items.
    if (!column && iequals(get(style, "direction"), "rtl")) {
        const std::string_view writing_mode = get(style, "writing-mode");
        if (writing_mode.empty() || iequals(writing_mode, "horizontal-tb")) reverse = !reverse;
    }

    const double main_gap = column ? gap_px(style, "row-gap", ctx, font_size, content_height)
                                   : gap_px(style, "column-gap", ctx, font_size, content_width);

    // The main axis's available space. A column container with an indefinite
    // height has no main size to distribute, so nothing grows or shrinks.
    double available_main = column ? content_height : content_width;
    bool definite_main = available_main >= 0;

    // ---- Collect the items ------------------------------------------------
    std::vector<Item>& items = scratch.items;
    std::vector<BoxId>& out_of_flow = scratch.out_of_flow;
    int source_index = 0;
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        // Read from the STYLE, not from cb.position: the box's position is
        // stamped by apply_box_model during layout, which has not run yet.
        // Filtering on the unstamped field let a `position: absolute` child
        // count as a flex item, and its width ate a share of the free space —
        // three `flex: 1` cells came out 126.67 wide instead of 142.67.
        const PositionType pos = parse_position_type(get(cb.style, "position"));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            // Still laid out, so the positioning pass has geometry to place;
            // it is just not an item. Its STATIC position (what `left/top:
            // auto` fall back to) is where it would sit as the sole item
            // (§4.1): the container's justify-content and align-items apply.
            // An 80px halo under an 8px marker with both centred sits at
            // -36, -36, in Chrome and the reference alike.
            // An auto width is shrink-to-fit for an absolutely positioned box
            // (§10.3.7) and the static position centres its FINAL width; laid
            // out at the container's width it would centre a full-width box
            // at 0 and only shrink afterwards, in the positioning pass.
            const std::string_view w_raw = get(cb.style, "width");
            if (w_raw.empty() || iequals(w_raw, "auto")) {
                block->shrink_to_fit(c, content_width, style);
            } else {
                block->layout_block(c, content_width, style);
            }
            out_of_flow.push_back(c);
            continue;
        }
        Item it;
        it.box = c;
        it.source_index = source_index++;
        it.order = static_cast<int>(number_or(cb.style, "order", 0));
        items.push_back(it);
    }
    // Static positions for the out-of-flow children (§4.1): each sits where
    // it would as the sole item, so the container's justify-content and
    // align-items apply. Placed against the container's FINAL content size —
    // a cross size known only once the lines are — which is why this is a
    // function called at the end rather than done at collection. An 80px
    // halo under an 8px marker with both centred sits at -36, -36; under an
    // auto-height marker it centres on the marker's laid-out height.
    const auto place_out_of_flow = [&](double final_main, double final_cross) {
        const Box& cont = (*tree)[container];
        const double li = cont.padding_left + cont.border_left;
        const double ti = cont.padding_top + cont.border_top;
        const auto offset_in = [&](std::string_view keyword, double avail, double outer) {
            if (avail < 0) return 0.0;
            if (iequals(keyword, "center")) return (avail - outer) * 0.5;
            if (iequals(keyword, "flex-end") || iequals(keyword, "end")) return avail - outer;
            return 0.0;
        };
        const std::string_view justify = get(style, "justify-content");
        for (BoxId c : out_of_flow) {
            Box& ab = (*tree)[c];
            std::string_view self = get(ab.style, "align-self");
            if (self.empty() || iequals(self, "auto")) self = get(style, "align-items");
            const double outer_w = ab.width + ab.margin_left + ab.margin_right;
            const double outer_h = ab.height + ab.margin_top + ab.margin_bottom;
            const double main_off = offset_in(justify, final_main, column ? outer_h : outer_w);
            const double cross_off = offset_in(self, final_cross, column ? outer_w : outer_h);
            ab.x = li + ab.margin_left + (column ? cross_off : main_off);
            ab.y = ti + ab.margin_top + (column ? main_off : cross_off);
        }
    };

    if (items.empty()) {
        place_out_of_flow(column ? content_height : content_width,
                          column ? content_width : content_height);
        return 0;
    }

    // §5.4: items are laid out in `order`, ties broken by document order.
    std::stable_sort(items.begin(), items.end(), [](const Item& a, const Item& b) {
        return a.order != b.order ? a.order < b.order : a.source_index < b.source_index;
    });

    // ---- Flex base and hypothetical main sizes (§9.2) ----------------------
    // Percentages on the main axis resolve against the container's main size
    // only when it is definite; otherwise they have no basis at all.
    const std::optional<double> main_basis =
        definite_main ? std::optional<double>(available_main) : std::nullopt;
    for (Item& it : items) {
        // Laid out once at the container's inner width so its natural sizes and
        // box model are resolved; the main size is corrected below.
        block->layout_block(it.box, content_width, style);
        Box& b = (*tree)[it.box];
        const ComputedStyle* is = b.style;

        it.grow = std::max(0.0, number_or(is, "flex-grow", 0));
        it.shrink = std::max(0.0, number_or(is, "flex-shrink", 1));
        // An auto margin on the main axis is resolved by THIS algorithm (§9.5
        // step 1), not by block layout, which may already have centred the box
        // with it. Zeroed here so the share below is the whole used value.
        it.auto_margin_start = is_auto(get(is, column ? "margin-top" : "margin-left"));
        it.auto_margin_end = is_auto(get(is, column ? "margin-bottom" : "margin-right"));
        if (it.auto_margin_start) (column ? b.margin_top : b.margin_left) = 0;
        if (it.auto_margin_end) (column ? b.margin_bottom : b.margin_right) = 0;
        it.main_margins = column ? b.margin_top + b.margin_bottom : b.margin_left + b.margin_right;
        it.cross_margins = column ? b.margin_left + b.margin_right : b.margin_top + b.margin_bottom;

        const std::string_view basis_raw = get(is, "flex-basis");
        const std::string_view size_raw = get(is, column ? "height" : "width");
        double base = column ? b.height : b.width;
        if (!basis_raw.empty() && !iequals(basis_raw, "auto") &&
            !iequals(basis_raw, "content")) {
            // A percentage against an INDEFINITE main size behaves as
            // `content` (§7.2.3). The basis is passed as absent, not as -1: a
            // present basis turns the percentage into a length, and -1 made
            // `flex: 1` (basis 0%) in an auto-height column resolve to 0.
            const ResolvedLength r =
                resolve_length(is, "flex-basis", ctx, b.font_size > 0 ? b.font_size : font_size,
                               main_basis);
            if (r.kind == LengthKind::Length) {
                base = r.pixels;
            } else if (r.kind == LengthKind::Percent && definite_main) {
                base = available_main * r.percent * 0.01;
            }
            // flex-basis is a CONTENT size under content-box sizing, while
            // b.width/height are border-box, so the frame is added back.
            if (!is_border_box(is)) {
                base += column ? b.padding_top + b.padding_bottom + b.border_top + b.border_bottom
                               : b.padding_left + b.padding_right + b.border_left + b.border_right;
            }
        } else if (iequals(size_raw, "auto") || size_raw.empty()) {
            // `flex-basis: auto` with an auto size is the content size. In a
            // row that is max-content; in a column the laid-out height already
            // is it.
            if (!column) {
                const double frame =
                    b.padding_left + b.padding_right + b.border_left + b.border_right;
                base = max_content_width(*tree, it.box, &ctx) + frame;
            }
        }

        const ResolvedLength min_r =
            resolve_length(is, column ? "min-height" : "min-width", ctx,
                           b.font_size > 0 ? b.font_size : font_size, main_basis);
        // min/max are compared with the BORDER-box main size, so under
        // content-box sizing they carry the frame — the same correction
        // flex-basis gets above. Without it `min-width: 38px; padding: 0 10px`
        // clamped the border box to 38 where Chrome and the reference give 58.
        const double main_frame =
            column ? b.padding_top + b.padding_bottom + b.border_top + b.border_bottom
                   : b.padding_left + b.padding_right + b.border_left + b.border_right;
        const double minmax_frame = is_border_box(is) ? 0 : main_frame;
        if (min_r.kind == LengthKind::Length) {
            it.min_main = std::max(0.0, min_r.pixels) + minmax_frame;
        } else if (column && (min_r.kind == LengthKind::Auto || get(is, "min-height").empty())) {
            // §4.5, the automatic minimum size: `min-height: auto` on a column
            // item is its content-based minimum unless it is a scroll
            // container. A `height: 100vh; overflow: auto` app shell whose
            // content overflows was shrinking its topbar to its padding and a
            // quest card to 40px less than its own children; the container
            // is what overflows (and scrolls), not the items.
            //
            // The laid-out height at this point is the content height (or the
            // specified one, which the spec bounds the minimum by anyway).
            // Row items are not yet covered: their minimum is the min-content
            // WIDTH, which needs a probe this pass does not run.
            const std::string_view oy = get(is, "overflow-y");
            const std::string_view ox = get(is, "overflow-x");
            const bool scroll_container =
                (!oy.empty() && !iequals(oy, "visible") && !iequals(oy, "clip")) ||
                (!ox.empty() && !iequals(ox, "visible") && !iequals(ox, "clip"));
            // Only an AUTO height is its content: an explicit `height: 80px` on
            // an empty item has a content size of zero and shrinks freely.
            const bool auto_main = size_raw.empty() || iequals(size_raw, "auto");
            if (!scroll_container && auto_main) it.min_main = std::max(it.min_main, b.height);
        } else if (!column && (min_r.kind == LengthKind::Auto || get(is, "min-width").empty())) {
            // The row counterpart: a row item's automatic minimum is its
            // min-content WIDTH — the widest word, atom or fixed-width child
            // — unless it is a scroll container. A carousel of fixed-width
            // cards wider than the page keeps its 1310px and overflows,
            // centred at x = -15, as Chrome and the reference lay it out.
            const std::string_view oy = get(is, "overflow-y");
            const std::string_view ox = get(is, "overflow-x");
            const bool scroll_container =
                (!oy.empty() && !iequals(oy, "visible") && !iequals(oy, "clip")) ||
                (!ox.empty() && !iequals(ox, "visible") && !iequals(ox, "clip"));
            const bool auto_main = size_raw.empty() || iequals(size_raw, "auto");
            if (!scroll_container && auto_main) {
                const double frame =
                    b.padding_left + b.padding_right + b.border_left + b.border_right;
                it.min_main = std::max(it.min_main, min_content_width(*tree, it.box, &ctx) + frame);
            }
        }
        const ResolvedLength max_r =
            resolve_length(is, column ? "max-height" : "max-width", ctx,
                           b.font_size > 0 ? b.font_size : font_size, main_basis);
        if (max_r.kind == LengthKind::Length) {
            it.max_main = std::max(0.0, max_r.pixels) + minmax_frame;
        }


        it.base = std::max(0.0, base);
        it.hypothetical = it.base;
        if (it.hypothetical < it.min_main) it.hypothetical = it.min_main;
        if (it.max_main >= 0 && it.hypothetical > it.max_main) it.hypothetical = it.max_main;
        it.main = it.hypothetical;
    }

    // ---- Flex lines (§9.3) --------------------------------------------------
    // Single-line unless `flex-wrap` asks otherwise AND the main size is
    // definite — without one there is nothing to wrap against. Items go onto
    // a line while their outer hypothetical sizes plus gaps fit; the first
    // that does not fit starts the next line, and an item that fits nowhere
    // still gets a line of its own. Before this every wrapping card grid was
    // one squeezed row.
    const std::string_view wrap_raw = get(style, "flex-wrap");
    const bool wrap_reverse = iequals(wrap_raw, "wrap-reverse");
    const bool wraps = iequals(wrap_raw, "wrap") || wrap_reverse;

    const double total_gap = main_gap * static_cast<double>(items.size() - 1);
    double used = total_gap;
    for (const Item& it : items) used += it.hypothetical + it.main_margins;

    // §9.2 step 4: a container with no definite main size is sized to its
    // content — and then clamped by its own min/max, which makes that size
    // definite and the items flex into it. Column containers with
    // `min-height: 100vh` and a `flex: 1` body are the page-shell idiom this
    // exists for: without the clamp the body's 0% basis stayed 0.
    if (!definite_main && column) {
        const Box& cb = (*tree)[container];
        const double frame =
            cb.padding_top + cb.padding_bottom + cb.border_top + cb.border_bottom;
        const double own_frame = is_border_box(style) ? frame : 0;
        const ResolvedLength min_r =
            resolve_length(style, "min-height", ctx, font_size, std::nullopt);
        const ResolvedLength max_r =
            resolve_length(style, "max-height", ctx, font_size, std::nullopt);
        double clamped = used;
        if (min_r.kind == LengthKind::Length) {
            clamped = std::max(clamped, std::max(0.0, min_r.pixels - own_frame));
        }
        if (max_r.kind == LengthKind::Length) {
            clamped = std::min(clamped, std::max(0.0, max_r.pixels - own_frame));
        }
        if (std::fabs(clamped - used) > 1e-9) {
            available_main = clamped;
            definite_main = true;
        }
    }

    std::vector<Line>& lines = scratch.lines;
    lines.clear();
    if (wraps && definite_main) {
        size_t begin = 0;
        double line_used = 0;
        for (size_t i = 0; i < items.size(); ++i) {
            const double outer = items[i].hypothetical + items[i].main_margins;
            if (i > begin && line_used + main_gap + outer > available_main + 1e-9) {
                lines.push_back({begin, i});
                begin = i;
                line_used = 0;
            }
            line_used += (i > begin ? main_gap : 0.0) + outer;
        }
        lines.push_back({begin, items.size()});
    } else {
        lines.push_back({0, items.size()});
    }

    // ---- Resolve the flexible lengths (§9.7), one line at a time -----------
    const auto resolve_line = [&](const Line& ln) {
        const size_t n = ln.end - ln.begin;
        const double gaps = main_gap * static_cast<double>(n - 1);
        double line_used = gaps;
        for (size_t i = ln.begin; i < ln.end; ++i) {
            line_used += items[i].hypothetical + items[i].main_margins;
        }
        if (!definite_main || std::fabs(available_main - line_used) <= 1e-9) return;
        const bool growing = available_main > line_used;
        // An item that cannot flex in the needed direction is frozen up front.
        for (size_t i = ln.begin; i < ln.end; ++i) {
            Item& it = items[i];
            it.main = it.hypothetical;
            it.frozen = growing ? it.grow <= 0 : it.shrink <= 0;
        }
        // Loop because clamping an item to its min or max frees space that the
        // remaining items must absorb — §9.7 step 4's "restart" condition.
        for (size_t pass = 0; pass < n + 1; ++pass) {
            double frozen_total = gaps;
            double flex_factor = 0;
            for (size_t i = ln.begin; i < ln.end; ++i) {
                const Item& it = items[i];
                frozen_total += it.main_margins;
                if (it.frozen) frozen_total += it.main;
                else {
                    frozen_total += it.base;
                    flex_factor += growing ? it.grow : it.shrink * it.base;
                }
            }
            const double free_space = available_main - frozen_total;
            if (flex_factor <= 0) break;

            bool clamped_any = false;
            for (size_t i = ln.begin; i < ln.end; ++i) {
                Item& it = items[i];
                if (it.frozen) continue;
                const double share = growing ? it.grow : it.shrink * it.base;
                double target = it.base + free_space * (share / flex_factor);
                if (target < it.min_main) {
                    target = it.min_main;
                    it.frozen = true;
                    clamped_any = true;
                } else if (it.max_main >= 0 && target > it.max_main) {
                    target = it.max_main;
                    it.frozen = true;
                    clamped_any = true;
                }
                if (target < 0) target = 0;
                it.main = target;
            }
            if (!clamped_any) break;
        }
    };
    for (const Line& ln : lines) resolve_line(ln);

    const std::string_view align_items = get(style, "align-items");
    const auto self_align = [&](const Box& b) {
        std::string_view self = get(b.style, "align-self");
        if (self.empty() || iequals(self, "auto")) self = align_items;
        if (self.empty() || iequals(self, "normal")) self = "stretch";
        return self;
    };

    // An item's baseline is its first line box's, or its bottom margin edge
    // when it has none — the same rule an inline-block follows.
    const auto first_baseline = [&](BoxId box) {
        double baseline = (*tree)[box].height;
        for (BoxId c : tree->children(box)) {
            if ((*tree)[c].kind == BoxKind::Line) {
                baseline = (*tree)[c].y + (*tree)[c].baseline;
                break;
            }
        }
        return baseline;
    };

    // ---- Re-lay each item at its final main size, and measure the cross ----
    for (Line& ln : lines) {
        double line_cross = 0;
        // §9.4 step 8: the baseline-aligned items of a row contribute as a
        // group — the largest distance from a baseline up to its item's outer
        // cross-start edge, plus the largest distance from a baseline down to
        // its item's outer cross-end edge. A 20px title and a padded 11px
        // badge that align on the baseline make a 25.22px line, not a 22.86px
        // one: the badge hangs below the title.
        double baseline_above = 0;
        double baseline_below = 0;
        for (size_t i = ln.begin; i < ln.end; ++i) {
            Item& it = items[i];
            // Re-indexed after every call that can lay out a box: BoxTree::create
            // appends to a vector, so a `Box&` held across a relayout dangles.
            if (!column) {
                if (std::fabs((*tree)[it.box].width - it.main) > 1e-9) {
                    block->relayout_at(it.box, it.main);
                }
            } else {
                // Re-laid, not merely stamped: the flexed main size is definite
                // for the item's own contents (§9.8), so a nested row flex
                // inside it stretches its children to this height, and a
                // `margin-top: auto` child has this height to push against.
                if (std::fabs((*tree)[it.box].height - it.main) > 1e-9) {
                    block->relayout_at_size(it.box, (*tree)[it.box].width, it.main);
                }
                (*tree)[it.box].height = it.main;
                // §9.4: an item that is NOT being stretched sizes to fit its own
                // content on the cross axis. In a column that means the width
                // has to come off the block default of filling the container.
                const std::string_view cross_raw = get((*tree)[it.box].style, "width");
                if ((cross_raw.empty() || iequals(cross_raw, "auto")) &&
                    !iequals(self_align((*tree)[it.box]), "stretch")) {
                    const Box& cb = (*tree)[it.box];
                    const double frame =
                        cb.padding_left + cb.padding_right + cb.border_left + cb.border_right;
                    const double fit =
                        std::min(content_width, max_content_width(*tree, it.box, &ctx) + frame);
                    if (std::fabs((*tree)[it.box].width - fit) > 1e-9) {
                        block->relayout_at(it.box, fit);
                    }
                    (*tree)[it.box].height = it.main;
                }
            }
            const Box& measured = (*tree)[it.box];
            const double outer = (column ? measured.width : measured.height) + it.cross_margins;
            if (!column && iequals(self_align(measured), "baseline")) {
                const double above = first_baseline(it.box) + measured.margin_top;
                baseline_above = std::max(baseline_above, above);
                baseline_below = std::max(baseline_below, outer - above);
            } else {
                line_cross = std::max(line_cross, outer);
            }
        }
        ln.cross = std::max(line_cross, baseline_above + baseline_below);
    }

    // ---- Cross sizes of the lines (§9.4 step 8, §9.6 align-content) --------
    const double container_cross = column ? content_width : content_height;   // -1: indefinite
    const double cross_gap = column ? gap_px(style, "column-gap", ctx, font_size, content_width)
                                    : gap_px(style, "row-gap", ctx, font_size, content_height);
    double total_cross = 0;
    if (lines.size() == 1) {
        Line& ln = lines[0];
        // A definite cross size IS the single line's cross size, not a floor
        // under the items: an item wider than a column container overflows it
        // and centres around it, rather than growing the line to itself.
        if (container_cross >= 0) ln.cross = container_cross;
        // ...and a row container with an auto height but a min-height is at
        // least that tall, which is what `align-items: flex-end` in a
        // `min-height: 100vh` stage pushes against.
        if (!column && content_height < 0) {
            const Box& cb = (*tree)[container];
            const double frame =
                cb.padding_top + cb.padding_bottom + cb.border_top + cb.border_bottom;
            const double own_frame = is_border_box(style) ? frame : 0;
            const ResolvedLength min_r =
                resolve_length(style, "min-height", ctx, font_size, std::nullopt);
            const ResolvedLength max_r =
                resolve_length(style, "max-height", ctx, font_size, std::nullopt);
            if (min_r.kind == LengthKind::Length) {
                ln.cross = std::max(ln.cross, std::max(0.0, min_r.pixels - own_frame));
            }
            if (max_r.kind == LengthKind::Length) {
                ln.cross = std::min(ln.cross, std::max(0.0, max_r.pixels - own_frame));
            }
        }
        ln.cross_pos = 0;
        total_cross = ln.cross;
    } else {
        double sum = cross_gap * static_cast<double>(lines.size() - 1);
        for (const Line& ln : lines) sum += ln.cross;
        double before = 0, extra_between = 0;
        if (container_cross >= 0 && container_cross > sum) {
            const double free_space = container_cross - sum;
            const std::string_view ac = get(style, "align-content");
            const size_t n = lines.size();
            if (ac.empty() || iequals(ac, "normal") || iequals(ac, "stretch")) {
                // The free space is split equally onto the lines themselves.
                for (Line& ln : lines) ln.cross += free_space / static_cast<double>(n);
            } else if (iequals(ac, "center")) {
                before = free_space * 0.5;
            } else if (iequals(ac, "flex-end") || iequals(ac, "end")) {
                before = free_space;
            } else if (iequals(ac, "space-between")) {
                extra_between = free_space / static_cast<double>(n - 1);
            } else if (iequals(ac, "space-around")) {
                extra_between = free_space / static_cast<double>(n);
                before = extra_between * 0.5;
            } else if (iequals(ac, "space-evenly")) {
                extra_between = free_space / static_cast<double>(n + 1);
                before = extra_between;
            }
        }
        double pos = before;
        for (Line& ln : lines) {
            ln.cross_pos = pos;
            pos += ln.cross + cross_gap + extra_between;
        }
        total_cross = container_cross >= 0 ? container_cross : pos - cross_gap - extra_between;
        if (wrap_reverse) {
            // Lines stack from the cross-end instead.
            for (Line& ln : lines) ln.cross_pos = total_cross - ln.cross_pos - ln.cross;
        }
    }

    // ---- Main-axis alignment (§9.5), per line ------------------------------
    const std::string_view justify = get(style, "justify-content");
    for (Line& ln : lines) {
        const size_t n = ln.end - ln.begin;
        double content_main = main_gap * static_cast<double>(n - 1);
        for (size_t i = ln.begin; i < ln.end; ++i) content_main += items[i].main + items[i].main_margins;
        const double leftover = definite_main ? available_main - content_main : 0;

        // §9.5 step 1: positive free space goes to the main-axis auto margins
        // first, split equally; justify-content only sees what is left, which
        // with any auto margin present is nothing. The margins are written
        // back onto the boxes so placement and any later reader see them.
        double leftover_for_justify = leftover;
        if (leftover > 0) {
            int auto_count = 0;
            for (size_t i = ln.begin; i < ln.end; ++i) {
                auto_count += (items[i].auto_margin_start ? 1 : 0) + (items[i].auto_margin_end ? 1 : 0);
            }
            if (auto_count > 0) {
                const double each = leftover / static_cast<double>(auto_count);
                for (size_t i = ln.begin; i < ln.end; ++i) {
                    Item& it = items[i];
                    Box& b = (*tree)[it.box];
                    if (it.auto_margin_start) {
                        (column ? b.margin_top : b.margin_left) += each;
                        it.main_margins += each;
                    }
                    if (it.auto_margin_end) {
                        (column ? b.margin_bottom : b.margin_right) += each;
                        it.main_margins += each;
                    }
                }
                leftover_for_justify = 0;
            }
        }

        ln.main_pos = 0;
        ln.between = main_gap;
        if (leftover_for_justify < 0) {
            // Negative free space: `center` and `end` are unsafe and overflow
            // to both sides / the start (§8.2); `space-around` and
            // `space-evenly` fall back to center, `space-between` to start. A
            // 1310px carousel centred in a 1280px page sits at x = -15.
            const double lo = leftover_for_justify;
            if (iequals(justify, "center") || iequals(justify, "space-around") ||
                iequals(justify, "space-evenly")) {
                ln.main_pos = lo * 0.5;
            } else if (iequals(justify, "flex-end") || iequals(justify, "end") ||
                       iequals(justify, "right")) {
                ln.main_pos = lo;
            }
        } else if (leftover_for_justify > 0) {
            const double lo = leftover_for_justify;
            if (iequals(justify, "center")) ln.main_pos = lo * 0.5;
            else if (iequals(justify, "flex-end") || iequals(justify, "end") ||
                     iequals(justify, "right")) {
                ln.main_pos = lo;
            } else if (iequals(justify, "space-between") && n > 1) {
                ln.between += lo / static_cast<double>(n - 1);
            } else if (iequals(justify, "space-around")) {
                const double each = lo / static_cast<double>(n);
                ln.main_pos = each * 0.5;
                ln.between += each;
            } else if (iequals(justify, "space-evenly")) {
                const double each = lo / static_cast<double>(n + 1);
                ln.main_pos = each;
                ln.between += each;
            }
        }
    }

    // ---- Cross-axis alignment (§9.6) and placement -------------------------
    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;

    for (const Line& ln : lines) {
        // Baseline alignment needs the deepest first baseline on the line
        // before any item can be placed.
        double max_baseline = 0;
        for (size_t i = ln.begin; i < ln.end; ++i) {
            const Box& b = (*tree)[items[i].box];
            if (!iequals(self_align(b), "baseline")) continue;
            max_baseline = std::max(max_baseline, first_baseline(items[i].box) + b.margin_top);
        }

        // `row-reverse` / `column-reverse`: main-start is the END edge, so the
        // first item sits against it and the rest run back toward the start
        // (§5.1). With a definite main size the extent is the container's;
        // without one the content's own extent stands in, which is what an
        // auto-height column-reverse packs against.
        double reverse_extent = 0;
        if (reverse) {
            reverse_extent = definite_main ? available_main : 0;
            if (!definite_main) {
                reverse_extent = main_gap * static_cast<double>(ln.end - ln.begin - 1);
                for (size_t i = ln.begin; i < ln.end; ++i) {
                    reverse_extent += items[i].main + items[i].main_margins;
                }
            }
        }
        double cursor = ln.main_pos;
        for (size_t k = 0; k < ln.end - ln.begin; ++k) {
            const size_t i = ln.begin + k;
            Item& it = items[i];
            const std::string_view self = self_align((*tree)[it.box]);

            const double outer_cross =
                (column ? (*tree)[it.box].width : (*tree)[it.box].height) + it.cross_margins;
            double cross_pos = 0;
            if (iequals(self, "center")) {
                cross_pos = (ln.cross - outer_cross) * 0.5;
            } else if (iequals(self, "flex-end") || iequals(self, "end")) {
                cross_pos = ln.cross - outer_cross;
            } else if (iequals(self, "baseline") && !column) {
                cross_pos = max_baseline - (first_baseline(it.box) + (*tree)[it.box].margin_top);
            } else if (!iequals(self, "flex-start") && !iequals(self, "start")) {
                // `stretch` is the initial value: an item with an auto cross size
                // fills the line. One with a definite size keeps it.
                const std::string_view cross_raw =
                    get((*tree)[it.box].style, column ? "width" : "height");
                if (cross_raw.empty() || iequals(cross_raw, "auto")) {
                    double stretched = std::max(0.0, ln.cross - it.cross_margins);
                    // §9.4: the stretched size is still clamped by the item's own
                    // min/max in that axis. A `max-width: 760px` grid in a column
                    // was stretched to the page.
                    {
                        const Box& ib = (*tree)[it.box];
                        const double item_fs = ib.font_size > 0 ? ib.font_size : font_size;
                        const double cross_frame =
                            column ? ib.padding_left + ib.padding_right + ib.border_left + ib.border_right
                                   : ib.padding_top + ib.padding_bottom + ib.border_top + ib.border_bottom;
                        const double mm_frame = is_border_box(ib.style) ? 0 : cross_frame;
                        const std::optional<double> cross_basis =
                            container_cross >= 0 ? std::optional<double>(container_cross)
                                                 : std::nullopt;
                        const ResolvedLength min_c = resolve_length(
                            ib.style, column ? "min-width" : "min-height", ctx, item_fs, cross_basis);
                        const ResolvedLength max_c = resolve_length(
                            ib.style, column ? "max-width" : "max-height", ctx, item_fs, cross_basis);
                        if (min_c.kind == LengthKind::Length) {
                            stretched = std::max(stretched, min_c.pixels + mm_frame);
                        }
                        if (max_c.kind == LengthKind::Length) {
                            stretched = std::min(stretched, max_c.pixels + mm_frame);
                        }
                    }
                    // Re-laid, not just stamped: anything inside whose layout
                    // depends on the cross size has to see the stretched value.
                    if (!column) {
                        if (std::fabs((*tree)[it.box].height - stretched) > 1e-9) {
                            block->relayout_at_size(it.box, (*tree)[it.box].width, stretched);
                        }
                    } else if (std::fabs((*tree)[it.box].width - stretched) > 1e-9) {
                        block->relayout_at_size(it.box, stretched, (*tree)[it.box].height);
                    }
                }
            }
            // CSS Box Alignment §5.4: `center` and `end` default to UNSAFE, so
            // an item wider than the line overflows both sides equally rather
            // than being pushed back to the start. Baseline stays clamped.
            if (cross_pos < 0 && iequals(self, "baseline")) cross_pos = 0;
            // wrap-reverse swaps cross-start and cross-end (§5.2), for the
            // items within a line as much as for the lines: flex-start sits
            // against the line's far edge. A single line of buttons in a
            // 150px wrap-reverse strip sits at its bottom.
            // `center` is symmetric and an applied stretch fills the line;
            // a stretch that could not apply (definite cross size) is
            // flex-start and flips with it.
            if (wrap_reverse && !iequals(self, "center")) {
                const double now_cross =
                    (column ? (*tree)[it.box].width : (*tree)[it.box].height) + it.cross_margins;
                cross_pos = ln.cross - now_cross - cross_pos;
            }

            // Safe from here: placement creates nothing.
            Box& b = (*tree)[it.box];
            const double main_offset =
                reverse ? reverse_extent - cursor - it.main - it.main_margins : cursor;
            if (column) {
                b.x = left_inner + ln.cross_pos + cross_pos + b.margin_left;
                b.y = top_inner + main_offset + b.margin_top;
            } else {
                b.x = left_inner + main_offset + b.margin_left;
                b.y = top_inner + ln.cross_pos + cross_pos + b.margin_top;
            }
            cursor += it.main + it.main_margins + ln.between;
        }
    }

    // The container's content height: the lines' cross extent in a row, the
    // items' extent in a column.
    if (!column) {
        place_out_of_flow(available_main, total_cross);
        return total_cross;
    }
    double bottom = 0;
    for (const Item& it : items) {
        const Box& b = (*tree)[it.box];
        bottom = std::max(bottom, b.y + b.height + b.margin_bottom - top_inner);
    }
    place_out_of_flow(definite_main ? available_main : bottom, total_cross);
    return bottom;
}

} // namespace weva
