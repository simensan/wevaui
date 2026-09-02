#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/css_value.h"

#include <cctype>
#include <cstdlib>
#include <optional>
#include <string>

#include "weva/css_properties.h"

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

bool is_non_auto(std::string_view v) {
    return !v.empty() && v != "auto";
}

// CSS Multi-column L1 §2: a block container becomes a multicol container when
// column-count or column-width is non-auto. Flex, grid and table containers
// ignore the column properties.
bool is_multicol_container(const ComputedStyle* style) {
    if (!style) return false;
    return is_non_auto(get(style, "column-count")) || is_non_auto(get(style, "column-width"));
}

bool equals_ignoring_case(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

bool is_out_of_flow_position(const ComputedStyle* style) {
    const std::string_view p = get(style, "position");
    return equals_ignoring_case(p, "absolute") || equals_ignoring_case(p, "fixed");
}

bool is_floated(const ComputedStyle* style) {
    const std::string_view f = get(style, "float");
    return !f.empty() && !equals_ignoring_case(f, "none");
}

bool is_whitespace_only(std::string_view s) {
    for (char c : s) {
        if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') return false;
    }
    return true;
}

// A flex or grid container blockifies its in-flow children (CSS Flexbox §4,
// Grid §6): an inline child becomes a block-level item. Inline-flex and
// inline-grid containers do too — the inline-ness is about the OUTER display.
bool blockifies_children(DisplayKind d) {
    return d == DisplayKind::Flex || d == DisplayKind::InlineFlex ||
           d == DisplayKind::Grid || d == DisplayKind::InlineGrid;
}

DisplayKind blockified(DisplayKind d) {
    switch (d) {
        case DisplayKind::InlineBlock: return DisplayKind::Block;
        case DisplayKind::InlineFlex:  return DisplayKind::Flex;
        case DisplayKind::InlineGrid:  return DisplayKind::Grid;
        case DisplayKind::InlineTable: return DisplayKind::Table;
        default: return d;
    }
}

bool establishes_block_box(DisplayKind d) {
    return d == DisplayKind::Block || d == DisplayKind::Flex || d == DisplayKind::Grid ||
           d == DisplayKind::FlowRoot || d == DisplayKind::ListItem;
}

bool is_inline_level_block(DisplayKind d) {
    return d == DisplayKind::InlineBlock || d == DisplayKind::InlineFlex ||
           d == DisplayKind::InlineGrid || d == DisplayKind::InlineTable;
}

} // namespace

// CSS Text L3 §2.1 `text-transform`: uppercase, lowercase and capitalize
// over ASCII and Latin-1 (the ranges the samples use); other scripts pass
// through unchanged. The result is owned by the tree, since no DOM node
// holds it. Applied when the run's box is built so layout measures the
// transformed text — with a real face the capitals are wider.
std::string_view BoxBuilder::transformed_text(std::string_view text,
                                              const ComputedStyle* style) {
    const std::string_view mode = get(style, "text-transform");
    if (mode.empty() || mode == "none" || text.empty()) return text;
    const bool upper = mode == "uppercase", lower = mode == "lowercase",
               capitalize = mode == "capitalize";
    if (!upper && !lower && !capitalize) return text;
    std::string out;
    out.reserve(text.size());
    bool at_word_start = true;
    for (size_t i = 0; i < text.size(); ++i) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        // A two-byte Latin-1 letter: U+00C0-U+00DE upper, U+00E0-U+00FE lower
        // (× and ÷ excepted), encoded as C3 80-9E / C3 A0-BE.
        if (c == 0xC3 && i + 1 < text.size()) {
            unsigned char d = static_cast<unsigned char>(text[i + 1]);
            const bool is_upper = d >= 0x80 && d <= 0x9E && d != 0x97;
            const bool is_lower = d >= 0xA0 && d <= 0xBE && d != 0xB7;
            const bool to_upper = upper || (capitalize && at_word_start);
            if (to_upper && is_lower) d = static_cast<unsigned char>(d - 0x20);
            else if (lower && is_upper) d = static_cast<unsigned char>(d + 0x20);
            out.push_back(static_cast<char>(c));
            out.push_back(static_cast<char>(d));
            at_word_start = false;
            ++i;
            continue;
        }
        if (c >= 0x80) {
            out.push_back(static_cast<char>(c));
            at_word_start = false;
            continue;
        }
        const bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        const bool digit = c >= '0' && c <= '9';
        char o = static_cast<char>(c);
        if (upper || (capitalize && at_word_start)) {
            if (c >= 'a' && c <= 'z') o = static_cast<char>(c - 'a' + 'A');
        } else if (lower) {
            if (c >= 'A' && c <= 'Z') o = static_cast<char>(c - 'A' + 'a');
        }
        out.push_back(o);
        // Capitalize acts on the first typographic letter unit of each word;
        // a word starts after whitespace or punctuation (§2.1 "word" per UA).
        at_word_start = !(letter || digit || c == '\'');
    }
    return tree_->own_text(std::move(out));
}

