#include "weva/table_layout.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"
#include "weva/dom.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
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

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\n' ||
                          s.front() == '\r')) {
        s.remove_prefix(1);
    }
    while (!s.empty() &&
           (s.back() == ' ' || s.back() == '\t' || s.back() == '\n' || s.back() == '\r')) {
        s.remove_suffix(1);
    }
    return s;
}

bool is_collapsed(const Box& b) { return b.style && iequals(get(b.style, "visibility"), "collapse"); }

// An integer attribute such as colspan / rowspan / span, or `fallback` when
// absent or unparseable. A non-positive value is the fallback too, except
// that rowspan="0" is meaningful to the caller and is reported as 0.
int int_attribute(const Element* e, std::string_view name, int fallback, int max, bool zero_ok) {
    if (!e) return fallback;
    const std::string_view raw = trim(e->get_attribute(name));
    if (raw.empty()) return fallback;
    const std::string s(raw);
    char* end = nullptr;
    const long v = std::strtol(s.c_str(), &end, 10);
    if (end == s.c_str() || *end != '\0') return fallback;
    if (v == 0 && zero_ok) return 0;
    if (v < 1) return fallback;
    return v > max ? max : static_cast<int>(v);
}

// `span` for <col>/<colgroup>; the reference reads `colspan` as a fallback.
int span_attribute(const Element* e) {
    if (!e) return 1;
    if (!trim(e->get_attribute("span")).empty()) return int_attribute(e, "span", 1, 1000, false);
    return int_attribute(e, "colspan", 1, 1000, false);
}

double own_font_size(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    const Box& b = tree[id];
    const BoxId p = b.parent;
    return font_size_px(b.style, p == kNoBox ? nullptr : tree[p].style, ctx);
}

struct Placement {
    BoxId cell = kNoBox;
    int column = 0;
    int col_span = 1;
    int row_span = 1;
    double min_w = 0;
    double max_w = 0;
};

struct Row {
    BoxId box = kNoBox;
    BoxId group = kNoBox;   // the row group this row sits in, or kNoBox
    std::vector<Placement> cells;
    bool collapsed = false;
};

bool is_row_group(DisplayKind d) {
    return d == DisplayKind::TableRowGroup || d == DisplayKind::TableHeaderGroup ||
           d == DisplayKind::TableFooterGroup;
}

void add_rows_of_group(const BoxTree& tree, BoxId group, std::vector<Row>* rows) {
    for (BoxId c : tree.children(group)) {
        if (tree[c].kind == BoxKind::Block && tree[c].display == DisplayKind::TableRow) {
            Row r;
            r.box = c;
            r.group = group;
            rows->push_back(r);
        }
    }
}

// Rows in header → body (and direct rows) → footer order (CSS Tables L3
// §3.6); a group with `visibility: collapse` contributes none.
std::vector<Row> collect_rows(const BoxTree& tree, BoxId table) {
    std::vector<Row> rows;
    for (BoxId c : tree.children(table)) {
        const Box& b = tree[c];
        if (b.kind == BoxKind::Block && b.display == DisplayKind::TableHeaderGroup &&
            !is_collapsed(b)) {
            add_rows_of_group(tree, c, &rows);
        }
    }
    for (BoxId c : tree.children(table)) {
        const Box& b = tree[c];
        if (b.kind != BoxKind::Block) continue;
        if (b.display == DisplayKind::TableRowGroup) {
            if (!is_collapsed(b)) add_rows_of_group(tree, c, &rows);
        } else if (b.display == DisplayKind::TableRow) {
            Row r;
            r.box = c;
            rows.push_back(r);
        }
    }
    for (BoxId c : tree.children(table)) {
        const Box& b = tree[c];
        if (b.kind == BoxKind::Block && b.display == DisplayKind::TableFooterGroup &&
            !is_collapsed(b)) {
            add_rows_of_group(tree, c, &rows);
        }
    }
    return rows;
}

