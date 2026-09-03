// CSS Grid Layout L1, the explicit-grid subset. Every case here was graded
// against the C# reference through the corpus first; these exist so a
// regression shows up in a second rather than in a corpus run.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/grid.h"
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

void test_grid_tracks() {
    {
        // Fixed columns, a gap between them, row-major auto-placement.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px;"
                    "     grid-template-columns: 100px 100px; column-gap: 20px }"
                    ".c { height: 30px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=c2 class=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 120) && near(f.box("b").y, 0));
        // The third item wraps to the next row.
        CHECK(near(f.box("c2").x, 0) && near(f.box("c2").y, 30));
    }
    {
        // repeat() expands, and `fr` splits what the fixed tracks leave.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 320px;"
                    "     grid-template-columns: repeat(3, 1fr); column-gap: 10px }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "<div id=c2 class=c></div></div></body>"));
        CHECK(near(f.box("a").width, 100));
        CHECK(near(f.box("b").x, 110));
        CHECK(near(f.box("c2").x, 220));
    }
    {
        // A fixed track and an fr track share the row.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px;"
                    "     grid-template-columns: 240px 1fr }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").width, 240));
        CHECK(near(f.box("b").width, 160));
    }
    {
        // Leftover space stretches an AUTO track: align-content/justify-content
        // default to `normal`, which behaves as stretch for a grid.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px; height: 200px }"
                    "#a { }"));
        CHECK(f.layout("<body><div id=g><div id=a>x</div></div></body>"));
        CHECK(near(f.box("a").width, 400));
        CHECK(near(f.box("a").height, 200));
    }
}

void test_grid_items() {
    {
        // An item in a FIXED track still gets its box model resolved. Skipping
        // that left padding, border and margin at zero, and the item's own
        // children were then placed at its content origin rather than inside
        // its padding.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 400px; grid-template-columns: 200px 1fr }"
                    "#a { padding: 16px }"
                    "#inner { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a><div id=inner></div></div></div></body>"));
        CHECK(near(f.box("a").width, 200));
        CHECK(near(f.box("inner").x, 16));
        CHECK(near(f.box("inner").y, 16));
    }
    {
        // A scroll container's automatic minimum size is zero, so it does not
        // force its row to grow to its content — it scrolls inside a row the
        // container's own height decides.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px; height: 100px }"
                    "#a { overflow-y: auto }"
                    ".tall { height: 300px }"));
        CHECK(f.layout("<body><div id=g><div id=a><div class=tall></div></div></div></body>"));
        CHECK(near(f.box("a").height, 100));
    }
    {
        // An out-of-flow child is not a grid item and takes no cell.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px; position: relative;"
                    "     grid-template-columns: 100px 100px }"
                    ".c { height: 10px }"
                    "#z { position: absolute; top: 0; left: 0; width: 5px; height: 5px }"));
        CHECK(f.layout("<body><div id=g><div id=z></div><div id=a class=c></div>"
                       "<div id=b class=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, 100));
    }
}

void test_grid_areas() {
    // grid-template-areas names cells; an item claims the rectangle its area
    // covers, and spanning areas take every track they touch.
    Fixture f;
    CHECK(f.css("#g { display: grid; width: 300px;"
                "     grid-template-columns: 100px 100px 100px;"
                "     grid-template-rows: 40px 60px;"
                "     grid-template-areas: \"top top top\" \"a b b\" }"
                "#t { grid-area: top }"
                "#p { grid-area: a }"
                "#q { grid-area: b }"));
    CHECK(f.layout("<body><div id=g><div id=t></div><div id=p></div>"
                   "<div id=q></div></div></body>"));
    CHECK(near(f.box("t").x, 0) && near(f.box("t").y, 0));
    CHECK(near(f.box("t").width, 300) && near(f.box("t").height, 40));
    CHECK(near(f.box("p").x, 0) && near(f.box("p").y, 40));
    CHECK(near(f.box("p").width, 100));
    // `b` spans two columns.
    CHECK(near(f.box("q").x, 100) && near(f.box("q").y, 40));
    CHECK(near(f.box("q").width, 200) && near(f.box("q").height, 60));
}

