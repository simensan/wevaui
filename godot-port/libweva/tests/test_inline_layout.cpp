#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/inline_layout.h"
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
    MonoFontMetrics metrics;   // 0.5em per char, 1.2em line, 0.8/0.4 asc/desc
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
    std::vector<BoxId> lines(std::string_view id) const {
        std::vector<BoxId> out;
        for (BoxId c : tree.children(find(id))) {
            if (tree[c].kind == BoxKind::Line) out.push_back(c);
        }
        return out;
    }
    // The text of one line, with runs joined by nothing (a collapsed space is
    // its own run, so the join is faithful).
    std::string line_text(BoxId line) const {
        std::string s;
        for (BoxId c : tree.children(line)) s += std::string(tree[c].text);
        return s;
    }
};

bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

} // namespace

void test_font_metrics() {
    MonoFontMetrics m;
    // The parameterless shape is what the reference's own arithmetic is pinned
    // against: 5 chars at 16px is 40px.
    CHECK(near(m.measure("hello", 16), 40));
    CHECK(near(m.line_height(16), 19.2));
    CHECK(near(m.ascent(16), 12.8));
    CHECK(near(m.descent(16), 6.4));
    CHECK(near(m.measure("", 16), 0));

    // UTF-8 is decoded per code point, not per byte: an accented letter is one
    // glyph, and an emoji is one WIDE glyph. Charging bytes would overstate the
    // first and understate the second.
    CHECK(near(m.measure("é", 16), 8));
    CHECK(near(m.measure("⚡", 16), 16 * 1.3));
    CHECK(near(m.measure("a⚡a", 16), 8 + 20.8 + 8));
    // A Dingbat is medium-width, not wide.
    CHECK(near(m.measure("✓", 16), 16.0));
    // A neighbouring text-presented symbol keeps the Latin advance.
    CHECK(near(m.measure("⌂", 16), 8));

    MonoFontMetrics chrome = MonoFontMetrics::chrome_sans_serif();
    CHECK(near(chrome.measure("hello", 16), 5 * 0.45 * 16));
    CHECK(near(chrome.line_height(16), 16 * 1.143));
}

void test_inline_item_collection() {
    Fixture f;
    CHECK(f.css("#w { display: block; font-size: 16px }"
                "#big { font-size: 32px }"));
    CHECK(f.layout("<body><div id=w>one <span id=big>two</span> three</div></body>"));

    // The inline box tree is flattened, but each item still knows which inline
    // box it came from — line breaking works on a flat sequence because a break
    // can fall anywhere in it.
    BoxTree t2;
    // Re-collect from a freshly built tree, since layout replaced the children
    // with line boxes.
    Fixture g;
    CHECK(g.css("#w { display: block; font-size: 16px } #big { font-size: 32px }"));
    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    g.doc = parse_html("<body><div id=w>one <span id=big>two</span> three</div></body>",
                       &g.symbols, o, &he);
    for (const Ref<Node>& c : g.doc->children()) {
        if (c->node_type() == NodeType::Element) {
            g.styles.compute_tree(static_cast<const Element&>(*c), nullptr);
        }
    }
    BoxBuilder builder(&g.tree, &g.styles);
    g.root = builder.build_document(*g.doc);
    const BoxId w = g.find("w");
    const std::vector<InlineItem> items = collect_inline_items(g.tree, w, g.ctx);
    CHECK(items.size() == 3);
    CHECK(items[0].text == "one ");
    CHECK(items[1].text == "two");
    CHECK(items[2].text == " three");
    // The middle item is inside the span, the outer two are not.
    CHECK(items[0].inline_parent == kNoBox);
    CHECK(items[1].inline_parent != kNoBox);
    CHECK(near(items[1].font_size, 32));
    CHECK(near(items[0].font_size, 16));
}

void test_line_breaking() {
    {
        // Text that fits stays on one line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>hello world</div></body>"));
        CHECK(f.lines("w").size() == 1);
        CHECK_EQ(f.line_text(f.lines("w")[0]), "hello world");
        // 11 chars at 8px each.
        CHECK(near(f.box("w").height, 19.2));
    }
    {
        // A word that would overflow starts a new line instead.
        // "hello" and "world" are 40px each, the space 8px: 88px total, so a
        // 60px box breaks between them.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>hello world</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        CHECK_EQ(f.line_text(ls[0]), "hello");
        CHECK_EQ(f.line_text(ls[1]), "world");
        // The trailing space is trimmed off line one rather than left hanging.
        CHECK(near(f.tree[ls[0]].y, 0));
        CHECK(near(f.tree[ls[1]].y, 19.2));
        CHECK(near(f.box("w").height, 38.4));
    }
    {
        // A single word wider than the line overflows rather than looping or
        // being split — breaking inside a word needs overflow-wrap.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>hello</div></body>"));
        CHECK(f.lines("w").size() == 1);
        CHECK_EQ(f.line_text(f.lines("w")[0]), "hello");
    }
    {
        // `white-space: nowrap` forbids the break entirely.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 16px;"
                    "     white-space: nowrap }"));
        CHECK(f.layout("<body><div id=w>hello world</div></body>"));
        CHECK(f.lines("w").size() == 1);
    }
}

