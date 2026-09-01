#include "weva/inline_layout.h"

// For FloatContext, which line-box narrowing queries.
#include "weva/block_layout.h"

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
            const std::string_view wb = get(item.style, "word-break");
            const std::string_view ow = get(item.style, "overflow-wrap");
            // `anywhere` differs from `break-all` only in how it affects
            // min-content sizing, which is not tracked yet, so the two are
            // observably identical here — the same simplification the
            // reference makes, and it says so.
            item.break_anywhere =
                item.allow_wrap && (iequals(wb, "break-all") || iequals(ow, "anywhere"));
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
            item.font_size = font_size_px(item.style, nullptr, ctx);
            item.line_height = line_height_px(item.style, item.font_size, ctx, metrics);
            out->push_back(item);
        } else if (b.kind == BoxKind::Inline || b.kind == BoxKind::AnonymousInline) {
            // CSS 2.1 §9.4.2: an inline element produces a box on every line it
            // covers. A marker records where it starts so a box that ends up
            // with no fragments of its own is still placed.
            if (b.kind == BoxKind::Inline) {
                InlineItem item;
                item.inline_box_start = c;
                item.inline_parent = inline_parent;
                item.style = b.style ? b.style : inherited;
                item.font_size = font_size_px(item.style, nullptr, ctx);
                item.line_height = line_height_px(item.style, item.font_size, ctx, metrics);
                out->push_back(item);
            }
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
                           const LayoutContext& ctx, const FontMetrics& metrics,
                           const InlineFloatEnv* float_env) {
    const Box& cbox = (*tree)[container];
    const double top_inner = cbox.padding_top + cbox.border_top;
    const double left_inner = cbox.padding_left + cbox.border_left;
    const std::string_view align = resolve_text_align(cbox.style);
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

    // CSS 2.1 §9.5: a line box beside a float is shortened to make room for it.
    // This has to be known while the line is being FILLED, not only when it is
    // flushed — the wrap decision compares against it — so it is recomputed
    // whenever a line starts.
    // Inline boxes already given their first fragment; a second line covering
    // the same box clones it rather than moving it.
    std::vector<BoxId> attached_inlines;

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
        // Zero-width inline-box markers ride along: a marker sitting after a
        // trailing space must end up at the TRIMMED pen, not keep the position
        // the removed space had pushed it to.
        std::vector<Fragment> trailing_markers;
        while (!line.empty()) {
            if (line.back().item->is_inline_start()) {
                trailing_markers.push_back(line.back());
                line.pop_back();
                continue;
            }
            if (line.back().is_space) {
                pen -= line.back().width;
                line.pop_back();
                continue;
            }
            break;
        }
        for (auto it = trailing_markers.rbegin(); it != trailing_markers.rend(); ++it) {
            Fragment m = *it;
            m.x = pen;
            line.push_back(m);
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

        // CSS 2.1 §9.4.2: each inline box covering this line gets a fragment.
        // Spans are accumulated over the inline ANCESTOR chain, so a nested
        // `<a><b>x</b></a>` gives both a box. The chain is walked through the
        // tree because it is still intact here — clear_children runs once, at
        // the very end, and only detaches the container's direct children.
        struct Span { BoxId box; double x0; double x1; };
        std::vector<Span> spans;
        const auto contribute = [&](BoxId from, double x0, double x1) {
            for (BoxId b = from; b != kNoBox && b != container; b = (*tree)[b].parent) {
                if ((*tree)[b].kind != BoxKind::Inline) break;
                bool found = false;
                for (Span& sp : spans) {
                    if (sp.box == b) {
                        if (x0 < sp.x0) sp.x0 = x0;
                        if (x1 > sp.x1) sp.x1 = x1;
                        found = true;
                        break;
                    }
                }
                if (!found) spans.push_back({b, x0, x1});
            }
        };

        for (const Fragment& f : line) {
            if (f.item->is_inline_start()) {
                // A marker has no width; it only pins where the box begins.
                contribute(f.item->inline_box_start, f.x + dx, f.x + dx);
                continue;
            }
            if (f.item->inline_parent != kNoBox) {
                contribute(f.item->inline_parent, f.x + dx, f.x + dx + f.width);
            }
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
        for (const Span& sp : spans) {
            // The first line an inline box covers reuses the box itself, so the
            // element keeps its identity; every later line gets a fragment
            // carrying the same element and style.
            BoxId frag = sp.box;
            if (std::find(attached_inlines.begin(), attached_inlines.end(), sp.box) !=
                attached_inlines.end()) {
                frag = tree->create(BoxKind::Inline, (*tree)[sp.box].element,
                                    (*tree)[sp.box].style);
                (*tree)[frag].font_size = (*tree)[sp.box].font_size;
            } else {
                attached_inlines.push_back(sp.box);
            }
            Box& fb = (*tree)[frag];
            const double fs = fb.font_size > 0 ? fb.font_size : ctx.root_font_size_px;
            fb.x = sp.x0;
            // An inline box's content area sits on the baseline and is as tall
            // as the font, not as the line.
            fb.y = baseline - metrics.ascent(fs);
            fb.width = sp.x1 - sp.x0;
            fb.height = metrics.ascent(fs) + metrics.descent(fs);
            tree->append_child(lb, frag);
        }

        line_boxes.push_back(lb);
        y += line_height;
        line.clear();
        pen = 0;
        reset_line_metrics();
        begin_line_at(y);
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
        if (it.is_inline_start()) {
            // Zero width and no break opportunity: it only records a position.
            // It does grow the line metrics, because an inline box contributes
            // its strut whether or not it holds anything.
            grow_line_metrics(it);
            line.push_back({&it, {}, false, pen, 0});
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
            if (!line.empty() && pen + it.atom_outer_width > line_width) {
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
                    metrics.measure(word.substr(from, next - from), it.font_size);
                if (w > max_width) break;
                fits = next - from;
                i = next;
            }
            return fits;
        };

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
            if (it.break_anywhere) {
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
                    const double sw = metrics.measure(slice, it.font_size);
                    grow_line_metrics(it);
                    line.push_back({&it, slice, false, pen, sw});
                    pen += sw;
                    idx += take;
                    if (idx < t.word.size()) flush_line(false);
                }
                continue;
            }
            const double w = metrics.measure(t.word, it.font_size);
            // A word that does not fit starts a new line — unless the line is
            // already empty, in which case it overflows rather than looping.
            if (it.allow_wrap && !line.empty() && pen + w > line_width) {
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
