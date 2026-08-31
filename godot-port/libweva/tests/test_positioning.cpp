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
        ctx.viewport_width_px = vw;
        ctx.viewport_height_px = vh;
        BlockLayout bl(&tree, ctx, &metrics);
        bl.layout_root(root, vw, vh);
        run_positioning(&tree, root, ctx, &bl);
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
    // Root-relative origin, which is what the placement rules are stated in.
    std::pair<double, double> abs_pos(std::string_view id) const {
        double x = 0, y = 0;
        absolute_position(tree, find(id), &x, &y);
        return {x, y};
    }
};

bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

} // namespace

void test_relative_positioning() {
    // `relative` offsets the box from its in-flow position WITHOUT affecting
    // the flow: the sibling after it stays where it was.
    Fixture f;
    CHECK(f.css("div { display: block; height: 50px }"
                "#r { position: relative; top: 10px; left: 20px }"
                "#neg { position: relative; bottom: 5px; right: 5px }"
                "#over { position: relative; left: 7px; right: 100px }"));
    CHECK(f.layout("<body><div id=a></div><div id=r></div><div id=neg></div>"
                   "<div id=over></div><div id=after></div></body>"));

    CHECK(near(f.box("r").y, 50 + 10));
    CHECK(near(f.box("r").x, 20));
    // bottom/right push the other way.
    CHECK(near(f.box("neg").y, 100 - 5));
    CHECK(near(f.box("neg").x, -5));
    // §9.4.3 over-constrained: with both edges given, the start edge wins in
    // LTR and `right` is ignored.
    CHECK(near(f.box("over").x, 7));
    // The flow is untouched: `after` sits where it would have without any of
    // the offsets above.
    CHECK(near(f.box("after").y, 200));
}

void test_absolute_containing_block() {
    {
        // The containing block is the nearest POSITIONED ancestor's padding
        // box — inside the border, so `inset: 0` lands two border-widths
        // smaller than the ancestor's border box.
        Fixture f;
        CHECK(f.css("#outer { display: block; position: relative; width: 400px;"
                    "         height: 300px; margin-left: 100px;"
                    "         border-left-style: solid; border-left-width: 5px;"
                    "         border-top-style: solid; border-top-width: 5px }"
                    "#a { position: absolute; top: 0; left: 0; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=outer><div id=a></div></div></body>"));
        const auto p = f.abs_pos("a");
        CHECK(near(p.first, 105) && near(p.second, 5));
    }
    {
        // A STATIC ancestor does not establish one, so the box resolves against
        // the viewport instead.
        Fixture f;
        CHECK(f.css("#outer { display: block; margin-left: 100px; margin-top: 50px }"
                    "#a { position: absolute; top: 0; left: 0; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=outer><div id=a></div></div></body>"));
        const auto p = f.abs_pos("a");
        CHECK(near(p.first, 0) && near(p.second, 0));
    }
    {
        // ...but a `transform` on a static ancestor DOES capture it (CSS
        // Transforms L1 §6.1). Missing this is how an `inset: 0` child of
        // `transform: scale(1)` ends up filling the viewport.
        Fixture f;
        CHECK(f.css("#outer { display: block; margin-left: 100px; width: 200px;"
                    "         height: 100px; transform: scale(1) }"
                    "#a { position: absolute; top: 0; left: 0; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=outer><div id=a></div></div></body>"));
        CHECK(near(f.abs_pos("a").first, 100));
    }
    {
        // The same properties capture `position: fixed`, which otherwise
        // resolves against the viewport regardless of positioned ancestors.
        Fixture f;
        CHECK(f.css("#rel { display: block; position: relative; margin-left: 100px;"
                    "       width: 200px; height: 100px }"
                    "#tr { display: block; position: relative; margin-left: 60px;"
                    "      width: 200px; height: 100px; filter: blur(1px) }"
                    "#a, #b { position: fixed; top: 0; left: 0; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=rel><div id=a></div></div>"
                       "<div id=tr><div id=b></div></div></body>"));
        // A merely positioned ancestor does NOT capture a fixed box.
        CHECK(near(f.abs_pos("a").first, 0));
        // A filtered one does.
        CHECK(near(f.abs_pos("b").first, 60));
    }
}

