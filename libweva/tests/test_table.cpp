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
#include "weva/table_border.h"
#include "weva/paint.h"
#include "weva/hit_test.h"
#include "weva/software_renderer.h"
#include "weva/user_agent_stylesheet.h"

#include <cmath>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace weva;

void test_table_border_conflicts() {
    using S = TableBorderStyle;
    using O = TableBorderOrigin;
    const auto border = [](double width, S style, O origin, int source, uint32_t order = 0) {
        return TableBorderCandidate{width, style, origin, source, TableBorderSide::Right, order};
    };
    const auto hidden = border(0, S::Hidden, O::Table, 1);
    const auto none = border(100, S::None, O::Cell, 2);
    const auto thick = border(20, S::Double, O::Cell, 3);
    CHECK(resolve_table_border(hidden, thick).source == 1);
    CHECK(resolve_table_border(thick, hidden).used_width() == 0);
    CHECK(resolve_table_border(none, thick).source == 3);
    CHECK(resolve_table_border(thick, none).used_width() == 20);
    CHECK(none.used_width() == 0);

    // Width beats style and origin, while style beats origin at equal width.
    CHECK(resolve_table_border(border(5, S::Dotted, O::Table, 4), thick).source == 3);
    CHECK(resolve_table_border(border(21, S::Inset, O::Table, 4), thick).source == 4);
    CHECK(resolve_table_border(border(20, S::Solid, O::Cell, 4),
                               border(20, S::Double, O::Table, 5)).source == 5);
    const S styles[] = {S::Inset, S::Groove, S::Outset, S::Ridge, S::Dotted,
                        S::Dashed, S::Solid, S::Double};
    for (int i = 0; i < 8; ++i) {
        for (int j = i + 1; j < 8; ++j) {
            const auto weaker = border(3, styles[i], O::Cell, 10);
            const auto stronger = border(3, styles[j], O::Table, 11);
            CHECK(resolve_table_border(weaker, stronger).source == 11);
            CHECK(resolve_table_border(stronger, weaker).source == 11);
        }
    }
    const O origins[] = {O::Table, O::ColumnGroup, O::Column, O::RowGroup, O::Row, O::Cell};
    for (int i = 0; i < 6; ++i) {
        for (int j = i + 1; j < 6; ++j) {
            CHECK(resolve_table_border(border(4, S::Solid, origins[i], 12),
                                       border(4, S::Solid, origins[j], 13)).source == 13);
        }
    }
    // The collector's start/top rank controls ties, independently of visit order.
    auto first = border(1, S::Solid, O::Cell, 14, 2);
    first.side = TableBorderSide::Left;
    const auto second = border(1, S::Solid, O::Cell, 15, 3);
    CHECK(resolve_table_border(second, first).source == 14);
    CHECK(resolve_table_border(first, second).source == 14);
    CHECK(resolve_table_border(second, first).side == TableBorderSide::Left);
    CHECK(resolve_table_border(first, second).used_width() == 1);
    CHECK(resolve_table_border(TableBorderCandidate{}, first).source == 14);
    CHECK(resolve_table_border(first, first).source == 14);
}

