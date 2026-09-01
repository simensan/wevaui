#include "weva/grid.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"
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
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

// One track of the explicit grid. `fr` and `auto` are resolved after the fixed
// tracks have taken their space.
struct Track {
    enum class Kind { Fixed, Fraction, Auto } kind = Kind::Auto;
    double value = 0;   // pixels for Fixed, the flex factor for Fraction
    double size = 0;    // resolved
    double position = 0;
};

// Splits a value list on top-level whitespace, keeping a function call and its
// parentheses together so `repeat(3, 1fr)` survives as one token.
std::vector<std::string_view> split_tracks(std::string_view raw) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = std::string_view::npos;
    for (size_t i = 0; i < raw.size(); ++i) {
        const char c = raw[i];
        if (c == '(') ++depth;
        else if (c == ')') --depth;
        const bool space = depth == 0 && (c == ' ' || c == '\t' || c == '\n' || c == '\r');
        if (space) {
            if (start != std::string_view::npos) {
                out.push_back(raw.substr(start, i - start));
                start = std::string_view::npos;
            }
        } else if (start == std::string_view::npos) {
            start = i;
        }
    }
    if (start != std::string_view::npos) out.push_back(raw.substr(start));
    return out;
}

bool parse_track(std::string_view text, const LayoutContext& ctx, double font_size, double basis,
                 Track* out) {
    if (text.empty() || iequals(text, "auto")) {
        *out = {Track::Kind::Auto, 0, 0, 0};
        return true;
    }
    if (text.size() > 2 && text.substr(text.size() - 2) == "fr") {
        const std::string number(text.substr(0, text.size() - 2));
        *out = {Track::Kind::Fraction, std::strtod(number.c_str(), nullptr), 0, 0};
        return true;
    }
    const ResolvedLength r = resolve_length(text, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) {
        *out = {Track::Kind::Fixed, r.pixels, 0, 0};
        return true;
    }
    if (r.kind == LengthKind::Percent) {
        *out = {Track::Kind::Fixed, basis * r.percent * 0.01, 0, 0};
        return true;
    }
    // Anything this cannot read — minmax(), fit-content(), min-content —
    // becomes an auto track. Visibly the wrong size rather than subtly so.
    *out = {Track::Kind::Auto, 0, 0, 0};
    return true;
}

std::vector<Track> parse_track_list(std::string_view raw, const LayoutContext& ctx,
                                    double font_size, double basis) {
    std::vector<Track> tracks;
    if (raw.empty() || iequals(raw, "none")) return tracks;
    for (std::string_view token : split_tracks(raw)) {
        // repeat(<count>, <track>): the only repeat form in the corpus. The
        // auto-fill and auto-fit counts need the container's size and the
        // items' sizes, which is a different algorithm.
        if (token.size() > 7 && iequals(token.substr(0, 7), "repeat(") &&
            token.back() == ')') {
            const std::string_view inner = token.substr(7, token.size() - 8);
            const size_t comma = inner.find(',');
            if (comma == std::string_view::npos) continue;
            const std::string count_text(inner.substr(0, comma));
            const int count = std::atoi(count_text.c_str());
            std::string_view body = inner.substr(comma + 1);
            while (!body.empty() && (body.front() == ' ' || body.front() == '\t')) {
                body.remove_prefix(1);
            }
            for (int i = 0; i < count && i < 1024; ++i) {
                for (std::string_view sub : split_tracks(body)) {
                    Track t;
                    if (parse_track(sub, ctx, font_size, basis, &t)) tracks.push_back(t);
                }
            }
            continue;
        }
        Track t;
        if (parse_track(token, ctx, font_size, basis, &t)) tracks.push_back(t);
    }
    return tracks;
}

// `grid-template-areas: "a a b" "c d b"` — one quoted string per row, each
// naming the area occupying every cell of that row.
std::vector<std::vector<std::string>> parse_areas(std::string_view raw) {
    std::vector<std::vector<std::string>> rows;
    size_t i = 0;
    while (i < raw.size()) {
        if (raw[i] != '"' && raw[i] != '\'') { ++i; continue; }
        const char quote = raw[i];
        const size_t end = raw.find(quote, i + 1);
        if (end == std::string_view::npos) break;
        const std::string_view row = raw.substr(i + 1, end - i - 1);
        std::vector<std::string> names;
        size_t start = std::string_view::npos;
        for (size_t k = 0; k <= row.size(); ++k) {
            const bool space = k == row.size() || row[k] == ' ' || row[k] == '\t';
            if (space) {
                if (start != std::string_view::npos) {
                    names.emplace_back(row.substr(start, k - start));
                    start = std::string_view::npos;
                }
            } else if (start == std::string_view::npos) {
                start = k;
            }
        }
        rows.push_back(std::move(names));
        i = end + 1;
    }
    return rows;
}

