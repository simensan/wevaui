#include "weva/css_properties.h"
#include "weva/multicol.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"

#include <algorithm>
#include <cmath>
#include <limits>
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
const int kId_direction = CssPropertyRegistry::instance().id_of("direction");
const int kId_position = CssPropertyRegistry::instance().id_of("position");
const int kId_break_inside = CssPropertyRegistry::instance().id_of("break-inside");
const int kId_break_before = CssPropertyRegistry::instance().id_of("break-before");
const int kId_column_rule_width = CssPropertyRegistry::instance().id_of("column-rule-width");

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

// CSS Fragmentation L3 §3: `break-inside: avoid` keeps a box in one column;
// `break-before: column` (or a forced value) starts it in a new one.
bool avoids_break_inside(const ComputedStyle* s) {
    const std::string_view v = get(s, kId_break_inside);
    return iequals(v, "avoid") || iequals(v, "avoid-column") || iequals(v, "avoid-page");
}

bool forces_break_before(const ComputedStyle* s) {
    const std::string_view v = get(s, kId_break_before);
    return iequals(v, "column") || iequals(v, "always") || iequals(v, "page") || iequals(v, "left") ||
           iequals(v, "right");
}

// A block whose inside the fragmenter walks: plain flow content. A flex,
// grid or table container, a nested multicol, a float, a positioned box or
// one that avoids breaks moves as a whole.
bool fragmentable_block(const Box& b) {
    if (b.kind != BoxKind::Block && b.kind != BoxKind::AnonymousBlock) return false;
    if (b.is_multicol || b.is_float()) return false;
    if (b.position == PositionType::Absolute || b.position == PositionType::Fixed) return false;
    if (b.display != DisplayKind::Block && b.display != DisplayKind::FlowRoot && b.display != DisplayKind::ListItem) return false;
    return !avoids_break_inside(b.style);
}

// The smallest piece a column set breaks between: a line box, or a block
// that moves as a whole. `top`/`bottom` are its border-box extent in the
// container's coordinates (every box's x/y is relative to its parent's
// origin, so a line's is its block's origin plus the line's own y).
struct Unit {
    BoxId box = kNoBox;
    BoxId group = kNoBox;   // the block whose line this is; kNoBox for a whole block
    double top = 0, bottom = 0;
    int line_index = 0, line_count = 0;   // within the group's lines
    bool force_break_before = false;
};

// A block whose children became units, and its origin in the container's
// coordinates, so its rect can be re-fitted to where its fragments went.
struct Group {
    BoxId box = kNoBox;
    double origin_y = 0;
};

// Walks a block's subtree in flow order. `origin_y` is the block's own top in
// the container's coordinates.
void collect_units(const BoxTree& tree, BoxId block, double origin_y, std::vector<Unit>* out,
                   std::vector<Group>* groups) {
    const Box& b = tree[block];
    std::vector<BoxId> lines;
    bool has_blocks = false;
    for (BoxId c : tree.children(block)) {
        const BoxKind k = tree[c].kind;
        if (k == BoxKind::Line) lines.push_back(c);
        else if (k == BoxKind::Block || k == BoxKind::AnonymousBlock) has_blocks = true;
    }
    if (!lines.empty()) {
        groups->push_back({block, origin_y});
        for (size_t i = 0; i < lines.size(); ++i) {
            const Box& l = tree[lines[i]];
            Unit u;
            u.box = lines[i];
            u.group = block;
            u.top = origin_y + l.y;
            u.bottom = u.top + l.height;
            u.line_index = static_cast<int>(i);
            u.line_count = static_cast<int>(lines.size());
            if (i == 0) u.force_break_before = forces_break_before(b.style);
            out->push_back(u);
        }
        return;
    }
    if (has_blocks) {
        groups->push_back({block, origin_y});
        for (BoxId c : tree.children(block)) {
            const Box& cb = tree[c];
            if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
            if (cb.position == PositionType::Absolute || cb.position == PositionType::Fixed) continue;
            const size_t before = out->size();
            if (fragmentable_block(cb)) collect_units(tree, c, origin_y + cb.y, out, groups);
            if (out->size() == before) {
                Unit u;
                u.box = c;
                u.top = origin_y + cb.y;
                u.bottom = u.top + cb.height;
                u.force_break_before = forces_break_before(cb.style);
                out->push_back(u);
            }
        }
        return;
    }
    // A leaf block: nothing inside to break between.
    Unit u;
    u.box = block;
    u.top = origin_y;
    u.bottom = origin_y + b.height;
    u.force_break_before = forces_break_before(b.style);
    out->push_back(u);
}