void test_table_border_grid() {
    using S = TableBorderStyle;
    using O = TableBorderOrigin;
    const auto sides = [](double width, int source, O origin = O::Cell) {
        TableBorderGrid::Sides result;
        for (int side = 0; side < 4; ++side)
            result[side] = {width, S::Solid, origin, source, static_cast<TableBorderSide>(side), 0};
        return result;
    };
    TableBorderGrid grid;
    CHECK(grid.reset(2, 2));
    CHECK(grid.add_rectangle(0, 0, 1, 1, sides(4, 1), true));
    CHECK(grid.add_rectangle(0, 1, 1, 1, sides(6, 2), true));
    CHECK(grid.add_rectangle(1, 0, 1, 1, sides(2, 3), true));
    CHECK(grid.add_rectangle(1, 1, 1, 1, sides(2, 4), true));
    CHECK(grid.vertical(0, 1).source == 2);
    CHECK(grid.vertical(0, 1).side == TableBorderSide::Left);
    CHECK(grid.horizontal(1, 0).source == 1);
    CHECK(grid.horizontal(1, 0).side == TableBorderSide::Bottom);
    const auto a = grid.half_widths(0, 0, 1, 1);
    CHECK(a[0] == 2 && a[1] == 3 && a[2] == 2 && a[3] == 2);

    // Lower-origin borders still participate: width is considered first.
    CHECK(grid.add_rectangle(0, 0, 2, 2, sides(8, 5, O::Table)));
    CHECK(grid.vertical(0, 0).source == 5);
    CHECK(grid.vertical(0, 1).source == 2); // table has no internal border
    CHECK(grid.horizontal(1, 0).source == 1);

    // A cell spanning two rows suppresses only the horizontal segment inside
    // its rectangle. Later row contributions must not resurrect that edge.
    CHECK(grid.reset(2, 2));
    CHECK(grid.add_rectangle(0, 0, 2, 1, sides(4, 6), true));
    CHECK(grid.add_rectangle(0, 0, 1, 2, sides(10, 7, O::Row)));
    CHECK(grid.horizontal(1, 0).used_width() == 0);
    CHECK(grid.horizontal(1, 1).used_width() == 10);
    CHECK(grid.vertical(0, 1).used_width() == 4);
    CHECK(grid.vertical(1, 1).used_width() == 4);

    // The equivalent colspan suppresses a vertical interior segment, even
    // when its opposing column candidates were collected first.
    CHECK(grid.reset(2, 2));
    CHECK(grid.add_rectangle(0, 0, 2, 1, sides(10, 8, O::Column)));
    CHECK(grid.add_rectangle(0, 0, 1, 2, sides(3, 9), true));
    CHECK(grid.vertical(0, 1).used_width() == 0);
    CHECK(grid.vertical(1, 1).used_width() == 10);
    CHECK(grid.horizontal(1, 0).used_width() == 3);
    CHECK(grid.horizontal(1, 1).used_width() == 3);
    const auto spanning = grid.half_widths(0, 0, 1, 2);
    CHECK(spanning[0] == 5 && spanning[1] == 1.5 && spanning[2] == 1.5 && spanning[3] == 5);

    // Reusing storage must clear suppression as well as winners.
    CHECK(grid.reset(2, 2));
    CHECK(grid.add_rectangle(0, 0, 1, 1, sides(5, 10), true));
    CHECK(grid.vertical(0, 1).used_width() == 5);
    CHECK(grid.horizontal(1, 0).source == 10);
    CHECK(grid.vertical(1, 1).used_width() == 0);
    CHECK(!grid.add_rectangle(0, 0, 3, 1, sides(4, 11)));
    CHECK(!grid.add_rectangle(-1, 0, 1, 1, sides(4, 11)));
    CHECK(!grid.add_rectangle(0, 0, 1, 0, sides(4, 11)));
    CHECK(grid.horizontal(-1, 0).used_width() == 0);
    CHECK(grid.vertical(0, 3).used_width() == 0);
    CHECK(!grid.reset(-1, 2));
    CHECK(grid.horizontal(0, 0).used_width() == 0);
    CHECK(grid.reset(0, 0));
    CHECK(!grid.add_rectangle(0, 0, 1, 1, sides(4, 12)));
}

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
        compute_visual_overflow(&tree, root);
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

