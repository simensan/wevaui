// CSS Anchor Positioning L1 — `anchor()` insets and `anchor-size()`.
#include "check.h"
#include "weva/anchor.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
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
    bool layout(std::string_view html, double vw = 800, double vh = 600) {
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
        if (b.element && b.element->get_attribute("id") == id) return start;
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find(id, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
    const Box& box(std::string_view id) const { return tree[find(id)]; }
    // Root-relative, which is what the harvest dumps compare.
    void at(std::string_view id, double* x, double* y) const {
        absolute_position(tree, find(id), x, y);
    }
};

// `body { margin: 0 }` throughout, so a number in a test is the number a
// browser reports rather than that plus eight.
const char* kReset = "body { margin: 0; padding: 0 }";

} // namespace

void test_anchor_sides() {
    {
        // The four sides, against an anchor at (50,0) sized 100x30.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".a { width: 100px; height: 30px; anchor-name: --t; margin-left: 50px }"
                    "#tip { position: absolute; position-anchor: --t;"
                    "       left: anchor(left); top: anchor(bottom);"
                    "       width: 80px; height: 20px }"));
        CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 50));    // the anchor's left edge
        CHECK(near(y, 30));    // its bottom edge
    }
    {
        // anchor(right) and anchor(top), and an anchor that is not first in
        // the document so its own position is not zero.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".filler { height: 30px }"
                    ".a { width: 100px; height: 50px; anchor-name: --t }"
                    "#tip { position: absolute; position-anchor: --t;"
                    "       left: anchor(right); top: anchor(top);"
                    "       width: 80px; height: 20px }"));
        CHECK(f.layout("<body><div class=filler></div><div class=a></div>"
                       "<div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 100));   // 0 + width
        CHECK(near(y, 30));    // below the filler
    }
    {
        // `center` is the midpoint of the anchor on the property's own axis --
        // the horizontal one for `left`, the vertical one for `top`. Reading
        // one axis for both is the easy way to get this wrong, and it shows up
        // only when the anchor is not square.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".a { width: 100px; height: 50px; anchor-name: --a; margin-left: 100px }"
                    "#tip { position: absolute; position-anchor: --a;"
                    "       left: anchor(center); top: anchor(center);"
                    "       width: 80px; height: 20px }"));
        CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 150));   // 100 + 100/2
        CHECK(near(y, 25));    // 0 + 50/2
    }
}

void test_anchor_far_edge_properties() {
    // `right` and `bottom` count from the containing block's FAR edge, so the
    // same anchor side gives a different number in them than in left/top.
    // Getting this wrong puts the box the width of the page away, and only in
    // the cases that use the far properties.
    Fixture f;
    CHECK(f.css(kReset));
    CHECK(f.css(".a { width: 100px; height: 40px; anchor-name: --t; margin-left: 200px }"
                "#tip { position: absolute; position-anchor: --t;"
                "       right: anchor(right); bottom: anchor(bottom);"
                "       width: 80px; height: 20px }"));
    CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>", 800, 600));
    // right: anchor(right) is 800 - 300 = 500 from the right edge, which puts
    // the box's RIGHT edge at 300 and its left at 220.
    double x = 0, y = 0;
    f.at("tip", &x, &y);
    CHECK(near(x, 300 - 80));
    CHECK(near(y, 40 - 20));
}

void test_anchor_size() {
    {
        // The anchor's width becomes the tip's width, and the tip is otherwise
        // an auto-width absolute box, which would shrink to fit its (empty)
        // content without this.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".btn { width: 120px; height: 30px; anchor-name: --b }"
                    "#tip { position: absolute; position-anchor: --b;"
                    "       width: anchor-size(--b width); height: 20px }"));
        CHECK(f.layout("<body><div class=btn></div><div id=tip></div></body>"));
        CHECK(near(f.box("tip").width, 120));
    }
    {
        // ...and the height form, which an auto-height absolute box would
        // otherwise collapse to zero.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".btn { width: 80px; height: 50px; anchor-name: --b }"
                    "#tip { position: absolute; position-anchor: --b;"
                    "       width: 60px; height: anchor-size(--b height) }"));
        CHECK(f.layout("<body><div class=btn></div><div id=tip></div></body>"));
        CHECK(near(f.box("tip").height, 50));
        CHECK(near(f.box("tip").width, 60));
    }
    {
        // Without a name in the function, the element's own position-anchor
        // supplies it.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".btn { width: 140px; height: 30px; anchor-name: --b }"
                    "#tip { position: absolute; position-anchor: --b;"
                    "       width: anchor-size(width); height: 20px }"));
        CHECK(f.layout("<body><div class=btn></div><div id=tip></div></body>"));
        CHECK(near(f.box("tip").width, 140));
    }
}