void test_whitespace_collapsing() {
    {
        // Runs of whitespace collapse to one space, and a leading space on a
        // line is dropped — otherwise every wrapped line is indented.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>   a   b   </div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        CHECK_EQ(f.line_text(ls[0]), "a b");
    }
    {
        // Newlines and tabs are collapsible whitespace like spaces.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>a\n\t b</div></body>"));
        CHECK_EQ(f.line_text(f.lines("w")[0]), "a b");
    }
    {
        // Text spanning inline boxes still collapses across the boundary: the
        // space after "one" and the one before "three" survive as single
        // spaces, and nothing doubles up.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>one <span>two</span> three</div></body>"));
        CHECK_EQ(f.line_text(f.lines("w")[0]), "one two three");
    }
}

void test_line_metrics_and_align() {
    {
        // The line's height is the tallest content on it and its baseline the
        // deepest ascent, so a bigger span pushes the line down rather than
        // overlapping the one above. Runs sit on the SHARED baseline.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#big { font-size: 32px }"));
        CHECK(f.layout("<body><div id=w>a<span id=big>B</span></div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        // ascent 25.6 + descent 12.8 = 38.4 content; leading max(19.2, 38.4).
        CHECK(near(f.tree[ls[0]].height, 38.4));
        CHECK(near(f.tree[ls[0]].baseline, 25.6));
        const std::vector<BoxId> runs = {f.tree.child_at(ls[0], 0), f.tree.child_at(ls[0], 1)};
        // The small run sits lower so its baseline lines up with the big one.
        CHECK(near(f.tree[runs[0]].y, 25.6 - 12.8));
        CHECK(near(f.tree[runs[1]].y, 25.6 - 25.6));
    }
    {
        // A line-height larger than the text splits the extra evenly above and
        // below, which is what keeps the text centred in its line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 40px }"));
        CHECK(f.layout("<body><div id=w>a</div></body>"));
        const BoxId l = f.lines("w")[0];
        CHECK(near(f.tree[l].height, 40));
        // content 19.2, half-leading (40-19.2)/2 = 10.4, baseline 10.4 + 12.8.
        CHECK(near(f.tree[l].baseline, 23.2));
    }
    {
        // text-align shifts every run on the line by the same delta, recorded
        // on the line so a later pass can undo it rather than stacking shifts.
        Fixture f;
        CHECK(f.css("#l, #c, #r { display: block; width: 100px; font-size: 16px }"
                    "#c { text-align: center } #r { text-align: right }"));
        CHECK(f.layout("<body><div id=l>ab</div><div id=c>ab</div>"
                       "<div id=r>ab</div></body>"));
        CHECK(near(f.tree[f.tree.child_at(f.lines("l")[0], 0)].x, 0));
        CHECK(near(f.tree[f.tree.child_at(f.lines("c")[0], 0)].x, (100 - 16) * 0.5));
        CHECK(near(f.tree[f.tree.child_at(f.lines("r")[0], 0)].x, 100 - 16));
        CHECK(near(f.tree[f.lines("r")[0]].applied_text_align_delta, 84));
    }
    {
        // `start` and `end` resolve against the direction, so layout only ever
        // sees left/right/center.
        CHECK(resolve_text_align(nullptr) == "left");
    }
}

void test_shrink_to_fit() {
    {
        // CSS 2.1 §10.3.5: min(max-content, max(min-content, available)).
        // "hello world" is 88px at 8px/char; the float hugs it instead of
        // filling the 1000px line.
        Fixture f;
        CHECK(f.css("#w { display: block; font-size: 16px }"
                    "#fl { float: left }"));
        CHECK(f.layout("<body><div id=w><div id=fl>hello world</div></div></body>"));
        CHECK(near(f.box("fl").width, 88));
    }
    {
        // When max-content exceeds the available width the float takes the
        // available width and wraps inside it.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 16px }"
                    "#fl { float: left }"));
        CHECK(f.layout("<body><div id=w><div id=fl>hello world</div></div></body>"));
        CHECK(near(f.box("fl").width, 60));
    }
    {
        // CANDIDATE DIVERGENCE. CSS 2.1 §10.3.5 computes
        // min(preferred, max(preferred-minimum, available)), which for a 20px
        // container and a 40px longest word gives 40 — the float overflows
        // rather than squeezing below its min-content width. The reference
        // adds a final `if (fitted > avail) fitted = avail`, which contradicts
        // the formula and clamps to 20, so the word overflows the FLOAT rather
        // than the float overflowing its container. Ported as the reference has
        // it and pinned.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 16px }"
                    "#fl { float: left }"));
        CHECK(f.layout("<body><div id=w><div id=fl>hello world</div></div></body>"));
        CHECK(near(f.box("fl").width, 20));
    }
    {
        // The frame is added to the intrinsic content width, and min-/max-width
        // still clamp the result.
        Fixture f;
        CHECK(f.css("#w { display: block; font-size: 16px }"
                    "#pad { float: left; padding-left: 5px; padding-right: 5px }"
                    "#max { float: left; max-width: 10px }"
                    "#min { float: left; min-width: 200px }"));
        CHECK(f.layout("<body><div id=w><div id=pad>ab</div><div id=max>ab</div>"
                       "<div id=min>ab</div></div></body>"));
        CHECK(near(f.box("pad").width, 16 + 10));
        // max-width clamps DOWN; it never widens a box that already fits.
        CHECK(near(f.box("max").width, 10));
        CHECK(near(f.box("min").width, 200));
    }
}

