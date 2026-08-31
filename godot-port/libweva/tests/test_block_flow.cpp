#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/user_agent_stylesheet.h"
#include <cmath>
#include <map>
#include <memory>
#include <string>

using namespace weva;

namespace {

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
    LayoutContext ctx;
    BoxId root = kNoBox;

    Fixture() {
        // The UA sheet is part of normal engine setup, not test scaffolding:
        // without it `display` is `inline` everywhere and there is no block
        // layout to test.
        auto ua = std::make_unique<Stylesheet>();
        CssParseError e;
        parse_stylesheet(user_agent_stylesheet_source(), false, ua.get(), &e);
        styles.engine.add_stylesheet(ua.get(), DeclarationOrigin::UserAgent);
        sheets.push_back(std::move(ua));
    }
    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        styles.engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    // Parses, cascades, builds and lays out against a 1000x600 viewport.
    bool layout(std::string_view html, double vw = 1000, double vh = 600) {
        HtmlParseError he;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(html, &symbols, o, &he);
        if (!doc) return false;
        for (const Ref<Node>& c : doc->children()) {
            if (c->node_type() == NodeType::Element) {
                styles.compute_tree(static_cast<const Element&>(*c), nullptr);
            }
        }
        BoxBuilder builder(&tree, &styles);
        root = builder.build_document(*doc);
        if (root == kNoBox) return false;
        BlockLayout bl(&tree, ctx);
        bl.layout_root(root, vw, vh);
        return true;
    }
    // Matches an `id` attribute, or a tag name for the elements that have no
    // id of their own (html, body).
    BoxId find(std::string_view id, BoxId from = -2) const {
        const BoxId start = from == -2 ? root : from;
        if (start == kNoBox) return kNoBox;
        const Box& b = tree[start];
        if (b.element && (b.element->get_attribute("id") == id || b.element->tag_name() == id)) {
            return start;
        }
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find(id, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
    const Box& box(std::string_view id) const { return tree[find(id)]; }
    double y(std::string_view id) const { return box(id).y; }
    double h(std::string_view id) const { return box(id).height; }
};

bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

} // namespace

void test_margin_collapse_rules() {
    // Both positive: the larger wins. Both negative: the more negative wins.
    // Mixed: the algebraic sum.
    CHECK(near(collapse_margins(20, 10), 20));
    CHECK(near(collapse_margins(10, 20), 20));
    CHECK(near(collapse_margins(-20, -10), -20));
    CHECK(near(collapse_margins(20, -10), 10));
    CHECK(near(collapse_margins(-20, 10), -10));
    CHECK(near(collapse_margins(0, 0), 0));

    // A NaN margin is treated as ABSENT rather than propagated. Without this
    // one bad calc() corrupts every block below it, since NaN fails both sign
    // tests and falls through to the sum.
    const double nan = std::nan("");
    CHECK(near(collapse_margins(nan, 10), 10));
    CHECK(near(collapse_margins(10, nan), 10));
    CHECK(near(collapse_margins(nan, nan), 0));
}

void test_block_stacking() {
    Fixture f;
    CHECK(f.css("#a { height: 50px } #b { height: 30px } #c { height: 20px }"));
    CHECK(f.layout("<body><div id=a></div><div id=b></div><div id=c></div></body>"));

    // Children stack in document order, each starting where the last ended.
    CHECK(near(f.y("a"), 0));
    CHECK(near(f.y("b"), 50));
    CHECK(near(f.y("c"), 80));
    // The UA sheet gives body `height: 100%`, so it fills the viewport rather
    // than shrinking to content — a deliberate departure from the browser
    // default, so `height: 100%` bottoms out at the viewport for authors.
    CHECK(near(f.h("body"), 600));
    // An auto width fills the containing block.
    CHECK(near(f.box("a").width, 1000));
}

void test_sibling_margin_collapse() {
    {
        // Adjoining sibling margins collapse to the larger, not the sum.
        Fixture f;
        CHECK(f.css("body, div { display: block; margin: 0 }"
                    "#a { height: 50px; margin-bottom: 30px }"
                    "#b { height: 30px; margin-top: 20px }"));
        CHECK(f.layout("<body><div id=a></div><div id=b></div></body>"));
        CHECK(near(f.y("b"), 80));   // 50 + max(30, 20), not 50 + 50
    }
    {
        // Mixed signs sum.
        Fixture f;
        CHECK(f.css("body, div { display: block; margin: 0 }"
                    "#a { height: 50px; margin-bottom: 30px }"
                    "#b { height: 30px; margin-top: -10px }"));
        CHECK(f.layout("<body><div id=a></div><div id=b></div></body>"));
        CHECK(near(f.y("b"), 70));   // 50 + (30 - 10)
    }
    {
        // Padding on the parent closes its top edge, so the first child's
        // margin sits INSIDE rather than collapsing out.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0; padding-top: 5px }"
                    "#a { display: block; height: 50px; margin-top: 30px }"));
        CHECK(f.layout("<body><div id=a></div></body>"));
        CHECK(near(f.y("a"), 35));
    }
}