void test_anchor_naming() {
    {
        // An explicit name inside the function overrides position-anchor, and
        // an anchor declared AFTER the element referring to it still resolves
        // -- which is why the registry is built before the walk rather than
        // during it.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css("#tip { position: absolute; position-anchor: --one;"
                    "       left: anchor(--two right); top: anchor(--two bottom);"
                    "       width: 10px; height: 10px }"
                    ".one { width: 50px; height: 20px; anchor-name: --one }"
                    ".two { width: 70px; height: 40px; anchor-name: --two }"));
        CHECK(f.layout("<body><div id=tip></div><div class=one></div>"
                       "<div class=two></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 70));        // --two's right edge
        CHECK(near(y, 20 + 40));   // --two sits under --one, so its bottom is 60
    }
    {
        // Two elements claiming one name: the later in tree order wins.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".a { height: 10px; anchor-name: --dup }"
                    ".b { height: 25px; anchor-name: --dup }"
                    "#tip { position: absolute; position-anchor: --dup;"
                    "       top: anchor(top); left: 0; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div class=a></div><div class=b></div>"
                       "<div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(y, 10));   // .b's top, not .a's
    }
}

void test_anchor_invalid_falls_back() {
    {
        // A name nothing declares makes the function invalid, and an invalid
        // inset is `auto` -- so the box keeps its static position rather than
        // landing at zero. Resolving a missing anchor to 0 would put every
        // tooltip in a document with one typo in the top-left corner.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".filler { height: 40px }"
                    "#tip { position: absolute; position-anchor: --missing;"
                    "       left: anchor(left); top: anchor(bottom);"
                    "       width: 30px; height: 10px }"));
        CHECK(f.layout("<body><div class=filler></div><div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 0));
        CHECK(near(y, 40));   // the static position, under the filler
    }
    {
        // A side from the OTHER axis names no position on this one, so
        // `left: anchor(top)` is invalid rather than silently using the
        // anchor's y.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".a { width: 100px; height: 60px; anchor-name: --t; margin-left: 30px }"
                    "#tip { position: absolute; position-anchor: --t;"
                    "       left: anchor(top); top: anchor(bottom);"
                    "       width: 30px; height: 10px }"));
        CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 0));    // invalid, so the static position
        CHECK(near(y, 60));   // the valid one still resolves
    }
    {
        // Arithmetic around the function is not supported, and must not be
        // half-applied: `anchor(bottom) + 4px` resolves to nothing rather than
        // to the anchor's bottom with the offset silently dropped.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".filler { height: 40px }"
                    ".a { width: 100px; height: 20px; anchor-name: --t }"
                    "#tip { position: absolute; position-anchor: --t;"
                    "       top: calc(anchor(bottom) + 4px); left: 0;"
                    "       width: 30px; height: 10px }"));
        CHECK(f.layout("<body><div class=filler></div><div class=a></div>"
                       "<div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(y, 60));   // the static position, not 64 and not 4
    }
    {
        // No position-anchor at all, and no name in the function.
        Fixture f;
        CHECK(f.css(kReset));
        CHECK(f.css(".a { width: 100px; height: 35px; anchor-name: --t }"
                    "#tip { position: absolute; left: anchor(left); top: anchor(bottom);"
                    "       width: 30px; height: 10px }"));
        CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>"));
        double x = 0, y = 0;
        f.at("tip", &x, &y);
        CHECK(near(x, 0));
        CHECK(near(y, 35));   // static, which here happens to sit under .a
    }
}

void test_anchor_percentage() {
    // A percentage runs from the axis's near edge to its far edge, so 25% of a
    // 200px-wide anchor starting at 40 is 90.
    Fixture f;
    CHECK(f.css(kReset));
    CHECK(f.css(".a { width: 200px; height: 80px; anchor-name: --t; margin-left: 40px }"
                "#tip { position: absolute; position-anchor: --t;"
                "       left: anchor(25%); top: anchor(50%);"
                "       width: 10px; height: 10px }"));
    CHECK(f.layout("<body><div class=a></div><div id=tip></div></body>"));
    double x = 0, y = 0;
    f.at("tip", &x, &y);
    CHECK(near(x, 40 + 50));
    CHECK(near(y, 40));
}

void test_anchor_registry() {
    // The registry itself: `anchor-name: none` is not a name, and a document
    // that declares nothing produces an empty registry -- which is the check
    // that keeps the cost off every other document.
    Fixture f;
    CHECK(f.css(kReset));
    CHECK(f.css(".n { anchor-name: none; height: 5px }"
                ".y { anchor-name: --real; height: 5px }"));
    CHECK(f.layout("<body><div class=n></div><div class=y></div></body>"));
    const AnchorRegistry reg = collect_anchors(f.tree, f.root);
    CHECK(!reg.empty());
    CHECK(reg.find("--real") != kNoBox);
    CHECK(reg.find("none") == kNoBox);
    CHECK(reg.find("--nothing") == kNoBox);

    Fixture g;
    CHECK(g.css(kReset));
    CHECK(g.layout("<body><div></div></body>"));
    CHECK(collect_anchors(g.tree, g.root).empty());
}
