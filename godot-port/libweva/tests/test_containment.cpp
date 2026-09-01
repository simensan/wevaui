// CSS Containment, and the anonymous-box inheritance rule that sits next to it
// in the same corpus cases. Both were found by the oracle.
#include "check.h"
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

void test_size_containment() {
    {
        // `contain: size` sizes the box as though it had no contents. The
        // contents are still laid out and still have real geometry — Chrome and
        // the reference both report normal rects inside a contained box — so
        // this is a rule about the box's own contribution, not about skipping
        // its children.
        Fixture f;
        CHECK(f.css("#w { width: 400px }"
                    "#c { contain: size }"
                    "#tall { height: 100px }"
                    "#after { height: 20px }"));
        CHECK(f.layout("<body><div id=w><div id=c><div id=tall></div></div>"
                       "<div id=after></div></div></body>"));
        CHECK(near(f.box("c").height, 0));
        // The child kept its size, and the sibling moved up as if the
        // contained box were empty.
        CHECK(near(f.box("tall").height, 100));
        CHECK(near(f.box("after").y, 0));
    }
    {
        // contain-intrinsic-size supplies the substitute size; the second value
        // is the height.
        Fixture f;
        CHECK(f.css("#w { width: 400px }"
                    "#c { contain: size; contain-intrinsic-size: 120px 40px }"
                    "#tall { height: 100px }"));
        CHECK(f.layout("<body><div id=w><div id=c><div id=tall></div></div></div></body>"));
        CHECK(near(f.box("c").height, 40));
    }
    {
        // `content-visibility: hidden` implies size containment.
        Fixture f;
        CHECK(f.css("#w { width: 400px }"
                    "#c { content-visibility: hidden }"
                    "#tall { height: 100px }"));
        CHECK(f.layout("<body><div id=w><div id=c><div id=tall></div></div></div></body>"));
        CHECK(near(f.box("c").height, 0));
    }
    {
        // `contain: inline-size` is not size containment, and a substring match
        // on the value would wrongly read it as one.
        Fixture f;
        CHECK(f.css("#w { width: 400px }"
                    "#c { contain: inline-size }"
                    "#tall { height: 100px }"));
        CHECK(f.layout("<body><div id=w><div id=c><div id=tall></div></div></div></body>"));
        CHECK(near(f.box("c").height, 100));
    }
}

void test_anonymous_box_inherits_line_height() {
    // CSS 2.1 §9.2.1.1. An anonymous block is created with a null style, so
    // reading line-height off the container alone missed the author's value and
    // fell back to the font's metric height. The case that exposed it was a
    // list item holding both text and a nested list: the text is wrapped in an
    // anonymous block, and only that block came out 18.29 tall instead of 16.
    Fixture f;
    CHECK(f.css("#w { width: 400px; font-size: 16px; line-height: 1 }"
                "#inner { height: 10px }"));
    CHECK(f.layout("<body><div id=w>text<div id=inner></div></div></body>"));
    // 16 for the anonymous block holding "text", plus the 10px child.
    CHECK(near(f.box("w").height, 26));
    CHECK(near(f.box("inner").y, 16));
}
