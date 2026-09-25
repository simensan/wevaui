#include "weva/css_properties.h"
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


// The same lookup by id. A property id is resolved once for the
// program below rather than hashed from its name on every call --
// sampling put ComputedStyle::get and CssPropertyRegistry::id_of
// together at a quarter of a layout pass, ahead of any layout
// algorithm. Safe because the registry keeps an id stable across
// re-registration, which is what its header promises it for.
const int kId_max_height = CssPropertyRegistry::instance().id_of("max-height");
const int kId_max_width = CssPropertyRegistry::instance().id_of("max-width");
const int kId_min_height = CssPropertyRegistry::instance().id_of("min-height");
const int kId_align_self = CssPropertyRegistry::instance().id_of("align-self");
const int kId_justify_self = CssPropertyRegistry::instance().id_of("justify-self");
const int kId_align_items = CssPropertyRegistry::instance().id_of("align-items");
const int kId_justify_items = CssPropertyRegistry::instance().id_of("justify-items");
const int kId_grid_auto_flow = CssPropertyRegistry::instance().id_of("grid-auto-flow");
const int kId_min_width = CssPropertyRegistry::instance().id_of("min-width");

std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

// Resolved at static-init. The registry is a function-local static,
// so it is constructed on first use and these cannot outrun it.
const int kId_align_content = CssPropertyRegistry::instance().id_of("align-content");
const int kId_column_gap = CssPropertyRegistry::instance().id_of("column-gap");
const int kId_grid_area = CssPropertyRegistry::instance().id_of("grid-area");
const int kId_grid_auto_columns = CssPropertyRegistry::instance().id_of("grid-auto-columns");
const int kId_grid_auto_rows = CssPropertyRegistry::instance().id_of("grid-auto-rows");
const int kId_grid_template_areas = CssPropertyRegistry::instance().id_of("grid-template-areas");
const int kId_grid_template_columns = CssPropertyRegistry::instance().id_of("grid-template-columns");
const int kId_grid_template_rows = CssPropertyRegistry::instance().id_of("grid-template-rows");
const int kId_height = CssPropertyRegistry::instance().id_of("height");
const int kId_justify_content = CssPropertyRegistry::instance().id_of("justify-content");
const int kId_overflow_x = CssPropertyRegistry::instance().id_of("overflow-x");
const int kId_overflow_y = CssPropertyRegistry::instance().id_of("overflow-y");
const int kId_position = CssPropertyRegistry::instance().id_of("position");
const int kId_row_gap = CssPropertyRegistry::instance().id_of("row-gap");
const int kId_width = CssPropertyRegistry::instance().id_of("width");


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
        if (c == '[' && depth == 0 && start != std::string_view::npos) {
            out.push_back(raw.substr(start, i - start));
            start = std::string_view::npos;
        }
        if (c == ']' && depth == 1) {
            if (start != std::string_view::npos) out.push_back(raw.substr(start, i - start + 1));
            start = std::string_view::npos;
            --depth;
            continue;
        }
        if (c == '(' || c == '[') ++depth;
        else if (c == ')' || c == ']') --depth;
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

using LineNames = std::vector<std::vector<std::string>>;