// Both of these were found by the differential oracle rather than here, which
// is the point of keeping them: the C++ suite had no case that could tell a
// missing forced break or a missing intra-word break from correct output.
void test_forced_breaks() {
    {
        // `br` forces a line break and leaves a zero-width box on the line it
        // ends. Collecting it the way any other inline box is collected finds
        // no children and loses the break entirely, which is what happened.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 1 }"));
        CHECK(f.layout("<body><div id=w>one<br>two<br>three</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 3);
        CHECK_EQ(f.line_text(ls[0]), "one");
        CHECK_EQ(f.line_text(ls[1]), "two");
        CHECK_EQ(f.line_text(ls[2]), "three");
        CHECK(near(f.box("w").height, 48));

        const BoxId br = f.find("br");
        CHECK(br != kNoBox);
        CHECK(near(f.tree[br].width, 0));
        // It takes the height of the line it ends, not of its own content.
        CHECK(near(f.tree[br].height, 16));
    }
    {
        // A break with nothing before it still ends a line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 1 }"));
        CHECK(f.layout("<body><div id=w>a<br>b</div></body>"));
        CHECK(f.lines("w").size() == 2);
    }
}

void test_break_all() {
    {
        // A word longer than the line is split at character boundaries rather
        // than left to overflow. 20 chars at 8px in a 40px box is 5 per line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 16px;"
                    "     line-height: 1; word-break: break-all }"));
        CHECK(f.layout("<body><div id=w>abcdefghijklmnopqrst</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 4);
        CHECK_EQ(f.line_text(ls[0]), "abcde");
        CHECK_EQ(f.line_text(ls[3]), "pqrst");
    }
    {
        // Without it the same word overflows on one line, which is the
        // behaviour every other value keeps.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 16px;"
                    "     line-height: 1 }"));
        CHECK(f.layout("<body><div id=w>abcdefghijklmnopqrst</div></body>"));
        CHECK(f.lines("w").size() == 1);
    }
    {
        // A box too narrow for even one character still makes progress rather
        // than looping: the character is placed and overflows.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 2px; font-size: 16px;"
                    "     line-height: 1; word-break: break-all }"));
        CHECK(f.layout("<body><div id=w>abc</div></body>"));
        CHECK(f.lines("w").size() == 3);
    }
}

void test_inline_atoms() {
    {
        // An inline-block is an atom: sized by shrink-to-fit, then placed whole
        // on the line with its baseline on the line's.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#a { display: inline-block; height: 30px }"));
        CHECK(f.layout("<body><div id=w>x<span id=a>ab</span>y</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        // The atom hugs its two characters.
        CHECK(near(f.box("a").width, 16));
        // Line: "x" 8px, atom 16px, "y" 8px.
        CHECK(near(f.tree[f.tree.child_at(ls[0], 0)].x, 0));
        CHECK(near(f.box("a").x, 8));
        CHECK(near(f.tree[f.tree.child_at(ls[0], 2)].x, 24));
    }
    {
        // The atom's baseline is its bottom margin edge, so a tall atom pushes
        // the line's baseline down and the text beside it sits on that line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#a { display: inline-block; width: 10px; height: 50px }"));
        CHECK(f.layout("<body><div id=w>x<span id=a></span></div></body>"));
        const BoxId l = f.lines("w")[0];
        CHECK(near(f.tree[l].baseline, 50));
        CHECK(near(f.box("a").y, 0));
        // The text sits on the same baseline, 12.8px of ascent above it.
        CHECK(near(f.tree[f.tree.child_at(l, 0)].y, 50 - 12.8));
        // The line is tall enough for the atom plus the text's descent.
        CHECK(near(f.tree[l].height, 50 + 6.4));
    }
    {
        // An atom wraps as a unit: it moves to the next line when it does not
        // fit, and is never split.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 16px }"
                    "#a { display: inline-block; width: 50px; height: 10px }"));
        CHECK(f.layout("<body><div id=w>hello<span id=a></span></div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        // The atom is REPARENTED onto its line box, so its y is line-relative
        // and the line carries the offset down the page.
        CHECK(f.tree[f.find("a")].parent == ls[1]);
        CHECK(near(f.tree[ls[1]].y, 19.2));
        CHECK(near(f.box("a").y, 0));
    }
}