void test_table_collapsed_border_paint() {
    Fixture f;
    CHECK(f.css("#t{width:200px;table-layout:fixed;border-collapse:collapse}"
        "td{padding:0;border:4px solid red}td>div{height:20px}#b{border-left:4px solid blue}"));
    CHECK(f.layout("<body><table id=t><tr><td id=a><div></div></td>"
        "<td id=b><div></div></td></tr></table></body>"));
    const BoxId table = f.find("t");
    const auto* borders = f.tree.table_borders(table);
    CHECK(borders && borders->size() == 7);
    if (!borders) return;
    for (const auto& segment : *borders) CHECK(segment.border.source == -1);
    SoftwareRenderer renderer(220, 60);
    PaintContext paint;
    paint.styles = &f.styles;
    paint.backend = &renderer;
    paint_tree(f.tree, f.root, f.ctx, paint);
    CHECK(renderer.pixel(2, 12).r > .99f);
    CHECK(renderer.pixel(50, 2).r > .99f);
    CHECK(renderer.pixel(50, 12).a == 0);
    // At equal width/style/origin, the left cell's red border wins in LTR.
    CHECK(renderer.pixel(100, 12).r > .99f);
    CHECK(renderer.pixel(100, 12).b < .01f);
    CHECK(renderer.pixel(198, 12).r > .99f);

    BoxTree imported;
    const BoxId replacement = imported.create(BoxKind::Block);
    imported.replace_subtree(replacement, f.tree, table);
    CHECK(imported.table_borders(replacement) && imported.table_borders(replacement)->size() == 7);
    f.tree.reset();
    CHECK(!f.tree.table_borders(table));
    renderer.clear(LinearColor::transparent());
    paint_tree(imported, replacement, f.ctx, paint);
    CHECK(renderer.pixel(100, 12).r > .99f);
    imported.reset();
    CHECK(!imported.table_borders(replacement));
}

void test_table_positioned_content_order() {
    // Chrome: ordinary overflow remains below collapsed borders and the next
    // cell background. Positioned content crosses both; positioned cell
    // backgrounds themselves must still remain under the shared border.
    for (const std::string cell : {"static", "relative"})
    for (const std::string content : {"static", "relative", "absolute"})
    for (const std::string first : {"transparent", "cyan"})
    for (const std::string second : {"transparent", "yellow"}) {
        Fixture f;
        CHECK(f.css("html,body{margin:0;padding:0}table{width:200px;table-layout:fixed;border-collapse:collapse}"
            "td{padding:0;border:8px solid red;height:40px}#a{position:" + cell + ";background:" + first + "}"
            "#b{background:" + second + "}#overlay{position:" + content + ";width:140px;height:20px;background:blue;" +
            (content == "absolute" ? "left:0;top:10px;" : "") + "}"));
        CHECK(f.layout("<table id=t><tr><td id=a><div id=overlay></div></td><td id=b></td></tr></table>", 240, 100));
        SoftwareRenderer renderer(240, 100);
        PaintContext paint;
        paint.styles = &f.styles;
        paint.backend = &renderer;
        paint_tree(f.tree, f.root, f.ctx, paint);
        const bool ordinary = cell == "static" && content == "static";
        for (const int x : {98, 102, 120}) {
            const auto top = renderer.pixel(x, 6);
            CHECK(top.r > .99f && top.g < .01f && top.b < .01f && top.a > .99f);
            const auto middle = renderer.pixel(x, 24);
            if (ordinary && x < 104) CHECK(middle.r > .99f && middle.b < .01f);
            else if (ordinary && second == "yellow") CHECK(middle.r > .99f && middle.g > .99f && middle.b < .01f);
            else CHECK(middle.b > .99f && middle.r < .01f && middle.g < .01f);
            const Element* hit = element_at_point(f.tree, f.root, x, 24, &f.ctx);
            CHECK(hit && hit->get_attribute("id") == (ordinary && x >= 100 ? "b" : "overlay"));
        }
    }
}

