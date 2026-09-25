#include "check.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/html.h"
#include <memory>
#include <string>
#include <vector>

using namespace weva;

namespace {

// Computes every element's style once up front, which is what the layout
// engine's styleOf callback amounts to.
struct CascadeStyles : StyleProvider {
    CascadeEngine engine;
    NullStateProvider state;
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    std::map<const Element*, ComputedStyle*> by_element;

    void compute_tree(const Element& e, const ComputedStyle* parent) {
        auto cs = std::make_unique<ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element) {
                compute_tree(static_cast<const Element&>(*c), raw);
            }
        }
    }
    const ComputedStyle* style_of(const Element& e) override {
        auto it = by_element.find(&e);
        return it == by_element.end() ? nullptr : it->second;
    }
    std::map<std::pair<const Element*, std::string>, std::unique_ptr<ComputedStyle>> pseudos;
    const ComputedStyle* pseudo_style_of(const Element& e, std::string_view name) override {
        auto key = std::make_pair(&e, std::string(name));
        auto it = pseudos.find(key);
        if (it != pseudos.end()) return it->second.get();
        const ComputedStyle* host = style_of(e);
        if (!host) return nullptr;
        auto ps = std::make_unique<ComputedStyle>();
        if (!engine.compute_pseudo_element(e, name, state, *host, ps.get())) return nullptr;
        return (pseudos[key] = std::move(ps)).get();
    }
};

