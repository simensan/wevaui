#include "weva/grid.h"

#include "weva/block_layout.h"
#include "weva/computed_style.h"
#include "weva/inline_layout.h"

#include <algorithm>
#include <cmath>
#include <map>
#include <optional>
#include <string>
#include <utility>
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

// One side of a grid-placement property (CSS Grid L1 §8.3): `auto`, a line
// number (negative counts from the end of the explicit grid), or `span N`.
// Named lines are not ported and read as `auto`.
struct LineSpec {
    bool is_auto = true;
    bool is_span = false;
    int n = 0;
};

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\n')) {
        s.remove_prefix(1);
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\n')) {
        s.remove_suffix(1);
    }
    return s;
}

bool parse_int(std::string_view s, int* out) {
    s = trim(s);
    if (s.empty()) return false;
    size_t i = 0;
    bool neg = false;
    if (s[0] == '-' || s[0] == '+') { neg = s[0] == '-'; i = 1; }
    if (i >= s.size()) return false;
    long v = 0;
    for (; i < s.size(); ++i) {
        if (s[i] < '0' || s[i] > '9') return false;
        v = v * 10 + (s[i] - '0');
        if (v > 100000) return false;
    }
    *out = static_cast<int>(neg ? -v : v);
    return true;
}

LineSpec parse_line_spec(std::string_view raw) {
    LineSpec spec;
    raw = trim(raw);
    if (raw.empty() || iequals(raw, "auto")) return spec;
    // Split on whitespace: `span 2`, `2 span`, `3`.
    std::vector<std::string_view> words;
    size_t start = std::string_view::npos;
    for (size_t k = 0; k <= raw.size(); ++k) {
        const bool ws = k == raw.size() || raw[k] == ' ' || raw[k] == '\t';
        if (ws) {
            if (start != std::string_view::npos) {
                words.push_back(raw.substr(start, k - start));
                start = std::string_view::npos;
            }
        } else if (start == std::string_view::npos) {
            start = k;
        }
    }
    bool span = false;
    int n = 0;
    bool have_n = false;
    for (std::string_view w : words) {
        if (iequals(w, "span")) span = true;
        else if (parse_int(w, &n)) have_n = true;
        else return spec;   // a named line: not ported, behaves as auto
    }
    if (span) {
        spec.is_auto = false;
        spec.is_span = true;
        spec.n = have_n ? std::max(1, n) : 1;
        return spec;
    }
    if (!have_n || n == 0) return spec;
    spec.is_auto = false;
    spec.n = n;
    return spec;
}

// The two sides of one axis, read from the shorthand (`grid-column: 1 / 3`)
// when it is set and the longhands otherwise. The shorthands are stored raw
// rather than expanded, so a single value is start-only and the end is auto.
void read_axis(const ComputedStyle* style, std::string_view shorthand,
               std::string_view start_prop, std::string_view end_prop,
               LineSpec* start, LineSpec* end) {
    const std::string_view raw = trim(get(style, shorthand));
    if (!raw.empty() && !iequals(raw, "auto")) {
        const size_t slash = raw.find('/');
        if (slash == std::string_view::npos) {
            *start = parse_line_spec(raw);
            *end = LineSpec{};
        } else {
            *start = parse_line_spec(raw.substr(0, slash));
            *end = parse_line_spec(raw.substr(slash + 1));
        }
        return;
    }
    *start = parse_line_spec(get(style, start_prop));
    *end = parse_line_spec(get(style, end_prop));
}

// §8.3.1: a start line and an end line resolve to a track index and a span.
// `definite` is false when the axis still needs auto-placement, in which case
// only the span is meaningful.
struct AxisPlacement {
    bool definite = false;
    int start = 0;   // 0-based track index
    int span = 1;
};

AxisPlacement resolve_axis(const LineSpec& s, const LineSpec& e, int explicit_tracks) {
    // A negative line counts from the end of the explicit grid: -1 is its last
    // line, which is explicit_tracks + 1 in positive numbering.
    const auto to_line = [&](int n) { return n > 0 ? n : explicit_tracks + 2 + n; };
    AxisPlacement out;
    const bool s_line = !s.is_auto && !s.is_span;
    const bool e_line = !e.is_auto && !e.is_span;
    if (s_line && e_line) {
        int a = to_line(s.n), b = to_line(e.n);
        if (a == b) b = a + 1;
        if (b < a) std::swap(a, b);
        if (a < 1) a = 1;   // implicit tracks before the explicit grid: not ported
        out.definite = true;
        out.start = a - 1;
        out.span = b - a;
        return out;
    }
    if (s_line) {
        const int a = std::max(1, to_line(s.n));
        out.definite = true;
        out.start = a - 1;
        out.span = e.is_span ? e.n : 1;
        return out;
    }
    if (e_line) {
        const int span = s.is_span ? s.n : 1;
        int a = to_line(e.n) - span;
        if (a < 1) a = 1;
        out.definite = true;
        out.start = a - 1;
        out.span = span;
        return out;
    }
    out.span = s.is_span ? s.n : (e.is_span ? e.n : 1);
    return out;
}