BoxId BoxBuilder::new_block_box_for(DisplayKind display, const Element* e,
                                    const ComputedStyle* style) {
    const BoxId id = tree_->create(BoxKind::Block, e, style);
    Box& b = (*tree_)[id];
    b.display = display;
    // The inline-* displays are block containers whose OUTER display is inline:
    // they lay out their contents as a block/flex/grid/table but participate in
    // the parent's inline formatting context.
    b.is_inline_block = is_inline_level_block(display);
    if ((display == DisplayKind::Block || display == DisplayKind::FlowRoot ||
         display == DisplayKind::ListItem) &&
        is_multicol_container(style)) {
        b.is_multicol = true;
    }
    return id;
}

BoxId BoxBuilder::build(const Element& root, const ComputedStyle* root_style) {
    const DisplayKind display = parse_display(get(root_style, "display"));
    if (display == DisplayKind::None) return kNoBox;

    const BoxId id = new_block_box_for(display, &root, root_style);
    build_children(root, root_style, id);
    return id;
}

BoxId BoxBuilder::build_document(const Document& doc) {
    // Neither element nor style: this stands in for the initial containing
    // block, not for `<html>`.
    const BoxId root = tree_->create(BoxKind::Block, nullptr, nullptr);
    for (const Ref<Node>& child : doc.children()) {
        append_node_as_block_child(*child, nullptr, root);
    }
    finalize_block_children(root);
    return root;
}

