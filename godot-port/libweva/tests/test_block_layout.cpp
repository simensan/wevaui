#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
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

    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        styles.engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    bool build(std::string_view html) {
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
        return root != kNoBox;
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
    // Applies the box model to `id` against a containing block of `cb_width`,
    // using the element's real parent style.
    double apply(std::string_view id, double cb_width) {
        const BoxId b = find(id);
        if (b == kNoBox) return -1;
        const BoxId p = tree[b].parent;
        const ComputedStyle* ps = p == kNoBox ? nullptr : tree[p].style;
        return apply_box_model(&tree, b, cb_width, ps, ctx);
    }
    const Box& box(std::string_view id) const { return tree[find(id)]; }
};

bool near(double a, double b) { return a - b < 1e-9 && b - a < 1e-9; }

} // namespace

void test_box_model_edges() {
    Fixture f;
    // Borders are written as longhands: this port does not yet expand
    // shorthands at cascade time, so `border: solid 6px` sets nothing. See
    // test_shorthand_expansion_gap below.
    CHECK(f.css("#a { padding: 5px 10px; margin: 1px 2px 3px 4px;"
                "     border-top-style: solid; border-top-width: 6px;"
                "     border-right-style: solid; border-right-width: 7px;"
                "     border-bottom-style: solid; border-bottom-width: 8px;"
                "     border-left-style: solid; border-left-width: 9px }"
                "#pct { padding: 10%; margin-top: 10% }"
                "#nostyle { border-top-width: 6px; border-left-width: 6px }"
                "#em { padding: 2em; font-size: 20px }"
                "#lh { padding: 1lh; font-size: 20px; line-height: 2 }"
                "#lhlong { padding-top: 1lh; font-size: 20px; line-height: 2 }"));
    CHECK(f.build("<div id=a></div><div id=pct></div><div id=nostyle></div>"
                  "<div id=em></div><div id=lh></div><div id=lhlong></div>"));

    f.apply("a", 1000);
    const Box& a = f.box("a");
    CHECK(near(a.padding_top, 5) && near(a.padding_right, 10));
    CHECK(near(a.padding_bottom, 5) && near(a.padding_left, 10));
    CHECK(near(a.margin_top, 1) && near(a.margin_right, 2));
    CHECK(near(a.margin_bottom, 3) && near(a.margin_left, 4));
    CHECK(near(a.border_top, 6) && near(a.border_right, 7));
    CHECK(near(a.border_bottom, 8) && near(a.border_left, 9));

    // Percentage padding and margin resolve against the containing block's
    // WIDTH on every edge — margin-top included, which is the surprising one.
    f.apply("pct", 400);
    CHECK(near(f.box("pct").padding_top, 40) && near(f.box("pct").padding_left, 40));
    CHECK(near(f.box("pct").margin_top, 40));

    // border-style is `none` by default, so setting only border-width gives no
    // border at all.
    f.apply("nostyle", 1000);
    CHECK(near(f.box("nostyle").border_top, 0) && near(f.box("nostyle").border_left, 0));

    // em binds to the element's own font size.
    f.apply("em", 1000);
    CHECK(near(f.box("em").padding_top, 40));
    // `padding: 1lh` gives NO padding. The shorthand expander's <length>
    // validator predates `lh` and rejects the token, so the shorthand expands
    // to nothing — and an unexpandable shorthand is still dropped, taking the
    // declaration with it. Authored as a longhand (`padding-top: 1lh`) it
    // resolves to 40px. Reference behaviour, pinned rather than tidied.
    f.apply("lh", 1000);
    CHECK(near(f.box("lh").padding_top, 0));
    f.apply("lhlong", 1000);
    CHECK(near(f.box("lhlong").padding_top, 40));
}