// Packs the units into columns of height `height`, in order: a unit that
// would end past the column's bottom starts the next column (the flow
// between them -- a margin -- is truncated at the break, CSS Fragmentation
// §5.1), and a unit that fits nowhere overflows the column it starts.
// Between a paragraph's lines a break needs two lines before it in the
// column (`orphans: 2`, the browser's initial value); Chrome balances with
// no regard to `widows`, so a three-line paragraph goes two lines and one.
// Returns the number of columns used; `column` and `start` (the flow
// position that maps to the column's top) per unit; and, when the columns
// ran out, the smallest amount by which a column fell short of taking the
// unit that started the next one -- what Blink grows the height by.
int pack(const std::vector<Unit>& units, double height, double set_top,
         std::vector<int>* column, std::vector<double>* start, double* shortage) {
    const size_t n = units.size();
    column->assign(n, 0);
    start->assign(n, set_top);
    *shortage = std::numeric_limits<double>::infinity();
    int col = 0;
    double s = set_top;
    size_t first_in_col = 0;
    size_t i = 0;
    while (i < n) {
        const Unit& u = units[i];
        const bool forced = u.force_break_before && i > first_in_col;
        const bool overflows = i > first_in_col && u.bottom - s > height + 1e-6;
        if (forced || overflows) {
            size_t k = i;
            if (!forced && u.group != kNoBox) {
                // Orphans: the group's lines before the break, in this column.
                size_t group_first = k;
                while (group_first > first_in_col && units[group_first - 1].group == u.group) --group_first;
                if (k - group_first < 2 && group_first > first_in_col) k = group_first;
            }
            if (k > first_in_col) {
                // How much taller this column had to be to keep the unit:
                // the smallest such amount over the set is what the height
                // grows by when the columns run out.
                if (overflows) *shortage = std::min(*shortage, u.bottom - s - height);
                ++col;
                s = units[k].top;
                first_in_col = k;
                i = k;
                (*column)[i] = col;
                (*start)[i] = s;
                ++i;
                continue;
            }
            // Nothing can break here: the unit stays and overflows.
        }
        (*column)[i] = col;
        (*start)[i] = s;
        ++i;
    }
    return col + 1;
}

// After the units moved, a block's rect is where its fragments are: the
// union of its children (which are relative to it), with its own padding
// and border around, and the children re-based on the new origin. A
// fragment that continues into the next column reaches that column's
// bottom (`extend_to`, in the container's coordinates; -inf when the
// group's content is all in one column). Deepest blocks first, so a parent
// sees its child's re-fitted rect.
void refit_groups(BoxTree* tree, const std::vector<Group>& groups, const std::vector<double>& extend_to) {
    for (size_t gi = groups.size(); gi-- > 0;) {
        const BoxId g = groups[gi].box;
        Box& b = (*tree)[g];
        double x0 = std::numeric_limits<double>::infinity(), y0 = x0, x1 = -x0, y1 = -x0;
        bool any = false;
        for (BoxId c : tree->children(g)) {
            const Box& cb = (*tree)[c];
            if (cb.kind != BoxKind::Line && cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
            if (cb.position == PositionType::Absolute || cb.position == PositionType::Fixed) continue;
            any = true;
            x0 = std::min(x0, cb.x);
            y0 = std::min(y0, cb.y);
            x1 = std::max(x1, cb.x + cb.width);
            y1 = std::max(y1, cb.y + cb.height);
        }
        if (!any) continue;
        if (extend_to[gi] > -std::numeric_limits<double>::infinity()) {
            y1 = std::max(y1, extend_to[gi] - groups[gi].origin_y - b.padding_bottom - b.border_bottom);
        }
        const double left = b.padding_left + b.border_left, top = b.padding_top + b.border_top;
        const double dx = x0 - left, dy = y0 - top;
        const double width = (x1 - x0) + left + b.padding_right + b.border_right;
        const double height = (y1 - y0) + top + b.padding_bottom + b.border_bottom;
        if (std::fabs(dx) < 1e-9 && std::fabs(dy) < 1e-9 && std::fabs(width - b.width) < 1e-9 &&
            std::fabs(height - b.height) < 1e-9)
            continue;
        b.x += dx;
        b.y += dy;
        b.width = width;
        b.height = height;
        for (BoxId c : tree->children(g)) {
            Box& cb = (*tree)[c];
            cb.x -= dx;
            cb.y -= dy;
        }
    }
}

} // namespace

