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

// CSS Grid L2 §2: a grid item with `grid-template-columns/rows: subgrid`
// takes its tracks from the parent grid's tracks it spans. The parent knows
// those sizes; the child is laid out through block layout, which does not
// carry them — so the parent leaves them here, keyed by the child's box,
// right before it lays the child out, and the child's layout_grid picks them
// up. The subgrid's own margin, border and padding come off its edge tracks
// (§2.1), so its content width still equals the tracks plus gaps.
struct SubgridTracks {
    std::vector<double> columns, rows;
    double column_gap = 0, row_gap = 0;
};
thread_local std::map<BoxId, SubgridTracks> g_subgrid_tracks;

// Defined further down, beside the other track-list parsing.
std::string_view trim(std::string_view s);

// A template value is at its initial value when it says nothing: absent, or
// the `none` the registry hands back for an unset track list.
bool is_template_initial(std::string_view raw) {
    const std::string_view v = trim(raw);
    return v.empty() || iequals(v, "none");
}

// `grid-template: <rows> / <columns>`, split at the TOP-LEVEL slash. A slash
// inside parentheses belongs to a minmax() or a repeat(), one inside brackets
// to a line name, and one inside a string to an area row -- none of them
// separate the two halves.
//
// Ports GridShorthand.SplitTemplate. Without it `grid-template` set nothing at
// all: both longhands stayed at `none`, the grid fell back to one implicit
// column, and every item came out the container's full width. The oracle's
// cov-grid case caught it with Chrome and the reference agreeing against us on
// all 36 values.
void split_template(std::string_view text, std::string* rows, std::string* columns) {
    rows->clear();
    columns->clear();
    if (text.empty()) return;
    int parens = 0, brackets = 0;
    char in_string = '\0';
    for (std::size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (in_string != '\0') {
            if (c == in_string) in_string = '\0';
            continue;
        }
        if (c == '"' || c == '\'') { in_string = c; continue; }
        else if (c == '(') ++parens;
        else if (c == ')') --parens;
        else if (c == '[') ++brackets;
        else if (c == ']') --brackets;
        else if (c == '/' && parens == 0 && brackets == 0) {
            *rows = std::string(trim(text.substr(0, i)));
            *columns = std::string(trim(text.substr(i + 1)));
            return;
        }
    }
    // No slash: the whole value is the rows half.
    *rows = std::string(trim(text));
}

bool wants_subgrid(const ComputedStyle* style, std::string_view property) {
    std::string_view v = get(style, property);
    while (!v.empty() && (v.front() == ' ' || v.front() == '\t')) v.remove_prefix(1);
    while (!v.empty() && (v.back() == ' ' || v.back() == '\t')) v.remove_suffix(1);
    return iequals(v, "subgrid");
}

// A track sizing function (CSS Grid L1 §7.2.3): one side of minmax(). `fr`
// is only ever a max; fit-content() only ever a max as well.
struct Sizing {
    enum class Kind { Fixed, Auto, MinContent, MaxContent, Flex, FitContent } kind = Kind::Auto;
    double value = 0;   // pixels for Fixed and the fit-content() limit, the flex factor for Flex
};

// One track: its min and max sizing functions, and the state §12 works on.
// `100px` is minmax(100px, 100px); `1fr` is minmax(auto, 1fr); `auto` is
// minmax(auto, auto).
struct Track {
    Sizing min, max;
    double base = 0;         // §12.3 base size
    double limit = -1;       // growth limit; negative = not yet bounded / infinite
    double size = 0;         // the used size
    double position = 0;
    bool collapsible = false;   // produced by repeat(auto-fit)
    bool collapsed = false;     // an auto-fit track nothing landed in: no size, no gap
    bool is_flex() const { return max.kind == Sizing::Kind::Flex; }
    bool is_definite() const {
        return min.kind == Sizing::Kind::Fixed && max.kind == Sizing::Kind::Fixed;
    }
    bool intrinsic_min() const {
        return min.kind == Sizing::Kind::Auto || min.kind == Sizing::Kind::MinContent ||
               min.kind == Sizing::Kind::MaxContent;
    }
    bool intrinsic_max() const {
        return max.kind == Sizing::Kind::Auto || max.kind == Sizing::Kind::MinContent ||
               max.kind == Sizing::Kind::MaxContent || max.kind == Sizing::Kind::FitContent;
    }
    static Track fixed(double px) {
        Track t;
        t.min = {Sizing::Kind::Fixed, px};
        t.max = {Sizing::Kind::Fixed, px};
        return t;
    }
};

// What an item asks of the tracks it covers (§12.5): its min-content and
// max-content contributions, outer sizes.
struct Contribution {
    int start = 0;
    int span = 1;
    double min_c = 0;
    double max_c = 0;
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

std::string_view trim_track(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\n')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\n')) s.remove_suffix(1);
    return s;
}

// Splits on top-level commas, so `minmax(100px, 1fr)` yields its two sides
// and `repeat(2, minmax(0, 1fr))` its count and body.
std::vector<std::string_view> split_top_level_commas(std::string_view raw) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] == '(') ++depth;
        else if (raw[i] == ')') --depth;
        else if (raw[i] == ',' && depth == 0) {
            out.push_back(trim_track(raw.substr(start, i - start)));
            start = i + 1;
        }
    }
    out.push_back(trim_track(raw.substr(start)));
    return out;
}