void test_positioned_overflow_containing_blocks() {
    // Chrome-derived controls: external containing blocks escape intermediate
    // clips/scroll offsets; captured absolute/fixed descendants do not. Rounded
    // clipping affects hit targets as well as pixels, including the clip itself.
    for (const std::string position : {"absolute", "fixed"})
    for (const std::string owner : {"static", "relative", "transform", "displaced"})
    for (const bool rounded : {false, true}) for (const bool scrolled : {false, true})
    for (const bool nested : {false, true}) {
        Fixture f;
        CHECK(f.css("html,body{margin:0;padding:0}#outer{position:relative;width:130px;height:120px;" +
            std::string(nested ? "overflow:hidden;" : "") + "}#clip{width:100px;height:100px;overflow:hidden;" +
            (owner == "relative" ? "position:relative;" : owner == "transform" ? "transform:translate(0,0);" : owner == "displaced" ? "margin-left:300px;" : "") +
            (rounded ? "border-radius:20px;" : "") + "}#overlay{position:" + position +
            ";left:80px;top:0;width:80px;height:20px;background:blue}"
            "#ordinary{position:relative;top:40px;width:200px;height:20px;background:yellow}"));
        CHECK(f.layout("<div id=outer><div id=clip><div id=overlay></div><div id=ordinary></div></div></div>",240,160));
        f.tree[f.find("clip")].scroll_x = scrolled ? 20 : 0;
        SoftwareRenderer renderer(240,160);
        PaintContext paint; paint.styles=&f.styles; paint.backend=&renderer;
        paint_tree(f.tree,f.root,f.ctx,paint);
        const bool captured = owner == "transform" || (position == "absolute" && owner == "relative");
        const auto probe = [&](int x, int y, bool blue, const char* target) {
            const auto pixel = renderer.pixel(x,y);
            CHECK(blue ? pixel.b > .99f && pixel.r < .01f && pixel.a > .99f : pixel.a == 0);
            const auto* hit = element_at_point(f.tree,f.root,x,y,&f.ctx);
            const std::string_view actual = hit ? hit->get_attribute("id") : std::string_view();
            CHECK(actual == target);
        };
        probe(95,5,!captured || !rounded,captured && rounded ? "outer" : "overlay");
        probe(120,10,!captured,captured ? "outer" : "overlay");
        const bool outside_blue = !captured && (!nested || position == "fixed");
        probe(140,10,outside_blue,outside_blue ? "overlay" : "");
        probe(120,50,false,"outer"); // A following sibling must regain the clip.
        const auto ordinary = renderer.pixel(80,50);
        CHECK(owner == "displaced" ? ordinary.a == 0 : ordinary.r > .99f && ordinary.g > .99f && ordinary.b < .01f);
        double x=0,y=0;
        visual_position(f.tree,f.find("overlay"),&x,&y);
        CHECK(near(x,80 - (captured && scrolled ? 20 : 0)) && near(y,0));
    }
}

void test_table_unequal_border_intersections() {
    for (const auto& pair : {std::pair<int,int>{8,4}, {4,8}, {4,4}}) {
        const int h = pair.first, v = pair.second;
        Fixture f;
        CHECK(f.css("#t{width:200px;table-layout:fixed;border-collapse:collapse}td{padding:0;border:" +
            std::to_string(v) + "px solid blue;border-top:" + std::to_string(h) +
            "px solid red;border-bottom:" + std::to_string(h) + "px solid red}td>div{height:20px}"));
        CHECK(f.layout("<body><table id=t><tr><td><div></div></td><td><div></div></td></tr>"
            "<tr><td><div></div></td><td><div></div></td></tr></table></body>"));
        SoftwareRenderer renderer(220,90);
        PaintContext paint; paint.styles = &f.styles; paint.backend = &renderer;
        paint_tree(f.tree,f.root,f.ctx,paint);
        int differences = 0;
        for (int y = 0; y < 90; ++y) for (int x = 0; x < 220; ++x) {
            const bool vertical = y < 40 + 3*h && (x < v || (x >= 100-v/2 && x < 100+v/2) || (x >= 200-v && x < 200));
            const bool horizontal = x < 200 && (y < h || (y >= 20+h && y < 20+2*h) || (y >= 40+2*h && y < 40+3*h));
            // The equal-width top-right junctions are won by the cell right
            // side; the top-left corner and lower crossings are horizontal.
            const bool red = horizontal && (!vertical || h > v || (h == v && !(y < h && x >= 100-v/2)));
            const auto pixel = renderer.pixel(x,y);
            if (red ? pixel.r < .99f || pixel.b > .01f || pixel.a < .99f :
                vertical ? pixel.b < .99f || pixel.r > .01f || pixel.a < .99f : pixel.a > .01f) ++differences;
        }
        if (differences) std::printf("table intersection %d/%d: %d pixel differences\n",h,v,differences);
        CHECK(differences == 0);
    }
}