void BoxBuilder::append_node_as_block_child(const Node& node, const ComputedStyle* parent_style,
                                            BoxId parent) {
    if (node.node_type() == NodeType::Text) {
        const auto& tn = static_cast<const TextNode&>(node);
        const BoxId id = tree_->create(BoxKind::Text, (*tree_)[parent].element, parent_style);
        Box& b = (*tree_)[id];
        b.text = transformed_text(tn.data(), parent_style);
        b.source_node = &tn;
        tree_->append_child(parent, id);
        return;
    }
    if (node.node_type() != NodeType::Element) return;

    const auto& e = static_cast<const Element&>(node);
    const ComputedStyle* style = styles_ ? styles_->style_of(e) : nullptr;
    DisplayKind disp = parse_display(get(style, "display"));
    if (disp == DisplayKind::None) return;

    const bool blockify = blockifies_children((*tree_)[parent].display);

    // CSS 2.1 §9.7: an out-of-flow or floated element with an inline outer
    // display is blockified. Authors routinely write
    // `<span style="float:left">` and expect block-flow semantics; without
    // this the box would be an inline box and block layout would never see it
    // as a float.
    //
    // Excluded inside a flex or grid container, whose items cannot float and
    // whose children are blockified below anyway (CSS Flexbox §3, Grid §6.4).
    //
    // Every inline-LEVEL display blockifies, not only `inline`: a
    // `position: absolute` <button> is inline-block by the UA sheet, and left
    // as one it stayed an inline atom, was placed on a line box, and the dump
    // reported it one level deeper than the reference — for every absolutely
    // positioned button in the corpus.
    if (!blockify && (disp == DisplayKind::Inline || is_inline_level_block(disp)) &&
        (is_out_of_flow_position(style) || is_floated(style))) {
        disp = disp == DisplayKind::Inline ? DisplayKind::Block : blockified(disp);
    }

    if (establishes_block_box(disp) || is_inline_level_block(disp) || is_table_display(disp)) {
        // A flex or grid container forces its children's OUTER display to
        // block. Without this, the anonymous-block pass sees the is_inline_block
        // flag and sweeps consecutive items into ONE anonymous wrapper, so a
        // row of flex items becomes a single item and per-item sizing is never
        // applied to any of them.
        const DisplayKind used = blockify ? blockified(disp) : disp;
        const BoxId bb = new_block_box_for(used, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    if (disp == DisplayKind::Contents) {
        // The element generates no box; its children take its place, styled by
        // it for inheritance purposes.
        for (const Ref<Node>& c : e.children()) append_node_as_block_child(*c, style, parent);
        return;
    }

    if (blockify) {
        // `display: inline` inside a flex or grid container becomes a
        // block-level item.
        const BoxId bb = new_block_box_for(DisplayKind::Block, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    const BoxId ib = tree_->create(BoxKind::Inline, &e, style);
    (*tree_)[ib].display = disp;
    build_inline_children(e, style, ib);
    tree_->append_child(parent, ib);
}

void BoxBuilder::build_children(const Element& element, const ComputedStyle* style,
                                BoxId parent) {
    ++element_depth_;
    apply_counters(style, element_depth_);
    inject_pseudo(element, style, parent, "before");
    for (const Ref<Node>& c : element.children()) {
        append_node_as_block_child(*c, style, parent);
    }
    inject_pseudo(element, style, parent, "after");
    close_counters(element_depth_);
    --element_depth_;
    finalize_block_children(parent);
}

void BoxBuilder::build_inline_children(const Element& element, const ComputedStyle* style,
                                       BoxId parent) {
    ++element_depth_;
    apply_counters(style, element_depth_);
    inject_pseudo(element, style, parent, "before");
    for (const Ref<Node>& c : element.children()) {
        append_inline_child(*c, style, parent);
    }
    inject_pseudo(element, style, parent, "after");
    close_counters(element_depth_);
    --element_depth_;
}

// ---- counters and quotes -----------------------------------------------------

namespace {

// `name [<integer>]` pairs, or `none`.
std::vector<std::pair<std::string, std::optional<int>>> parse_counter_list(std::string_view raw) {
    std::vector<std::pair<std::string, std::optional<int>>> out;
    size_t i = 0;
    const auto skip_ws = [&] { while (i < raw.size() && std::isspace(static_cast<unsigned char>(raw[i]))) ++i; };
    const auto token = [&] {
        const size_t s = i;
        while (i < raw.size() && !std::isspace(static_cast<unsigned char>(raw[i]))) ++i;
        return raw.substr(s, i - s);
    };
    skip_ws();
    while (i < raw.size()) {
        const std::string_view name = token();
        if (name.empty()) break;
        if (equals_ignoring_case(name, "none")) return {};
        skip_ws();
        std::optional<int> n;
        if (i < raw.size() && (std::isdigit(static_cast<unsigned char>(raw[i])) || raw[i] == '-' || raw[i] == '+')) {
            const size_t s = i;
            const std::string_view t = token();
            char* end = nullptr;
            const std::string tmp(t);
            const long v = std::strtol(tmp.c_str(), &end, 10);
            if (end && *end == '\0') n = static_cast<int>(v);
            else i = s, token();   // not a number after all: treat as the next name
            skip_ws();
        }
        out.emplace_back(std::string(name), n);
    }
    return out;
}

std::string roman(int n, bool upper) {
    if (n <= 0 || n >= 4000) return std::to_string(n);
    static const std::pair<int, const char*> table[] = {
        {1000, "m"}, {900, "cm"}, {500, "d"}, {400, "cd"}, {100, "c"}, {90, "xc"}, {50, "l"},
        {40, "xl"}, {10, "x"}, {9, "ix"}, {5, "v"}, {4, "iv"}, {1, "i"}};
    std::string s;
    for (const auto& [v, sym] : table) {
        while (n >= v) { s += sym; n -= v; }
    }
    if (upper) for (char& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return s;
}

std::string alpha(int n, bool upper) {
    if (n <= 0) return std::to_string(n);
    std::string s;
    while (n > 0) {
        --n;
        s.insert(s.begin(), static_cast<char>((upper ? 'A' : 'a') + n % 26));
        n /= 26;
    }
    return s;
}

std::string format_counter(int value, std::string_view style) {
    if (style.empty() || equals_ignoring_case(style, "decimal")) return std::to_string(value);
    if (equals_ignoring_case(style, "none")) return "";
    if (equals_ignoring_case(style, "lower-roman")) return roman(value, false);
    if (equals_ignoring_case(style, "upper-roman")) return roman(value, true);
    if (equals_ignoring_case(style, "lower-alpha") || equals_ignoring_case(style, "lower-latin")) return alpha(value, false);
    if (equals_ignoring_case(style, "upper-alpha") || equals_ignoring_case(style, "upper-latin")) return alpha(value, true);
    if (equals_ignoring_case(style, "decimal-leading-zero")) {
        return (value >= 0 && value < 10 ? "0" : "") + std::to_string(value);
    }
    if (equals_ignoring_case(style, "disc")) return "\xE2\x80\xA2";
    if (equals_ignoring_case(style, "circle")) return "\xE2\x97\xA6";
    if (equals_ignoring_case(style, "square")) return "\xE2\x96\xAA";
    return std::to_string(value);
}

std::string ident_of(const CssValue& v) {
    if (v.kind() == CssValueKind::Identifier) return static_cast<const CssIdentifier&>(v).name;
    if (v.kind() == CssValueKind::Keyword) return static_cast<const CssKeyword&>(v).name;
    if (v.kind() == CssValueKind::String) return static_cast<const CssString&>(v).text;
    if (v.kind() == CssValueKind::List) {
        const auto& l = static_cast<const CssValueList&>(v);
        if (!l.items.empty() && l.items[0]) return ident_of(*l.items[0]);
    }
    return {};
}

// `quotes`: pairs of strings; `auto` (and the initial value) is the English
// typographic pair; `none` is no text at all (but the depth still moves).
struct QuotePairs {
    std::vector<std::pair<std::string, std::string>> pairs;
    bool none = false;
};

QuotePairs parse_quotes(std::string_view raw) {
    QuotePairs q;
    if (raw.empty() || equals_ignoring_case(raw, "auto") || equals_ignoring_case(raw, "initial") ||
        equals_ignoring_case(raw, "inherit") || equals_ignoring_case(raw, "unset")) {
        q.pairs = {{"\xE2\x80\x9C", "\xE2\x80\x9D"}, {"\xE2\x80\x98", "\xE2\x80\x99"}};
        return q;
    }
    if (equals_ignoring_case(raw, "none")) {
        q.none = true;
        return q;
    }
    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    std::vector<std::string> strings;
    const auto collect = [&](const CssValue& x, auto& self) -> void {
        if (x.kind() == CssValueKind::String) strings.push_back(static_cast<const CssString&>(x).text);
        else if (x.kind() == CssValueKind::List) {
            for (const CssValuePtr& it : static_cast<const CssValueList&>(x).items) if (it) self(*it, self);
        }
    };
    if (v) collect(*v, collect);
    for (size_t i = 0; i + 1 < strings.size(); i += 2) q.pairs.emplace_back(strings[i], strings[i + 1]);
    if (q.pairs.empty()) q.pairs = {{"\xE2\x80\x9C", "\xE2\x80\x9D"}, {"\xE2\x80\x98", "\xE2\x80\x99"}};
    return q;
}

} // namespace

void BoxBuilder::apply_counters(const ComputedStyle* style, int depth) {
    if (!style) return;
    // Order per CounterContext.cs: reset (opens scopes), then increment, then set.
    for (const auto& [name, n] : parse_counter_list(get(style, "counter-reset"))) {
        counters_.push_back({name, n.value_or(0), depth});
    }
    const auto innermost = [&](const std::string& name) -> CounterScope* {
        for (size_t i = counters_.size(); i-- > 0;) {
            if (counters_[i].name == name) return &counters_[i];
        }
        return nullptr;
    };
    for (const auto& [name, n] : parse_counter_list(get(style, "counter-increment"))) {
        CounterScope* s = innermost(name);
        if (!s) {
            // §12.4: an increment with no scope behaves as if reset to 0 here.
            counters_.push_back({name, 0, depth});
            s = &counters_.back();
        }
        s->value += n.value_or(1);
    }
    for (const auto& [name, n] : parse_counter_list(get(style, "counter-set"))) {
        CounterScope* s = innermost(name);
        if (!s) {
            counters_.push_back({name, 0, depth});
            s = &counters_.back();
        }
        s->value = n.value_or(0);
    }
}

void BoxBuilder::close_counters(int depth) {
    // Scopes opened at this depth or deeper end with the element. Following
    // siblings start over, which is what CounterContext.cs does too (the
    // sibling-inheritance of css-lists-3 is not modelled by either side).
    while (!counters_.empty() && counters_.back().depth >= depth) counters_.pop_back();
}

bool BoxBuilder::resolve_content(const ComputedStyle* ps, const Element& host, std::string* out) {
    const std::string_view raw = get(ps, "content");
    if (raw.empty() || equals_ignoring_case(raw, "none") || equals_ignoring_case(raw, "normal")) {
        return false;
    }
    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return false;
    if (v->kind() == CssValueKind::Keyword || v->kind() == CssValueKind::Identifier) {
        const std::string name = ident_of(*v);
        if (equals_ignoring_case(name, "none") || equals_ignoring_case(name, "normal") ||
            equals_ignoring_case(name, "inherit") || equals_ignoring_case(name, "initial") ||
            equals_ignoring_case(name, "unset")) {
            return false;
        }
    }
    out->clear();
    std::optional<QuotePairs> quotes;
    const auto quote_pairs = [&]() -> const QuotePairs& {
        if (!quotes) quotes = parse_quotes(get(ps, "quotes"));
        return *quotes;
    };
    const auto values_of = [&](const std::string& name, std::vector<int>* vals) {
        for (const CounterScope& s : counters_) if (s.name == name) vals->push_back(s.value);
    };
    const auto append = [&](const CssValue& x, auto& self) -> void {
        switch (x.kind()) {
        case CssValueKind::String:
            *out += static_cast<const CssString&>(x).text;
            return;
        case CssValueKind::List:
            for (const CssValuePtr& it : static_cast<const CssValueList&>(x).items) if (it) self(*it, self);
            return;
        case CssValueKind::Keyword:
        case CssValueKind::Identifier: {
            const std::string kw = ident_of(x);
            if (equals_ignoring_case(kw, "open-quote")) {
                const QuotePairs& q = quote_pairs();
                if (!q.none) {
                    const size_t idx = std::min<size_t>(static_cast<size_t>(quote_depth_), q.pairs.size() - 1);
                    *out += q.pairs[idx].first;
                }
                ++quote_depth_;
            } else if (equals_ignoring_case(kw, "close-quote")) {
                if (quote_depth_ > 0) --quote_depth_;
                const QuotePairs& q = quote_pairs();
                if (!q.none) {
                    const size_t idx = std::min<size_t>(static_cast<size_t>(quote_depth_), q.pairs.size() - 1);
                    *out += q.pairs[idx].second;
                }
            } else if (equals_ignoring_case(kw, "no-open-quote")) {
                ++quote_depth_;
            } else if (equals_ignoring_case(kw, "no-close-quote")) {
                if (quote_depth_ > 0) --quote_depth_;
            }
            return;
        }
        case CssValueKind::FunctionCall: {
            const auto& f = static_cast<const CssFunctionCall&>(x);
            const auto arg = [&](size_t i) -> std::string {
                return i < f.arguments.size() && f.arguments[i] ? ident_of(*f.arguments[i]) : std::string();
            };
            if (f.name == "attr") {
                const std::string a = arg(0);
                if (!a.empty()) *out += std::string(host.get_attribute(a));
            } else if (f.name == "counter") {
                std::vector<int> vals;
                values_of(arg(0), &vals);
                *out += format_counter(vals.empty() ? 0 : vals.back(), arg(1));
            } else if (f.name == "counters") {
                std::vector<int> vals;
                values_of(arg(0), &vals);
                const std::string sep = arg(1);
                const std::string style = arg(2);
                for (size_t i = 0; i < vals.size(); ++i) {
                    if (i) *out += sep;
                    *out += format_counter(vals[i], style);
                }
                if (vals.empty()) *out += format_counter(0, style);
            }
            // url() / image content: a box with no text.
            return;
        }
        default:
            return;
        }
    };
    append(*v, append);
    return true;
}

// Ports BoxBuilder.MaybeInjectPseudoElement + BuildPseudoBox. The pseudo box
// has no element (a pseudo is not one: the dump and hit-testing never see it)
// but remembers its host, and its text child carries the pseudo's own style.
// Blockification follows the element path exactly — an absolutely positioned
// or floated `::before { content: "" }` is the idiom for decorative overlays,
// and left inline it would never reach the width/height/inset it was given.
void BoxBuilder::inject_pseudo(const Element& host, const ComputedStyle* host_style, BoxId parent,
                               std::string_view name) {
    if (!styles_ || !host_style) return;
    const ComputedStyle* ps = styles_->pseudo_style_of(host, name);
    if (!ps) return;
    // The pseudo's own counter properties apply before its content is read;
    // a scope it opens closes with it (it has no descendants to see it).
    apply_counters(ps, element_depth_ + 1);
    std::string text;
    const bool has_content = resolve_content(ps, host, &text);
    close_counters(element_depth_ + 1);
    if (!has_content) return;

    DisplayKind disp = parse_display(get(ps, "display"));
    if (disp == DisplayKind::None) return;
    const bool blockify = blockifies_children((*tree_)[parent].display);
    if (!blockify && (disp == DisplayKind::Inline || is_inline_level_block(disp)) &&
        (is_out_of_flow_position(ps) || is_floated(ps))) {
        disp = disp == DisplayKind::Inline ? DisplayKind::Block : blockified(disp);
    }

    BoxId text_box = kNoBox;
    if (!text.empty()) {
        const std::string_view owned = tree_->own_text(std::move(text));
        text_box = tree_->create(BoxKind::Text, nullptr, ps);
        (*tree_)[text_box].text = transformed_text(owned, ps);
    }

    if (establishes_block_box(disp) || is_inline_level_block(disp) || is_table_display(disp) ||
        blockify) {
        DisplayKind used = disp;
        if (blockify) used = disp == DisplayKind::Inline ? DisplayKind::Block : blockified(disp);
        const BoxId bb = new_block_box_for(used, nullptr, ps);
        (*tree_)[bb].pseudo_host = &host;
        if (text_box != kNoBox) tree_->append_child(bb, text_box);
        // A raw text child of a flex/grid pseudo must become an anonymous
        // item, or the container sees no items and collapses.
        finalize_block_children(bb);
        tree_->append_child(parent, bb);
        return;
    }

    const BoxId ib = tree_->create(BoxKind::Inline, nullptr, ps);
    (*tree_)[ib].display = disp;
    (*tree_)[ib].pseudo_host = &host;
    if (text_box != kNoBox) tree_->append_child(ib, text_box);
    tree_->append_child(parent, ib);
}

void BoxBuilder::append_inline_child(const Node& node, const ComputedStyle* parent_style,
                                     BoxId parent) {
    if (node.node_type() == NodeType::Text) {
        const auto& tn = static_cast<const TextNode&>(node);
        const BoxId id = tree_->create(BoxKind::Text, (*tree_)[parent].element, parent_style);
        Box& b = (*tree_)[id];
        b.text = transformed_text(tn.data(), parent_style);
        b.source_node = &tn;
        tree_->append_child(parent, id);
        return;
    }
    if (node.node_type() != NodeType::Element) return;

    const auto& e = static_cast<const Element&>(node);
    const ComputedStyle* style = styles_ ? styles_->style_of(e) : nullptr;
    DisplayKind disp = parse_display(get(style, "display"));
    if (disp == DisplayKind::None) return;

    if (disp == DisplayKind::Contents) {
        for (const Ref<Node>& c : e.children()) append_inline_child(*c, style, parent);
        return;
    }

    // Same §9.7 blockification as the block path: a float nested inside an
    // inline box must still reach float layout rather than being folded into
    // the inline stream.
    if ((disp == DisplayKind::Inline || is_inline_level_block(disp)) &&
        (is_out_of_flow_position(style) || is_floated(style))) {
        disp = disp == DisplayKind::Inline ? DisplayKind::Block : blockified(disp);
    }

    if (establishes_block_box(disp) || is_inline_level_block(disp) || is_table_display(disp)) {
        const BoxId bb = new_block_box_for(disp, &e, style);
        build_children(e, style, bb);
        tree_->append_child(parent, bb);
        return;
    }

    const BoxId ib = tree_->create(BoxKind::Inline, &e, style);
    (*tree_)[ib].display = disp;
    build_inline_children(e, style, ib);
    tree_->append_child(parent, ib);
}

void BoxBuilder::flush_anonymous(BoxId parent, std::vector<BoxId>* inlines) {
    // A run that is nothing but whitespace generates no box. This is what stops
    // the newlines between block-level siblings in formatted HTML from
    // producing an empty anonymous block between every pair of them.
    bool all_whitespace = true;
    for (BoxId id : *inlines) {
        const Box& b = (*tree_)[id];
        if (b.kind != BoxKind::Text || !is_whitespace_only(b.text)) {
            all_whitespace = false;
            break;
        }
    }
    if (all_whitespace) return;

    const BoxId anon = tree_->create(BoxKind::AnonymousBlock, nullptr, nullptr);
    for (BoxId id : *inlines) tree_->append_child(anon, id);
    tree_->append_child(parent, anon);
}

// CSS 2.1 §9.2.1.1, the other half: an inline box that contains a block-level
// box is broken around it. The block becomes a block-level sibling of the
// inline box's pieces, and the pieces — the original box first, then clones
// carrying the same element and style — hold what came before and after. A
// `<card><span>title</span><p>body</p><button>ok</button></card>` custom
// element lays out as a line, a paragraph, a line; left whole, the `<p>` sat
// inside an inline box that inline layout could not lay out at all, 0x0.
// Out-of-flow and floated boxes stay where they are: they are not in flow.
namespace {

bool is_in_flow_block(const BoxTree& tree, BoxId id) {
    const Box& b = tree[id];
    if (b.kind != BoxKind::Block || b.is_inline_block) return false;
    if (b.style) {
        if (is_out_of_flow_position(b.style) || is_floated(b.style)) return false;
    }
    return true;
}

bool holds_in_flow_block(const BoxTree& tree, BoxId inline_box) {
    for (BoxId c : tree.children(inline_box)) {
        if (is_in_flow_block(tree, c)) return true;
        if (tree[c].kind == BoxKind::Inline && holds_in_flow_block(tree, c)) return true;
    }
    return false;
}

} // namespace

void BoxBuilder::split_inline_around_blocks(BoxId inline_box, std::vector<BoxId>* out) {
    std::vector<BoxId> kids;
    for (BoxId c : tree_->children(inline_box)) kids.push_back(c);
    tree_->clear_children(inline_box);

    BoxId piece = inline_box;
    // Every piece is a product of the split, the reused original included.
    (*tree_)[inline_box].is_split_fragment = true;
    const auto new_piece = [&] {
        const BoxId clone = tree_->create(BoxKind::Inline, (*tree_)[inline_box].element,
                                          (*tree_)[inline_box].style);
        (*tree_)[clone].display = (*tree_)[inline_box].display;
        (*tree_)[clone].is_split_fragment = true;
        return clone;
    };
    for (BoxId k : kids) {
        if (is_in_flow_block(*tree_, k)) {
            out->push_back(piece);
            out->push_back(k);
            piece = new_piece();
            continue;
        }
        if ((*tree_)[k].kind == BoxKind::Inline && holds_in_flow_block(*tree_, k)) {
            std::vector<BoxId> sub;
            split_inline_around_blocks(k, &sub);
            for (BoxId s : sub) {
                if (is_in_flow_block(*tree_, s)) {
                    out->push_back(piece);
                    out->push_back(s);
                    piece = new_piece();
                } else {
                    tree_->append_child(piece, s);
                }
            }
            continue;
        }
        tree_->append_child(piece, k);
    }
    out->push_back(piece);
}

void BoxBuilder::finalize_block_children(BoxId parent) {
    // CSS 2.1 §9.2.1.1: a block container holds either only inline-level boxes
    // or only block-level ones. Where an author mixes them, each run of
    // consecutive inline children is wrapped in an anonymous block, so every
    // layout pass below can assume one case or the other.
    if ((*tree_)[parent].first_child == kNoBox) {
        (*tree_)[parent].contains_inlines = false;
        return;
    }

    // First, inline boxes holding blocks are broken around them, so the
    // classification below sees the blocks as the parent's own children.
    if (!blockifies_children((*tree_)[parent].display)) {
        bool any_split = false;
        for (BoxId c : tree_->children(parent)) {
            if ((*tree_)[c].kind == BoxKind::Inline && holds_in_flow_block(*tree_, c)) {
                any_split = true;
                break;
            }
        }
        if (any_split) {
            std::vector<BoxId> original;
            for (BoxId c : tree_->children(parent)) original.push_back(c);
            tree_->clear_children(parent);
            for (BoxId c : original) {
                if ((*tree_)[c].kind == BoxKind::Inline && holds_in_flow_block(*tree_, c)) {
                    std::vector<BoxId> pieces;
                    split_inline_around_blocks(c, &pieces);
                    for (BoxId p : pieces) tree_->append_child(parent, p);
                } else {
                    tree_->append_child(parent, c);
                }
            }
        }
    }

    const auto is_block_level = [this](BoxId id) {
        const Box& b = (*tree_)[id];
        // An anonymous block is deliberately excluded: it is a product of this
        // pass, never an input to the classification.
        return b.kind == BoxKind::Block && !b.is_inline_block;
    };
    const auto is_inline_level = [this](BoxId id) {
        const Box& b = (*tree_)[id];
        return b.kind == BoxKind::Inline || b.kind == BoxKind::AnonymousInline ||
               b.kind == BoxKind::Text || (b.kind == BoxKind::Block && b.is_inline_block);
    };

    bool any_block = false;
    bool any_inline = false;
    for (BoxId c : tree_->children(parent)) {
        if (is_block_level(c)) any_block = true;
        else if (is_inline_level(c)) any_inline = true;
    }

    if (!any_block) {
        // CSS Flexbox §4 / Grid §6: text directly inside a flex or grid
        // container is wrapped in an anonymous item. Element children were
        // blockified on the way in; raw text bypasses that branch, and without
        // the wrap the container sees zero items and collapses to its padding.
        if (any_inline && blockifies_children((*tree_)[parent].display)) {
            existing_.clear();
            for (BoxId c : tree_->children(parent)) existing_.push_back(c);
            tree_->clear_children(parent);
            flush_anonymous(parent, &existing_);
            existing_.clear();
            (*tree_)[parent].contains_inlines = false;
            return;
        }
        (*tree_)[parent].contains_inlines = true;
        return;
    }
    if (!any_inline) {
        (*tree_)[parent].contains_inlines = false;
        return;
    }

    existing_.clear();
    for (BoxId c : tree_->children(parent)) existing_.push_back(c);
    tree_->clear_children(parent);

    current_inlines_.clear();
    for (BoxId c : existing_) {
        if (is_block_level(c)) {
            if (!current_inlines_.empty()) {
                flush_anonymous(parent, &current_inlines_);
                current_inlines_.clear();
            }
            tree_->append_child(parent, c);
        } else {
            current_inlines_.push_back(c);
        }
    }
    if (!current_inlines_.empty()) {
        flush_anonymous(parent, &current_inlines_);
        current_inlines_.clear();
    }
    existing_.clear();
    (*tree_)[parent].contains_inlines = false;
}

} // namespace weva