double layout_multicol(BoxTree* tree, BoxId container, double content_width, double font_size,
                       const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    const ComputedStyle* style = (*tree)[container].style;
    (*tree)[container].column_sets.clear();

    // In multicol (unlike grid/flex), normal is one em.
    double gap = font_size;
    {
        const std::string_view raw = get(style, kId_column_gap);
        if (!raw.empty() && !iequals(raw, "normal")) {
            const ResolvedLength r =
                resolve_length(style, kId_column_gap, ctx, font_size, content_width);
            if (r.kind == LengthKind::Length) gap = std::max(0.0, r.pixels);
        }
    }

    // §3.4: when both properties are set, column-count is a maximum.
    // Otherwise use the count or the number of preferred widths that fit.
    // Clamp before integer conversion, including extremely large CSS values.
    int count = 0;
    const std::string_view count_raw = get(style, kId_column_count);
    if (!count_raw.empty() && !iequals(count_raw, "auto")) {
        const ResolvedLength r = resolve_length(style, kId_column_count, ctx, font_size,
                                                std::nullopt);
        if (r.kind == LengthKind::Length && r.pixels > 0)
            count = static_cast<int>(std::min(r.pixels, static_cast<double>(std::numeric_limits<int>::max())));
    }
    const std::string_view width_raw = get(style, kId_column_width);
    if (!width_raw.empty() && !iequals(width_raw, "auto")) {
        const ResolvedLength r =
            resolve_length(style, kId_column_width, ctx, font_size, content_width);
        if (r.kind == LengthKind::Length && r.pixels >= 0) {
            // A zero/subpixel preferred width is legal; Chrome uses the
            // specified minimum used width of one CSS pixel.
            const double preferred = std::max(1.0, r.pixels);
            const double fitting = std::floor((content_width + gap) / (preferred + gap));
            const int fit = fitting >= 1 ? static_cast<int>(std::min(fitting,
                static_cast<double>(std::numeric_limits<int>::max()))) : 1;
            count = count > 0 ? std::min(count, fit) : fit;
        }
    }
    if (count <= 0) count = 1;

    // Chrome allocates column widths in 1/64 CSS-pixel units, but rounds
    // each origin from the ideal stride. Rounding the stride first would
    // accumulate an error across columns. Split off the integral part to
    // avoid overflow when quantizing very large authored dimensions.
    const auto layout_floor = [](double value) {
        double whole = 0;
        const double fraction = std::modf(value, &whole);
        return whole + std::floor(fraction * 64) / 64;
    };
    const auto layout_round = [](double value) {
        double whole = 0;
        const double fraction = std::modf(value, &whole);
        return whole + std::round(fraction * 64) / 64;
    };
    const double stride = (content_width + gap) / static_cast<double>(count);
    const double column_width = layout_floor(std::max(0.0, stride - gap));
    {
        Box& cb = (*tree)[container];
        cb.column_count_used = count;
        cb.column_width_used = column_width;
        cb.column_gap_used = gap;
        cb.column_height_used = 0;
        // The rule's width, resolved here for paint: the border keywords or
        // a length; `medium` when unset.
        const std::string_view rule_width = get(style, kId_column_rule_width);
        double width = 3;
        if (iequals(rule_width, "thin")) width = 1;
        else if (iequals(rule_width, "thick")) width = 5;
        else if (!rule_width.empty() && !iequals(rule_width, "medium")) {
            const ResolvedLength r = resolve_length(style, kId_column_rule_width, ctx, font_size, std::nullopt);
            if (r.kind == LengthKind::Length) width = std::max(0.0, r.pixels);
        }
        cb.column_rule_width_used = width;
    }

    // Children are laid out at the column width first: fragmenting needs
    // their lines, and their lines depend on that width.
    std::vector<BoxId> children;
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        const PositionType pos = parse_position_type(get(cb.style, kId_position));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            block->layout_block(c, content_width, style);
            continue;
        }
        const bool spans = iequals(get(cb.style, "column-span"), "all");
        block->layout_block(c, spans ? content_width : column_width, style);
        children.push_back(c);
    }
    if (children.empty()) return 0;

    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;
    const bool rtl = iequals(get(style, kId_direction), "rtl");
    // Column order follows the container's inline base direction. Mirror
    // the allocated column box, preserving its fractional width remainder.
    const auto column_left = [&](int column) {
        const double offset = layout_round(static_cast<double>(column) * stride);
        return rtl ? content_width - column_width - offset : offset;
    };

    // One balanced set: the children of a run, flowed as one tall column and
    // cut into `count` columns the way Blink balances (measured against
    // Chrome, Tools/oracle cov-multicol and nine probes):
    //   - the height starts at the flow's height (trailing margin included)
    //     over the count, never below the tallest piece that cannot break;
    //   - the flow is packed at that height; while it needs more columns
    //     than the count, the height grows by the smallest amount a column
    //     fell short of taking the unit that overflowed it, and packs again;
    //   - the set is that height even where its columns end short of it.
    // Returns the set's height.
    const auto layout_columns = [&](const std::vector<BoxId>& run, double y_offset) -> double {
        if (run.empty()) return 0.0;
        const double set_top = top_inner + y_offset;
        // Stack the run as one flow in column 0.
        double cursor = set_top;
        for (BoxId c : run) {
            Box& b = (*tree)[c];
            b.x = left_inner + column_left(0) + resolve_block_inline_offset(b, column_width, rtl);
            b.y = cursor + b.margin_top;
            cursor += b.margin_top + b.height + b.margin_bottom;
        }
        const double total = std::max(0.0, cursor - set_top);
        std::vector<Unit> units;
        std::vector<Group> groups;
        for (BoxId c : run) {
            const Box& b = (*tree)[c];
            const size_t before = units.size();
            if (fragmentable_block(b)) collect_units(*tree, c, b.y, &units, &groups);
            if (units.size() == before) {
                Unit u;
                u.box = c;
                u.top = b.y;
                u.bottom = b.y + b.height;
                u.force_break_before = forces_break_before(b.style);
                units.push_back(u);
            }
        }
        if (units.empty()) return 0.0;

        // The tallest piece that cannot break: a whole block, or a
        // paragraph's first two lines (orphans).
        double tallest = 0;
        for (size_t i = 0; i < units.size(); ++i) {
            const Unit& u = units[i];
            if (u.group == kNoBox) {
                tallest = std::max(tallest, u.bottom - u.top);
            } else if (u.line_index == 0) {
                const double bottom = (u.line_count >= 2 && i + 1 < units.size() && units[i + 1].group == u.group)
                                          ? units[i + 1].bottom : u.bottom;
                tallest = std::max(tallest, bottom - u.top);
            }
        }
        double height = count > 1 ? std::max(total / count, tallest) : total;
        std::vector<int> column;
        std::vector<double> start;
        double shortage = 0;
        for (int iteration = 0; iteration < 64; ++iteration) {
            const int used = pack(units, height, set_top, &column, &start, &shortage);
            if (used <= count || !(shortage > 1e-6) || !std::isfinite(shortage)) break;
            height += shortage;
        }
        pack(units, height, set_top, &column, &start, &shortage);

        // Move each unit to its column: right by the column's offset, up by
        // the flow the earlier columns took. A line moves within its block; a
        // whole block moves within the container.
        std::vector<double> extend_to(groups.size(), -std::numeric_limits<double>::infinity());
        std::vector<int> group_min_column(groups.size(), std::numeric_limits<int>::max());
        std::vector<int> group_max_column(groups.size(), -1);
        for (size_t i = 0; i < units.size(); ++i) {
            const Unit& u = units[i];
            Box& b = (*tree)[u.box];
            b.x += column_left(column[i]) - column_left(0);
            b.y += set_top - start[i];
            if (u.group != kNoBox) {
                for (size_t gi = 0; gi < groups.size(); ++gi) {
                    if (groups[gi].box != u.group) continue;
                    group_min_column[gi] = std::min(group_min_column[gi], column[i]);
                    group_max_column[gi] = std::max(group_max_column[gi], column[i]);
                    break;
                }
            }
        }
        // A group split across columns reaches the bottom of every column
        // but its last: its earlier fragments run to the column's end.
        for (size_t gi = 0; gi < groups.size(); ++gi) {
            if (group_max_column[gi] > group_min_column[gi]) extend_to[gi] = set_top + height;
        }
        // A group of blocks (not lines) whose children span columns is
        // handled the same way through its children's own extents.
        refit_groups(tree, groups, extend_to);
        (*tree)[container].column_sets.emplace_back(set_top, height);
        (*tree)[container].column_height_used = std::max((*tree)[container].column_height_used, height);
        return height;
    };

    bool has_span = false;
    for (BoxId c : children) has_span |= iequals(get((*tree)[c].style, "column-span"), "all");
    if (!has_span) return layout_columns(children, 0);
    std::vector<BoxId> run;
    double y = 0;
    const double left = left_inner;
    const double top = top_inner;
    bool previous_was_span = false;
    double previous_bottom_margin = 0;
    for (BoxId c : children) {
        Box& b = (*tree)[c];
        if (!iequals(get(b.style, "column-span"), "all")) {
            run.push_back(c);
            previous_was_span = false;
            continue;
        }
        y += layout_columns(run, y);
        run.clear();
        if (previous_was_span)
            y += collapse_margins(previous_bottom_margin, b.margin_top) -
                 previous_bottom_margin - b.margin_top;
        b.x = left + resolve_block_inline_offset(b, content_width, rtl);
        b.y = top + y + b.margin_top;
        y += b.margin_top + b.height + b.margin_bottom;
        previous_was_span = true;
        previous_bottom_margin = b.margin_bottom;
    }
    return std::max(0.0, y + layout_columns(run, y));
}

} // namespace weva