// A scroll container's automatic minimum size is zero (CSS Grid L1 §6.6, the
// same rule Flexbox §4.5 states), so it does not force the track it sits in to
// grow to its content — it scrolls instead. Without this a 552px row whose item
// held 604px of content came out 604 tall and overflowed its own grid.
bool clips_overflow(const ComputedStyle* style) {
    const std::string_view x = get(style, "overflow-x");
    if (!x.empty() && !iequals(x, "visible")) return true;
    const std::string_view y = get(style, "overflow-y");
    return !y.empty() && !iequals(y, "visible");
}

struct Placement {
    BoxId box = kNoBox;
    int column = 0, row = 0;
    int column_span = 1, row_span = 1;
};

// The rectangle a named area occupies, or false when the name is absent.
bool area_rect(const std::vector<std::vector<std::string>>& areas, const std::string& name,
               int* out_col, int* out_row, int* out_col_span, int* out_row_span) {
    int min_r = -1, max_r = -1, min_c = -1, max_c = -1;
    for (int r = 0; r < static_cast<int>(areas.size()); ++r) {
        for (int c = 0; c < static_cast<int>(areas[r].size()); ++c) {
            if (areas[r][c] != name) continue;
            if (min_r < 0 || r < min_r) min_r = r;
            if (r > max_r) max_r = r;
            if (min_c < 0 || c < min_c) min_c = c;
            if (c > max_c) max_c = c;
        }
    }
    if (min_r < 0) return false;
    *out_col = min_c;
    *out_row = min_r;
    *out_col_span = max_c - min_c + 1;
    *out_row_span = max_r - min_r + 1;
    return true;
}

void resolve_tracks(std::vector<Track>* tracks, double available, double gap) {
    if (tracks->empty()) return;
    const double total_gap = gap * static_cast<double>(tracks->size() - 1);
    double fixed = total_gap;
    double fraction_total = 0;
    for (const Track& t : *tracks) {
        if (t.kind == Track::Kind::Fixed) fixed += t.value;
        else if (t.kind == Track::Kind::Fraction) fraction_total += t.value;
        else fixed += t.size;   // an auto track sized from its content already
    }
    const double free_space = available >= 0 ? std::max(0.0, available - fixed) : 0;
    for (Track& t : *tracks) {
        if (t.kind == Track::Kind::Fixed) t.size = t.value;
        else if (t.kind == Track::Kind::Fraction) {
            t.size = fraction_total > 0 ? free_space * (t.value / fraction_total) : 0;
        }
    }
    // CSS Box Alignment §5.3: `align-content` / `justify-content` default to
    // `normal`, which for a grid container behaves as `stretch` — leftover space
    // goes to the AUTO tracks rather than being left as a gap at the end.
    // Without it a single auto column in an 800px container came out at its
    // max-content width, and a single auto row in a 600px-tall container
    // stopped at its content height.
    //
    // Only when nothing is flexible: an `fr` track has already absorbed the
    // free space and there is none left to stretch with.
    if (available >= 0 && fraction_total <= 0) {
        int auto_count = 0;
        for (const Track& t : *tracks) {
            if (t.kind == Track::Kind::Auto) ++auto_count;
        }
        if (auto_count > 0 && free_space > 0) {
            const double share = free_space / auto_count;
            for (Track& t : *tracks) {
                if (t.kind == Track::Kind::Auto) t.size += share;
            }
        }
    }
    double pos = 0;
    for (Track& t : *tracks) {
        t.position = pos;
        pos += t.size + gap;
    }
}

double span_size(const std::vector<Track>& tracks, int start, int span, double gap) {
    double total = 0;
    for (int i = start; i < start + span && i < static_cast<int>(tracks.size()); ++i) {
        total += tracks[i].size;
        if (i > start) total += gap;
    }
    return total;
}

} // namespace

