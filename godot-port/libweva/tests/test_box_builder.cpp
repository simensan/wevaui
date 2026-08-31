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
        CHECK(f.css("#c { display: contents } #a, #b { display: block }"));
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