void test_grid_line_placement() {
    // CSS Grid L1 §8.3 line-based placement and §8.5 sparse auto-placement.
    // Before this, grid-column / grid-row were never read and a page shell's
    // `grid-column: 3` sidebar landed in column 1.
    {
        // `grid-column: 3` locks the column; `1 / 3` spans two; `span 2` is
        // auto-placed at that width. The cursor rules decide the rows: b's
        // start (line 1) is before the cursor a left at line 3, so it drops a
        // row; c does not fit beside b and wraps to an implicit third row.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px;"
                    "     grid-template-columns: 100px 100px 100px;"
                    "     grid-template-rows: 40px 40px }"
                    "#a { grid-column: 3 }"
                    "#b { grid-column: 1 / 3 }"
                    "#c { grid-column: span 2 }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div>"
                       "<div id=c></div></div></body>"));
        CHECK(near(f.box("a").x, 200) && near(f.box("a").y, 0));
        CHECK(near(f.box("a").width, 100));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 40));
        CHECK(near(f.box("b").width, 200));
        CHECK(near(f.box("c").x, 0) && near(f.box("c").y, 80));
        CHECK(near(f.box("c").width, 200));
    }
    {
        // Negative lines count from the end of the explicit grid; the
        // longhands and the four-part grid-area resolve the same way.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px;"
                    "     grid-template-columns: 100px 100px 100px;"
                    "     grid-template-rows: 40px 40px }"
                    "#a { grid-column: -2 / -1 }"
                    "#b { grid-column-start: 2; grid-row-start: 2 }"
                    "#c { grid-area: 1 / 1 / 3 / 2 }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div>"
                       "<div id=c></div></div></body>"));
        CHECK(near(f.box("a").x, 200) && near(f.box("a").y, 0));
        CHECK(near(f.box("a").width, 100));
        CHECK(near(f.box("b").x, 100) && near(f.box("b").y, 40));
        CHECK(near(f.box("c").x, 0) && near(f.box("c").y, 0));
        CHECK(near(f.box("c").height, 80));
    }
    {
        // A row-locked item takes the first free column in its row past what
        // the step already put there; running past the explicit columns adds
        // an implicit auto track rather than wrapping.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px;"
                    "     grid-template-columns: 100px 100px; grid-template-rows: 40px }"
                    "#a { grid-row: 1 } #b { grid-row: 1 } #c { grid-row: 1 }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div>"
                       "<div id=c></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 100) && near(f.box("b").y, 0));
        CHECK(near(f.box("c").x, 200) && near(f.box("c").y, 0));
    }
    {
        // A spanning auto item wraps when the remaining columns cannot hold
        // it, and the next item fills in after it.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px;"
                    "     grid-template-columns: 100px 100px 100px }"
                    ".c { height: 10px } #b { grid-column: span 2 }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=a2 class=c></div>"
                       "<div id=b class=c></div><div id=d class=c></div></div></body>"));
        CHECK(near(f.box("a2").x, 100) && near(f.box("a2").y, 0));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 10));
        CHECK(near(f.box("b").width, 200));
        CHECK(near(f.box("d").x, 200) && near(f.box("d").y, 10));
    }
    {
        // A named area still wins, and a name that is not in the template
        // falls back to auto-placement rather than vanishing.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px;"
                    "     grid-template-columns: 100px 100px;"
                    "     grid-template-areas: \"x y\" }"
                    "#a { grid-area: y } #b { grid-area: nope }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("a").x, 100));
        CHECK(near(f.box("b").x, 0));
    }
}