void test_box_model_width() {
    Fixture f;
    CHECK(f.css("#fixed { width: 300px; padding: 10px;"
                "         border-left-style: solid; border-left-width: 5px;"
                "         border-right-style: solid; border-right-width: 5px }"
                "#bb { width: 300px; padding: 10px; box-sizing: border-box;"
                "      border-left-style: solid; border-left-width: 5px;"
                "      border-right-style: solid; border-right-width: 5px }"
                "#pct { width: 50%; padding: 10px }"
                "#auto { margin: 0 20px }"
                "#ratio { aspect-ratio: 2; height: 100px }"
                "#ratiobb { aspect-ratio: 2; height: 100px; padding: 10px }"));
    CHECK(f.build("<div id=fixed></div><div id=bb></div><div id=pct></div>"
                  "<div id=auto></div><div id=ratio></div><div id=ratiobb></div>"));

    // width/height on a Box are always BORDER-box, so under the default
    // content-box sizing the frame is added to the authored width.
    f.apply("fixed", 1000);
    CHECK(near(f.box("fixed").width, 300 + 20 + 10));
    CHECK(near(f.box("fixed").content_width(), 300));

    // Under border-box the authored value already includes the frame.
    f.apply("bb", 1000);
    CHECK(near(f.box("bb").width, 300));
    CHECK(near(f.box("bb").content_width(), 300 - 20 - 10));

    f.apply("pct", 400);
    CHECK(near(f.box("pct").width, 200 + 20));

    // An auto width fills the space left after margins.
    f.apply("auto", 500);
    CHECK(near(f.box("auto").width, 500 - 40));

    // CSS Sizing L4 §5: with width auto and a definite height, the ratio
    // derives the width. The reference takes the ratio result as the BORDER-box
    // width directly — no frame is added, and the ratio is measured against the
    // authored (content) height. So padding does not widen the box here, which
    // is not what box-sizing would suggest.
    f.apply("ratio", 1000);
    CHECK(near(f.box("ratio").width, 200));
    f.apply("ratiobb", 1000);
    CHECK(near(f.box("ratiobb").width, 200));
}

void test_box_model_min_max() {
    Fixture f;
    CHECK(f.css("#max { width: 800px; max-width: 300px }"
                "#min { width: 100px; min-width: 400px }"
                "#both { width: 500px; min-width: 400px; max-width: 200px }"
                "#pct { width: 800px; max-width: 25% }"
                "#frame { width: 800px; max-width: 300px; padding: 10px }"
                "#framebb { width: 800px; max-width: 300px; padding: 10px;"
                "           box-sizing: border-box }"));
    CHECK(f.build("<div id=max></div><div id=min></div><div id=both></div>"
                  "<div id=pct></div><div id=frame></div><div id=framebb></div>"));

    f.apply("max", 1000);
    CHECK(near(f.box("max").width, 300));
    f.apply("min", 1000);
    CHECK(near(f.box("min").width, 400));

    // CSS Sizing L3 §5.2: when min exceeds max, MIN wins. Applying max first
    // and min second gets that without a special case.
    f.apply("both", 1000);
    CHECK(near(f.box("both").width, 400));

    f.apply("pct", 1000);
    CHECK(near(f.box("pct").width, 250));

    // min/max share width's box-sizing basis, so under content-box the bound
    // is a CONTENT bound and the frame is added before comparing.
    f.apply("frame", 1000);
    CHECK(near(f.box("frame").width, 320));
    f.apply("framebb", 1000);
    CHECK(near(f.box("framebb").width, 300));
}

void test_box_model_height_and_position() {
    Fixture f;
    CHECK(f.css("#h { height: 200px; padding: 10px }"
                "#hbb { height: 200px; padding: 10px; box-sizing: border-box }"
                "#outer { height: 400px } #inner { height: 50% }"
                "#orphan { height: 25% }"
                "#rel { position: relative } #abs { position: absolute }"
                "#stick { position: STICKY } #plain {}"));
    CHECK(f.build("<div id=h></div><div id=hbb></div>"
                  "<div id=outer><div id=inner></div></div><div id=orphan></div>"
                  "<div id=rel></div><div id=abs></div><div id=stick></div><div id=plain></div>"));

    f.apply("h", 1000);
    CHECK(near(f.box("h").height, 220));
    f.apply("hbb", 1000);
    CHECK(near(f.box("hbb").height, 200));

    // A percentage height needs the PARENT's definite content height, so the
    // parent has to be resolved first.
    f.apply("outer", 1000);
    f.apply("inner", 1000);
    CHECK(near(f.box("inner").height, 200));
    // With no definite parent height there is no basis, and the height stays
    // unresolved rather than collapsing to zero.
    f.apply("orphan", 1000);
    CHECK(near(f.box("orphan").height, 0));

    f.apply("rel", 100);
    f.apply("abs", 100);
    f.apply("stick", 100);
    f.apply("plain", 100);
    CHECK(f.box("rel").position == PositionType::Relative);
    CHECK(f.box("abs").position == PositionType::Absolute);
    CHECK(f.box("stick").position == PositionType::Sticky);
    CHECK(f.box("plain").position == PositionType::Static);
}