void test_mixed_sign_chain() {
    // The case that makes pairwise folding wrong. Across a chain the result is
    // max(positives) + min(negatives): {+20, -15, +10, -25} gives -5, where
    // folding left with collapse_margins gives -10.
    //
    // Verified against the fold: collapse(collapse(collapse(20,-15),10),-25)
    //   = collapse(collapse(5,10),-25) = collapse(10,-25) = -15.
    // Either way it is not -5, which is why the chain tracks max and min.
    Fixture f;
    CHECK(f.css("body { display: block; margin: 0; padding-top: 1px }"
                "div { display: block; height: 40px }"
                "#a { margin-bottom: 20px } #b { margin-top: -15px; margin-bottom: 10px }"
                "#c { margin-top: -25px }"));
    CHECK(f.layout("<body><div id=a></div><div id=b></div><div id=c></div></body>"));

    // a: padding 1, height 40 -> bottom 41. Chain to b is {+20, -15} -> +5.
    CHECK(near(f.y("a"), 1));
    CHECK(near(f.y("b"), 46));
    // Chain to c is {+10, -25} -> max 10, min -25 -> -15.
    CHECK(near(f.y("c"), 71));
}

void test_parent_child_collapse() {
    {
        // With the parent's top open, the first child's margin collapses OUT
        // onto the parent, and the child sits flush at the inner edge.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0 }"
                    "#p { display: block } #a { display: block; height: 50px; margin-top: 30px }"));
        CHECK(f.layout("<body><div id=p><div id=a></div></div></body>"));
        CHECK(near(f.box("p").margin_top, 30));
        CHECK(near(f.y("a"), 0));
        CHECK(near(f.h("p"), 50));
    }
    {
        // A border closes the top, so the margin stays inside.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0 }"
                    "#p { display: block; border-top-style: solid; border-top-width: 2px }"
                    "#a { display: block; height: 50px; margin-top: 30px }"));
        CHECK(f.layout("<body><div id=p><div id=a></div></div></body>"));
        CHECK(near(f.box("p").margin_top, 0));
        CHECK(near(f.y("a"), 32));
    }
    {
        // A new block formatting context closes the top regardless of padding.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0 }"
                    "#p { display: block; overflow: hidden }"
                    "#a { display: block; height: 50px; margin-top: 30px }"));
        CHECK(f.layout("<body><div id=p><div id=a></div></div></body>"));
        CHECK(near(f.box("p").margin_top, 0));
        CHECK(near(f.y("a"), 30));
    }
    {
        // The bottom collapses too when the parent's height is auto...
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0 }"
                    "#p { display: block } #a { display: block; height: 50px; margin-bottom: 40px }"));
        CHECK(f.layout("<body><div id=p><div id=a></div></div></body>"));
        CHECK(near(f.h("p"), 50));
        CHECK(near(f.box("p").margin_bottom, 40));
    }
    {
        // ...but an explicit height blocks it, and the margin becomes a gap
        // inside the parent instead. Note an explicit height does NOT block
        // TOP collapsing, which is the asymmetry worth pinning.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0 }"
                    "#p { display: block; height: 200px }"
                    "#a { display: block; height: 50px; margin-top: 10px; margin-bottom: 40px }"));
        CHECK(f.layout("<body><div id=p><div id=a></div></div></body>"));
        CHECK(near(f.box("p").margin_top, 10));    // top still collapses out
        CHECK(near(f.box("p").margin_bottom, 0));  // bottom does not
        CHECK(near(f.h("p"), 200));
    }
}