bool is_function(std::string_view text, std::string_view name) {
    return text.size() > name.size() + 1 && iequals(text.substr(0, name.size() + 1),
                                                    std::string(name) + "(") &&
           text.back() == ')';
}

Sizing parse_sizing(std::string_view text, const LayoutContext& ctx, double font_size,
                    double basis, bool allow_flex) {
    text = trim_track(text);
    if (text.empty() || iequals(text, "auto")) return {Sizing::Kind::Auto, 0};
    if (iequals(text, "min-content")) return {Sizing::Kind::MinContent, 0};
    if (iequals(text, "max-content")) return {Sizing::Kind::MaxContent, 0};
    if (allow_flex && text.size() > 2 && iequals(text.substr(text.size() - 2), "fr")) {
        const std::string number(text.substr(0, text.size() - 2));
        return {Sizing::Kind::Flex, std::max(0.0, std::strtod(number.c_str(), nullptr))};
    }
    if (allow_flex && is_function(text, "fit-content")) {
        const Sizing inner = parse_sizing(text.substr(12, text.size() - 13), ctx, font_size, basis, false);
        if (inner.kind == Sizing::Kind::Fixed) return {Sizing::Kind::FitContent, inner.value};
        return {Sizing::Kind::Auto, 0};
    }
    const ResolvedLength r = resolve_length(text, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return {Sizing::Kind::Fixed, std::max(0.0, r.pixels)};
    if (r.kind == LengthKind::Percent) {
        // A percentage against an indefinite size behaves as auto (§7.2.1).
        if (basis <= 0) return {Sizing::Kind::Auto, 0};
        return {Sizing::Kind::Fixed, std::max(0.0, basis * r.percent * 0.01)};
    }
    // subgrid, named lines, anything else: auto. Visibly the wrong size
    // rather than subtly so.
    return {Sizing::Kind::Auto, 0};
}

Track parse_track(std::string_view text, const LayoutContext& ctx, double font_size,
                  double basis) {
    text = trim_track(text);
    Track t;
    if (is_function(text, "minmax")) {
        const std::vector<std::string_view> sides =
            split_top_level_commas(text.substr(7, text.size() - 8));
        if (sides.size() == 2) {
            t.min = parse_sizing(sides[0], ctx, font_size, basis, false);
            t.max = parse_sizing(sides[1], ctx, font_size, basis, true);
            return t;
        }
        return t;
    }
    const Sizing one = parse_sizing(text, ctx, font_size, basis, true);
    if (one.kind == Sizing::Kind::Flex || one.kind == Sizing::Kind::FitContent) {
        t.min = {Sizing::Kind::Auto, 0};
        t.max = one;
        return t;
    }
    t.min = one;
    t.max = one;
    return t;
}

// The size a track pattern occupies when counting repeat(auto-fill|auto-fit)
// repetitions (§7.2.3.2): a definite max, else a definite min, else nothing.
double counting_size(const Track& t) {
    if (t.max.kind == Sizing::Kind::Fixed) return t.max.value;
    if (t.min.kind == Sizing::Kind::Fixed) return t.min.value;
    return 0;
}

std::vector<Track> parse_track_list(std::string_view raw, const LayoutContext& ctx,
                                    double font_size, double basis, double gap) {
    std::vector<Track> tracks;
    raw = trim_track(raw);
    if (raw.empty() || iequals(raw, "none")) return tracks;
    for (std::string_view token : split_tracks(raw)) {
        if (is_function(token, "repeat")) {
            const std::vector<std::string_view> parts =
                split_top_level_commas(token.substr(7, token.size() - 8));
            if (parts.size() < 2) continue;
            std::vector<Track> body;
            for (std::string_view sub : split_tracks(parts[1])) {
                body.push_back(parse_track(sub, ctx, font_size, basis));
            }
            if (body.empty()) continue;
            int count = 0;
            bool collapsible = false;
            if (iequals(parts[0], "auto-fill") || iequals(parts[0], "auto-fit")) {
                // As many repetitions as fit the definite size, at least one.
                // An indefinite size fits exactly one (§7.2.3.2).
                collapsible = iequals(parts[0], "auto-fit");
                count = 1;
                if (basis > 0) {
                    double pattern = gap * static_cast<double>(body.size());
                    for (const Track& t : body) pattern += counting_size(t);
                    if (pattern > 0) {
                        count = std::max(1, static_cast<int>(std::floor((basis + gap) / pattern + 1e-9)));
                    }
                }
            } else {
                count = std::atoi(std::string(parts[0]).c_str());
            }
            for (int i = 0; i < count && i < 1024; ++i) {
                for (Track t : body) {
                    t.collapsible = collapsible;
                    tracks.push_back(t);
                }
            }
            continue;
        }
        tracks.push_back(parse_track(token, ctx, font_size, basis));
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

// The gap after track i: none after a collapsed track or before one, and
// none after the last.
double gap_after(const std::vector<Track>& tracks, size_t i, double gap) {
    if (tracks[i].collapsed) return 0;
    for (size_t j = i + 1; j < tracks.size(); ++j) {
        if (!tracks[j].collapsed) return gap;
    }
    return 0;
}

double gaps_within(const std::vector<Track>& tracks, int start, int span, double gap) {
    double total = 0;
    const int end = std::min(static_cast<int>(tracks.size()), start + span);
    for (int i = start; i < end - 1; ++i) {
        if (!tracks[i].collapsed) {
            for (int j = i + 1; j < end; ++j) {
                if (!tracks[j].collapsed) { total += gap; break; }
            }
        }
    }
    return total;
}

// CSS Grid L1 §12.3–12.8, the track sizing algorithm, for one axis.
//
// `available` is the definite space in that axis or negative when there is
// none — a max-content constraint, under which every free-space step treats
// the space as infinite and the tracks grow to their limits.
// A pooled buffer for the sorted copy size_tracks needs.
//
// It sorts the contributions, so it cannot take them by reference and work in
// place -- a caller reuses the same list for a probe and then for the real
// pass. Taking them BY VALUE allocated a fresh vector on every call, seven per
// grid, and a page of grids does that thousands of times a pass.
//
// A pool rather than one thread_local buffer, because a grid item can itself
// be a grid and is sized inside its parent's call.
std::vector<std::unique_ptr<std::vector<Contribution>>>& contribution_pool() {
    thread_local std::vector<std::unique_ptr<std::vector<Contribution>>> pool;
    return pool;
}

class ContributionLease {
public:
    ContributionLease() {
        auto& pool = contribution_pool();
        if (pool.empty()) {
            owned_ = std::make_unique<std::vector<Contribution>>();
        } else {
            owned_ = std::move(pool.back());
            pool.pop_back();
        }
        owned_->clear();
    }
    // Handed back with its capacity, which is the point.
    ~ContributionLease() { contribution_pool().push_back(std::move(owned_)); }
    ContributionLease(const ContributionLease&) = delete;
    ContributionLease& operator=(const ContributionLease&) = delete;
    std::vector<Contribution>& operator*() const { return *owned_; }

private:
    std::unique_ptr<std::vector<Contribution>> owned_;
};

void size_tracks(std::vector<Track>* tracks, double available, double gap,
                 const std::vector<Contribution>& items_in, std::string_view content_align) {
    ContributionLease lease;
    std::vector<Contribution>& items = *lease;
    items.assign(items_in.begin(), items_in.end());
    if (tracks->empty()) return;
    const int n = static_cast<int>(tracks->size());
    const bool definite = available >= 0;

    // §12.4 initialise: a fixed min is the base, a fixed max the limit;
    // intrinsic ones wait for the items; a flexible max is unbounded.
    for (Track& t : *tracks) {
        if (t.collapsed) { t.base = 0; t.limit = 0; t.size = 0; continue; }
        t.base = t.min.kind == Sizing::Kind::Fixed ? t.min.value : 0;
        t.limit = t.max.kind == Sizing::Kind::Fixed ? t.max.value : -1;
    }
    const auto min_contribution = [](const Track& t, const Contribution& c) {
        return t.min.kind == Sizing::Kind::MaxContent ? c.max_c : c.min_c;
    };
    const auto max_contribution = [](const Track& t, const Contribution& c) {
        if (t.max.kind == Sizing::Kind::MinContent) return c.min_c;
        if (t.max.kind == Sizing::Kind::FitContent) {
            return std::max(c.min_c, std::min(c.max_c, t.max.value));
        }
        return c.max_c;
    };

    // §12.5 step 2: items spanning one track.
    for (const Contribution& c : items) {
        if (c.span != 1 || c.start < 0 || c.start >= n) continue;
        Track& t = (*tracks)[c.start];
        if (t.collapsed) continue;
        if (t.intrinsic_min()) t.base = std::max(t.base, min_contribution(t, c));
        if (t.intrinsic_max()) t.limit = std::max(t.limit, max_contribution(t, c));
    }
    for (Track& t : *tracks) {
        if (t.collapsed) continue;
        if (t.intrinsic_max() && t.limit < 0) t.limit = t.base;
        if (t.limit >= 0 && t.limit < t.base) t.limit = t.base;
    }

    // §12.5 step 3: spanning items, shortest spans first, not those crossing
    // a flexible track (step 4 handles those through the fr size). Whatever
    // the tracks they cover do not already hold is split equally onto the
    // intrinsic ones among them.
    std::stable_sort(items.begin(), items.end(),
                     [](const Contribution& a, const Contribution& b) { return a.span < b.span; });
    for (const Contribution& c : items) {
        if (c.span <= 1 || c.start < 0) continue;
        const int end = std::min(n, c.start + c.span);
        bool crosses_flex = false;
        double sum_base = 0, sum_limit = 0;
        int intrinsic_mins = 0, intrinsic_maxes = 0;
        for (int i = c.start; i < end; ++i) {
            const Track& t = (*tracks)[i];
            if (t.collapsed) continue;
            if (t.is_flex()) crosses_flex = true;
            sum_base += t.base;
            sum_limit += t.limit >= 0 ? t.limit : t.base;
            if (t.intrinsic_min()) ++intrinsic_mins;
            if (t.intrinsic_max()) ++intrinsic_maxes;
        }
        if (crosses_flex) continue;
        const double g = gaps_within(*tracks, c.start, c.span, gap);
        const double extra_min = c.min_c - (sum_base + g);
        if (extra_min > 0 && intrinsic_mins > 0) {
            const double share = extra_min / intrinsic_mins;
            for (int i = c.start; i < end; ++i) {
                Track& t = (*tracks)[i];
                if (t.collapsed || !t.intrinsic_min()) continue;
                t.base += share;
                if (t.limit >= 0 && t.limit < t.base) t.limit = t.base;
            }
        }
        // The limits are re-read after the minimum step raised some of them.
        sum_limit = 0;
        for (int i = c.start; i < end; ++i) {
            const Track& t = (*tracks)[i];
            if (!t.collapsed) sum_limit += t.limit >= 0 ? t.limit : t.base;
        }
        const double extra_max = c.max_c - (sum_limit + g);
        if (extra_max > 0 && intrinsic_maxes > 0) {
            const double share = extra_max / intrinsic_maxes;
            for (int i = c.start; i < end; ++i) {
                Track& t = (*tracks)[i];
                if (t.collapsed || !t.intrinsic_max()) continue;
                t.limit = (t.limit >= 0 ? t.limit : t.base) + share;
            }
        }
    }

    const auto total_gaps = [&] {
        double g = 0;
        for (size_t i = 0; i < tracks->size(); ++i) g += gap_after(*tracks, i, gap);
        return g;
    };

    // §12.6 maximize: positive free space grows the non-flexible tracks
    // equally up to their limits. Under a max-content constraint the space is
    // infinite and each simply reaches its limit.
    if (definite) {
        double used = total_gaps();
        for (const Track& t : *tracks) used += t.base;
        double free_space = available - used;
        for (int pass = 0; pass < n + 1 && free_space > 1e-9; ++pass) {
            int growable = 0;
            for (const Track& t : *tracks) {
                if (!t.collapsed && !t.is_flex() && t.limit >= 0 && t.base < t.limit - 1e-9) ++growable;
            }
            if (growable == 0) break;
            const double share = free_space / growable;
            for (Track& t : *tracks) {
                if (t.collapsed || t.is_flex() || t.limit < 0 || t.base >= t.limit - 1e-9) continue;
                const double grow = std::min(share, t.limit - t.base);
                t.base += grow;
                free_space -= grow;
            }
        }
    } else {
        for (Track& t : *tracks) {
            if (!t.collapsed && !t.is_flex() && t.limit >= 0) t.base = t.limit;
        }
    }
    for (Track& t : *tracks) t.size = t.base;

    // §12.7 expand flexible tracks.
    bool any_flex = false;
    for (const Track& t : *tracks) any_flex = any_flex || (!t.collapsed && t.is_flex());
    if (any_flex) {
        if (definite) {
            // §12.7.1 find the size of an fr: a flexible track whose base
            // already exceeds its share is treated as inflexible and the rest
            // re-divided, until none does.
            double leftover = available - total_gaps();
            for (const Track& t : *tracks) {
                if (!t.collapsed && !t.is_flex()) leftover -= t.base;
            }
            std::vector<bool> flexible(tracks->size(), false);
            for (size_t i = 0; i < tracks->size(); ++i) {
                flexible[i] = !(*tracks)[i].collapsed && (*tracks)[i].is_flex();
            }
            double hypothetical = 0;
            for (int pass = 0; pass < n + 1; ++pass) {
                double sum_fr = 0, inflexible = 0;
                for (size_t i = 0; i < tracks->size(); ++i) {
                    const Track& t = (*tracks)[i];
                    if (!t.is_flex() || t.collapsed) continue;
                    if (flexible[i]) sum_fr += t.max.value;
                    else inflexible += t.base;
                }
                hypothetical = sum_fr > 0 ? std::max(0.0, (leftover - inflexible) / std::max(sum_fr, 1.0)) : 0;
                bool changed = false;
                for (size_t i = 0; i < tracks->size(); ++i) {
                    const Track& t = (*tracks)[i];
                    if (!flexible[i]) continue;
                    if (t.base > hypothetical * t.max.value + 1e-9) {
                        flexible[i] = false;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
            for (size_t i = 0; i < tracks->size(); ++i) {
                Track& t = (*tracks)[i];
                if (!t.is_flex() || t.collapsed) continue;
                t.size = flexible[i] ? hypothetical * t.max.value : t.base;
            }
        } else {
            // Indefinite: the fr is the largest of each flexible track's base
            // over its factor (§12.7.1) — its content, in other words.
            double fr = 0;
            for (const Track& t : *tracks) {
                if (t.collapsed || !t.is_flex()) continue;
                const double f = t.max.value > 1 ? t.max.value : 1.0;
                fr = std::max(fr, t.base / f);
            }
            for (Track& t : *tracks) {
                if (!t.collapsed && t.is_flex()) t.size = std::max(t.base, fr * t.max.value);
            }
        }
    }

    // §12.8 stretch auto tracks: `normal`/`stretch` content alignment hands
    // any remaining definite free space equally to the tracks with an `auto`
    // max. `justify-content: start` on auto columns leaves them at their
    // content size, which is what the keyword is for.
    const bool stretches = content_align.empty() || iequals(content_align, "normal") ||
                           iequals(content_align, "stretch");
    if (definite && stretches) {
        double used = total_gaps();
        for (const Track& t : *tracks) used += t.size;
        const double free_space = available - used;
        int auto_count = 0;
        for (const Track& t : *tracks) {
            if (!t.collapsed && t.max.kind == Sizing::Kind::Auto) ++auto_count;
        }
        if (free_space > 1e-9 && auto_count > 0) {
            const double share = free_space / auto_count;
            for (Track& t : *tracks) {
                if (!t.collapsed && t.max.kind == Sizing::Kind::Auto) t.size += share;
            }
        }
    }

    double pos = 0;
    for (size_t i = 0; i < tracks->size(); ++i) {
        Track& t = *(&(*tracks)[i]);
        t.position = pos;
        pos += t.size + gap_after(*tracks, i, gap);
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
    int n = 0;
    double used = 0;
    for (size_t i = 0; i < tracks->size(); ++i) {
        const Track& t = (*tracks)[i];
        if (t.collapsed) continue;
        ++n;
        used += t.size + gap_after(*tracks, i, gap);
    }
    if (n == 0) return;
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
    for (size_t i = 0; i < tracks->size(); ++i) {
        Track& t = (*tracks)[i];
        t.position = pos;
        if (t.collapsed) continue;
        bool followed = false;
        for (size_t j = i + 1; j < tracks->size(); ++j) followed = followed || !(*tracks)[j].collapsed;
        pos += t.size + gap_after(*tracks, i, gap) + (followed ? extra_gap : 0);
    }
}

double span_size(const std::vector<Track>& tracks, int start, int span, double gap) {
    double total = 0;
    for (int i = start; i < start + span && i < static_cast<int>(tracks.size()); ++i) {
        total += tracks[i].size;
    }
    return total + gaps_within(tracks, start, span, gap);
}

} // namespace

double layout_grid(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block) {
    if (!tree || !block || container == kNoBox) return 0;
    const ComputedStyle* style = (*tree)[container].style;
    const double font_size =
        (*tree)[container].font_size > 0 ? (*tree)[container].font_size : ctx.root_font_size_px;

    const double own_column_gap = [&] {
        const std::string_view raw = get(style, "column-gap");
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, "column-gap", ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();
    const double own_row_gap = [&] {
        const std::string_view raw = get(style, "row-gap");
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, "row-gap", ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();

    // The longhands win; the shorthands are consulted only when NEITHER was
    // set, which is what makes `grid-template-columns` after `grid-template`
    // behave the way the cascade says it should. `grid` is checked after
    // `grid-template` for the same reason and in the same way.
    std::string_view columns_raw = get(style, "grid-template-columns");
    std::string_view rows_raw = get(style, "grid-template-rows");
    std::string from_rows, from_columns;
    if (is_template_initial(columns_raw) && is_template_initial(rows_raw)) {
        for (const char* shorthand : {"grid-template", "grid"}) {
            const std::string_view raw = get(style, shorthand);
            if (is_template_initial(raw)) continue;
            split_template(raw, &from_rows, &from_columns);
            if (!from_rows.empty()) rows_raw = from_rows;
            if (!from_columns.empty()) columns_raw = from_columns;
            break;
        }
    }

    std::vector<Track> columns = parse_track_list(columns_raw, ctx,
                                                  font_size, content_width, own_column_gap);
    std::vector<Track> rows =
        parse_track_list(rows_raw, ctx, font_size,
                         content_height >= 0 ? content_height : 0, own_row_gap);
    // Implicit tracks take their sizing from grid-auto-columns/rows, cycling
    // through the list; the initial `auto` when there is none.
    std::vector<Track> auto_columns = parse_track_list(get(style, "grid-auto-columns"), ctx,
                                                       font_size, content_width, own_column_gap);
    std::vector<Track> auto_rows =
        parse_track_list(get(style, "grid-auto-rows"), ctx, font_size,
                         content_height >= 0 ? content_height : 0, own_row_gap);
    if (auto_columns.empty()) auto_columns.push_back(Track{});
    if (auto_rows.empty()) auto_rows.push_back(Track{});
    const std::vector<std::vector<std::string>> areas =
        parse_areas(get(style, "grid-template-areas"));

    // Subgrid: adopt the spanned parent tracks and the parent's gap; this
    // box's own edges shorten the first and last of them.
    double column_gap_used = own_column_gap;
    double row_gap_used = own_row_gap;
    {
        const auto it = g_subgrid_tracks.find(container);
        if (it != g_subgrid_tracks.end()) {
            const Box& self = (*tree)[container];
            const auto adopt = [&](const std::vector<double>& sizes, double start_edge,
                                   double end_edge, std::vector<Track>* out) {
                out->clear();
                for (double px : sizes) out->push_back(Track::fixed(px));
                if (!out->empty()) {
                    Track& first = out->front();
                    first.min.value = first.max.value = std::max(0.0, first.max.value - start_edge);
                    Track& last = out->back();
                    last.min.value = last.max.value = std::max(0.0, last.max.value - end_edge);
                }
            };
            if (wants_subgrid(style, "grid-template-columns") && !it->second.columns.empty()) {
                adopt(it->second.columns, self.padding_left + self.border_left,
                      self.padding_right + self.border_right, &columns);
                column_gap_used = it->second.column_gap;
            }
            if (wants_subgrid(style, "grid-template-rows") && !it->second.rows.empty()) {
                adopt(it->second.rows, self.padding_top + self.border_top,
                      self.padding_bottom + self.border_bottom, &rows);
                row_gap_used = it->second.row_gap;
            }
            g_subgrid_tracks.erase(it);
        }
    }
    // From here on the gaps are the ones in force for this grid.
    const double column_gap = column_gap_used;
    const double row_gap = row_gap_used;

    // A container with no explicit columns is one column wide, which is what
    // the initial `grid-template-columns: none` means for row-major flow.
    if (columns.empty()) columns.push_back(Track{});

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
    int explicit_columns = static_cast<int>(columns.size());
    int explicit_rows = static_cast<int>(rows.size());
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

    // `grid-auto-flow` (CSS Grid L1 8.5). Two independent things live in one
    // property: which axis the cursor walks, and whether it packs densely.
    //
    // The port read neither, so `grid-auto-flow: column` laid out in rows --
    // a toolbar of buttons meant to run down the side came out across the top.
    bool flow_column = false;
    bool flow_dense = false;
    {
        const std::string_view raw = get((*tree)[container].style, "grid-auto-flow");
        flow_column = raw.find("column") != std::string_view::npos;
        flow_dense = raw.find("dense") != std::string_view::npos;
    }

    // COLUMN FLOW IS ROW FLOW TRANSPOSED. Rather than a second copy of the
    // placement algorithm with the axes swapped -- two chances to get the
    // spec wrong, and two to fix whenever it changes -- the pending items go
    // in transposed, the row-major algorithm runs unchanged, and the results
    // come back out transposed.
    if (flow_column) {
        for (Pending& p : pending) std::swap(p.col, p.row);
        std::swap(explicit_columns, explicit_rows);
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
            // `dense` starts the search over for every item, so a later
            // small one backfills a hole an earlier large one left. The
            // sparse default never looks backwards, which is what keeps
            // document order and visual order together.
            if (flow_dense) {
                cursor_row = 0;
                cursor_col = 0;
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

    // Back to real axes, now that placement is done.
    if (flow_column) {
        for (Placement& pl : items) {
            std::swap(pl.column, pl.row);
            std::swap(pl.column_span, pl.row_span);
        }
        std::swap(explicit_columns, explicit_rows);
    }

    // Implicit columns: a placement past the explicit grid adds `auto` tracks,
    // since grid-auto-columns is not ported.
    int max_column = 0;
    for (const Placement& p : items) max_column = std::max(max_column, p.column + p.column_span);
    for (int i = 0; static_cast<int>(columns.size()) < max_column; ++i) {
        columns.push_back(auto_columns[static_cast<size_t>(i) % auto_columns.size()]);
    }

    // Implicit rows: a grid with more items than explicit rows grows, each
    // new row sized by grid-auto-rows.
    int max_row = 0;
    for (const Placement& p : items) max_row = std::max(max_row, p.row + p.row_span);
    for (int i = 0; static_cast<int>(rows.size()) < max_row; ++i) {
        rows.push_back(auto_rows[static_cast<size_t>(i) % auto_rows.size()]);
    }

    // repeat(auto-fit): a track nothing landed in collapses to nothing and
    // takes its gap with it.
    for (size_t c = 0; c < columns.size(); ++c) {
        if (!columns[c].collapsible) continue;
        bool occupied = false;
        for (const Placement& p : items) {
            if (p.column <= static_cast<int>(c) && static_cast<int>(c) < p.column + p.column_span) {
                occupied = true;
                break;
            }
        }
        columns[c].collapsed = !occupied;
    }
    for (size_t r = 0; r < rows.size(); ++r) {
        if (!rows[r].collapsible) continue;
        bool occupied = false;
        for (const Placement& p : items) {
            if (p.row <= static_cast<int>(r) && static_cast<int>(r) < p.row + p.row_span) {
                occupied = true;
                break;
            }
        }
        rows[r].collapsed = !occupied;
    }

    // ---- Size the columns, then lay the items out to size the rows ---------
    // Every item is laid out once first, whatever track it lands in. This is
    // what resolves its box model, and skipping it for items in FIXED tracks
    // left their padding, border and margin at zero — the children of a
    // 260px-wide sidebar were placed at x=0 rather than at its 16px padding.
    for (const Placement& p : items) {
        block->layout_block(p.box, content_width, style);
    }

    // Each item's inline contributions (§12.5): its min-content and
    // max-content widths plus frame and margins, bounded by its own min-/max-
    // width, or its explicit width for both; a scroll container's automatic
    // minimum is zero.
    std::vector<Contribution> column_contributions;
    for (const Placement& p : items) {
        const Box& b = (*tree)[p.box];
        const double frame = b.padding_left + b.padding_right + b.border_left + b.border_right;
        const double margins = b.margin_left + b.margin_right;
        Contribution c;
        c.start = p.column;
        c.span = p.column_span;
        const std::string_view width_raw = get(b.style, "width");
        const bool explicit_width = !width_raw.empty() && !iequals(width_raw, "auto") &&
                                    width_raw.find('%') == std::string_view::npos;
        if (explicit_width) {
            c.min_c = c.max_c = b.width + margins;
        } else {
            double min_c = min_content_width(*tree, p.box, &ctx) + frame;
            double max_c = max_content_width(*tree, p.box, &ctx) + frame;
            const double item_fs = b.font_size > 0 ? b.font_size : font_size;
            const double minmax_frame = is_border_box(b.style) ? 0 : frame;
            const ResolvedLength min_w =
                resolve_length(b.style, "min-width", ctx, item_fs, content_width);
            if (min_w.kind == LengthKind::Length) {
                min_c = std::max(min_c, min_w.pixels + minmax_frame);
                max_c = std::max(max_c, min_w.pixels + minmax_frame);
            }
            const ResolvedLength max_w =
                resolve_length(b.style, "max-width", ctx, item_fs, content_width);
            if (max_w.kind == LengthKind::Length) {
                min_c = std::min(min_c, max_w.pixels + minmax_frame);
                max_c = std::min(max_c, max_w.pixels + minmax_frame);
            }
            if (clips_overflow(b.style) && min_w.kind != LengthKind::Length) min_c = 0;
            c.min_c = min_c + margins;
            c.max_c = std::max(min_c, max_c) + margins;
        }
        column_contributions.push_back(c);
    }
    // The grid's own intrinsic inline sizes, for whoever shrink-fits it: the
    // tracks under a max-content constraint, and again with every item's
    // max-content pinned to its min-content for the min side.
    {
        std::vector<Track> probe = columns;
        size_tracks(&probe, -1, column_gap, column_contributions, "normal");
        double max_w = 0;
        for (size_t c = 0; c < probe.size(); ++c) max_w += probe[c].size + gap_after(probe, c, column_gap);
        std::vector<Contribution> mins = column_contributions;
        for (Contribution& c : mins) c.max_c = c.min_c;
        std::vector<Track> probe_min = columns;
        size_tracks(&probe_min, -1, column_gap, mins, "normal");
        double min_w = 0;
        for (size_t c = 0; c < probe_min.size(); ++c) min_w += probe_min[c].size + gap_after(probe_min, c, column_gap);
        (*tree)[container].grid_min_content = min_w;
        (*tree)[container].grid_max_content = max_w;
    }
    size_tracks(&columns, content_width, column_gap, column_contributions,
                get(style, "justify-content"));
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
    // Set once the rows are sized: in a grid with a definite height and
    // `align-content: normal | stretch`, the auto rows were stretched to fill
    // it (§12.8), and an item can treat such a row as a definite block size —
    // but only where its columns have an intrinsic minimum that the
    // transferred size can feed. In a FIXED column the inline size is settled
    // first and the height follows the ratio: Chrome keeps vendor's
    // `aspect-ratio: 1` frame in an `80px` column at 80x80 under a row
    // stretched to 200, and grows stats' `1fr` columns to the stretched row.
    bool rows_definite_by_stretch = false;
    const auto columns_intrinsic = [&](const Placement& p) {
        for (int c = p.column; c < p.column + p.column_span && c < static_cast<int>(columns.size()); ++c) {
            if (!columns[c].intrinsic_min()) return false;
        }
        return true;
    };
    const auto row_is_definite = [&](const Placement& p) {
        if (p.row >= static_cast<int>(rows.size())) return false;
        const Track& t = rows[p.row];
        if (t.is_definite()) return true;
        if (t.is_flex() && content_height >= 0) return true;
        return rows_definite_by_stretch && t.intrinsic_max() && columns_intrinsic(p);
    };
    // A subgrid child gets the parent tracks it spans handed over before every
    // layout of it, and is always re-laid so it sees them.
    const auto hand_over = [&](const Placement& p, bool with_rows) {
        const Box& b = (*tree)[p.box];
        const bool cols = wants_subgrid(b.style, "grid-template-columns");
        const bool rws = with_rows && wants_subgrid(b.style, "grid-template-rows");
        if (!cols && !rws) return false;
        SubgridTracks st;
        st.column_gap = column_gap;
        st.row_gap = row_gap;
        if (cols) {
            for (int i = p.column; i < p.column + p.column_span && i < static_cast<int>(columns.size()); ++i) {
                st.columns.push_back(columns[i].size);
            }
        }
        if (rws) {
            for (int i = p.row; i < p.row + p.row_span && i < static_cast<int>(rows.size()); ++i) {
                st.rows.push_back(rows[i].size);
            }
        }
        g_subgrid_tracks[p.box] = std::move(st);
        return true;
    };

    const auto size_inline = [&](const Placement& p) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        const bool subgrid = hand_over(p, false);
        const Box& b = (*tree)[p.box];
        const std::string_view width_raw = get(b.style, "width");
        const bool auto_width = width_raw.empty() || iequals(width_raw, "auto");
        if (!auto_width) {
            block->layout_block(p.box, w, style);
            return;
        }
        if (subgrid) {
            block->relayout_at(p.box, w);
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
    // Each item's block contributions: its laid-out outer height for both,
    // except that a scroll container's automatic minimum is zero (§6.6) — so
    // in a grid with a DEFINITE height such an item does not force the row
    // past the space there is; the row takes its share and the item scrolls.
    // Under an auto height the grid is sized under a max-content constraint
    // and every auto track grows to its limit, scroll container or not.
    std::vector<Contribution> row_contributions;
    for (const Placement& p : items) {
        const Box& b = (*tree)[p.box];
        const double outer = b.height + b.margin_top + b.margin_bottom;
        Contribution c;
        c.start = p.row;
        c.span = p.row_span;
        c.min_c = clips_overflow(b.style) ? 0 : outer;
        c.max_c = outer;
        row_contributions.push_back(c);
    }
    // A container with no definite height is sized to its rows and then
    // clamped by its own min/max-height; the clamped size is definite, so the
    // rows distribute it (the same rule the flex container applies in §9.2).
    // `align-content: space-between; min-height: 150px` over two 40px rows
    // puts the second row at 110, not 40.
    double rows_available = content_height;
    size_tracks(&rows, rows_available, row_gap, row_contributions, get(style, "align-content"));
    if (rows_available < 0 && !rows.empty()) {
        double natural = 0;
        for (size_t r = 0; r < rows.size(); ++r) natural += rows[r].size + gap_after(rows, r, row_gap);
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
        if (std::fabs(clamped - natural) > 1e-9) {
            rows_available = clamped;
            size_tracks(&rows, rows_available, row_gap, row_contributions,
                        get(style, "align-content"));
        }
    }
    distribute_content(&rows, rows_available, row_gap, get(style, "align-content"));

    // ---- §12.1 steps 3 and 4: the rows can feed back into the columns -------
    // An aspect-ratio item that will be stretched to a now-definite row takes
    // its inline size from that height (css-sizing-4 §5.1), and that
    // transferred size is its new min-content contribution. Where it exceeds
    // what the column had, the columns are re-resolved with it and the rows
    // after them. Chrome: a `flex: 1` gear grid in a column flex gets a
    // definite 287.81px height, its two auto rows stretch to 139.91, and the
    // four `1fr` columns grow to 139.91 squares — overflowing the 460px grid
    // rather than staying at their 109px share.
    {
        const std::string_view ac = get(style, "align-content");
        rows_definite_by_stretch =
            rows_available >= 0 && (ac.empty() || iequals(ac, "normal") || iequals(ac, "stretch"));
        bool columns_changed = false;
        for (size_t i = 0; i < items.size() && i < column_contributions.size(); ++i) {
            const Placement& p = items[i];
            const Box& b = (*tree)[p.box];
            if (!has_ratio(b) || !row_is_definite(p)) continue;
            const std::string_view width_raw = get(b.style, "width");
            const std::string_view height_raw = get(b.style, "height");
            if (!(width_raw.empty() || iequals(width_raw, "auto"))) continue;
            if (!(height_raw.empty() || iequals(height_raw, "auto"))) continue;
            if (!is_stretch(self_alignment(b.style, style, true))) continue;
            const double h =
                span_size(rows, p.row, p.row_span, row_gap) - b.margin_top - b.margin_bottom;
            if (h <= 0) continue;
            double ratio = 1;
            try_resolve_aspect_ratio(b.style, &ratio);
            const double h_frame =
                b.padding_top + b.padding_bottom + b.border_top + b.border_bottom;
            const double w_frame =
                b.padding_left + b.padding_right + b.border_left + b.border_right;
            const double transferred =
                (is_border_box(b.style) ? h * ratio : std::max(0.0, h - h_frame) * ratio + w_frame) +
                b.margin_left + b.margin_right;
            Contribution& c = column_contributions[i];
            if (transferred > c.min_c + 1e-9) {
                c.min_c = transferred;
                c.max_c = std::max(c.max_c, transferred);
                columns_changed = true;
            }
        }
        if (columns_changed) {
            size_tracks(&columns, content_width, column_gap, column_contributions,
                        get(style, "justify-content"));
            distribute_content(&columns, content_width, column_gap, get(style, "justify-content"));
            for (const Placement& p : items) size_inline(p);
            row_contributions.clear();
            for (const Placement& p : items) {
                const Box& b = (*tree)[p.box];
                const double outer = b.height + b.margin_top + b.margin_bottom;
                Contribution c;
                c.start = p.row;
                c.span = p.row_span;
                c.min_c = clips_overflow(b.style) ? 0 : outer;
                c.max_c = outer;
                row_contributions.push_back(c);
            }
            size_tracks(&rows, rows_available, row_gap, row_contributions, ac);
            distribute_content(&rows, rows_available, row_gap, ac);
        }
    }

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
        const bool subgrid_rows = hand_over(p, true);
        if (subgrid_rows && h > 0) {
            // The rows are known now; the child adopts them and is re-laid at
            // its area's height whatever its alignment.
            block->relayout_at_size(p.box, before.width, h);
        } else if (auto_height && is_stretch(align) && h > 0) {
            if (has_ratio(before) && auto_width) {
                if (row_is_definite(p)) {
                    // Both axes stretch and the ratio transfers each stretched
                    // size into the other axis; each axis ends up the LARGER
                    // of its own stretch and the transfer (css-sizing-4 §5.1
                    // with the automatic minimums of §4.4). Chrome: 196x196
                    // cells in 119px columns and 196px rows, and 195x195 tiles
                    // in 195px columns and 110px rows.
                    double ratio = 1;
                    try_resolve_aspect_ratio(before.style, &ratio);
                    const bool border_box = is_border_box(before.style);
                    const double h_frame = before.padding_top + before.padding_bottom +
                                           before.border_top + before.border_bottom;
                    const double w_frame = before.padding_left + before.padding_right +
                                           before.border_left + before.border_right;
                    const auto width_from_height = [&](double hh) {
                        return border_box ? hh * ratio : std::max(0.0, hh - h_frame) * ratio + w_frame;
                    };
                    const auto height_from_width = [&](double ww) {
                        return border_box ? ww / ratio : std::max(0.0, ww - w_frame) / ratio + h_frame;
                    };
                    const double final_w = std::max(before.width, width_from_height(h));
                    const double final_h = std::max(h, height_from_width(before.width));
                    block->relayout_at_size(p.box, final_w, final_h);
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

    // The container's content height is its row tracks' extent. An item that
    // overflows its area — an aspect-ratio tile taller than its 110px row —
    // overflows the grid too rather than growing it (Chrome: 248 for two
    // 110px rows and a gap, with 195px tiles hanging out of them).
    if (!rows.empty()) {
        double extent = 0;
        for (size_t r = 0; r < rows.size(); ++r) {
            if (rows[r].collapsed) continue;
            extent = std::max(extent, rows[r].position + rows[r].size);
        }
        bottom = extent;
    }
    return bottom;
}

} // namespace weva