std::vector<Track> parse_track_list(std::string_view raw, const LayoutContext& ctx,
                                    double font_size, double basis, double gap, LineNames* names = nullptr, int nesting = 0) {
    std::vector<Track> tracks;
    if (names) names->resize(1);
    raw = trim_track(raw);
    if (nesting > 1 || raw.empty() || iequals(raw, "none")) return tracks;
    for (std::string_view token : split_tracks(raw)) {
        if (token.size() >= 2 && token.front() == '[' && token.back() == ']') {
            if (names) for (auto name : split_tracks(token.substr(1, token.size() - 2)))
                names->back().emplace_back(name);
            continue;
        }
        if (is_function(token, "repeat")) {
            const std::vector<std::string_view> parts =
                split_top_level_commas(token.substr(7, token.size() - 8));
            if (parts.size() < 2) continue;
            LineNames body_names;
            const std::vector<Track> body = parse_track_list(parts[1], ctx, font_size, basis, gap,
                                                            names ? &body_names : nullptr, nesting + 1);
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
                for (size_t j = 0; j < body.size(); ++j) {
                    if (names) {
                        auto& line = names->back();
                        line.insert(line.end(), body_names[j].begin(), body_names[j].end());
                        names->emplace_back();
                    }
                    Track t = body[j];
                    t.collapsible = collapsible;
                    tracks.push_back(t);
                }
                if (names) {
                    auto& line = names->back();
                    line.insert(line.end(), body_names.back().begin(), body_names.back().end());
                }
            }
            continue;
        }
        tracks.push_back(parse_track(token, ctx, font_size, basis));
        if (names) names->emplace_back();
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
    const std::string_view x = get(style, kId_overflow_x);
    if (!x.empty() && !iequals(x, "visible")) return true;
    const std::string_view y = get(style, kId_overflow_y);
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
// Names are resolved against explicit and area-generated line names below.
struct LineSpec {
    bool is_auto = true;
    bool is_span = false;
    int n = 0;
    std::string name;
    bool explicit_number = false;
    bool bare_name() const { return !name.empty() && !is_span && !explicit_number; }
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
        else if (spec.name.empty()) spec.name = std::string(w);
        else return LineSpec{};
    }
    spec.explicit_number = have_n;
    if (!have_n && !spec.name.empty()) n = 1;
    if (span) {
        spec.is_auto = false;
        spec.is_span = true;
        if (have_n && n <= 0) return LineSpec{};
        spec.n = have_n ? n : 1;
        return spec;
    }
    if ((!have_n && spec.name.empty()) || n == 0) return LineSpec{};
    spec.is_auto = false;
    spec.n = n;
    return spec;
}