void test_absolute_placement() {
    {
        // Each edge places against the corresponding containing-block edge.
        Fixture f;
        CHECK(f.css("#o { display: block; position: relative; width: 400px; height: 300px }"
                    "div div { position: absolute; width: 20px; height: 10px }"
                    "#tl { top: 5px; left: 7px } #br { bottom: 5px; right: 7px }"));
        CHECK(f.layout("<body><div id=o><div id=tl></div><div id=br></div></div></body>"));
        CHECK(near(f.box("tl").x, 7) && near(f.box("tl").y, 5));
        // right/bottom measure from the far edge inward, so the box's own size
        // is subtracted.
        CHECK(near(f.box("br").x, 400 - 7 - 20));
        CHECK(near(f.box("br").y, 300 - 5 - 10));
    }
    {
        // Percentage offsets resolve against the containing block, not the
        // parent's provisional width.
        Fixture f;
        CHECK(f.css("#o { display: block; position: relative; width: 400px; height: 200px }"
                    "#a { position: absolute; top: 50%; left: 25%; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=o><div id=a></div></div></body>"));
        CHECK(near(f.box("a").x, 100) && near(f.box("a").y, 100));
    }
    {
        // Both edges pinned with no explicit size: the box stretches between
        // them.
        Fixture f;
        CHECK(f.css("#o { display: block; position: relative; width: 400px; height: 200px }"
                    "#a { position: absolute; left: 30px; right: 50px; top: 10px; bottom: 20px }"));
        CHECK(f.layout("<body><div id=o><div id=a></div></div></body>"));
        CHECK(near(f.box("a").width, 400 - 30 - 50));
        CHECK(near(f.box("a").height, 200 - 10 - 20));
        CHECK(near(f.box("a").x, 30) && near(f.box("a").y, 10));
    }
    {
        // `inset: 0; margin: auto` centres, which is the dialog pattern. The
        // slack on each axis is split evenly between the two auto margins.
        Fixture f;
        CHECK(f.css("#o { display: block; position: relative; width: 400px; height: 200px }"
                    "#a { position: absolute; inset: 0; margin: auto;"
                    "     width: 100px; height: 50px }"));
        CHECK(f.layout("<body><div id=o><div id=a></div></div></body>"));
        CHECK(near(f.box("a").x, (400 - 100) * 0.5));
        CHECK(near(f.box("a").y, (200 - 50) * 0.5));
    }
    {
        // With NEITHER edge on an axis, the box keeps its STATIC position —
        // where it would have been in flow — rather than snapping to the
        // containing block's origin.
        Fixture f;
        CHECK(f.css("#o { display: block; position: relative; width: 400px }"
                    "#first { display: block; height: 60px }"
                    "#a { position: absolute; width: 10px; height: 10px }"));
        CHECK(f.layout("<body><div id=o><div id=first></div><div id=a></div></div></body>"));
        CHECK(near(f.box("a").y, 60));
        CHECK(near(f.box("a").x, 0));
    }
}

void test_offsets_and_zindex() {
    Fixture f;
    CHECK(f.css("#auto { display: block } #zero { display: block; top: 0 }"
                "#z { display: block; z-index: 5 } #zn { display: block; z-index: -2 }"
                "#za { display: block; z-index: auto }"));
    CHECK(f.layout("<body><div id=auto></div><div id=zero></div><div id=z></div>"
                   "<div id=zn></div><div id=za></div></body>"));

    // `auto` is ABSENT, not zero: the two lead to different placement, so the
    // distinction has to survive into the box.
    CHECK(!f.box("auto").offset_top.has_value());
    CHECK(f.box("zero").offset_top.has_value() && near(*f.box("zero").offset_top, 0));

    CHECK(f.box("z").z_index.has_value() && *f.box("z").z_index == 5);
    CHECK(f.box("zn").z_index.has_value() && *f.box("zn").z_index == -2);
    // `auto` z-index is absent too — it participates in its parent's stacking
    // context rather than creating one.
    CHECK(!f.box("za").z_index.has_value());
    CHECK(!f.box("auto").z_index.has_value());
}

void test_out_of_flow_relayout() {
    // The children of a pinned box were sized against the containing block's
    // PROVISIONAL width during block layout. Once the pin narrows the box, its
    // content must be re-laid or it keeps the wider measure and overflows.
    Fixture f;
    CHECK(f.css("#o { display: block; position: relative; width: 400px; height: 200px }"
                "#a { position: absolute; left: 0; right: 300px; font-size: 16px }"));
    CHECK(f.layout("<body><div id=o><div id=a>hello world</div></div></body>"));
    CHECK(near(f.box("a").width, 100));
    // 88px of text fits on one line in 100px; the point is that the runs were
    // re-measured against 100 rather than 400.
    int lines = 0;
    for (BoxId c : f.tree.children(f.find("a"))) {
        if (f.tree[c].kind == BoxKind::Line) ++lines;
    }
    CHECK(lines == 1);
    CHECK(near(f.tree[f.tree.child_at(f.find("a"), 0)].width, 100));
}
