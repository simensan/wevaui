#include "weva/inline_layout.h"

#include "weva/text_classes.h"

// For FloatContext, which line-box narrowing queries.
#include "weva/block_layout.h"

#include <cmath>
#include <optional>

#include "weva/css_properties.h"

#include <algorithm>
#include <memory>

namespace weva {

namespace {

// A box shrink-fitted to its content is re-laid at the SUM of its run widths,
// and the pen then re-accumulates those same widths in another order. With a
// real face the advances are arbitrary floats, and the last word can come out
// an ulp past the line and wrap. One part in a billion is below any decision
// layout makes and above any rounding it does.
constexpr double kFitEpsilon = 1e-9;

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

// The same lookup by id. A property id is resolved once for the
// program below rather than hashed from its name on every call --
// sampling put ComputedStyle::get and CssPropertyRegistry::id_of
// together at a quarter of a layout pass, ahead of any layout
// algorithm. Safe because the registry keeps an id stable across
// re-registration, which is what its header promises it for.
const int kId_max_width = CssPropertyRegistry::instance().id_of("max-width");
const int kId_min_width = CssPropertyRegistry::instance().id_of("min-width");
const int kId_color = CssPropertyRegistry::instance().id_of("color");
const int kId_margin = CssPropertyRegistry::instance().id_of("margin");
const int kId_padding = CssPropertyRegistry::instance().id_of("padding");

std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

// Resolved at static-init. The registry is a function-local static,
// so it is constructed on first use and these cannot outrun it.
const int kId__webkit_line_clamp = CssPropertyRegistry::instance().id_of("-webkit-line-clamp");
const int kId_column_gap = CssPropertyRegistry::instance().id_of("column-gap");
const int kId_display = CssPropertyRegistry::instance().id_of("display");
const int kId_flex_direction = CssPropertyRegistry::instance().id_of("flex-direction");
const int kId_flex_wrap = CssPropertyRegistry::instance().id_of("flex-wrap");
const int kId_font_family = CssPropertyRegistry::instance().id_of("font-family");
const int kId_letter_spacing = CssPropertyRegistry::instance().id_of("letter-spacing");
const int kId_line_break = CssPropertyRegistry::instance().id_of("line-break");
const int kId_line_height = CssPropertyRegistry::instance().id_of("line-height");
const int kId_margin_left = CssPropertyRegistry::instance().id_of("margin-left");
const int kId_margin_right = CssPropertyRegistry::instance().id_of("margin-right");
const int kId_overflow_wrap = CssPropertyRegistry::instance().id_of("overflow-wrap");
const int kId_position = CssPropertyRegistry::instance().id_of("position");
const int kId_tab_size = CssPropertyRegistry::instance().id_of("tab-size");
const int kId_text_align = CssPropertyRegistry::instance().id_of("text-align");
const int kId_text_indent = CssPropertyRegistry::instance().id_of("text-indent");
const int kId_text_overflow = CssPropertyRegistry::instance().id_of("text-overflow");
const int kId_white_space = CssPropertyRegistry::instance().id_of("white-space");
const int kId_width = CssPropertyRegistry::instance().id_of("width");
const int kId_word_break = CssPropertyRegistry::instance().id_of("word-break");
const int kId_word_spacing = CssPropertyRegistry::instance().id_of("word-spacing");
const int kId_word_wrap = CssPropertyRegistry::instance().id_of("word-wrap");


bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

bool is_collapsible_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}

// A token is either a run of collapsible whitespace — which becomes exactly one
// space — or a run of non-whitespace.
struct Token {
    bool is_space = false;
    std::string_view word;
};

// Fills `out` rather than returning a vector: this runs once per text segment
// on every line, and the buffer belongs to the pass.
// Whether a token holds anything that breaks between characters. Cheap: the
// answer is no for every Latin word, and the scan stops at the first byte that
// is not ASCII when it is.
bool has_cjk(std::string_view word) {
    for (std::size_t i = 0; i < word.size();) {
        if (static_cast<unsigned char>(word[i]) < 0x80) {
            ++i;
            continue;
        }
        std::size_t n = 0;
        const int cp = utf8_at(word, i, &n);
        if (is_cjk_flow_char(cp)) return true;
        i += n;
    }
    return false;
}

// The end of the piece starting at `from`: the next legal break, or the end of
// the token. A piece is one CJK character when kinsoku allows, and longer when
// it does not -- 「日 stays together because a break after an opening bracket
// is forbidden, and a Latin run inside a CJK sentence stays whole because a
// break needs CJK on BOTH sides.
std::size_t cjk_piece_end(std::string_view word, std::size_t from, LineBreakLevel level) {
    std::size_t n = 0;
    int previous = utf8_at(word, from, &n);
    std::size_t i = from + n;
    while (i < word.size()) {
        std::size_t next_len = 0;
        const int cp = utf8_at(word, i, &next_len);
        if (is_cjk_break_opportunity(previous, cp, level)) return i;
        previous = cp;
        i += next_len;
    }
    return word.size();
}

void tokenize_collapsing(std::string_view text, std::vector<Token>* out) {
    out->clear();
    size_t i = 0;
    while (i < text.size()) {
        if (is_collapsible_ws(text[i])) {
            while (i < text.size() && is_collapsible_ws(text[i])) ++i;
            out->push_back({true, {}});
        } else {
            const size_t start = i;
            while (i < text.size() && !is_collapsible_ws(text[i])) ++i;
            out->push_back({false, text.substr(start, i - start)});
        }
    }
}

// CSS 2.1 §9.4.2's qualifier: an inline box only keeps its line box alive when
// it paints an edge. Margins, padding and a STYLED border all do; `border-width`
// on its own does not, because it computes to the keyword `medium` by default
// and paints nothing while `border-style` is `none`.
bool is_zero_length(std::string_view v) {
    return v.empty() || v == "0" || v == "0px" || v == "0%" || v == "none" || v == "auto";
}

bool inline_edge_is_zero(const ComputedStyle* st) {
    if (!st) return true;
    static const char* kEdges[] = {"margin-left", "margin-right",  "padding-left",
                                   "padding-right", "padding-top", "padding-bottom"};
    for (const char* p : kEdges) {
        if (!is_zero_length(st->get(p))) return false;
    }
    static const char* kBorderStyle[] = {"border-left-style", "border-right-style",
                                         "border-top-style", "border-bottom-style"};
    static const char* kBorderWidth[] = {"border-left-width", "border-right-width",
                                         "border-top-width", "border-bottom-width"};
    for (int i = 0; i < 4; ++i) {
        const std::string_view style = st->get(kBorderStyle[i]);
        if (style.empty() || style == "none" || style == "hidden") continue;
        if (!is_zero_length(st->get(kBorderWidth[i]))) return false;
    }
    return true;
}

double letter_spacing_px(const ComputedStyle* style, const LayoutContext& ctx, double font_size) {
    const std::string_view raw = get(style, kId_letter_spacing);
    if (raw.empty() || iequals(raw, "normal")) return 0;
    const ResolvedLength r = resolve_length(style, kId_letter_spacing, ctx, font_size, std::nullopt);
    if (r.kind == LengthKind::Length) return r.pixels;
    // A percentage is of the font size (css-text-4), the same reading the
    // reference takes.
    if (r.kind == LengthKind::Percent) return font_size * r.percent * 0.01;
    return 0;
}

// UTF-16 code units, which is what the reference's string.Length counts: an
// astral-plane emoji is two, everything else one. A 32px glyph with 0.01em
// spacing measured 0.32px wider there than a code-point count gives.
int letter_count(std::string_view text) {
    int n = 0;
    for (unsigned char c : text) {
        if ((c & 0xC0) == 0x80) continue;
        n += (c & 0xF8) == 0xF0 ? 2 : 1;
    }
    return n;
}

// The reference measures a whole run as width + spacing × (characters − 1),
// spaces included. A run is placed here piece by piece, so every piece adds
// spacing × characters and the run's FIRST piece adds one less — the sum is
// the same however the run breaks. Counting per piece with (n − 1) each
// dropped one spacing per space: "LV 7" at 0.16em came out 6.4px short.
double measure_spaced(const FontMetrics& default_metrics, std::string_view text,
                      const InlineItem& it, bool first_piece_of_item) {
    const FontMetrics& fm = it.metrics ? *it.metrics : default_metrics;
    double w = fm.measure(text, it.font_size);
    if (it.letter_spacing != 0) {
        const int n = letter_count(text) - (first_piece_of_item ? 1 : 0);
        if (n > 0) w += it.letter_spacing * static_cast<double>(n);
    }
    return w;
}

// CSS Text L3 7.2 `tab-size`, as a COUNT OF SPACES. A plain number is that
// count; a length is converted through the space's own width, since a tab stop
// is only ever expressed here in spaces.
//
// A tab in preserved text had no width control at all before this: `\t` was
// measured as whatever the face gives it, which for the built-in one is
// nothing, so a code listing in a <pre> lost every level of its indentation.
double tab_size_spaces(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                       double space_width) {
    const std::string_view raw = get(style, kId_tab_size);
    if (raw.empty()) return 8;
    // A bare number first: `tab-size: 4` is the common form and is NOT a
    // length.
    const std::string text(raw);
    char* end = nullptr;
    const double number = std::strtod(text.c_str(), &end);
    if (end != text.c_str() && (*end == '\0' || *end == ' ')) {
        return number > 0 ? number : 8;
    }
    const ResolvedLength r = resolve_length(raw, ctx, font_size, font_size);
    if (r.kind == LengthKind::Length && r.pixels > 0 && space_width > 0) {
        return r.pixels / space_width;
    }
    return 8;
}

// CSS Text L3 8.1 `word-spacing`, in pixels. `normal` is zero extra; a
// percentage resolves against the font size, as the reference has it.
double word_spacing_px(const ComputedStyle* style, const LayoutContext& ctx, double font_size) {
    const std::string_view raw = get(style, kId_word_spacing);
    if (raw.empty() || iequals(raw, "normal")) return 0;
    const ResolvedLength r = resolve_length(raw, ctx, font_size, font_size);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return font_size * r.percent * 0.01;
    return 0;
}

// CSS Text L3 7.1 `text-indent`, in pixels: the first line's leading offset.
// A percentage is of the containing block's width. The trailing `hanging` and
// `each-line` keywords are accepted and ignored -- neither semantic is
// implemented, and dropping the whole declaration over them would lose the
// indent an author did write, which is the reference's reasoning too.
double text_indent_px(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                      double containing_width) {
    std::string_view raw = get(style, kId_text_indent);
    if (raw.empty()) return 0;
    // Take the leading term; the rest is keywords or nothing.
    const std::size_t space = raw.find(' ');
    if (space != std::string_view::npos) raw = raw.substr(0, space);
    if (iequals(raw, "hanging") || iequals(raw, "each-line")) return 0;
    const ResolvedLength r = resolve_length(raw, ctx, font_size, containing_width);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return containing_width * r.percent * 0.01;
    return 0;
}

// The face a run measures with: the family's registered metrics, then the
// weight/italic variant of it when the host provides one.
const FontMetrics* metrics_for_style(const LayoutContext& ctx, const ComputedStyle* style) {
    const FontMetrics* base = ctx.font_for(get(style, kId_font_family));
    if (!ctx.variant_metrics) return base;
    const int weight = resolve_font_weight(style);
    const bool italic = resolve_font_italic(style);
    if (weight < 600 && !italic) return base;
    const FontMetrics* v = ctx.variant_metrics(ctx.variant_user, base, weight, italic);
    return v ? v : base;
}

void collect_recursive(const BoxTree& tree, BoxId node, BoxId inline_parent,
                       const LayoutContext& ctx, const ComputedStyle* inherited,
                       const ComputedStyle* inherited_parent, const FontMetrics* metrics,
                       std::vector<InlineItem>* out) {
    for (BoxId c : tree.children(node)) {
        const Box& b = tree[c];
        if (b.kind == BoxKind::Text) {
            InlineItem item;
            item.source_run = c;
            item.inline_parent = inline_parent;
            item.text = b.text;
            item.is_list_marker_outside = b.is_list_marker_outside;
            item.style = b.style ? b.style : inherited;
            // A text run's style is its element's, so `em` resolves against
            // that element's PARENT — `<small>` (0.83em) inside a 14px label is
            // 11.62px, not 0.83 of the root.
            item.font_size = font_size_px(item.style, inherited_parent, ctx);
            item.metrics = metrics_for_style(ctx, item.style);
            item.line_height = line_height_px(item.style, item.font_size, ctx,
                                              item.metrics ? item.metrics : metrics);
            item.letter_spacing = letter_spacing_px(item.style, ctx, item.font_size);
            const std::string_view ws = get(item.style, kId_white_space);
            // `pre` and `pre-wrap` preserve whitespace; `nowrap` and `pre`
            // forbid wrapping. Only the two axes matter to layout, so they are
            // decomposed here rather than carried as a keyword.
            item.collapse_whitespace = !(iequals(ws, "pre") || iequals(ws, "pre-wrap") ||
                                         iequals(ws, "break-spaces"));
            item.allow_wrap = !(iequals(ws, "nowrap") || iequals(ws, "pre"));
            item.preserve_newlines = iequals(ws, "pre") || iequals(ws, "pre-wrap") ||
                                     iequals(ws, "pre-line") || iequals(ws, "break-spaces");
            const std::string_view wb = get(item.style, kId_word_break);
            const std::string_view ow = get(item.style, kId_overflow_wrap);
            // `anywhere` differs from `break-all` only in how it affects
            // min-content sizing, which is not tracked yet, so the two are
            // observably identical here — the same simplification the
            // reference makes, and it says so.
            item.break_anywhere =
                item.allow_wrap && (iequals(wb, "break-all") || iequals(ow, "anywhere"));
            // CSS Text L3 5.5: `break-word` on either property. Both spellings,
            // because `word-wrap: break-word` is the older name and still the
            // one in most stylesheets.
            item.break_word = item.allow_wrap && !item.break_anywhere &&
                              (iequals(ow, "break-word") || iequals(wb, "break-word") ||
                               iequals(get(item.style, kId_word_wrap), "break-word"));
            // `line-break` decides which kinsoku prohibitions apply between
            // CJK characters -- whether a small kana may start a line.
            item.line_break = line_break_level(get(item.style, kId_line_break));
            // CSS Text L3 8.1: extra space added at each word separator, on
            // top of the space's own advance. Unread until now, so a heading
            // set with `word-spacing: 4px` came out at its natural spacing.
            item.word_spacing = word_spacing_px(item.style, ctx, item.font_size);
            // Only preserved text can contain a tab: a collapsing run turns
            // one into a single space long before it reaches layout.
            if (!item.collapse_whitespace) {
                const FontMetrics& fm = item.metrics ? *item.metrics : *metrics;
                const double space_w = fm.measure(" ", item.font_size);
                item.tab_spaces = tab_size_spaces(item.style, ctx, item.font_size, space_w);
            }
            out->push_back(item);
        } else if (b.kind == BoxKind::Inline && b.element &&
                   b.element->tag_name() == "br") {
            // HTML §14.3.3: `br` is an inline element with no content that
            // forces a line break. Recursing into it the way any other inline
            // box is recursed into finds nothing and the break is lost.
            InlineItem item;
            item.break_box = c;
            item.inline_parent = inline_parent;
            item.style = b.style ? b.style : inherited;
            item.font_size = font_size_px(item.style, inherited, ctx);
            item.metrics = metrics_for_style(ctx, item.style);
            item.line_height = line_height_px(item.style, item.font_size, ctx,
                                              item.metrics ? item.metrics : metrics);
            out->push_back(item);
        } else if (b.kind == BoxKind::Inline || b.kind == BoxKind::AnonymousInline) {
            // CSS 2.1 §9.4.2: an inline element produces a box on every line it
            // covers. A marker records where it starts so a box that ends up
            // with no fragments of its own is still placed.
            ResolvedSides pad, bor, mar;
            if (b.kind == BoxKind::Inline && b.style) {
                const double fs = font_size_px(b.style, inherited, ctx);
                const double lh = line_height_px(b.style, fs, ctx, metrics);
                // Percentages of the containing block's width: the block
                // container's, which is what the box tree's ancestor chain
                // reaches through the container box.
                pad = resolve_box_sides_px(b.style, kId_padding, ctx, fs, 0, lh);
                mar = resolve_box_sides_px(b.style, kId_margin, ctx, fs, 0, lh);
                bor = resolve_border_edges(b.style, ctx, fs);
            }
            if (b.kind == BoxKind::Inline) {
                InlineItem item;
                item.inline_box_start = c;
                item.inline_parent = inline_parent;
                item.style = b.style ? b.style : inherited;
                item.font_size = font_size_px(item.style, inherited, ctx);
                item.margin_edge = mar.left;
                item.decoration = bor.left + pad.left;
                item.metrics = metrics_for_style(ctx, item.style);
                item.line_height = line_height_px(item.style, item.font_size, ctx,
                                                  item.metrics ? item.metrics : metrics);
                out->push_back(item);
            }
            collect_recursive(tree, c, c, ctx, b.style ? b.style : inherited,
                              b.style ? inherited : inherited_parent, metrics, out);
            if (b.kind == BoxKind::Inline) {
                InlineItem item;
                item.inline_box_end = c;
                item.inline_parent = inline_parent;
                item.style = b.style ? b.style : inherited;
                item.font_size = font_size_px(item.style, inherited, ctx);
                item.margin_edge = mar.right;
                item.decoration = bor.right + pad.right;
                out->push_back(item);
            }
        } else if (b.kind == BoxKind::Block && b.is_inline_block) {
            // An atom: placed whole, never broken. It is recorded here but not
            // sized — sizing it needs the block layout engine, so the caller
            // fills in the width and baseline before layout runs.
            InlineItem item;
            item.atom_box = c;
            item.inline_parent = inline_parent;
            item.style = b.style ? b.style : inherited;
            item.font_size = font_size_px(item.style, inherited, ctx);
            item.metrics = metrics_for_style(ctx, item.style);
            item.line_height = line_height_px(item.style, item.font_size, ctx,
                                              item.metrics ? item.metrics : metrics);
            // CSS Text L3 3: whether a line may break BETWEEN two inline-level
            // boxes is the containing block's business, not the atom's -- and
            // `nowrap` there forbids it. Without this a row of cards under
            // `white-space: nowrap` stacked into a column the moment it grew
            // past its box, instead of overflowing it to be scrolled, which is
            // exactly what such a row is for.
            const std::string_view ws = get(inherited ? inherited : item.style, kId_white_space);
            item.allow_wrap = !(iequals(ws, "nowrap") || iequals(ws, "pre"));
            out->push_back(item);
        }
    }
}

} // namespace