void test_grid_alignment() {
    // CSS Box Alignment in a grid container: *-items / *-self place an item
    // within its area, *-content places the tracks within the container.
    {
        // The modal idiom: place-items: center on a full-size grid centres a
        // fixed-size child. Before this the child was stretched to the cell
        // (an explicit width was overwritten) and sat at the origin.
        Fixture f;
        CHECK(f.css("#o { display: grid; place-items: center; width: 800px; height: 600px }"
                    "#m { width: 200px; height: 100px }"));
        CHECK(f.layout("<body><div id=o><div id=m></div></div></body>"));
        CHECK(near(f.box("m").width, 200) && near(f.box("m").height, 100));
        CHECK(near(f.box("m").x, 300) && near(f.box("m").y, 250));
    }
    {
        // Self overrides items; end and center in either axis.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 100px; grid-template-rows: 100px;"
                    "     justify-items: start; align-items: start }"
                    "#a { width: 50px; height: 20px; justify-self: end; align-self: center }"));
        CHECK(f.layout("<body><div id=g><div id=a></div></div></body>"));
        CHECK(near(f.box("a").x, 50) && near(f.box("a").y, 40));
    }
    {
        // Default stretch keeps an explicit width rather than widening it,
        // and an auto width under `start` fits its content.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 100px 100px;"
                    "     grid-template-rows: 30px; justify-items: start }"
                    "#a { width: 50px }"
                    "#b { justify-self: stretch }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b>ab</div></div></body>"));
        CHECK(near(f.box("a").width, 50) && near(f.box("a").x, 0));
        CHECK(near(f.box("a").height, 30));
        CHECK(near(f.box("b").width, 100));
        Fixture g;
        CHECK(g.css("#g { display: grid; grid-template-columns: 100px; justify-items: start }"));
        CHECK(g.layout("<body><div id=g><div id=b>ab</div></div></body>"));
        CHECK(g.box("b").width > 0 && g.box("b").width < 100);
    }
    {
        // align-content: space-between against a min-height the rows do not
        // reach — the clamped height is definite and the rows spread over it.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: repeat(2, 60px);"
                    "     grid-template-rows: repeat(2, 40px); align-content: space-between;"
                    "     min-height: 150px }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div>"
                       "<div id=c></div><div id=d></div></div></body>"));
        CHECK(near(f.box("g").height, 150));
        CHECK(near(f.box("a").y, 0));
        CHECK(near(f.box("c").y, 110) && near(f.box("d").y, 110));
    }
    {
        // justify-content: center on fixed columns narrower than the container.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px; grid-template-columns: 100px 100px;"
                    "     justify-content: center }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "</div></body>"));
        CHECK(near(f.box("a").x, 50) && near(f.box("b").x, 150));
    }
    {
        // With free space and no auto tracks, `normal` behaves as start: the
        // pre-existing case, unchanged.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px; grid-template-columns: 100px 100px }"
                    ".c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "</div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("b").x, 100));
    }
}

void test_grid_auto_track_contributions() {
    {
        // `justify-content: start` keeps auto columns at their content size
        // instead of stretching them over the free space, and an item's
        // min-width is part of that content size.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: auto auto; gap: 10px;"
                    "     justify-content: start; width: 800px }"
                    ".c { min-width: 200px; display: flex }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c>x</div><div id=b class=c>y</div>"
                       "</div></body>"));
        CHECK(near(f.box("a").width, 200) && near(f.box("a").x, 0));
        CHECK(near(f.box("b").width, 200) && near(f.box("b").x, 210));
        // ...while the default `normal` still stretches them.
        Fixture g;
        CHECK(g.css("#g { display: grid; grid-template-columns: auto auto; gap: 10px;"
                    "     width: 810px }"
                    ".c { min-width: 200px }"));
        CHECK(g.layout("<body><div id=g><div id=a class=c>x</div><div id=b class=c>y</div>"
                       "</div></body>"));
        CHECK(near(g.box("a").width, 400) && near(g.box("b").x, 410));
    }
    {
        // An overflow:hidden item sizes an auto row in an auto-height grid —
        // the grid is sized under a max-content constraint, so the row grows
        // to the item's contribution — and a centred sibling centres in it.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 200px 1fr; align-items: center;"
                    "     width: 800px }"
                    "#ctrl { overflow: hidden; height: 28px }"
                    "#lab { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=lab></div><div id=ctrl></div></div></body>"));
        CHECK(near(f.box("g").height, 28));
        CHECK(near(f.box("lab").y, 9));
        CHECK(near(f.box("ctrl").y, 0));
    }
    {
        // In a DEFINITE-height grid the scroll container's automatic minimum
        // is zero: the row takes the container's height, not the item's.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-rows: auto; height: 100px; width: 200px }"
                    "#s { overflow: auto; height: 300px }"));
        CHECK(f.layout("<body><div id=g><div id=s></div></div></body>"));
        CHECK(near(f.box("g").height, 100));
        CHECK(near(f.box("s").y, 0));
    }
}