void test_self_collapsing_block() {
    {
        // An empty block with no padding, border or height contributes no
        // height, and its two margins join one chain with its neighbours'.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0; padding-top: 1px }"
                    "div { display: block }"
                    "#a { height: 40px; margin-bottom: 10px }"
                    "#empty { margin-top: 25px; margin-bottom: 5px }"
                    "#b { height: 40px; margin-top: 15px }"));
        CHECK(f.layout("<body><div id=a></div><div id=empty></div><div id=b></div></body>"));
        // Chain across a, empty and b is {+10, +25, +5, +15} -> 25.
        CHECK(near(f.y("a"), 1));
        CHECK(near(f.y("b"), 66));
        CHECK(near(f.h("empty"), 0));
    }
    {
        // Padding stops a block self-collapsing: it now has a height.
        Fixture f;
        CHECK(f.css("body { display: block; margin: 0; padding-top: 1px }"
                    "div { display: block }"
                    "#a { height: 40px } #mid { padding-top: 3px } #b { height: 40px }"));
        CHECK(f.layout("<body><div id=a></div><div id=mid></div><div id=b></div></body>"));
        CHECK(near(f.h("mid"), 3));
        CHECK(near(f.y("b"), 44));
    }
}

void test_barriers_do_not_collapse() {
    {
        // An inline-block between two blocks is classified as INLINE by the
        // anonymous-block pass, so it is wrapped rather than stacked. The
        // wrapper's content is an inline formatting context, which is not
        // ported — so it contributes zero height for now, and the surrounding
        // blocks close up around it. Pinned so the limitation is visible and
        // this test fails loudly when inline layout lands.
        Fixture f;
        CHECK(f.css("#a { height: 40px; margin-bottom: 20px }"
                    "#ib { display: inline-block; height: 10px; margin-top: 20px }"
                    "#b { height: 40px; margin-top: 20px }"));
        CHECK(f.layout("<body><div id=a></div><span id=ib></span><div id=b></div></body>"));
        CHECK(near(f.y("a"), 0));
        CHECK(near(f.y("b"), 60));
        CHECK(f.tree[f.tree[f.find("ib")].parent].kind == BoxKind::AnonymousBlock);
        // The inline-block itself is never laid out: its parent returned before
        // descending, so even its explicit height is unresolved.
        CHECK(near(f.h("ib"), 0));
    }
    {
        // An out-of-flow box is placed but never joins the chain, and does not
        // advance the cursor: the block after it sits where it would have with
        // the out-of-flow box absent.
        Fixture f;
        // Scoped to the two flow children: a `div { height }` rule would also
        // pin the wrapper and hide the content-height result being tested.
        CHECK(f.css("#w { display: block } #a, #b { height: 40px }"
                    "#abs { position: absolute; height: 500px }"));
        CHECK(f.layout("<body><div id=w><div id=a></div><div id=abs></div>"
                       "<div id=b></div></div></body>"));
        CHECK(near(f.y("b"), 40));
        CHECK(near(f.h("w"), 80));
    }
}

void test_percent_height_chain() {
    // A percentage height needs a DEFINITE basis, which the viewport-seeded
    // synthetic root provides: viewport -> html -> body.
    Fixture f;
    CHECK(f.css("html, body { display: block; margin: 0; height: 100% }"
                "#half { display: block; height: 50% }"));
    CHECK(f.layout("<body><div id=half></div></body>", 1000, 600));
    CHECK(near(f.h("html"), 600));
    CHECK(near(f.h("body"), 600));
    CHECK(near(f.h("half"), 300));

    // The synthetic root collapses back to its content once children are
    // placed — it is seeded with the viewport height only so percentages have a
    // basis. Here html fills the viewport (UA `height: 100%`), so the root
    // reports 600; with the UA rule overridden it follows the content.
    Fixture g;
    CHECK(g.css("html, body { height: auto } div { display: block; height: 25px }"));
    CHECK(g.layout("<body><div id=a></div><div id=b></div></body>", 1000, 600));
    CHECK(near(g.tree[g.root].height, 50));
}

void test_auto_height_clamps() {
    Fixture f;
    CHECK(f.css("body, div { display: block; margin: 0 }"
                "#min { min-height: 100px } #max { max-height: 20px }"
                "#minf { min-height: 100px; padding-top: 10px; padding-bottom: 10px }"
                "#kid { height: 40px }"));
    CHECK(f.layout("<body><div id=min><div id=kid></div></div>"
                   "<div id=max><div class=k></div></div>"
                   "<div id=minf></div></body>"));
    CHECK(near(f.h("min"), 100));
    CHECK(near(f.h("max"), 0));
    // min-height shares height's box-sizing basis, so under content-box the
    // frame is added before clamping the border-box value.
    CHECK(near(f.h("minf"), 120));
}
