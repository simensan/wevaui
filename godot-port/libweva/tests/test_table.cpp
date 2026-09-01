// CSS 2.1 §17 tables, the separated-borders subset the reference implements.
// Numbers follow the fixture's stand-in metrics: 0.5em per glyph, 1.2 line
// height, so a 16px line is 19.2 tall and "x" is 8 wide.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/positioning.h"
#include "weva/table_layout.h"
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
    // Absolute position, summed up the parent chain — rows sit in groups and
    // cells in rows, so a cell's own y says little on its own.
    double abs_x(std::string_view id) const {
        double x = 0;
        for (BoxId b = find(id); b != kNoBox; b = tree[b].parent) x += tree[b].x;
        return x;
    }
    double abs_y(std::string_view id) const {
        double y = 0;
        for (BoxId b = find(id); b != kNoBox; b = tree[b].parent) y += tree[b].y;
        return y;
    }
};

const double kLine = 16 * 1.2;

} // namespace

void test_table_fixed_layout_columns() {
    // table-layout: fixed with collapsed borders: the first row's authored
    // widths (content-box, so the frame is added) pin their columns and the
    // rest share what is left; cells re-lay at their column width.
    Fixture f;
    CHECK(f.css("#t { width: 400px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 }"
                "#a { width: 100px }"));
    CHECK(f.layout("<body><table id=t><tr id=r><td id=a>x</td><td id=b>y</td>"
                   "<td id=c>z</td></tr></table></body>"));
    CHECK(near(f.box("t").width, 400));
    CHECK(near(f.box("a").width, 100));
    CHECK(near(f.box("b").width, 150));
    CHECK(near(f.box("c").width, 150));
    CHECK(near(f.abs_x("a"), 0));
    CHECK(near(f.abs_x("b"), 100));
    CHECK(near(f.abs_x("c"), 250));
    CHECK(near(f.box("r").height, kLine));
    CHECK(near(f.box("t").height, kLine));
    // Padding is part of the column: a 10px-padded 100px cell takes 120.
    Fixture g;
    CHECK(g.css("#t { width: 400px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 }"
                "#a { width: 100px; padding: 0 10px }"));
    CHECK(g.layout("<body><table id=t><tr><td id=a>x</td><td id=b>y</td></tr></table></body>"));
    CHECK(near(g.box("a").width, 120));
    CHECK(near(g.box("b").width, 280));
}

void test_table_border_spacing_and_auto_layout() {
    // Separated borders: spacing before, between and after the columns and
    // rows. Automatic layout with two auto cells splits the rest equally.
    Fixture f;
    CHECK(f.css("#t { width: 300px; border-spacing: 10px } td { padding: 0 }"));
    CHECK(f.layout("<body><table id=t><tr><td id=a>x</td><td id=b>y</td></tr></table></body>"));
    CHECK(near(f.box("a").width, 135));
    CHECK(near(f.box("b").width, 135));
    CHECK(near(f.abs_x("a"), 10));
    CHECK(near(f.abs_x("b"), 155));
    CHECK(near(f.abs_y("a"), 10));
    CHECK(near(f.box("t").height, 10 + kLine + 10));
    // Two-value form: horizontal then vertical.
    Fixture g;
    CHECK(g.css("#t { width: 300px; border-spacing: 4px 20px } td { padding: 0 }"));
    CHECK(g.layout("<body><table id=t><tr><td id=a>x</td></tr><tr><td id=c>z</td></tr></table></body>"));
    CHECK(near(g.abs_x("a"), 4));
    CHECK(near(g.abs_y("a"), 20));
    CHECK(near(g.abs_y("c"), 20 + kLine + 20));
    CHECK(near(g.box("t").height, 20 + kLine + 20 + kLine + 20));
    // The UA sheet's 2px applies to a bare <table>.
    Fixture u;
    CHECK(u.css("#t { width: 300px } td { padding: 0 }"));
    CHECK(u.layout("<body><table id=t><tr><td id=a>x</td></tr></table></body>"));
    CHECK(near(u.abs_x("a"), 2));
    CHECK(near(u.box("t").height, 2 + kLine + 2));
}

void test_table_colspan_and_rowspan() {
    // A colspan cell covers both columns; a rowspan cell reserves its column
    // in the next row, whose first cell lands in column 1 and whose height
    // the spanning cell shares.
    Fixture f;
    CHECK(f.css("#t { width: 200px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 }"));
    CHECK(f.layout("<body><table id=t>"
                   "<tr><td id=ab colspan=2>x</td></tr>"
                   "<tr><td id=c>x</td><td id=d>y</td></tr>"
                   "</table></body>"));
    CHECK(near(f.box("ab").width, 200));
    CHECK(near(f.box("c").width, 100));
    CHECK(near(f.abs_x("d"), 100));
    CHECK(near(f.abs_y("d"), kLine));

    Fixture g;
    CHECK(g.css("#t { width: 200px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 }"));
    CHECK(g.layout("<body><table id=t>"
                   "<tr><td id=r rowspan=2>x</td><td id=s>y</td></tr>"
                   "<tr><td id=u>z</td></tr>"
                   "</table></body>"));
    CHECK(near(g.abs_x("u"), 100));
    CHECK(near(g.abs_y("u"), kLine));
    CHECK(near(g.box("r").height, 2 * kLine));
    CHECK(near(g.box("t").height, 2 * kLine));
}