// §17.5.1 grid placement: each cell takes the first free slot in its row,
// spanning cells reserve their columns for the rows they cover. Returns the
// column count.
int place_cells(const BoxTree& tree, std::vector<Row>* rows) {
    std::vector<int> active;   // rows still reserved per column
    int col_count = 0;
    for (size_t r = 0; r < rows->size(); ++r) {
        Row& row = (*rows)[r];
        int col = 0;
        for (BoxId c : tree.children(row.box)) {
            const Box& b = tree[c];
            if (b.kind != BoxKind::Block || b.display != DisplayKind::TableCell) continue;
            while (col < static_cast<int>(active.size()) && active[col] > 0) ++col;
            Placement p;
            p.cell = c;
            p.column = col;
            p.col_span = int_attribute(b.element, "colspan", 1, 1000, false);
            const int remaining = static_cast<int>(rows->size() - r);
            int rs = int_attribute(b.element, "rowspan", 1, 65534, true);
            if (rs == 0) rs = remaining > 0 ? remaining : 1;
            if (remaining > 0 && rs > remaining) rs = remaining;
            p.row_span = rs;
            if (static_cast<int>(active.size()) < col + p.col_span) active.resize(col + p.col_span, 0);
            if (rs > 1) {
                for (int k = col; k < col + p.col_span; ++k) active[k] = std::max(active[k], rs);
            }
            col += p.col_span;
            col_count = std::max(col_count, col);
            row.cells.push_back(p);
        }
        for (int& a : active) {
            if (a > 0) --a;
        }
    }
    for (size_t c = 0; c < active.size(); ++c) {
        if (active[c] > 0) col_count = std::max(col_count, static_cast<int>(c) + 1);
    }
    return col_count;
}

double column_width_of(const BoxTree& tree, BoxId col, double basis, const LayoutContext& ctx) {
    const Box& b = tree[col];
    const double fs = own_font_size(tree, col, ctx);
    if (b.style) {
        const ResolvedLength r = resolve_length(b.style, "width", ctx, fs, basis);
        if (r.kind == LengthKind::Length && r.pixels > 0) return r.pixels;
    }
    if (!b.element) return 0;
    const std::string_view raw = trim(b.element->get_attribute("width"));
    if (raw.empty()) return 0;
    const std::string s(raw);
    char* end = nullptr;
    const double px = std::strtod(s.c_str(), &end);
    if (end != s.c_str() && *end == '\0' && px > 0) return px;
    return resolve_length_px(raw, 0, ctx, fs, basis);
}

void apply_hint(std::vector<double>* hints, int* col, int span, double width) {
    if (*col >= static_cast<int>(hints->size())) return;
    if (span < 1) span = 1;
    if (*col + span > static_cast<int>(hints->size())) span = static_cast<int>(hints->size()) - *col;
    if (width > 0) {
        const double share = width / span;
        for (int c = *col; c < *col + span; ++c) (*hints)[c] = std::max((*hints)[c], share);
    }
    *col += span;
}

// <col> / <colgroup> width hints, consumed in declaration order.
std::vector<double> column_hints(const BoxTree& tree, BoxId table, int col_count, double avail,
                                 const LayoutContext& ctx) {
    std::vector<double> hints(static_cast<size_t>(col_count), 0.0);
    int col = 0;
    for (BoxId c : tree.children(table)) {
        if (col >= col_count) break;
        const Box& b = tree[c];
        if (b.kind != BoxKind::Block || !b.element) continue;
        if (b.display == DisplayKind::TableColumn) {
            apply_hint(&hints, &col, span_attribute(b.element), column_width_of(tree, c, avail, ctx));
        } else if (b.display == DisplayKind::TableColumnGroup) {
            const int before = col;
            for (BoxId k : tree.children(c)) {
                if (col >= col_count) break;
                const Box& kb = tree[k];
                if (kb.kind != BoxKind::Block || !kb.element ||
                    kb.display != DisplayKind::TableColumn) {
                    continue;
                }
                apply_hint(&hints, &col, span_attribute(kb.element),
                           column_width_of(tree, k, avail, ctx));
            }
            if (col == before) {
                apply_hint(&hints, &col, span_attribute(b.element),
                           column_width_of(tree, c, avail, ctx));
            }
        }
    }
    return hints;
}

