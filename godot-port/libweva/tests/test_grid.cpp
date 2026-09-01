// CSS Grid Layout L1, the explicit-grid subset. Every case here was graded
// against the C# reference through the corpus first; these exist so a
// regression shows up in a second rather than in a corpus run.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/grid.h"
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

void test_grid_tracks() {
    {
        // Fixed columns, a gap between them, row-major auto-placement.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px;"
                    "     grid-template-columns: 100px 100px; column-gap: 20px }"
                    ".c { height: 30px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=c2 class=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 120) && near(f.box("b").y, 0));
        // The third item wraps to the next row.
        CHECK(near(f.box("c2").x, 0) && near(f.box("c2").y, 30));
    }
    {
        // repeat() expands, and `fr` splits what the fixed tracks leave.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 320px;"
                    "     grid-template-columns: repeat(3, 1fr); column-gap: 10px }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=c2 class=c></div></div></body>"));
        CHECK(near(f.box("a").width, 100));
        CHECK(near(f.box("b").x, 110));
        CHECK(near(f.box("c2").x, 220));
    }
    {
        // A fixed track and an fr track share the row.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px;"
                    "     grid-template-columns: 240px 1fr }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").width, 240));
        CHECK(near(f.box("b").width, 160));
    }
    {
        // Leftover space stretches an AUTO track: align-content/justify-content
        // default to `normal`, which behaves as stretch for a grid.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px; height: 200px }"
                    "#a { }"));
        CHECK(f.layout("<body><div id=g><div id=a>x</div></div></body>"));
        CHECK(near(f.box("a").width, 400));
        CHECK(near(f.box("a").height, 200));
    }
}

void test_grid_items() {
    {
        // An item in a FIXED track still gets its box model resolved. Skipping
        // that left padding, border and margin at zero, and the item's own
        // children were then placed at its content origin rather than inside
        // its padding.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px; grid-template-columns: 200px 1fr }"
                    "#a { padding: 16px }"
                    "#inner { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a><div id=inner></div></div></div></body>"));
        CHECK(near(f.box("a").width, 200));
        CHECK(near(f.box("inner").x, 16));
        CHECK(near(f.box("inner").y, 16));
    }
    {
        // A scroll container's automatic minimum size is zero, so it does not
        // force its row to grow to its content — it scrolls inside a row the
        // container's own height decides.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px; height: 100px }"
                    "#a { overflow-y: auto }"
                    ".tall { height: 300px }"));
        CHECK(f.layout("<body><div id=g><div id=a><div class=tall></div></div></div></body>"));
        CHECK(near(f.box("a").height, 100));
    }
    {
        // An out-of-flow child is not a grid item and takes no cell.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px; position: relative;"
                    "     grid-template-columns: 100px 100px }"
                    ".c { height: 10px }"
                    "#z { position: absolute; top: 0; left: 0; width: 5px; height: 5px }"));
        CHECK(f.layout("<body><div id=g><div id=z></div><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 100));
    }
}

void test_grid_areas() {
    // grid-template-areas names cells; an item claims the rectangle its area
    // covers, and spanning areas take every track they touch.
    Fixture f;
    CHECK(f.css("#g { display: grid; width: 300px;"
                "     grid-template-columns: 100px 100px 100px;"
                "     grid-template-rows: 40px 60px;"
                "     grid-template-areas: \"top top top\" \"a b b\" }"
                "#t { grid-area: top }"
                "#p { grid-area: a }"
                "#q { grid-area: b }"));
    CHECK(f.layout("<body><div id=g><div id=t></div><div id=p></div>"
                   "<div id=q></div></div></body>"));
    CHECK(near(f.box("t").x, 0) && near(f.box("t").y, 0));
    CHECK(near(f.box("t").width, 300) && near(f.box("t").height, 40));
    CHECK(near(f.box("p").x, 0) && near(f.box("p").y, 40));
    CHECK(near(f.box("p").width, 100));
    // `b` spans two columns.
    CHECK(near(f.box("q").x, 100) && near(f.box("q").y, 40));
    CHECK(near(f.box("q").width, 200) && near(f.box("q").height, 60));
}