void resolve_tracks(std::vector<Track>* tracks, double available, double gap,
                    std::string_view content_align) {
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
    // ...and only for those two keywords: `justify-content: start` on auto
    // columns leaves them at their content size, which is what the keyword is
    // for.
    const bool stretches = content_align.empty() || iequals(content_align, "normal") ||
                           iequals(content_align, "stretch");
    if (available >= 0 && fraction_total <= 0 && stretches) {
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

// CSS Box Alignment §6: an item's alignment in one axis is its own
// `*-self` value, falling back to the container's `*-items`; `auto` and
// `normal` mean `stretch` for a grid item. The place-* shorthands are
// expanded to these longhands by the cascade.
std::string_view self_alignment(const ComputedStyle* item, const ComputedStyle* container,
                                bool block_axis) {
    std::string_view v = get(item, block_axis ? "align-self" : "justify-self");
    if (v.empty() || iequals(v, "auto") || iequals(v, "normal")) {
        v = get(container, block_axis ? "align-items" : "justify-items");
    }
    // `legacy` is justify-items' initial value and behaves as normal.
    if (v.empty() || iequals(v, "auto") || iequals(v, "normal") || iequals(v, "legacy") ||
        iequals(v, "baseline")) {
        return "stretch";
    }
    if (iequals(v, "flex-start") || iequals(v, "self-start") || iequals(v, "left")) return "start";
    if (iequals(v, "flex-end") || iequals(v, "self-end") || iequals(v, "right")) return "end";
    return v;
}

bool is_stretch(std::string_view v) { return iequals(v, "stretch"); }

// The offset of an outer size within a cell for one alignment keyword.
double align_offset(std::string_view v, double cell, double outer) {
    if (iequals(v, "center")) return (cell - outer) * 0.5;
    if (iequals(v, "end")) return cell - outer;
    return 0;
}

// CSS Box Alignment §5: align-content / justify-content distribute the free
// space of a definite container between the tracks. `normal` and `stretch`
// have already given that space to the auto tracks in resolve_tracks; when
// none exist they fall through to `start`, which is why this only ever moves
// tracks for the other keywords.
void distribute_content(std::vector<Track>* tracks, double available, double gap,
                        std::string_view align) {
    if (available < 0 || tracks->empty()) return;
    const int n = static_cast<int>(tracks->size());
    double used = gap * static_cast<double>(n - 1);
    for (const Track& t : *tracks) used += t.size;
    const double free_space = available - used;
    if (free_space <= 0) return;
    double offset = 0, extra_gap = 0;
    if (iequals(align, "center")) {
        offset = free_space * 0.5;
    } else if (iequals(align, "end") || iequals(align, "flex-end")) {
        offset = free_space;
    } else if (iequals(align, "space-between")) {
        if (n < 2) return;
        extra_gap = free_space / static_cast<double>(n - 1);
    } else if (iequals(align, "space-around")) {
        extra_gap = free_space / static_cast<double>(n);
        offset = extra_gap * 0.5;
    } else if (iequals(align, "space-evenly")) {
        extra_gap = free_space / static_cast<double>(n + 1);
        offset = extra_gap;
    } else {
        return;
    }
    double pos = offset;
    for (Track& t : *tracks) {
        t.position = pos;
        pos += t.size + gap + extra_gap;
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
    // CSS Grid L1 §8.5, sparse `row` flow. Items are grouped by how much of
    // their position is definite: a named area or two line numbers fixes both
    // axes; `grid-column: 3` fixes one; the rest take the auto-placement
    // cursor. Before this, `grid-column` and `grid-row` were never read, so a
    // page shell's `grid-column: 3` sidebar landed in column 1.
    struct Pending {
        BoxId box = kNoBox;
        AxisPlacement col, row;
    };
    std::vector<Pending> pending;
    const int explicit_columns = static_cast<int>(columns.size());
    const int explicit_rows = static_cast<int>(rows.size());
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        const PositionType pos = parse_position_type(get(cb.style, "position"));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            block->layout_block(c, content_width, style);
            continue;
        }
        Pending p;
        p.box = c;
        const std::string_view area = trim(get(cb.style, "grid-area"));
        int col = 0, row = 0, col_span = 1, row_span = 1;
        if (!area.empty() && !iequals(area, "auto") && area.find('/') == std::string_view::npos &&
            area_rect(areas, std::string(area), &col, &row, &col_span, &row_span)) {
            p.col = {true, col, col_span};
            p.row = {true, row, row_span};
        } else {
            LineSpec cs, ce, rs, re;
            if (area.find('/') != std::string_view::npos) {
                // `grid-area: <row-start> / <column-start> / <row-end> / <column-end>`;
                // omitted trailing values are auto.
                std::vector<std::string_view> parts;
                size_t from = 0;
                while (true) {
                    const size_t slash = area.find('/', from);
                    parts.push_back(area.substr(from, slash == std::string_view::npos
                                                          ? std::string_view::npos
                                                          : slash - from));
                    if (slash == std::string_view::npos) break;
                    from = slash + 1;
                }
                rs = parse_line_spec(parts[0]);
                cs = parts.size() > 1 ? parse_line_spec(parts[1]) : LineSpec{};
                re = parts.size() > 2 ? parse_line_spec(parts[2]) : LineSpec{};
                ce = parts.size() > 3 ? parse_line_spec(parts[3]) : LineSpec{};
            } else {
                read_axis(cb.style, "grid-column", "grid-column-start", "grid-column-end",
                          &cs, &ce);
                read_axis(cb.style, "grid-row", "grid-row-start", "grid-row-end", &rs, &re);
            }
            p.col = resolve_axis(cs, ce, explicit_columns);
            p.row = resolve_axis(rs, re, explicit_rows);
        }
        pending.push_back(p);
    }

    std::vector<Placement> items;
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
        const auto area_free = [&](int r, int c, int rs, int cs) {
            for (int rr = r; rr < r + rs; ++rr) {
                for (int cc = c; cc < c + cs; ++cc) {
                    if (is_taken(rr, cc)) return false;
                }
            }
            return true;
        };
        const auto place = [&](const Pending& p, int r, int c) {
            Placement out;
            out.box = p.box;
            out.column = c;
            out.row = r;
            out.column_span = p.col.span;
            out.row_span = p.row.span;
            for (int rr = r; rr < r + out.row_span; ++rr) {
                for (int cc = c; cc < c + out.column_span; ++cc) occupy(rr, cc);
            }
            items.push_back(out);
        };

        // Step 1: anything definite in both axes.
        for (const Pending& p : pending) {
            if (p.col.definite && p.row.definite) place(p, p.row.start, p.col.start);
        }
        // Step 2: items locked to a row take the first free column in it that
        // is past anything this step already put there.
        std::map<int, int> row_cursor;
        for (const Pending& p : pending) {
            if (p.col.definite || !p.row.definite) continue;
            int c = row_cursor.count(p.row.start) ? row_cursor[p.row.start] : 0;
            while (!area_free(p.row.start, c, p.row.span, p.col.span)) ++c;
            place(p, p.row.start, c);
            row_cursor[p.row.start] = c + p.col.span;
        }
        // Step 3: the implicit grid's column count — the explicit tracks, plus
        // whatever definite placements or spans reach past them.
        int column_count = explicit_columns;
        for (const Placement& pl : items) {
            column_count = std::max(column_count, pl.column + pl.column_span);
        }
        for (const Pending& p : pending) {
            if (!p.col.definite && !p.row.definite) {
                column_count = std::max(column_count, p.col.span);
            }
        }
        // Step 4: the cursor walks the remaining items in row-major order.
        int cursor_row = 0, cursor_col = 0;
        for (const Pending& p : pending) {
            if (p.row.definite) continue;
            if (p.col.definite) {
                if (p.col.start < cursor_col) ++cursor_row;
                cursor_col = p.col.start;
                while (!area_free(cursor_row, cursor_col, p.row.span, p.col.span)) ++cursor_row;
                place(p, cursor_row, cursor_col);
                continue;
            }
            while (true) {
                if (cursor_col + p.col.span > column_count) {
                    cursor_col = 0;
                    ++cursor_row;
                    continue;
                }
                if (area_free(cursor_row, cursor_col, p.row.span, p.col.span)) break;
                ++cursor_col;
            }
            place(p, cursor_row, cursor_col);
            cursor_col += p.col.span;
            if (cursor_col >= column_count) {
                cursor_col = 0;
                ++cursor_row;
            }
        }
    }

    // Implicit columns: a placement past the explicit grid adds `auto` tracks,
    // since grid-auto-columns is not ported.
    int max_column = 0;
    for (const Placement& p : items) max_column = std::max(max_column, p.column + p.column_span);
    while (static_cast<int>(columns.size()) < max_column) {
        columns.push_back({Track::Kind::Auto, 0, 0, 0});
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
            double contribution = max_content_width(*tree, p.box, &ctx) + frame;
            // The item's own min-/max-width bound its contribution: a
            // `min-width: 200px` cell in an auto column makes the column 200
            // wide even when its text is narrower.
            const double item_fs = b.font_size > 0 ? b.font_size : font_size;
            const double minmax_frame = is_border_box(b.style) ? 0 : frame;
            const ResolvedLength min_w =
                resolve_length(b.style, "min-width", ctx, item_fs, content_width);
            if (min_w.kind == LengthKind::Length) {
                contribution = std::max(contribution, min_w.pixels + minmax_frame);
            }
            const ResolvedLength max_w =
                resolve_length(b.style, "max-width", ctx, item_fs, content_width);
            if (max_w.kind == LengthKind::Length) {
                contribution = std::min(contribution, max_w.pixels + minmax_frame);
            }
            widest = std::max(widest, contribution + b.margin_left + b.margin_right);
        }
        columns[c].size = widest;
    }

    resolve_tracks(&columns, content_width, column_gap, get(style, "justify-content"));
    distribute_content(&columns, content_width, column_gap, get(style, "justify-content"));

    // Each item takes its inline size from its cell: a stretched auto-width
    // item fills it, a `start`/`center`/`end` one fits its content inside it,
    // and an explicit width is kept — re-resolved against the cell, since the
    // grid AREA is the item's containing block — rather than overwritten. The
    // height that falls out then sizes any auto row.
    // css-align §6.1: `normal` behaves as stretch for a grid item — except one
    // with a preferred aspect ratio, which is stretched in ONE axis and takes
    // the other from the ratio. Chrome stretches the block axis when the row
    // is definite (a 1fr row in a sized grid: the cell's height, and the width
    // follows) and the inline axis otherwise (an auto row: the column's width,
    // and the height follows). Stretching both gave a square in an `80px 1fr`
    // card the height of the text beside it.
    const auto has_ratio = [&](const Box& b) {
        double ratio = 0;
        return b.style && try_resolve_aspect_ratio(b.style, &ratio) && ratio > 0;
    };
    const auto row_is_definite = [&](const Placement& p) {
        if (p.row >= static_cast<int>(rows.size())) return false;
        const Track& t = rows[p.row];
        if (t.kind == Track::Kind::Fixed) return true;
        return t.kind == Track::Kind::Fraction && content_height >= 0;
    };
    const auto size_inline = [&](const Placement& p) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        const Box& b = (*tree)[p.box];
        const std::string_view width_raw = get(b.style, "width");
        const bool auto_width = width_raw.empty() || iequals(width_raw, "auto");
        if (!auto_width) {
            block->layout_block(p.box, w, style);
            return;
        }
        const std::string_view height_raw = get(b.style, "height");
        const bool auto_height = height_raw.empty() || iequals(height_raw, "auto");
        if (auto_height && has_ratio(b) && row_is_definite(p) &&
            is_stretch(self_alignment(b.style, style, true))) {
            // The block axis will be stretched at placement; the width follows
            // from it there. Lay out at the cell width for now so the box
            // model is resolved.
            if (std::fabs(b.width - w) > 1e-9) block->relayout_at(p.box, w);
            return;
        }
        if (is_stretch(self_alignment(b.style, style, false))) {
            if (std::fabs(b.width - w) > 1e-9) block->relayout_at(p.box, w);
            return;
        }
        block->shrink_to_fit(p.box, w, style);
    };
    for (const Placement& p : items) size_inline(p);
    // An auto row's base size is its items' minimum contributions, and a
    // scroll container's automatic minimum is zero (§6.6) — so in a grid with
    // a DEFINITE height such an item does not force the row past the space
    // there is; the row takes its share of that height and the item scrolls.
    // A grid with an AUTO height is sized under a max-content constraint, and
    // there every auto track grows to its growth limit, which is the items'
    // max-content contribution, scroll container or not. Skipping the scroll
    // container in both cases left an `overflow: hidden` segmented control
    // out of its own row's height.
    for (size_t r = 0; r < rows.size(); ++r) {
        if (rows[r].kind != Track::Kind::Auto) continue;
        double base = 0, limit = 0;
        for (const Placement& p : items) {
            if (p.row != static_cast<int>(r) || p.row_span != 1) continue;
            const Box& b = (*tree)[p.box];
            const double outer = b.height + b.margin_top + b.margin_bottom;
            limit = std::max(limit, outer);
            if (!clips_overflow(b.style)) base = std::max(base, outer);
        }
        rows[r].size = content_height >= 0 ? base : limit;
    }
    // A container with no definite height is sized to its rows and then
    // clamped by its own min/max-height; the clamped size is definite, so the
    // rows distribute it (the same rule the flex container applies in §9.2).
    // `align-content: space-between; min-height: 150px` over two 40px rows
    // puts the second row at 110, not 40.
    double rows_available = content_height;
    if (rows_available < 0 && !rows.empty()) {
        double natural = row_gap * static_cast<double>(rows.size() - 1);
        for (const Track& t : rows) natural += t.kind == Track::Kind::Fixed ? t.value : t.size;
        const Box& cb = (*tree)[container];
        const double frame =
            cb.padding_top + cb.padding_bottom + cb.border_top + cb.border_bottom;
        const double own_frame = is_border_box(style) ? frame : 0;
        const ResolvedLength min_r =
            resolve_length(style, "min-height", ctx, font_size, std::nullopt);
        const ResolvedLength max_r =
            resolve_length(style, "max-height", ctx, font_size, std::nullopt);
        double clamped = natural;
        if (min_r.kind == LengthKind::Length) {
            clamped = std::max(clamped, std::max(0.0, min_r.pixels - own_frame));
        }
        if (max_r.kind == LengthKind::Length) {
            clamped = std::min(clamped, std::max(0.0, max_r.pixels - own_frame));
        }
        if (std::fabs(clamped - natural) > 1e-9) rows_available = clamped;
    }
    resolve_tracks(&rows, rows_available, row_gap, get(style, "align-content"));
    distribute_content(&rows, rows_available, row_gap, get(style, "align-content"));

    // ---- Place ------------------------------------------------------------
    const double left_inner = (*tree)[container].padding_left + (*tree)[container].border_left;
    const double top_inner = (*tree)[container].padding_top + (*tree)[container].border_top;
    double bottom = 0;
    for (const Placement& p : items) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        const double h = span_size(rows, p.row, p.row_span, row_gap);
        // `stretch` is the initial alignment in both axes, so an auto-sized
        // item fills its cell; a definite size, or any other keyword, keeps
        // the item's own size and offsets it within the cell.
        const Box& before = (*tree)[p.box];
        const std::string_view justify = self_alignment(before.style, style, false);
        const std::string_view align = self_alignment(before.style, style, true);
        const std::string_view height_raw = get(before.style, "height");
        const bool auto_height = height_raw.empty() || iequals(height_raw, "auto");
        const std::string_view width_raw = get(before.style, "width");
        const bool auto_width = width_raw.empty() || iequals(width_raw, "auto");
        if (auto_height && is_stretch(align) && h > 0) {
            if (has_ratio(before) && auto_width) {
                if (row_is_definite(p)) {
                    // Block axis stretched, inline derived from the ratio.
                    double ratio = 1;
                    try_resolve_aspect_ratio(before.style, &ratio);
                    const bool border_box = is_border_box(before.style);
                    const double h_frame = before.padding_top + before.padding_bottom +
                                           before.border_top + before.border_bottom;
                    const double w_frame = before.padding_left + before.padding_right +
                                           before.border_left + before.border_right;
                    const double content_h = std::max(0.0, h - h_frame);
                    const double derived_w = border_box ? h * ratio : content_h * ratio + w_frame;
                    block->relayout_at_size(p.box, derived_w, h);
                }
                // Otherwise the inline axis was stretched and the height the
                // ratio gave it stands: no block-axis stretch.
            } else {
                // At the inline size it already has, not the cell's: a `center`
                // or explicit-width item must not be widened by the stretch.
                block->relayout_at_size(p.box, before.width, h);
            }
        }
        Box& b = (*tree)[p.box];
        const double outer_w = b.width + b.margin_left + b.margin_right;
        const double outer_h = b.height + b.margin_top + b.margin_bottom;
        b.x = left_inner + (p.column < static_cast<int>(columns.size())
                                ? columns[p.column].position
                                : 0) +
              b.margin_left + align_offset(justify, w, outer_w);
        b.y = top_inner +
              (p.row < static_cast<int>(rows.size()) ? rows[p.row].position : 0) + b.margin_top +
              align_offset(align, h, outer_h);
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
