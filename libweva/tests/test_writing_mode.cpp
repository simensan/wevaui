// CSS Writing Modes L3: `writing-mode: vertical-rl` and `vertical-lr` as
// orthogonal flow roots inside a horizontal document.
//
// Geometry is asserted in physical coordinates with the mono metrics: 16px
// font, 8px per character, 19.2px line height.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/positioning.h"
#include "weva/user_agent_stylesheet.h"
#include "weva/writing_mode.h"

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
    // The first text run under a box, in tree order.
    BoxId first_run(BoxId from) const {
        if (from == kNoBox) return kNoBox;
        if (tree[from].kind == BoxKind::Text) return from;
        for (BoxId c : tree.children(from)) {
            const BoxId hit = first_run(c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
};

constexpr double kLine = 19.2;   // 16px * 1.2

} // namespace

void test_writing_mode() {
    // vertical-rl: the block axis runs right to left, so the paragraph is a
    // column against the section's right edge; its inline size is the text
    // advance, its block size the line height.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;width:100px}p{margin:0}"));
        CHECK(f.layout("<div id=v><p id=p>abcdefghij</p></div>"));
        CHECK(near(f.box("v").x, 0) && near(f.box("v").y, 0));
        CHECK(near(f.box("v").width, 100));
        CHECK(near(f.box("v").height, 80));
        CHECK(near(f.box("p").x, 100 - kLine));
        CHECK(near(f.box("p").y, 0));
        CHECK(near(f.box("p").width, kLine));
        CHECK(near(f.box("p").height, 80));
        CHECK(f.box("v").orthogonal_root);
        CHECK(f.box("p").vertical_text == static_cast<uint8_t>(VerticalMode::RL));
        // Layout hands the ORIGINAL styles back: paint reads physical sides.
        CHECK(vertical_mode_of(f.box("v").style) == VerticalMode::RL);
        CHECK(vertical_mode_of(f.box("p").style) == VerticalMode::RL);
        const BoxId run = f.first_run(f.find("p"));
        CHECK(run != kNoBox);
        if (run != kNoBox) {
            CHECK(f.tree[run].vertical_text == static_cast<uint8_t>(VerticalMode::RL));
            CHECK(near(f.tree[run].height, 80));
        }
    }
    // vertical-lr: the same column sits against the left edge.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-lr;width:100px}p{margin:0}"));
        CHECK(f.layout("<div id=v><p id=p>abcdefghij</p></div>"));
        CHECK(near(f.box("v").height, 80));
        CHECK(near(f.box("p").x, 0));
        CHECK(near(f.box("p").width, kLine));
        CHECK(near(f.box("p").height, 80));
        CHECK(f.box("p").vertical_text == static_cast<uint8_t>(VerticalMode::LR));
    }
    // Two paragraphs stack along the block axis: leftwards under rl,
    // rightwards under lr. The section's block size is the sum.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl}p{margin:0}"));
        CHECK(f.layout("<div id=v><p id=a>abcdefghij</p><p id=b>abcde</p></div>"));
        CHECK(near(f.box("v").width, 2 * kLine));
        CHECK(near(f.box("v").height, 80));
        CHECK(near(f.box("a").x, kLine));
        CHECK(near(f.box("b").x, 0));
        // A block-level child's auto inline size fills the container, so the
        // shorter paragraph is as tall as the longer one.
        CHECK(near(f.box("b").height, 80));
    }
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-lr}p{margin:0}"));
        CHECK(f.layout("<div id=v><p id=a>abcdefghij</p><p id=b>abcde</p></div>"));
        CHECK(near(f.box("v").width, 2 * kLine));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, kLine));
    }
    // The user-agent paragraph margins are flow-relative: 1em on the
    // block-start and block-end sides, which are the right and left here,
    // and nothing above or below.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;width:100px}"));
        CHECK(f.layout("<div id=v><p id=p>abcdefghij</p></div>"));
        CHECK(near(f.box("p").x, 100 - 16 - kLine));
        CHECK(near(f.box("p").y, 0));
        CHECK(near(f.box("p").margin_right, 16));
        CHECK(near(f.box("p").margin_left, 16));
        CHECK(near(f.box("p").margin_top, 0));
        CHECK(near(f.box("v").height, 80));
    }
    // Physical padding and border on the vertical box stay where the author
    // put them; the content starts inside them on every side.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;width:100px;padding:1px 2px 3px 4px;border:5px solid}p{margin:0}"));
        CHECK(f.layout("<div id=v><p id=p>abcdefghij</p></div>"));
        const Box& v = f.box("v");
        CHECK(near(v.width, 100 + 2 + 4 + 10));
        CHECK(near(v.height, 80 + 1 + 3 + 10));
        CHECK(near(v.padding_right, 2) && near(v.padding_left, 4));
        CHECK(near(v.padding_top, 1) && near(v.padding_bottom, 3));
        CHECK(near(v.border_right, 5) && near(v.border_top, 5));
        CHECK(near(f.box("p").x, v.width - 5 - 2 - kLine));
        CHECK(near(f.box("p").y, 5 + 1));
    }
    // The inline size of an orthogonal root fits its content against the
    // block size available to it (§7.3.2): here the viewport height, since
    // the body is 100% tall. 20 words of 40px want 792px and get 600, so
    // the paragraph wraps to two lines.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl}p{margin:0}"));
        std::string words;
        for (int i = 0; i < 20; ++i) words += "abcd ";
        CHECK(f.layout("<div id=v><p id=p>" + words + "</p></div>", 1000, 600));
        CHECK(near(f.box("v").height, 600));
        CHECK(near(f.box("p").height, 600));
        CHECK(near(f.box("p").width, 2 * kLine));
        CHECK(near(f.box("v").width, 2 * kLine));
    }
    // An explicit height is the inline size; the block size is the content.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;height:200px}"));
        CHECK(f.layout("<div id=v>abcdefghij</div>"));
        CHECK(near(f.box("v").height, 200));
        CHECK(near(f.box("v").width, kLine));
    }
    // A flex container inside the vertical flow: its row runs along the
    // inline axis, top to bottom, and the items stretch across the block
    // axis. Physical padding on a chip is physical.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;width:100px}.s{display:flex;gap:6px}.c{padding:2px 6px;border:1px solid}"));
        CHECK(f.layout("<div id=v><div id=s class=s><div id=a class=c>a</div><div id=b class=c>b</div></div></div>"));
        const double chip_w = kLine + 12 + 2;
        const double chip_h = 8 + 4 + 2;
        CHECK(near(f.box("a").width, chip_w));
        CHECK(near(f.box("a").height, chip_h));
        CHECK(near(f.box("a").y, 0));
        CHECK(near(f.box("b").y, chip_h + 6));
        // Positions are parent-relative: the stack sits against the section's
        // right edge and the chips fill the stack's width.
        CHECK(near(f.box("s").x, 100 - chip_w));
        CHECK(near(f.box("a").x, 0));
        CHECK(near(f.box("b").x, f.box("a").x));
        CHECK(near(f.box("s").width, chip_w));
        CHECK(near(f.box("s").height, 2 * chip_h + 6));
        CHECK(near(f.box("v").height, 2 * chip_h + 6));
    }
    // A vertical inline-block in a horizontal line contributes its block
    // size as width and its text advance as height.
    {
        Fixture f;
        CHECK(f.css("#v{display:inline-block;writing-mode:vertical-rl}"));
        CHECK(f.layout("<div id=h><div id=v>abcdefghij</div>after</div>"));
        CHECK(near(f.box("v").width, kLine));
        CHECK(near(f.box("v").height, 80));
        CHECK(near(f.box("h").height, 80));
    }
    // A vertical flex item: the horizontal row measures it by its block
    // size and stretches its physical height to the line.
    {
        Fixture f;
        CHECK(f.css("#h{display:flex;height:150px}#v{writing-mode:vertical-rl}#n{width:50px}"));
        CHECK(f.layout("<div id=h><div id=v>abcdefghij</div><div id=n></div></div>"));
        CHECK(near(f.box("v").width, kLine));
        CHECK(near(f.box("v").height, 150));
        CHECK(near(f.box("n").x, kLine));
    }
    // Insets on a positioned child are physical, whatever the writing mode
    // of its containing block.
    {
        Fixture f;
        CHECK(f.css("#v{position:relative;writing-mode:vertical-rl;width:100px;height:100px}#a{position:absolute;top:5px;left:7px;width:10px;height:10px}"));
        CHECK(f.layout("<div id=v>text<div id=a></div></div>"));
        CHECK(near(f.box("a").x, 7));
        CHECK(near(f.box("a").y, 5));
        CHECK(near(f.box("a").width, 10) && near(f.box("a").height, 10));
    }
    // The horizontal flow around the vertical box goes on below it, and the
    // box's own physical margins collapse with its siblings' as any block's.
    {
        Fixture f;
        CHECK(f.css("#v{writing-mode:vertical-rl;width:100px;margin-bottom:10px}#n{margin-top:4px;height:10px}"));
        CHECK(f.layout("<div id=v>abcdefghij</div><div id=n></div>"));
        CHECK(near(f.box("v").height, 80));
        CHECK(near(f.box("n").y, 80 + 10));
    }
}
