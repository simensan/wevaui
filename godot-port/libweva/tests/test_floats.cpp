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
        BlockLayout bl(&tree, ctx);
        bl.layout_root(root, vw, vh);
        return true;
    }
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
};

bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

} // namespace

void test_float_keywords() {
    CHECK(parse_float_type("left") == FloatType::Left);
    CHECK(parse_float_type("RIGHT") == FloatType::Right);
    CHECK(parse_float_type("none") == FloatType::None);
    CHECK(parse_float_type("") == FloatType::None);
    // CSS Logical L1 §4.1: the flow-relative keywords alias to left/right,
    // which is only correct in horizontal-tb LTR — the writing mode this float
    // code handles.
    CHECK(parse_float_type("inline-start") == FloatType::Left);
    CHECK(parse_float_type("inline-end") == FloatType::Right);

    CHECK(parse_clear_type("left") == ClearType::Left);
    CHECK(parse_clear_type("both") == ClearType::Both);
    CHECK(parse_clear_type("none") == ClearType::None);
    CHECK(parse_clear_type("inline-end") == ClearType::Right);
}

void test_float_context_queries() {
    FloatContext fc;
    // A 100-wide left float from y=0 to y=50, and a 200-wide right float from
    // y=20 to y=80 in a 1000-wide BFC.
    fc.add({0, FloatType::Left, 0, 50, 0, 100});
    fc.add({1, FloatType::Right, 20, 80, 800, 1000});

    CHECK(near(fc.left_extent_at(0), 100));
    CHECK(near(fc.left_extent_at(49), 100));
    // The y range is half-open: a float ending at y no longer intrudes there.
    CHECK(near(fc.left_extent_at(50), 0));
    CHECK(near(fc.left_extent_at(-1), 0));

    CHECK(near(fc.right_extent_at(10, 1000), 0));
    CHECK(near(fc.right_extent_at(20, 1000), 200));
    CHECK(near(fc.right_extent_at(80, 1000), 0));

    // clear looks at matching sides only, and `both` at either.
    CHECK(near(fc.clear_bottom(ClearType::Left), 50));
    CHECK(near(fc.clear_bottom(ClearType::Right), 80));
    CHECK(near(fc.clear_bottom(ClearType::Both), 80));
    CHECK(near(fc.clear_bottom(ClearType::None), 0));
    CHECK(near(fc.max_bottom(), 80));

    // Placement steps DOWN to the first row with room. At y=0 the free band is
    // 1000-100 = 900; at y=20 it drops to 700 once the right float starts.
    CHECK(near(fc.find_placement_y(0, 900, FloatType::Left, 1000), 0));
    // At y=50 the left float has ended but the right one is still active, so
    // 900 does not fit until y=80 when both have gone.
    CHECK(near(fc.find_placement_y(20, 900, FloatType::Left, 1000), 80));
    CHECK(near(fc.find_placement_y(20, 701, FloatType::Left, 1000), 50));
    CHECK(near(fc.find_placement_y(20, 700, FloatType::Left, 1000), 20));
    // A float wider than the BFC never fits; it lands at the last row tried and
    // overflows rather than looping.
    CHECK(near(fc.find_placement_y(0, 5000, FloatType::Left, 1000), 80));
    // Zero width always fits where asked.
    CHECK(near(fc.find_placement_y(33, 0, FloatType::Left, 1000), 33));
}

void test_float_placement() {
    {
        // Left floats stack left-to-right; right floats stack right-to-left.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#a, #b { float: left; width: 100px; height: 40px }"
                    "#c { float: right; width: 150px; height: 40px }"));
        CHECK(f.layout("<body><div id=w><div id=a></div><div id=b></div>"
                       "<div id=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 100) && near(f.box("b").y, 0));
        CHECK(near(f.box("c").x, 1000 - 150) && near(f.box("c").y, 0));
    }
    {
        // A float that does not fit beside the previous one drops to the row
        // below it rather than overlapping.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#a { float: left; width: 700px; height: 40px }"
                    "#b { float: left; width: 400px; height: 40px }"));
        CHECK(f.layout("<body><div id=w><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 40));
    }
    {
        // A float's margins are part of the box other floats avoid, and they
        // are never collapsed away.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#a { float: left; width: 100px; height: 40px; margin: 10px }"
                    "#b { float: left; width: 100px; height: 40px }"));
        CHECK(f.layout("<body><div id=w><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").x, 10) && near(f.box("a").y, 10));
        // b starts past a's full margin box: 10 + 100 + 10.
        CHECK(near(f.box("b").x, 120));
    }
    {
        // §10.6.7: a BFC grows to enclose its floats even though they are out
        // of the normal flow.
        Fixture f;
        CHECK(f.css("#w { display: flow-root }"
                    "#a { float: left; width: 100px; height: 90px }"));
        CHECK(f.layout("<body><div id=w><div id=a></div></div></body>"));
        CHECK(near(f.box("w").height, 90));
    }
}