std::string_view resolve_text_align(const ComputedStyle* style) {
    std::string_view t = get(style, kId_text_align);
    if (t.empty()) t = "start";
    const bool rtl = is_rtl(style);
    if (iequals(t, "start")) return rtl ? "right" : "left";
    if (iequals(t, "end")) return rtl ? "left" : "right";
    return t;
}

std::vector<InlineItem> collect_inline_items(const BoxTree& tree, BoxId container,
                                             const LayoutContext& ctx,
                                             const FontMetrics* metrics) {
    std::vector<InlineItem> out;
    const Box& cb = tree[container];
    // The container's own parent style: an anonymous block has none of its
    // own and its text belongs to the parent element, so the parent's parent
    // is the right `em` basis there.
    const ComputedStyle* container_parent =
        cb.parent != kNoBox ? tree[cb.parent].style : nullptr;
    collect_recursive(tree, container, kNoBox, ctx, cb.style ? cb.style : container_parent,
                      cb.style ? container_parent
                               : (cb.parent != kNoBox && tree[cb.parent].parent != kNoBox
                                      ? tree[tree[cb.parent].parent].style
                                      : nullptr),
                      metrics, &out);
    return out;
}
// ---- text-overflow: ellipsis (CSS Text Overflow L3) ----------------------
//
// A single line that overflows its box is cut and given a "..." rather than
// running past the edge. The port read `text-overflow` nowhere at all, so a
// fixed-width label with a long value simply spilled -- or, inside a clipping
// box, was sliced mid-letter.
//
// The conditions are the reference's, and they are the spec's: `text-overflow:
// ellipsis`, `white-space: nowrap` (a line that can wrap does not overflow in
// the first place), and an inline axis that clips.
bool wants_ellipsis(const ComputedStyle* style) {
    if (!style) return false;
    if (!iequals(get(style, kId_text_overflow), "ellipsis")) return false;
    if (!iequals(get(style, kId_white_space), "nowrap")) return false;
    for (const char* prop : {"overflow-x", "overflow"}) {
        const std::string_view v = get(style, prop);
        if (iequals(v, "hidden") || iequals(v, "scroll") || iequals(v, "clip") ||
            iequals(v, "auto")) {
            return true;
        }
    }
    return false;
}