void test_grid_item_relaid_in_a_second_pass_drops_the_imposed_height() {
    // A grid inside a column flex is laid out more than once. The imposed
    // height a grid area stamped in the first pass must not survive into the
    // second, or a square item keeps its provisional height.
    Fixture f;
    CHECK(f.css("#col { display: flex; flex-direction: column; width: 400px }"
                "#g { display: grid; grid-template-columns: 80px 80px; gap: 10px }"
                ".f { aspect-ratio: 1 / 1; display: flex; align-items: center;"
                "     justify-content: center }"
                ".i { width: 10px; height: 10px }"));
    CHECK(f.layout("<body><div id=col><div id=g><div id=a class=f><div class=i></div></div>"
                   "<div id=b class=f><div class=i></div></div></div></div></body>"));
    CHECK(near(f.box("a").width, 80) && near(f.box("a").height, 80));
    CHECK(near(f.box("b").height, 80));
    CHECK(near(f.box("g").height, 80));
}

void test_grid_aspect_ratio_item_stretches_one_axis() {
    {
        // Auto row: the inline axis is stretched to the column and the height
        // follows from the ratio — the square is not stretched to the text
        // beside it. Chrome: 80 tall.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 80px 1fr; gap: 12px; width: 600px }"
                    "#sq { aspect-ratio: 1 / 1; display: flex; align-items: center;"
                    "      justify-content: center }"
                    "#body { height: 200px }"));
        CHECK(f.layout("<body><div id=g><div id=sq></div><div id=body></div></div></body>"));
        CHECK(near(f.box("sq").width, 80));
        CHECK(near(f.box("sq").height, 80));
        CHECK(near(f.box("g").height, 200));
    }
    {
        // Definite rows: the block axis is stretched to the row and the width
        // follows — 196 squares. That transferred size is then the items'
        // min-content contribution, so the `1fr` columns re-resolve to 196
        // and overflow the 500px grid (§12.1 step 3; Chrome puts the second
        // square at 204). The reference keeps the 119px columns.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: repeat(4, 1fr);"
                    "     grid-template-rows: 1fr 1fr; gap: 8px; width: 500px; height: 400px }"
                    ".c { aspect-ratio: 1 / 1 }"));
        CHECK(f.layout("<body><div id=g><div id=a class=c></div><div id=b class=c></div>"
                       "<div class=c></div><div class=c></div><div class=c></div>"
                       "<div class=c></div><div class=c></div><div class=c></div></div></body>"));
        CHECK(near(f.box("a").height, 196));
        CHECK(near(f.box("a").width, 196));
        CHECK(near(f.box("b").x, 196 + 8));
    }
}

