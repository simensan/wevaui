#include "weva/inline_layout.h"

#include <optional>

#include "weva/css_properties.h"

#include <algorithm>

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

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

std::vector<Token> tokenize_collapsing(std::string_view text) {
    std::vector<Token> out;
    size_t i = 0;
    while (i < text.size()) {
        if (is_collapsible_ws(text[i])) {
            while (i < text.size() && is_collapsible_ws(text[i])) ++i;
            out.push_back({true, {}});
        } else {
            const size_t start = i;
            while (i < text.size() && !is_collapsible_ws(text[i])) ++i;
            out.push_back({false, text.substr(start, i - start)});
        }
    }
    return out;
}

void collect_recursive(const BoxTree& tree, BoxId node, BoxId inline_parent,
                       const LayoutContext& ctx, const ComputedStyle* inherited,
                       const FontMetrics* metrics, std::vector<InlineItem>* out) {
    for (BoxId c : tree.children(node)) {
        const Box& b = tree[c];
        if (b.kind == BoxKind::Text) {
            InlineItem item;
            item.source_run = c;
            item.inline_parent = inline_parent;
            item.text = b.text;
            item.style = b.style ? b.style : inherited;
            item.font_size = font_size_px(item.style, nullptr, ctx);
            item.line_height = line_height_px(item.style, item.font_size, ctx, metrics);
            const std::string_view ws = get(item.style, "white-space");
            // `pre` and `pre-wrap` preserve whitespace; `nowrap` and `pre`
            // forbid wrapping. Only the two axes matter to layout, so they are
            // decomposed here rather than carried as a keyword.
            item.collapse_whitespace = !(iequals(ws, "pre") || iequals(ws, "pre-wrap") ||
                                         iequals(ws, "break-spaces"));
            item.allow_wrap = !(iequals(ws, "nowrap") || iequals(ws, "pre"));
            out->push_back(item);
        } else if (b.kind == BoxKind::Inline || b.kind == BoxKind::AnonymousInline) {
            collect_recursive(tree, c, c, ctx, b.style ? b.style : inherited, metrics, out);
        } else if (b.kind == BoxKind::Block && b.is_inline_block) {
            // An atom: placed whole, never broken. It is recorded here but not
            // sized — sizing it needs the block layout engine, so the caller
            // fills in the width and baseline before layout runs.
            InlineItem item;
            item.atom_box = c;
            item.inline_parent = inline_parent;
            item.style = b.style ? b.style : inherited;
            item.font_size = font_size_px(item.style, nullptr, ctx);
            item.line_height = line_height_px(item.style, item.font_size, ctx, metrics);
            out->push_back(item);
        }
    }
}

} // namespace

std::string_view resolve_text_align(const ComputedStyle* style) {
    std::string_view t = get(style, "text-align");
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
    collect_recursive(tree, container, kNoBox, ctx, tree[container].style, metrics, &out);
    return out;
}

double layout_inline(BoxTree* tree, BoxId container, double available_width,
                     const LayoutContext& ctx, const FontMetrics& metrics) {
    return layout_inline_items(tree, container,
                               collect_inline_items(*tree, container, ctx, &metrics),
                               available_width, ctx, metrics);
}