// How many lines `-webkit-line-clamp` allows, or 0 for no clamp.
//
// The PREFIXED property only, and only inside a `-webkit-box`, because that is
// what Chrome implements. Unprefixed `line-clamp` it ignores outright -- the
// oracle case shows Chrome leaving a `line-clamp: 2` block at its full 130px
// while the C# reference clamps it to 50 -- so implementing that here would
// mean matching the reference by diverging from the browser.
int line_clamp_of(const ComputedStyle* style) {
    if (!style) return 0;
    const std::string_view display = get(style, kId_display);
    if (!iequals(display, "-webkit-box") && !iequals(display, "-webkit-inline-box")) return 0;
    std::string_view raw = get(style, kId__webkit_line_clamp);
    while (!raw.empty() && (raw.front() == ' ' || raw.front() == '\t')) raw.remove_prefix(1);
    while (!raw.empty() && (raw.back() == ' ' || raw.back() == '\t')) raw.remove_suffix(1);
    if (raw.empty() || iequals(raw, "none")) return 0;
    int n = 0;
    for (char c : raw) {
        if (c < '0' || c > '9') return 0;
        n = n * 10 + (c - '0');
        if (n > 1000000) return 0;
    }
    return n;
}

// Cuts the line down so that what is left plus the ellipsis fits, and drops
// every run after the cut.
void truncate_line_with_ellipsis(BoxTree* tree, BoxId line, double content_width,
                                 const LayoutContext& ctx, const FontMetrics& fallback) {
    // Written as BYTES, not as a character: this literal went through a
    // rewrite that double-encoded it once already, and one that silently
    // becomes three Latin-1 characters measures three times too wide
    // without ever looking wrong in the source.
    static constexpr std::string_view kEllipsis = "…";   // U+2026
    std::vector<BoxId> runs;
    for (BoxId c : tree->children(line)) runs.push_back(c);
    if (runs.empty()) return;

    // The last run's face draws the ellipsis, as it is the one the eye was
    // following when the text ran out.
    const Box& last = (*tree)[runs.back()];
    const FontMetrics* fm = metrics_for_style(ctx, last.style);
    const FontMetrics& metrics = fm ? *fm : fallback;
    const double font_size = last.font_size > 0 ? last.font_size : 16;
    const double ellipsis_width = metrics.measure(kEllipsis, font_size);
    const double budget = std::max(0.0, content_width - ellipsis_width);

    for (std::size_t i = 0; i < runs.size(); ++i) {
        Box& r = (*tree)[runs[i]];
        if (r.x + r.width <= budget + 1e-9) continue;

        // This run crosses the budget. Keep the longest prefix of it that
        // still fits, never splitting a character.
        const FontMetrics* rm = metrics_for_style(ctx, r.style);
        const FontMetrics& run_metrics = rm ? *rm : fallback;
        const double run_size = r.font_size > 0 ? r.font_size : font_size;
        std::string_view text = r.text;
        std::size_t keep = 0;
        double kept_width = 0;
        while (keep < text.size()) {
            std::size_t next = keep + 1;
            while (next < text.size() &&
                   (static_cast<unsigned char>(text[next]) & 0xC0) == 0x80) {
                ++next;
            }
            const double w = run_metrics.measure(text.substr(0, next), run_size);
            if (r.x + w > budget) break;
            kept_width = w;
            keep = next;
        }
        // The text is not owned by the box, so the truncated form plus the
        // ellipsis has to live somewhere that outlives this call. The tree's
        // arena is where every other generated string goes.
        std::string cut(text.substr(0, keep));
        cut += kEllipsis;
        r.text = tree->own_text(std::move(cut));
        r.width = kept_width + ellipsis_width;

        // Everything after it is gone: it was past the edge anyway.
        for (std::size_t j = runs.size(); j-- > i + 1;) tree->remove_child(runs[j]);
        return;
    }
}


double layout_inline(BoxTree* tree, BoxId container, double available_width,
                     const LayoutContext& ctx, const FontMetrics& metrics) {
    return layout_inline_items(tree, container,
                               collect_inline_items(*tree, container, ctx, &metrics),
                               available_width, ctx, metrics);
}

namespace {

// One fragment of text placed on the line being built.
struct Fragment {
    const InlineItem* item;
    std::string_view text;
    bool is_space;
    double x;
    double width;
};

// One inline box's extent on the line being flushed.
struct Span {
    BoxId box;
    BoxId fragment;
    double x0;
    double x1;
    bool covers_content;
};

// The buffers one call to layout_inline_items works in.
//
// All of them were locals, so every inline formatting context built its
// vectors from nothing and freed them again -- and three of them are per LINE,
// not per call. That was the largest remaining source of heap traffic in a
// layout pass: vendor.html's 8,162 boxes cost 18,961 allocations, better than
// a third of them here, against a plan that asks for none.
//
// Nothing here outlives the call, so the buffers are borrowed and handed back
// with their capacity, and the second pass over a document allocates nothing
// for them at all.
struct InlineScratch {
    std::vector<Fragment> line;
    std::vector<BoxId> line_boxes;
    std::vector<BoxId> attached_inlines;
    std::vector<BoxId> boxes_with_content;
    std::vector<Fragment> trailing_markers;
    std::vector<Fragment> carried;
    std::vector<Span> spans;
    std::vector<Token> tokens;

    void reset() {
        line.clear();
        line_boxes.clear();
        attached_inlines.clear();
        boxes_with_content.clear();
        trailing_markers.clear();
        carried.clear();
        spans.clear();
        tokens.clear();
    }
};

// A pool rather than one buffer set: an inline-block atom is laid out while its
// container's own line is being built, so a call can be inside another one.
// Thread-local, so two documents laying out at once do not share it.
std::vector<std::unique_ptr<InlineScratch>>& scratch_pool() {
    thread_local std::vector<std::unique_ptr<InlineScratch>> pool;
    return pool;
}

class ScratchLease {
public:
    ScratchLease() {
        auto& pool = scratch_pool();
        if (pool.empty()) {
            owned_ = std::make_unique<InlineScratch>();
        } else {
            owned_ = std::move(pool.back());
            pool.pop_back();
        }
        owned_->reset();
    }
    ~ScratchLease() {
        // Capacity is the point, so the buffers go back full-sized; reset()
        // empties them when the next call borrows them.
        scratch_pool().push_back(std::move(owned_));
    }
    ScratchLease(const ScratchLease&) = delete;
    ScratchLease& operator=(const ScratchLease&) = delete;
    InlineScratch& operator*() const { return *owned_; }

private:
    std::unique_ptr<InlineScratch> owned_;
};

}   // namespace