void test_grid_track_sizing_functions() {
    {
        // minmax(80px, 1fr) minmax(80px, 2fr) minmax(80px, 1fr): the fr shares
        // 1:2:1 once every track's 80px minimum is met — 146/292/146 in 600px
        // with 8px gaps (Chrome and the reference).
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 600px; gap: 8px;"
                    "     grid-template-columns: minmax(80px, 1fr) minmax(80px, 2fr) minmax(80px, 1fr) }"
                    ".i { padding: 12px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i>A</div><div id=b class=i>B</div>"
                       "<div id=c class=i>C</div></div></body>"));
        CHECK(near(f.box("a").width, 146));
        CHECK(near(f.box("b").x, 154) && near(f.box("b").width, 292));
        CHECK(near(f.box("c").x, 454));
    }
    {
        // A fixed minimum wins over the fr share when the share is smaller.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 300px; grid-template-columns: minmax(200px, 1fr) 1fr }"
                    ".i { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div></div></body>"));
        CHECK(near(f.box("a").width, 200));
        CHECK(near(f.box("b").width, 100));
    }
    {
        // minmax(100px, 200px): the track grows from its base toward its limit
        // with the free space and stops there; the rest goes to the auto track.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 600px; grid-template-columns: minmax(100px, 200px) auto }"
                    ".i { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div></div></body>"));
        CHECK(near(f.box("a").width, 200));
        CHECK(near(f.box("b").width, 400));
    }
    {
        // repeat(auto-fill, minmax(140px, 1fr)) in 600px with 10px gaps: four
        // columns (4*140 + 3*10 = 590), each grown to (600 - 30) / 4.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 600px; gap: 10px;"
                    "     grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)) }"
                    ".i { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div>"
                       "<div class=i></div><div class=i></div><div id=e class=i></div></div></body>"));
        CHECK(near(f.box("a").width, 142.5));
        CHECK(near(f.box("b").x, 152.5));
        CHECK(near(f.box("e").y, 20));
        CHECK(near(f.box("g").height, 30));
    }
    {
        // auto-fit collapses the empty columns: two items in a 600px grid that
        // fits four get 295 each, not 142.5.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 600px; gap: 10px;"
                    "     grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)) }"
                    ".i { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div></div></body>"));
        CHECK(near(f.box("a").width, 295));
        CHECK(near(f.box("b").x, 305));
    }
    {
        // grid-auto-rows sizes the implicit rows; an indefinite-height grid
        // with `grid-auto-rows: 1fr` gives every row the tallest content.
        Fixture f;
        CHECK(f.css("#g { display: grid; width: 200px; grid-template-columns: 1fr 1fr;"
                    "     grid-auto-rows: 1fr }"
                    "#a { height: 30px } #b { height: 50px } #c { height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=a></div><div id=b></div><div id=c></div></div></body>"));
        CHECK(near(f.box("c").y, 50));
        CHECK(near(f.box("g").height, 100));
        Fixture h;
        CHECK(h.css("#g { display: grid; width: 200px; grid-template-columns: 1fr;"
                    "     grid-auto-rows: 40px 60px }"
                    ".i { height: 5px }"));
        CHECK(h.layout("<body><div id=g><div class=i></div><div class=i></div><div id=c class=i></div>"
                       "</div></body>"));
        CHECK(near(h.box("c").y, 100));
    }
    {
        // An item spanning two auto columns spreads its width over them.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: auto auto; justify-content: start;"
                    "     width: 600px }"
                    "#w { grid-column: 1 / 3; width: 100px; height: 10px }"
                    "#a { width: 20px; height: 10px } #b { width: 20px; height: 10px }"));
        CHECK(f.layout("<body><div id=g><div id=w></div><div id=a></div><div id=b></div></div></body>"));
        CHECK(near(f.box("b").x, 50));
    }
}

void test_grid_aspect_ratio_item_takes_the_larger_transfer() {
    // With both axes stretched, each axis is the larger of its own stretch
    // and the size transferred from the other's: a square in a 195px column
    // and a 110px row is 195x195 (Chrome), just as one in a 119px column and
    // a 196px row is 196x196.
    Fixture f;
    CHECK(f.css("#g { display: grid; grid-template-columns: repeat(4, 1fr); grid-auto-rows: 110px;"
                "     gap: 16px; width: 828px }"
                ".sq { aspect-ratio: 1 / 1; display: flex }"));
    CHECK(f.layout("<body><div id=g><div id=a class=sq></div><div class=sq></div>"
                   "<div class=sq></div><div class=sq></div></div></body>"));
    CHECK(near(f.box("a").width, 195));
    CHECK(near(f.box("a").height, 195));
}

