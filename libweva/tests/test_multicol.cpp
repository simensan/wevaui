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
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2}.span{column-span:all;height:0;margin:-5px 0}"));
        CHECK(f.layout("<div id=columns><div class=span></div><div class=span></div></div>"));
        CHECK(near(f.box("columns").height, 0));
    }
    for (int margin : {10, -5}) {
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2;column-gap:20px}.span{column-span:all;height:30px;margin:" + std::to_string(margin) + "px 0}"));
        CHECK(f.layout("<div id=columns><div id=a class=span></div><div id=b class=span></div></div>"));
        CHECK(near(f.box("a").y, margin));
        CHECK(near(f.box("b").y, 30 + 2 * margin));
        CHECK(near(f.box("columns").height, 60 + 3 * margin));
    }
    for (const char* direction : {"ltr", "rtl"}) {
        Fixture f;
        CHECK(f.css(std::string("#columns{width:300px;column-count:2;column-gap:20px;direction:") + direction +
            "}.item{height:20px;break-inside:avoid}#heading{height:30px;column-span:all}"));
        CHECK(f.layout("<div id=columns><div id=a class=item></div><div id=b class=item></div><div id=heading></div><div id=c class=item></div><div id=d class=item></div></div>"));
        CHECK(near(f.box("heading").width, 300));
        CHECK(near(f.box("heading").y, 20));
        CHECK(near(f.box("columns").height, 70));
        CHECK(near(f.box("a").y, 0) && near(f.box("b").y, 0));
        CHECK(near(f.box("c").y, 50) && near(f.box("d").y, 50));
        CHECK(near(f.box("a").x, std::string(direction) == "rtl" ? 160 : 0));
        CHECK(near(f.box("c").x, f.box("a").x));
    }
    // Chrome152: used count/width/gap for whole, unbreakable cards. These
    // checks do not claim paragraph fragmentation or spanning support.
    {
        struct SizingCase { const char* css; double bounds[13][4]; };
        const SizingCase cases[] = {
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,210.65625,20},{0,20,210.65625,20},{0,40,210.65625,20},{0,60,210.65625,20},{210.671875,0,210.65625,20},{210.671875,20,210.65625,20},{210.671875,40,210.65625,20},{210.671875,60,210.65625,20},{421.328125,0,210.65625,20},{421.328125,20,210.65625,20},{421.328125,40,210.65625,20},{421.328125,60,210.65625,20}}}, // fractional-three
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:5;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,60},{0,0,126.390625,20},{0,20,126.390625,20},{0,40,126.390625,20},{126.40625,0,126.390625,20},{126.40625,20,126.390625,20},{126.40625,40,126.390625,20},{252.796875,0,126.390625,20},{252.796875,20,126.390625,20},{252.796875,40,126.390625,20},{379.203125,0,126.390625,20},{379.203125,20,126.390625,20},{379.203125,40,126.390625,20}}}, // fractional-five
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;column-gap:10.5px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,203.65625,20},{0,20,203.65625,20},{0,40,203.65625,20},{0,60,203.65625,20},{214.171875,0,203.65625,20},{214.171875,20,203.65625,20},{214.171875,40,203.65625,20},{214.171875,60,203.65625,20},{428.328125,0,203.65625,20},{428.328125,20,203.65625,20},{428.328125,40,203.65625,20},{428.328125,60,203.65625,20}}}, // fractional-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20}}}, // normal-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:648px;font-size:24px;column-count:3}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,648,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{224,0,200,20},{224,20,200,20},{224,40,200,20},{224,60,200,20},{448,0,200,20},{448,20,200,20},{448,40,200,20},{448,60,200,20}}}, // normal-font
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:600px;column-count:3;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,600,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{200,0,200,20},{200,20,200,20},{200,40,200,20},{200,60,200,20},{400,0,200,20},{400,20,200,20},{400,40,200,20},{400,60,200,20}}}, // zero-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:4;column-width:200px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20}}}, // width-limits-count
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:2;column-width:100px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,120},{0,0,308,20},{0,20,308,20},{0,40,308,20},{0,60,308,20},{0,80,308,20},{0,100,308,20},{324,0,308,20},{324,20,308,20},{324,40,308,20},{324,60,308,20},{324,80,308,20},{324,100,308,20}}}, // count-limits-width
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:180px;column-count:4;column-width:200px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,180,240},{0,0,180,20},{0,20,180,20},{0,40,180,20},{0,60,180,20},{0,80,180,20},{0,100,180,20},{0,120,180,20},{0,140,180,20},{0,160,180,20},{0,180,180,20},{0,200,180,20},{0,220,180,20}}}, // narrow-container
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-width:200px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20}}}, // auto-count
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:8px;column-width:0;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,8,40},{0,0,1,20},{0,20,1,20},{1,0,1,20},{1,20,1,20},{2,0,1,20},{2,20,1,20},{3,0,1,20},{3,20,1,20},{4,0,1,20},{4,20,1,20},{5,0,1,20},{5,20,1,20}}}, // zero-column-width
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:8px;column-width:.25px;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,8,40},{0,0,1,20},{0,20,1,20},{1,0,1,20},{1,20,1,20},{2,0,1,20},{2,20,1,20},{3,0,1,20},{3,20,1,20},{4,0,1,20},{4,20,1,20},{5,0,1,20},{5,20,1,20}}}, // subpixel-column-width
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:8px;column-width:.5px;column-count:4;column-gap:0}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,8,60},{0,0,2,20},{0,20,2,20},{0,40,2,20},{2,0,2,20},{2,20,2,20},{2,40,2,20},{4,0,2,20},{4,20,2,20},{4,40,2,20},{6,0,2,20},{6,20,2,20},{6,40,2,20}}}, // subpixel-count-cap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:600px;column-count:3;column-gap:10%}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,600,80},{0,0,160,20},{0,20,160,20},{0,40,160,20},{0,60,160,20},{220,0,160,20},{220,20,160,20},{220,40,160,20},{220,60,160,20},{440,0,160,20},{440,20,160,20},{440,40,160,20},{440,60,160,20}}}, // percentage-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:640px;font-size:20px;column-count:4;column-width:10em}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,640,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{220,0,200,20},{220,20,200,20},{220,40,200,20},{220,60,200,20},{440,0,200,20},{440,20,200,20},{440,40,200,20},{440,60,200,20}}}, // relative-width
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:200px;column-count:3;column-width:40px;column-gap:400px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,200,240},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{0,80,200,20},{0,100,200,20},{0,120,200,20},{0,140,200,20},{0,160,200,20},{0,180,200,20},{0,200,200,20},{0,220,200,20}}}, // large-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}#m>div{width:100px}", {{0,0,632,80},{532,0,100,20},{532,20,100,20},{532,40,100,20},{532,60,100,20},{316,0,100,20},{316,20,100,20},{316,40,100,20},{316,60,100,20},{100,0,100,20},{100,20,100,20},{100,40,100,20},{100,60,100,20}}}, // rtl-fixed-card
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}#m>div{width:100px;margin-left:7px;margin-right:11px}", {{0,0,632,80},{521,0,100,20},{521,20,100,20},{521,40,100,20},{521,60,100,20},{305,0,100,20},{305,20,100,20},{305,40,100,20},{305,60,100,20},{89,0,100,20},{89,20,100,20},{89,40,100,20},{89,60,100,20}}}, // rtl-card-margins
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}#m>div{width:100px;margin-left:auto;margin-right:auto}", {{0,0,632,80},{482,0,100,20},{482,20,100,20},{482,40,100,20},{482,60,100,20},{266,0,100,20},{266,20,100,20},{266,40,100,20},{266,60,100,20},{50,0,100,20},{50,20,100,20},{50,40,100,20},{50,60,100,20}}}, // rtl-card-auto-margins
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}body{direction:rtl}#m{margin-left:0;margin-right:auto}", {{0,0,632,80},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20}}}, // rtl-inherited
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20}}}, // rtl-three
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;column-gap:0;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{421.34375,0,210.65625,20},{421.34375,20,210.65625,20},{421.34375,40,210.65625,20},{421.34375,60,210.65625,20},{210.671875,0,210.65625,20},{210.671875,20,210.65625,20},{210.671875,40,210.65625,20},{210.671875,60,210.65625,20},{0.015625,0,210.65625,20},{0.015625,20,210.65625,20},{0.015625,40,210.65625,20},{0.015625,60,210.65625,20}}}, // rtl-fractional-three
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:5;column-gap:0;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,60},{505.609375,0,126.390625,20},{505.609375,20,126.390625,20},{505.609375,40,126.390625,20},{379.203125,0,126.390625,20},{379.203125,20,126.390625,20},{379.203125,40,126.390625,20},{252.8125,0,126.390625,20},{252.8125,20,126.390625,20},{252.8125,40,126.390625,20},{126.40625,0,126.390625,20},{126.40625,20,126.390625,20},{126.40625,40,126.390625,20}}}, // rtl-fractional-five
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;column-gap:10.5px;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{428.34375,0,203.65625,20},{428.34375,20,203.65625,20},{428.34375,40,203.65625,20},{428.34375,60,203.65625,20},{214.171875,0,203.65625,20},{214.171875,20,203.65625,20},{214.171875,40,203.65625,20},{214.171875,60,203.65625,20},{0.015625,0,203.65625,20},{0.015625,20,203.65625,20},{0.015625,40,203.65625,20},{0.015625,60,203.65625,20}}}, // rtl-gap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:3;padding:13px 17px;border:3px solid;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,672,112},{452,16,200,20},{452,36,200,20},{452,56,200,20},{452,76,200,20},{236,16,200,20},{236,36,200,20},{236,56,200,20},{236,76,200,20},{20,16,200,20},{20,36,200,20},{20,56,200,20},{20,76,200,20}}}, // rtl-padding
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;column-count:4;column-width:200px;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20}}}, // rtl-width-cap
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:180px;column-count:4;column-width:200px;direction:rtl}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,180,240},{0,0,180,20},{0,20,180,20},{0,40,180,20},{0,60,180,20},{0,80,180,20},{0,100,180,20},{0,120,180,20},{0,140,180,20},{0,160,180,20},{0,180,180,20},{0,200,180,20},{0,220,180,20}}}, // rtl-single
            {"html,body{margin:0;padding:0;font-size:16px}#m{width:632px;columns:4 200px}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}", {{0,0,632,80},{0,0,200,20},{0,20,200,20},{0,40,200,20},{0,60,200,20},{216,0,200,20},{216,20,200,20},{216,40,200,20},{216,60,200,20},{432,0,200,20},{432,20,200,20},{432,40,200,20},{432,60,200,20}}}, // shorthand
        };
        for (const auto& row : cases) {
            Fixture f;
            CHECK(f.css(row.css));
            CHECK(f.layout("<div id=m><div id=c0></div><div id=c1></div><div id=c2></div><div id=c3></div><div id=c4></div><div id=c5></div><div id=c6></div><div id=c7></div><div id=c8></div><div id=c9></div><div id=c10></div><div id=c11></div></div>"));
            for (int i=0;i<13;++i) {
                const auto& box = f.box(i == 0 ? "m" : "c" + std::to_string(i-1));
                CHECK(near(box.x, row.bounds[i][0]));
                CHECK(near(box.y, row.bounds[i][1]));
                CHECK(near(box.width, row.bounds[i][2]));
                CHECK(near(box.height, row.bounds[i][3]));
            }
        }
    }

    {
        // The `columns` shorthand reaching layout, which is the point of
        // expanding it: identical geometry to the column-count case below,
        // written the way an author actually writes it. Before the expansion
        // existed this laid out as ONE column -- box_builder decides
        // is_multicol from the longhands, and neither was ever set.
        Fixture f;
        CHECK(f.css("#m { width: 760px; columns: 3; column-gap: 20px }"
                    ".item { height: 60px }"));
        CHECK(f.layout("<body><div id=m><div id=a class=item></div><div id=b class=item></div>"
                       "<div id=c class=item></div><div id=d class=item></div>"
                       "<div id=e class=item></div><div id=g class=item></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("a").width, 240));
        CHECK(near(f.box("c").x, 260));
        CHECK(near(f.box("e").x, 520));
        CHECK(near(f.box("m").height, 120));
    }
    {
        // And the width form, which the same shorthand has to tell apart from
        // the count form by nothing but the unit.
        Fixture f;
        CHECK(f.css("#m { width: 760px; columns: 200px; column-gap: 20px }"
                    ".item { height: 60px }"));
        CHECK(f.layout("<body><div id=m><div id=a class=item></div><div id=b class=item></div>"
                       "<div id=c class=item></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 260));
        CHECK(near(f.box("c").x, 520));
        CHECK(near(f.box("a").width, 240));
    }
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
        CHECK(multicol_is_fully_ported());
    }

    // Fragmentation: a paragraph's lines flow down one column and into the
    // next, and its box is where its lines are -- the union of its
    // fragments, as getBoundingClientRect reports it. Twelve lines of 10px
    // in two 140px columns balance to six lines each; the paragraph then
    // spans both columns and stands six lines tall.
    {
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2;column-gap:20px;font-size:10px;line-height:10px}p{margin:0}"));
        std::string words;
        for (int i = 0; i < 60; ++i) words += "word ";
        CHECK(f.layout("<div id=columns><p id=p>" + words + "</p></div>"));
        const Box& p = f.box("p");
        const Box& columns = f.box("columns");
        // The container is as tall as its tallest column, which is at most
        // half the flow plus one line; the paragraph spans both columns.
        CHECK(near(p.x, 0));
        CHECK(near(p.width, 300));
        CHECK(near(columns.height, p.height));
        CHECK(columns.height <= 70 + 1e-6);   // twelve or thirteen lines of 10px, balanced
        CHECK(columns.height >= 30);
    }

    // `break-inside: avoid` keeps a block whole: three 30px cards in two
    // columns balance to 60 (two and one, filled in order), and no card
    // straddles the column boundary. Cards of 46, 30 and 30 with 8px margins
    // -- the cov-multicol sample -- balance to 68: one, then two, as Chrome.
    {
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2;column-gap:20px}.card{break-inside:avoid;height:30px}"));
        CHECK(f.layout("<div id=columns><div id=a class=card></div><div id=b class=card></div><div id=c class=card></div></div>"));
        CHECK(near(f.box("columns").height, 60));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 30));
        CHECK(near(f.box("c").x, 160) && near(f.box("c").y, 0));
    }
    {
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2;column-gap:20px}.card{break-inside:avoid;margin-bottom:8px}#a{height:46px}#b,#c{height:30px}"));
        CHECK(f.layout("<div id=columns><div id=a class=card></div><div id=b class=card></div><div id=c class=card></div></div>"));
        CHECK(near(f.box("columns").height, 68));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 160) && near(f.box("b").y, 0));
        CHECK(near(f.box("c").x, 160) && near(f.box("c").y, 38));
    }

    // A forced break: `break-before: column` starts a new column even when
    // the content would have fit.
    {
        Fixture f;
        CHECK(f.css("#columns{width:300px;column-count:2;column-gap:20px}.b{height:10px}#c{break-before:column}"));
        CHECK(f.layout("<div id=columns><div id=a class=b></div><div id=c class=b></div></div>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("c").x, 160) && near(f.box("c").y, 0));
        CHECK(near(f.box("columns").height, 10));
    }
}