double layout_inline_items(BoxTree* tree, BoxId container,
                           const std::vector<InlineItem>& items, double available_width,
                           const LayoutContext& ctx, const FontMetrics& metrics,
                           const InlineFloatEnv* float_env) {
    const Box& cbox = (*tree)[container];
    const double top_inner = cbox.padding_top + cbox.border_top;
    const double left_inner = cbox.padding_left + cbox.border_left;
    // CSS 2.1 §9.2.1.1 again: an anonymous block has no style of its own and
    // inherits from its parent, so `text-align` is read off the parent when the
    // container is anonymous — the same fallback line-height takes below. Read
    // off the null style it resolved to `start`, and an inline-flex pill that
    // shared its right-aligned parent with a block sibling (so it sat in an
    // anonymous block) was flushed left.
    const std::string_view align = resolve_text_align(
        cbox.style ? cbox.style : (cbox.parent != kNoBox ? (*tree)[cbox.parent].style : nullptr));
    // Copied out, not read through `cbox`: BoxTree::create appends to a vector,
    // so every box reference is invalidated by the next create — and flush_line
    // creates one line box plus one run per fragment. `cbox` stays valid only
    // until the first line is flushed, which is why the second line of a
    // container that had grown the vector was reading freed memory. The style
    // pointer is owned outside the tree, so copying it is safe.
    //
    // Everything else below re-indexes; nothing holds a Box& across a create.
    const ComputedStyle* const container_style = cbox.style;

    // CSS 2.1 §10.8.1: half-leading is SIGNED. A line-height smaller than the
    // font's own ascent+descent gives a negative half-leading and a line box
    // shorter than its content, with the glyphs overflowing it — it does not
    // clamp back up to the metric height.
    //
    // Taking max(content, leading) instead made `line-height: 1` on a 16px font
    // indistinguishable from `normal`, which the oracle caught across most of
    // the corpus. The C# fixed the same bug once and left a comment saying so.
    //
    // The override is keyed on the CONTAINER declaring line-height, not on the
    // per-item values, because that is what the reference does: it lays lines
    // out at their natural metric height and then overrides them in a pass over
    // the container's children.
    // CSS 2.1 §9.2.1.1: an anonymous box inherits inheritable properties from
    // its parent. Anonymous blocks are created with a null style, so reading
    // line-height off the container alone missed the author's value and fell
    // back to the metric height — a nested list item came out 18.29 tall where
    // `line-height: 1` on body asks for 16. The reference falls back the same
    // way, and says so at the same spot.
    const ComputedStyle* const line_height_style =
        cbox.style ? cbox.style
                   : (cbox.parent != kNoBox ? (*tree)[cbox.parent].style : nullptr);
    std::optional<double> declared_line_height;
    if (line_height_style) {
        const std::string_view raw = get(line_height_style, kId_line_height);
        if (!raw.empty() && !iequals(raw, "normal")) {
            const double container_fs =
                font_size_px(line_height_style,
                             cbox.parent != kNoBox ? (*tree)[cbox.parent].style : nullptr, ctx);
            declared_line_height = line_height_px(line_height_style, container_fs, ctx, &metrics);
        }
    }

    // CSS 2.1 §10.8: every line box contains a "strut" — a zero-width inline
    // box with the containing block's font and line-height. Nothing here
    // created one, so a line holding ONLY an inline-block came out exactly the
    // atom's height: the block's own text descent below the atom's baseline
    // went missing. A `<button>` whose label wraps to two lines inside an
    // explicit 40px height has its baseline (its LAST line's) at its bottom
    // edge, so the strut's descent is all that sits below — Chrome puts that
    // line at 44.56 and this produced 40, which is menu.html's `.card` coming
    // out 108.57 against the reference's and Chrome's 113.
    const FontMetrics* const strut_metrics =
        line_height_style ? metrics_for_style(ctx, line_height_style) : nullptr;
    const FontMetrics& strut_fm = strut_metrics ? *strut_metrics : metrics;
    const double strut_font_size =
        line_height_style
            ? font_size_px(line_height_style,
                           cbox.parent != kNoBox ? (*tree)[cbox.parent].style : nullptr, ctx)
            : ctx.root_font_size_px;
    const double strut_ascent = strut_fm.ascent(strut_font_size);
    const double strut_descent = strut_fm.descent(strut_font_size);
    const double strut_leading =
        declared_line_height ? *declared_line_height : strut_fm.line_height(strut_font_size);

    // An atom contributes above- and below-baseline extents like a glyph does,
    // so the line grows around it instead of clipping it.
    ScratchLease lease;
    InlineScratch& scratch = *lease;
    std::vector<Fragment>& line = scratch.line;
    std::vector<BoxId>& line_boxes = scratch.line_boxes;
    const int line_clamp = line_clamp_of(container_style);
    int lines_flushed = 0;
    double clamp_bottom = 0;

    double y = top_inner;
    double pen = 0;
    double max_ascent = 0, max_descent = 0, max_leading = 0;
    // CSS Text L3 §8.2, and the shape the reference measures in. A run's width
    // is `text + spacing x (characters - 1)` — the spacing sits BETWEEN
    // characters, so a run of n characters carries n-1 of them. Placing a run
    // piece by piece reproduces that by charging n per piece and n-1 for the
    // run's first piece.
    //
    // A LINE BREAK restarts that count: the piece opening the next line is a
    // first piece again, because the spacing that would have followed the last
    // character of the previous line has nowhere to sit. Carrying the flag
    // across the break charged one spacing too many for every wrapped run —
    // dialogue.html's `<strong>` sat 0.16 to the right, which at its 16px font
    // and `letter-spacing: 0.01em` is exactly one.
    bool first_piece = true;

    // CSS 2.1 §9.5: a line box beside a float is shortened to make room for it.
    // This has to be known while the line is being FILLED, not only when it is
    // flushed — the wrap decision compares against it — so it is recomputed
    // whenever a line starts.
    // Inline boxes already given their first fragment; a second line covering
    // the same box clones it rather than moving it.
    std::vector<BoxId>& attached_inlines = scratch.attached_inlines;

    // Inline boxes that enclose some content somewhere in the stream. A box
    // that opens at the very end of a line and whose text wraps gets NO
    // fragment on that line — its first box is where its content is, which is
    // what the reference emits. A box with no content on ANY line (the
    // block-in-inline `<a>`) still gets its zero-width fragment where it
    // opens. Computed here, while the ancestor chain is still intact.
    std::vector<BoxId>& boxes_with_content = scratch.boxes_with_content;
    for (const InlineItem& it : items) {
        if (it.is_marker() || it.is_break()) continue;
        for (BoxId b = it.inline_parent; b != kNoBox && b != container; b = (*tree)[b].parent) {
            if ((*tree)[b].kind != BoxKind::Inline) break;
            if (std::find(boxes_with_content.begin(), boxes_with_content.end(), b) ==
                boxes_with_content.end()) {
                boxes_with_content.push_back(b);
            }
        }
    }

    double line_left = 0;
    double line_width = available_width;
    const auto begin_line_at = [&](double line_y) {
        line_left = 0;
        line_width = available_width;
        if (!float_env || !float_env->floats) return;
        const double bfc_y = float_env->bfc_content_top + (line_y - top_inner);
        const double left_in = float_env->floats->left_extent_at(bfc_y);
        const double right_in = float_env->floats->right_extent_at(bfc_y, available_width);
        line_left = left_in;
        line_width = available_width - left_in - right_in;
        // A float wider than the containing block leaves nothing; the line then
        // holds one overflowing word rather than looping forever on a zero
        // width.
        if (line_width < 0) line_width = 0;
    };
    begin_line_at(y);
    // CSS Text L3 7.1: the FIRST line starts inset. Applied after
    // begin_line_at so a float's own inset is not lost, and only once --
    // every later line starts at the float edge as before.
    {
        const ComputedStyle* container_style =
            tree->valid(container) ? (*tree)[container].style : nullptr;
        const double indent = text_indent_px(container_style, ctx,
                                             font_size_px(container_style, nullptr, ctx),
                                             available_width);
        if (indent != 0) {
            line_left += indent;
            line_width -= indent;
            if (line_width < 0) line_width = 0;
        }
    }

    const auto reset_line_metrics = [&] {
        // Seeded with the strut, not with zero: the containing block's own
        // font is present on every line whether or not any text lands there.
        max_ascent = strut_ascent;
        max_descent = strut_descent;
        max_leading = strut_leading;
    };
    // Markers are not content: a line holding only the opening of an inline
    // box is still at its start for the purposes of dropping a leading
    // collapsible space and of the wrap decision.
    const auto line_has_content = [&] {
        for (const Fragment& f : line) {
            if (!f.item->is_marker()) return true;
        }
        return false;
    };

    // Emits the fragments collected so far as one LineBox with TextRun children.
    const auto flush_line = [&](bool is_final) {
        // Trailing collapsible spaces do not occupy the end of a line — they
        // would otherwise push the alignment of every centred or right-aligned
        // line by a space width.
        // Zero-width inline-box markers ride along: a marker sitting after a
        // trailing space must end up at the TRIMMED pen, not keep the position
        // the removed space had pushed it to.
        std::vector<Fragment>& trailing_markers = scratch.trailing_markers;
        trailing_markers.clear();
        double trimmed_space = 0;
        while (!line.empty()) {
            if (line.back().item->is_marker()) {
                // Start AND end markers ride along: `Click <a>` at a line's
                // end trims the space and puts the empty box at 76, not 83,
                // whether or not the box's end marker follows its start.
                trailing_markers.push_back(line.back());
                pen -= line.back().width;
                line.pop_back();
                continue;
            }
            if (line.back().is_space) {
                pen -= line.back().width;
                trimmed_space += line.back().width;
                line.pop_back();
                continue;
            }
            break;
        }
        // A marker left dangling at the END of a line whose box has content
        // further on belongs to the next line: the box's first fragment is
        // where its content is, which is what the reference emits. Only a
        // box with no content anywhere keeps its zero-width fragment here.
        std::vector<Fragment>& carried = scratch.carried;
        carried.clear();
        for (auto it = trailing_markers.rbegin(); it != trailing_markers.rend(); ++it) {
            Fragment m = *it;
            const bool has_content_later =
                m.item->is_inline_start() &&
                std::find(boxes_with_content.begin(), boxes_with_content.end(),
                          m.item->inline_box_start) != boxes_with_content.end();
            if (!is_final && has_content_later) {
                carried.push_back(m);
                continue;
            }
            m.x = pen;
            pen += m.width;
            line.push_back(m);
        }
        // A line holding nothing but inline-box markers still IS a line — an
        // empty `<span></span>` gives its container a line box, and so the
        // strut's height — but it gets no fragment boxes. An inline box earns a
        // fragment by covering content on the line; one on a line with no
        // content at all covers nothing.
        //
        // The two halves are both load-bearing and pull opposite ways. Drop the
        // line and `<div><section><span></span></section></div>` loses the
        // section's 18.29 height; keep the fragment and the same markup reports
        // three elements where the reference reports two. Emit an empty `<a>`
        // on a line that DOES have content, though, and it must appear — that
        // is the block-in-inline case in 23-inline-splitting.
        bool only_markers = !line.empty();
        for (const Fragment& f : line) {
            if (!f.item->is_marker()) { only_markers = false; break; }
        }

        // CSS 2.1 §9.2.1.1: the empty anonymous blocks a block-in-inline split
        // leaves on either side of the block are NOT generated. The fragment
        // box still has to exist — the element is there, and paint and hit
        // testing walk it — so the line is emitted at ZERO height rather than
        // dropped, which is also what the reference produces:
        // `<span><div>block</div></span>` gives the span a zero-height fragment
        // and a container exactly as tall as the block. Without this the port
        // made that container THREE line-heights tall (an empty line, the
        // block, another empty line) where Chrome and the reference both say
        // one, and card-component.html's whole page sat 18.29 too low.
        //
        // Deliberately NOT the same as an empty `<span></span>` the author
        // wrote, which does form a line of strut height; `is_split_fragment` is
        // what tells the manufactured fragments apart.
        // Of those, the trailing one — the only empty fragment that earns a
        // box (see the fragment loop below).
        bool emits_trailing_split_fragment = false;
        // CSS 2.1 §9.4.2: a line box holding no text, no preserved whitespace
        // and no inline element with non-zero margins, padding or borders is
        // treated as ZERO-HEIGHT. So a line carrying only empty inline boxes
        // collapses whether or not a split manufactured them — Chrome gives
        // `<div><span></span></div>`, `<div><span> </span></div>` and a nested
        // empty pair a height of 0 alike. The edge test is the spec's own
        // qualifier: an inline that paints a margin, padding or border keeps
        // its line.
        bool only_edgeless_inlines = only_markers;
        if (only_edgeless_inlines) {
            for (const Fragment& f : line) {
                const BoxId b = f.item->is_inline_start() ? f.item->inline_box_start
                                                          : f.item->inline_box_end;
                if (b == kNoBox || !inline_edge_is_zero((*tree)[b].style)) {
                    only_edgeless_inlines = false;
                    break;
                }
            }
        }

        bool only_empty_split_fragments = only_markers;
        if (only_empty_split_fragments) {
            for (const Fragment& f : line) {
                const BoxId b = f.item->is_inline_start() ? f.item->inline_box_start
                                                          : f.item->inline_box_end;
                // Emptiness is judged by what reached THIS LINE, not by the
                // fragment's child list: a split piece routinely holds a
                // whitespace text node (a cloned template body brings its own
                // indentation with it), and that whitespace collapses away at
                // the line's start so the fragment still covers nothing.
                // Testing first_child instead left card-component.html's page
                // a line-height low.
                if (b == kNoBox || !(*tree)[b].is_split_fragment) {
                    only_empty_split_fragments = false;
                    emits_trailing_split_fragment = false;
                    break;
                }
                if ((*tree)[b].is_last_split_fragment) emits_trailing_split_fragment = true;
            }
        }

        if (line.empty() && !is_final) {
            reset_line_metrics();
            pen = 0;
            for (Fragment& m : carried) { m.x = pen; pen += m.width; line.push_back(m); }
            return;
        }
        if (line.empty()) return;

        // The line's height is the tallest content on it, and its baseline the
        // deepest ascent — so a taller span pushes the whole line down rather
        // than overlapping the one above.
        const double content_height = max_ascent + max_descent;
        const double natural_height = std::max(content_height, max_leading);
        // Half-leading is split evenly above and below, which is what keeps a
        // line-height larger than the text centred on it — and, when the
        // declared line-height is smaller than the content, pulls it up.
        const double natural_baseline = (natural_height - content_height) * 0.5 + max_ascent;
        const double line_height = (only_empty_split_fragments || only_edgeless_inlines)
                                       ? 0.0
                                       : declared_line_height.value_or(natural_height);
        const double baseline = natural_baseline + (line_height - natural_height) * 0.5;

        // CSS Lists L3 §3.2: an `outside` marker -- the initial value, and so
        // nearly every marker -- sits in the area BEFORE the content edge and
        // takes no inline space. The port builds the marker as a text run at
        // the start of the item's content, which is what let it be read as
        // text; what it must not do is push the content along.
        //
        // Every inline child of every list item was shifted right by the
        // marker's advance: a <span>, an <input> or an <img> in an <li> came
        // out at x=54.4 where both Chrome and the reference put it at 40.
        // Nothing caught it because a list item holding only TEXT has no
        // element after the marker to dump, and every gated sample's items
        // hold only text.
        //
        // The marker keeps its width -- it still draws -- and moves to its
        // own left of the content edge. `inside` markers are not flagged and
        // stay in the flow, which is exactly what `inside` means.
        // ALL the marker's fragments, not the first: "1. " tokenises into the
        // number and the space that follows it, so shifting by one fragment's
        // width moved the content half way and looked like a rounding bug.
        double marker_width = 0;
        size_t marker_first = line.size();
        for (size_t k = 0; k < line.size(); ++k) {
            if (!line[k].item->is_list_marker_outside) continue;
            if (marker_first == line.size()) marker_first = k;
            marker_width += line[k].width;
        }
        if (marker_first < line.size() && marker_width > 0) {
            // From the marker onwards: the marker's own fragments end up left
            // of the content edge, and everything after them closes the gap.
            // Anything BEFORE it -- a ::before, which the builder injects
            // ahead of the marker -- keeps its place.
            for (size_t k = marker_first; k < line.size(); ++k) line[k].x -= marker_width;
            pen -= marker_width;
        }

        double dx = line_left;
        if (iequals(align, "right")) dx += line_width - pen;
        else if (iequals(align, "center")) dx += (line_width - pen) * 0.5;
        if (dx < line_left) dx = line_left;

        const BoxId lb = tree->create(BoxKind::Line, nullptr, container_style);
        (*tree)[lb].y = y;
        // The line box spans only the space the floats leave it, offset to
        // where that space starts.
        (*tree)[lb].x = left_inner + line_left;
        (*tree)[lb].width = line_width;
        (*tree)[lb].height = line_height;
        (*tree)[lb].baseline = baseline;
        (*tree)[lb].is_final_line = is_final;
        (*tree)[lb].applied_text_align_delta = dx;
        // What the wrap took off the end of this line, so the unwrapped width
        // of the paragraph can be rebuilt from its lines.
        (*tree)[lb].trimmed_trailing_space = trimmed_space;
        double decoration_total = 0;
        for (const Fragment& f : line) {
            if (f.item->is_marker()) decoration_total += f.width;
        }
        (*tree)[lb].inline_decoration_width = decoration_total;

        // CSS 2.1 §9.4.2: each inline box covering this line gets a fragment.
        // Spans are accumulated over the inline ANCESTOR chain, so a nested
        // `<a><b>x</b></a>` gives both a box. The chain is walked through the
        // tree because it is still intact here — clear_children runs once, at
        // the very end, and only detaches the container's direct children.
        std::vector<Span>& spans = scratch.spans;
        spans.clear();
        const auto contribute = [&](BoxId from, double x0, double x1, bool content) {
            for (BoxId b = from; b != kNoBox && b != container; b = (*tree)[b].parent) {
                if ((*tree)[b].kind != BoxKind::Inline) break;
                bool found = false;
                for (Span& sp : spans) {
                    if (sp.box == b) {
                        if (x0 < sp.x0) sp.x0 = x0;
                        if (x1 > sp.x1) sp.x1 = x1;
                        sp.covers_content = sp.covers_content || content;
                        found = true;
                        break;
                    }
                }
                if (!found) spans.push_back({b, kNoBox, x0, x1, content});
            }
        };

        // Two passes, and the split is load-bearing. `contribute` walks an
        // item's inline ANCESTORS through the tree, and attaching a fragment
        // REPARENTS it onto the line box — so doing both in one pass severed
        // the chain for every fragment after the first, and an outer
        // `<a><b>x</b></a>` stopped enclosing its inner box.
        for (const Fragment& f : line) {
            // A marker-only line earns no fragment: an inline box covers
            // content on the line, or it covers nothing.
            //
            // One exception, and it is asymmetric: a block-in-inline split
            // leaves an empty fragment on EACH side of the block, and the
            // reference emits a zero-size box for the trailing one only. That
            // asymmetry is load-bearing rather than incidental — the dump, like
            // paint, takes the FIRST box an element owns, so emitting the
            // leading fragment too would report the `<span>` before the block
            // instead of after it.
            if (only_markers && !emits_trailing_split_fragment) break;
            if (f.item->is_inline_start()) {
                const double x0 = f.x + dx + f.item->margin_edge;
                contribute(f.item->inline_box_start, x0, x0 + f.item->decoration, false);
            } else if (f.item->is_inline_end()) {
                const double x0 = f.x + dx;
                contribute(f.item->inline_box_end, x0, x0 + f.item->decoration, false);
            } else if (f.item->inline_parent != kNoBox) {
                contribute(f.item->inline_parent, f.x + dx, f.x + dx + f.width, true);
            }
        }

        for (const Fragment& f : line) {
            if (f.item->is_inline_start()) {
                // Attached at the point the inline box OPENS. A line's
                // children are in document order, and the dump — like paint
                // and hit testing — reads the first box an element owns.
                // Appending the fragments after the runs put
                // `<label>Name</label>` AFTER the `<input>` and `<button>`
                // that follow it in the source. Geometry is stamped once every
                // span is known, below.
                for (Span& sp : spans) {
                    if (sp.box != f.item->inline_box_start || sp.fragment != kNoBox) continue;
                    // The first line an inline box covers reuses the box
                    // itself, so the element keeps its identity; a later line
                    // gets a fragment carrying the same element and style.
                    BoxId frag = sp.box;
                    if (std::find(attached_inlines.begin(), attached_inlines.end(), sp.box) !=
                        attached_inlines.end()) {
                        frag = tree->create(BoxKind::Inline, (*tree)[sp.box].element,
                                            (*tree)[sp.box].style);
                    } else {
                        attached_inlines.push_back(sp.box);
                        // Reusing the box means its ORIGINAL children come with
                        // it, and its text children are exactly what the line's
                        // runs have just replaced. Left attached they are
                        // painted a second time -- and the box builder stamps no
                        // font size on a text box, so they came out at size
                        // ZERO: a pile of 3x3 glyph blobs at the container's
                        // origin, which reads as a stray 1px dash above the
                        // real text. Every `<b>` and `<code>` on a page had
                        // one.
                        //
                        // Only the TEXT children go. A nested inline box is
                        // still walked as an ancestor by later lines, and
                        // clearing it wholesale severed `<a><b>x</b></a>`.
                        std::vector<BoxId> stale;
                        for (BoxId c : tree->children(sp.box)) {
                            if ((*tree)[c].kind == BoxKind::Text) stale.push_back(c);
                        }
                        for (BoxId c : stale) tree->remove_child(c);
                    }
                    // The box builder never stamps a font size on an inline
                    // box, so without this the fragment's height came from
                    // the root size: a 35px `<span>` reported 18.29 tall.
                    (*tree)[frag].font_size = f.item->font_size;
                    sp.fragment = frag;
                    // Inserted FIRST, as the reference does (InlineLayout.cs,
                    // `line.InsertChildFirst(frag)`): every fragment precedes
                    // the runs, and fragments opened later precede ones opened
                    // earlier. Not document order — but the dump is a walk of
                    // the box tree, and this is the tree the reference builds.
                    // Appending instead put `<code>` before `<kbd>` where the
                    // reference has `<kbd>` first, on every page with two
                    // inline elements on one line.
                    tree->insert_child_first(lb, frag);
                    break;
                }
                continue;
            }
            if (f.item->is_inline_end()) continue;
            if (f.item->is_break()) {
                Box& br = (*tree)[f.item->break_box];
                br.x = f.x + dx;
                br.y = 0;
                br.width = 0;
                br.height = line_height;
                tree->append_child(lb, f.item->break_box);
                continue;
            }
            if (f.item->is_atom()) {
                // The atom keeps its own box; only its position on the line is
                // decided here. Its baseline sits on the line's.
                Box& a = (*tree)[f.item->atom_box];
                a.x = f.x + dx + a.margin_left;
                a.y = baseline - f.item->atom_baseline + a.margin_top;
                tree->append_child(lb, f.item->atom_box);
                continue;
            }
            const BoxId run = tree->create(BoxKind::Text, (*tree)[f.item->source_run].element,
                                           f.item->style);
            Box& r = (*tree)[run];
            r.text = f.text;
            r.source_node = (*tree)[f.item->source_run].source_node;
            r.font_size = f.item->font_size;
            r.font_family = get(f.item->style, kId_font_family);
            r.color = get(f.item->style, kId_color);
            r.x = f.x + dx;
            // Runs sit on the shared baseline, so a smaller span aligns with a
            // larger one rather than with the line's top edge.
            const FontMetrics& fm = f.item->metrics ? *f.item->metrics : metrics;
            r.y = baseline - fm.ascent(f.item->font_size);
            r.width = f.width;
            r.height = fm.ascent(f.item->font_size) + fm.descent(f.item->font_size);
            tree->append_child(lb, run);
        }
        for (const Span& sp : spans) {
            // Attached during the loop above, at the point the box opened.
            if (sp.fragment == kNoBox) continue;
            Box& fb = (*tree)[sp.fragment];
            if (only_empty_split_fragments) {
                // A zero-height line has no baseline to hang a content area
                // from, so the fragment is a point at the line's start.
                // Through the stamp below it landed half a line ABOVE the line
                // with a full line's height.
                fb.x = sp.x0;
                fb.y = 0;
                fb.width = 0;
                fb.height = 0;
                continue;
            }
            const double fs = fb.font_size > 0 ? fb.font_size : ctx.root_font_size_px;
            fb.x = sp.x0;
            // An inline box's content area sits on the baseline and is as tall
            // as the font, not as the line.
            fb.y = baseline - metrics.ascent(fs);
            fb.width = sp.x1 - sp.x0;
            fb.height = metrics.ascent(fs) + metrics.descent(fs);
        }

        line_boxes.push_back(lb);
        y += line_height;
        // Where the clamp's last allowed line ends. Recorded as it goes rather
        // than re-derived: line heights vary down a block, so the answer is
        // not count * line_height.
        if (++lines_flushed == line_clamp) clamp_bottom = y;
        first_piece = true;
        line.clear();
        pen = 0;
        reset_line_metrics();
        begin_line_at(y);
        for (Fragment& m : carried) { m.x = pen; pen += m.width; line.push_back(m); }
    };

    const auto grow_line_metrics = [&](const InlineItem& it) {
        if (it.is_atom()) {
            // The atom's own box sets the line's extents: everything above its
            // baseline counts as ascent, everything below as descent.
            const Box& a = (*tree)[it.atom_box];
            const double outer_h = a.margin_top + a.height + a.margin_bottom;
            max_ascent = std::max(max_ascent, it.atom_baseline);
            max_descent = std::max(max_descent, outer_h - it.atom_baseline);
            return;
        }
        const FontMetrics& fm = it.metrics ? *it.metrics : metrics;
        max_ascent = std::max(max_ascent, fm.ascent(it.font_size));
        max_descent = std::max(max_descent, fm.descent(it.font_size));
        max_leading = std::max(max_leading, it.line_height);
    };

    for (const InlineItem& it : items) {
        if (it.is_inline_start()) {
            // No break opportunity: it records where the box opens and takes
            // the box's start edges (margin, border, padding). It grows the
            // line metrics, because an inline box contributes its strut
            // whether or not it holds anything.
            grow_line_metrics(it);
            const double w = it.margin_edge + it.decoration;
            line.push_back({&it, {}, false, pen, w});
            pen += w;
            continue;
        }
        if (it.is_inline_end()) {
            const double w = it.decoration + it.margin_edge;
            line.push_back({&it, {}, false, pen, w});
            pen += w;
            continue;
        }
        if (it.is_break()) {
            // The break box sits at the pen, ends the line, and takes the
            // line's own height — which is only known at flush, so it is
            // stamped there.
            grow_line_metrics(it);
            line.push_back({&it, {}, false, pen, 0});
            flush_line(false);
            continue;
        }
        if (it.is_atom()) {
            // An atom wraps as a unit: it moves to the next line when it does
            // not fit, but is never split.
            if (it.allow_wrap && line_has_content() &&
                pen + it.atom_outer_width > line_width + kFitEpsilon) {
                flush_line(false);
            }
            grow_line_metrics(it);
            line.push_back({&it, {}, false, pen, it.atom_outer_width});
            pen += it.atom_outer_width;
            continue;
        }
        first_piece = true;
        // Largest prefix of `word` from `from` whose measured width fits, never
        // splitting a UTF-8 sequence. Zero when not even one character fits.
        const auto prefix_that_fits = [&](std::string_view word, size_t from,
                                          double max_width) -> size_t {
            if (max_width <= 0) return 0;
            size_t fits = 0;
            size_t i = from;
            while (i < word.size()) {
                // Advance one code point: continuation bytes are 10xxxxxx.
                size_t next = i + 1;
                while (next < word.size() &&
                       (static_cast<unsigned char>(word[next]) & 0xC0) == 0x80) {
                    ++next;
                }
                const double w =
                    measure_spaced(metrics, word.substr(from, next - from), it, first_piece);
                if (w > max_width) break;
                fits = next - from;
                i = next;
            }
            return fits;
        };

        // CSS Text L3 4.1.1: a preserved segment break forces a line break,
        // whatever the line's remaining space. `white-space: pre` on a block of
        // source lines used to arrive here as ONE unbreakable fragment, so a
        // nine-line code listing laid out as a single 1.3kpx-wide line and its
        // block reported one line-height of height (weva-landing's `.code-body`
        // came out 242.55 -> 58.95). Cut the text at each newline and place the
        // pieces through the normal machinery, flushing a line between them.
        // Tabs, expanded to the spaces that reach the next stop.
        //
        // EXPANDED rather than measured wide, because layout and paint each
        // measure the text they are given: a fragment whose width said "tab
        // stop" while its text still held a `	` would draw its glyphs
        // somewhere the layout did not put them. With the tab replaced by
        // spaces the two cannot disagree, which is also what the reference
        // does.
        const auto expand_tabs = [&](std::string_view text, double pen_at_start) {
            if (text.find('	') == std::string_view::npos) return text;
            const FontMetrics& fm = it.metrics ? *it.metrics : metrics;
            double space_w = fm.measure(" ", it.font_size);
            if (space_w <= 0) space_w = it.font_size * 0.5;
            const double stop = space_w * (it.tab_spaces > 0 ? it.tab_spaces : 8);
            std::string out;
            out.reserve(text.size() + 8);
            double x = pen_at_start;
            for (const char c : text) {
                if (c != '	') {
                    out.push_back(c);
                    x += fm.measure(std::string_view(&c, 1), it.font_size);
                    continue;
                }
                // To the next multiple of the stop, never zero: a tab always
                // advances at least one stop.
                const double next = (std::floor(x / stop) + 1) * stop;
                int spaces = static_cast<int>(std::lround((next - x) / space_w));
                if (spaces < 1) spaces = 1;
                out.append(static_cast<size_t>(spaces), ' ');
                x = next;
            }
            return std::string_view(tree->own_text(std::move(out)));
        };

        size_t seg_begin = 0;
        while (true) {
            const size_t nl =
                it.preserve_newlines ? it.text.find('\n', seg_begin) : std::string_view::npos;
            const std::string_view seg =
                nl == std::string_view::npos ? it.text.substr(seg_begin)
                                             : it.text.substr(seg_begin, nl - seg_begin);
            // Each segment opens a line of its own, so it is a first piece
            // again for the letter-spacing at its leading edge.
            first_piece = true;
            if (!it.collapse_whitespace && !it.allow_wrap) {
                // `pre`: whitespace preserved AND no wrapping, so the segment
                // is one unbreakable fragment and its width is accounted for
                // whatever it is. An EMPTY segment still pushes its
                // (zero-width) fragment when the line holds nothing, because a
                // blank line inside a `pre` is a line: without a fragment
                // flush_line drops it and the block comes up one line-height
                // short.
                const std::string_view text = expand_tabs(seg, pen);
                const double w = measure_spaced(metrics, text, it, true);
                if (!text.empty() || line.empty()) {
                    grow_line_metrics(it);
                    line.push_back({&it, text, false, pen, w});
                    pen += w;
                }
            } else if (!it.collapse_whitespace) {
                // `pre-wrap` and `break-spaces`: whitespace is preserved and
                // the line STILL wraps (CSS Text L3 3.1). Placing the segment
                // as one fragment, as `pre` does, is why no textarea in the
                // engine soft-wrapped -- the value ran off the side of the box
                // and grew a horizontal scrollbar instead of a second line.
                //
                // Spaces keep their width and stay where they are; a word that
                // does not fit starts a new line. The spaces before that break
                // hang past the edge, as they do in a browser, rather than
                // pushing the word down a line early.
                size_t at = 0;
                if (seg.empty() && line.empty()) {
                    grow_line_metrics(it);
                    line.push_back({&it, seg, false, pen, 0});
                }
                while (at < seg.size()) {
                    const bool spaces = seg[at] == ' ' || seg[at] == '\t';
                    size_t end = at;
                    while (end < seg.size() &&
                           ((seg[end] == ' ' || seg[end] == '\t') == spaces)) {
                        ++end;
                    }
                    const std::string_view piece = expand_tabs(seg.substr(at, end - at), pen);
                    // A preserved run of spaces is charged word-spacing per
                    // space, the same as a collapsed one is charged for the
                    // single space it becomes.
                    double w = measure_spaced(metrics, piece, it, first_piece);
                    if (spaces && it.word_spacing != 0) {
                        w += it.word_spacing * static_cast<double>(piece.size());
                    }
                    first_piece = false;
                    if (!spaces && line_has_content() && pen + w > line_width + kFitEpsilon) {
                        flush_line(false);
                    }
                    grow_line_metrics(it);
                    line.push_back({&it, piece, spaces, pen, w});
                    pen += w;
                    at = end;
                }
            } else {
            tokenize_collapsing(seg, &scratch.tokens);
            for (const Token& t : scratch.tokens) {
                if (t.is_space) {
                    // A collapsed space at the very start of a line is dropped:
                    // it would indent every wrapped line by a space. A line that
                    // holds only inline-box markers counts as its start — the
                    // whitespace after `<card>` is not a 7px indent.
                    if (!line_has_content()) continue;
                    const double w = measure_spaced(metrics, " ", it, first_piece) + it.word_spacing;
                    first_piece = false;
                    grow_line_metrics(it);
                    line.push_back({&it, " ", true, pen, w});
                    pen += w;
                    continue;
                }
                // `overflow-wrap: break-word` is the value authors actually
                // write to stop a long name blowing out a panel, and the port
                // read neither spelling of it. It is NOT `break-all`: a word
                // is kept whole and moved to the next line as usual, and only
                // broken when it is alone on a line and STILL does not fit.
                //
                // So the decision needs the width, which is measured below --
                // this only records that the option is open.
                bool slice_word = it.break_anywhere;
                if (!slice_word && it.break_word && it.allow_wrap) {
                    const double whole = measure_spaced(metrics, t.word, it, first_piece);
                    if (line_has_content() && pen + whole > line_width + kFitEpsilon) {
                        flush_line(false);
                    }
                    // On a line of its own now. Wider than the line itself is
                    // the only case break-word breaks.
                    slice_word = whole > line_width + kFitEpsilon;
                }
                if (slice_word) {
                    // Every character boundary is a break opportunity, so the word
                    // is placed a slice at a time: fill the rest of this line, wrap,
                    // repeat. A slice is a view into the same source buffer, so no
                    // string is built.
                    size_t idx = 0;
                    while (idx < t.word.size()) {
                        double remaining = line_width - pen;
                        if (remaining <= 1e-9 && !line.empty()) {
                            flush_line(false);
                            remaining = line_width - pen;
                        }
                        size_t take = prefix_that_fits(t.word, idx, remaining);
                        if (take == 0) {
                            // Nothing fits. Wrap and retry; on an already-empty
                            // line take one character anyway, because a line that
                            // can hold nothing still has to make progress.
                            if (!line.empty()) {
                                flush_line(false);
                                continue;
                            }
                            take = 1;
                            while (idx + take < t.word.size() &&
                                   (static_cast<unsigned char>(t.word[idx + take]) & 0xC0) == 0x80) {
                                ++take;
                            }
                        }
                        const std::string_view slice = t.word.substr(idx, take);
                        const double sw = measure_spaced(metrics, slice, it, first_piece);
                        first_piece = false;
                        grow_line_metrics(it);
                        line.push_back({&it, slice, false, pen, sw});
                        pen += sw;
                        idx += take;
                        if (idx < t.word.size()) flush_line(false);
                    }
                    continue;
                }
                // Japanese and Chinese have no spaces, so the tokeniser hands
                // this loop the whole sentence as one word. Placed whole it
                // runs off the side of the box -- the port could not wrap a
                // CJK line at all. Break it at the opportunities kinsoku
                // allows: between any two CJK characters except before a
                // closing mark or after an opening one.
                if (it.allow_wrap && !it.break_anywhere && has_cjk(t.word)) {
                    size_t idx = 0;
                    while (idx < t.word.size()) {
                        const size_t stop = cjk_piece_end(t.word, idx, it.line_break);
                        const std::string_view piece = t.word.substr(idx, stop - idx);
                        const double pw = measure_spaced(metrics, piece, it, first_piece);
                        first_piece = false;
                        if (line_has_content() && pen + pw > line_width + kFitEpsilon) {
                            flush_line(false);
                        }
                        grow_line_metrics(it);
                        line.push_back({&it, piece, false, pen, pw});
                        pen += pw;
                        idx = stop;
                    }
                    continue;
                }
                const double w = measure_spaced(metrics, t.word, it, first_piece);
                first_piece = false;
                // A word that does not fit starts a new line — unless the line is
                // already empty, in which case it overflows rather than looping.
                if (it.allow_wrap && line_has_content() && pen + w > line_width + kFitEpsilon) {
                    flush_line(false);
                }
                grow_line_metrics(it);
                line.push_back({&it, t.word, false, pen, w});
                pen += w;
            }
            }
            if (nl == std::string_view::npos) break;
            grow_line_metrics(it);
            flush_line(false);
            seg_begin = nl + 1;
        }
    }
    flush_line(true);

    // `text-overflow: ellipsis`, once the line is final: the cut needs the
    // measured runs, and nothing before this point has them.
    if (wants_ellipsis(container_style) && line_boxes.size() == 1) {
        truncate_line_with_ellipsis(tree, line_boxes.front(), line_width, ctx, metrics);
    }

    // `-webkit-line-clamp`: keep the first N lines and end the last of them
    // with an ellipsis, the way a browser does. The lines past the clamp are
    // simply not attached to the container below -- they stay in the arena,
    // like the text boxes the lines replaced.
    if (line_clamp > 0 && static_cast<int>(line_boxes.size()) > line_clamp) {
        truncate_line_with_ellipsis(tree, line_boxes[static_cast<size_t>(line_clamp) - 1],
                                    line_width, ctx, metrics);
        line_boxes.resize(static_cast<size_t>(line_clamp));
        y = clamp_bottom;
    }

    // The container's children become its line boxes. The original text boxes
    // stay allocated in the arena but are no longer reachable from the tree —
    // the line's runs carry the geometry now.
    tree->clear_children(container);
    for (BoxId lb : line_boxes) tree->append_child(container, lb);

    return y - top_inner;
}