// Which columns a `visibility: collapse` <col>/<colgroup> covers; empty when
// none does.
std::vector<bool> collapsed_columns(const BoxTree& tree, BoxId table, int col_count) {
    std::vector<bool> mask;
    const auto mark = [&](int from, int span) {
        if (mask.empty()) mask.assign(static_cast<size_t>(col_count), false);
        for (int c = from; c < from + span && c < col_count; ++c) mask[static_cast<size_t>(c)] = true;
    };
    int col = 0;
    for (BoxId c : tree.children(table)) {
        if (col >= col_count) break;
        const Box& b = tree[c];
        if (b.kind != BoxKind::Block || !b.element) continue;
        if (b.display == DisplayKind::TableColumn) {
            const int span = span_attribute(b.element);
            if (is_collapsed(b)) mark(col, span);
            col += span;
        } else if (b.display == DisplayKind::TableColumnGroup) {
            const bool group_collapsed = is_collapsed(b);
            int inner = 0;
            for (BoxId k : tree.children(c)) {
                if (col >= col_count) break;
                const Box& kb = tree[k];
                if (kb.kind != BoxKind::Block || !kb.element ||
                    kb.display != DisplayKind::TableColumn) {
                    continue;
                }
                const int span = span_attribute(kb.element);
                if (group_collapsed || is_collapsed(kb)) mark(col, span);
                col += span;
                ++inner;
            }
            if (inner == 0) {
                const int span = span_attribute(b.element);
                if (group_collapsed) mark(col, span);
                col += span;
            }
        }
    }
    return mask;
}

// A cell's authored width as an OUTER (border-box) width, or 0. Cells are
// content-box unless told otherwise, so the frame is added (the reference
// records leaderboard's fixed columns coming out 32px narrow without it).
double explicit_outer_width(const BoxTree& tree, BoxId cell, double basis, const LayoutContext& ctx) {
    const Box& b = tree[cell];
    if (!b.style) return 0;
    const double fs = own_font_size(tree, cell, ctx);
    const ResolvedLength r = resolve_length(b.style, "width", ctx, fs, basis);
    if (r.kind != LengthKind::Length || r.pixels <= 0) return 0;
    const double frame = b.padding_left + b.padding_right + b.border_left + b.border_right;
    return is_border_box(b.style) ? r.pixels : r.pixels + frame;
}

void distribute_spanned(double width, std::vector<double>* columns, int start, int span) {
    if (width <= 0 || columns->empty() || start < 0 || start >= static_cast<int>(columns->size())) {
        return;
    }
    int count = span < 1 ? 1 : span;
    if (start + count > static_cast<int>(columns->size())) {
        count = static_cast<int>(columns->size()) - start;
    }
    const double share = width / count;
    for (int c = start; c < start + count; ++c) (*columns)[c] = std::max((*columns)[c], share);
}

std::vector<double> resolve_auto(const std::vector<Row>& rows, int col_count, double avail,
                                 const std::vector<double>& hints) {
    std::vector<double> col_min(static_cast<size_t>(col_count), 0.0);
    std::vector<double> col_max(static_cast<size_t>(col_count), 0.0);
    for (const Row& row : rows) {
        for (const Placement& p : row.cells) {
            distribute_spanned(p.min_w, &col_min, p.column, p.col_span);
            distribute_spanned(p.max_w, &col_max, p.column, p.col_span);
        }
    }
    double sum_min = 0, sum_max = 0;
    for (int c = 0; c < col_count; ++c) {
        if (hints[c] > 0) {
            col_min[c] = std::max(col_min[c], hints[c]);
            col_max[c] = std::max(col_max[c], hints[c]);
        }
        col_max[c] = std::max(col_max[c], col_min[c]);
        sum_min += col_min[c];
        sum_max += col_max[c];
    }
    std::vector<double> widths(static_cast<size_t>(col_count), 0.0);
    if (sum_max <= avail || sum_max <= 0) {
        const double slack = avail - sum_max;
        if (slack > 0 && sum_max > 0) {
            for (int c = 0; c < col_count; ++c) widths[c] = col_max[c] + slack * (col_max[c] / sum_max);
        } else if (sum_max > 0) {
            for (int c = 0; c < col_count; ++c) widths[c] = col_max[c];
        } else {
            const double each = col_count > 0 ? avail / col_count : 0;
            for (int c = 0; c < col_count; ++c) widths[c] = each;
        }
    } else if (sum_min >= avail) {
        for (int c = 0; c < col_count; ++c) widths[c] = col_min[c];
    } else {
        const double slack = avail - sum_min;
        const double range_sum = sum_max - sum_min;
        for (int c = 0; c < col_count; ++c) {
            const double range = col_max[c] - col_min[c];
            widths[c] = col_min[c] + (range_sum > 0 ? slack * (range / range_sum) : 0);
        }
    }
    return widths;
}

