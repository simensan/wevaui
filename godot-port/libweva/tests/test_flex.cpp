// CSS Flexible Box Layout L1, the single-line subset. Every case here was
// graded against the C# reference through the corpus first; these exist so a
// regression is caught in a second rather than in a corpus run.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/flex.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/positioning.h"
#include "weva/user_agent_stylesheet.h"

#include <cmath>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-6) { return std::fabs(a - b) < eps; }

struct Styles : StyleProvider {
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
    Styles styles;
    BoxTree tree;
    LayoutContext ctx;
    MonoFontMetrics metrics;
    BoxId root = kNoBox;

    Fixture() {
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
        BlockLayout bl(&tree, ctx, &metrics);
        bl.layout_root(root, vw, vh);
        run_positioning(&tree, root, ctx, &bl);
        return true;
    }
    BoxId find(std::string_view id, BoxId from = -2) const {
        const BoxId start = from == -2 ? root : from;
        if (start == kNoBox) return kNoBox;
        const Box& b = tree[start];
        if (b.kind == BoxKind::Block && b.element && b.element->get_attribute("id") == id) {
            return start;
        }
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find(id, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
    const Box& box(std::string_view id) const { return tree[find(id)]; }
};

} // namespace

void test_flex_main_axis() {
    {
        // Fixed-width items laid out in a row with a gap between them.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 400px; column-gap: 10px }"
                    ".c { width: 100px; height: 50px }"));
        CHECK(f.layout("<body><div id=r><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 110));
        // The line's cross size is the tallest item.
        CHECK(near(f.box("r").height, 50));
    }
    {
        // `flex: 1` gives basis 0 and an equal share, NOT the content size —
        // this is what the shorthand's one-number form means and it is easy to
        // expand wrongly.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px; column-gap: 20px }"
                    ".c { flex: 1; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=c2 class=c></div></div></body>"));
        // 300 - 40 of gap, split three ways.
        CHECK(near(f.box("a").width, 260.0 / 3));
        CHECK(near(f.box("b").x, 260.0 / 3 + 20));
    }
    {
        // flex-shrink: 0 keeps an item at its base when the line overflows.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 100px }"
                    "#a { width: 80px; height: 10px; flex-shrink: 0 }"
                    "#b { width: 80px; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").width, 80));
        CHECK(near(f.box("b").width, 20));
    }
    {
        // justify-content moves the whole line within the leftover space.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 200px; justify-content: center }"
                    "#a { width: 50px; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").x, 75));

        Fixture g;
        CHECK(g.css("#r { display: flex; width: 200px; justify-content: flex-end }"
                    "#a { width: 50px; height: 10px }"));
        CHECK(g.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(g.box("a").x, 150));
    }
    {
        // An out-of-flow child is not an item and takes no share of the free
        // space. Its position type has to be read from the STYLE, because the
        // box's own field is only stamped once layout runs.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px; position: relative }"
                    ".c { flex: 1; height: 10px }"
                    "#z { position: absolute; top: 0; right: 0; width: 30px; height: 5px }"));
        CHECK(f.layout("<body><div id=r><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=z></div></div></body>"));
        CHECK(near(f.box("a").width, 150));
        CHECK(near(f.box("b").width, 150));
    }
}

void test_flex_min_height_is_not_a_definite_height() {
    // `min-height` constrains the height; it does not GIVE one. Treating "the
    // height property is not auto" as "a used height exists" handed a column
    // container an available main size of zero — read off a height that had not
    // been computed — and every item shrank to nothing. `min-height: 100vh` on
    // a page shell is common enough that this was five harvested cases.
    Fixture f;
    CHECK(f.css("#s { min-height: 600px; display: flex; flex-direction: column }"
                "#bar { height: 100px }"
                "#rest { flex: 1 1 auto }"));
    CHECK(f.layout("<body><div id=s><div id=bar></div><div id=rest></div></div></body>"));
    CHECK(near(f.box("bar").height, 100));
}

void test_flex_cross_axis() {
    {
        // stretch is the initial alignment: an auto-height item fills the line.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 200px; height: 80px }"
                    "#a { width: 50px }"
                    "#b { width: 50px; height: 20px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").height, 80));
        CHECK(near(f.box("b").height, 20));
    }
    {
        // center leaves the item at its own size and moves it.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 200px; height: 80px; align-items: center }"
                    "#a { width: 50px; height: 20px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").height, 20));
        CHECK(near(f.box("a").y, 30));
    }
    {
        // A column container's non-stretched item sizes to its content on the
        // cross axis rather than filling the container — it was coming out full
        // width and then being "centred" with nowhere to move.
        Fixture f;
        CHECK(f.css("#r { display: flex; flex-direction: column; width: 200px;"
                    "     height: 100px; align-items: center }"
                    "#a { }"));
        CHECK(f.layout("<body><div id=r><div id=a>ab</div></div></body>"));
        // Two characters at 8px.
        CHECK(near(f.box("a").width, 16));
        CHECK(near(f.box("a").x, 92));
    }
    {
        // A stretched item's content is re-laid at the imposed size, so a
        // nested column container has a main size to distribute. Without the
        // re-layout the inner justify-content has nothing to centre in.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 200px; height: 100px }"
                    "#t { width: 60px; display: flex; flex-direction: column;"
                    "     justify-content: center }"
                    "#i { width: 20px; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=t><div id=i></div></div></div></body>"));
        CHECK(near(f.box("t").height, 100));
        CHECK(near(f.box("i").y, 45));
    }
}

void test_flex_direction_and_order() {
    {
        // A column stacks along the block axis and gaps use row-gap.
        Fixture f;
        CHECK(f.css("#r { display: flex; flex-direction: column; width: 200px; row-gap: 5px }"
                    ".c { width: 30px; height: 20px }"));
        CHECK(f.layout("<body><div id=r><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").y, 0));
        CHECK(near(f.box("b").y, 25));
        CHECK(near(f.box("r").height, 45));
    }
    {
        // `order` reorders the line; equal orders keep document order.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px }"
                    ".c { width: 50px; height: 10px }"
                    "#a { order: 2 }"));
        CHECK(f.layout("<body><div id=r><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("b").x, 0));
        CHECK(near(f.box("a").x, 50));
    }
}
