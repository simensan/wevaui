#include "weva/flex.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"
#include "weva/css_value.h"
#include "weva/inline_layout.h"

#include <algorithm>
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

} // namespace

double layout_flex(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    const ComputedStyle* style = (*tree)[container].style;
    const double font_size = (*tree)[container].font_size > 0 ? (*tree)[container].font_size
                                                              : ctx.root_font_size_px;

    const std::string_view direction = get(style, "flex-direction");
    const bool column = iequals(direction, "column") || iequals(direction, "column-reverse");
    const bool reverse = iequals(direction, "row-reverse") || iequals(direction, "column-reverse");

    const double main_gap = column ? gap_px(style, "row-gap", ctx, font_size, content_height)
                                   : gap_px(style, "column-gap", ctx, font_size, content_width);

    // The main axis's available space. A column container with an indefinite
    // height has no main size to distribute, so nothing grows or shrinks.
    double available_main = column ? content_height : content_width;
    bool definite_main = available_main >= 0;

    // ---- Collect the items ------------------------------------------------
    std::vector<Item> items;
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
            // it is just not an item.
            block->layout_block(c, content_width, style);
            continue;
        }
        Item it;
        it.box = c;
        it.source_index = source_index++;
        it.order = static_cast<int>(number_or(cb.style, "order", 0));
        items.push_back(it);
    }
    if (items.empty()) return 0;

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

    // ---- Resolve the flexible lengths (§9.7) -------------------------------
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

    if (definite_main && std::fabs(available_main - used) > 1e-9) {
        const bool growing = available_main > used;
        // An item that cannot flex in the needed direction is frozen up front.
        for (Item& it : items) {
            it.main = it.hypothetical;
            it.frozen = growing ? it.grow <= 0 : it.shrink <= 0;
        }
        // Loop because clamping an item to its min or max frees space that the
        // remaining items must absorb — §9.7 step 4's "restart" condition.
        for (int pass = 0; pass < static_cast<int>(items.size()) + 1; ++pass) {
            double frozen_total = total_gap;
            double flex_factor = 0;
            for (const Item& it : items) {
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
            for (Item& it : items) {
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
    }

    const std::string_view align_items = get(style, "align-items");
    const auto self_align = [&](const Box& b) {
        std::string_view self = get(b.style, "align-self");
        if (self.empty() || iequals(self, "auto")) self = align_items;
        if (self.empty() || iequals(self, "normal")) self = "stretch";
        return self;
    };

    // ---- Re-lay each item at its final main size, and measure the cross ----
    double line_cross = 0;
    for (Item& it : items) {
        // Re-indexed after every call that can lay out a box: BoxTree::create
        // appends to a vector, so a `Box&` held across a relayout dangles. This
        // is the third place in the port to hit that; see PORT_PLAN.md on
        // making the storage stable instead of relying on discipline.
        if (!column) {
            if (std::fabs((*tree)[it.box].width - it.main) > 1e-9) {
                block->relayout_at(it.box, it.main);
            }
        } else {
            // Re-laid, not merely stamped: the flexed main size is definite for
            // the item's own contents (§9.8), so a nested row flex inside it
            // stretches its children to this height, and a `margin-top: auto`
            // child has this height to push against. Stamping alone left a
            // `flex: 1 1 auto` row 180 tall with a 0-tall stretched child.
            if (std::fabs((*tree)[it.box].height - it.main) > 1e-9) {
                block->relayout_at_size(it.box, (*tree)[it.box].width, it.main);
            }
            (*tree)[it.box].height = it.main;
            // §9.4: an item that is NOT being stretched sizes to fit its own
            // content on the cross axis. In a column that means the width has
            // to come off the block default of filling the container — an
            // `align-items: center` item was coming out full width and then
            // "centred" with no space to move in.
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
        line_cross = std::max(line_cross,
                              (column ? measured.width : measured.height) + it.cross_margins);
    }
    // A row container with a definite height gives its line that height, so
    // `align-items: center` centres against the container rather than against
    // the tallest item.
    if (!column && content_height >= 0) line_cross = std::max(line_cross, content_height);
    if (column && content_width >= 0) line_cross = std::max(line_cross, content_width);
    // ...and a row container with an auto height but a min-height is at least
    // that tall, which is what `align-items: flex-end` in a `min-height: 100vh`
    // stage pushes against. Same rule as the column main-size clamp above.
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
            line_cross = std::max(line_cross, std::max(0.0, min_r.pixels - own_frame));
        }
        if (max_r.kind == LengthKind::Length) {
            line_cross = std::min(line_cross, std::max(0.0, max_r.pixels - own_frame));
        }
    }

    // ---- Main-axis alignment (§9.5) ---------------------------------------
    double content_main = total_gap;
    for (const Item& it : items) content_main += it.main + it.main_margins;
    const double leftover = definite_main ? available_main - content_main : 0;

    // §9.5 step 1: positive free space goes to the main-axis auto margins
    // first, split equally; justify-content only sees what is left, which with
    // any auto margin present is nothing. The margins are written back onto the
    // boxes so placement below and any later reader see the used values.
    double leftover_for_justify = leftover;
    if (leftover > 0) {
        int auto_count = 0;
        for (const Item& it : items) {
            auto_count += (it.auto_margin_start ? 1 : 0) + (it.auto_margin_end ? 1 : 0);
        }
        if (auto_count > 0) {
            const double each = leftover / static_cast<double>(auto_count);
            for (Item& it : items) {
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

    const std::string_view justify = get(style, "justify-content");
    double main_pos = 0;
    double between = main_gap;
    if (leftover_for_justify > 0) {
        const double leftover = leftover_for_justify;
        if (iequals(justify, "center")) main_pos = leftover * 0.5;
        else if (iequals(justify, "flex-end") || iequals(justify, "end") ||
                 iequals(justify, "right")) {
            main_pos = leftover;
        } else if (iequals(justify, "space-between") && items.size() > 1) {
            between += leftover / static_cast<double>(items.size() - 1);
        } else if (iequals(justify, "space-around")) {
            const double each = leftover / static_cast<double>(items.size());
            main_pos = each * 0.5;
            between += each;
        } else if (iequals(justify, "space-evenly")) {
            const double each = leftover / static_cast<double>(items.size() + 1);
            main_pos = each;
            between += each;
        }
    }

    // ---- Cross-axis alignment (§9.6) and placement -------------------------
    // Baseline alignment needs the deepest first baseline on the line before
    // any item can be placed.
    // An item's baseline is its first line box's, or its bottom margin edge
    // when it has none — the same rule an inline-block follows.
    double max_baseline = 0;
    for (const Item& it : items) {
        const Box& b = (*tree)[it.box];
        if (!iequals(self_align(b), "baseline")) continue;
        double baseline = b.height;
        for (BoxId c : tree->children(it.box)) {
            if ((*tree)[c].kind == BoxKind::Line) {
                baseline = (*tree)[c].y + (*tree)[c].baseline;
                break;
            }
        }
        max_baseline = std::max(max_baseline, baseline + b.margin_top);
    }

    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;

    if (reverse) std::reverse(items.begin(), items.end());

    double cursor = main_pos;
    for (const Item& it : items) {
        const std::string_view self = self_align((*tree)[it.box]);

        const double outer_cross =
            (column ? (*tree)[it.box].width : (*tree)[it.box].height) + it.cross_margins;
        double cross_pos = 0;
        if (iequals(self, "center")) {
            cross_pos = (line_cross - outer_cross) * 0.5;
        } else if (iequals(self, "flex-end") || iequals(self, "end")) {
            cross_pos = line_cross - outer_cross;
        } else if (iequals(self, "baseline") && !column) {
            double baseline = (*tree)[it.box].height;
            for (BoxId c : tree->children(it.box)) {
                if ((*tree)[c].kind == BoxKind::Line) {
                    baseline = (*tree)[c].y + (*tree)[c].baseline;
                    break;
                }
            }
            cross_pos = max_baseline - (baseline + (*tree)[it.box].margin_top);
        } else if (!iequals(self, "flex-start") && !iequals(self, "start")) {
            // `stretch` is the initial value: an item with an auto cross size
            // fills the line. One with a definite size keeps it.
            const std::string_view cross_raw =
                get((*tree)[it.box].style, column ? "width" : "height");
            if (cross_raw.empty() || iequals(cross_raw, "auto")) {
                double stretched = std::max(0.0, line_cross - it.cross_margins);
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
                        (column ? content_width : content_height) >= 0
                            ? std::optional<double>(column ? content_width : content_height)
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
                // depends on the cross size has to see the stretched value. A
                // nested column flex container is the case that makes this
                // visible — its main size IS this height, and without the
                // re-layout its justify-content had nothing to centre in.
                if (!column) {
                    if (std::fabs((*tree)[it.box].height - stretched) > 1e-9) {
                        block->relayout_at_size(it.box, (*tree)[it.box].width, stretched);
                    }
                } else if (std::fabs((*tree)[it.box].width - stretched) > 1e-9) {
                    block->relayout_at_size(it.box, stretched, (*tree)[it.box].height);
                }
            }
        }
        if (cross_pos < 0) cross_pos = 0;

        // Safe from here: placement creates nothing.
        Box& b = (*tree)[it.box];
        if (column) {
            b.x = left_inner + cross_pos + b.margin_left;
            b.y = top_inner + cursor + b.margin_top;
        } else {
            b.x = left_inner + cursor + b.margin_left;
            b.y = top_inner + cross_pos + b.margin_top;
        }
        cursor += it.main + it.main_margins + between;
    }

    // The container's content height: the line's cross size in a row, the sum
    // of the items in a column.
    if (!column) return line_cross;
    double bottom = 0;
    for (const Item& it : items) {
        const Box& b = (*tree)[it.box];
        bottom = std::max(bottom, b.y + b.height + b.margin_bottom - top_inner);
    }
    return bottom;
}

} // namespace weva