std::vector<double> resolve_fixed(const BoxTree& tree, const std::vector<Row>& rows, int col_count,
                                  double avail, const std::vector<double>& hints,
                                  const LayoutContext& ctx) {
    std::vector<double> widths(static_cast<size_t>(col_count), 0.0);
    std::vector<bool> set(static_cast<size_t>(col_count), false);
    for (int c = 0; c < col_count; ++c) {
        if (hints[c] > 0) {
            widths[c] = hints[c];
            set[c] = true;
        }
    }
    if (!rows.empty()) {
        for (const Placement& p : rows.front().cells) {
            const double w = explicit_outer_width(tree, p.cell, avail, ctx);
            if (w <= 0) continue;
            if (p.column < 0 || p.column >= col_count) continue;
            int span = p.col_span < 1 ? 1 : p.col_span;
            if (p.column + span > col_count) span = col_count - p.column;
            const double share = w / span;
            for (int c = p.column; c < p.column + span; ++c) {
                if (set[c]) continue;
                widths[c] = share;
                set[c] = true;
            }
        }
    }
    double used = 0;
    int unset = 0;
    for (int c = 0; c < col_count; ++c) {
        if (set[c]) used += widths[c];
        else ++unset;
    }
    const double remaining = std::max(0.0, avail - used);
    const double each = unset > 0 ? remaining / unset : 0;
    for (int c = 0; c < col_count; ++c) {
        if (!set[c]) widths[c] = each;
    }
    return widths;
}

double sum_columns(const std::vector<double>& widths, int start, int span, double spacing) {
    if (widths.empty() || start < 0 || start >= static_cast<int>(widths.size())) return 0;
    int count = span < 1 ? 1 : span;
    if (start + count > static_cast<int>(widths.size())) count = static_cast<int>(widths.size()) - start;
    double w = 0;
    for (int c = start; c < start + count; ++c) {
        w += widths[c];
        if (c > start) w += spacing;
    }
    return w;
}

double sum_rows(const std::vector<double>& heights, int start, int span, double spacing) {
    if (heights.empty() || start < 0 || start >= static_cast<int>(heights.size())) return 0;
    int count = span < 1 ? 1 : span;
    if (start + count > static_cast<int>(heights.size())) count = static_cast<int>(heights.size()) - start;
    double h = 0;
    for (int r = start; r < start + count; ++r) {
        h += heights[r];
        if (r > start) h += spacing;
    }
    return h;
}

std::string_view caption_side(const Box& cap) {
    const std::string_view raw = trim(get(cap.style, "caption-side"));
    if (iequals(raw, "bottom") || iequals(raw, "block-end")) return "bottom";
    return "top";
}

} // namespace