double max_content_width(const BoxTree& tree, BoxId id) {
    return max_content_width(tree, id, nullptr);
}

double intrinsic_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx, bool minimum);

namespace {

// A block-level child's outer max-content contribution: its explicit width
// when it has one (already resolved onto the box), otherwise its content plus
// its own frame, bounded by its min-/max-width — plus margins either way.
double block_child_contribution(const BoxTree& tree, BoxId c, const LayoutContext* ctx, bool minimum) {
    const Box& b = tree[c];
    const std::string_view width_raw = get(b.style, kId_width);
    const bool explicit_width = !width_raw.empty() && !iequals(width_raw, "auto") &&
                                width_raw.find('%') == std::string_view::npos;
    double w;
    if (explicit_width) {
        w = b.width;
    } else {
        const double frame = b.padding_left + b.padding_right + b.border_left + b.border_right;
        w = intrinsic_width(tree, c, ctx, minimum) + frame;
        if (ctx && b.style) {
            const double fs = b.font_size > 0 ? b.font_size : ctx->root_font_size_px;
            const double minmax_frame = is_border_box(b.style) ? 0 : frame;
            const ResolvedLength min_w = resolve_length(b.style, kId_min_width, *ctx, fs, std::nullopt);
            const ResolvedLength max_w = resolve_length(b.style, kId_max_width, *ctx, fs, std::nullopt);
            if (min_w.kind == LengthKind::Length) w = std::max(w, min_w.pixels + minmax_frame);
            if (max_w.kind == LengthKind::Length) w = std::min(w, max_w.pixels + minmax_frame);
        }
    }
    // `margin: auto` is resolved by the box's container (centring, or a flex
    // line's free space) and holds whatever was left over at the last layout;
    // it is not content. Counting it made a card with an auto-margin-pushed
    // item as wide as the line it was last laid out in, and it then could not
    // share a line with anything.
    const std::string_view ml = get(b.style, kId_margin_left);
    const std::string_view mr = get(b.style, kId_margin_right);
    const double margins = (iequals(ml, "auto") ? 0.0 : b.margin_left) +
                           (iequals(mr, "auto") ? 0.0 : b.margin_right);
    return w + margins;
}

} // namespace