void test_table_collapsed_content_box_width() {
    for (bool border_box : {false,true}) for (int padding : {0,20}) for (int border : {0,8}) for (int width : {0,1,4,8,10,60,200}) {
        Fixture f;
        CHECK(f.css(std::string("#t{width:") + std::to_string(width) + "px;table-layout:fixed;border-collapse:collapse;box-sizing:" +
            (border_box ? "border-box" : "content-box") + ";padding:" + std::to_string(padding) +
            "px;border:" + std::to_string(border) + "px solid blue}td{padding:0;border:4px solid red}td>div{height:20px}"));
        CHECK(f.layout("<body><table id=t><tbody id=g><tr><td id=a><div></div></td>"
            "<td><div></div></td></tr></tbody></table></body>"));
        const double half_frame = border ? 8 : 4;
        CHECK(near(f.box("t").width, border_box ? std::max(double(width), half_frame) : width + half_frame));
        CHECK(near(f.box("g").width, border_box ? std::max(0.0, width - half_frame) : width));
        CHECK(near(f.box("t").padding_left, 0));
        CHECK(near(f.abs_x("a"), half_frame * .5));
    }
}

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
    // Chrome: the wrapper's full 200px fits these three 65px blocks on one
    // line. Grid borders and padding must not narrow or offset the caption.
    for (bool collapse : {false,true}) for (bool bottom : {false,true}) for (int padding : {0,20}) {
        Fixture c;
        CHECK(c.css(std::string("#t{width:200px;table-layout:fixed;border-collapse:") +
            (collapse ? "collapse" : "separate") + ";border-spacing:2px;padding:" + std::to_string(padding) +
            "px;border:8px solid blue;background:lime}caption{font-size:0;line-height:0;text-align:left;caption-side:" +
            (bottom ? "bottom" : "top") + "}caption i{display:inline-block;width:65px;height:10px}"
            "td{padding:0;border:4px solid red}td>div{height:20px}#after{height:10px}"));
        CHECK(c.layout("<body><table id=t><caption id=cap><i></i><i></i><i></i></caption>"
            "<tbody id=g><tr id=r><td><div></div></td><td><div></div></td></tr></tbody></table><div id=after></div></body>"));
        const double grid_height = collapse ? 36 : 48 + 2 * padding;
        CHECK(near(c.box("cap").width,200));
        CHECK(near(c.box("cap").height,10));
        CHECK(near(c.abs_x("cap"),0));
        CHECK(near(c.abs_y("cap"),bottom ? grid_height : 0));
        CHECK(near(c.box("t").height,grid_height + 10));
        CHECK(near(c.abs_y("after"),grid_height + 10));
        const double inset = collapse ? 4 : 10 + padding;
        CHECK(near(c.abs_x("g"),inset));
        CHECK(near(c.abs_x("r"),inset));
        CHECK(near(c.box("g").width,200 - 2 * inset));
        CHECK(near(c.box("r").width,200 - 2 * inset));
        CHECK(near(c.box("g").height,28));
        SoftwareRenderer renderer(400,300);
        PaintContext paint; paint.backend = &renderer; paint.styles = &c.styles;
        paint_tree(c.tree,c.root,c.ctx,paint);
        CHECK(renderer.pixel(50,static_cast<int>(c.abs_y("cap")) + 5).a == 0);
        const int grid_top = bottom ? 0 : 10;
        CHECK(renderer.pixel(50,grid_top + 2).b > .99f);
        CHECK(renderer.pixel(50,grid_top + static_cast<int>(grid_height) - 2).b > .99f);


    }

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