double layout_grid(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    const ComputedStyle* style = (*tree)[container].style;
    const double font_size =
        (*tree)[container].font_size > 0 ? (*tree)[container].font_size : ctx.root_font_size_px;

    const double column_gap = [&] {
        const std::string_view raw = get(style, "column-gap");
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, "column-gap", ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();
    const double row_gap = [&] {
        const std::string_view raw = get(style, "row-gap");
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, "row-gap", ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();

    std::vector<Track> columns =
        parse_track_list(get(style, "grid-template-columns"), ctx, font_size, content_width);
    std::vector<Track> rows =
        parse_track_list(get(style, "grid-template-rows"), ctx, font_size,
                         content_height >= 0 ? content_height : 0);
    const std::vector<std::vector<std::string>> areas =
        parse_areas(get(style, "grid-template-areas"));

    // A container with no explicit columns is one column wide, which is what
    // the initial `grid-template-columns: none` means for row-major flow.
    if (columns.empty()) columns.push_back({Track::Kind::Auto, 0, 0, 0});

    // ---- Collect and place the items ---------------------------------------
    std::vector<Placement> items;
    std::vector<BoxId> auto_placed;
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        const PositionType pos = parse_position_type(get(cb.style, "position"));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            block->layout_block(c, content_width, style);
            continue;
        }
        Placement p;
        p.box = c;
        const std::string_view area = get(cb.style, "grid-area");
        if (!area.empty() && !iequals(area, "auto") &&
            area_rect(areas, std::string(area), &p.column, &p.row, &p.column_span,
                      &p.row_span)) {
            items.push_back(p);
        } else {
            auto_placed.push_back(c);
        }
    }

    // Row-major auto-placement into the cells the named areas left free.
    {
        std::vector<std::vector<bool>> taken;
        const auto occupy = [&](int r, int c) {
            if (r < 0 || c < 0) return;
            if (static_cast<int>(taken.size()) <= r) taken.resize(r + 1);
            if (static_cast<int>(taken[r].size()) <= c) taken[r].resize(c + 1, false);
            taken[r][c] = true;
        };
        const auto is_taken = [&](int r, int c) {
            return r < static_cast<int>(taken.size()) &&
                   c < static_cast<int>(taken[r].size()) && taken[r][c];
        };
        for (const Placement& p : items) {
            for (int r = p.row; r < p.row + p.row_span; ++r) {
                for (int c = p.column; c < p.column + p.column_span; ++c) occupy(r, c);
            }
        }
        int cursor_row = 0, cursor_col = 0;
        for (BoxId box : auto_placed) {
            while (is_taken(cursor_row, cursor_col)) {
                if (++cursor_col >= static_cast<int>(columns.size())) {
                    cursor_col = 0;
                    ++cursor_row;
                }
            }
            Placement p;
            p.box = box;
            p.column = cursor_col;
            p.row = cursor_row;
            items.push_back(p);
            occupy(cursor_row, cursor_col);
            if (++cursor_col >= static_cast<int>(columns.size())) {
                cursor_col = 0;
                ++cursor_row;
            }
        }
    }

    // Implicit rows: a grid with more items than explicit rows grows. Every
    // implicit row is `auto`, since grid-auto-rows is not ported.
    int max_row = 0;
    for (const Placement& p : items) max_row = std::max(max_row, p.row + p.row_span);
    while (static_cast<int>(rows.size()) < max_row) rows.push_back({Track::Kind::Auto, 0, 0, 0});

    // ---- Size the columns, then lay the items out to size the rows ---------
    // Every item is laid out once first, whatever track it lands in. This is
    // what resolves its box model, and skipping it for items in FIXED tracks
    // left their padding, border and margin at zero — the children of a
    // 260px-wide sidebar were placed at x=0 rather than at its 16px padding.
    for (const Placement& p : items) {
        block->layout_block(p.box, content_width, style);
    }

    // An auto column takes the widest max-content of the items in it.
    for (size_t c = 0; c < columns.size(); ++c) {
        if (columns[c].kind != Track::Kind::Auto) continue;
        double widest = 0;
        for (const Placement& p : items) {
            if (p.column != static_cast<int>(c) || p.column_span != 1) continue;
            const Box& b = (*tree)[p.box];
            const double frame =
                b.padding_left + b.padding_right + b.border_left + b.border_right;
            widest = std::max(widest, max_content_width(*tree, p.box) + frame);
        }
        columns[c].size = widest;
    }
    resolve_tracks(&columns, content_width, column_gap);

    // Each item is laid out at its cell's width so its height is known; that
    // height then sizes any auto row.
    for (const Placement& p : items) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        if (std::fabs((*tree)[p.box].width - w) > 1e-9) block->relayout_at(p.box, w);
    }
    for (size_t r = 0; r < rows.size(); ++r) {
        if (rows[r].kind != Track::Kind::Auto) continue;
        double tallest = 0;
        for (const Placement& p : items) {
            if (p.row != static_cast<int>(r) || p.row_span != 1) continue;
            const Box& b = (*tree)[p.box];
            if (clips_overflow(b.style)) continue;
            tallest = std::max(tallest, b.height + b.margin_top + b.margin_bottom);
        }
        rows[r].size = tallest;
    }
    resolve_tracks(&rows, content_height, row_gap);

    // ---- Place ------------------------------------------------------------
    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;
    double bottom = 0;
    for (const Placement& p : items) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        const double h = span_size(rows, p.row, p.row_span, row_gap);
        // `stretch` is the initial placement in both axes, so an item fills its
        // cell unless it has a definite size of its own.
        const Box& before = (*tree)[p.box];
        const std::string_view height_raw = get(before.style, "height");
        const bool auto_height = height_raw.empty() || iequals(height_raw, "auto");
        if (auto_height && h > 0) {
            block->relayout_at_size(p.box, w, h);
        }
        Box& b = (*tree)[p.box];
        b.x = left_inner + (p.column < static_cast<int>(columns.size())
                                ? columns[p.column].position
                                : 0) +
              b.margin_left;
        b.y = top_inner +
              (p.row < static_cast<int>(rows.size()) ? rows[p.row].position : 0) + b.margin_top;
        bottom = std::max(bottom, b.y + b.height + b.margin_bottom - top_inner);
    }

    // The container's content height is the row track total when the rows are
    // definite, and the items' extent when they are not.
    if (!rows.empty()) {
        const Track& last = rows.back();
        bottom = std::max(bottom, last.position + last.size);
    }
    return bottom;
}

} // namespace weva