void test_auto_margin_centering() {
    Fixture f;
    CHECK(f.css("#c { width: 400px; margin: 0 auto }"
                "#fill { margin: 0 auto }"
                "#clamped { max-width: 400px; margin: 0 auto }"
                "#one { width: 400px; margin-left: auto }"
                "#abs { width: 400px; margin: 0 auto; position: absolute }"
                "#ib { width: 400px; margin: 0 auto; display: inline-block }"
                "#wide { width: 1200px; margin: 0 auto }"));
    CHECK(f.build("<div id=c></div><div id=fill></div><div id=clamped></div>"
                  "<div id=one></div><div id=abs></div><span id=ib></span>"
                  "<div id=wide></div>"));

    f.apply("c", 1000);
    CHECK(near(f.box("c").margin_left, 300) && near(f.box("c").margin_right, 300));

    // A still-filling auto width leaves no free space, so it does not centre.
    f.apply("fill", 1000);
    CHECK(near(f.box("fill").width, 1000));
    CHECK(near(f.box("fill").margin_left, 0));

    // ...but an auto width that a max-width clamp shrank below its fill width
    // does: the gap is free space for the margins to absorb. This is the
    // `width:auto; max-width:X; margin:0 auto` pattern.
    f.apply("clamped", 1000);
    CHECK(near(f.box("clamped").width, 400));
    CHECK(near(f.box("clamped").margin_left, 300));

    // Centring needs BOTH margins auto.
    f.apply("one", 1000);
    CHECK(near(f.box("one").margin_left, 0));

    // Out-of-flow and inline-level boxes are placed by other machinery; adding
    // the in-flow centring margin to them shifts them off-centre.
    f.apply("abs", 1000);
    CHECK(near(f.box("abs").margin_left, 0));
    f.apply("ib", 1000);
    CHECK(near(f.box("ib").margin_left, 0));

    // A box wider than its containing block has no space to distribute, and
    // must not get a negative margin.
    f.apply("wide", 1000);
    CHECK(near(f.box("wide").margin_left, 0));
}

void test_shorthand_expansion_gap() {
    // Formerly a pinned gap: the cascade now expands shorthands, so a
    // shorthand and a longhand resolve by ordinary source order, matching both
    // the C# and a browser.
    Fixture f;
    CHECK(f.css("#mix { padding: 5px; padding-left: 20px }"
                "#rev { padding-left: 20px; padding: 5px }"
                "#bord { border: solid 5px }"
                "#reset { border-width: 9px; border: solid }"));
    CHECK(f.build("<div id=mix></div><div id=rev></div><div id=bord></div>"
                  "<div id=reset></div>"));

    f.apply("mix", 1000);
    CHECK(near(f.box("mix").padding_top, 5));
    CHECK(near(f.box("mix").padding_left, 20));

    // Reversed, the shorthand is later and wins every side.
    f.apply("rev", 1000);
    CHECK(near(f.box("rev").padding_left, 5));

    f.apply("bord", 1000);
    CHECK(near(f.box("bord").border_left, 5));
    CHECK(near(f.box("bord").border_top, 5));

    // A component the author omitted resets to its INITIAL value rather than
    // being left alone, so a later `border: solid` clears the earlier width
    // back to `medium` (3px) instead of keeping 9px.
    f.apply("reset", 1000);
    CHECK(near(f.box("reset").border_top, 3));
}
