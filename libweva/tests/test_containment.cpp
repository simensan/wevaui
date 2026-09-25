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
    for (const char* layout : {"display:flex", "display:grid;grid-template-columns:1fr 50px"}) {
        Fixture f;
        CHECK(f.css(std::string("#row{width:200px;")+layout+"}#panel{container-type:inline-size;flex:1}"
            "#child{width:300px;height:10px}#other{width:50px;flex:none}"));
        CHECK(f.layout("<div id=row><div id=panel><div id=child></div></div><div id=other></div></div>"));
        CHECK(near(f.box("panel").width,150));
        CHECK(near(f.box("child").width,300));
        CHECK(near(f.box("other").x,150));
    }
    {
        Fixture f;
        CHECK(f.css("#row{display:flex;width:5px}#item{width:50px}#child{width:100px;height:10px}"));
        CHECK(f.layout("<div id=row><div id=item><div id=child></div></div></div>"));
        CHECK(near(f.box("item").width,50)); // specified size caps, rather than erases, auto minimum
        CHECK(near(f.box("child").width,100));
    }
    {
        Fixture f;
        CHECK(f.css("#panel{position:absolute;container-type:inline-size;padding:5px}"
            "#label{position:absolute;white-space:nowrap;padding:0 4px}"
            "#wrap{width:50px}#text{display:inline-block;font-size:16px}"));
        CHECK(f.layout("<div id=panel><div id=label>XXXX</div></div>"
                       "<div id=wrap><div id=text>XXXX XXXX XXXX</div></div>"));
        CHECK(f.box("label").width>8); // unbreakable text can overflow a zero-content-width parent
        CHECK(near(f.box("text").width,50)); // ordinary text still wraps at available space
    }
    for (const char* containment : {"content", "strict"}) {
        Fixture f;
        CHECK(f.css(std::string("#c{contain:") + containment +
            ";width:200px;padding:5px;border:1px solid}#tall{height:100px}#after{height:20px}"));
        CHECK(f.layout("<div id=c><div id=tall></div></div><div id=after></div>"));
        const double height=std::string(containment)=="content" ? 112 : 12;
        CHECK(near(f.box("c").height,height));
        CHECK(near(f.box("after").y,height));
        CHECK(near(f.box("tall").height,100));
    }
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
        // The block's width still comes from its containing block: nothing
        // about size containment makes a normal-flow block shrink.
        CHECK(near(f.box("c").width, 400));
    }
    {
        // ...but a box whose width comes from its CONTENTS has no contents to
        // take it from. CSS Containment L2 3.1 is both axes, and the first
        // value of contain-intrinsic-size is the width.
        //
        // This was missing entirely: an inline-block came out the width of the
        // text it was defined not to have, and everything after it on the line
        // moved with it. It hid because a block in normal flow takes its width
        // from its containing block either way, and that is what every
        // containment case in the corpus was.
        Fixture f;
        CHECK(f.css("body { font-family: monospace; font-size: 16px }"
                    "#w { width: 600px }"
                    "#c { display: inline-block; contain: size;"
                    "     contain-intrinsic-size: 100px 50px }"));
        CHECK(f.layout("<body><div id=w><span id=c>a very long piece of text</span></div></body>"));
        CHECK(near(f.box("c").width, 100));
        CHECK(near(f.box("c").height, 50));
    }
    {
        // The same for a float, and with the longhand rather than the
        // shorthand -- which is read from the opposite end of the value.
        Fixture f;
        CHECK(f.css("body { font-family: monospace; font-size: 16px }"
                    "#w { width: 600px }"
                    "#c { float: left; contain: size; contain-intrinsic-width: 120px }"));
        CHECK(f.layout("<body><div id=w><div id=c>floating content, quite wide</div></div></body>"));
        CHECK(near(f.box("c").width, 120));
    }
    {
        // Contained and shrink-to-fit with NO intrinsic size stated is zero
        // wide, not content-wide. Falling back to the content would be the
        // same bug wearing a default.
        Fixture f;
        CHECK(f.css("body { font-family: monospace; font-size: 16px }"
                    "#w { width: 600px }"
                    "#c { display: inline-block; contain: size }"));
        CHECK(f.layout("<body><div id=w><span id=c>some text here</span></div></body>"));
        CHECK(near(f.box("c").width, 0));
    }
    {
        // CSS Containment L3 §2.1: `contain: inline-size` contains the INLINE
        // axis only. A shrink-to-fit box has no contents to take a width from,
        // so it is its own frame and nothing more -- but its HEIGHT still comes
        // from the contents, which is what separates this from `contain: size`.
        Fixture f;
        CHECK(f.css("body { font-family: monospace; font-size: 16px }"
                    "#w { position: relative; width: 600px; height: 300px }"
                    "#t { position: absolute; top: 0; left: 0; padding: 12px;"
                    "     border: 1px solid #444; contain: inline-size }"));
        CHECK(f.layout("<body><div id=w><section id=t><p id=p>some content here</p>"
                       "</section></div></body>"));
        // 12 + 12 of padding and 1 + 1 of border, with no content width.
        CHECK(near(f.box("t").width, 26));
        // ...and a height that still measures the contents, so NOT 26.
        CHECK(f.box("t").height > 26);
    }
    {
        // Padding and border are NOT contained away -- containment removes the
        // contents, not the box's own frame.
        Fixture f;
        CHECK(f.css("body { font-family: monospace; font-size: 16px }"
                    "#w { width: 600px }"
                    "#c { display: inline-block; contain: size; padding: 5px;"
                    "     border: 2px solid #000; contain-intrinsic-size: 40px 10px }"));
        CHECK(f.layout("<body><div id=w><span id=c>text</span></div></body>"));
        CHECK(near(f.box("c").width, 40 + 10 + 4));
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