void test_grid_subgrid() {
    {
        // Columns: the child spans the parent's three columns and lays its
        // cells out on exactly those tracks.
        Fixture f;
        CHECK(f.css("#p { display: grid; grid-template-columns: 100px 200px 150px;"
                    "     grid-template-rows: 30px; width: 600px }"
                    "#c { display: grid; grid-column: 1 / 4; grid-template-columns: subgrid }"
                    ".cell { height: 30px }"));
        CHECK(f.layout("<body><div id=p><div id=c><div id=a class=cell></div>"
                       "<div id=b class=cell></div><div id=d class=cell></div></div></div></body>"));
        CHECK(near(f.box("c").width, 450));
        CHECK(near(f.box("a").width, 100) && near(f.box("b").x, 100));
        CHECK(near(f.box("b").width, 200) && near(f.box("d").x, 300));
        CHECK(near(f.box("d").width, 150));
    }
    {
        // Rows: the child spans two of the parent's rows; items past them go
        // into implicit auto rows of its own.
        Fixture f;
        CHECK(f.css("#p { display: grid; grid-template-columns: 200px;"
                    "     grid-template-rows: 40px 80px 40px 80px; width: 200px; height: 400px }"
                    "#c { display: grid; grid-column: 1 / 2; grid-row: 1 / 3;"
                    "     grid-template-rows: subgrid; grid-auto-rows: subgrid }"
                    ".item { width: 10px }"));
        CHECK(f.layout("<body><div id=p><div id=c><div id=i1 class=item></div>"
                       "<div id=i2 class=item></div><div id=i3 class=item></div>"
                       "<div id=i4 class=item></div></div></div></body>"));
        CHECK(near(f.box("c").height, 120));
        CHECK(near(f.box("i1").height, 40));
        CHECK(near(f.box("i2").y, 40) && near(f.box("i2").height, 80));
        CHECK(near(f.box("i3").y, 120));
    }
    {
        // The parent's gap is the subgrid's gap, and the subgrid's own padding
        // comes off its edge tracks so the cells still line up with the
        // parent's columns.
        Fixture f;
        CHECK(f.css("#p { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; width: 320px }"
                    "#c { display: grid; grid-column: 1 / 3; grid-template-columns: subgrid;"
                    "     padding: 0 10px }"
                    ".cell { height: 30px }"));
        CHECK(f.layout("<body><div id=p><div id=c><div id=a class=cell></div>"
                       "<div id=b class=cell></div></div></div></body>"));
        // Box x is relative to the child's border-box origin: the first cell
        // starts after the 10px padding and is the 158px track less that.
        CHECK(near(f.box("a").x, 10) && near(f.box("a").width, 148));
        CHECK(near(f.box("b").x, 162) && near(f.box("b").width, 148));
    }
}

void test_grid_stretched_rows_feed_back_into_columns() {
    // §12.1 steps 3-4. A `flex: 1` grid in a 300px column flex has a definite
    // height; its two auto rows stretch to 150 each, the aspect-ratio items
    // take 150 from the row, and that transferred size is their new
    // min-content contribution — the two `1fr` columns re-resolve to 150 and
    // overflow the 200px grid, as Chrome lays it out. Without a definite
    // height the columns keep their 100px share and the items are 100 squares.
    Fixture f;
    CHECK(f.css("#col { display: flex; flex-direction: column; height: 300px; width: 200px }"
                "#g { display: grid; grid-template-columns: repeat(2, 1fr); flex: 1 }"
                ".s { aspect-ratio: 1 / 1 }"));
    CHECK(f.layout("<body><div id=col><div id=g><div id=a class=s></div><div id=b class=s></div>"
                   "<div id=c class=s></div><div id=d class=s></div></div></div></body>"));
    CHECK(near(f.box("g").height, 300));
    CHECK(near(f.box("a").width, 150));
    CHECK(near(f.box("a").height, 150));
    CHECK(near(f.box("b").x, 150));
    CHECK(near(f.box("c").y, 150));

    Fixture g;
    CHECK(g.css("#g { display: grid; grid-template-columns: repeat(2, 1fr); width: 200px }"
                ".s { aspect-ratio: 1 / 1 }"));
    CHECK(g.layout("<body><div id=g><div id=a class=s></div><div id=b class=s></div></div></body>"));
    CHECK(near(g.box("a").width, 100));
    CHECK(near(g.box("a").height, 100));
    CHECK(near(g.box("g").height, 100));
}