void test_float_does_not_advance_flow() {
    // A float is out of the normal flow: the in-flow sibling after it keeps the
    // Y it would have had, and the float does not join the margin-collapse
    // chain either.
    Fixture f;
    CHECK(f.css("#w { display: block }"
                "#a { height: 30px } #b { height: 30px }"
                "#fl { float: left; width: 100px; height: 200px }"));
    CHECK(f.layout("<body><div id=w><div id=a></div><div id=fl></div>"
                   "<div id=b></div></div></body>"));
    CHECK(near(f.box("a").y, 0));
    CHECK(near(f.box("b").y, 30));
    // ...but the container still grows to enclose the float, because body's UA
    // `overflow: hidden` makes it a BFC and #w is inside it. #w itself is not a
    // BFC, so the float belongs to body's context and #w keeps its flow height.
    CHECK(near(f.box("w").height, 60));
    CHECK(near(f.box("fl").y, 30));
}

void test_clear() {
    {
        // `clear: left` pushes the box's top margin edge below the float.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#fl { float: left; width: 100px; height: 80px }"
                    "#after { height: 20px; clear: left }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div><div id=after></div></div></body>"));
        CHECK(near(f.box("after").y, 80));
    }
    {
        // Clearing the other side does nothing.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#fl { float: left; width: 100px; height: 80px }"
                    "#after { height: 20px; clear: right }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div><div id=after></div></div></body>"));
        CHECK(near(f.box("after").y, 0));
    }
    {
        // `clear: both` clears either side.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#fl { float: right; width: 100px; height: 80px }"
                    "#after { height: 20px; clear: both }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div><div id=after></div></div></body>"));
        CHECK(near(f.box("after").y, 80));
    }
    {
        // Clearance moves the box's top MARGIN edge to the clear line, and the
        // box's own margin then applies ON TOP of that: 80 + 30, not 80.
        //
        // CANDIDATE DIVERGENCE. CSS 2.1 §9.5.2 says clearance is introduced
        // so the top margin edge sits below the float, which is one reading of
        // this; Chrome instead lets clearance absorb the margin and lands the
        // border edge at 80. Ported as the reference has it and pinned, since
        // "fixing" it here would guarantee a mismatch against the oracle.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#fl { float: left; width: 100px; height: 80px }"
                    "#after { height: 20px; clear: left; margin-top: 30px }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div><div id=after></div></div></body>"));
        CHECK(near(f.box("after").y, 110));
    }
    {
        // Same rule, and here it over-triggers: a 100px margin would already
        // have cleared a float ending at 40, so the spec introduces NO
        // clearance and the box lands at 100. The reference tests the clear
        // line against the cursor BEFORE folding in the child's own margin, so
        // it clears first and then adds the margin: 40 + 100.
        //
        // Same candidate divergence as above, in its more visible form.
        Fixture f;
        CHECK(f.css("#w { display: block }"
                    "#fl { float: left; width: 100px; height: 40px }"
                    "#after { height: 20px; clear: left; margin-top: 100px }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div><div id=after></div></div></body>"));
        CHECK(near(f.box("after").y, 140));
    }
}

void test_float_bfc_scoping() {
    {
        // Floats do not escape their BFC: a float inside an `overflow: hidden`
        // box is invisible to content outside it.
        Fixture f;
        CHECK(f.css("#bfc { display: block; overflow: hidden }"
                    "#fl { float: left; width: 100px; height: 200px }"
                    "#outside { height: 20px; clear: left }"));
        CHECK(f.layout("<body><div id=bfc><div id=fl></div></div>"
                       "<div id=outside></div></body>"));
        // The BFC encloses its float, so its own height is right...
        CHECK(near(f.box("bfc").height, 200));
        // ...but the box after it sits at 0, overlapping it.
        //
        // CANDIDATE DIVERGENCE. A box whose only children are floats has no
        // in-flow content, so is_self_collapsing calls it self-collapsing and
        // the flow cursor never advances past it — even though §10.6.7 gave it
        // a 200px height. The reference's IsSelfCollapsing tests the STYLE
        // height, not the computed one, so it reaches the same answer. Chrome
        // puts this box at 200. Pinned rather than fixed: the oracle should
        // settle whether to change both engines.
        CHECK(near(f.box("outside").y, 0));
    }
    {
        // An auto-width float currently fills its containing block rather than
        // shrinking to fit, because shrink-to-fit needs intrinsic sizing and
        // that needs the inline formatting context. Pinned as a known
        // limitation; this should fail when inline layout lands.
        Fixture f;
        CHECK(f.css("#w { display: block } #fl { float: left; height: 40px }"));
        CHECK(f.layout("<body><div id=w><div id=fl></div></div></body>"));
        CHECK(near(f.box("fl").width, 1000));
    }
}