double layout_inline_items(BoxTree* tree, BoxId container,
                           const std::vector<InlineItem>& items, double available_width,
                           const LayoutContext& ctx, const FontMetrics& metrics) {
    const Box& cbox = (*tree)[container];
    const double top_inner = cbox.padding_top + cbox.border_top;
    const double left_inner = cbox.padding_left + cbox.border_left;
    const std::string_view align = resolve_text_align(cbox.style);

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
    std::optional<double> declared_line_height;
    if (cbox.style) {
        const std::string_view raw = get(cbox.style, "line-height");
        if (!raw.empty() && !iequals(raw, "normal")) {
            const double container_fs =
                font_size_px(cbox.style, cbox.parent != kNoBox ? (*tree)[cbox.parent].style
                                                               : nullptr, ctx);
            declared_line_height = line_height_px(cbox.style, container_fs, ctx, &metrics);
        }
    }

    // One fragment of text placed on the line being built.
    struct Fragment {
        const InlineItem* item;
        std::string_view text;
        bool is_space;
        double x;
        double width;
    };
    // An atom contributes above- and below-baseline extents like a glyph does,
    // so the line grows around it instead of clipping it.
    std::vector<Fragment> line;
    std::vector<BoxId> line_boxes;

    double y = top_inner;
    double pen = 0;
    double max_ascent = 0, max_descent = 0, max_leading = 0;

    const auto reset_line_metrics = [&] {
        max_ascent = 0;
        max_descent = 0;
        max_leading = 0;
    };

    // Emits the fragments collected so far as one LineBox with TextRun children.
    const auto flush_line = [&](bool is_final) {
        // Trailing collapsible spaces do not occupy the end of a line — they
        // would otherwise push the alignment of every centred or right-aligned
        // line by a space width.
        while (!line.empty() && line.back().is_space) {
            pen -= line.back().width;
            line.pop_back();
        }
        if (line.empty() && !is_final) {
            reset_line_metrics();
            pen = 0;
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
        const double line_height = declared_line_height.value_or(natural_height);
        const double baseline = natural_baseline + (line_height - natural_height) * 0.5;

        double dx = 0;
        if (iequals(align, "right")) dx = available_width - pen;
        else if (iequals(align, "center")) dx = (available_width - pen) * 0.5;
        if (dx < 0) dx = 0;

        const BoxId lb = tree->create(BoxKind::Line, nullptr, cbox.style);
        (*tree)[lb].x = left_inner;
        (*tree)[lb].y = y;
        (*tree)[lb].width = available_width;
        (*tree)[lb].height = line_height;
        (*tree)[lb].baseline = baseline;
        (*tree)[lb].is_final_line = is_final;
        (*tree)[lb].applied_text_align_delta = dx;

        for (const Fragment& f : line) {
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
            r.font_family = get(f.item->style, "font-family");
            r.color = get(f.item->style, "color");
            r.x = f.x + dx;
            // Runs sit on the shared baseline, so a smaller span aligns with a
            // larger one rather than with the line's top edge.
            r.y = baseline - metrics.ascent(f.item->font_size);
            r.width = f.width;
            r.height = metrics.ascent(f.item->font_size) + metrics.descent(f.item->font_size);
            tree->append_child(lb, run);
        }
        line_boxes.push_back(lb);
        y += line_height;
        line.clear();
        pen = 0;
        reset_line_metrics();
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
        max_ascent = std::max(max_ascent, metrics.ascent(it.font_size));
        max_descent = std::max(max_descent, metrics.descent(it.font_size));
        max_leading = std::max(max_leading, it.line_height);
    };

    for (const InlineItem& it : items) {
        if (it.is_atom()) {
            // An atom wraps as a unit: it moves to the next line when it does
            // not fit, but is never split.
            if (!line.empty() && pen + it.atom_outer_width > available_width) {
                flush_line(false);
            }
            grow_line_metrics(it);
            line.push_back({&it, {}, false, pen, it.atom_outer_width});
            pen += it.atom_outer_width;
            continue;
        }
        if (!it.collapse_whitespace) {
            // Preserved whitespace is a later slice; the text is placed as one
            // unbreakable fragment so its width is still accounted for.
            const double w = metrics.measure(it.text, it.font_size);
            grow_line_metrics(it);
            line.push_back({&it, it.text, false, pen, w});
            pen += w;
            continue;
        }
        for (const Token& t : tokenize_collapsing(it.text)) {
            if (t.is_space) {
                // A collapsed space at the very start of a line is dropped:
                // it would indent every wrapped line by a space.
                if (line.empty()) continue;
                const double w = metrics.measure(" ", it.font_size);
                grow_line_metrics(it);
                line.push_back({&it, " ", true, pen, w});
                pen += w;
                continue;
            }
            const double w = metrics.measure(t.word, it.font_size);
            // A word that does not fit starts a new line — unless the line is
            // already empty, in which case it overflows rather than looping.
            if (it.allow_wrap && !line.empty() && pen + w > available_width) {
                flush_line(false);
            }
            grow_line_metrics(it);
            line.push_back({&it, t.word, false, pen, w});
            pen += w;
        }
    }
    flush_line(true);

    // The container's children become its line boxes. The original text boxes
    // stay allocated in the arena but are no longer reachable from the tree —
    // the line's runs carry the geometry now.
    tree->clear_children(container);
    for (BoxId lb : line_boxes) tree->append_child(container, lb);

    return y - top_inner;
}

double max_content_width(const BoxTree& tree, BoxId id) {
    double max = 0;
    for (BoxId c : tree.children(id)) {
        const Box& b = tree[c];
        if (b.position == PositionType::Absolute || b.position == PositionType::Fixed) continue;
        // CSS 2.1 §10.3.5: a float is out of flow for intrinsic sizing — its
        // containing block flows around it and it contributes nothing.
        if (b.is_float()) continue;

        if (b.kind == BoxKind::Line) {
            // The line's own width is post-alignment; summing the raw run
            // widths gives the natural text advance instead.
            double sum = 0;
            for (BoxId r : tree.children(c)) sum += tree[r].width;
            if (sum > max) max = sum;
            continue;
        }
        if (b.kind == BoxKind::Block && b.is_inline_block) {
            if (b.width > max) max = b.width;
            continue;
        }
        const double inner = max_content_width(tree, c);
        if (inner > max) max = inner;
    }
    return max;
}

} // namespace weva