// Chrome/152.0.7977.83: Tools/oracle/check_table_stacking_chrome.cjs.
// An auto-z cell does not isolate descendant stacking; an explicit zero does.
void test_table_nested_stacking_order() {
    for (const std::string parent_z : {"auto", "0", "2", "-1"}) {
        for (const std::string child_z : {"3", "-1"}) for (bool wrapper : {false, true}) {
            Fixture f;
            CHECK(f.css("html,body{margin:0;padding:0}table{position:relative;width:200px;table-layout:fixed;border-collapse:collapse}"
                "td{padding:0;border:8px solid red;height:40px;position:relative}#a{z-index:" + parent_z + "}#b{background:yellow}"
                "#front{position:absolute;left:0;top:10px;width:150px;height:20px;background:blue;z-index:" + child_z + "}"
                "#back{position:absolute;left:0;top:10px;width:80px;height:20px;background:lime;z-index:1}"
                "#wrapper{position:relative;height:40px}"));
            const std::string front = "<div id=front></div>";
            CHECK(f.layout("<table id=t><tr><td id=a>" +
                (wrapper ? "<div id=wrapper>" + front + "</div>" : front) +
                "</td><td id=b><div id=back></div></td></tr></table>",240,100));
            SoftwareRenderer renderer(240,100);
            PaintContext paint; paint.styles=&f.styles; paint.backend=&renderer;
            paint_tree(f.tree,f.root,f.ctx,paint);
            const bool behind = parent_z == "-1" || (parent_z == "auto" && child_z == "-1");
            const bool above = parent_z == "2" || (parent_z == "auto" && child_z == "3");
            for (int x : {98,102,120}) {
                const auto pixel = renderer.pixel(x,24);
                const bool blue = !behind && (above || x < 120);
                const bool red = behind && x < 120;
                CHECK(near(pixel.r,red ? 1 : 0));
                CHECK(near(pixel.g,!blue && !red ? 1 : 0));
                CHECK(near(pixel.b,blue ? 1 : 0));
                CHECK(near(pixel.a,1));
                const char* expected = above ? "front" : x == 120 ? "back" : x == 102 ? "b" :
                    parent_z == "-1" ? "t" : behind ? "a" : "front";
                const auto* hit=element_at_point(f.tree,f.root,x,24,&f.ctx);
                CHECK(hit && hit->get_attribute("id") == expected);
            }
        }
    }
}

void test_table_opacity_stacking() {
    for (const char* opacity : {"1","1.0","100%","1e0","+1","2"}) for (bool wrapper : {false,true}) {
        Fixture f;
        CHECK(f.css(std::string("html,body{margin:0;padding:0}table{position:relative;width:200px;table-layout:fixed;border-collapse:collapse}") +
            "td{padding:0;border:8px solid red;height:40px;position:relative}#a{opacity:" + opacity + "}#b{background:yellow}"
            "#front{position:absolute;left:0;top:10px;width:150px;height:20px;background:blue;z-index:3}"
            "#back{position:absolute;left:0;top:10px;width:80px;height:20px;background:lime;z-index:1}"
            "#wrapper{position:relative;height:40px}"));
        const std::string front="<div id=front></div>";
        CHECK(f.layout("<table id=t><tr><td id=a>" + (wrapper ? "<div id=wrapper>"+front+"</div>" : front) +
            "</td><td id=b><div id=back></div></td></tr></table>",240,100));
        SoftwareRenderer renderer(240,100); PaintContext paint; paint.styles=&f.styles;paint.backend=&renderer;
        paint_tree(f.tree,f.root,f.ctx,paint);
        CHECK(!table_positioned_isolation(f.box("a")));
        for (int x : {98,102,120}) {
            const auto pixel=renderer.pixel(x,24);
            CHECK(near(pixel.r,0) && near(pixel.g,0) && near(pixel.b,1) && near(pixel.a,1));
            const auto* hit=element_at_point(f.tree,f.root,x,24,&f.ctx);
            CHECK(hit && hit->get_attribute("id")=="front");
        }
    }
    ComputedStyle style;
    Box box; box.style=&style; box.position=PositionType::Relative;
    for (const char* value : {".5","50%","5e-1"}) {
        style.set("opacity",value);
        CHECK(near(resolve_opacity(&style),.5));
        CHECK(table_positioned_isolation(box));
    }
}