double layout_table(BoxTree* tree, BoxId table, double content_width, const LayoutContext& ctx,
                    BlockLayout* block) {
    const ComputedStyle* style = (*tree)[table].style;
    const double fs = own_font_size(*tree, table, ctx);
    const double left_inner = (*tree)[table].padding_left + (*tree)[table].border_left;
    const double top_inner = (*tree)[table].padding_top + (*tree)[table].border_top;
    const double content_w = std::max(0.0, content_width);

    // ---- border-spacing (§17.6.1): initial 0, the UA sheet's 2px for
    // <table>, nothing under border-collapse: collapse ---------------------
    double spacing_x = 0, spacing_y = 0;
    if (style && !iequals(get(style, "border-collapse"), "collapse")) {
        const std::string_view raw = trim(get(style, "border-spacing"));
        if (!raw.empty()) {
            const size_t sp = raw.find(' ');
            if (sp == std::string_view::npos) {
                spacing_x = spacing_y = resolve_length_px(raw, 0, ctx, fs, std::nullopt);
            } else {
                spacing_x = resolve_length_px(trim(raw.substr(0, sp)), 0, ctx, fs, std::nullopt);
                spacing_y = resolve_length_px(trim(raw.substr(sp + 1)), 0, ctx, fs, std::nullopt);
            }
        }
    }

    // ---- captions and rows -----------------------------------------------
    std::vector<BoxId> captions;
    for (BoxId c : tree->children(table)) {
        if ((*tree)[c].kind == BoxKind::Block && (*tree)[c].display == DisplayKind::TableCaption) {
            captions.push_back(c);
        }
    }
    for (BoxId cap : captions) block->layout_block(cap, content_w, style);

    std::vector<Row> rows = collect_rows(*tree, table);
    const int col_count = place_cells(*tree, &rows);

    // Every cell is laid out once at the table's content width. That first
    // pass is also what the reference's automatic layout reads as a cell's
    // max-content: the laid-out width (the full content width for an auto
    // cell, the authored width for a sized one) — and its min-width, if a
    // length, as its min-content.
    for (Row& row : rows) {
        for (Placement& p : row.cells) {
            block->layout_block(p.cell, content_w, (*tree)[row.box].style);
            const double outer = explicit_outer_width(*tree, p.cell, content_w, ctx);
            if (outer > 0) {
                p.min_w = p.max_w = outer;
            } else {
                const Box& cb = (*tree)[p.cell];
                p.max_w = cb.width;
                if (p.max_w <= 0) {
                    for (BoxId k : tree->children(p.cell)) p.max_w = std::max(p.max_w, (*tree)[k].width);
                }
                p.min_w = 0;
                if (cb.style) {
                    const ResolvedLength mn =
                        resolve_length(cb.style, "min-width", ctx, own_font_size(*tree, p.cell, ctx),
                                       std::nullopt);
                    if (mn.kind == LengthKind::Length) p.min_w = mn.pixels;
                }
            }
        }
    }

    // ---- columns -----------------------------------------------------------
    std::vector<double> widths, offsets;
    if (col_count > 0) {
        const double avail = std::max(0.0, content_w - spacing_x * (col_count + 1));
        const std::vector<double> hints = column_hints(*tree, table, col_count, avail, ctx);
        const bool fixed = style && iequals(trim(get(style, "table-layout")), "fixed") && content_w > 0;
        widths = fixed ? resolve_fixed(*tree, rows, col_count, avail, hints, ctx)
                       : resolve_auto(rows, col_count, avail, hints);
        const std::vector<bool> collapsed = collapsed_columns(*tree, table, col_count);
        offsets.assign(static_cast<size_t>(col_count), 0.0);
        double cursor = spacing_x;
        for (int c = 0; c < col_count; ++c) {
            if (!collapsed.empty() && collapsed[c]) widths[c] = 0;
            offsets[c] = cursor;
            // A collapsed track gives up its slot AND its trailing spacing
            // (CSS Tables L3 §11.5); the survivors compact leftward.
            if (collapsed.empty() || !collapsed[c]) cursor += widths[c] + spacing_x;
        }
    }

    // ---- rows: re-lay every cell at its column width, measure -------------
    const int row_count = static_cast<int>(rows.size());
    std::vector<double> row_heights(static_cast<size_t>(row_count), 0.0);
    std::vector<double> row_floors(static_cast<size_t>(row_count), 0.0);
    struct SpanMeasure {
        int row, span;
        double natural;
    };
    std::vector<SpanMeasure> spanning;
    for (int r = 0; r < row_count; ++r) {
        Row& row = rows[r];
        row.collapsed = is_collapsed((*tree)[row.box]);
        if (row.collapsed) continue;
        if ((*tree)[row.box].style) {
            const ResolvedLength h = resolve_length((*tree)[row.box].style, "height", ctx,
                                                    own_font_size(*tree, row.box, ctx), std::nullopt);
            if (h.kind == LengthKind::Length) row_floors[r] = h.pixels;
        }
        row_heights[r] = row_floors[r];
        for (const Placement& p : row.cells) {
            const double col_w = sum_columns(widths, p.column, p.col_span, spacing_x);
            if (std::fabs((*tree)[p.cell].width - col_w) > 1e-9) block->relayout_at(p.cell, col_w);
            const double natural = (*tree)[p.cell].height;
            int span = p.row_span;
            if (r + span > row_count) span = row_count - r;
            if (span <= 1) {
                row_heights[r] = std::max(row_heights[r], natural);
            } else {
                spanning.push_back({r, span, natural});
            }
        }
    }
    for (const SpanMeasure& s : spanning) {
        const double covered = sum_rows(row_heights, s.row, s.span, spacing_y);
        if (s.natural <= covered) continue;
        const double extra = (s.natural - covered) / s.span;
        for (int r = s.row; r < s.row + s.span && r < row_count; ++r) {
            if (!rows[r].collapsed) row_heights[r] += extra;
        }
    }
    for (int r = 0; r < row_count; ++r) {
        if (rows[r].collapsed) row_heights[r] = 0;
        else row_heights[r] = std::max(row_heights[r], row_floors[r]);
    }

    // ---- placement ---------------------------------------------------------
    double cursor_y = top_inner + spacing_y;
    for (BoxId cap : captions) {
        if (caption_side((*tree)[cap]) == "bottom") continue;
        Box& cb = (*tree)[cap];
        cb.x = left_inner;
        cb.y = cursor_y;
        cb.width = content_w;
        cursor_y += cb.height;
    }

    BoxId current_group = kNoBox;
    double group_start = cursor_y;
    const auto close_group = [&](BoxId g) {
        if (g == kNoBox) return;
        Box& gb = (*tree)[g];
        gb.x = left_inner;
        gb.y = group_start;
        gb.width = content_w;
        gb.height = cursor_y - group_start;
    };
    for (int r = 0; r < row_count; ++r) {
        Row& row = rows[r];
        if (row.group != current_group) {
            close_group(current_group);
            current_group = row.group;
            group_start = cursor_y;
        }
        Box& rb = (*tree)[row.box];
        rb.x = current_group != kNoBox ? 0 : left_inner;
        rb.y = current_group != kNoBox ? cursor_y - group_start : cursor_y;
        rb.width = content_w;

        double max_cell_h = row_heights[r];
        for (const Placement& p : row.cells) {
            const double col_w = sum_columns(widths, p.column, p.col_span, spacing_x);
            Box& cb = (*tree)[p.cell];
            cb.x = p.column < static_cast<int>(offsets.size()) ? offsets[p.column] : 0;
            cb.y = 0;
            if (std::fabs(cb.width - col_w) > 1e-9) block->relayout_at(p.cell, col_w);
            if (!row.collapsed && p.row_span <= 1) max_cell_h = std::max(max_cell_h, (*tree)[p.cell].height);
        }
        if (row.collapsed) max_cell_h = 0;

        // §17.5.3: cells stretch to the row; `vertical-align: middle |
        // bottom` moves the content down by half / all of the slack.
        // `baseline` is `top` here, as in the reference.
        for (const Placement& p : row.cells) {
            int span = p.row_span;
            if (r + span > row_count) span = row_count - r;
            const double target = span > 1 ? sum_rows(row_heights, r, span, spacing_y) : max_cell_h;
            Box& cb = (*tree)[p.cell];
            const double slack = target - cb.height;
            if (slack > 0) {
                const std::string_view va = trim(get(cb.style, "vertical-align"));
                double factor = 0;
                if (iequals(va, "middle")) factor = 0.5;
                else if (iequals(va, "bottom")) factor = 1.0;
                if (factor > 0) {
                    const double shift = slack * factor;
                    for (BoxId k : tree->children(p.cell)) (*tree)[k].y += shift;
                }
            }
            cb.height = target;
        }
        (*tree)[row.box].height = max_cell_h;
        cursor_y += max_cell_h;
        if (!row.collapsed) cursor_y += spacing_y;
    }
    close_group(current_group);

    // A collapsed row group keeps no rectangle.
    for (BoxId c : tree->children(table)) {
        Box& b = (*tree)[c];
        if (b.kind != BoxKind::Block || !is_row_group(b.display) || !is_collapsed(b)) continue;
        b.x = left_inner;
        b.y = cursor_y;
        b.width = 0;
        b.height = 0;
    }

    for (BoxId cap : captions) {
        if (caption_side((*tree)[cap]) != "bottom") continue;
        Box& cb = (*tree)[cap];
        cb.x = left_inner;
        cb.y = cursor_y;
        cb.width = content_w;
        cursor_y += cb.height;
    }

    return cursor_y - top_inner;
}

} // namespace weva
