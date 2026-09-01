// CSS Multi-column Layout L1, the balanced-columns subset.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/multicol.h"
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

void test_multicol() {
    {
        // column-count with a gap: three 240px columns in 760px, six 60px
        // children balanced two per column, so the container is 120 tall.
        Fixture f;
        CHECK(f.css("#m { width: 760px; column-count: 3; column-gap: 20px }"
                    ".item { height: 60px }"));
        CHECK(f.layout("<body><div id=m><div id=a class=item></div><div id=b class=item></div>"
                       "<div id=c class=item></div><div id=d class=item></div>"
                       "<div id=e class=item></div><div id=g class=item></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("a").width, 240));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 60));
        CHECK(near(f.box("c").x, 260) && near(f.box("c").y, 0));
        CHECK(near(f.box("d").x, 260) && near(f.box("d").y, 60));
        CHECK(near(f.box("e").x, 520) && near(f.box("e").y, 0));
        CHECK(near(f.box("g").x, 520) && near(f.box("g").y, 60));
        CHECK(near(f.box("m").height, 120));
    }
    {
        // column-width derives the count: the last column needs no gap after
        // it, so 760px of content fits floor((760+20)/(200+20)) = 3 columns,
        // which are then 240 wide rather than the 200 asked for.
        Fixture f;
        CHECK(f.css("#m { width: 760px; column-width: 200px; column-gap: 20px }"
                    ".item { height: 60px }"));
        CHECK(f.layout("<body><div id=m><div id=a class=item></div><div id=b class=item></div>"
                       "<div id=c class=item></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 260));
        CHECK(near(f.box("c").x, 520));
        CHECK(near(f.box("a").width, 240));
        CHECK(near(f.box("m").height, 60));
    }
    {
        // Uneven content still balances: five items over two columns puts
        // three in the first and two in the second, not four and one.
        Fixture f;
        CHECK(f.css("#m { width: 200px; column-count: 2; column-gap: 0 }"
                    ".item { height: 10px }"));
        CHECK(f.layout("<body><div id=m><div id=a class=item></div><div id=b class=item></div>"
                       "<div id=c class=item></div><div id=d class=item></div>"
                       "<div id=e class=item></div></div></body>"));
        CHECK(near(f.box("c").x, 0));
        CHECK(near(f.box("d").x, 100));
        CHECK(near(f.box("m").height, 30));
    }
    {
        // A child taller than the balanced height takes a column to itself and
        // overflows rather than being split — the documented gap, pinned so it
        // is a known limit rather than a surprise.
        Fixture f;
        CHECK(f.css("#m { width: 200px; column-count: 2; column-gap: 0 }"
                    "#a { height: 100px } #b { height: 10px }"));
        CHECK(f.layout("<body><div id=m><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").height, 100));
        CHECK(near(f.box("b").x, 100));
        CHECK(!multicol_is_fully_ported());
    }
}
