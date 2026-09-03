#include "weva/css_properties.h"
#include "weva/multicol.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"

#include <algorithm>
#include <cmath>
#include <vector>

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

// The same lookup by id. A property id is resolved once for the
// program below rather than hashed from its name on every call --
// sampling put ComputedStyle::get and CssPropertyRegistry::id_of
// together at a quarter of a layout pass, ahead of any layout
// algorithm. Safe because the registry keeps an id stable across
// re-registration, which is what its header promises it for.
std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

// Resolved at static-init. The registry is a function-local static,
// so it is constructed on first use and these cannot outrun it.
const int kId_column_count = CssPropertyRegistry::instance().id_of("column-count");
const int kId_column_gap = CssPropertyRegistry::instance().id_of("column-gap");
const int kId_column_width = CssPropertyRegistry::instance().id_of("column-width");
const int kId_position = CssPropertyRegistry::instance().id_of("position");


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

} // namespace

double layout_multicol(BoxTree* tree, BoxId container, double content_width,
                       const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    const ComputedStyle* style = (*tree)[container].style;
    const double font_size =
        (*tree)[container].font_size > 0 ? (*tree)[container].font_size : ctx.root_font_size_px;

    double gap = 0;
    {
        const std::string_view raw = get(style, kId_column_gap);
        if (!raw.empty() && !iequals(raw, "normal")) {
            const ResolvedLength r =
                resolve_length(style, "column-gap", ctx, font_size, content_width);
            if (r.kind == LengthKind::Length) gap = std::max(0.0, r.pixels);
        }
    }

    // §3.4: the used column count comes from `column-count` when it is set, and
    // otherwise from how many columns of `column-width` fit — the last column
    // needs no gap after it, hence the (available + gap) / (width + gap) form.
    int count = 0;
    const std::string_view count_raw = get(style, kId_column_count);
    if (!count_raw.empty() && !iequals(count_raw, "auto")) {
        const ResolvedLength r = resolve_length(style, "column-count", ctx, font_size,
                                                std::nullopt);
        if (r.kind == LengthKind::Length) count = static_cast<int>(r.pixels);
    }
    if (count <= 0) {
        const std::string_view width_raw = get(style, kId_column_width);
        if (!width_raw.empty() && !iequals(width_raw, "auto")) {
            const ResolvedLength r =
                resolve_length(style, "column-width", ctx, font_size, content_width);
            if (r.kind == LengthKind::Length && r.pixels > 0) {
                count = static_cast<int>(std::floor((content_width + gap) / (r.pixels + gap)));
            }
        }
    }
    if (count <= 0) count = 1;

    const double column_width =
        std::max(0.0, (content_width - gap * (count - 1)) / static_cast<double>(count));

    // Children are laid out at the column width first: balancing needs their
    // heights, and their heights depend on that width.
    std::vector<BoxId> children;
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        const PositionType pos = parse_position_type(get(cb.style, kId_position));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            block->layout_block(c, content_width, style);
            continue;
        }
        block->layout_block(c, column_width, style);
        children.push_back(c);
    }
    if (children.empty()) return 0;

    double total = 0;
    for (BoxId c : children) {
        const Box& b = (*tree)[c];
        total += b.margin_top + b.height + b.margin_bottom;
    }

    // §6.1 balancing: the SMALLEST column height that still fits the content in
    // `count` columns.
    //
    // Not `total / count`. That is the lower bound, not the answer, and with
    // whole children it is usually unreachable: five 10px children in two
    // columns have a lower bound of 25, but no column can be 25 tall, and
    // filling to 25 puts two children in the first column and three in the
    // second — where a browser puts three and two. Both are 30 tall, so the
    // distinction is invisible in the totals and visible in every child's x.
    //
    // Feasibility is monotone in the height -- a taller column never needs
    // MORE of them -- so the smallest feasible height is a binary search.
    //
    // It used to try the PREFIX SUMS in order, which quietly assumes the
    // answer is one of them. It need not be: three children of 54, 38 and 38
    // have prefix sums 54, 92 and 130, and the smallest height that fits them
    // in two columns is 76 -- a contiguous run that is not a prefix. Taking 92
    // instead packed two children into the first column where a browser puts
    // one, which is invisible in the total height and visible in every child's
    // x. The oracle's cov-multicol case caught it against both Chrome and the
    // reference.
    std::vector<double> outer(children.size());
    for (size_t i = 0; i < children.size(); ++i) {
        const Box& b = (*tree)[children[i]];
        outer[i] = b.margin_top + b.height + b.margin_bottom;
    }
    const auto columns_needed = [&](double height) {
        int used = 1;
        double filled = 0;
        for (double h : outer) {
            if (filled > 0 && filled + h > height + 1e-9) {
                ++used;
                filled = 0;
            }
            filled += h;
        }
        return used;
    };
    double target = total;
    {
        // A column is at least as tall as the tallest child, since nothing is
        // split; and `total` always fits, so it is a feasible upper bound.
        double low = 0;
        for (double h : outer) low = std::max(low, h);
        double high = total;
        for (int i = 0; i < 64 && high - low > 1e-6; ++i) {
            const double mid = 0.5 * (low + high);
            if (columns_needed(mid) <= count) high = mid;
            else low = mid;
        }
        target = columns_needed(low) <= count ? low : high;
    }

    // A child is never split, so one taller than the target takes a column to
    // itself and overflows. Real column layout fragments it instead; this is
    // the gap the header names.

    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;

    int column = 0;
    double column_top = 0;
    double tallest = 0;
    for (size_t i = 0; i < children.size(); ++i) {
        Box& b = (*tree)[children[i]];
        const double outer_height = b.margin_top + b.height + b.margin_bottom;
        // Move on when this child would overflow the balanced height — unless
        // the column is still empty, in which case it goes here and overflows,
        // or this is the last column and there is nowhere else to go.
        if (column_top > 0 && column < count - 1 && column_top + outer_height > target + 1e-9) {
            ++column;
            column_top = 0;
        }
        b.x = left_inner + static_cast<double>(column) * (column_width + gap) + b.margin_left;
        b.y = top_inner + column_top + b.margin_top;
        // A child laid out at the container's width before this pass would be
        // the wrong width in its column.
        column_top += outer_height;
        tallest = std::max(tallest, column_top);
    }
    return tallest;
}

} // namespace weva