// CSS 2.1 §10.3.5 / css-sizing-3 §5: the max-content inline size of a box's
// CONTENT (the caller adds the box's own frame). A flex row sums its items —
// a container is as wide as everything on its one line — where a block
// container takes the widest child. Taking the max for a flex row made an
// absolutely positioned pill (icon + amount) shrink-to-fit to its widest
// item alone, and its items then shrank to fit into that.
double max_content_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx) {
    return intrinsic_width(tree, id, ctx, false);
}

double min_content_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx) {
    return intrinsic_width(tree, id, ctx, true);
}

double intrinsic_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx, bool minimum) {
    const Box& self = tree[id];
    // A grid container's intrinsic size is its tracks', which layout_grid
    // records when it sizes them (§12 under a max-content constraint); the
    // children alone say nothing about fixed tracks or gaps. An 8-column
    // 56px board centred in a flex row was shrink-fitted to one tile.
    if (self.kind == BoxKind::Block &&
        (self.display == DisplayKind::Grid || self.display == DisplayKind::InlineGrid) &&
        self.grid_max_content >= 0) {
        return minimum ? self.grid_min_content : self.grid_max_content;
    }
    const bool flex = self.kind == BoxKind::Block &&
                      (self.display == DisplayKind::Flex || self.display == DisplayKind::InlineFlex);
    const std::string_view direction = get(self.style, kId_flex_direction);
    const bool flex_row = flex && !(iequals(direction, "column") || iequals(direction, "column-reverse"));
    // A row that may wrap breaks between items, so its min-content is its
    // widest item; one that may not still sums them. Text under
    // `white-space: nowrap`/`pre` cannot break either.
    const std::string_view wrap_raw = get(self.style, kId_flex_wrap);
    const bool row_sums = flex_row && (!minimum || !(iequals(wrap_raw, "wrap") ||
                                                       iequals(wrap_raw, "wrap-reverse")));
    const std::string_view ws = get(self.style, kId_white_space);
    const bool text_unbreakable = !minimum || iequals(ws, "nowrap") || iequals(ws, "pre");

    double max = 0;
    double sum = 0;
    double paragraph = 0;   // running unwrapped width of the current run of lines
    int in_flow_blocks = 0;
    for (BoxId c : tree.children(id)) {
        const Box& b = tree[c];
        if (b.position == PositionType::Absolute || b.position == PositionType::Fixed) continue;
        // The layout pass has not always stamped `position` yet (see the flex
        // collection note); the style is the truth either way. Only for
        // ELEMENT boxes: a line box carries its container's style, and an
        // absolutely positioned container's own lines are its content.
        if (b.style && b.element && b.kind == BoxKind::Block) {
            const PositionType p = parse_position_type(get(b.style, kId_position));
            if (p == PositionType::Absolute || p == PositionType::Fixed) continue;
        }
        // CSS 2.1 §10.3.5: a float is out of flow for intrinsic sizing — its
        // containing block flows around it and it contributes nothing.
        if (b.is_float()) continue;

        if (b.kind == BoxKind::Line) {
            // The line's own width is post-alignment; summing the raw run
            // widths gives the natural text advance instead. For min-content
            // the widest single run — a word, a space, an atom — is the
            // unbreakable unit. For max-content the lines of one paragraph
            // are joined back up (with the spaces the wrap trimmed), because
            // the lines were broken at the width the box happened to have;
            // reading the widest WRAPPED line fitted a centred paragraph to
            // 163.8px inside its 194px column. A forced break ends a paragraph.
            double line_sum = 0, widest = 0;
            bool forced_break = false;
            for (BoxId r : tree.children(c)) {
                const Box& run = tree[r];
                if (run.kind == BoxKind::Inline && run.element && run.element->tag_name() == "br") {
                    forced_break = true;
                }
                // An inline box's fragment spans the runs it covers; counting
                // it as well as them doubled every bold word.
                if (run.kind == BoxKind::Inline) continue;
                const double w = run.width + (run.kind == BoxKind::Block
                                                  ? run.margin_left + run.margin_right
                                                  : 0);
                line_sum += w;
                if (w > widest) widest = w;
            }
            if (text_unbreakable) {
                // The space a wrap trimmed sits BETWEEN two lines of the
                // paragraph and comes back when they are joined; one trimmed
                // at the paragraph's end is gone in max-content too. The
                // inline boxes' own edges are on the line but in no run.
                const bool ends_paragraph = forced_break || b.is_final_line;
                paragraph += line_sum + b.inline_decoration_width +
                             (ends_paragraph ? 0.0 : b.trimmed_trailing_space);
                if (ends_paragraph) {
                    if (paragraph > max) max = paragraph;
                    paragraph = 0;
                }
            } else if (widest > max) {
                max = widest;
            }
            continue;
        }
        if (b.kind == BoxKind::Block && b.is_inline_block) {
            // An atom that has not been placed on a line yet (a container
            // whose inline content is still raw): its own width.
            if (b.width > max) max = b.width;
            continue;
        }
        if (b.kind != BoxKind::Block && b.kind != BoxKind::AnonymousBlock) continue;
        const double contribution = block_child_contribution(tree, c, ctx, minimum);
        ++in_flow_blocks;
        sum += contribution;
        if (contribution > max) max = contribution;
    }
    if (paragraph > max) max = paragraph;   // lines that did not end in a final line
    if (row_sums && in_flow_blocks > 1) {
        double gap = 0;
        if (ctx && self.style) {
            const std::string_view raw = get(self.style, kId_column_gap);
            if (!raw.empty() && !iequals(raw, "normal")) {
                const double fs = self.font_size > 0 ? self.font_size : ctx->root_font_size_px;
                const ResolvedLength r = resolve_length(self.style, kId_column_gap, *ctx, fs, std::nullopt);
                if (r.kind == LengthKind::Length) gap = std::max(0.0, r.pixels);
            }
        }
        return sum + gap * static_cast<double>(in_flow_blocks - 1);
    }
    return max;
}

} // namespace weva