// `grid-auto-flow` (CSS Grid L1 8.5). The port read neither half of it, so
// `column` laid out in rows -- a toolbar meant to run down the side came out
// across the top -- and `dense` packed sparsely.
void test_grid_auto_flow_column() {
    {
        // Row flow, the default: four items across two columns fill left to
        // right, wrapping to a second row.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 100px 100px;"
                    "     grid-template-rows: 40px 40px; width: 400px }"
                    ".i { height: 40px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div>"
                       "<div id=c class=i></div><div id=d class=i></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 100) && near(f.box("b").y, 0));    // across first
        CHECK(near(f.box("c").x, 0) && near(f.box("c").y, 40));
        CHECK(near(f.box("d").x, 100) && near(f.box("d").y, 40));
    }
    {
        // Column flow: the same four fill top to bottom, wrapping to a second
        // COLUMN. Every position differs from the case above except the first.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-auto-flow: column;"
                    "     grid-template-columns: 100px 100px;"
                    "     grid-template-rows: 40px 40px; width: 400px }"
                    ".i { height: 40px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div>"
                       "<div id=c class=i></div><div id=d class=i></div></div></body>"));
        CHECK(near(f.box("a").x, 0) && near(f.box("a").y, 0));
        CHECK(near(f.box("b").x, 0) && near(f.box("b").y, 40));     // down first
        CHECK(near(f.box("c").x, 100) && near(f.box("c").y, 0));
        CHECK(near(f.box("d").x, 100) && near(f.box("d").y, 40));
    }
    {
        // A single-row template with column flow: items keep adding implicit
        // COLUMNS rather than wrapping, which is the toolbar case.
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-auto-flow: column;"
                    "     grid-template-rows: 40px; width: 400px }"
                    ".i { height: 40px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div>"
                       "<div id=c class=i></div></div></body>"));
        CHECK(near(f.box("a").y, 0) && near(f.box("b").y, 0) && near(f.box("c").y, 0));
        CHECK(f.box("b").x > f.box("a").x);
        CHECK(f.box("c").x > f.box("b").x);
    }
}

// `dense` backfills; the sparse default does not.
void test_grid_auto_flow_dense() {
    // A wide item in the middle leaves a hole on the first row. Sparse leaves
    // it; dense puts the next item that fits into it.
    const char* html = "<body><div id=g><div id=a class=i></div>"
                       "<div id=wide class=i></div><div id=c class=i></div></div></body>";
    {
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-template-columns: 50px 50px 50px; width: 300px }"
                    ".i { height: 20px } #wide { grid-column: span 3 }"));
        CHECK(f.layout(html));
        // `a` at (0,0); `wide` needs three columns so it drops to row 2;
        // `c` follows it on row 3 rather than filling the hole beside `a`.
        CHECK(near(f.box("a").y, 0));
        CHECK(near(f.box("wide").y, 20));
        CHECK(near(f.box("c").y, 40));
    }
    {
        Fixture f;
        CHECK(f.css("#g { display: grid; grid-auto-flow: row dense;"
                    "     grid-template-columns: 50px 50px 50px; width: 300px }"
                    ".i { height: 20px } #wide { grid-column: span 3 }"));
        CHECK(f.layout(html));
        CHECK(near(f.box("a").y, 0));
        CHECK(near(f.box("wide").y, 20));
        // Dense looks back: `c` fills the hole on the first row.
        CHECK(near(f.box("c").y, 0));
        CHECK(near(f.box("c").x, 50));
    }
}
