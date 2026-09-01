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
    // And once the content (100) is clamped up to the min (600), THAT size is
    // definite and the flexible item grows into it (§9.2 step 4).
    CHECK(near(f.box("s").height, 600));
    CHECK(near(f.box("rest").height, 500));
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

void test_flex_min_max_carry_the_frame() {
    // min-/max-width are content-box sizes under the default box-sizing, and
    // the algorithm clamps the BORDER-box main size — so the frame is added,
    // the same way flex-basis gets it. `min-width: 38px; padding: 0 10px`
    // clamped the border box to 38 where Chrome and the reference give 58.
    {
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 400px }"
                    "#a { min-width: 38px; padding: 0 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a>x</div></div></body>"));
        CHECK(near(f.box("a").width, 58));
    }
    {
        // Under border-box the min already IS a border-box size.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 400px }"
                    "#a { box-sizing: border-box; min-width: 38px; padding: 0 10px;"
                    "     height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a>x</div></div></body>"));
        CHECK(near(f.box("a").width, 38));
    }
    {
        // max-width the same way: 30 of content plus 20 of padding.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 400px }"
                    "#a { flex: 1; max-width: 30px; padding: 0 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").width, 50));
    }
    {
        // Column: min-height carries the vertical frame.
        Fixture f;
        CHECK(f.css("#r { display: flex; flex-direction: column; width: 100px; height: 300px }"
                    "#a { min-height: 40px; padding: 5px 0; width: 10px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").height, 50));
    }
}

void test_flex_auto_margins_on_main_axis() {
    // §9.5 step 1: positive free space goes to auto margins on the main axis
    // before justify-content sees any of it.
    {
        // margin-left: auto pushes the item to the end and starves
        // justify-content: center of its space.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px; justify-content: center }"
                    "#a { width: 50px; height: 10px }"
                    "#b { width: 50px; height: 10px; margin-left: auto }"));
        CHECK(f.layout("<body><div id=r><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 250));
    }
    {
        // Two auto margins split the space equally: the item is centred, and
        // the value block layout may have resolved for the same margin is not
        // added on top.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px }"
                    "#a { width: 100px; height: 10px; margin-left: auto; margin-right: auto }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").x, 100));
        CHECK(near(f.box("a").margin_left, 100));
    }
    {
        // Column: margin-top: auto on the footer of a column that a row flex
        // line stretched to 300 — the stretched height is what it pushes
        // against, so the footer sits at the bottom.
        Fixture f;
        CHECK(f.css("#row { display: flex; align-items: stretch; height: 300px; width: 400px }"
                    "#col { display: flex; flex-direction: column; flex: 0 0 200px }"
                    "#top { height: 20px }"
                    "#foot { margin-top: auto; height: 24px }"));
        CHECK(f.layout("<body><div id=row><div id=col><div id=top></div>"
                       "<div id=foot></div></div></div></body>"));
        CHECK(near(f.box("col").height, 300));
        CHECK(near(f.box("foot").y, 276));
    }
    {
        // No free space, no effect: an auto margin on an overflowing line is 0.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 100px }"
                    "#a { width: 80px; height: 10px; flex-shrink: 0 }"
                    "#b { width: 80px; height: 10px; flex-shrink: 0; margin-left: auto }"));
        CHECK(f.layout("<body><div id=r><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("b").x, 80));
    }
}

void test_flex_column_item_height_is_definite_for_its_content() {
    // §9.8: an item's flexed main size is definite for its own contents, so a
    // column item is RE-LAID at that height rather than stamped with it — a
    // nested row flex inside stretches its children into it. Stamping alone
    // left a `flex: 1 1 auto` row 180 tall holding a 0-tall stretched child.
    Fixture f;
    CHECK(f.css("#col { display: flex; flex-direction: column; height: 200px; width: 200px }"
                "#top { height: 20px }"
                "#row { display: flex; flex: 1 1 auto; align-items: stretch }"
                "#bar { flex: 0 0 30px }"));
    CHECK(f.layout("<body><div id=col><div id=top></div><div id=row><div id=bar></div>"
                   "</div></div></body>"));
    CHECK(near(f.box("row").height, 180));
    CHECK(near(f.box("bar").height, 180));
    CHECK(near(f.box("bar").width, 30));
}

