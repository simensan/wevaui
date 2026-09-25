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

void test_block_auto_margins() {
    // Chrome152, unbreakable cards isolate horizontal alignment from fragmentation.
    struct Case { const char* css; double x,y,w,h; };
    const Case cases[] = {
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;display:flow-root}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;display:flow-root}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;display:flow-root}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;display:flow-root}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;display:flow-root}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;display:flow-root}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;display:flow-root}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;display:flow-root}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;display:flow-root}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;display:flow-root}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;display:flow-root}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;display:flow-root}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;display:flow-root}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;display:flow-root}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;display:flow-root}",100,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;display:flow-root}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;display:flow-root}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;display:flow-root}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;display:flow-root}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;display:flow-root}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;}",39,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;}",25,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;}",39,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;}",25,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;}",7,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;}",0,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:ltr;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;}",7,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:auto;}",157,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:auto;margin-right:auto;}",175,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:100px;margin-left:7px;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:auto;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;width:400px;margin-left:7px;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:auto;}",157,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:auto;margin-right:auto;}",175,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;max-width:100px;margin-left:7px;margin-right:11px;}",189,0,100,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:11px;}",-111,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:auto;margin-right:auto;}",-100,0,400,20},
        {"html,body{margin:0;padding:0}#p{width:300px;direction:rtl;columns:2;column-gap:0}#c{break-inside:avoid;height:20px;min-width:400px;margin-left:7px;margin-right:11px;}",-111,0,400,20},
    };
    for (const auto& r : cases) {
        Fixture f; CHECK(f.css(r.css)); CHECK(f.layout("<div id=p><div id=c></div></div>"));
        const auto& c=f.box("c");
        if (!near(c.x,r.x) || !near(c.width,r.w) || !near(c.height,r.h)) std::printf("margin case: %s: actual %g,%g,%g expected %g,%g,%g\n",r.css,c.x,c.width,c.height,r.x,r.w,r.h);
        CHECK(near(c.x,r.x)); CHECK(near(c.y,r.y)); CHECK(near(c.width,r.w)); CHECK(near(c.height,r.h));
    }
}