struct Fixture {
    SymbolTable symbols;
    Ref<Document> doc;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    CascadeStyles styles;
    BoxTree tree;

    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        styles.engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    // Parses, cascades and builds in one step. CSS must be added first.
    BoxId build(std::string_view html) {
        HtmlParseError he;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(html, &symbols, o, &he);
        if (!doc) return kNoBox;
        for (const Ref<Node>& c : doc->children()) {
            if (c->node_type() == NodeType::Element) {
                styles.compute_tree(static_cast<const Element&>(*c), nullptr);
            }
        }
        BoxBuilder builder(&tree, &styles);
        return builder.build_document(*doc);
    }
    // The box for the element with this id, found by walking the built tree.
    BoxId find(BoxId from, std::string_view element_id) const {
        const Box& b = tree[from];
        if (b.element && b.element->get_attribute("id") == element_id) return from;
        for (BoxId c : tree.children(from)) {
            const BoxId hit = find(c, element_id);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
    std::vector<BoxKind> child_kinds(BoxId parent) const {
        std::vector<BoxKind> out;
        for (BoxId c : tree.children(parent)) out.push_back(tree[c].kind);
        return out;
    }
};

} // namespace

void test_box_builder_display() {
    {
        Fixture f;
        CHECK(f.css("#b { display: block } #i { display: inline }"
                    "#n { display: none } #ib { display: inline-block }"
                    "#fx { display: flex } #t { display: table-cell }"));
        const BoxId root = f.build("<div id=b></div><span id=i></span><div id=n></div>"
                                   "<span id=ib></span><div id=fx></div><div id=t></div>");
        CHECK(root != kNoBox);
        CHECK(f.tree[f.find(root, "b")].kind == BoxKind::Block);
        CHECK(f.tree[f.find(root, "i")].kind == BoxKind::Inline);
        // `display: none` generates no box at all, not an empty one.
        CHECK(f.find(root, "n") == kNoBox);
        // inline-block is a BLOCK box whose outer display is inline.
        const BoxId ib = f.find(root, "ib");
        CHECK(f.tree[ib].kind == BoxKind::Block && f.tree[ib].is_inline_block);
        CHECK(f.tree[f.find(root, "fx")].display == DisplayKind::Flex);
        CHECK(f.tree[f.find(root, "t")].display == DisplayKind::TableCell);
    }
    {
        // An unrecognised display behaves as the initial value — a typo should
        // not delete content.
        CHECK(parse_display("bogus") == DisplayKind::Inline);
        CHECK(parse_display("") == DisplayKind::Inline);
        CHECK(parse_display("  BLOCK  ") == DisplayKind::Block);
        CHECK(parse_display("inline-flex") == DisplayKind::InlineFlex);
    }
    {
        // display: contents generates no box; the children take its place in
        // the parent.
        Fixture f;
        // #w is block: this fixture has no UA sheet, and an INLINE box holding
        // blocks is broken around them (§9.2.1.1), which is a different test.
        CHECK(f.css("#w { display: block } #c { display: contents } #a, #b { display: block }"));
        const BoxId root = f.build("<div id=w><div id=c><div id=a></div><div id=b></div></div></div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.find(root, "c") == kNoBox);
        CHECK(f.tree.child_count(w) == 2);
        CHECK(f.tree[f.tree.child_at(w, 0)].element->get_attribute("id") == "a");
        CHECK(f.tree[f.tree.child_at(w, 1)].element->get_attribute("id") == "b");
    }
}

void test_box_builder_blockification() {
    {
        // CSS 2.1 §9.7: a floated or out-of-flow inline is blockified, so
        // block layout can see it as a float rather than folding it into the
        // inline stream.
        Fixture f;
        CHECK(f.css("#fl { float: left } #ab { position: absolute } #fx { position: fixed }"
                    "#plain { display: inline }"));
        const BoxId root = f.build("<div id=w><span id=fl></span><span id=ab></span>"
                                   "<span id=fx></span><span id=plain></span></div>");
        CHECK(f.tree[f.find(root, "fl")].kind == BoxKind::Block);
        CHECK(f.tree[f.find(root, "ab")].kind == BoxKind::Block);
        CHECK(f.tree[f.find(root, "fx")].kind == BoxKind::Block);
        CHECK(f.tree[f.find(root, "plain")].kind == BoxKind::Inline);
    }
    {
        // Every inline-LEVEL display blockifies when out of flow or floated,
        // not only `inline`: an absolutely positioned inline-block — a
        // <button> by the UA sheet — is a block-level box, not an atom on a
        // line. Left as an atom it sat one level deeper in the tree than the
        // reference, under a line box, for every positioned button in the
        // corpus.
        Fixture f;
        CHECK(f.css("#ib { display: inline-block; position: absolute }"
                    "#if { display: inline-flex; float: left }"));
        const BoxId root = f.build("<div id=w><span id=ib></span><span id=if></span></div>");
        CHECK(f.tree[f.find(root, "ib")].kind == BoxKind::Block);
        CHECK(!f.tree[f.find(root, "ib")].is_inline_block);
        CHECK(f.tree[f.find(root, "ib")].display == DisplayKind::Block);
        CHECK(f.tree[f.find(root, "if")].display == DisplayKind::Flex);
        CHECK(!f.tree[f.find(root, "if")].is_inline_block);
        // Nothing inline-level remains, so the wrapper holds them directly:
        // no anonymous block and no inline formatting context.
        const BoxId w = f.find(root, "w");
        CHECK(f.tree.child_count(w) == 2);
        CHECK(!f.tree[w].contains_inlines);
    }
    {
        // `float: none` is not a float, so the inline stays inline.
        Fixture f;
        CHECK(f.css("#nf { float: none; display: inline }"));
        const BoxId root = f.build("<div id=w><span id=nf></span></div>");
        CHECK(f.tree[f.find(root, "nf")].kind == BoxKind::Inline);
    }
    {
        // Flex and grid containers blockify every in-flow child, including
        // inline ones and the inline-* block containers. Without this the
        // anonymous-block pass sweeps a whole row of items into ONE wrapper and
        // per-item sizing is never applied to any of them.
        Fixture f;
        CHECK(f.css("#fx { display: flex } #s { display: inline }"
                    "#ib { display: inline-block } #if { display: inline-flex }"));
        const BoxId root = f.build("<div id=fx><span id=s></span><span id=ib></span>"
                                   "<span id=if></span></div>");
        const BoxId fx = f.find(root, "fx");
        CHECK(f.tree.child_count(fx) == 3);
        for (BoxId c : f.tree.children(fx)) {
            CHECK(f.tree[c].kind == BoxKind::Block);
            // Outer display is block now, so the anonymous pass treats each as
            // its own item.
            CHECK(!f.tree[c].is_inline_block);
        }
        CHECK(f.tree[f.find(root, "if")].display == DisplayKind::Flex);
        CHECK(f.tree[f.find(root, "ib")].display == DisplayKind::Block);
    }
    {
        // Floats inside a flex container are NOT blockified by the float rule —
        // flex items cannot float (CSS Flexbox §3) — but they are blockified as
        // items anyway, so the outcome is a block box either way.
        Fixture f;
        CHECK(f.css("#fx { display: flex } #s { float: left }"));
        const BoxId root = f.build("<div id=fx><span id=s></span></div>");
        CHECK(f.tree[f.find(root, "s")].kind == BoxKind::Block);
        // Blockified as an ITEM, not as a float — the box builder never stamps
        // float_type at all; block layout reads `float` from the style later.
    }
    {
        // A block-level box nested inside an inline box still gets a block box:
        // the inline parent does not suppress it.
        Fixture f;
        CHECK(f.css("#s { display: inline } #d { display: block }"));
        const BoxId root = f.build("<span id=s><div id=d></div></span>");
        CHECK(f.tree[f.find(root, "s")].kind == BoxKind::Inline);
        CHECK(f.tree[f.find(root, "d")].kind == BoxKind::Block);
    }
}

void test_anonymous_block_wrapping() {
    {
        // Mixed content: each RUN of consecutive inline children gets one
        // anonymous block, so the container's children alternate cleanly.
        Fixture f;
        CHECK(f.css("#w, #b1, #b2 { display: block } span { display: inline }"));
        const BoxId root = f.build("<div id=w>one<span>two</span><div id=b1></div>"
                                   "three<div id=b2></div>four</div>");
        const BoxId w = f.find(root, "w");
        CHECK(!f.tree[w].contains_inlines);
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({
            BoxKind::AnonymousBlock, BoxKind::Block,
            BoxKind::AnonymousBlock, BoxKind::Block,
            BoxKind::AnonymousBlock}));
        // The first wrapper holds BOTH inline children, not one each.
        CHECK(f.tree.child_count(f.tree.child_at(w, 0)) == 2);
    }
    {
        // All-inline content is left alone and flagged, not wrapped.
        Fixture f;
        CHECK(f.css("#w { display: block }"));
        const BoxId root = f.build("<div id=w>hello <span>there</span></div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.tree[w].contains_inlines);
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({BoxKind::Text, BoxKind::Inline}));
    }
    {
        // All-block content is left alone too.
        Fixture f;
        CHECK(f.css("#w, #a, #b { display: block }"));
        const BoxId root = f.build("<div id=w><div id=a></div><div id=b></div></div>");
        const BoxId w = f.find(root, "w");
        CHECK(!f.tree[w].contains_inlines);
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({BoxKind::Block, BoxKind::Block}));
    }
    {
        // The case that makes this pass worth having: the newlines between
        // block siblings in formatted HTML are text nodes. A run that is
        // entirely whitespace generates NO anonymous block, or every pair of
        // siblings would be separated by an empty one.
        Fixture f;
        CHECK(f.css("#w, #a, #b { display: block }"));
        const BoxId root = f.build("<div id=w>\n  <div id=a></div>\n  <div id=b></div>\n</div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({BoxKind::Block, BoxKind::Block}));
    }
    {
        // Whitespace that is part of a non-empty run is kept, because the run
        // as a whole is not whitespace-only.
        Fixture f;
        CHECK(f.css("#w, #a { display: block }"));
        const BoxId root = f.build("<div id=w> text <div id=a></div></div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({BoxKind::AnonymousBlock, BoxKind::Block}));
    }
    {
        // An empty container contains no inlines: "no children" is not "inline
        // children".
        Fixture f;
        CHECK(f.css("#w { display: block }"));
        const BoxId root = f.build("<div id=w></div>");
        CHECK(!f.tree[f.find(root, "w")].contains_inlines);
    }
    {
        // Raw text directly inside a flex container is wrapped in an anonymous
        // item. Element children were blockified on the way in; text bypasses
        // that branch, and without the wrap the container has zero items and
        // collapses to its padding.
        Fixture f;
        CHECK(f.css("#fx { display: flex }"));
        const BoxId root = f.build("<div id=fx>bare text</div>");
        const BoxId fx = f.find(root, "fx");
        CHECK(f.child_kinds(fx) == std::vector<BoxKind>({BoxKind::AnonymousBlock}));
        CHECK(!f.tree[fx].contains_inlines);
    }
    {
        // ...but whitespace-only text in a flex container still wraps to
        // nothing, leaving a container with no items rather than one empty one.
        Fixture f;
        CHECK(f.css("#fx { display: flex }"));
        const BoxId root = f.build("<div id=fx>   </div>");
        const BoxId fx = f.find(root, "fx");
        CHECK(f.tree.child_count(fx) == 0);
        CHECK(!f.tree[fx].contains_inlines);
    }
    {
        // An inline-block sibling counts as INLINE for this classification, so
        // it joins the anonymous wrapper rather than standing alone.
        Fixture f;
        CHECK(f.css("#w, #b { display: block } #ib { display: inline-block }"));
        const BoxId root = f.build("<div id=w><span id=ib></span><div id=b></div></div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.child_kinds(w) == std::vector<BoxKind>({BoxKind::AnonymousBlock, BoxKind::Block}));
        CHECK(f.tree[f.tree.child_at(w, 0)].first_child == f.find(root, "ib"));
    }
}

void test_box_builder_text_and_multicol() {
    {
        // A text run borrows its parent's element and style: it has no element
        // of its own, but paint needs both.
        Fixture f;
        CHECK(f.css("#w { display: block; color: red }"));
        const BoxId root = f.build("<div id=w>hello</div>");
        const BoxId w = f.find(root, "w");
        const BoxId t = f.tree.child_at(w, 0);
        CHECK(f.tree[t].kind == BoxKind::Text);
        CHECK(f.tree[t].text == "hello");
        CHECK(f.tree[t].element == f.tree[w].element);
        CHECK(f.tree[t].style == f.tree[w].style);
        CHECK(f.tree[t].source_node != nullptr);
    }
    {
        // CSS Multi-column §2: a non-auto column-count or column-width makes a
        // BLOCK container a multicol container. Flex, grid and table containers
        // ignore the column properties.
        Fixture f;
        CHECK(f.css("#a { display: block; column-count: 3 }"
                    "#b { display: block; column-width: 200px }"
                    "#c { display: block; column-count: auto }"
                    "#d { display: flex; column-count: 3 }"));
        const BoxId root = f.build("<div id=a></div><div id=b></div>"
                                   "<div id=c></div><div id=d></div>");
        CHECK(f.tree[f.find(root, "a")].is_multicol);
        CHECK(f.tree[f.find(root, "b")].is_multicol);
        CHECK(!f.tree[f.find(root, "c")].is_multicol);
        CHECK(!f.tree[f.find(root, "d")].is_multicol);
    }
    {
        // The document root box stands in for the initial containing block: it
        // has neither element nor style, and `<html>` is its child.
        Fixture f;
        const BoxId root = f.build("<div id=a></div>");
        CHECK(f.tree[root].element == nullptr && f.tree[root].style == nullptr);
        CHECK(f.tree.child_count(root) == 1);
        CHECK(f.tree[f.tree.child_at(root, 0)].element->tag_name() == "html");
    }
}

void test_block_in_inline_splitting() {
    // §9.2.1.1: an inline box holding a block is broken around it. The block
    // becomes a block-level child of the container; the inline's pieces —
    // the original box and a clone with the same element and style — hold
    // what came before and after, each wrapped in an anonymous block.
    {
        Fixture f;
        CHECK(f.css("#w { display: block } card { display: inline } p { display: block }"));
        const BoxId root = f.build("<div id=w><card id=c><span id=s>t</span><p id=p>b</p>"
                                   "<button id=b>ok</button></card></div>");
        const BoxId w = f.find(root, "w");
        const std::vector<BoxKind> kinds = f.child_kinds(w);
        CHECK(kinds.size() == 3);
        CHECK(kinds[0] == BoxKind::AnonymousBlock);
        CHECK(kinds[1] == BoxKind::Block);
        CHECK(kinds[2] == BoxKind::AnonymousBlock);
        CHECK(f.tree[f.find(root, "p")].parent == w);
        // Both pieces are inline boxes of the card element.
        int card_pieces = 0;
        for (BoxId anon : f.tree.children(w)) {
            for (BoxId c : f.tree.children(anon)) {
                if (f.tree[c].kind == BoxKind::Inline && f.tree[c].element &&
                    f.tree[c].element->get_attribute("id") == "c") {
                    ++card_pieces;
                }
            }
        }
        CHECK(card_pieces == 2);
        CHECK(!f.tree[w].contains_inlines);
    }
    {
        // Nested: <a><b><div/></b></a> splits both levels; an out-of-flow
        // block inside the inline is left where it is.
        Fixture f;
        CHECK(f.css("a, b { display: inline } div { display: block } #z { position: absolute }"));
        const BoxId root = f.build("<div id=w><a id=a>x<b id=bb>y<div id=d></div>z</b></a>"
                                   "<span id=s>q<div id=z></div></span></div>");
        const BoxId w = f.find(root, "w");
        CHECK(f.tree[f.find(root, "d")].parent == w);
        const std::vector<BoxKind> kinds = f.child_kinds(w);
        CHECK(kinds.size() == 3);   // anon(a>b piece 1), div, anon(a>b piece 2, span)
        CHECK(f.tree[f.find(root, "z")].parent == f.find(root, "s"));
    }
}

// CSS 2.1 §12.1: ::before/::after generate boxes as the host's first and last
// children, styled by the pseudo's own cascade, with no element of their own.
void test_pseudo_element_boxes() {
    {
        Fixture f;
        CHECK(f.css("div { display: block }"
                    "#a::before { content: 'B' } #a::after { content: 'A'; display: block }"));
        const BoxId root = f.build("<div id=a>x</div>");
        const BoxId a = f.find(root, "a");
        CHECK(a != kNoBox);
        // inline ::before + text → one anonymous block; block ::after follows.
        CHECK((f.child_kinds(a) == std::vector<BoxKind>{BoxKind::AnonymousBlock, BoxKind::Block}));
        const BoxId anon = f.tree[a].first_child;
        const BoxId before = f.tree[anon].first_child;
        CHECK(f.tree[before].kind == BoxKind::Inline);
        CHECK(f.tree[before].element == nullptr);
        CHECK(f.tree[before].pseudo_host == f.tree[a].element);
        CHECK(f.tree[f.tree[before].first_child].text == "B");
        CHECK(f.tree[f.tree[before].first_child].style == f.tree[before].style);
        const BoxId after = f.tree[a].last_child;
        CHECK(f.tree[after].pseudo_host == f.tree[a].element);
        CHECK(f.tree[after].element == nullptr);
        CHECK(f.tree[after].contains_inlines);
        CHECK(f.tree[f.tree[after].first_child].text == "A");
    }
    {
        // No rule, `content: none`, and `display: none` all generate nothing;
        // `content: ""` still generates an (empty) box.
        Fixture f;
        CHECK(f.css("div { display: block }"
                    "#n::before { content: none } #d::after { content: 'x'; display: none }"
                    "#e::before { content: '' }"));
        const BoxId root = f.build("<div id=n>t</div><div id=d>t</div><div id=e>t</div>"
                                   "<div id=z>t</div>");
        CHECK(f.child_kinds(f.find(root, "n")) == std::vector<BoxKind>{BoxKind::Text});
        CHECK(f.child_kinds(f.find(root, "d")) == std::vector<BoxKind>{BoxKind::Text});
        CHECK(f.child_kinds(f.find(root, "z")) == std::vector<BoxKind>{BoxKind::Text});
        const BoxId e = f.find(root, "e");
        CHECK((f.child_kinds(e) == std::vector<BoxKind>{BoxKind::Inline, BoxKind::Text}));
        CHECK(f.tree[f.tree[e].first_child].first_child == kNoBox);
    }
    {
        // §9.7: an absolutely positioned or floated pseudo is blockified, so
        // the decorative-overlay idiom reaches block layout.
        Fixture f;
        CHECK(f.css("div { display: block }"
                    "#a::before { content: ''; position: absolute; width: 10px; height: 10px }"
                    "#b::after { content: ''; float: left }"));
        const BoxId root = f.build("<div id=a>t</div><div id=b>t</div>");
        const BoxId a = f.find(root, "a");
        CHECK(f.tree[f.tree[a].first_child].kind == BoxKind::Block);
        CHECK(f.tree[f.tree[a].first_child].pseudo_host != nullptr);
        CHECK(f.tree[f.tree[f.find(root, "b")].last_child].kind == BoxKind::Block);
    }
    {
        // A pseudo inside a flex container is an item like any child.
        Fixture f;
        CHECK(f.css("#fx { display: flex } #fx::after { content: 'tail' }"));
        const BoxId root = f.build("<div id=fx><span>a</span></div>");
        const BoxId fx = f.find(root, "fx");
        CHECK((f.child_kinds(fx) == std::vector<BoxKind>{BoxKind::Block, BoxKind::Block}));
        const BoxId item = f.tree[fx].last_child;
        CHECK(f.tree[item].pseudo_host != nullptr && !f.tree[item].is_inline_block);
        CHECK(f.tree[f.tree[item].first_child].text == "tail");
    }
    {
        // Inline hosts get pseudos too; attr() reads the host; strings
        // concatenate; text-transform applies to generated text.
        Fixture f;
        CHECK(f.css("p { display: block }"
                    "#s::before { content: '[' attr(data-k) ']'; text-transform: uppercase }"));
        const BoxId root = f.build("<p><span id=s data-k=ab>t</span></p>");
        const BoxId s = f.find(root, "s");
        CHECK((f.child_kinds(s) == std::vector<BoxKind>{BoxKind::Inline, BoxKind::Text}));
        CHECK(f.tree[f.tree[f.tree[s].first_child].first_child].text == "[AB]");
    }
    {
        // open-quote / close-quote resolve to the English pair (the UA
        // sheet's `q` rules; the fixture cascades author rules only).
        Fixture f;
        CHECK(f.css("p { display: block } q::before { content: open-quote }"
                    "q::after { content: close-quote }"));
        const BoxId root = f.build("<p><q id=q>w</q></p>");
        const BoxId q = f.find(root, "q");
        CHECK((f.child_kinds(q) == std::vector<BoxKind>{BoxKind::Inline, BoxKind::Text, BoxKind::Inline}));
        CHECK(f.tree[f.tree[f.tree[q].first_child].first_child].text == "\xE2\x80\x9C");
    }
}

// CSS 2.1 §12.4 / Generated Content §3: counter() / counters() and the quote
// keywords resolve against the walk's state (hand corpus 43 and 44).
void test_pseudo_counters_and_quotes() {
    const auto pseudo_text = [](const Fixture& f, BoxId host) {
        const BoxId p = f.tree[host].first_child;
        return std::string(f.tree[f.tree[p].first_child].text);
    };
    {
        Fixture f;
        CHECK(f.css("div, p { display: block }"
                    "#wrap { counter-reset: sec } .heading { counter-increment: sec }"
                    ".heading::before { content: counter(sec) '. ' }"
                    ".chapter { counter-reset: ch; counter-increment: ch }"
                    ".section { counter-reset: ch; counter-increment: ch }"
                    ".section2 { counter-reset: ch; counter-increment: ch 2 }"
                    ".leaf::before { content: counters(ch, '.') }"
                    "#r::before { content: counter(sec, upper-roman) '-' counter(sec, lower-alpha) }"));
        const BoxId root = f.build(
            "<div id=wrap><div id=h1 class=heading>A</div><div id=h2 class=heading>B</div>"
            "<div id=h3 class=heading>C</div>"
            "<div class=chapter><div class=section><span id=l1 class=leaf>a</span></div>"
            "<div class=section2><span id=l2 class=leaf>b</span></div></div>"
            "<div id=r class=heading>D</div></div>");
        CHECK_EQ(pseudo_text(f, f.find(root, "h1")), "1. ");
        CHECK_EQ(pseudo_text(f, f.find(root, "h2")), "2. ");
        CHECK_EQ(pseudo_text(f, f.find(root, "h3")), "3. ");
        // The section's scope nests inside the chapter's; the second
        // section starts a fresh inner scope rather than continuing.
        CHECK_EQ(pseudo_text(f, f.find(root, "l1")), "1.1");
        CHECK_EQ(pseudo_text(f, f.find(root, "l2")), "1.2");
        CHECK_EQ(pseudo_text(f, f.find(root, "r")), "IV-d");
    }
    {
        // An increment with no open scope resets to 0 first (§12.4).
        Fixture f;
        CHECK(f.css("div { display: block } #a { counter-increment: n 5 }"
                    "#a::after { content: counter(n) }"));
        const BoxId root = f.build("<div id=a>x</div>");
        const BoxId a = f.find(root, "a");
        CHECK_EQ(std::string(f.tree[f.tree[f.tree[a].last_child].first_child].text), "5");
    }
    {
        // Quotes: the `quotes` pairs by nesting depth, the English pair for
        // `auto`, nothing for `none`; depth carries across the document.
        Fixture f;
        CHECK(f.css("div, p { display: block } q::before { content: open-quote }"
                    "q::after { content: close-quote }"
                    "#w { quotes: '[' ']' '<' '>' } #n { quotes: none }"));
        const BoxId root = f.build("<div id=w><q id=o>a<q id=i>b</q></q></div>"
                                   "<p><q id=d>c</q></p><div id=n><q id=z>d</q></div>");
        CHECK_EQ(pseudo_text(f, f.find(root, "o")), "[");
        CHECK_EQ(pseudo_text(f, f.find(root, "i")), "<");
        CHECK_EQ(std::string(f.tree[f.tree[f.tree[f.find(root, "i")].last_child].first_child].text), ">");
        CHECK_EQ(std::string(f.tree[f.tree[f.tree[f.find(root, "o")].last_child].first_child].text), "]");
        CHECK_EQ(pseudo_text(f, f.find(root, "d")), "\xE2\x80\x9C");
        // `quotes: none` still generates the (empty) box.
        const BoxId z = f.find(root, "z");
        CHECK(f.tree[z].first_child != kNoBox && f.tree[f.tree[z].first_child].first_child == kNoBox);
    }
}

// A pseudo's `content` may come from a custom property set inline on the
// host (combat-hud's `style="--icon: '⚔'"` ability slots).
void test_pseudo_content_from_inline_custom_property() {
    Fixture f;
    CHECK(f.css("li { display: block } li::before { content: var(--icon) }"
                "#k::after { content: var(--missing, 'fb') }"));
    const BoxId root = f.build("<li id=a style=\"--icon: 'X'\">t</li><li id=k>t</li>");
    const BoxId a = f.find(root, "a");
    CHECK(f.tree[a].first_child != kNoBox && f.tree[f.tree[a].first_child].pseudo_host != nullptr);
    CHECK_EQ(std::string(f.tree[f.tree[f.tree[a].first_child].first_child].text), "X");
    const BoxId k = f.find(root, "k");
    CHECK_EQ(std::string(f.tree[f.tree[f.tree[k].last_child].first_child].text), "fb");
    // The token may live on an ancestor of the host (combat-hud sets --icon
    // on the <li>; the pseudo hangs off a child div).
    Fixture g;
    CHECK(g.css("li, div { display: block } .ic::before { content: var(--icon) }"));
    const BoxId r2 = g.build("<li style=\"--icon: 'Y'\"><div id=i class=ic></div></li>");
    const BoxId i = g.find(r2, "i");
    CHECK(g.tree[i].first_child != kNoBox);
    CHECK_EQ(std::string(g.tree[g.tree[g.tree[i].first_child].first_child].text), "Y");
}

// An element's own var() reads a custom property set in its inline style
// (level-select's `style="--c:#3f8ea3;--r:25deg"` roads).
void test_inline_custom_property_feeds_var() {
    Fixture f;
    CHECK(f.css("div { display: block; background-color: var(--c, blue); transform: rotate(var(--r)) }"));
    const BoxId root = f.build("<div id=a style=\"--c: red; --r: 25deg;\">t</div><div id=b>t</div>");
    const ComputedStyle* a = f.tree[f.find(root, "a")].style;
    const ComputedStyle* b = f.tree[f.find(root, "b")].style;
    CHECK_EQ(std::string(a->get("background-color")), "red");
    CHECK_EQ(std::string(a->get("transform")), "rotate(25deg)");
    CHECK_EQ(std::string(b->get("background-color")), "blue");
    // The compact form authors actually write: no spaces, hash colours,
    // several tokens, trailing semicolon.
    Fixture g;
    CHECK(g.css("div, span { display: block; background: var(--bg, #d8e6ef) }"
                "span { background: var(--c, #888); transform: translate(-50%, -50%) rotate(var(--r, 0deg)) }"));
    const BoxId r2 = g.build("<div id=m style=\"--bg:#cfe0c6;\"><span id=l style=\"--c:#7aa35a;--r:30deg;\"></span></div>");
    const ComputedStyle* m = g.tree[g.find(r2, "m")].style;
    const ComputedStyle* l = g.tree[g.find(r2, "l")].style;
    CHECK_EQ(std::string(m->get("background-color")), "#cfe0c6");
    CHECK_EQ(std::string(l->get("background-color")), "#7aa35a");
    CHECK_EQ(std::string(l->get("transform")), "translate(-50%, -50%) rotate(30deg)");
}

void test_text_transform_at_build() {
    // `text-transform` is applied when the run's box is built, so layout
    // measures the transformed text; the tree owns the new string.
    Fixture f;
    CHECK(f.css("#u { text-transform: uppercase; display: block }"
                "#l { text-transform: lowercase; display: block }"
                "#c { text-transform: capitalize; display: block }"));
    const BoxId root = f.build("<div id=u>View Full Ladder \xc3\xa9t\xc3\xa9</div>"
                               "<div id=l>ABC Def</div>"
                               "<div id=c>hello wide-world o'neil</div>");
    const auto text_of = [&](std::string_view id) {
        const BoxId b = f.find(root, id);
        for (BoxId c : f.tree.children(b)) {
            if (f.tree[c].kind == BoxKind::Text) return std::string(f.tree[c].text);
        }
        return std::string("<none>");
    };
    CHECK(text_of("u") == "VIEW FULL LADDER \xc3\x89T\xc3\x89");
    CHECK(text_of("l") == "abc def");
    CHECK(text_of("c") == "Hello Wide-World O'neil");
}