void test_flex_container_min_height_makes_the_main_size_definite() {
    // §9.2 step 4: an auto-height column container is sized to its content
    // and then clamped by its own min/max-height; the clamped size is definite
    // and the items flex into it. The page-shell idiom — `min-height: 100vh`
    // with a `flex: 1` body — is exactly this, and the body's 0% basis stayed
    // 0 without it.
    {
        Fixture f;
        CHECK(f.css("#app { display: flex; flex-direction: column; min-height: 600px;"
                    "       width: 800px }"
                    "#top { height: 60px }"
                    "#content { flex: 1; display: flex }"
                    "#side { width: 200px }"
                    "#main { flex: 1 }"));
        CHECK(f.layout("<body><div id=app><div id=top></div><div id=content>"
                       "<div id=side>Side</div><div id=main>Main</div></div></div></body>"));
        CHECK(near(f.box("app").height, 600));
        CHECK(near(f.box("content").height, 540));
        // ...and that height is in turn definite for the nested row, whose
        // items stretch to it.
        CHECK(near(f.box("side").height, 540));
        CHECK(near(f.box("main").height, 540));
    }
    {
        // max-height shrinks the items the same way.
        Fixture f;
        CHECK(f.css("#c { display: flex; flex-direction: column; max-height: 100px; width: 100px }"
                    ".i { height: 80px }"));
        CHECK(f.layout("<body><div id=c><div id=a class=i></div><div id=b class=i></div>"
                       "</div></body>"));
        CHECK(near(f.box("c").height, 100));
        CHECK(near(f.box("a").height, 50));
        CHECK(near(f.box("b").y, 50));
    }
    {
        // Content already past the min: nothing changes, the container is its
        // content height and nothing flexes.
        Fixture f;
        CHECK(f.css("#c { display: flex; flex-direction: column; min-height: 50px; width: 100px }"
                    ".i { height: 80px } #b { flex: 1 }"));
        CHECK(f.layout("<body><div id=c><div id=a class=i></div><div id=b class=i></div>"
                       "</div></body>"));
        CHECK(near(f.box("c").height, 160));
        CHECK(near(f.box("b").height, 80));
    }
}

void test_flex_stretch_is_clamped_by_the_items_min_max() {
    // §9.4: a stretched cross size still honours the item's own min/max in
    // that axis. A `max-width: 760px` grid in a column flex was stretched to
    // the page width.
    {
        Fixture f;
        CHECK(f.css("#col { display: flex; flex-direction: column; width: 1200px }"
                    "#g { max-width: 760px; height: 10px }"
                    "#m { min-width: 900px; width: auto; height: 10px }"));
        CHECK(f.layout("<body><div id=col><div id=g></div><div id=m></div></div></body>",
                       1280, 720));
        CHECK(near(f.box("g").width, 760));
        CHECK(near(f.box("m").width, 1200));
        Fixture h;
        CHECK(h.css("#col { display: flex; flex-direction: column; width: 800px }"
                    "#m { min-width: 900px; height: 10px }"));
        CHECK(h.layout("<body><div id=col><div id=m></div></div></body>", 1280, 720));
        CHECK(near(h.box("m").width, 900));
    }
    {
        // Row: max-height clamps the stretched height.
        Fixture f;
        CHECK(f.css("#r { display: flex; width: 300px; height: 200px }"
                    "#a { width: 50px; max-height: 80px }"));
        CHECK(f.layout("<body><div id=r><div id=a></div></div></body>"));
        CHECK(near(f.box("a").height, 80));
    }
}

void test_flex_row_min_height_is_the_line_cross_size() {
    // A row container with an auto height but a min-height is at least that
    // tall, so `align-items: flex-end` has something to push against — the
    // full-viewport dialogue stage.
    Fixture f;
    CHECK(f.css("#stage { display: flex; align-items: flex-end; justify-content: center;"
                "         min-height: 600px; padding-bottom: 56px; width: 1000px }"
                "#d { width: 820px; height: 188px }"));
    CHECK(f.layout("<body><div id=stage><div id=d></div></div></body>", 1000, 600));
    CHECK(near(f.box("stage").height, 656));
    CHECK(near(f.box("d").y, 412));
    CHECK(near(f.box("d").x, 90));
}

void test_flex_row_max_content_sums_its_items() {
    // The intrinsic width of a flex row is the sum of its items plus gaps, not
    // the widest item: an absolutely positioned pill (icon + amount) shrank to
    // fit its amount alone and its icon was then shrunk to make room.
    Fixture f;
    CHECK(f.css("#pill { position: absolute; top: 0; right: 0; display: flex;"
                "        align-items: center; gap: 10px; padding: 8px 20px 8px 12px }"
                "#coin { width: 26px; height: 26px }"
                "#amt { width: 50px; height: 10px }"));
    CHECK(f.layout("<body><div id=w><div id=pill><div id=coin></div><div id=amt></div>"
                   "</div></div></body>"));
    CHECK(near(f.box("pill").width, 12 + 26 + 10 + 50 + 20));
    CHECK(near(f.box("coin").width, 26));
    CHECK(near(f.box("amt").x, 12 + 26 + 10));
}
