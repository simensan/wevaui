#pragma once
#include <cstdint>
#include <array>
#include <algorithm>
#include <vector>

namespace weva {
class Element;
class ComputedStyle;

// CSS 2.2 17.6.2.1. Values encode precedence, not the appearance used to
// paint inset/outset (which become ridge/groove only after conflict resolution).
enum class TableBorderStyle : uint8_t {
    None, Inset, Groove, Outset, Ridge, Dotted, Dashed, Solid, Double, Hidden
};
enum class TableBorderOrigin : uint8_t {
    Table, ColumnGroup, Column, RowGroup, Row, Cell
};
enum class TableBorderSide : uint8_t { Top, Right, Bottom, Left };

struct TableBorderCandidate {
    double width = 0;
    TableBorderStyle style = TableBorderStyle::None;
    TableBorderOrigin origin = TableBorderOrigin::Table;
    // Index of the source box and its authored side; paint reads the winning
    // color from that source, rather than averaging neighboring colors.
    int32_t source = -1;
    TableBorderSide side = TableBorderSide::Top;
    // Lower values win a same-origin tie. The grid collector supplies this in
    // top/start order, using the table's direction for the inline start edge.
    uint32_t order = 0;

    double used_width() const {
        return style == TableBorderStyle::None || style == TableBorderStyle::Hidden ? 0 : width;
    }
};

// Coordinates are relative to the owning table. Source identity is DOM-based:
// box indices from a scratch layout must never escape into a retained tree.
struct TableBorderSegment {
    double x = 0, y = 0, length = 0;
    double start_extension = 0, end_extension = 0;
    bool horizontal = true;
    TableBorderCandidate border;
    const Element* element = nullptr;
    const ComputedStyle* fallback_style = nullptr;
};

// No retained references or allocations: suitable for folding every candidate
// incident on a grid segment into one result shared by layout and paint.
inline TableBorderCandidate resolve_table_border(TableBorderCandidate a, TableBorderCandidate b) {
    const bool a_hidden = a.style == TableBorderStyle::Hidden;
    const bool b_hidden = b.style == TableBorderStyle::Hidden;
    if (a_hidden != b_hidden) return a_hidden ? a : b;
    const bool a_none = a.style == TableBorderStyle::None;
    const bool b_none = b.style == TableBorderStyle::None;
    if (a_none != b_none) return a_none ? b : a;
    // Width is immaterial when both candidates suppress the segment.
    if (!a_hidden && !a_none && a.width != b.width) return a.width > b.width ? a : b;
    if (a.style != b.style) return a.style > b.style ? a : b;
    if (a.origin != b.origin) return a.origin > b.origin ? a : b;
    if (a.order != b.order) return a.order < b.order ? a : b;
    return a;
}

// One entry per physical grid segment. A spanning cell removes interior edges,
// even if a row/column/table candidate was added before or after that cell.
// Indices follow physical top-to-bottom/left-to-right order; candidate.order
// separately carries the table-direction tie-break rank.
class TableBorderGrid {
public:
    using Sides = std::array<TableBorderCandidate, 4>; // top, right, bottom, left
    bool reset(int rows, int columns) {
        rows_ = columns_ = 0;
        horizontal_.clear();
        vertical_.clear();
        if (rows < 0 || columns < 0) return false;
        const size_t r = static_cast<size_t>(rows), c = static_cast<size_t>(columns);
        if (c && r + 1 > horizontal_.max_size() / c) return false;
        if (r && c + 1 > vertical_.max_size() / r) return false;
        horizontal_.resize((r + 1) * c);
        vertical_.resize(r * (c + 1));
        rows_ = rows;
        columns_ = columns;
        return true;
    }
    bool add_rectangle(int row, int column, int row_span, int column_span,
                       const Sides& sides, bool cell = false) {
        if (!valid_rectangle(row, column, row_span, column_span)) return false;
        const int end_row = row + row_span, end_column = column + column_span;
        for (int c = column; c < end_column; ++c) {
            merge(h(row, c), sides[0]);
            merge(h(end_row, c), sides[2]);
        }
        for (int r = row; r < end_row; ++r) {
            merge(v(r, column), sides[3]);
            merge(v(r, end_column), sides[1]);
        }
        if (cell) {
            for (int r = row + 1; r < end_row; ++r)
                for (int c = column; c < end_column; ++c) h(r, c).suppressed = true;
            for (int r = row; r < end_row; ++r)
                for (int c = column + 1; c < end_column; ++c) v(r, c).suppressed = true;
        }
        return true;
    }
    TableBorderCandidate horizontal(int row_line, int column) const {
        if (row_line < 0 || row_line > rows_ || column < 0 || column >= columns_) return {};
        return resolved(horizontal_[static_cast<size_t>(row_line) * columns_ + column]);
    }
    TableBorderCandidate vertical(int row, int column_line) const {
        if (row < 0 || row >= rows_ || column_line < 0 || column_line > columns_) return {};
        return resolved(vertical_[static_cast<size_t>(row) * (static_cast<size_t>(columns_) + 1) + column_line]);
    }
    // Layout uses the largest half-border on each side of a spanning cell;
    // paint still has every individual segment's winning source and width.
    std::array<double, 4> half_widths(int row, int column, int row_span, int column_span) const {
        std::array<double, 4> widths{};
        if (!valid_rectangle(row, column, row_span, column_span)) return widths;
        for (int c = column; c < column + column_span; ++c) {
            widths[0] = std::max(widths[0], horizontal(row, c).used_width() * 0.5);
            widths[2] = std::max(widths[2], horizontal(row + row_span, c).used_width() * 0.5);
        }
        for (int r = row; r < row + row_span; ++r) {
            widths[3] = std::max(widths[3], vertical(r, column).used_width() * 0.5);
            widths[1] = std::max(widths[1], vertical(r, column + column_span).used_width() * 0.5);
        }
        return widths;
    }
private:
    struct Segment { TableBorderCandidate winner; bool suppressed = false; };
    int rows_ = 0, columns_ = 0;
    std::vector<Segment> horizontal_, vertical_;
    bool valid_rectangle(int row, int column, int row_span, int column_span) const {
        return row >= 0 && row < rows_ && column >= 0 && column < columns_ &&
               row_span > 0 && row_span <= rows_ - row &&
               column_span > 0 && column_span <= columns_ - column;
    }
    Segment& h(int row, int column) {
        return horizontal_[static_cast<size_t>(row) * columns_ + column];
    }
    Segment& v(int row, int column) {
        return vertical_[static_cast<size_t>(row) * (static_cast<size_t>(columns_) + 1) + column];
    }
    static void merge(Segment& segment, TableBorderCandidate candidate) {
        segment.winner = resolve_table_border(segment.winner, candidate);
    }
    static TableBorderCandidate resolved(const Segment& segment) {
        return segment.suppressed ? TableBorderCandidate{} : segment.winner;
    }
};

} // namespace weva