void test_table_cells_stretch_and_vertical_align() {
    // Every cell in a row is as tall as the row; `vertical-align: middle`
    // moves a shorter cell's content down by half the slack, `bottom` by all
    // of it, and the default (`top`, and `baseline` for now) leaves it.
    Fixture f;
    CHECK(f.css("#t { width: 300px; border-collapse: collapse } td { padding: 0 }"
                "#tall { height: 60px }"
                "#mid { vertical-align: middle }"
                "#bot { vertical-align: bottom }"));
    CHECK(f.layout("<body><table id=t><tr id=r><td id=tall>x</td><td id=mid>y</td>"
                   "<td id=bot>z</td><td id=top>w</td></tr></table></body>"));
    CHECK(near(f.box("r").height, 60));
    CHECK(near(f.box("mid").height, 60));
    CHECK(near(f.box("top").height, 60));
    const BoxId mid_line = f.tree[f.find("mid")].first_child;
    const BoxId bot_line = f.tree[f.find("bot")].first_child;
    const BoxId top_line = f.tree[f.find("top")].first_child;
    CHECK(mid_line != kNoBox && bot_line != kNoBox && top_line != kNoBox);
    CHECK(near(f.tree[mid_line].y, (60 - kLine) / 2));
    CHECK(near(f.tree[bot_line].y, 60 - kLine));
    CHECK(near(f.tree[top_line].y, 0));
}

void test_table_row_groups_and_captions() {
    // Rows come out header → body → footer whatever the source order, each
    // group spans its rows, and a caption sits above (or, with caption-side:
    // bottom, below) the row stack.
    Fixture f;
    CHECK(f.css("#t { width: 300px; border-collapse: collapse } td, th, caption { padding: 0 }"));
    CHECK(f.layout("<body><table id=t>"
                   "<caption id=cap>Title</caption>"
                   "<tfoot id=foot><tr><td id=f>f</td></tr></tfoot>"
                   "<tbody id=body><tr><td id=b1>b</td></tr><tr><td id=b2>b</td></tr></tbody>"
                   "<thead id=head><tr><th id=h>h</th></tr></thead>"
                   "</table></body>"));
    CHECK(near(f.abs_y("cap"), 0));
    CHECK(near(f.abs_y("h"), kLine));
    CHECK(near(f.abs_y("b1"), 2 * kLine));
    CHECK(near(f.abs_y("b2"), 3 * kLine));
    CHECK(near(f.abs_y("f"), 4 * kLine));
    CHECK(near(f.box("body").height, 2 * kLine));
    CHECK(near(f.box("head").height, kLine));
    CHECK(near(f.box("t").height, 5 * kLine));

    Fixture g;
    CHECK(g.css("#t { width: 300px; border-collapse: collapse } td, caption { padding: 0 }"
                "caption { caption-side: bottom }"));
    CHECK(g.layout("<body><table id=t><caption id=cap>Title</caption>"
                   "<tr><td id=a>a</td></tr></table></body>"));
    CHECK(near(g.abs_y("a"), 0));
    CHECK(near(g.abs_y("cap"), kLine));
}

void test_table_visibility_collapse_and_column_hints() {
    // A collapsed row takes no space; a <col> width pins its column under
    // fixed layout; a collapsed <col> gives its slot up and the survivors
    // slide left.
    Fixture f;
    CHECK(f.css("#t { width: 300px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 } #gone { visibility: collapse }"));
    CHECK(f.layout("<body><table id=t><tr><td id=a>a</td></tr>"
                   "<tr id=gone><td id=g>g</td></tr><tr><td id=b>b</td></tr></table></body>"));
    CHECK(near(f.abs_y("b"), kLine));
    CHECK(near(f.box("t").height, 2 * kLine));

    Fixture g;
    CHECK(g.css("#t { width: 300px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 } #c1 { width: 50px }"));
    CHECK(g.layout("<body><table id=t><colgroup><col id=c1><col id=c2></colgroup>"
                   "<tr><td id=a>a</td><td id=b>b</td></tr></table></body>"));
    CHECK(near(g.box("a").width, 50));
    CHECK(near(g.box("b").width, 250));

    Fixture h;
    CHECK(h.css("#t { width: 300px; table-layout: fixed; border-collapse: collapse }"
                "td { padding: 0 } #c1 { visibility: collapse }"));
    CHECK(h.layout("<body><table id=t><col id=c1><col id=c2>"
                   "<tr><td id=a>a</td><td id=b>b</td></tr></table></body>"));
    CHECK(near(h.box("a").width, 0));
    CHECK(near(h.abs_x("b"), 0));
    CHECK(near(h.box("b").width, 150));
}