// The two sides of one axis, read from the shorthand (`grid-column: 1 / 3`)
// when it is set and the longhands otherwise. The shorthands are stored raw
// rather than expanded. A bare name also supplies the omitted end.
void read_axis(const ComputedStyle* style, std::string_view shorthand,
               std::string_view start_prop, std::string_view end_prop,
               LineSpec* start, LineSpec* end) {
    const std::string_view raw = trim(get(style, shorthand));
    if (!raw.empty() && !iequals(raw, "auto")) {
        const size_t slash = raw.find('/');
        if (slash == std::string_view::npos) {
            *start = parse_line_spec(raw);
            *end = start->bare_name() ? *start : LineSpec{};
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

AxisPlacement resolve_axis(const LineSpec& s, const LineSpec& e, int explicit_tracks,
                           const LineNames& names) {
    const auto matches = [&](int line, const std::string& name) {
        return line >= 1 && line <= static_cast<int>(names.size()) &&
            std::find(names[line - 1].begin(), names[line - 1].end(), name) != names[line - 1].end();
    };
    const auto line_number = [&](const LineSpec& spec, bool end) {
        if (spec.name.empty()) return spec.n > 0 ? spec.n : explicit_tracks + 2 + spec.n;
        if (spec.bare_name()) {
            const std::string edge = spec.name + (end ? "-end" : "-start");
            for (int line = 1; line <= explicit_tracks + 1; ++line)
                if (matches(line, edge)) return line;
        }
        int left = std::abs(spec.n);
        const int direction = spec.n < 0 ? -1 : 1;
        int line = direction > 0 ? 1 : explicit_tracks + 1;
        for (; line >= 1 && line <= explicit_tracks + 1; line += direction)
            if (matches(line, spec.name) && --left == 0) return line;
        return direction > 0 ? explicit_tracks + 1 + left : 1 - left;
    };
    const auto span_edge = [&](const LineSpec& spec, int opposite, int direction) {
        if (spec.name.empty()) return opposite + direction * spec.n;
        int left = spec.n;
        int line = opposite + direction;
        while (true) {
            // Only implicit lines on the search side receive the missing name.
            const bool implicit = direction > 0 ? line > explicit_tracks + 1 : line < 1;
            if ((implicit || matches(line, spec.name)) && --left == 0) return line;
            line += direction;
        }
    };
    AxisPlacement out;
    const bool sl = !s.is_auto && !s.is_span, el = !e.is_auto && !e.is_span;
    if (sl || el) {
        int a = sl ? line_number(s, false) : 0;
        int b = el ? line_number(e, true) : 0;
        if (!sl) a = s.is_span ? span_edge(s, b, -1) : b - 1;
        if (!el) b = e.is_span ? span_edge(e, a, 1) : a + 1;
        if (a == b) ++b;
        if (b < a) std::swap(a,b);
        out = {true, a - 1, b - a};
    } else {
        const LineSpec& span = s.is_span ? s : e;
        out.span = span.is_span && span.name.empty() ? span.n : 1;
    }
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
    std::string_view v = get(item, block_axis ? kId_align_self : kId_justify_self);
    if (v.empty() || iequals(v, "auto") || iequals(v, "normal")) {
        v = get(container, block_axis ? kId_align_items : kId_justify_items);
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
        const std::string_view raw = get(style, kId_column_gap);
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, kId_column_gap, ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();
    const double own_row_gap = [&] {
        const std::string_view raw = get(style, kId_row_gap);
        if (raw.empty() || iequals(raw, "normal")) return 0.0;
        const ResolvedLength r = resolve_length(style, kId_row_gap, ctx, font_size, content_width);
        return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0.0;
    }();

    // The longhands win; the shorthands are consulted only when NEITHER was
    // set, which is what makes `grid-template-columns` after `grid-template`
    // behave the way the cascade says it should. `grid` is checked after
    // `grid-template` for the same reason and in the same way.
    std::string_view columns_raw = get(style, kId_grid_template_columns);
    std::string_view rows_raw = get(style, kId_grid_template_rows);
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

    LineNames column_names, row_names;
    std::vector<Track> columns = parse_track_list(columns_raw, ctx,
                                                  font_size, content_width, own_column_gap,
                                                  columns_raw.find('[') != std::string_view::npos ? &column_names : nullptr);
    std::vector<Track> rows =
        parse_track_list(rows_raw, ctx, font_size,
                         content_height >= 0 ? content_height : 0, own_row_gap,
                         rows_raw.find('[') != std::string_view::npos ? &row_names : nullptr);
    // Implicit tracks take their sizing from grid-auto-columns/rows, cycling
    // through the list; the initial `auto` when there is none.
    std::vector<Track> auto_columns = parse_track_list(get(style, kId_grid_auto_columns), ctx,
                                                       font_size, content_width, own_column_gap);
    std::vector<Track> auto_rows =
        parse_track_list(get(style, kId_grid_auto_rows), ctx, font_size,
                         content_height >= 0 ? content_height : 0, own_row_gap);
    if (auto_columns.empty()) auto_columns.push_back(Track{});
    if (auto_rows.empty()) auto_rows.push_back(Track{});
    const std::vector<std::vector<std::string>> areas =
        parse_areas(get(style, kId_grid_template_areas));

    // Subgrid: adopt the spanned parent tracks and the parent's gap; this
    // box's own edges shorten the first and last of them.
    double column_gap_used = own_column_gap;
    double row_gap_used = own_row_gap;
    // CSS Grid L2 §9: a subgridded axis has no implicit tracks. Items that run
    // past the subgridded range are clamped into the last one instead of
    // growing the grid, so these follow the adoption below into the placement.
    bool columns_are_subgrid = false;
    bool rows_are_subgrid = false;
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
                columns_are_subgrid = true;
            }
            if (wants_subgrid(style, "grid-template-rows") && !it->second.rows.empty()) {
                adopt(it->second.rows, self.padding_top + self.border_top,
                      self.padding_bottom + self.border_bottom, &rows);
                row_gap_used = it->second.row_gap;
                rows_are_subgrid = true;
            }
            g_subgrid_tracks.erase(it);
        }
    }
    // From here on the gaps are the ones in force for this grid.
    const double column_gap = column_gap_used;
    const double row_gap = row_gap_used;

    // Template areas also establish explicit tracks, even when the track
    // lists omit their sizes. Those tracks use the implicit sizing pattern.
    size_t area_columns = 0;
    for (const auto& area_row : areas) area_columns = std::max(area_columns, area_row.size());
    if (!columns_are_subgrid) for (size_t i = 0; columns.size() < area_columns; ++i)
        columns.push_back(auto_columns[i % auto_columns.size()]);
    if (!rows_are_subgrid) for (size_t i = 0; rows.size() < areas.size(); ++i)
        rows.push_back(auto_rows[i % auto_rows.size()]);

    // A container with no explicit columns is one column wide, which is what
    // the initial `grid-template-columns: none` means for row-major flow.
    const bool default_implicit_column = columns.empty();
    if (default_implicit_column) columns.push_back(auto_columns.front());

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
    int explicit_columns = static_cast<int>(columns.size()) - (default_implicit_column ? 1 : 0);
    int explicit_rows = static_cast<int>(rows.size());
    if (!column_names.empty() || !areas.empty()) column_names.resize(explicit_columns + 1);
    if (!row_names.empty() || !areas.empty()) row_names.resize(explicit_rows + 1);
    std::vector<std::string_view> named_areas;
    for (const auto& row_names_in_area : areas) for (const auto& name : row_names_in_area) {
        if (name.empty() || name.front() == '.' ||
            std::find(named_areas.begin(), named_areas.end(), name) != named_areas.end()) continue;
        named_areas.push_back(name);
        int c, r, cs, rs;
        if (!area_rect(areas, name, &c, &r, &cs, &rs)) continue;
        column_names[std::min(c, explicit_columns)].push_back(name + "-start");
        column_names[std::min(c + cs, explicit_columns)].push_back(name + "-end");
        row_names[std::min(r, explicit_rows)].push_back(name + "-start");
        row_names[std::min(r + rs, explicit_rows)].push_back(name + "-end");
    }
    for (BoxId c : tree->children(container)) {
        const Box& cb = (*tree)[c];
        if (cb.kind != BoxKind::Block && cb.kind != BoxKind::AnonymousBlock) continue;
        const PositionType pos = parse_position_type(get(cb.style, kId_position));
        if (pos == PositionType::Absolute || pos == PositionType::Fixed) {
            block->layout_block(c, content_width, style);
            continue;
        }
        Pending p;
        p.box = c;
        const std::string_view area = trim(get(cb.style, kId_grid_area));
        {
            LineSpec cs, ce, rs, re;
            if (!area.empty() && !iequals(area, "auto")) {
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
                cs = parts.size() > 1 ? parse_line_spec(parts[1]) : (rs.bare_name() ? rs : LineSpec{});
                re = parts.size() > 2 ? parse_line_spec(parts[2]) : (rs.bare_name() ? rs : LineSpec{});
                ce = parts.size() > 3 ? parse_line_spec(parts[3]) : (cs.bare_name() ? cs : LineSpec{});
            } else {
                read_axis(cb.style, "grid-column", "grid-column-start", "grid-column-end",
                          &cs, &ce);
                read_axis(cb.style, "grid-row", "grid-row-start", "grid-row-end", &rs, &re);
            }
            p.col = resolve_axis(cs, ce, explicit_columns, column_names);
            p.row = resolve_axis(rs, re, explicit_rows, row_names);
        }
        pending.push_back(p);
    }

    // Implicit tracks preceding line 1 shift the explicit grid and every
    // definite item together. Auto-placement starts at the implicit grid edge.
    const auto prepend_tracks = [&](bool columns_axis, bool subgrid, std::vector<Track>& tracks,
                                    const std::vector<Track>& pattern, int& count) {
        int prefix = 0;
        for (const auto& p : pending) {
            const auto& axis = columns_axis ? p.col : p.row;
            if (axis.definite) prefix = std::max(prefix, -axis.start);
        }
        if (subgrid) {
            for (auto& p : pending) {
                auto& axis = columns_axis ? p.col : p.row;
                if (axis.definite && axis.start < 0) { axis.span = std::max(1, axis.span + axis.start); axis.start = 0; }
            }
            return;
        }
        if (!prefix) return;
        std::vector<Track> leading;
        for (int i = 0; i < prefix; ++i) {
            const int n = static_cast<int>(pattern.size());
            leading.push_back(pattern[((i - prefix) % n + n) % n]);
        }
        tracks.insert(tracks.begin(), leading.begin(), leading.end());
        count += prefix;
        for (auto& p : pending) {
            auto& axis = columns_axis ? p.col : p.row;
            if (axis.definite) axis.start += prefix;
        }
    };
    prepend_tracks(true, columns_are_subgrid, columns, auto_columns, explicit_columns);
    prepend_tracks(false, rows_are_subgrid, rows, auto_rows, explicit_rows);

    // `grid-auto-flow` (CSS Grid L1 8.5). Two independent things live in one
    // property: which axis the cursor walks, and whether it packs densely.
    //
    // The port read neither, so `grid-auto-flow: column` laid out in rows --
    // a toolbar of buttons meant to run down the side came out across the top.
    bool flow_column = false;
    bool flow_dense = false;
    {
        const std::string_view raw = get((*tree)[container].style, kId_grid_auto_flow);
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
            int c = !flow_dense && row_cursor.count(p.row.start) ? row_cursor[p.row.start] : 0;
            while (!area_free(p.row.start, c, p.row.span, p.col.span)) ++c;
            place(p, p.row.start, c);
            row_cursor[p.row.start] = c + p.col.span;
        }
        // Step 3: the implicit grid's column count — the explicit tracks, plus
        // whatever definite placements or spans reach past them.
        int column_count = std::max(1, explicit_columns);
        for (const Placement& pl : items) {
            column_count = std::max(column_count, pl.column + pl.column_span);
        }
        for (const Pending& p : pending) {
            if (p.col.definite) {
                column_count = std::max(column_count, p.col.start + p.col.span);
            } else if (!p.row.definite) {
                column_count = std::max(column_count, p.col.span);
            }
        }
        // Step 4: the cursor walks the remaining items in row-major order.
        int cursor_row = 0, cursor_col = 0;
        for (const Pending& p : pending) {
            if (p.row.definite) continue;
            if (p.col.definite) {
                if (flow_dense) cursor_row = 0;
                else if (p.col.start < cursor_col) ++cursor_row;
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

    // CSS Grid L2 §9: "the subgrid does not have implicit tracks in the
    // subgridded axis" -- an item placed past the end is clamped into the last
    // subgridded track rather than growing the grid.
    //
    // Without this, a subgrid spanning two 40px and 80px parent rows and
    // holding four items put items three and four at y=120 and y=130, below
    // the subgrid's own 120px box. Chrome stacks all the overflow in the last
    // row, at y=40. The parent's tracks are the subgrid's whole world; there
    // is nowhere else for an item to go.
    const auto clamp_into = [](std::vector<Placement>& placements, int track_count,
                               int Placement::*line, int Placement::*span) {
        if (track_count <= 0) return;
        for (Placement& p : placements) {
            if (p.*span > track_count) p.*span = track_count;
            if (p.*line + p.*span > track_count) p.*line = track_count - p.*span;
            if (p.*line < 0) p.*line = 0;
        }
    };
    if (columns_are_subgrid) {
        clamp_into(items, static_cast<int>(columns.size()), &Placement::column,
                   &Placement::column_span);
    }
    if (rows_are_subgrid) {
        clamp_into(items, static_cast<int>(rows.size()), &Placement::row,
                   &Placement::row_span);
    }

    // Implicit columns: a placement past the explicit grid adds tracks sized
    // by grid-auto-columns, cycling through its list.
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
        (*tree)[p.box].parent_layout_input = measure_parent_layout_input(*tree, p.box, content_width, ctx);
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
        const std::string_view width_raw = get(b.style, kId_width);
        const bool explicit_width = !width_raw.empty() && !iequals(width_raw, "auto") &&
                                    width_raw.find('%') == std::string_view::npos;
        if (explicit_width) {
            c.min_c = c.max_c = b.width + margins;
        } else {
            double min_c = b.parent_layout_input.min_content + frame;
            double max_c = b.parent_layout_input.max_content + frame;
            const double item_fs = b.font_size > 0 ? b.font_size : font_size;
            const double minmax_frame = is_border_box(b.style) ? 0 : frame;
            const ResolvedLength min_w =
                resolve_length(b.style, kId_min_width, ctx, item_fs, content_width);
            if (min_w.kind == LengthKind::Length) {
                min_c = std::max(min_c, min_w.pixels + minmax_frame);
                max_c = std::max(max_c, min_w.pixels + minmax_frame);
            }
            const ResolvedLength max_w =
                resolve_length(b.style, kId_max_width, ctx, item_fs, content_width);
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
                get(style, kId_justify_content));
    distribute_content(&columns, content_width, column_gap, get(style, kId_justify_content));

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

    const auto stretch_size = [&](const Box& item, double area, bool block_axis) {
        const double frame = block_axis
            ? item.padding_top + item.padding_bottom + item.border_top + item.border_bottom
            : item.padding_left + item.padding_right + item.border_left + item.border_right;
        const double margins = block_axis ? item.margin_top + item.margin_bottom
                                          : item.margin_left + item.margin_right;
        const double fs = item.font_size > 0 ? item.font_size : font_size;
        const double extra = is_border_box(item.style) ? 0 : frame;
        double size = std::max(frame, area - margins);
        const auto maximum = resolve_length(item.style, block_axis ? kId_max_height : kId_max_width, ctx, fs, area);
        const auto minimum = resolve_length(item.style, block_axis ? kId_min_height : kId_min_width, ctx, fs, area);
        if (maximum.kind == LengthKind::Length) size = std::min(size, maximum.pixels + extra);
        if (minimum.kind == LengthKind::Length) size = std::max(size, minimum.pixels + extra);
        return std::max(frame, size);
    };

    const auto size_inline = [&](const Placement& p) {
        const double w = span_size(columns, p.column, p.column_span, column_gap);
        const bool subgrid = hand_over(p, false);
        const Box& b = (*tree)[p.box];
        const std::string_view width_raw = get(b.style, kId_width);
        const bool auto_width = width_raw.empty() || iequals(width_raw, "auto");
        if (!auto_width) {
            block->layout_block(p.box, w, style);
            return;
        }
        if (subgrid) {
            block->relayout_at(p.box, w);
            return;
        }
        const double target_w = stretch_size(b, w, false);
        const std::string_view height_raw = get(b.style, kId_height);
        const bool auto_height = height_raw.empty() || iequals(height_raw, "auto");
        if (auto_height && has_ratio(b) && row_is_definite(p) &&
            is_stretch(self_alignment(b.style, style, true))) {
            // The block axis will be stretched at placement; the width follows
            // from it there. Lay out at the cell width for now so the box
            // model is resolved.
            if (std::fabs(b.width - target_w) > 1e-9) block->relayout_at(p.box, target_w);
            return;
        }
        if (is_stretch(self_alignment(b.style, style, false))) {
            if (std::fabs(b.width - target_w) > 1e-9) block->relayout_at(p.box, target_w);
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
    size_tracks(&rows, rows_available, row_gap, row_contributions, get(style, kId_align_content));
    if (rows_available < 0 && !rows.empty()) {
        double natural = 0;
        for (size_t r = 0; r < rows.size(); ++r) natural += rows[r].size + gap_after(rows, r, row_gap);
        const Box& cb = (*tree)[container];
        const double frame =
            cb.padding_top + cb.padding_bottom + cb.border_top + cb.border_bottom;
        const double own_frame = is_border_box(style) ? frame : 0;
        const ResolvedLength min_r =
            resolve_length(style, kId_min_height, ctx, font_size, std::nullopt);
        const ResolvedLength max_r =
            resolve_length(style, kId_max_height, ctx, font_size, std::nullopt);
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
                        get(style, kId_align_content));
        }
    }
    distribute_content(&rows, rows_available, row_gap, get(style, kId_align_content));

    // The auto block size is established from the first row pass. A later
    // cyclic aspect-ratio transfer can overflow it, but cannot enlarge it.
    double initial_row_extent = 0;
    for (const Track& row : rows)
        if (!row.collapsed) initial_row_extent = std::max(initial_row_extent, row.position + row.size);
    bool preserve_auto_block_size = false;

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
        const std::string_view ac = get(style, kId_align_content);
        rows_definite_by_stretch =
            rows_available >= 0 && (ac.empty() || iequals(ac, "normal") || iequals(ac, "stretch"));
        bool columns_changed = false;
        for (size_t i = 0; i < items.size() && i < column_contributions.size(); ++i) {
            const Placement& p = items[i];
            const Box& b = (*tree)[p.box];
            if (!has_ratio(b)) continue;
            // Once rows are sized, their area can change an aspect-ratio
            // item's inline intrinsic contribution even without auto-track
            // stretch (e.g. inventory cells with unequal side borders).
            if (!row_is_definite(p) && !columns_intrinsic(p)) continue;
            const std::string_view width_raw = get(b.style, kId_width);
            const std::string_view height_raw = get(b.style, kId_height);
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
            preserve_auto_block_size = rows_available < 0;
            size_tracks(&columns, content_width, column_gap, column_contributions,
                        get(style, kId_justify_content));
            distribute_content(&columns, content_width, column_gap, get(style, kId_justify_content));
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
        const std::string_view height_raw = get(before.style, kId_height);
        const bool auto_height = height_raw.empty() || iequals(height_raw, "auto");
        const std::string_view width_raw = get(before.style, kId_width);
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
                const double stretched = stretch_size(before, h, true);
                if (stretched == before.height && !subtree_reads_definite_height(*tree, p.box)) {
                    // The auto layout already has this height and nothing in
                    // it reads the height as definite, so re-laying it out
                    // would reproduce it. Doing so anyway re-measured every
                    // nested grid at every level: 2^depth layouts, and twenty
                    // nested grids took 2.2 s.
                    Box& fixed = (*tree)[p.box];
                    fixed.cross_size_imposed = true;
                } else {
                    block->relayout_at_size(p.box, before.width, stretched);
                }
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
    return preserve_auto_block_size ? initial_row_extent : bottom;
}

} // namespace weva
